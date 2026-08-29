#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
#nullable enable annotations
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using DxMessaging.Editor;
    using NUnit.Framework;

    public sealed class ReadonlyFastHandlerUpgradeTests
    {
        private static IEnumerable<TestCaseData> SupportedCallbackCases()
        {
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message));",
                "token.RegisterUntargeted<Pulse>((in Pulse message) => Observe(message));",
                1
            ).SetName("Untargeted lambda");
            yield return new TestCaseData(
                "token.RegisterBroadcastWithoutSource<Hit>((ref InstanceId source, ref Hit message) => Observe(source, message));",
                "token.RegisterBroadcastWithoutSource<Hit>((in InstanceId source, in Hit message) => Observe(source, message));",
                2
            ).SetName("Context lambda");
            yield return new TestCaseData(
                "token.RegisterTargetedWithoutTargetingPostProcessor<Hit>((ref InstanceId target, ref Hit message) => Observe(target, message));",
                "token.RegisterTargetedWithoutTargetingPostProcessor<Hit>((in InstanceId target, in Hit message) => Observe(target, message));",
                2
            ).SetName("Post-processor lambda");
            yield return new TestCaseData(
                "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = (ref Pulse message) => Observe(message);",
                "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = (in Pulse message) => Observe(message);",
                1
            ).SetName("Explicit fast delegate lambda");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>(static (ref Pulse message) => Observe(message));",
                "token.RegisterUntargeted<Pulse>(static (in Pulse message) => Observe(message));",
                1
            ).SetName("Static lambda");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>(delegate(ref Pulse message) { Observe(message); });",
                "token.RegisterUntargeted<Pulse>(delegate(in Pulse message) { Observe(message); });",
                1
            ).SetName("Anonymous delegate");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((DxMessaging.Core.MessageHandler.FastHandler<Pulse>)(ref Pulse message) => Observe(message));",
                "token.RegisterUntargeted<Pulse>((DxMessaging.Core.MessageHandler.FastHandler<Pulse>)(in Pulse message) => Observe(message));",
                1
            ).SetName("Fully qualified cast lambda");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>(new DxMessaging.Core.MessageHandler.FastHandler<Pulse>((ref Pulse message) => Observe(message)));",
                "token.RegisterUntargeted<Pulse>(new DxMessaging.Core.MessageHandler.FastHandler<Pulse>((in Pulse message) => Observe(message)));",
                1
            ).SetName("Constructed delegate lambda");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>(priority: 10, untargetedHandler: (ref Pulse message) => Observe(message));",
                "token.RegisterUntargeted<Pulse>(priority: 10, untargetedHandler: (in Pulse message) => Observe(message));",
                1
            ).SetName("Out-of-order named lambda");
            yield return new TestCaseData(
                "protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }",
                "protected override void HandleGlobalStringMessage(in GlobalStringMessage message) { }",
                1
            ).SetName("Changed MessageAwareComponent override");
        }

        private static IEnumerable<TestCaseData> UnchangedCases()
        {
            yield return new TestCaseData(
                "token.RegisterUntargetedInterceptor<Pulse>((ref Pulse message) => true);"
            ).SetName("Interceptor lambda");
            yield return new TestCaseData("bus.EmitUntargeted(ref message);").SetName(
                "Emission call"
            );
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((Pulse message) => Observe(message));"
            ).SetName("By-value lambda");
            yield return new TestCaseData(
                "// token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message));\n"
                    + "string sample = \"token.RegisterUntargeted<Pulse>((ref Pulse message) => { })\";"
            ).SetName("Comments and strings");
            yield return new TestCaseData(
                "void Local(ref Pulse message) { }\nLocal(ref message);"
            ).SetName("Unrelated ref method");
        }

        [TestCaseSource(nameof(SupportedCallbackCases))]
        public void AnalyzeConvertsSupportedReadonlyCallbacks(
            string source,
            string expected,
            int expectedReplacements
        )
        {
            source = WithProvenContext(source);
            expected = WithProvenContext(expected);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(expected),
                $"Source:\n{source}\nExpected:\n{expected}\nActual:\n{result.UpgradedSource}"
            );
            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(expectedReplacements),
                $"Source:\n{source}"
            );
            Assert.That(result.ManualReviewMethods, Is.Empty, $"Source:\n{source}");
        }

        [TestCaseSource(nameof(UnchangedCases))]
        public void AnalyzeLeavesMutableAndUnrelatedRefCodeUnchanged(string source)
        {
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source), $"Source:\n{source}");
            Assert.That(result.ReplacementCount, Is.Zero, $"Source:\n{source}");
            Assert.That(result.ManualReviewMethods, Is.Empty, $"Source:\n{source}");
        }

        [Test]
        public void AnalyzeConvertsUniqueRegisteredMethodGroups()
        {
            const string Source =
                @"
sealed class Receiver
{
    private DxMessaging.Core.MessageRegistrationToken token;

    void Register()
    {
        token.RegisterUntargeted<Pulse>(OnPulse);
        token.RegisterBroadcastWithoutSource<Hit>(this.OnHit);
    }

    private void OnPulse(ref Pulse message) { }
    private void OnHit(ref InstanceId source, ref Hit message) { }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(3),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Contain("OnPulse(in Pulse message)"));
            Assert.That(
                result.UpgradedSource,
                Does.Contain("OnHit(in InstanceId source, in Hit message)")
            );
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeConvertsDuplicateFirstMessageCallbacksWithinTheirOwnTypes()
        {
            const string Source =
                @"
using DxMessaging.Unity;
public sealed class TimeScaleDriver : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<GamePaused>(OnGamePaused);
    }

    private void OnGamePaused(ref GamePaused message) { }
}

