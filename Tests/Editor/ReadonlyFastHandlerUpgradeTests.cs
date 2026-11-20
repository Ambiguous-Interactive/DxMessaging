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
            yield return new TestCaseData(
                "ObserveTuple((GetHolder(low < high).Value, message));"
            ).SetName("A tuple value beside a nested comparison still migrates");
        }

        private static IEnumerable<TestCaseData> UnresolvedMemberReadCases()
        {
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
        }

        [TestCaseSource(nameof(GroupedByReferenceUseCases))]
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

        [TestCaseSource(nameof(ExplicitParameterWriteCases))]
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
        }

        [TestCaseSource(nameof(ReceiverInvocationCases))]
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
