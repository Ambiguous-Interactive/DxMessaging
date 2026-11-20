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
        private const string UnresolvedMessageRead =
            "reads a member or indexer on parameter 'message' that cannot be verified safe for 'in'";

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
                "protected override bool RegisterForStringMessages => true;\n\n"
                    + "protected override void HandleGlobalStringMessage(in GlobalStringMessage message) { }",
                1
            ).SetName("Changed MessageAwareComponent override");
            yield return new TestCaseData(
                "protected override bool RegisterForStringMessages => false;\n"
                    + "protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }",
                "protected override bool RegisterForStringMessages => false;\n"
                    + "protected override void HandleGlobalStringMessage(in GlobalStringMessage message) { }",
                1
            ).SetName("Existing string handler opt out");
            yield return new TestCaseData(
                "// RegisterForStringMessages\n"
                    + "private const string Note = \"RegisterForStringMessages\";\n"
                    + "private void Observe() { bool RegisterForStringMessages = false; }\n"
                    + "protected override void HandleStringComponentMessage(ref StringMessage message) { }",
                "protected override bool RegisterForStringMessages => true;\n\n"
                    + "// RegisterForStringMessages\n"
                    + "private const string Note = \"RegisterForStringMessages\";\n"
                    + "private void Observe() { bool RegisterForStringMessages = false; }\n"
                    + "protected override void HandleStringComponentMessage(in StringMessage message) { }",
                1
            ).SetName("Comments and strings do not suppress string handler opt in");
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
            Assert.That(
                result.StringHandlerOptInCount,
                Is.EqualTo(expected.Contains("RegisterForStringMessages => true;") ? 1 : 0),
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
            Assert.That(result.StringHandlerOptInCount, Is.Zero, $"Source:\n{source}");
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
}
#region class RegionLabel
#endregion
partial
class PartialReceiver : MessageAwareComponent
{
    protected override void HandleStringGameObjectMessage(ref StringMessage message) { }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(3),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(result.UpgradedSource, Does.Not.Contain("ref GlobalStringMessage"));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(2));
            Assert.That(
                result.UpgradedSource.Split(
                    new[] { "RegisterForStringMessages" },
                    StringSplitOptions.None
                ),
                Has.Length.EqualTo(3)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("partial MessageAwareComponent")
            );
        }

        [Test]
        public void AnalyzeConvertsChangedOverrideBaseForwardingCall()
        {
            const string Source =
                @"
using DxMessaging.Unity;
sealed class Receiver : MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message)
    {
        base.HandleGlobalStringMessage(ref message);
    }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.ReplacementCount,
                Is.EqualTo(2),
                $"Actual:\n{result.UpgradedSource}"
            );
            Assert.That(
                result.UpgradedSource,
                Does.Contain("HandleGlobalStringMessage(in GlobalStringMessage message)")
            );
            Assert.That(
                result.UpgradedSource,
                Does.Contain("base.HandleGlobalStringMessage(in message)")
            );
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeConvertsEscapedOverrideBaseForwardingCall()
        {
            const string Source =
                @"
using DxMessaging.Unity;
sealed class Receiver : MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage @event)
    {
        base.HandleGlobalStringMessage(ref @event);
    }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(
                result.UpgradedSource,
                Does.Contain("HandleGlobalStringMessage(in GlobalStringMessage @event)")
            );
            Assert.That(
                result.UpgradedSource,
                Does.Contain("base.HandleGlobalStringMessage(in @event)")
            );
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        private const string UnicodeOverrideForwardingSource =
            @"