public sealed class AudioPauseDriver : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterUntargeted<GamePaused>(OnGamePaused);
    }

    private void OnGamePaused(ref GamePaused message) { }
}

public sealed class ImpactSound : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterGameObjectBroadcast<CollisionOccurred>(gameObject, OnCollisionOccurred);
    }

    private void OnCollisionOccurred(ref CollisionOccurred message) { }
}

public sealed class BreakableObject : MessageAwareComponent
{
    protected override void RegisterMessageHandlers()
    {
        base.RegisterMessageHandlers();
        _ = Token.RegisterGameObjectBroadcast<CollisionOccurred>(gameObject, OnCollisionOccurred);
    }

    private void OnCollisionOccurred(ref CollisionOccurred message) { }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(4),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Not.Contain("ref GamePaused"));
            Assert.That(result.UpgradedSource, Does.Not.Contain("ref CollisionOccurred"));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeConvertsConstructedFastDelegateMethodGroup()
        {
            const string Source =
                @"
using DxMessaging.Core;
MessageHandler.FastHandler<Pulse> handler = new MessageHandler.FastHandler<Pulse>(OnPulse);
private void OnPulse(ref Pulse message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(1),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Contain("OnPulse(in Pulse message)"));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeReportsQualifiedCallbackWithoutChangingIt()
        {
            const string Source =
                @"
using DxMessaging.Unity;
sealed class Receiver : MessageAwareComponent
{
    void Register() => Token.RegisterUntargeted<Pulse>(Callbacks.OnPulse);
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("qualified callback"));
        }

        [Test]
        public void AnalyzeConvertsChangedOverridesInEveryDirectMessageAwareType()
        {
            const string Source =
                @"
using DxMessaging.Unity;
sealed class First : MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }
}
sealed class Second : MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(2),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Not.Contain("ref GlobalStringMessage"));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeReportsIndirectMessageAwareOverrideWithoutChangingIt()
        {
            const string Source =
                @"
sealed class Receiver : CustomMessageAwareBase
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
        }

        [Test]
        public void AnalyzeLeavesFastHandlerFactoryAndUnrelatedMessageHandlerUnchanged()
        {
            const string Source =
                @"
sealed class MessageHandler
{
    public delegate void FastHandler<T>(ref T message);
}
MessageHandler.FastHandler<Pulse> handler = (ref Pulse message) => Observe(message);
MessageHandler.FastHandlerFactory<Pulse> factory = (ref Pulse message) => Observe(message);";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
        }

        [Test]
        public void AnalyzeDoesNotUseTokenDeclarationFromAnotherTypeOrThroughShadowing()
        {
            const string Source =
                @"
sealed class Actual
{
    private MessageRegistrationToken token;
}
sealed class Unrelated
{
    private CustomToken token;
    void Register() => token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message));
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
        }

        [Test]
        public void AnalyzeRejectsHiddenTokenAndLookalikeDxMessagingTypes()
        {
            const string Source =
                @"
using DxMessaging.Core;
using DxMessaging.Unity;
using Vendor = MyCompany;
sealed class Receiver : MessageAwareComponent
{
    private new CustomToken Token;
    void Register() => Token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message));
}
sealed class Other
{
    private MyCompany . DxMessaging.Core.MessageRegistrationToken token;
    void Register() => token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message));
}
sealed class External : MyCompany . DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }
}
MyCompany . DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler =
    (ref Pulse message) => Observe(message);
Vendor :: DxMessaging.Core.MessageRegistrationToken aliasToken;
aliasToken.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message));
Vendor :: DxMessaging.Core.MessageHandler.FastHandler<Pulse> aliasHandler =
    (ref Pulse message) => Observe(message);
sealed class AliasExternal : Vendor :: DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(2));
            Assert.That(
                string.Join("\n", result.ManualReviewMethods),
                Does.Contain("receiver whose MessageRegistrationToken type cannot be proven")
            );
            Assert.That(
                string.Join("\n", result.ManualReviewMethods),
                Does.Contain("not in a directly declared MessageAwareComponent")
            );
        }

        [Test]
        public void AnalyzeDoesNotUseNamespaceScopedImportOrLocalLookalikeInterface()
        {
            const string Source =
                @"
namespace Imported
{
    using DxMessaging.Core;
}
namespace Unrelated
{
    interface MessageRegistrationToken { }
    sealed class Receiver
    {
        private MessageRegistrationToken token;
        void Register() => token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message));
    }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
        }

        [Test]
        public void AnalyzeDoesNotBleedFastDelegateTypeUsesIntoLaterAssignments()
        {
            const string Source =
                @"
using DxMessaging.Core;
void Configure(MessageHandler.FastHandler<Pulse> handler) { }
MessageHandler.FastHandler<Pulse> Handler { get; }
MessageHandler.FastHandler<Pulse> Create() => Handler;
CustomDelegate other = (ref OtherMessage message) => Observe(message);";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
        }

        [Test]
        public void AnalyzeDoesNotRewriteNestedMutableLambdaInsideReadonlyCallback()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>((ref Pulse message) =>
{
    token.RegisterUntargetedInterceptor<OtherPulse>((ref OtherPulse other) => true);
});";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(1),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Contain("(in Pulse message)"));
            Assert.That(result.UpgradedSource, Does.Contain("(ref OtherPulse other)"));
        }

        [Test]
        public void AnalyzeDoesNotRewriteDelegateInNonCallbackArgument()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterTargeted<Hit>(
    FindTarget((ref Probe probe) => probe.Id),
    OnHit
);
private void OnHit(ref Hit message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(1),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Contain("(ref Probe probe)"));
            Assert.That(result.UpgradedSource, Does.Contain("OnHit(in Hit message)"));
        }

        [Test]
        public void AnalyzeConvertsEveryGlobalAcceptAllMethodGroup()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