using DxMessaging.Unity;
sealed class Receiver : MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message)
    {
        base.HandleGlobalStringMessage(ref @\u006dessage);
    }
}";

        [Test]
        public void AnalyzeConvertsOverrideForwardingWithEquivalentIdentifierSpelling()
        {
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                UnicodeOverrideForwardingSource
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(
                result.UpgradedSource,
                Does.Contain("HandleGlobalStringMessage(in GlobalStringMessage message)")
            );
            Assert.That(
                result.UpgradedSource,
                Does.Contain(@"base.HandleGlobalStringMessage(in @\u006dessage)")
            );
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeLeavesMethodGroupWithRefForwardingForManualReview()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message)
{
    Forward(ref message);
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("passes parameter 'message' by ref")
            );
        }

        [Test]
        public void AnalyzeLeavesLambdaWithRefForwardingForManualReview()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterBroadcastWithoutSource<Hit>((ref InstanceId source, ref Hit message) =>
{
    Forward(ref message);
});";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("passes parameter 'message' by ref")
            );
        }

        private static IEnumerable<TestCaseData> ExplicitParameterWriteCases()
        {
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => message = default);",
                "message"
            ).SetName("Inline parameter assignment");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => message.Value = 1);",
                "message"
            ).SetName("Inline field assignment");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => (message).Value = 1);",
                "message"
            ).SetName("A grouped member receiver requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(1 + (message).Value++));",
                "message"
            ).SetName("A grouped write after addition requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(1 < (message).Value++));",
                "message"
            ).SetName("A grouped write after a comparison operator requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int[] buffer = new int[8]; Observe(buffer[..message.Value++]); });",
                "message"
            ).SetName("A range end write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int[] buffer = new int[8]; Observe(buffer[..(message).Value++]); });",
                "message"
            ).SetName("A grouped range end write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int[] buffer = new int[8]; Observe(buffer[++message.Value..]); });",
                "message"
            ).SetName("A range start write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(1 > (message).Value++));",
                "message"
            ).SetName("A grouped write after greater-than requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(8 >> (message).Value++));",
                "message"
            ).SetName("A grouped write after a right shift requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int low = 0; int high = 1; if (low < high) Observe(1 > (message).Value++); });",
                "message"
            ).SetName("A grouped write in an unbraced comparison body requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe((2 < 3, 4 > (message).Value++)));",
                "message"
            ).SetName("A grouped write in a later tuple element requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int low = 0, high = 1; Observe((low < high, (low + high) > (message).Value++)); });",
                "message"
            ).SetName("Grouped arithmetic before a comparison does not hide a write");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int low = 0, high = 1; Observe((low < high, RefCell(low, high) > (message).Value++)); });",
                "message"
            ).SetName("A preceding call with two arguments does not hide a write");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int low = 0, high = 1; Observe((low < high, message.Buffer[low] > (message).Value++)); });",
                "message"
            ).SetName("A preceding index access does not hide a write");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int threshold = 1; Observe(threshold>(message).Value++); });",
                "message"
            ).SetName("A grouped write after a variable comparison requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { Observe(1 < message.Value); Observe(1 > (message).Value++); });",
                "message"
            ).SetName(
                "A grouped comparison write after a separate comparison requires manual review"
            );
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int limit = 2; Observe(1 < limit && 1 > (message).Value++); });",
                "message"
            ).SetName("A grouped comparison write after logical and requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { int limit = 2; Observe(1 < limit || 1 > (message).Value++); });",
                "message"
            ).SetName("A grouped comparison write after logical or requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => message!.Value++);",
                "message"
            ).SetName("A null-forgiven member receiver requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe($\"{message /* note */ .Value++}\"));",
                "message"
            ).SetName("An interpolated member write after a comment requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => message.Child.Value++);",
                "message"
            ).SetName("A nested member write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => message.Child[0] = 1);",
                "message"
            ).SetName("A nested struct indexer write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => ++(message.Child)[0]);",
                "message"
            ).SetName("A grouped nested struct indexer increment requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => message[0][0] = 1);",
                "message"
            ).SetName("A repeated indexer write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => ((message).Child).Value++);",
                "message"
            ).SetName("A grouped nested member write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => ++((message).Value));",
                "message"
            ).SetName("A grouped prefix increment requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => ((message))[0]--);",
                "message"
            ).SetName("A grouped indexer decrement requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => \\u006dessage.Value++);",
                "message"
            ).SetName("Writing through an escaped parameter spelling requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe($\"{message.Value++}\"));",
                "message"
            ).SetName("An interpolated direct write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe($\"{{{message.Value++}}}\"));",
                "message"
            ).SetName("An interpolated write between escaped braces requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe($@\"{message[0] += 1}\"));",
                "message"
            ).SetName("A verbatim interpolated direct write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe($$\"\"\"{{message.Value++}}\"\"\"));",
                "message"
            ).SetName("A raw interpolated direct write requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " Observe($$\"\"\"{{message.Value /* note */ ++}}\"\"\"));",
                "message"
            ).SetName("A raw interpolation write after block trivia requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " Observe($$\"\"\"{{message. /* note */ Value++}}\"\"\"));",
                "message"
            ).SetName(
                "A raw interpolation write after a commented access dot requires manual review"
            );
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " Observe($$\"\"\"\n{{message.Value // note\n++}}\n\"\"\"));",
                "message"
            ).SetName("A raw interpolation write after line trivia requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { Observe($$\"\"\"{{ message[\"]\"]++ }}\"\"\"); });",
                "message"
            ).SetName("A raw indexer write with a quoted closing bracket requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { Observe($$\"\"\"{{ message[\"[\"]++ }}\"\"\"); });",
                "message"
            ).SetName("A raw indexer write with a quoted opening bracket requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " Observe($$\"\"\"{{ ((buffer[\"[\"], message) = (1, default(Pulse))) }}\"\"\"));",
                "message"
            ).SetName(
                "A raw tuple write after a sibling with a quoted bracket requires manual review"
            );
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " Observe($$\"\"\"{{ Echo($$\"\"\"{{ message.Value++ }}\"\"\") }}\"\"\"));",
                "message"
            ).SetName("A write in a nested raw interpolation requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => message.\u0394 = 1);",
                "message"
            ).SetName("Unicode member assignment requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => message[0] = 1);",
                "message"
            ).SetName("Direct indexer assignment requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> { int[] indices = { 0 }; ++message[indices[0]]; });",
                "message"
            ).SetName(
                "Direct indexer increment with nested index expression requires manual review"
            );
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>(OnPulse);\n"
                    + "void OnPulse(ref Pulse message) => message.Value += 1;",
                "message"
            ).SetName("Method group compound field assignment");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => ++message.Value);",
                "message"
            ).SetName("Inline prefix field increment");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>(delegate(ref Pulse @event) { @event.Value--; });",
                "@event"
            ).SetName("Anonymous method postfix field decrement");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => @message.Value--);",
                "message"
            ).SetName("Explicit identifier escaping preserves the parameter binding");
            yield return new TestCaseData(
                "token.RegisterBroadcastWithoutSource<Pulse>((ref InstanceId source, ref Pulse message)"
                    + " => { source = default; Observe(message); });",
                "source"
            ).SetName("Context assignment keeps both parameters unchanged");
            yield return new TestCaseData(
                "token.RegisterBroadcastWithoutSource<Pulse>((ref InstanceId source, ref Pulse message)"
                    + " => (message, source) = (default(Pulse), default(InstanceId)));",
                "source"
            ).SetName("Tuple assignment preserves both original parameters");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> { int local; (message.Value, local) = (1, 2); Observe(local); });",
                "message"
            ).SetName("A direct member tuple target requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> { int first, second; ((message[0], first), second) = ((1, 2), 3);"
                    + " Observe(first + second); });",
                "message"
            ).SetName("A nested tuple indexer target requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> { int local; if (message.Value != 0) (message.Value, local) = (1, 2);"
                    + " Observe(message); });",
                "message"
            ).SetName("A tuple target in an if body without braces requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> { int local; if (message.Value == 0) Observe(message);"
                    + " else (message.Value, local) = (1, 2); });",
                "message"
            ).SetName("A tuple target in an else body without braces requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> { int local; foreach (int value in new[] { 1 })"
                    + " (message.Value, local) = (value, 2); });",
                "message"
            ).SetName("A tuple target in a foreach body without braces requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                    + " { System.IDisposable resource = new System.IO.MemoryStream();"
                    + " using (resource) (message.Value, _) = (1, 2); });",
                "message"
            ).SetName("A tuple target in a using body without braces requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> { int index = 0; int[] buffer = new int[2];"
                    + " (buffer[index < 1 ? 0 : 1], message.Value) = (1, 2); });",
                "message"
            ).SetName("A comparison in another tuple index preserves detection of a direct write");
        }

        private static IEnumerable<TestCaseData> SafeParameterUseCases()
        {
            yield return new TestCaseData(
                "Observe($@\"{{message.Value++}}\"); Observe(@$\"{{message.Value++}}\");"
            ).SetName("Escaped braces in verbatim interpolated strings still migrate");
            yield return new TestCaseData(
                "Observe($@\"\"\"{{message.Value++}}\"\"\");"
                    + " Observe(@$\"\"\"{{message.Value++}}\"\"\");"
            ).SetName("Escaped quotes and braces in verbatim interpolated strings still migrate");
            yield return new TestCaseData("Observe(\"{message.Value++}\");").SetName(
                "Plain string text matching an interpolated write still migrates"
            );
            yield return new TestCaseData("Observe(\"\"\"{{message.Value++}}\"\"\");").SetName(
                "Plain raw string text matching an interpolated write still migrates"
            );
            yield return new TestCaseData(
                "Pulse other = message; other.Value++; Observe(other);"
            ).SetName("Writes to another receiver still migrate");
            yield return new TestCaseData(
                "var holder = (message, index: 0); holder . @message.Value++; Observe(holder);"
            ).SetName("A qualified member named like the parameter still migrates");
            yield return new TestCaseData(
                "Pulse messageOther; Observe(AssignPulse(out messageOther)); Observe(message);"
            ).SetName("Byref use of a similarly named local still migrates");
            yield return new TestCaseData(
                "Pulse message\\u0031; Observe(AssignPulse(out message\\u0031));"
                    + " Observe(message);"
            ).SetName("An escaped longer local still migrates");
            yield return new TestCaseData("Observe(\\u006dessage);").SetName(
                "An escaped parameter read still migrates"
            );
            yield return new TestCaseData(
                "Observe(\"message.Value--\"); /* ++message.Value; */ Observe(message);"
            ).SetName("Write-like text still migrates");
            yield return new TestCaseData("GetRef<int>(message).Value = 1;").SetName(
                "Writing to a generic call result still migrates"
            );
            yield return new TestCaseData("GetRef<Box<Box<int>>> (message).Value = 3;").SetName(
                "Writing to a nested generic call result still migrates"
            );
            yield return new TestCaseData(
                "ref readonly Pulse view = ref message; Observe(view);"
            ).SetName("A plain readonly alias still migrates");
            yield return new TestCaseData(
                "ref readonly Pulse view = ref (message); Observe(view);"
            ).SetName("A grouped readonly alias still migrates");
            yield return new TestCaseData(
                "RefPulseCallback callback = RefPulse; callback!(message).Value = 2;"
            ).SetName("Writing to a null-forgiven delegate result still migrates");
            yield return new TestCaseData(
                "RefPulseCallback callback = RefPulse; Observe(callback!(message).Flag);"
            ).SetName("Reading a null-forgiven unrelated delegate result still migrates");
            yield return new TestCaseData(
                "int value = ToInt(message); if (value == 3 || value != 4 || value <= 5 "
                    + "|| value >= 6) Observe(message);"
            ).SetName("Comparisons on a local value still migrate");
            yield return new TestCaseData(
                "System.IDisposable resource = new System.IO.MemoryStream();"
                    + " Pulse copy; int value; using (resource) (copy, value) = (message, 2);"
                    + " Observe(copy); Observe(value);"
            ).SetName("A whole parameter on a using tuple right hand side still migrates");
            yield return new TestCaseData("Observe($\"{message}\");").SetName(
                "A whole parameter in an interpolation still migrates"
            );
            yield return new TestCaseData("Observe($\"{{message.Value++}} {message}\");").SetName(
                "Escaped braces beside a whole parameter interpolation still migrate"
            );
            yield return new TestCaseData("Observe($\"{message:message.Value++}\");").SetName(
                "Write-like interpolation format text still migrates"
            );
            yield return new TestCaseData("Observe($$\"\"\"{{message}}\"\"\");").SetName(
                "A whole parameter in a raw interpolation still migrates"
            );
            yield return new TestCaseData("Observe($$\"\"\"{{ ByValue(message) }}\"\"\");").SetName(
                "A whole parameter passed by value in a raw interpolation still migrates"
            );
            yield return new TestCaseData(
                "Observe($$\"\"\"{{ (buffer[\"[\"], message) }}\"\"\");"
            ).SetName("A raw tuple value with a quoted bracket still migrates");
            yield return new TestCaseData(
                "Observe($$\"\"\"Bump(ref message) {{ ByValue(message) }}\"\"\");"
            ).SetName("Write-like literal text beside a raw interpolation still migrates");
            yield return new TestCaseData(
                "Observe($$\"\"\"{{ message:message.Value++}}\"\"\");"
            ).SetName("Write-like raw interpolation format text still migrates");
            yield return new TestCaseData(
                "Observe($$\"\"\"{{ Echo(\"\"\"message.Value++\"\"\") }} {{ message }}\"\"\");"
            ).SetName("Write-like text in a nested raw literal still migrates");
            yield return new TestCaseData(
                "int count = 1; Observe(count+++ToInt(message)); Observe(count---ToInt(message));"
            ).SetName("Postfix operators before an unrelated call still migrate");
            yield return new TestCaseData(
                "int count = 0; count += ToInt(message); Observe(count);"
            ).SetName("Writes to a local from an unrelated call still migrate");
            yield return new TestCaseData(
                "Observe(ToInt(message) + +ToInt(message)); Observe(ToInt(message) - -ToInt(message));"
            ).SetName("Separate unary operators on unrelated values still migrate");
            yield return new TestCaseData("Observe((message)); Observe(((message)));").SetName(
                "Grouped whole parameter reads still migrate"
            );
            yield return new TestCaseData(
                "Observe(1 + ToInt((message))); Observe(1 < ToInt((message)));"
            ).SetName(
                "Grouped whole parameter reads during arithmetic and comparison still migrate"
            );
            yield return new TestCaseData(
                "Pulse other = default; if (message != other) Observe((message!));"
            ).SetName("An inequality followed by a null-forgiven whole parameter still migrates");
            yield return new TestCaseData(
                "RefPulse(message).Value = 1; RefCell(ToInt((message))) = 2;"
            ).SetName("Grouped whole parameter reads in unrelated assignments still migrate");
            yield return new TestCaseData(
                "Observe(((message))); Observe(Bump(ref RefCell(ToInt((message)))));"
            ).SetName("A whole parameter read in an unrelated byref argument still migrates");
            yield return new TestCaseData("Observe(@ref (message).Value);").SetName(
                "An escaped ref callable still migrates"
            );
            yield return new TestCaseData(
                "Pulse copy; int value; (copy, value) = (message, ToInt(message));"
                    + " Observe(copy); Observe(value);"
            ).SetName("A whole parameter on a tuple right hand side still migrates");
            yield return new TestCaseData(
                "int[] buffer = new int[2]; int local;"
                    + " (buffer[ToInt(message) < 1 ? 0 : 1], local) = (1, 2); Observe(local);"
            ).SetName("A whole parameter in a tuple index comparison still migrates");
            yield return new TestCaseData(
                "Observe($$\"\"\"{{ F(\"\"\"hi\"\"\", message) }}\"\"\");"
            ).SetName("A nested raw literal with a whole parameter still migrates");
            yield return new TestCaseData(
                "void BumpLocal(ref Pulse message) { message.Value++; }"
                    + " Pulse copy = message; BumpLocal(ref copy); Observe(copy);"
            ).SetName("A write to a shadowing local function parameter still migrates");
            yield return new TestCaseData(
                "int BumpLocal(ref Pulse message) => Bump(ref message);"
                    + " Pulse copy = message; BumpLocal(ref copy); Observe(copy);"
            ).SetName("A byref use in a shadowing local function still migrates");
            yield return new TestCaseData(
                "static void BumpLocal(ref Pulse @\\u006dessage)"
                    + " { Observe($\"{@message.Value++}\"); }"
                    + " Pulse copy = message; BumpLocal(ref copy); Observe(copy);"
            ).SetName("An interpolated write to a shadowing escaped parameter still migrates");
            yield return new TestCaseData(
                "void BumpLocal(Pulse message) { message.Value++; Observe(message); }"
                    + " BumpLocal(message);"
            ).SetName("A write to a shadowing by-value local function parameter still migrates");
            yield return new TestCaseData(
                "foreach (var item in Whole(message)) Observe(item);"
            ).SetName("Foreach over an unrelated call with the whole parameter still migrates");
            yield return new TestCaseData(
                "Pulse copy = message; foreach (var item in copy) Observe(item);"
            ).SetName("Foreach over a local copy of the whole parameter still migrates");
            yield return new TestCaseData(
                "foreach (var item in (Whole(((message))))) Observe(item);"
            ).SetName("Foreach over a grouped call with a grouped argument still migrates");
            yield return new TestCaseData(
                "foreach (var item in Whole(in message)) Observe(item);"
            ).SetName("Foreach over a call taking a readonly reference still migrates");
            yield return new TestCaseData("ObserveValue(choose ? (message) : fallback);").SetName(
                "A grouped by-value conditional operand still migrates"
            );
            yield return new TestCaseData(
                "ref Pulse alias = ref (Read(in (message)) > 0 ? ref fallback : ref fallback); "
                    + "ObserveValue(alias);"
            ).SetName("A grouped value used to choose a reference still migrates");
            yield return new TestCaseData(
                "ref readonly Pulse alias = ref checked(message); ObserveValue(alias);"
            ).SetName("A checked whole-parameter readonly alias still migrates");
            yield return new TestCaseData(
                "ref readonly Pulse alias = ref checked(message)!; ObserveValue(alias);"
            ).SetName("A null-forgiven checked readonly alias still migrates");
            yield return new TestCaseData("Read(in unchecked((message)));").SetName(
                "A checked whole-parameter in argument still migrates"
            );
            yield return new TestCaseData("Bump(ref @checked(message));").SetName(
                "An escaped checked callable with a by-value argument still migrates"
            );
            yield return new TestCaseData("Observe(when(message).Next);").SetName(
                "A member of an unrelated when callable result still migrates"
            );
            yield return new TestCaseData(
                "Observe(1 switch { 0 => 0, _ => when (message).Next });"
            ).SetName("A member of an unrelated when call in an arm result still migrates");
            yield return new TestCaseData(
                "switch (0) { case 0 when when (message).Next > 0: Observe(1); break; }"
            ).SetName("A member of an unrelated when call in a switch guard still migrates");
            yield return new TestCaseData(
                "Observe(flag switch { true => Wrap(message).Next, _ => 0 });"
            ).SetName("A switch result reading an unrelated call result still migrates");
            yield return new TestCaseData("Observe(@checked(message).Next);").SetName(
                "An escaped checked callable still migrates"
            );
            yield return new TestCaseData("Observe((Pulse)(message));").SetName(
                "Casting the whole parameter by value still migrates"
            );
            yield return new TestCaseData("Observe(((Pulse)(message)).Next);").SetName(
                "Reading a member on a copy from a cast still migrates"
            );
            yield return new TestCaseData("Observe((long)Wrap(message).Next);").SetName(
                "Casting a member of a call result still migrates"
            );
            yield return new TestCaseData("Observe(((int, int))(Wrap(message)).Next);").SetName(
                "A tuple cast of a member on a call result still migrates"
            );
            yield return new TestCaseData("Observe(((GetWrap(0, 1)))(message).Next);").SetName(
                "A grouped callable with a nested argument comma still migrates"
            );
            yield return new TestCaseData(
                "System.Func<Pulse, Pulse> wrap = Wrap; Observe(((wrap))(message).Next);"
            ).SetName("Reading a member of a grouped callable result still migrates");
            yield return new TestCaseData(
                "int option = 0; Observe(GetWrap(option)(message).Next);"
            ).SetName("Reading a member of a returned callable result still migrates");
            yield return new TestCaseData(
                "var wraps = new System.Func<Pulse, Pulse>[] { Wrap };"
                    + " Observe((wraps[0])(message).Next);"
            ).SetName("Reading a member of an indexed callable result still migrates");
            yield return new TestCaseData(
                "ObserveTuple((GetHolder(low < high).Value, message));"
            ).SetName("A tuple value beside a nested comparison still migrates");
            yield return new TestCaseData(
                "int[] buffer = { 1 }; fixed (int* p = buffer) Wrap(message).Value++;"
            ).SetName("Writing to a call result in a fixed statement still migrates");
            yield return new TestCaseData(
                "unsafe { fixed (int* pointer = Wrap(message)) Observe(*pointer); }"
            ).SetName("Pinning a call result still migrates");
            yield return new TestCaseData(
                "unsafe { Pulse copy = message; fixed (int* pointer = copy) Observe(*pointer); }"
            ).SetName("Pinning a local parameter copy still migrates");
            yield return new TestCaseData(
                "// note\tmessage = default;\nObserveValue(message);"
            ).SetName("A tab keeps a write inside its single-line comment");
            yield return new TestCaseData(
                "/* note\u2028message = default; */\nObserveValue(message);"
            ).SetName("A line separator keeps a write inside its block comment");
        }

        private static IEnumerable<TestCaseData> UnresolvedMemberReadCases()
        {
            yield return new TestCaseData("Observe((long)(message).Next);").SetName(
                "Casting a read on a grouped parameter requires manual review"
            );
            yield return new TestCaseData(
                "Observe((long)(global::System.Int32)((message)).Next);"
            ).SetName("Nested casts around a grouped parameter member require review");
            yield return new TestCaseData("Observe((Box<long>)(message).Next);").SetName(
                "Casting to a generic type after a grouped parameter member requires review"
            );
            yield return new TestCaseData("Observe(((int, int))(message).Next);").SetName(
                "A tuple cast of a grouped parameter member requires manual review"
            );
            yield return new TestCaseData(
                "Observe(((int first, (int second, int third) rest))(message).Nested);"
            ).SetName("A nested named tuple cast of a grouped parameter member requires review");
            yield return new TestCaseData("Observe((Box<(int, int)>)(message).Next);").SetName(
                "A generic tuple cast of a grouped parameter member requires review"
            );
            yield return new TestCaseData("Observe(checked(message).Next);").SetName(
                "Reading a parameter member in a checked expression requires review"
            );
            yield return new TestCaseData("Observe(unchecked(checked(message)).Next);").SetName(
                "Reading a parameter member in nested checked expressions requires review"
            );
            yield return new TestCaseData(
                "Observe(flag switch { true => (message).Next, _ => 0 });"
            ).SetName("A grouped parameter member read after a switch arrow requires review");
            yield return new TestCaseData(
                "if (message.Value == 3 || message.Value != 4 || message.Value <= 5 "
                    + "|| message.Value >= 6) Observe(message);"
            ).SetName("Comparisons reading a parameter member require manual review");
            yield return new TestCaseData(
                "System.IDisposable resource = new System.IO.MemoryStream();"
                    + " int first, second; using (resource) (first, second) = (message.Value, 2);"
                    + " Observe(first + second);"
            ).SetName(
                "A using tuple right hand side reading a parameter member requires manual review"
            );
            yield return new TestCaseData("Observe($\"{message.Value}\");").SetName(
                "An interpolated parameter member read requires manual review"
            );
            yield return new TestCaseData(
                "Observe($\"{{message.Value++}} {message.Value}\");"
            ).SetName(
                "Escaped braces beside an interpolated parameter member read require manual review"
            );
            yield return new TestCaseData("Observe($\"{message.Value:message.Value++}\");").SetName(
                "An interpolation value read requires review even with a write-like format"
            );
            yield return new TestCaseData("Observe($$\"\"\"{{message.Value}}\"\"\");").SetName(
                "A raw interpolated parameter member read requires manual review"
            );
            yield return new TestCaseData(
                "Observe($$\"\"\"{{message.Value /* note */}}\"\"\");"
            ).SetName("A commented raw interpolation member read requires manual review");
            yield return new TestCaseData("Observe($$\"\"\"{{ message[\"]\"] }}\"\"\");").SetName(
                "A raw indexer read with a quoted closing bracket requires manual review"
            );
            yield return new TestCaseData("Observe($$\"\"\"{{ message[\"[\"] }}\"\"\");").SetName(
                "A raw indexer read with a quoted opening bracket requires manual review"
            );
            yield return new TestCaseData("Observe(message.\u0394);").SetName(
                "A read of a Unicode parameter member requires manual review"
            );
            yield return new TestCaseData("Observe(message[0]);").SetName(
                "A direct parameter indexer read requires manual review"
            );
            yield return new TestCaseData("Observe(message.Child[0]);").SetName(
                "A nested struct indexer read requires manual review"
            );
            yield return new TestCaseData("Observe(message[\"items\"][0]);").SetName(
                "A repeated parameter indexer read requires manual review"
            );
            yield return new TestCaseData(
                "int count = 1; Observe(count+++message.Value); Observe(count---message.Value);"
            ).SetName("A preceding postfix operator preserves manual review of a member read");
            yield return new TestCaseData(
                "int count = 0; count += message.Value; Observe(count);"
            ).SetName("A local assignment from a parameter member requires manual review");
            yield return new TestCaseData(
                "Observe(message.Value + +message.Value); Observe(message.Value - -message.Value);"
            ).SetName("Separate unary operators preserve manual review of member reads");
            yield return new TestCaseData(
                "Observe((message).Value); Observe(((message)));"
            ).SetName("A grouped parameter member read requires manual review");
            yield return new TestCaseData("Observe(1 + (message).Value);").SetName(
                "A grouped member read after addition requires manual review"
            );
            yield return new TestCaseData("Observe(1 > (message).Value);").SetName(
                "A grouped comparison member read requires manual review"
            );
            yield return new TestCaseData(
                "int limit = 2; Observe(1 < limit && 1 > (message).Value);"
            ).SetName("Grouped member reads in mixed comparisons require manual review");
            yield return new TestCaseData(
                "Pulse other = default; if (message != other) Observe(message!.Value);"
            ).SetName("An inequality and a null-forgiven member read require manual review");
            yield return new TestCaseData("Observe(message.Child.Value);").SetName(
                "Nested parameter member reads require manual review"
            );
            yield return new TestCaseData(
                "if (message.Child.Value < (1 + 2)) Observe(message);"
            ).SetName("A nested parameter member comparison requires manual review");
            yield return new TestCaseData(
                "RefPulse(message).Value = 1; RefCell((message.Value)) = 2;"
            ).SetName(
                "An unrelated target still requires manual review of a parameter member argument"
            );
            yield return new TestCaseData(
                "Observe(((message)).Value); Observe(Bump(ref RefCell((message).Value)));"
            ).SetName(
                "An unrelated byref argument still requires manual review of a parameter member read"
            );
            yield return new TestCaseData(
                "Pulse copy; int value; (copy, value) = (message, message.Value);"
                    + " Observe(copy); Observe(value);"
            ).SetName(
                "Reading a parameter member on a tuple right hand side requires manual review"
            );
            yield return new TestCaseData(
                "int[] buffer = new int[1]; int messageOther;"
                    + " (buffer[message.Value], messageOther) = (1, 2); Observe(messageOther);"
            ).SetName(
                "Reading a parameter member in another tuple target index requires manual review"
            );
            yield return new TestCaseData(
                "int[] buffer = new int[2]; int local;"
                    + " (buffer[message.Value < 1 ? 0 : 1], local) = (1, 2); Observe(local);"
            ).SetName(
                "Reading a parameter member in a tuple index comparison requires manual review"
            );
            yield return new TestCaseData("Observe(message?.Value);").SetName(
                "A conditional member read requires manual review"
            );
            yield return new TestCaseData("Observe(message?[0]);").SetName(
                "A conditional indexer read requires manual review"
            );
            yield return new TestCaseData("Observe((message!).Value);").SetName(
                "A grouped null-forgiven parameter member requires manual review"
            );
            yield return new TestCaseData("Observe(((message)!).Value);").SetName(
                "A nested grouped null-forgiven parameter member requires manual review"
            );
            yield return new TestCaseData("Observe(message /* comment */ .Value);").SetName(
                "A commented parameter member read requires manual review"
            );
            yield return new TestCaseData("Observe($\"{message /* comment */ .Value}\");").SetName(
                "A commented interpolated parameter member read requires manual review"
            );
            yield return new TestCaseData(
                "Observe($$$\"\"\"literal {{token}} {{{message.Value}}}\"\"\");"
            ).SetName("A triple-dollar interpolation reads a parameter member");
            yield return new TestCaseData("Observe(!(message).Flag);").SetName(
                "A prefix negation still requires manual review of a grouped member"
            );
            yield return new TestCaseData("throw (message).Error;").SetName(
                "A throw expression reading a grouped parameter member requires manual review"
            );
            yield return new TestCaseData(
                "Observe($$\"\"\"{{ F(\"\"\"hi\"\"\", message.Next) }}\"\"\");"
            ).SetName("A getter after a nested raw literal requires manual review");
            yield return new TestCaseData(
                "Observe($$\"\"\"{{ F(message.Next, \"\"\"hi\"\"\") }}\"\"\");"
            ).SetName("A getter before a nested raw literal requires manual review");
            yield return new TestCaseData(
                "foreach (var item in (message).Values) Observe(item);"
            ).SetName("Foreach over a grouped parameter member requires manual review");
            yield return new TestCaseData(
                "foreach (var item in message.Values) Observe(item);"
            ).SetName("Foreach over a parameter member requires manual review");
            yield return new TestCaseData(
                "switch (0) { case 0 when (message).Next > 0: Observe(1); break; }"
            ).SetName("A switch guard reading a grouped parameter member requires manual review");
            yield return new TestCaseData(
                "switch (0) { case 0 when message.Next > 0: Observe(1); break; }"
            ).SetName("A switch guard reading a parameter member requires manual review");
            yield return new TestCaseData(
                "Observe(1 switch { 0 => 0, 1 when(message).Next > 0 => 1, _ => 2 });"
            ).SetName(
                "A later switch arm guard reading a grouped parameter member requires manual review"
            );
        }

        private static IEnumerable<TestCaseData> InterpolatedByReferenceUseCases()
        {
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe($\"{Bump(ref /* note */ message.Value)}\"));",
                "ref"
            ).SetName("An interpolated ref argument after a comment requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe($\"{Bump(ref message.Value)}\"));",
                "ref"
            ).SetName("An interpolated ref field argument requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe($\"{Assign(out message.Value)}\"));",
                "out"
            ).SetName("An interpolated out field argument requires manual review");
        }

        [TestCaseSource(nameof(InterpolatedByReferenceUseCases))]
        public void AnalyzeLeavesInterpolatedByReferenceUsesForManualReview(
            string registration,
            string modifier
        )
        {
            string source = "DxMessaging.Core.MessageRegistrationToken token;\n" + registration;

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain($"passes parameter 'message' by {modifier}")
            );
        }

        private static IEnumerable<TestCaseData> EscapedByReferenceUseCases()
        {
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => AssignPulse(out @message));",
                "message"
            ).SetName("An escaped byref use requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe($\"{AssignPulse(out @message)}\"));",
                "message"
            ).SetName("An escaped interpolated byref use requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse @message) => AssignPulse(out message));",
                "@message"
            ).SetName("A plain byref use of an escaped parameter requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>(OnPulse);\n"
                    + "void OnPulse(ref Pulse \u0394message) => AssignPulse(out \\u0394message);",
                "\u0394message"
            ).SetName("A byref use of a Unicode parameter requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse @\\u006dessage) "
                    + "=> Observe($\"{AssignPulse(out message)}\"));",
                "@\\u006dessage"
            ).SetName("An interpolated use of an escaped declaration requires manual review");
        }

        [TestCaseSource(nameof(EscapedByReferenceUseCases))]
        public void AnalyzeLeavesEscapedByReferenceUsesForManualReview(
            string registration,
            string parameter
        )
        {
            string source = "DxMessaging.Core.MessageRegistrationToken token;\n" + registration;

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain($"passes parameter '{parameter}' by out")
            );
        }

        private static IEnumerable<TestCaseData> GroupedByReferenceUseCases()
        {
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Bump(ref (message).Value));",
                "ref"
            ).SetName("A grouped receiver in a ref argument requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => BumpPulse(ref (message)));",
                "ref"
            ).SetName("A grouped ref argument for the whole parameter still requires review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> Observe($\"{Assign(out ((message).Value))}\"));",
                "out"
            ).SetName("A grouped member in an interpolated out argument requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> Observe($\"{Bump(ref (message /* comment */).Value)}\"));",
                "ref"
            ).SetName(
                "A commented grouped receiver in an interpolated ref argument requires manual review"
            );
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> Observe($\"{Bump(ref (/* note */ message))}\"));",
                "ref"
            ).SetName(
                "A comment inside an interpolated grouped ref operand requires manual review"
            );
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> Observe($$\"\"\"{{ Bump(ref (/* note */ message)) }}\"\"\"));",
                "ref"
            ).SetName("A comment inside a raw grouped ref operand requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> Observe($$\"\"\"\n{{ Assign(out ((// unused\n message))) }}\n\"\"\"));",
                "out"
            ).SetName("A line comment inside a raw grouped out operand requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Bump(ref(message).Value));",
                "ref"
            ).SetName("A grouped ref argument without a space requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) "
                    + "=> Observe($\"{Assign(out((message).Value))}\"));",
                "out"
            ).SetName("A grouped out argument without a space requires manual review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => { "
                    + "ref Pulse alias = ref (choose ? ref (message) : ref fallback); "
                    + "Observe(++alias.Value); });",
                "ref"
            ).SetName("The grouped first operand of a writable ref conditional requires review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => { "
                    + "ref int alias = ref (choose ? ref (message).Value : ref fallback.Value); "
                    + "Observe(++alias); });",
                "ref"
            ).SetName("A grouped member before a ref conditional colon requires review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => "
                    + "Bump(ref (choose ? ref ((message /* use */)) : ref fallback)));",
                "ref"
            ).SetName("A nested grouped ref conditional argument requires review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => "
                    + "Observe($\"{Bump(ref (choose ? ref (message) : ref fallback))}\"));",
                "ref"
            ).SetName("A grouped ref conditional in an interpolation requires review");
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => "
                    + "Observe($$\"\"\"{{ Bump(ref (choose ? ref (message) : ref fallback)) }}\"\"\"));",
                "ref"
            ).SetName("A grouped ref conditional in a raw interpolation requires review");
        }

        [TestCaseSource(nameof(GroupedByReferenceUseCases))]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) => Bump(ref checked(message)));",
            "ref",
            TestName = "A checked parameter passed by ref requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) => Assign(out unchecked((message))));",
            "out",
            TestName = "An unchecked grouped parameter passed by out requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) => { "
                + "ref Pulse alias = ref (choose ? ref checked((message)) : ref fallback); "
                + "Observe(++alias.Value); });",
            "ref",
            TestName = "A checked first ref conditional operand requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) => { "
                + "ref Pulse alias = ref (choose ? ref fallback : ref unchecked((message))); "
                + "Observe(++alias.Value); });",
            "ref",
            TestName = "An unchecked second ref conditional operand requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) => "
                + "Observe($\"{Bump(ref unchecked(checked(message)))}\"));",
            "ref",
            TestName = "A checked ref argument inside an interpolation requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) => { "
                + "ref Pulse alias = ref checked(message)!; Observe(++alias.Value); });",
            "ref",
            TestName = "A null-forgiven checked ref initializer requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) => { "
                + "ref Pulse alias = ref (choose ? ref checked(message)! : ref fallback); "
                + "Observe(++alias.Value); });",
            "ref",
            TestName = "A null-forgiven checked ref conditional operand requires manual review"
        )]
        public void AnalyzeLeavesGroupedByReferenceUsesForManualReview(
            string registration,
            string modifier
        )
        {
            string source = "DxMessaging.Core.MessageRegistrationToken token;\n" + registration;

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain($"passes parameter 'message' by {modifier}")
            );
        }

        private const string GroupedOutWithBaseForwardingSource =
            @"