void Register()
{
    token.RegisterGlobalAcceptAll(OnUntargeted, OnTargeted, OnBroadcast);
}
void OnUntargeted(ref IUntargetedMessage message) { }
void OnTargeted(ref InstanceId target, ref ITargetedMessage message) { }
void OnBroadcast(ref InstanceId source, ref IBroadcastMessage message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(5),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Not.Contain("ref "));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeConvertsMethodGroupAssignedToExplicitFastDelegate()
        {
            const string Source =
                @"
DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = OnPulse;
private void OnPulse(ref Pulse message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(1),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Contain("OnPulse(in Pulse message)"));
        }

        [Test]
        public void AnalyzeConvertsNamedAndExplicitlyCastMethodGroups()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(
    priority: 10,
    untargetedHandler: (DxMessaging.Core.MessageHandler.FastHandler<Pulse>)OnPulse
);
private void OnPulse(ref Pulse message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(1),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Contain("OnPulse(in Pulse message)"));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeReportsOverloadedMethodGroupWithoutChangingIt()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message) { }
void OnPulse(ref OtherPulse message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("OnPulse has overloads"));
        }

        [Test]
        public void AnalyzeReportsMethodGroupDeclaredInAnotherFileWithoutChangingIt()
        {
            const string Source =
                "DxMessaging.Core.MessageRegistrationToken token;\n"
                + "token.RegisterUntargeted<Pulse>(OnPulse);";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("not declared in this file"));
        }

        [Test]
        public void AnalyzeDoesNotReportSameFileByValueMethodGroup()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(Pulse message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeReportsCallbackSharedWithInterceptorWithoutChangingIt()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
token.RegisterUntargetedInterceptor<Pulse>(OnPulse);
void OnPulse(ref Pulse message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("also used as an interceptor"));
        }

        [Test]
        public void AnalyzeIsIdempotentAndPreservesLineEndings()
        {
            const string Source =
                "DxMessaging.Core.MessageRegistrationToken token;\r\n"
                + "token.RegisterUntargeted<Pulse>(OnPulse);\r\n"
                + "void OnPulse(ref Pulse message) { }\r\n";

            ReadonlyFastHandlerUpgrade.UpgradeResult first = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult second = ReadonlyFastHandlerUpgrade.Analyze(
                first.UpgradedSource
            );

            Assert.That(first.UpgradedSource, Does.Contain("\r\n"));
            Assert.That(first.ReplacementCount, Is.EqualTo(1));
            Assert.That(second.UpgradedSource, Is.EqualTo(first.UpgradedSource));
            Assert.That(second.ReplacementCount, Is.Zero);
        }

        [Test]
        public void UpgradeBytesPreservesUtf8BomAndCrLf()
        {
            const string Source =
                "DxMessaging.Core.MessageRegistrationToken token;\r\n"
                + "token.RegisterUntargeted<Pulse>(OnPulse);\r\n"
                + "void OnPulse(ref Pulse message) { }\r\n";
            byte[] sourceBytes = WithPreamble(Encoding.UTF8, Source);

            byte[] upgradedBytes = ReadonlyFastHandlerUpgrade.UpgradeBytes(
                sourceBytes,
                out ReadonlyFastHandlerUpgrade.UpgradeResult result
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            AssertPreamble(upgradedBytes, Encoding.UTF8.GetPreamble());
            string upgraded = Encoding.UTF8.GetString(
                upgradedBytes,
                Encoding.UTF8.GetPreamble().Length,
                upgradedBytes.Length - Encoding.UTF8.GetPreamble().Length
            );
            Assert.That(upgraded, Does.Contain("OnPulse(in Pulse message)"));
            Assert.That(upgraded, Does.Contain("\r\n"));
            Assert.That(upgraded.Replace("\r\n", string.Empty), Does.Not.Contain("\n"));
        }

        [Test]
        public void UpgradeBytesPreservesUtf16Encoding()
        {
            const string Source =
                "DxMessaging.Core.MessageRegistrationToken token;\n"
                + "token.RegisterUntargeted<Pulse>(OnPulse);\n"
                + "void OnPulse(ref Pulse message) { }\n";
            byte[] sourceBytes = WithPreamble(Encoding.Unicode, Source);

            byte[] upgradedBytes = ReadonlyFastHandlerUpgrade.UpgradeBytes(
                sourceBytes,
                out ReadonlyFastHandlerUpgrade.UpgradeResult result
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            AssertPreamble(upgradedBytes, Encoding.Unicode.GetPreamble());
            string upgraded = Encoding.Unicode.GetString(
                upgradedBytes,
                Encoding.Unicode.GetPreamble().Length,
                upgradedBytes.Length - Encoding.Unicode.GetPreamble().Length
            );
            Assert.That(upgraded, Does.Contain("OnPulse(in Pulse message)"));
        }

        [Test]
        public void UpgradeBytesRejectsUnsupportedEncodingWithoutProducingOutput()
        {
            byte[] invalidUtf8WithoutBom = { 0xC3, 0x28 };

            Assert.That(
                () =>
                    ReadonlyFastHandlerUpgrade.UpgradeBytes(
                        invalidUtf8WithoutBom,
                        out ReadonlyFastHandlerUpgrade.UpgradeResult _
                    ),
                Throws.TypeOf<DecoderFallbackException>()
            );
        }

        [TestCase("Assets/Generated/Receiver.cs", "class Receiver { }")]
        [TestCase("Assets/Receiver.g.cs", "class Receiver { }")]
        [TestCase("Assets/Receiver.generated.cs", "class Receiver { }")]
        [TestCase("Assets/Receiver.Designer.cs", "class Receiver { }")]
        [TestCase("Assets/Receiver.cs", "// <auto-generated />\nclass Receiver { }")]
        [TestCase("Assets/Receiver.cs", "// @generated\nclass Receiver { }")]
        [TestCase(
            "Assets/Receiver.cs",
            "//------------------------------------------------------------------------------\n// <auto-generated />\nclass Receiver { }"
        )]
        public void IsGeneratedSourceRecognizesGeneratedPathsAndHeaders(
            string assetPath,
            string source
        )
        {
            Assert.That(ReadonlyFastHandlerUpgrade.IsGeneratedSource(assetPath, source), Is.True);
        }

        [Test]
        public void IsGeneratedSourceAcceptsConsumerOwnedScript()
        {
            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource(
                    "Assets/Scripts/Receiver.cs",
                    "sealed class Receiver { }"
                ),
                Is.False
            );
            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource(
                    "Assets/Scripts/Receiver.cs",
                    "const string Tag = \"@generated\";\nsealed class Receiver { }"
                ),
                Is.False
            );
        }

        [Test]
        public void ApplyAtomicallyRestoresEarlierFilesWhenALaterWriteFails()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]>
                {
                    ["first.cs"] = new byte[] { 1 },
                    ["second.cs"] = new byte[] { 2 },
                },
                failOnReplaceIndex: 1
            );
            ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
            {
                new("first.cs", new byte[] { 1 }, new byte[] { 10 }),
                new("second.cs", new byte[] { 2 }, new byte[] { 20 }),
            };

            Assert.That(
                () => ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store),
                Throws.TypeOf<InvalidOperationException>()
            );

            Assert.That(store.Files["first.cs"], Is.EqualTo(new byte[] { 1 }));
            Assert.That(store.Files["second.cs"], Is.EqualTo(new byte[] { 2 }));
            Assert.That(store.Backups, Is.Empty);
        }

        [Test]
        public void ApplyAtomicallyPreservesNewerEditMadeBeforeRollback()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]>
                {
                    ["first.cs"] = new byte[] { 1 },
                    ["second.cs"] = new byte[] { 2 },
                },
                failOnReplaceIndex: 1,
                mutateFileBeforeFailure: "first.cs"
            );
            ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
            {
                new("first.cs", new byte[] { 1 }, new byte[] { 10 }),
                new("second.cs", new byte[] { 2 }, new byte[] { 20 }),
            };

            Assert.That(
                () => ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store),
                Throws.TypeOf<AggregateException>()
            );

            Assert.That(store.Files["first.cs"], Is.EqualTo(new byte[] { 99 }));
            Assert.That(store.Files["second.cs"], Is.EqualTo(new byte[] { 2 }));
            Assert.That(store.Backups, Has.Count.EqualTo(1));
        }

        [Test]
        public void ApplyAtomicallyCommitsAllFilesAndDiscardsBackups()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]>
                {
                    ["first.cs"] = new byte[] { 1 },
                    ["second.cs"] = new byte[] { 2 },
                }
            );
            ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
            {
                new("first.cs", new byte[] { 1 }, new byte[] { 10 }),
                new("second.cs", new byte[] { 2 }, new byte[] { 20 }),
            };

            ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store);

            Assert.That(store.Files["first.cs"], Is.EqualTo(new byte[] { 10 }));
            Assert.That(store.Files["second.cs"], Is.EqualTo(new byte[] { 20 }));
            Assert.That(store.Backups, Is.Empty);
        }

        [Test]
        public void ApplyAtomicallyKeepsCommittedWritesWhenBackupCleanupFails()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]> { ["receiver.cs"] = new byte[] { 1 } },
                throwOnDiscard: true
            );
            ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
            {
                new("receiver.cs", new byte[] { 1 }, new byte[] { 10 }),
            };

            Assert.That(
                () => ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store),
                Throws.Nothing
            );

            Assert.That(store.Files["receiver.cs"], Is.EqualTo(new byte[] { 10 }));
            Assert.That(store.Backups, Has.Count.EqualTo(1));
        }

        [Test]
        public void ApplyAtomicallyDoesNotOverwriteAFileChangedAfterPreview()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]> { ["receiver.cs"] = new byte[] { 2 } }
            );
            ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
            {
                new("receiver.cs", new byte[] { 1 }, new byte[] { 10 }),
            };

            Assert.That(
                () => ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store),
                Throws.TypeOf<InvalidOperationException>()
            );

            Assert.That(store.Files["receiver.cs"], Is.EqualTo(new byte[] { 2 }));
            Assert.That(store.Backups, Is.Empty);
        }

        [Test]
        public void PhysicalFileStoreReplacesAndRestoresExactBytes()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "dxmsg-readonly-upgrade-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "Receiver.cs");
            byte[] original = { 1, 2, 3 };
            byte[] upgraded = { 4, 5, 6 };
            File.WriteAllBytes(path, original);

            try
            {
                ReadonlyFastHandlerUpgrade.PhysicalAtomicFileStore store = new();
                string backup = store.Replace(path, original, upgraded);

                Assert.That(File.ReadAllBytes(path), Is.EqualTo(upgraded));
                Assert.That(File.Exists(backup), Is.True);

                store.Restore(path, backup, upgraded);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
                Assert.That(File.Exists(backup), Is.False);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void PhysicalFileStoreDoesNotOverwriteChangedBytes()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "dxmsg-readonly-upgrade-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "Receiver.cs");
            byte[] changed = { 9, 9, 9 };
            File.WriteAllBytes(path, changed);

            try
            {
                ReadonlyFastHandlerUpgrade.PhysicalAtomicFileStore store = new();
                Assert.That(
                    () => store.Replace(path, new byte[] { 1 }, new byte[] { 2 }),
                    Throws.TypeOf<IOException>()
                );
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(changed));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static byte[] WithPreamble(Encoding encoding, string source)
        {
            byte[] preamble = encoding.GetPreamble();
            byte[] content = encoding.GetBytes(source);
            byte[] result = new byte[preamble.Length + content.Length];
            preamble.CopyTo(result, 0);
            content.CopyTo(result, preamble.Length);
            return result;
        }

        private static string WithProvenContext(string source)
        {
            if (source.Contains("protected override"))
            {
                return "using DxMessaging.Unity;\n"
                    + "sealed class Receiver : MessageAwareComponent\n{\n"
                    + source
                    + "\n}";
            }
            return source.Contains("token.")
                ? "DxMessaging.Core.MessageRegistrationToken token;\n" + source
                : source;
        }

        private static void AssertPreamble(byte[] actual, byte[] expectedPreamble)
        {
            Assert.That(actual.Length, Is.GreaterThanOrEqualTo(expectedPreamble.Length));
            for (int index = 0; index < expectedPreamble.Length; index++)
            {
                Assert.That(actual[index], Is.EqualTo(expectedPreamble[index]), $"Byte {index}");
            }
        }

        private sealed class FakeAtomicFileStore : ReadonlyFastHandlerUpgrade.IAtomicFileStore
        {
            private readonly int _failOnReplaceIndex;
            private readonly string? _mutateFileBeforeFailure;
            private readonly bool _throwOnDiscard;
            private int _replaceIndex;

            public FakeAtomicFileStore(
                Dictionary<string, byte[]> files,
                int failOnReplaceIndex = -1,
                bool throwOnDiscard = false,
                string? mutateFileBeforeFailure = null
            )
            {
                Files = files;
                _failOnReplaceIndex = failOnReplaceIndex;
                _throwOnDiscard = throwOnDiscard;
                _mutateFileBeforeFailure = mutateFileBeforeFailure;
            }

            public Dictionary<string, byte[]> Files { get; }

            public Dictionary<string, byte[]> Backups { get; } = new();

            public string Replace(string fullPath, byte[] originalBytes, byte[] upgradedBytes)
            {
                if (_replaceIndex++ == _failOnReplaceIndex)
                {
                    if (_mutateFileBeforeFailure != null)
                    {
                        Files[_mutateFileBeforeFailure] = new byte[] { 99 };
                    }
                    throw new InvalidOperationException("Injected write failure.");
                }

                if (!Files[fullPath].AsSpan().SequenceEqual(originalBytes))
                {
                    throw new InvalidOperationException("File changed after preview.");
                }

                string backupPath = fullPath + ".backup";
                Backups.Add(backupPath, Files[fullPath]);
                Files[fullPath] = upgradedBytes;
                return backupPath;
            }

            public void Restore(string fullPath, string backupPath, byte[] expectedCurrentBytes)
            {
                if (!Files[fullPath].AsSpan().SequenceEqual(expectedCurrentBytes))
                {
                    throw new InvalidOperationException("File changed before rollback.");
                }
                Files[fullPath] = Backups[backupPath];
                Backups.Remove(backupPath);
            }

            public void DiscardBackup(string backupPath)
            {
                if (_throwOnDiscard)
                {
                    throw new InvalidOperationException("Injected cleanup failure.");
                }
                Backups.Remove(backupPath);
            }
        }
    }
}
#endif