using DxMessaging.Unity;
sealed class Receiver : MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message)
    {
        Clear(out (message));
        base.HandleGlobalStringMessage(ref message);
    }

    private static void Clear(out GlobalStringMessage replacement) => replacement = default;
}";

        [Test]
        public void AnalyzeKeepsBaseForwardingWhenGroupedOutNeedsManualReview()
        {
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                GroupedOutWithBaseForwardingSource
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(GroupedOutWithBaseForwardingSource));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("passes parameter 'message' by out")
            );
        }

        private const string CompactBaseForwardingSource =
            @"
using DxMessaging.Unity;
sealed class Receiver : MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message)
    {
        base.HandleGlobalStringMessage(ref@message);
    }
}";

        [Test]
        public void AnalyzeConvertsBaseForwardingWithAnAdjacentEscape()
        {
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                CompactBaseForwardingSource
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(
                result.UpgradedSource,
                Does.Contain("base.HandleGlobalStringMessage(in@message)")
            );
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase(
            "message = default;",
            "writes to parameter 'message'",
            TestName = "A write between disabled ordinary quotes requires review"
        )]
        [TestCase(
            "Observe(message.Next);",
            "reads a member or indexer on parameter 'message'",
            TestName = "A member read between disabled ordinary quotes requires review"
        )]
        public void AnalyzeKeepsActiveCodeVisibleBetweenDisabledQuotes(
            string statement,
            string expectedReason
        )
        {
            string source =
                "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = "
                + "(ref Pulse message) => {\n#if false\n\"\n#endif\n"
                + statement
                + "\n#if false\n\"\n#endif\n};";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain(expectedReason));
        }

        [TestCase(
            "handler = Handle;\nvoid Handle(ref Pulse message) {\n"
                + "#if false\n}\n#endif\nmessage = default;\n}",
            "writes to parameter 'message'",
            TestName = "A disabled brace does not end a named callback early"
        )]
        [TestCase(
            "handler = (ref Pulse message) => {\n"
                + "#if false\n};\n#endif\nmessage = default;\n};",
            "writes to parameter 'message'",
            TestName = "A disabled statement end does not end a lambda early"
        )]
        [TestCase(
            "handler = (ref Pulse message) => {\n"
                + "#if false\n}\n#elif true\nmessage = default;\n#else\n}\n#endif\n};",
            "writes to parameter 'message'",
            TestName = "An active elif write remains visible between disabled braces"
        )]
        [TestCase(
            "handler = (ref Pulse message) => {\n"
                + "#if false\n#if UNKNOWN\n/*\n#endif\n}\n#endif\nmessage = default;\n};",
            "writes to parameter 'message'",
            TestName = "Inactive nested text cannot hide a write after its directives"
        )]
        [TestCase(
            "handler = Handle;\nvoid Handle(ref Pulse message) {\n"
                + "#if UNKNOWN\n}\n#endif\nmessage = default;\n}",
            "has boundaries that depend on conditional compilation",
            TestName = "An unresolved branch brace defers its named callback"
        )]
        [TestCase(
            "handler = (ref Pulse message) => {\n"
                + "#if UNKNOWN\nObserve(message);\n#else\nObserve(message);\n#endif\n};",
            "has boundaries that depend on conditional compilation",
            TestName = "An unresolved branch inside a callback requires manual review"
        )]
        [TestCase(
            "handler = (ref Pulse message) => {\n"
                + "#if false\nmessage = default;\n#endif\nObserve(message);\n};",
            null,
            TestName = "A disabled write does not prevent a safe callback migration"
        )]
        [TestCase(
            "handler = (ref Pulse message) => {\n"
                + "#if true\nObserve(message);\n#else\nmessage = default;\n#endif\n};",
            null,
            TestName = "A disabled else write does not prevent a safe callback migration"
        )]
        [TestCase(
            "handler = (ref Pulse message) => { Observe(@\"\n#if UNKNOWN\n}\n#endif\n\");"
                + " Observe(message); };",
            null,
            TestName = "Conditional directive text in a verbatim string still migrates"
        )]
        [TestCase(
            "handler = (ref Pulse message) => { /*\n#if UNKNOWN\n}\n#endif\n*/"
                + " Observe(message); };",
            null,
            TestName = "Conditional directive text in a block comment still migrates"
        )]
        public void AnalyzeKeepsConditionalBranchesOutOfCallableBoundaries(
            string callback,
            string expectedReason
        )
        {
            string source = "DxMessaging.Core.MessageHandler.FastHandler<Pulse> " + callback;
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            if (expectedReason == null)
            {
                Assert.That(
                    result.UpgradedSource,
                    Is.EqualTo(source.Replace("ref Pulse message", "in Pulse message"))
                );
                Assert.That(result.ReplacementCount, Is.EqualTo(1));
                Assert.That(result.ManualReviewMethods, Is.Empty);
                return;
            }

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain(expectedReason));
        }

        [Test]
        public void AnalyzeStillMigratesAfterAnUnrelatedBalancedConditional()
        {
            const string Source =
                "#if UNKNOWN\nObserve(83);\n#endif\n"
                + "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = "
                + "(ref Pulse message) => Observe(message);";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(Source.Replace("ref Pulse message", "in Pulse message"))
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeDefersAfterAnUnresolvedConditionalOpeningBrace()
        {
            const string Source =
                "#if UNKNOWN\n{\n#endif\n"
                + "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = "
                + "(ref Pulse message) => Observe(message);\n"
                + "#if UNKNOWN\n}\n#endif\n";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("has boundaries that depend on conditional compilation")
            );
        }

        [TestCase(
            "\r",
            true,
            TestName = "A conditional namespace after carriage returns needs review"
        )]
        [TestCase(
            "\u0085",
            true,
            TestName = "A conditional namespace after next-line characters needs review"
        )]
        [TestCase(
            "\u2028",
            true,
            TestName = "A conditional namespace after line separators needs review"
        )]
        [TestCase(
            "\u2029",
            true,
            TestName = "A conditional namespace after paragraph separators needs review"
        )]
        [TestCase(
            "\r",
            false,
            TestName = "An unconditional namespace after carriage returns still migrates"
        )]
        [TestCase(
            "\u0085",
            false,
            TestName = "An unconditional namespace after next-line characters still migrates"
        )]
        [TestCase(
            "\u2028",
            false,
            TestName = "An unconditional namespace after line separators still migrates"
        )]
        [TestCase(
            "\u2029",
            false,
            TestName = "An unconditional namespace after paragraph separators still migrates"
        )]
        public void AnalyzeUsesLogicalLineEndingsInNamespaceProof(string newline, bool conditional)
        {
            string imports = conditional
                ? "# if USE_DX\nusing DxMessaging.Core;\n#else\nusing Writable;\n#endif"
                : "using DxMessaging.Core;";
            string source = (
                "// Heading\n"
                + imports
                + "\nMessageRegistrationToken token;"
                + "\ntoken.RegisterUntargeted<Pulse>((ref Pulse message) => Read(message));"
            ).Replace("\n", newline);

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            if (conditional)
            {
                Assert.That(result.UpgradedSource, Is.EqualTo(source));
                Assert.That(result.ReplacementCount, Is.Zero);
                Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
                Assert.That(
                    result.ManualReviewMethods[0],
                    Does.Contain("receiver whose MessageRegistrationToken type cannot be proven")
                );
                return;
            }
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(source.Replace("ref Pulse message", "in Pulse message"))
            );
        }

        [TestCase(
            "Forward(ref\n#if true\nmessage\n#endif\n);",
            true,
            TestName = "A resolved directive does not hide a writable argument"
        )]
        [TestCase(
            "Forward(ref\n#if false\nother\n#elif true\nmessage\n#endif\n);",
            true,
            TestName = "A selected elif operand still requires writable argument review"
        )]
        [TestCase(
            "Read(\n#if true\nmessage\n#endif\n);",
            false,
            TestName = "A by-value argument across resolved directives still migrates"
        )]
        public void AnalyzeScansOperandsAcrossResolvedDirectives(string statement, bool writable)
        {
            string source =
                "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = "
                + "(ref Pulse message) => { "
                + statement
                + " };";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            if (writable)
            {
                Assert.That(result.UpgradedSource, Is.EqualTo(source));
                Assert.That(result.ReplacementCount, Is.Zero);
                Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
                Assert.That(
                    result.ManualReviewMethods[0],
                    Does.Contain("passes parameter 'message' by ref")
                );
                return;
            }
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(source.Replace("ref Pulse message", "in Pulse message"))
            );
        }

        [TestCase(
            "FastHandler<Pulse>",
            "(ref Pulse message) => Read(message)",
            1,
            TestName = "A conditionally bound fast lambda requires review"
        )]
        [TestCase(
            "FastHandler<Pulse>",
            "delegate(ref Pulse message) { Read(message); }",
            1,
            TestName = "A conditionally bound anonymous fast delegate requires review"
        )]
        [TestCase(
            "FastHandler<Pulse>",
            "delegate(ref Pulse message) { Run((int item) => Read(item)); Read(message); }",
            1,
            TestName = "A conditionally bound anonymous fast delegate with a nested lambda requires review"
        )]
        [TestCase(
            "FastHandler<Pulse>",
            "Forward",
            1,
            TestName = "A conditionally bound fast method group requires review"
        )]
        [TestCase(
            "FastHandlerWithContext<Pulse>",
            "(ref int context, ref Pulse message) => Read(message)",
            1,
            TestName = "A conditionally bound fast lambda with context requires review"
        )]
        [TestCase(
            "FastHandler<Pulse>",
            "null",
            0,
            TestName = "A conditionally bound fast delegate with no callback needs no review"
        )]
        public void AnalyzeReportsConditionallyBoundFastDelegateInitializers(
            string delegateType,
            string initializer,
            int expectedReviews
        )
        {
            const string Imports =
                "#if USE_DX\nusing DxMessaging.Core;\n#else\nusing Writable;\n#endif\n";
            string source =
                Imports
                + @"class Receiver
{
    void SetUp() { MessageHandler.TYPE handler = INITIALIZER; }
    static void Forward(ref Pulse message) => Read(message);
    static void Read(Pulse message) { }
    static void Read(int item) { }
    static void Run(System.Action<int> action) { }
}".Replace("TYPE", delegateType).Replace("INITIALIZER", initializer);

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(expectedReviews));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("MessageHandler type cannot be proven")
            );
        }

        [TestCase(
            "USE_DX",
            "handler",
            TestName = "Conditional delegate use defers a shared method group"
        )]
        [TestCase(
            "USE_DX",
            "registration",
            TestName = "Conditional registration defers a shared method group"
        )]
        [TestCase(
            "USE_DX",
            "both",
            TestName = "An unconditional proposal does not override a conditional method group"
        )]
        [TestCase(
            "true",
            "handler",
            TestName = "A literal branch still migrates a package-only method group"
        )]
        [TestCase(
            "true",
            "registration",
            TestName = "A literal branch still migrates a package-only registration"
        )]
        public void AnalyzeChecksMethodGroupUseAcrossConfigurations(string condition, string use)
        {
            const string Handler = "global::DxMessaging.Core.MessageHandler.FastHandler<Pulse>";
            string primary =
                use == "registration"
                    ? "token.RegisterUntargeted<Pulse>(Forward);"
                    : Handler + " selected = Forward;";
            string additional = use == "both" ? Handler + " shared = Forward;" : "";
            string source = @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    void SetUp()
    {
#if CONDITION
        PRIMARY
#else
        Vendor.Callback other = Forward;
#endif
        ADDITIONAL
    }
    static void Forward(ref Pulse message) => Read(message);
    static void Read(Pulse message) { }
}".Replace("CONDITION", condition).Replace("PRIMARY", primary).Replace("ADDITIONAL", additional);

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            if (condition == "true")
            {
                Assert.That(result.ReplacementCount, Is.EqualTo(1));
                Assert.That(
                    result.UpgradedSource,
                    Is.EqualTo(source.Replace("ref Pulse", "in Pulse"))
                );
                Assert.That(result.ManualReviewMethods, Is.Empty);
                return;
            }
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("conditional callback binding")
            );
        }

        // cspell:ignore oken
        [TestCase("Vendor.CustomToken", "@token", "token", false)]
        [TestCase("Vendor.CustomToken", @"\u0074oken", "token", false)]
        [TestCase("Vendor.CustomToken", "token", "@token", false)]
        [TestCase("Vendor.CustomToken", "token", @"\u0074oken", false)]
        [TestCase("Vendor.CustomToken?", "@token", "token", false)]
        [TestCase("global::DxMessaging.Core.MessageRegistrationToken", "@token", "token", true)]
        [TestCase(
            "global::DxMessaging.Core.MessageRegistrationToken",
            @"\u0074oken",
            @"\u0074oken",
            true
        )]
        public void AnalyzeMatchesReceiverIdentifiersByValue(
            string alternativeType,
            string alternativeSpelling,
            string receiver,
            bool migrate
        )
        {
            string source = @"
class Receiver
{
#if USE_DX
    global::DxMessaging.Core.MessageRegistrationToken token;
#else
    ALTERNATIVE_TYPE ALTERNATIVE_SPELLING;
#endif
    void SetUp()
    {
        RECEIVER.RegisterUntargeted<Pulse>((ref Pulse message) => Read(message));
    }
    static void Read(Pulse message) { }
}".Replace("ALTERNATIVE_TYPE", alternativeType).Replace(
                "ALTERNATIVE_SPELLING",
                alternativeSpelling
            ).Replace("RECEIVER", receiver);

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("receiver whose MessageRegistrationToken type cannot be proven")
            );
        }

        [TestCase("\n", TestName = "Two callbacks on one line report distinct LF columns")]
        [TestCase("\r\n", TestName = "Two callbacks on one line report distinct CRLF columns")]
        [TestCase(
            "\u2029",
            TestName = "Two callbacks on one line report distinct paragraph separator columns"
        )]
        public void AnalyzeReportsDistinctPositionsForIdenticalCallbacksOnOneLine(string newline)
        {
            const string Callback = "(ref Pulse message) => { message = default; };";
            const string Handler = "DxMessaging.Core.MessageHandler.FastHandler<Pulse>";
            string line =
                Handler + " first = " + Callback + " " + Handler + " second = " + Callback;
            string source = "// Heading" + newline + line;

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            int firstColumn = line.IndexOf(Callback, StringComparison.Ordinal) + 1;
            int secondColumn = line.LastIndexOf(Callback, StringComparison.Ordinal) + 1;
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(2));
            Assert.That(
                result.ManualReviewMethods,
                Has.Some.Contains($"callback on line 2, column {firstColumn} writes")
            );
            Assert.That(
                result.ManualReviewMethods,
                Has.Some.Contains($"callback on line 2, column {secondColumn} writes")
            );
        }

        [TestCase(
            "handler = (ref Pulse @ref) => Observe(@ref);",
            TestName = "An escaped ref lambda parameter keeps its name"
        )]
        [TestCase(
            "handler = delegate(ref Pulse @ref) { Observe(@ref); };",
            TestName = "An escaped ref anonymous delegate parameter keeps its name"
        )]
        [TestCase(
            "handler = Forward;\nvoid Forward([Mark] ref Pulse @ref) => Observe(@ref);",
            TestName = "An attributed escaped ref method parameter keeps its name"
        )]
        public void AnalyzeReplacesOnlyTheRefParameterModifier(string statement)
        {
            string source = "DxMessaging.Core.MessageHandler.FastHandler<Pulse> " + statement;
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(result.UpgradedSource, Is.EqualTo(source.Replace("ref Pulse", "in Pulse")));
        }

        [TestCase(
            "System.TypedReference reference = __makeref(message);",
            true,
            TestName = "A typed reference to the parameter requires manual review"
        )]
        [TestCase(
            "System.TypedReference reference = __makeref(((message)));",
            true,
            TestName = "A grouped typed reference to the parameter requires manual review"
        )]
        [TestCase(
            "Observe($\"{Read(__makeref(message))}\");",
            true,
            TestName = "A typed reference inside interpolation requires manual review"
        )]
        [TestCase(
            "Pulse copy = message; System.TypedReference reference = __makeref(copy);",
            false,
            TestName = "A typed reference to an independent copy still migrates"
        )]
        [TestCase(
            "@__makeref(message);",
            false,
            TestName = "An escaped method name passes the parameter by value"
        )]
        [TestCase("Observe(message);", false, TestName = "A by-value method call still migrates")]
        public void AnalyzeDefersDirectTypedReferenceOperands(string body, bool requiresReview)
        {
            string source =
                "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = "
                + "(ref Pulse message) => { "
                + body
                + " };";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(requiresReview ? 0 : 1));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(requiresReview ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(requiresReview ? source : source.Replace("ref Pulse", "in Pulse"))
            );
            if (requiresReview)
            {
                Assert.That(result.ManualReviewMethods, Has.All.Contains("with __makeref"));
            }
        }

        [TestCase(
            "UNKNOWN",
            "DxMessaging",
            "",
            true,
            TestName = "A conditional namespace alias requires review"
        )]
        [TestCase(
            "UNKNOWN",
            "@DxMessaging",
            "",
            true,
            TestName = "An escaped namespace alias requires review"
        )]
        [TestCase(
            "UNKNOWN",
            "\\u0044xMessaging",
            "",
            true,
            TestName = "A Unicode escaped namespace alias requires review"
        )]
        [TestCase(
            "true",
            "DxMessaging",
            "",
            true,
            TestName = "A known active namespace alias requires review"
        )]
        [TestCase(
            "false",
            "DxMessaging",
            "",
            false,
            TestName = "A proven inactive namespace alias still allows migration"
        )]
        [TestCase(
            "UNKNOWN",
            "DxMessaging",
            "global::",
            false,
            TestName = "A global qualifier bypasses a namespace alias"
        )]
        [TestCase(
            "UNKNOWN",
            "Other",
            "",
            false,
            TestName = "An unrelated namespace alias still allows migration"
        )]
        public void AnalyzeReportsRelativeFastDelegateNamespaceAliases(
            string condition,
            string alias,
            string qualifier,
            bool requiresReview
        )
        {
            string source =
                "namespace Consumer {\n#if "
                + condition
                + "\nusing "
                + alias
                + " = global::Writable;\n#endif\nclass Receiver { void Register() { "
                + qualifier
                + "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = "
                + "(ref Pulse message) => Observe(message); } } }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(requiresReview ? 0 : 1));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(requiresReview ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(requiresReview ? source : source.Replace("ref Pulse", "in Pulse"))
            );
            if (requiresReview)
            {
                Assert.That(
                    result.ManualReviewMethods,
                    Has.All.Contains("MessageHandler type cannot be proven")
                );
            }
        }

        [TestCase(
            false,
            "",
            TestName = "A relative token receiver under a namespace alias requires review"
        )]
        [TestCase(
            false,
            "global::",
            TestName = "A global token qualification bypasses a namespace alias"
        )]
        [TestCase(
            true,
            "",
            TestName = "A relative component override under a namespace alias requires review"
        )]
        [TestCase(
            true,
            "global::",
            TestName = "A global base qualification bypasses a namespace alias"
        )]
        public void AnalyzeUsesTheSameNamespaceProofForTokensAndOverrides(
            bool isOverride,
            string qualifier
        )
        {
            const string OpenNamespace =
                "namespace Consumer {\n#if USE_WRITABLE\nusing DxMessaging = global::Writable;\n#endif\n";
            string consumer = isOverride
                ? "class Receiver : "
                    + qualifier
                    + "DxMessaging.Unity.MessageAwareComponent\n{\n    "
                    + "protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) "
                    + "=> Observe(message);\n}"
                : "class Receiver { void Register() { "
                    + qualifier
                    + "DxMessaging.Core.MessageRegistrationToken token; "
                    + "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message)); } }";
            string source = OpenNamespace + consumer + " }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            bool qualified = 0 < qualifier.Length;
            Assert.That(result.ReplacementCount, Is.EqualTo(qualified ? 1 : 0));
            Assert.That(
                result.StringHandlerOptInCount,
                Is.EqualTo(qualified && isOverride ? 1 : 0)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(qualified ? 0 : 1));
            if (!qualified)
            {
                Assert.That(result.UpgradedSource, Is.EqualTo(source));
            }
        }

        [TestCase(
            "#if ACTIVE",
            "",
            "RegisterForStringMessages",
            0,
            1,
            TestName = "A conditional string opt-out needs configuration review"
        )]
        [TestCase(
            "#if true",
            "",
            "RegisterForStringMessages",
            0,
            0,
            TestName = "A known active string opt-out stays in place"
        )]
        [TestCase(
            "#if false",
            "",
            "RegisterForStringMessages",
            1,
            0,
            TestName = "A known inactive string opt-out still needs an opt-in"
        )]
        [TestCase(
            "#if ACTIVE",
            "#else\n    protected override bool RegisterForStringMessages => true;\n",
            "RegisterForStringMessages",
            0,
            1,
            TestName = "Conditional string properties stay intact for configuration review"
        )]
        [TestCase(
            "",
            "",
            "RegisterForStringMessages",
            0,
            0,
            TestName = "An unconditional string opt-out stays in place"
        )]
        [TestCase(
            "",
            "",
            "@RegisterForStringMessages",
            0,
            0,
            TestName = "An escaped string opt-out stays in place"
        )]
        [TestCase(
            "#if ACTIVE",
            "",
            "\\u0052egisterForStringMessages",
            0,
            1,
            TestName = "An escaped conditional string opt-out needs configuration review"
        )]
        public void AnalyzeReviewsConditionalStringHandlerProperties(
            string directive,
            string alternate,
            string propertyName,
            int expectedOptIns,
            int expectedReviews
        )
        {
            string optOut = $"    protected override bool {propertyName} => false;\n";
            string property =
                directive.Length == 0 ? optOut : directive + "\n" + optOut + alternate + "#endif\n";
            string source =
                "class Receiver : global::DxMessaging.Unity.MessageAwareComponent\n{\n"
                + property
                + "    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }\n"
                + "}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(expectedOptIns));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(expectedReviews));
            Assert.That(result.UpgradedSource, Does.Contain(property));
            if (0 < expectedReviews)
            {
                Assert.That(
                    result.ManualReviewMethods,
                    Has.All.Contains("each build configuration")
                );
            }
        }

        [TestCase(
            "DxMessaging.Core",
            "Vendor",
            true,
            TestName = "A vendor receiver in the alternate branch requires review"
        )]
        [TestCase(
            "Vendor",
            "DxMessaging.Core",
            true,
            TestName = "A vendor receiver in the first branch requires review"
        )]
        [TestCase(
            "DxMessaging.Core",
            "DxMessaging.Core",
            false,
            TestName = "The same proven receiver type in both branches still migrates"
        )]
        [TestCase(
            "DxMessaging.Core",
            "",
            false,
            TestName = "An unconditional global receiver still migrates"
        )]
        public void AnalyzeChecksAllFeasibleReceiverDeclarations(
            string primary,
            string alternate,
            bool requiresReview
        )
        {
            string declaration = "    global::" + primary + ".MessageRegistrationToken token;\n";
            string fields =
                alternate.Length == 0
                    ? declaration
                    : "#if ACTIVE\n"
                        + declaration
                        + "#else\n    global::"
                        + alternate
                        + ".MessageRegistrationToken token;\n#endif\n";
            string source =
                "class Receiver\n{\n"
                + fields
                + "    void Register() => token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message));\n}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(requiresReview ? 0 : 1));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(requiresReview ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(requiresReview ? source : source.Replace("ref Pulse", "in Pulse"))
            );
        }

        [TestCase(
            "[Mark(1 < 2)]",
            TestName = "An attribute comparison leaves both callback parameters visible"
        )]
        [TestCase(
            "[Mark(typeof(Dictionary<string, int>), 1 < 2)]",
            TestName = "Generic attribute arguments leave both callback parameters visible"
        )]
        [TestCase("[Mark(true)]", TestName = "An ordinary parameter attribute still migrates")]
        public void AnalyzeKeepsAttributeAnglesOutOfParameterSplitting(string attribute)
        {
            string source =
                "DxMessaging.Core.MessageHandler.FastHandlerWithContext<Pulse> handler = Handle;\n"
                + "void Handle("
                + attribute
                + " ref InstanceId source, ref Pulse message) { }";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(result.UpgradedSource, Is.EqualTo(source.Replace("ref ", "in ")));
        }

        [TestCase(
            "new Holder(ref message) { Value = 1 };",
            true,
            TestName = "An initializer can take a writable reference to a callback parameter"
        )]
        [TestCase(
            "Pulse copy = message; new Holder(ref copy) { Value = 1 };",
            false,
            TestName = "An initializer can take a reference to an independent copy"
        )]
        [TestCase(
            "void Forward(ref Pulse message) { message.Value++; } Observe(message);",
            false,
            TestName = "A real local function still shadows the outer parameter"
        )]
        public void AnalyzeDistinguishesWritableInitializersFromLocalFunctions(
            string body,
            bool requiresReview
        )
        {
            const string Parameter = "(ref Pulse message) =>";
            string source =
                "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = "
                + Parameter
                + " { "
                + body
                + " };";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(requiresReview ? 0 : 1));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(requiresReview ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(
                    requiresReview ? source : source.Replace(Parameter, "(in Pulse message) =>")
                )
            );
        }

        private static IEnumerable<TestCaseData> SourceLineTerminatorCases()
        {
            yield return new TestCaseData("\u0085").SetName(
                "A next-line character ends a single-line comment"
            );
            yield return new TestCaseData("\u2028").SetName(
                "A line separator ends a single-line comment"
            );
            yield return new TestCaseData("\u2029").SetName(
                "A paragraph separator ends a single-line comment"
            );
            yield return new TestCaseData("\r").SetName(
                "A carriage return ends a single-line comment"
            );
            yield return new TestCaseData("\r\n").SetName(
                "A carriage return and line feed end a single-line comment on one source line"
            );
        }

        [TestCaseSource(nameof(SourceLineTerminatorCases))]
        public void AnalyzeDetectsWritesAfterLineComments(string lineEnding)
        {
            string source =
                "DxMessaging.Core.MessageRegistrationToken token;"
                + lineEnding
                + "token.RegisterUntargeted<Pulse>((ref Pulse message) => { // note"
                + lineEnding
                + "message = default;\n});";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("callback on line 2, column 33 writes to parameter 'message'")
            );
        }

        [TestCaseSource(nameof(ExplicitParameterWriteCases))]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) => { ++checked(message); });",
            "message",
            TestName = "Incrementing a checked parameter requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                + " { int item; (checked(message), item) = (default(Pulse), 83); });",
            "message",
            TestName = "Writing a checked tuple target requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) => { message!++; });",
            "message",
            TestName = "Incrementing a null-forgiven parameter requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                + " { int item; (message!, item) = (default(Pulse), 83); });",
            "message",
            TestName = "Writing a null-forgiven tuple target requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                + " { int[] buffer = { 1 }; fixed (int* p = buffer) (message).Value++; });",
            "message",
            TestName = "A grouped field write in an unbraced fixed statement requires review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                + " { int[] buffer = { 1 }; int copy;"
                + " fixed (int* p = buffer) (copy, message) = (1, default(Pulse)); });",
            "message",
            TestName = "A tuple target in an unbraced fixed statement requires review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                + " { (GetHolder(low < high).Value, message) = (1, default(Pulse)); });",
            "message",
            TestName = "A tuple target after a nested comparison requires manual review"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>((ref Pulse message) =>"
                + " { (GetHolder<Key, (int, int)>().Value, message) = (1, default(Pulse)); });",
            "message",
            TestName = "A tuple target after a call with generic arguments requires manual review"
        )]
        public void AnalyzeLeavesExplicitParameterWritesForManualReview(
            string registration,
            string parameter
        )
        {
            string source = "DxMessaging.Core.MessageRegistrationToken token;\n" + registration;

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain($"writes to parameter '{parameter}'")
            );
        }

        private static IEnumerable<TestCaseData> ReceiverInvocationCases()
        {
            yield return new TestCaseData("message.Increment();").SetName(
                "A method call on the message requires manual review"
            );
            yield return new TestCaseData("message.Child.Increment();").SetName(
                "A method call on a nested message member requires manual review"
            );
            yield return new TestCaseData("foreach (var item in message) Observe(item);").SetName(
                "Foreach over the whole parameter requires manual review"
            );
            yield return new TestCaseData(
                "foreach (int item in /* source */ ((message)) /* end */) Observe(item);"
            ).SetName(
                "Foreach over a grouped whole parameter with comments requires manual review"
            );
            yield return new TestCaseData(
                "if (true) foreach (var item in (message!)) Observe(item);"
            ).SetName("Foreach over a null-forgiven whole parameter requires manual review");
        }

        [TestCaseSource(nameof(ReceiverInvocationCases))]
        [TestCase(
            "unsafe { fixed (int* pointer = unchecked(message)) Observe(*pointer); }",
            TestName = "Pinning a checked expression around the parameter requires manual review"
        )]
        [TestCase(
            "unsafe { fixed (int* pointer = message) Observe(*pointer); }",
            TestName = "Pinning the whole callback parameter requires manual review"
        )]
        [TestCase(
            "unsafe { fixed (int* pointer = ((message))!) Observe(*pointer); }",
            TestName = "Pinning a grouped null-forgiven parameter requires manual review"
        )]
        [TestCase(
            "unsafe { fixed (int* first = message, second = Wrap(message)) Observe(*first + *second); }",
            TestName = "Pinning the parameter in the first pointer declaration requires manual review"
        )]
        [TestCase(
            "unsafe { fixed (int* first = Wrap(message), second = message) Observe(*first + *second); }",
            TestName = "Pinning the parameter in a later pointer declaration requires manual review"
        )]
        public void AnalyzeLeavesReceiverInvocationsForManualReview(string body)
        {
            string source =
                "DxMessaging.Core.MessageRegistrationToken token;\n"
                + "token.RegisterUntargeted<Pulse>((ref Pulse message) => { "
                + body
                + " });";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("invokes a method on parameter 'message' or its member")
            );
        }

        [TestCaseSource(nameof(SafeParameterUseCases))]
        public void AnalyzeStillConvertsCallbacksThatOnlyReadParameters(string body)
        {
            const string Before =
                "DxMessaging.Core.MessageRegistrationToken token;\n"
                + "token.RegisterUntargeted<Pulse>((ref Pulse message) => { ";
            string source = Before + body + " });";
            string expected = Before.Replace("(ref Pulse", "(in Pulse") + body + " });";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(expected));
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCaseSource(nameof(UnresolvedMemberReadCases))]
        public void AnalyzeDefersUnresolvedParameterMemberReads(string body)
        {
            string source =
                "DxMessaging.Core.MessageRegistrationToken token;\n"
                + "token.RegisterUntargeted<Pulse>((ref Pulse message) => { "
                + body
                + " });";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain(UnresolvedMessageRead));
        }

        [TestCase(
            "Observe(message.Next); Observe($\"{++message.Value}\");",
            "writes to parameter 'message'"
        )]
        [TestCase(
            "Observe($\"{message.Next}\"); Assign(out message.Value);",
            "passes parameter 'message' by out"
        )]
        [TestCase(
            "Observe(message.Next); message.Child.Increment();",
            "invokes a method on parameter 'message'"
        )]
        [TestCase(
            "void BumpLocal(ref Pulse message) { message.Value++; }"
                + " Pulse copy = message; BumpLocal(ref copy); Observe($\"{message.Value++}\");",
            "writes to parameter 'message'"
        )]
        [TestCase(
            "int BumpLocal(ref Pulse message) => Bump(ref message);"
                + " Pulse copy = message; BumpLocal(ref copy); Bump(ref message);",
            "passes parameter 'message' by ref"
        )]
        public void AnalyzePrefersSpecificWriteAndBindingReasons(string body, string expectedReason)
        {
            string source =
                "DxMessaging.Core.MessageRegistrationToken token;\n"
                + "token.RegisterUntargeted<Pulse>((ref Pulse message) => { "
                + body
                + " });";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain(expectedReason));
        }

        [Test]
        public void AnalyzeKeepsBothParametersOfDeferredCallbackWithoutBlockingAnotherCallback()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterBroadcastWithoutSource<Hit>(
    (ref InstanceId source, ref Hit message) => Observe(source, message.Next)
);
token.RegisterUntargeted<Pulse>((ref Pulse other) => Observe(other));";
            string expected = Source.Replace("ref Pulse other", "in Pulse other");

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(expected));
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain(UnresolvedMessageRead));
        }

        [Test]
        public void AnalyzePrefersAContextCallbackWriteOverAnEarlierMemberRead()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterBroadcastWithoutSource<Hit>((ref InstanceId source, ref Hit message) =>
{
    Observe(source.Value);
    message.Value++;
});";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("writes to parameter 'message'")
            );
        }

        [Test]
        public void AnalyzeKeepsDeferredOverrideAndItsBaseForwarding()
        {
            const string Source =
                @"
class Receiver : DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message)
    {
        Observe(message.Value);
        base.HandleGlobalStringMessage(ref message);
    }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain(UnresolvedMessageRead));
        }

        [Test]
        public void AnalyzeLeavesOverrideWritesAndBaseForwardingUnchanged()
        {
            const string Source =
                @"
class Receiver : DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message)
    {
        message = default;
        base.HandleGlobalStringMessage(ref message);
    }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("writes to parameter 'message'")
            );
        }

        [Test]
        public void AnalyzeKeepsNestedBaseForwardingWhenTheOuterOverrideIsDeferred()
        {
            const string Source =
                @"
class Receiver : DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message)
    {
        void Forward(ref GlobalStringMessage message) => base.HandleGlobalStringMessage(ref message);
        message = default;
        GlobalStringMessage copy = message;
        Forward(ref copy);
    }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("writes to parameter 'message'")
            );
        }

        [TestCase(
            "message = default;",
            "",
            "Forward(ref copy);",
            false,
            TestName = "An outer write defers a nested delegate and its base forwarding"
        )]
        [TestCase(
            "",
            "",
            "Forward(ref copy);",
            false,
            TestName = "A direct local call defers its nested delegate and base forwarding"
        )]
        [TestCase(
            "",
            "",
            "Observe($\"{Evaluate(() => Forward(ref copy))}\");",
            false,
            TestName = "An interpolated local call defers its nested delegate and base forwarding"
        )]
        [TestCase(
            "",
            "message = default;",
            "Consume(handler, copy);",
            false,
            TestName = "A local write defers its connected override and base forwarding"
        )]
        [TestCase(
            "",
            "",
            "Consume(handler, copy); Other.Forward(ref copy);",
            true,
            TestName = "A qualified call keeps the nested delegate and base forwarding eligible"
        )]
        public void AnalyzeKeepsNestedDelegateAndBaseForwardingEditsTogether(
            string outerStatement,
            string localStatement,
            string invocation,
            bool shouldMigrate
        )
        {
            string source = @"
class Receiver : DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message)
    {
        void Forward(ref GlobalStringMessage message)
        {
            LOCAL_STATEMENT
            base.HandleGlobalStringMessage(ref message);
        }
        DxMessaging.Core.MessageHandler.FastHandler<GlobalStringMessage> handler = Forward;
        OUTER_STATEMENT
        GlobalStringMessage copy = message;
        INVOCATION
    }
}".Replace("LOCAL_STATEMENT", localStatement).Replace("OUTER_STATEMENT", outerStatement).Replace(
                "INVOCATION",
                invocation
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            if (shouldMigrate)
            {
                Assert.That(result.ReplacementCount, Is.EqualTo(3));
                Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
                Assert.That(result.ManualReviewMethods, Is.Empty);
                Assert.That(
                    result.UpgradedSource,
                    Does.Contain("HandleGlobalStringMessage(in GlobalStringMessage message)")
                );
                Assert.That(
                    result.UpgradedSource,
                    Does.Contain("void Forward(in GlobalStringMessage message)")
                );
                Assert.That(
                    result.UpgradedSource,
                    Does.Contain("base.HandleGlobalStringMessage(in message);")
                );
                Assert.That(result.UpgradedSource, Does.Contain(invocation));
                return;
            }

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(2));
        }

        [TestCase(
            "HandleGlobalStringMessage",
            "GlobalStringMessage",
            "void Forward(ref PAYLOAD forwarded) => base.HANDLER(ref forwarded);\n"
                + "        FAST_HANDLER first = Forward; Consume(first, outer);",
            3,
            TestName = "A global override binds a renamed local ref parameter for base forwarding"
        )]
        [TestCase(
            "HandleStringComponentMessage",
            "StringMessage",
            "PAYLOAD copy = outer;\n        if (true) {\n"
                + "            void Forward(ref InstanceId source, ref PAYLOAD forwarded) => base.HANDLER(ref forwarded);\n"
                + "            DxMessaging.Core.MessageHandler.FastHandlerWithContext<PAYLOAD> handler = Forward;\n"
                + "            Consume(handler, copy);\n        }",
            4,
            TestName = "A component override binds a renamed second local parameter for base forwarding"
        )]
        [TestCase(
            "HandleStringGameObjectMessage",
            "StringMessage",
            "void Forward(ref PAYLOAD first) => base.HANDLER(ref first);\n"
                + "        void Relay(ref PAYLOAD second) { base.HANDLER(ref second); }\n"
                + "        FAST_HANDLER firstHandler = Forward; FAST_HANDLER secondHandler = Relay;\n"
                + "        Consume(firstHandler, outer); Consume(secondHandler, outer);",
            5,
            TestName = "A GameObject override migrates two renamed local base forwarding callbacks"
        )]
        public void AnalyzeMigratesRenamedLocalBaseForwarding(
            string method,
            string payload,
            string body,
            int expectedReplacements
        )
        {
            string source = CreateRenamedForwardingSource(method, payload, body);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(expectedReplacements));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(result.UpgradedSource, Does.Contain($"{method}(in {payload} outer)"));
            Assert.That(result.UpgradedSource, Does.Not.Contain("ref "));
            if (source.Contains("ref InstanceId source"))
            {
                Assert.That(result.UpgradedSource, Does.Contain("Forward(in InstanceId source,"));
            }
        }

        [TestCase(
            "void Forward(ref PAYLOAD forwarded) => base.HANDLER(ref forwarded);\n"
                + "        FAST_HANDLER handler = Forward; outer = default; Consume(handler, outer);",
            2,
            TestName = "An outer write defers a renamed local base forwarding callback"
        )]
        [TestCase(
            "void Forward(ref PAYLOAD forwarded) { forwarded = default; base.HANDLER(ref forwarded); }\n"
                + "        void Relay(ref PAYLOAD outer) => base.HANDLER(ref outer);\n"
                + "        FAST_HANDLER first = Forward; FAST_HANDLER second = Relay; Consume(first, outer); Consume(second, outer);",
            3,
            TestName = "A write to a renamed parameter defers its sibling base forwarding callback"
        )]
        [TestCase(
            "void Forward(ref PAYLOAD forwarded) => base.HANDLER(ref forwarded);\n"
                + "        FAST_HANDLER handler = Forward; PAYLOAD copy = outer; Forward(ref copy);",
            2,
            TestName = "A direct ref caller defers a renamed local base forwarding callback"
        )]
        [TestCase(
            "void Forward(ref PAYLOAD forwarded) { PAYLOAD copy = forwarded; base.HANDLER(ref copy); }\n"
                + "        FAST_HANDLER handler = Forward; Consume(handler, outer);",
            2,
            TestName = "An unbound local copy defers the connected base forwarding edits"
        )]
        [TestCase(
            "void Forward(ref int forwarded) => base.HANDLER(ref forwarded);\n"
                + "        DxMessaging.Core.MessageHandler.FastHandler<int> handler = Forward; ConsumeInt(handler, 7);",
            2,
            TestName = "A different local ref type defers the connected base forwarding edits"
        )]
        [TestCase(
            "void Forward(PAYLOAD forwarded) => base.HANDLER(ref forwarded);\n"
                + "        Forward(outer);",
            1,
            TestName = "A by-value local parameter defers the connected base forwarding edits"
        )]
        [TestCase(
            "void Forward(ref PAYLOAD forwarded) { "
                + "ref PAYLOAD alias = ref (Choose ? ref checked(forwarded) : ref fallback); "
                + "alias = default; base.HANDLER(ref forwarded); }\n"
                + "        FAST_HANDLER handler = Forward; Consume(handler, outer);",
            2,
            TestName = "A checked writable alias defers its renamed local base forwarding edits"
        )]
        public void AnalyzeDefersRenamedBaseForwardingWithoutProvedBinding(
            string body,
            int expectedReviews
        )
        {
            string source = CreateRenamedForwardingSource(
                "HandleGlobalStringMessage",
                "GlobalStringMessage",
                body
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(expectedReviews));
        }

        [Test]
        public void AnalyzeKeepsAnEscapedBaseReceiverArgumentWritable()
        {
            string source = CreateRenamedForwardingSource(
                "HandleGlobalStringMessage",
                "GlobalStringMessage",
                "@base.HANDLER(ref outer);"
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("passes parameter 'outer' by ref")
            );
        }

        [TestCase(
            "outer",
            TestName = "An escaped base receiver leaves same-name local forwarding writable"
        )]
        [TestCase(
            "forwarded",
            TestName = "An escaped base receiver leaves renamed local forwarding writable"
        )]
        public void AnalyzeDefersOnlyTheCallbackThatPassesAReferenceToAnEscapedBaseReceiver(
            string parameter
        )
        {
            string source = CreateRenamedForwardingSource(
                "HandleGlobalStringMessage",
                "GlobalStringMessage",
                $"void Forward(ref PAYLOAD {parameter}) {{ @base.HANDLER(ref {parameter}); }}"
                    + " FAST_HANDLER handler = Forward; Consume(handler, outer);"
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(
                result.UpgradedSource,
                Does.Contain($"@base.HandleGlobalStringMessage(ref {parameter});")
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain($"passes parameter '{parameter}' by ref")
            );
        }

        private static string CreateRenamedForwardingSource(
            string method,
            string payload,
            string body
        )
        {
            const string Template =
                @"
class Receiver : DxMessaging.Unity.MessageAwareComponent
{
    protected override void HANDLER(ref PAYLOAD outer)
    {
        BODY
    }
}";
            return Template
                .Replace("BODY", body)
                .Replace("FAST_HANDLER", "DxMessaging.Core.MessageHandler.FastHandler<PAYLOAD>")
                .Replace("HANDLER", method)
                .Replace("PAYLOAD", payload);
        }

        [TestCase(
            "PAYLOAD copy = outer; Observe($\"{Run(() => base.HANDLER(ref copy))}\");",
            1,
            TestName = "A regular interpolation base call defers the string override"
        )]
        [TestCase(
            "PAYLOAD copy = outer; Observe($@\"{Run(() => base.HANDLER(ref copy))}\");",
            1,
            TestName = "A verbatim interpolation base call defers the string override"
        )]
        [TestCase(
            "PAYLOAD copy = outer; Observe($$\"\"\"{{ Echo($\"{Run(() => base.HANDLER(ref copy))}\") }}\"\"\");",
            1,
            TestName = "A nested raw interpolation base call defers the string override"
        )]
        [TestCase(
            "void Forward(ref PAYLOAD forwarded) { base.HANDLER(ref forwarded); PAYLOAD copy = forwarded;\n"
                + "            Observe($\"{Run(() => base.HANDLER(ref copy))}\"); }\n"
                + "        FAST_HANDLER handler = Forward; Consume(handler, outer);",
            2,
            TestName = "An interpolated base call defers both connected callbacks"
        )]
        [TestCase(
            "base.HANDLER(ref outer); PAYLOAD copy = outer;\n"
                + "        Observe($\"{Run(() => base.HANDLER(message: ref copy))}\");",
            1,
            TestName = "An interpolated base call also defers a proven direct base call"
        )]
        public void AnalyzeDefersBaseForwardingInExecutableInterpolation(
            string body,
            int expectedReviews
        )
        {
            string source = CreateRenamedForwardingSource(
                "HandleGlobalStringMessage",
                "GlobalStringMessage",
                body
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(expectedReviews));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("depends on a base forwarding argument")
            );
        }

        [TestCase(
            "Observe($\"base.HANDLER(ref copy) {outer:base.HANDLER(ref copy)}\");\n"
                + "        Observe($\"{Echo(\"base.HANDLER(ref copy)\")}\");",
            TestName = "Code in interpolation text leaves a direct base call eligible"
        )]
        [TestCase(
            "Observe($$\"\"\"base.HANDLER(ref copy) {{outer:base.HANDLER(ref copy)}}\"\"\");",
            TestName = "Code in raw interpolation text leaves a direct base call eligible"
        )]
        [TestCase(
            "PAYLOAD copy = outer; Observe($\"{Run(() => @base.HANDLER(ref copy))}\");",
            TestName = "An escaped receiver in interpolation leaves a direct base call eligible"
        )]
        public void AnalyzePreservesVerifiedBaseForwardingBesideInterpolation(string body)
        {
            string source = CreateRenamedForwardingSource(
                "HandleGlobalStringMessage",
                "GlobalStringMessage",
                "base.HANDLER(ref outer); " + body
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(
                result.UpgradedSource,
                Does.Contain("base.HandleGlobalStringMessage(in outer);")
            );
            Assert.That(result.UpgradedSource, Does.Contain("ref copy"));
        }

        [Test]
        public void AnalyzeIgnoresInterpolatedBaseCallsOutsideTheOverrideScope()
        {
            const string Source =
                @"
class Receiver : DxMessaging.Unity.MessageAwareComponent
{
    void Before(GlobalStringMessage copy) =>
        Observe($""{Run(() => base.HandleGlobalStringMessage(ref copy))}"");

    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) =>
        base.HandleGlobalStringMessage(ref message);

    void After(GlobalStringMessage copy) =>
        Observe($""{Run(() => base.HandleGlobalStringMessage(ref copy))}"");
}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(
                result.UpgradedSource,
                Does.Contain("base.HandleGlobalStringMessage(in message);")
            );
            Assert.That(
                result.UpgradedSource.Split(
                    new[] { "base.HandleGlobalStringMessage(ref copy)" },
                    StringSplitOptions.None
                ),
                Has.Length.EqualTo(3)
            );
        }

        [TestCase(
            "base.@HANDLER(ref copy);",
            TestName = "An escaped base member with a local copy defers both forwarding edits"
        )]
        [TestCase(
            "base.HANDLER(@message: ref copy);",
            TestName = "An escaped named argument with a local copy defers both forwarding edits"
        )]
        [TestCase(
            "Observe($\"{Run(() => base.@HANDLER(@message: ref copy))}\");",
            TestName = "Escaped base forwarding inside interpolation defers the connected edits"
        )]
        public void AnalyzeDefersUnprovedEscapedBaseForwarding(string forwarding)
        {
            string source = CreateRenamedForwardingSource(
                "HandleGlobalStringMessage",
                "GlobalStringMessage",
                "base.HANDLER(ref outer); PAYLOAD copy = outer; " + forwarding
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("depends on a base forwarding argument")
            );
        }

        [TestCase(
            "base.@HANDLER(ref outer);",
            "base.@HandleGlobalStringMessage(in outer);",
            TestName = "An escaped base member bound to the override parameter still migrates"
        )]
        [TestCase(
            "base.HANDLER(ref outer); PAYLOAD copy = outer; @base.@HANDLER(ref copy);",
            "@base.@HandleGlobalStringMessage(ref copy);",
            TestName = "An escaped base receiver and member keep the unrelated call writable"
        )]
        public void AnalyzePreservesVerifiedForwardingWithEscapedNames(
            string body,
            string expectedCall
        )
        {
            string source = CreateRenamedForwardingSource(
                "HandleGlobalStringMessage",
                "GlobalStringMessage",
                body
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(result.UpgradedSource, Does.Contain(expectedCall));
        }

        [TestCase(
            "base.HandleStringComponentMessage(ref outer);",
            3,
            TestName = "A directly forwarded contextual argument migrates across base handlers"
        )]
        [TestCase(
            "PAYLOAD copy = outer; base.HandleStringComponentMessage(ref copy);",
            0,
            TestName = "A local copy sent to another base handler defers the connected edits"
        )]
        [TestCase(
            "PAYLOAD copy = outer; Observe($\"{Run(() => base.@HandleStringComponentMessage(ref copy))}\");",
            0,
            TestName = "An escaped cross-handler call in interpolation defers the connected edits"
        )]
        [TestCase(
            "GlobalStringMessage copy = default; base.HandleGlobalStringMessage(ref copy);",
            0,
            TestName = "A different payload sent to another base handler defers the connected edits"
        )]
        public void AnalyzeKeepsCrossHandlerBaseForwardingEditsTogether(
            string body,
            int expectedReplacements
        )
        {
            string source = CreateRenamedForwardingSource(
                "HandleStringGameObjectMessage",
                "StringMessage",
                "base.HANDLER(ref outer); " + body
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(expectedReplacements));
            if (expectedReplacements == 0)
            {
                Assert.That(result.UpgradedSource, Is.EqualTo(source));
                Assert.That(result.StringHandlerOptInCount, Is.Zero);
                Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
                Assert.That(
                    result.ManualReviewMethods,
                    Has.All.Contains("depends on a base forwarding argument")
                );
                return;
            }
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(
                result.UpgradedSource,
                Does.Contain("base.HandleStringComponentMessage(in outer);")
            );
        }

        [Test]
        public void AnalyzeLeavesExpressionBodyWithGenericRefForwardingForManualReview()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message) => Forward<First, Second>(ref message);";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("passes parameter 'message' by ref")
            );
        }

        [TestCase("message.Value < 10", true)]
        [TestCase("ToInt(message) < 10", false)]
        public void AnalyzeStopsComparisonExpressionBodyBeforeLaterMethod(
            string comparison,
            bool requiresReview
        )
        {
            string source = @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message) => Observe(COMPARISON);
void Later(ref Pulse message) => Forward(ref message);".Replace("COMPARISON", comparison);

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            AssertComparisonBoundary(result, source, "OnPulse(ref Pulse message)", requiresReview);
        }

        [TestCase("message.Value < 10", true)]
        [TestCase("ToInt(message) < 10", false)]
        public void AnalyzeStopsComparisonLambdaBeforeFollowingArgument(
            string comparison,
            bool requiresReview
        )
        {
            string source = @"
DxMessaging.Core.MessageRegistrationToken token;
Pulse message = default;
token.RegisterUntargeted<Pulse>(
    (ref Pulse message) => Observe(COMPARISON),
    priority: ReadPriority(ref message) > 0 ? 1 : 0
);".Replace("COMPARISON", comparison);

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            AssertComparisonBoundary(result, source, "(ref Pulse message)", requiresReview);
        }

        private static void AssertComparisonBoundary(
            ReadonlyFastHandlerUpgrade.UpgradeResult result,
            string source,
            string signature,
            bool requiresReview
        )
        {
            if (requiresReview)
            {
                Assert.That(result.UpgradedSource, Is.EqualTo(source));
                Assert.That(result.ReplacementCount, Is.Zero);
                Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
                Assert.That(result.ManualReviewMethods[0], Does.Contain(UnresolvedMessageRead));
                return;
            }

            string expected = source.Replace(signature, signature.Replace("ref Pulse", "in Pulse"));
            Assert.That(result.UpgradedSource, Is.EqualTo(expected));
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase("Observe(from item in message select item + 1)", true)]
        [TestCase("Observe(from int item in (message) select item + 1)", true)]
        [TestCase(
            "Observe(from item in outer join int other in (message) on item equals other select item)",
            true
        )]
        [TestCase(
            "Observe(from item in outer join other in message on item equals other select item)",
            false
        )]
        [TestCase("Pulse copy = message; Observe(from item in copy select item + 1)", false)]
        [TestCase("Observe(from item in Items(message) select item + 1)", false)]
        public void AnalyzeChecksQuerySourceReceiver(string statement, bool requiresReview)
        {
            string source =
                "global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> callback = "
                + "(ref Pulse message) => { "
                + statement
                + "; };";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(requiresReview ? 0 : 1));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(requiresReview ? source : source.Replace("ref Pulse", "in Pulse"))
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(requiresReview ? 1 : 0));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("invokes a method on parameter")
            );
        }

        [TestCase("subject is Dictionary<int, string> && message.Next > 0", true)]
        [TestCase("subject is global::Boxes.Dictionary<int, string> || message.Next > 0", true)]
        [TestCase("subject is Dictionary<int, string> match && message.Next > 0", true)]
        [TestCase("subject is not Dictionary<int, string> && message.Next > 0", true)]
        [TestCase("subject is Dictionary<int, string> && Read(message)", false)]
        public void AnalyzeKeepsGenericTypePatternInsideCallback(
            string expression,
            bool requiresReview
        )
        {
            string source = @"
global::DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message) => sink = EXPRESSION;".Replace("EXPRESSION", expression);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            AssertComparisonBoundary(result, source, "OnPulse(ref Pulse message)", requiresReview);
        }

        [Test]
        public void AnalyzeKeepsGenericCommaAfterComparisonInsideExpressionBody()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message) =>
    message.Value < 10 ? Forward<First, Second>(ref message) : false;";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("passes parameter 'message' by ref")
            );
        }

        [Test]
        public void AnalyzeLeavesMethodGroupWithOutUseForManualReview()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message)
{
    Reset(out message);
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("passes parameter 'message' by out")
            );
        }

        [Test]
        public void AnalyzeLeavesOverrideWithUnsafeRefForwardingEntirelyUnchanged()
        {
            const string Source =
                @"
using DxMessaging.Unity;
sealed class Receiver : MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message)
    {
        Mutate(ref message);
        base.HandleGlobalStringMessage(ref message);
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
        public void AnalyzeIgnoresRefForwardingOfAnUnrelatedLocal()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message)
{
    Pulse local = message;
    Forward(ref local);
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.UpgradedSource, Does.Contain("OnPulse(in Pulse message)"));
            Assert.That(result.UpgradedSource, Does.Contain("Forward(ref local)"));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeIgnoresRefForwardingTextInCommentsAndStrings()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message)
{
    // Forward(ref message);
    Log(""Forward(ref message);"");
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.UpgradedSource, Does.Contain("OnPulse(in Pulse message)"));
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
Vendor :: MessageHandler.FastHandler<Pulse> directAliasHandler =
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
                "using DxMessaging.Unity;\r\n"
                + "sealed class Receiver : MessageAwareComponent\r\n"
                + "{\r\n"
                + "    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }\r\n"
                + "}\r\n";

            ReadonlyFastHandlerUpgrade.UpgradeResult first = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult second = ReadonlyFastHandlerUpgrade.Analyze(
                first.UpgradedSource
            );

            Assert.That(first.UpgradedSource, Does.Contain("\r\n"));
            Assert.That(first.ReplacementCount, Is.EqualTo(1));
            Assert.That(first.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(first.UpgradedSource.Replace("\r\n", string.Empty), Does.Not.Contain("\n"));
            Assert.That(second.UpgradedSource, Is.EqualTo(first.UpgradedSource));
            Assert.That(second.ReplacementCount, Is.Zero);
            Assert.That(second.StringHandlerOptInCount, Is.Zero);
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
