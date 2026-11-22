#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
#nullable enable annotations
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using DxMessaging.Editor;
    using NUnit.Framework;

    public sealed class ReadonlyFastHandlerUpgradeTests
    {
        // cspell:ignore mports Cdecl ndler ulse
        private const string UnresolvedMessageRead =
            "reads a member or indexer on parameter 'message' that cannot be verified safe for 'in'";

        internal static ReadonlyFastHandlerUpgrade.UpgradeResult AnalyzeWithFixtureMessageTypes(
            string source
        )
        {
            // The lexical fixtures omit the surrounding application types unless they test a binding.
            // Supply actual routing declarations so the preview can prove ordinary package overloads.
            const string Contracts =
                " : global::DxMessaging.Core.Messages.IUntargetedMessage,"
                + " global::DxMessaging.Core.Messages.ITargetedMessage,"
                + " global::DxMessaging.Core.Messages.IBroadcastMessage";
            StringBuilder declarations = new();
            foreach (
                string name in new[]
                {
                    "Pulse",
                    "OtherPulse",
                    "Hit",
                    "GamePaused",
                    "CollisionOccurred",
                    "PairMessage",
                }
            )
            {
                if (
                    !source.Contains(name)
                    || source.Contains("struct " + name)
                    || source.Contains("class " + name)
                    || source.Contains("record " + name)
                )
                {
                    continue;
                }
                string type = name == "PairMessage" ? name + "<TFirst, TSecond>" : name;
                declarations
                    .Append("\nstruct ")
                    .Append(type)
                    .Append(Contracts)
                    .Append(" { public global::System.Type MessageType => typeof(")
                    .Append(type)
                    .Append("); }");
            }

            string suffix = declarations.ToString();
            if (suffix.Length != 0 && source.Contains("namespace Consumer"))
            {
                suffix = "\nnamespace Consumer {" + suffix + "\n}";
            }
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source + suffix
            );
            Assert.That(result.UpgradedSource, Does.EndWith(suffix));
            return new ReadonlyFastHandlerUpgrade.UpgradeResult(
                result.UpgradedSource.Substring(0, result.UpgradedSource.Length - suffix.Length),
                result.ReplacementCount,
                result.StringHandlerOptInCount,
                result.ManualReviewMethods
            );
        }

        [TestCase(
            "Observe(value)",
            true,
            TestName = "An invocation remains a void-compatible callback expression"
        )]
        [TestCase(
            "{ return; }",
            true,
            TestName = "A bare return remains compatible with a void callback"
        )]
        [TestCase(
            "{ return /* comment */; }",
            true,
            TestName = "A block comment can separate a bare return and its semicolon"
        )]
        [TestCase(
            "{ return // comment\n; }",
            true,
            TestName = "A line comment can separate a bare return and its semicolon"
        )]
        [TestCase(
            "++count",
            true,
            TestName = "An outer prefix increment remains a void-compatible statement"
        )]
        [TestCase(
            "count = Observe(value)",
            true,
            TestName = "An outer assignment remains a void-compatible statement"
        )]
        [TestCase(
            "Observe<System.Collections.Generic.List<int>>(value)",
            true,
            TestName = "A nested generic call remains a void-compatible statement"
        )]
        [TestCase(
            "item => count = item",
            false,
            TestName = "An assignment in a returned lambda is not a void callback statement"
        )]
        [TestCase(
            "from item in new[] { 1 } select count = item",
            false,
            TestName = "An assignment in a returned query is not a void callback statement"
        )]
        [TestCase(
            "++count is int",
            false,
            TestName = "An increment in a returned type test is not a void callback statement"
        )]
        [TestCase(
            "number < 1 > (3)",
            false,
            TestName = "A returned comparison does not establish a generic method invocation"
        )]
        [TestCase(
            "1",
            false,
            TestName = "A value expression can bind a writable value-returning extension"
        )]
        [TestCase(
            "1 + Observe(value)",
            false,
            TestName = "A binary expression is not a void callback invocation"
        )]
        [TestCase(
            "checked(Observe(value))",
            false,
            TestName = "A checked expression does not prove a void callback"
        )]
        [TestCase(
            "{ return 1; }",
            false,
            TestName = "A value-returning block can bind a writable extension"
        )]
        [TestCase(
            @"{ return ""legacy""; }",
            false,
            TestName = "A masked string return still prevents a void callback proof"
        )]
        public void AnalyzeRequiresVoidCompatibilityForInlineCallbacks(string body, bool migrate)
        {
            string source = @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    static int count;
    void Configure()
    {
        token.RegisterUntargeted<global::DxMessaging.Core.Messages.GlobalStringMessage>(
            (ref global::DxMessaging.Core.Messages.GlobalStringMessage value) => BODY);
    }
    static int Observe(global::DxMessaging.Core.Messages.GlobalStringMessage value) => 1;
    static int Observe<T>(global::DxMessaging.Core.Messages.GlobalStringMessage value) => 1;
}".Replace("BODY", body);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref global", "in global") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [Test]
        public void AnalyzeDefersANamedCallbackWithANonvoidReturnType()
        {
            const string Source =
                @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    void Configure()
    {
        token.RegisterUntargeted<global::DxMessaging.Core.Messages.GlobalStringMessage>(Forward);
    }
    static int Forward(ref global::DxMessaging.Core.Messages.GlobalStringMessage value) => 1;
}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
        }

        [TestCase(
            false,
            TestName = "An optional package token can expose a writable receiver in another build"
        )]
        [TestCase(true, TestName = "A package token declared in each branch still migrates")]
        public void AnalyzeRequiresAReceiverInEveryFeasibleConfiguration(bool complete)
        {
            const string Field =
                "private new global::DxMessaging.Core.MessageRegistrationToken token;";
            string source = @"
class Receiver : VendorBase
{
#if LEGACY
    FIELD
OTHER
#endif
    void Configure()
    {
        token.RegisterUntargeted<global::DxMessaging.Core.Messages.GlobalStringMessage>(
            (ref global::DxMessaging.Core.Messages.GlobalStringMessage value) => Observe(value));
    }
    static void Observe(global::DxMessaging.Core.Messages.GlobalStringMessage value) { }
}".Replace("FIELD", Field).Replace("OTHER", complete ? "#else\n    " + Field : "");
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(complete ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(complete ? source.Replace("ref global", "in global") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(complete ? 0 : 1));
        }

        [TestCase(
            false,
            TestName = "An optional integer priority can expose a vendor overload in another build"
        )]
        [TestCase(true, TestName = "An integer priority declared in each branch permits migration")]
        public void AnalyzeRequiresAnArgumentTypeInEveryFeasibleConfiguration(bool complete)
        {
            const string Field = "private new int priority;";
            string source = @"
class Receiver : VendorBase
{
    global::DxMessaging.Core.MessageRegistrationToken token;
#if LEGACY
    FIELD
OTHER
#endif
    void Configure()
    {
        token.RegisterUntargeted<global::DxMessaging.Core.Messages.GlobalStringMessage>(
            (ref global::DxMessaging.Core.Messages.GlobalStringMessage value) => Observe(value),
            priority: priority);
    }
    static void Observe(global::DxMessaging.Core.Messages.GlobalStringMessage value) { }
}".Replace("FIELD", Field).Replace("OTHER", complete ? "#else\n    " + Field : "");
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(complete ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(complete ? source.Replace("ref global", "in global") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(complete ? 0 : 1));
        }

        [TestCase(
            "this.Token",
            false,
            TestName = "A partial component cannot prove the receiver of this Token"
        )]
        [TestCase(
            "Token",
            false,
            TestName = "A partial component cannot prove the receiver of bare Token"
        )]
        [TestCase(
            "base.Token",
            true,
            TestName = "A partial component can prove an explicit package base Token"
        )]
        public void AnalyzeChecksInheritedTokenAcrossPartialFiles(string token, bool migrate)
        {
            string source = @"
partial class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    void Configure()
    {
        RECEIVER.RegisterUntargeted<global::DxMessaging.Core.Messages.GlobalStringMessage>(
            (ref global::DxMessaging.Core.Messages.GlobalStringMessage value) => Observe(value));
    }
    static void Observe(global::DxMessaging.Core.Messages.GlobalStringMessage value) { }
}".Replace("RECEIVER", token);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref global", "in global") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase(
            false,
            "namespace @Imports { using DxMessaging.Core; }",
            TestName = "A verbatim sibling namespace cannot prove a package import"
        )]
        [TestCase(
            false,
            @"namespace \u0049mports { using DxMessaging.Core; }",
            TestName = "A Unicode escaped sibling namespace cannot prove a package import"
        )]
        [TestCase(
            true,
            "using DxMessaging.Core;",
            TestName = "A compilation-unit import still proves the package token"
        )]
        public void AnalyzeUsesOnlyAProvenCompilationUnitImport(bool migrate, string importLocation)
        {
            string source =
                importLocation
                + @"
namespace Consumer
{
    class Receiver
    {
        MessageRegistrationToken token;
        void Configure()
        {
            token.RegisterUntargeted<global::DxMessaging.Core.Messages.GlobalStringMessage>(
                (ref global::DxMessaging.Core.Messages.GlobalStringMessage value) => Observe(value));
        }
        static void Observe(global::DxMessaging.Core.Messages.GlobalStringMessage value) { }
    }
}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref global", "in global") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase("1", true, TestName = "An integer argument proves the package priority")]
        [TestCase(
            "(int)(ReadPriority())",
            true,
            TestName = "A cast of the whole argument proves the package priority"
        )]
        [TestCase(
            @"""legacy""",
            false,
            TestName = "A string priority can select a writable extension in another file"
        )]
        [TestCase(
            @"(int)1 + ""legacy""",
            false,
            TestName = "A cast of an operand does not prove the whole priority argument"
        )]
        [TestCase(
            "UnknownPriority()",
            false,
            TestName = "An unproved priority defers automatic migration"
        )]
        [TestCase(
            "_123",
            false,
            TestName = "A numeric-looking identifier does not prove an integer priority"
        )]
        [TestCase(
            "1_000",
            true,
            TestName = "A decimal integer with separators still proves priority"
        )]
        public void AnalyzeRequiresPriorityTypeProof(string priority, bool migrate)
        {
            string source = @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    const string _123 = ""legacy"";
    void Configure()
    {
        token.RegisterUntargeted<global::DxMessaging.Core.Messages.GlobalStringMessage>(
            (ref global::DxMessaging.Core.Messages.GlobalStringMessage value) => Observe(value),
            priority: PRIORITY);
    }
    static int ReadPriority() => 1;
    static void Observe(global::DxMessaging.Core.Messages.GlobalStringMessage value) { }
}".Replace("PRIORITY", priority);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref global", "in global") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("overload whose package binding cannot be proven")
            );
        }

        [TestCase(
            "global::DxMessaging.Core.Messages.IUntargetedMessage",
            true,
            TestName = "A matching route constraint proves a generic registration"
        )]
        [TestCase(
            "global::DxMessaging.Core.Messages.ITargetedMessage",
            false,
            TestName = "A different route constraint can select an extension"
        )]
        [TestCase("struct", false, TestName = "An unproved route constraint defers registration")]
        public void AnalyzeRequiresTheRouteConstraintForGenericRegistration(
            string constraint,
            bool migrate
        )
        {
            string source = @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    void Configure<T>() where T : CONSTRAINT
    {
        token.RegisterUntargeted<T>((ref T value) => Observe(value));
    }
    static void Observe<T>(T value) { }
}".Replace("CONSTRAINT", constraint);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref T", "in T") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase(
            "Pulse",
            true,
            TestName = "A callback for the explicit routed message still migrates"
        )]
        [TestCase(
            "Other",
            false,
            TestName = "A different callback payload can bind a writable extension"
        )]
        public void AnalyzeRequiresTheCallbackToMatchTheProvenMessageType(
            string callbackType,
            bool migrate
        )
        {
            string source = @"
struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage
{
    public global::System.Type MessageType => typeof(Pulse);
}
struct Other { }

class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    void Configure()
    {
        token.RegisterUntargeted<Pulse>(Forward);
    }
    static void Forward(ref CALLBACK value) { }
}".Replace("CALLBACK", callbackType);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase(
            "Pulse",
            true,
            TestName = "The package's one-argument message marker proves the route"
        )]
        [TestCase(
            "int, bool",
            false,
            TestName = "A marker with a different generic arity cannot prove the route"
        )]
        public void AnalyzeUsesTheActualArityOfTheRoutingMarker(string typeArguments, bool migrate)
        {
            string source = @"
namespace DxMessaging.Core.Messages
{
    public interface IUntargetedMessage<TFirst, TSecond> { }
}

struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage<ARGUMENTS>
{
    public global::System.Type MessageType => typeof(Pulse);
}

class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    void Configure()
    {
        token.RegisterUntargeted<Pulse>((ref Pulse value) => Observe(value));
    }
    static void Observe(Pulse value) { }
}".Replace("ARGUMENTS", typeArguments);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase(
            "IUntargetedMessage",
            "1 < 2",
            false,
            TestName = "A writable targeted extension survives an earlier less-than default"
        )]
        [TestCase(
            "IUntargetedMessage",
            "1 <= 2",
            false,
            TestName = "A writable targeted extension survives an earlier less-than-or-equal default"
        )]
        [TestCase(
            "ITargetedMessage",
            "1 < 2",
            false,
            TestName = "An optional comparison keeps the visible competing extension in scope"
        )]
        [TestCase(
            "ITargetedMessage",
            null,
            true,
            TestName = "An explicit fast delegate with the correct route still migrates"
        )]
        public void AnalyzeChecksRouteAndOptionalDefaultsForAnExplicitDelegate(
            string marker,
            string defaultValue,
            bool migrate
        )
        {
            const string Extension =
                @"
static class VendorExtensions
{
    public static void RegisterTargeted<T>(
        this MessageRegistrationToken token,
        bool flag = DEFAULT_VALUE,
        InstanceId target = default,
        MessageHandler.FastHandler<T> targetedHandler = null,
        int priority = 0
    ) where T : IMessage
    {
        T message = default;
        targetedHandler(ref message);
    }
}";
            string source =
                @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
class Receiver
{
    void Configure(MessageRegistrationToken token)
    {
        token.RegisterTargeted<Pulse>(
            target: default,
            targetedHandler: new MessageHandler.FastHandler<Pulse>((ref Pulse value) => { }),
            priority: 0
        );
    }
}
struct Pulse : global::DxMessaging.Core.Messages.MARKER
{
    public global::System.Type MessageType => typeof(Pulse);
}
".Replace("MARKER", marker)
                + (
                    defaultValue == null
                        ? string.Empty
                        : Extension.Replace("DEFAULT_VALUE", defaultValue)
                );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [Test]
        public void AnalyzeDefersCoupledGlobalCallbacksAcrossRegistrations()
        {
            const string Source =
                @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    void Configure()
    {
        token.RegisterGlobalAcceptAll(Write, SharedTarget, FirstBroadcast);
        token.RegisterGlobalAcceptAll(Read, SharedTarget, SecondBroadcast);
        token.RegisterUntargeted<global::DxMessaging.Core.Messages.IUntargetedMessage>(Read);
    }

    static void Write(ref global::DxMessaging.Core.Messages.IUntargetedMessage value) { value = default; }
    static void Read(ref global::DxMessaging.Core.Messages.IUntargetedMessage value) { }
    static void SharedTarget(ref global::DxMessaging.Core.InstanceId id, ref global::DxMessaging.Core.Messages.ITargetedMessage value) { }
    static void FirstBroadcast(ref global::DxMessaging.Core.InstanceId id, ref global::DxMessaging.Core.Messages.IBroadcastMessage value) { }
    static void SecondBroadcast(ref global::DxMessaging.Core.InstanceId id, ref global::DxMessaging.Core.Messages.IBroadcastMessage value) { }
}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(5));
            Assert.That(result.ManualReviewMethods, Has.Some.Contains("writes to parameter"));
            Assert.That(
                result.ManualReviewMethods,
                Has.Some.Contains("shares the RegisterGlobalAcceptAll call at line")
            );
        }

        [TestCase(
            "VendorHandler",
            false,
            TestName = "A conditional nonpackage binding defers an unconditional callback"
        )]
        [TestCase(
            "global::DxMessaging.Core.MessageHandler.FastHandler<Pulse>",
            true,
            TestName = "A named callback with package bindings in both configurations migrates"
        )]
        public void AnalyzeChecksEveryMethodGroupBinding(string alternateDelegate, bool migrate)
        {
            string source = @"
class Receiver
{
    delegate void VendorHandler(ref Pulse value);
    void SetUp()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> known = Forward;
#if LEGACY
        ALTERNATE alternate = Forward;
#endif
    }
    static void Forward(ref Pulse value) { }
}".Replace("ALTERNATE", alternateDelegate);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("Forward(ref", "Forward(in") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("conditional callback binding at line")
            );
        }

        [TestCase(
            "global::Vendor.@MessageRegistrationToken",
            false,
            TestName = "A verbatim type in a vendor namespace does not prove a package token"
        )]
        [TestCase(
            "global::DxMessaging.Core.@MessageRegistrationToken",
            true,
            TestName = "A verbatim type in the package namespace still proves the token"
        )]
        public void AnalyzeUsesTheWholeQualifiedTokenType(string typeName, bool migrate)
        {
            string source = @"
using DxMessaging.Core;
class Receiver
{
    TYPE token;
    void SetUp() { token.RegisterUntargeted<Pulse>((ref Pulse value) => Observe(value)); }
    static void Observe(Pulse value) { }
}".Replace("TYPE", typeName);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase(
            "global::Vendor.@DxMessaging.Core",
            false,
            TestName = "An escaped qualifier inside a vendor namespace does not prove the delegate"
        )]
        [TestCase(
            "global::@DxMessaging.Core",
            true,
            TestName = "An escaped package namespace qualifier still proves the delegate"
        )]
        public void AnalyzeUsesTheWholeQualifiedDelegateType(string typeName, bool migrate)
        {
            string source = @"
class Receiver
{
    void SetUp()
    {
        TYPE.MessageHandler.FastHandler<Pulse> callback = (ref Pulse value) => Observe(value);
    }
    static void Observe(Pulse value) { }
}".Replace("TYPE", typeName);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
        }

        [TestCase(
            "token",
            false,
            TestName = "An inferred lambda parameter overrides a bare field receiver"
        )]
        [TestCase(
            "this.token",
            true,
            TestName = "An explicitly qualified field ignores an inferred lambda parameter"
        )]
        public void AnalyzeChecksAnInferredTokenShadow(string receiver, bool migrate)
        {
            string source = @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    void Configure()
    {
        Use(token => RECEIVER.RegisterUntargeted<Pulse>((ref Pulse value) => Observe(value)));
    }

    static void Use(System.Action<global::Vendor.CustomToken> action) { }
    static void Observe(Pulse value) { }
}".Replace("RECEIVER", receiver);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [Test]
        public void AnalyzeKeepsPackageBindingsWithOrdinaryTokenReferencesAndNestedTypeArguments()
        {
            const string Source =
                @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;

    void Configure(global::DxMessaging.Core.MessageHandler handler)
    {
        token = global::DxMessaging.Core.MessageRegistrationToken.Create(handler);
        if (token == null) { return; }
        Observe(token);
        token.RegisterUntargeted<PairMessage<int, bool>>(Forward);
    }

    static void Forward(ref PairMessage<int, bool> value) { }
    static void Observe<T>(T value) { }
}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                Source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(Source.Replace("ref PairMessage", "in PairMessage"))
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase(
            "FastHandler",
            " first = (ref Pulse left) => Observe(left), second = (ref Pulse right) => Observe(right)",
            2,
            TestName = "Both initialized fast delegates in one declaration migrate"
        )]
        [TestCase(
            "FastHandler",
            " first, second = (ref Pulse right) => Observe(right)",
            1,
            TestName = "An uninitialized fast delegate preserves the following initializer"
        )]
        [TestCase(
            "@FastHandler",
            " handler = (ref Pulse value) => Observe(value)",
            1,
            TestName = "A verbatim fast delegate identifier binds the package type"
        )]
        [TestCase(
            @"FastH\u0061ndler",
            " handler = (ref Pulse value) => Observe(value)",
            1,
            TestName = "A Unicode escaped fast delegate identifier binds the package type"
        )]
        [TestCase(
            "FastHandler",
            "first = (ref Pulse left) => Observe(left),second = (ref Pulse right) => Observe(right)",
            2,
            TestName = "Fast delegate declarators may follow the generic closer immediately"
        )]
        [TestCase(
            "FastHandler",
            "?handler = (ref Pulse value) => Observe(value)",
            1,
            TestName = "A nullable fast delegate can initialize an inline callback"
        )]
        public void AnalyzeFindsEachFastDelegateInitializer(
            string handlerName,
            string declaration,
            int count
        )
        {
            string source = @"
class Receiver
{
    void Configure()
    {
        global::DxMessaging.Core.MessageHandler.HANDLER<Pulse>DECLARATION;
    }
    static void Observe(Pulse value) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("HANDLER", handlerName).Replace("DECLARATION", declaration);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source.Replace("ref Pulse", "in Pulse")));
            Assert.That(result.ReplacementCount, Is.EqualTo(count));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase(
            " handler",
            TestName = "A spaced fast delegate type pattern leaves the writable argument alone"
        )]
        [TestCase(
            "handler",
            TestName = "A compact fast delegate type pattern leaves the writable argument alone"
        )]
        [TestCase(
            "handler == true",
            TestName = "A fast delegate type pattern comparison cannot prove a declarator"
        )]
        public void AnalyzeDistinguishesFastDelegatePatternsFromDeclarations(string pattern)
        {
            string source = @"
using DxMessaging.Core;
class Receiver
{
    delegate void Writable(ref Pulse message);
    static void Callback(ref Pulse message) { }
    static void Sink(bool flag, Writable writable) { }
    static void Configure(object value, bool choose)
    {
        Writable fallback = null;
        Sink(value is MessageHandler.FastHandler<Pulse>PATTERN, fallback = Callback);
        for (; choose; ) { break; }
    }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("PATTERN", pattern);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase(
            "class",
            "set",
            "value",
            false,
            TestName = "A setter value shadows the package field"
        )]
        [TestCase(
            "class",
            "set",
            "this.value",
            true,
            TestName = "A setter can qualify the actual package field"
        )]
        [TestCase(
            "struct",
            "readonly set",
            "value",
            false,
            TestName = "A readonly struct setter still owns the implicit value"
        )]
        [TestCase(
            "struct",
            "readonly set",
            "this.value",
            true,
            TestName = "A readonly struct setter can qualify its package field"
        )]
        public void AnalyzeDistinguishesTheImplicitAccessorValue(
            string kind,
            string accessor,
            string receiver,
            bool migrate
        )
        {
            string source = @"
using DxMessaging.Core;
TYPE Receiver
{
    MessageRegistrationToken value;
    Vendor.CustomToken Other
    {
        get => null;
        ACCESSOR => RECEIVER.RegisterUntargeted<Pulse>((ref Pulse pulse) => { });
    }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
namespace Vendor
{
    class CustomToken
    {
        public delegate void Writable<T>(ref T message);
        public void RegisterUntargeted<T>(Writable<T> callback) { }
    }
}
".Replace("TYPE", kind).Replace("ACCESSOR", accessor).Replace("RECEIVER", receiver);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse pulse", "in Pulse pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.ManualReviewMethods,
                Has.Count.EqualTo(migrate ? 0 : 1),
                string.Join("\n", result.ManualReviewMethods)
            );
        }

        [TestCase("Receiver Current { set { value.Forward(ref copy); } }", false)]
        [TestCase("Receiver Current { init => value.Forward(ref copy); }", false)]
        [TestCase("void Invoke() { value.Forward(ref copy); }", true)]
        public void AnalyzeChecksImplicitAccessorReceiversBeforeChangingNamedCallbacks(
            string use,
            bool migrate
        )
        {
            string source = @"
using DxMessaging.Core;
class Receiver
{
    OtherReceiver value;
    Pulse copy;
    RECEIVER_USE

    void Install()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<global::Pulse> handler = Forward;
    }
    void Forward(ref Pulse message) { }
}

class OtherReceiver { public void Forward(ref Pulse message) { } }

public readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage
{
    public System.Type MessageType => typeof(Pulse);
}
".Replace("RECEIVER_USE", use);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(
                    migrate
                        ? source.Replace("    void Forward(ref Pulse", "    void Forward(in Pulse")
                        : source
                )
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase("internal", false)]
        [TestCase("public", false)]
        [TestCase("private", true)]
        public void AnalyzeDefersExposedCallbacksWhenTheProjectInventoryIsLimited(
            string access,
            bool migrate
        )
        {
            string source = @"
class Receiver
{
    void Install()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = Forward;
    }
    ACCESS void Forward(ref Pulse message) { }
}

public readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage
{
    public System.Type MessageType => typeof(Pulse);
}
".Replace("ACCESS", access);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source,
                _ => null,
                projectSourcesLimitedToAssets: true,
                findPackageBindingIssue: _ => null
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("scripts outside Assets"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AnalyzeRequiresPackageBindingForAnOverrideAndItsInheritedReceiver(bool proven)
        {
            const string Source =
                @"
sealed class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    protected override bool RegisterForStringMessages => true;

    protected override void HandleGlobalStringMessage(ref global::DxMessaging.Core.Messages.GlobalStringMessage message)
    {
        base.HandleGlobalStringMessage(ref message);
    }

    void Install() => Token.RegisterUntargeted<Pulse>(OnPulse);
    private void OnPulse(ref Pulse pulse) { }
}

readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                _ => null,
                projectSourcesLimitedToAssets: true,
                findPackageBindingIssue: type =>
                    proven || type != "MessageAwareComponent"
                        ? null
                        : "the compiler reference could not be verified"
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(proven ? 3 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(proven ? Source.Replace("ref ", "in ") : Source)
            );
            Assert.That(
                result.ManualReviewMethods.Any(issue =>
                    issue.Contains("line")
                    && issue.Contains("compiler reference could not be verified")
                ),
                Is.EqualTo(!proven)
            );
        }

        [TestCase("private new (int X, int Y) Token => default;", "this.Token", false)]
        [TestCase("private new (int X, int Y) Token { get; }", "Token", false)]
        [TestCase("private new (int X, int Y) Other, @Token;", "this.Token", false)]
        [TestCase(@"private new (int X, int Y) Tok\u0065n => default;", "this.Token", false)]
        [TestCase("private new (int X, int Y) Token => default;", "base.Token", true)]
        [TestCase("", "this.Token", true)]
        public void AnalyzeVerifiesTheInheritedTokenAgainstDirectMembers(
            string member,
            string receiver,
            bool migrate
        )
        {
            string source = @"
sealed class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    MEMBER_DECLARATION
    void Install()
    {
        RECEIVER.RegisterUntargeted<Pulse>((ref Pulse message) => { });
    }
}

readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("MEMBER_DECLARATION", member).Replace("RECEIVER", receiver);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref ", "in ") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase("@HandleGlobalStringMessage", "\n")]
        [TestCase(@"HandleGlobalStringMessag\u0065", "\n")]
        [TestCase(@"HandleGlobalStringMessag\u200Ce", "\n")]
        [TestCase(@"HandleGlobalStringMessag\u0065", "\r")]
        [TestCase("\nHandleGlobalStringMessag\\u0065", "\n")]
        public void AnalyzeBindsEscapedOverrideDeclarationsByIdentifierValue(
            string method,
            string newline
        )
        {
            string source = @"
sealed class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    protected override bool RegisterForStringMessages => true;
    protected override void PlaceholderHandler(ref global::DxMessaging.Core.Messages.GlobalStringMessage message)
    {
        base.HandleGlobalStringMessage(ref message);
    }
}
".Replace("PlaceholderHandler", method).Replace("\r\n", "\n").Replace("\n", newline);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.UpgradedSource, Is.EqualTo(source.Replace("ref ", "in ")));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeBindsAnEscapedNamedCallbackDeclaration()
        {
            const string Source =
                @"
sealed class Receiver
{
    void Install(global::DxMessaging.Core.MessageRegistrationToken token) =>
        token.RegisterUntargeted<Pulse>(OnPulse);

    private void OnPuls\u0065(ref Pulse pulse) { }
}

readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.UpgradedSource, Is.EqualTo(Source.Replace("ref ", "in ")));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase("using Vendor;", "", "MessageHandler", false)]
        [TestCase("using Vendor;", "", "global::DxMessaging.Core.MessageHandler", true)]
        [TestCase(
            "using Vendor; namespace Consumer { using global::DxMessaging.Core;",
            "}",
            "MessageHandler",
            true
        )]
        [TestCase("namespace Consumer { using Vendor;", "}", "MessageHandler", false)]
        [TestCase("namespace Consumer; using Vendor;", "", "MessageHandler", false)]
        [TestCase(
            "namespace Consumer { using Vendor;",
            "}",
            "DxMessaging.Core.MessageHandler",
            false
        )]
        [TestCase(
            "namespace Consumer { using Vendor;",
            "}",
            "global::DxMessaging.Core.MessageHandler",
            true
        )]
        [TestCase(
            "namespace Other { using Vendor; } namespace Consumer {",
            "}",
            "MessageHandler",
            true
        )]
        public void AnalyzeChecksNamespaceImportsInScopeBeforeChangingACompiledDelegate(
            string header,
            string footer,
            string handler,
            bool migrate
        )
        {
            string source =
                "using DxMessaging.Core;\n"
                + header
                + @"
sealed class Receiver
{
    void Install()
    {
        HANDLER.FastHandler<Pulse> callback = (ref Pulse message) => { };
    }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("HANDLER", handler)
                + footer;
            const string Assembly = "Game.dll";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript("Assets/Receiver.cs", source, Assembly);
            project.AddScript(
                "Assets/Vendor.cs",
                "namespace Vendor { class MessageHandler { public delegate void FastHandler<T>(ref T message); }"
                    + " class DxMessaging { public class Core { public class MessageHandler { public delegate void FastHandler<T>(ref T message); } } } }",
                Assembly
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source,
                projectTypes: project,
                assemblyName: Assembly
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref ", "in ") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase("namespace Consumer { using global::DxMessaging.Core;", "}")]
        [TestCase("namespace Consumer { using DxMessaging.Core;", "}")]
        [TestCase("namespace Consumer; using global::DxMessaging.Core;", "")]
        public void AnalyzeReportsDelegateFromANamespaceScopedPackageImport(
            string header,
            string footer
        )
        {
            string source =
                header
                + @"
sealed class Receiver
{
    MessageHandler.FastHandler<Pulse> callback = (ref Pulse message) => { };
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
"
                + footer;
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("line 4").And.Contain("MessageHandler type cannot be proven")
            );
        }

        [Test]
        public void AnalyzeIgnoresAPackageImportInASiblingNamespace()
        {
            const string Source =
                @"
namespace Other { using global::DxMessaging.Core; }
namespace Vendor
{
    static class MessageHandler { public delegate void FastHandler<T>(ref T message); }
}
namespace Consumer
{
    using Vendor;
    sealed class Receiver
    {
        MessageHandler.FastHandler<Pulse> callback = (ref Pulse message) => { };
    }
    readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase("Game.dll", "MessageHandler", false)]
        [TestCase("Game.dll", "global::DxMessaging.Core.MessageHandler", true)]
        [TestCase("Unrelated.dll", "MessageHandler", true)]
        [TestCase(null, "MessageHandler", false)]
        public void AnalyzeChecksGlobalNamespaceImportsFromOtherScripts(
            string importsAssembly,
            string handler,
            bool migrate
        )
        {
            string source = @"
using DxMessaging.Core;
sealed class Receiver
{
    HANDLER.FastHandler<Pulse> Callback = (ref Pulse message) => { };
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("HANDLER", handler);
            const string Assembly = "Game.dll";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript("Assets/Receiver.cs", source, Assembly);
            project.AddScript("Assets/Imports.cs", "global using Vendor;", importsAssembly);
            project.AddScript(
                "Assets/Vendor.cs",
                "namespace Vendor { class MessageHandler { public delegate void FastHandler<T>(ref T message); } }",
                Assembly
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source,
                projectTypes: project,
                assemblyName: Assembly
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase("global using static Vendor.Utility;", "Game.dll", false)]
        [TestCase("global using static Vendor.Utility;", "Game", false)]
        [TestCase("global using static Vendor.Utility;", "Other.dll", true)]
        [TestCase("global using static Vendor.Utility;", null, false)]
        [TestCase("global using MessageHandler = Vendor.Utility;", "Game.dll", false)]
        [TestCase("global using MessageHandler = Vendor.Utility;", "Other.dll", true)]
        [TestCase("global using MessageHandler = Vendor.Utility;", null, false)]
        public void AnalyzeLimitsGlobalStaticImportsAndAliasesToTheirCompilation(
            string import,
            string importAssembly,
            bool migrate
        )
        {
            const string Source =
                @"
using DxMessaging.Core;
sealed class Receiver
{
    MessageHandler.FastHandler<Pulse> Callback = (ref Pulse message) => { };
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            const string Assembly = "Game.dll";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript("Assets/Receiver.cs", Source, Assembly);
            project.AddScript("Assets/Imports.cs", import, importAssembly);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                projectTypes: project,
                assemblyName: Assembly
            );

            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? Source.Replace("ref Pulse", "in Pulse") : Source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase("Int32", true, false)]
        [TestCase("Int32", false, true)]
        [TestCase("int", true, true)]
        [TestCase("global::System.Int32", true, true)]
        public void AnalyzeChecksNearerImportsBeforeRelyingOnARegistrationArgument(
            string priorityType,
            bool competingMetadata,
            bool migrate,
            string import = "using Vendor;"
        )
        {
            string source = @"
using System;
namespace Consumer
{
    IMPORT
    class Receiver
    {
        void Install(global::DxMessaging.Core.MessageRegistrationToken token, PRIORITY priority)
        {
            token.RegisterUntargeted<Pulse>((ref Pulse message) => { }, priority);
        }
    }
    readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
}".Replace("PRIORITY", priorityType).Replace("IMPORT", import);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source,
                projectSourcesLimitedToAssets: true,
                findPackageBindingIssue: name =>
                    competingMetadata && name == "Int32"
                        ? "metadata from 'Vendor.dll' can affect the type binding"
                        : null
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            if (!migrate)
            {
                Assert.That(
                    result.ManualReviewMethods,
                    Has.Some.Contains("line").And.Contains("Vendor.dll")
                );
            }
        }

        [TestCase("Int32", false)]
        [TestCase("System.Int32", false)]
        [TestCase("int", true)]
        [TestCase("global::System.Int32", true)]
        public void AnalyzeRequiresCompilationEvidenceForARelativePriorityType(
            string priorityType,
            bool migrate
        ) =>
            AnalyzeChecksNearerImportsBeforeRelyingOnARegistrationArgument(
                priorityType,
                true,
                migrate,
                string.Empty
            );

        [TestCase("m = default;", "writes to parameter")]
        [TestCase("Observe(m);", "standalone delegate construction")]
        public void AnalyzeReportsStandaloneFastDelegateConstruction(string body, string reason)
        {
            string source = @"
class Receiver
{
    void Install()
    {
        var handler = new global::DxMessaging.Core.MessageHandler.FastHandler<Pulse>(
            (ref Pulse m) => { BODY });
    }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("BODY", body);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("line 7").And.Contain(reason));
        }

        [TestCase("(ref Pulse message) => Observe(message)", "expression-bodied property")]
        [TestCase("(ref Pulse message) => message = default", "writes to parameter")]
        [TestCase("Forward", "expression-bodied property")]
        public void AnalyzeReportsLegacyHandlersReturnedByAProperty(
            string expression,
            string reason
        )
        {
            string source = @"
sealed class Receiver
{
    global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> Handler => VALUE;
    private void Forward(ref Pulse message) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("VALUE", expression);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("line 4").And.Contain(reason));
        }

        [TestCase("global::Callbacks.IReceiver<int>.Handler => (ref Pulse message) => { };")]
        [TestCase("Callbacks.IReceiver<int>.Handler { get => (ref Pulse message) => { }; }")]
        [TestCase("Callbacks.IReceiver<int>.Handler { get { return Forward; } }")]
        [TestCase("Callbacks.IReceiver<int>.this[int index] => (ref Pulse message) => { };")]
        public void AnalyzeReportsLegacyHandlersReturnedByExplicitInterfaceProperties(string getter)
        {
            string source = @"
namespace Callbacks
{
    interface IReceiver<T>
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> MEMBER { get; }
    }
}

sealed class Receiver : Callbacks.IReceiver<int>
{
    global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> GETTER
    private void Forward(ref Pulse message) { }
}

readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("GETTER", getter).Replace(
                "MEMBER",
                getter.Contains(".this[") ? "this[int index]" : "Handler"
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("line 12").And.Contain("property")
            );
        }

        [TestCase("Game.dll", 1)]
        [TestCase("Other.dll", 0)]
        public void AnalyzeReportsDelegateFromAGlobalImportOnlyInItsCompilation(
            string importAssembly,
            int expectedReviews
        )
        {
            const string Source =
                @"sealed class Receiver
{
    private MessageHandler.FastHandler<Pulse> callback = (ref Pulse message) => { };
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }";
            const string AssemblyName = "Game";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript("Assets/Receiver.cs", Source, AssemblyName);
            project.AddScript(
                "Assets/Imports.cs",
                "global using DxMessaging.Core;",
                importAssembly
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                projectTypes: project,
                assemblyName: AssemblyName
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(expectedReviews));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("line 3"));
        }

        [TestCase(
            "#if UNITY_ANDROID\nclass Consumer { void Install(MessageRegistrationToken token) { token.RegisterUntargeted<Pulse>(null); } }\n#endif",
            null
        )]
        [TestCase("#if false\nstruct Int32 { }\n#endif", null)]
        [TestCase(
            "#if UNITY_ANDROID\nnamespace Consumer { public struct Int32 { } }\n#endif",
            "Int32"
        )]
        [TestCase(
            "public struct\n#if UNITY_ANDROID\nPlaceholder { }\n#else\nInt32 { }\n#endif",
            "Int32"
        )]
        [TestCase(
            "#if UNITY_ANDROID\nstatic class Registrations { public static void RegisterUntargeted<T>(this MessageRegistrationToken token) { } }\n#endif",
            "RegisterUntargeted"
        )]
        [TestCase(
            "static class Registrations { public static void RegisterUntargeted<T>(\n#if UNITY_ANDROID\nthis\n#endif\nMessageRegistrationToken token) { } }",
            "RegisterUntargeted"
        )]
        public void AlternateSourceScanFindsBindingDeclarationsAcrossBuildTargets(
            string source,
            string name
        )
        {
            HashSet<string> names =
                ReadonlyFastHandlerUpgrade.ProjectPackageBindings.FindAlternateSourceNames(source);

            if (name == null)
            {
                Assert.That(names, Is.Empty);
            }
            else
            {
                Assert.That(names, Does.Contain(name));
            }
        }

        [TestCase("Handler", false)]
        [TestCase("global::DxMessaging.Core.MessageHandler.FastHandler<Game.Pulse>", true)]
        public void AnalyzeReportsStandaloneLegacyDelegateFieldsDeclaredThroughAnAlias(
            string fieldType,
            bool migrate
        )
        {
            string source = @"
using Handler = global::DxMessaging.Core.MessageHandler.FastHandler<Game.Pulse>;
namespace Game
{
    readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
}
class Listener
{
    private DELEGATE handler = (ref Game.Pulse message) => { };
}
".Replace("DELEGATE", fieldType);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Game.Pulse", "in Game.Pulse") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("line 9"));
        }

        [TestCase("Game", true)]
        [TestCase("Game.dll", true)]
        [TestCase(null, true)]
        [TestCase("Other", false)]
        public void AnalyzeReportsGlobalFastDelegateAliasesWithinTheirCompilation(
            string aliasAssembly,
            bool reviews
        )
        {
            const string Alias =
                "global using Handler = global::DxMessaging.Core.MessageHandler.FastHandler<Game.Pulse>;";
            string source = @"
namespace Game
{
    DECLARATION
    readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
    class Listener
    {
        private Handler handler = (ref Pulse message) => { };
    }
}".Replace("DECLARATION", reviews ? "" : "delegate void Handler(ref Pulse message);");
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript("Assets/Aliases.cs", Alias, aliasAssembly);
            project.AddScript("Assets/Listener.cs", source, "Game");
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source,
                projectTypes: project,
                assemblyName: "Game"
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(reviews ? 1 : 0));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("line 8").And.Contains("type alias")
            );
        }

        [Test]
        public void AnalyzeLimitsFastDelegateAliasReportsToTheirNamespaceScope()
        {
            const string Source =
                @"
namespace Game
{
    readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
}
namespace Consumer
{
    using Handler = global::DxMessaging.Core.MessageHandler.FastHandler<Game.Pulse>;
    class Listener
    {
        private Handler handler = (ref Game.Pulse message) => { };
    }
}
namespace Other
{
    delegate void Handler(ref Game.Pulse message);
    class Listener
    {
        private Handler handler = (ref Game.Pulse message) => { };
    }
}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("line 11").And.Contain("type alias")
            );
        }

        [TestCase("", false, 0)]
        [TestCase("", true, 1)]
        [TestCase("#define LEGACY", true, 0)]
        [TestCase("#undef LEGACY", true, 1)]
        public void AnalyzeChecksWhetherAnInnerAliasAlwaysShadowsThePackageAlias(
            string prelude,
            bool conditional,
            int reviews
        )
        {
            string source =
                prelude
                + @"
using Handler = global::DxMessaging.Core.MessageHandler.FastHandler<Game.Pulse>;
namespace Game
{
    readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
}
namespace Compat
{
    delegate void Writable(ref Game.Pulse message);
}
namespace Consumer
{
BEGIN
    using Handler = global::Compat.Writable;
END
    class Listener
    {
        private Handler handler = (ref Game.Pulse message) => { };
    }
}".Replace("BEGIN", conditional ? "#if LEGACY" : "").Replace("END", conditional ? "#endif" : "");
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(reviews));
        }

        [TestCase("=> (ref Pulse message) => { };", "expression-bodied indexer")]
        [TestCase("{ get => Forward; }", "expression-bodied indexer")]
        [TestCase("{ get { return Forward; } }", "block-bodied indexer")]
        public void AnalyzeReportsLegacyHandlersReturnedByAnIndexer(string accessor, string reason)
        {
            string source = @"
sealed class Receiver
{
    public global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> this[int index] ACCESSOR
    private void Forward(ref Pulse message) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("ACCESSOR", accessor);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("line 4").And.Contain(reason));
        }

        [TestCase("GetHandler() => (ref Pulse message) => { };")]
        [TestCase("GetHandler<T>(T context) => (ref Pulse message) => { };")]
        [TestCase("GetHandler<T>() where T : class { return Forward; }")]
        [TestCase("GetHandler() { return (ref Pulse message) => { }; }")]
        public void AnalyzeReportsLegacyHandlersReturnedByMethods(string method)
        {
            string source = @"
sealed class Receiver
{
    private global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> METHOD
    private void Forward(ref Pulse message) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("METHOD", method);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("line 4").And.Contain("method")
            );
        }

        [TestCase(">=", "<=")]
        [TestCase("==", "!=")]
        public void AnalyzePreservesAnOptOutAfterComparisonOperators(string first, string second)
        {
            string source = @"
sealed class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    public static bool operator FIRST(Receiver left, Receiver right) { return false; }
    public static bool operator SECOND(Receiver left, Receiver right) { return true; }
    protected override bool RegisterForStringMessages => false;
    protected override void HandleGlobalStringMessage(ref global::DxMessaging.Core.Messages.GlobalStringMessage message)
    {
        base.HandleGlobalStringMessage(ref message);
    }
}
".Replace("FIRST", first).Replace("SECOND", second);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source.Replace("ref ", "in ")));
            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeReportsTheLastPropertyWhenItsAccessorReturnsALegacyHandler()
        {
            const string Source =
                @"
sealed class Receiver
{
    global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> Handler
    {
        get => (ref Pulse message) => { };
    }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("line 6").And.Contain("expression-bodied property")
            );
        }

        [TestCase("return (ref Pulse message) => { message = default; };", "writes to parameter")]
        [TestCase("return (ref Pulse message) => Observe(message);", "block-bodied property")]
        [TestCase("return Forward;", "block-bodied property")]
        public void AnalyzeReportsLegacyHandlersReturnedByABlockGetter(string body, string reason)
        {
            string source = @"
sealed class Receiver
{
    private global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> Handler
    {
        get
        {
            BODY
        }
    }

    private void Forward(ref Pulse message) { }
    private void Observe(Pulse message) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("BODY", body);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("line 8").And.Contain(reason));
        }

        [Test]
        public void AnalyzeReportsGetterReturnsFromSeparateBranchesAndIgnoresLocalFunctionReturns()
        {
            const string Source =
                @"
sealed class Receiver
{
    private delegate void Writable(ref Pulse value);
    private bool condition;
    private global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> Handler
    {
        get
        {
            Writable Local()
            {
                return (ref Pulse value) => { value = default; };
            }
            if (condition)
            {
                return (ref Pulse message) => { message = default; };
            }
            return Forward;
        }
    }

    private void Forward(ref Pulse message) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(2));
            Assert.That(
                result.ManualReviewMethods,
                Has.Some.Contains("line 16").And.Contains("writes to parameter")
            );
            Assert.That(
                result.ManualReviewMethods,
                Has.Some.Contains("line 18").And.Contains("block-bodied property")
            );
        }

        [TestCase("get => (ref Pulse message) => { };")]
        [TestCase("get { return (ref Pulse message) => { }; }")]
        public void AnalyzeReportsWritableGettersInAlternateFeasibleArms(string getter)
        {
            string source = @"
sealed class Receiver
{
    private global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> Handler
    {
#if LEGACY
        get => null;
#else
        GETTER
#endif
    }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("GETTER", getter);
            var result = ReadonlyFastHandlerUpgrade.Analyze(source);

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("line 9"));
        }

        [TestCase("condition ? (ref Pulse a) => { } : (ref Pulse b) => { }", 2)]
        [TestCase(
            "condition switch { true => (ref Pulse a) => { }, false => (ref Pulse b) => { } }",
            2
        )]
        [TestCase("((ref Pulse message) => { })", 1)]
        [TestCase("condition ? (in Pulse a) => { } : (in Pulse b) => { }", 0)]
        [TestCase("((in Pulse message) => { })", 0)]
        [TestCase("Build((ref int unrelated) => { })", 0)]
        public void AnalyzeReportsExplicitCallbacksInCompoundGetterValues(
            string value,
            int expected
        )
        {
            string source = @"
sealed class Receiver
{
    private bool condition;
    private delegate void Builder(ref int unrelated);
    private global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> Build(Builder input) => null;
    private global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> Handler { get { return VALUE; } }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("VALUE", value);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(expected));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("line 7").And.Contains("block-bodied property")
            );
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AnalyzeRequiresProofOfTheInstalledRegistration(bool verified)
        {
            const string Source =
                @"
sealed class Receiver
{
    void Install(global::DxMessaging.Core.MessageRegistrationToken token) =>
        token.RegisterUntargeted<Pulse>((ref Pulse message) => { });
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            var result = ReadonlyFastHandlerUpgrade.Analyze(Source, findPackageBindingIssue: name =>
                verified || name != "MessageRegistrationToken.RegisterUntargeted"
                    ? null
                    : "the installed registration overload set differs"
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(verified ? Source.Replace("ref Pulse", "in Pulse") : Source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(verified ? 1 : 0));
            if (!verified)
            {
                Assert.That(
                    result.ManualReviewMethods,
                    Has.Some.Contains("line 5")
                        .And.Contains("installed registration overload set differs")
                );
            }
        }

        [Test]
        public void AnalyzeReportsAStandaloneMethodGroupWithAWritableDelegateCall()
        {
            const string Source =
                @"
class Receiver
{
    void Install(ref Pulse message)
    {
        var handler = new global::DxMessaging.Core.MessageHandler.FastHandler<Pulse>(Forward);
        handler(ref message);
    }
    private void Forward(ref Pulse message) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("line 6").And.Contain("standalone delegate construction")
            );
        }

        [Test]
        public void AnalyzeReportsAnAbstractStringHandlerWithoutChangingItsContract()
        {
            const string Source =
                @"
abstract class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    protected abstract override void HandleGlobalStringMessage(ref global::DxMessaging.Core.Messages.GlobalStringMessage message);
}
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("line 4").And.Contain("abstract override")
            );
        }

        [TestCase("\n")]
        [TestCase("\r\n")]
        [TestCase("\r")]
        [TestCase("\u0085")]
        [TestCase("\u2028")]
        [TestCase("\u2029")]
        public void AnalyzeUsesTheMemberLineEndingForTheStringHandlerOptIn(string newline)
        {
            string source = string.Join(
                newline,
                "sealed class Receiver : global::DxMessaging.Unity.MessageAwareComponent",
                "{",
                "    protected override void HandleGlobalStringMessage(ref global::DxMessaging.Core.Messages.GlobalStringMessage message)",
                "    {",
                "        base.HandleGlobalStringMessage(ref message);",
                "    }",
                "}"
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            string expected = source
                .Insert(
                    source.IndexOf('{') + 1,
                    newline
                        + "    protected override bool RegisterForStringMessages => true;"
                        + newline
                )
                .Replace("ref ", "in ");

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.UpgradedSource, Is.EqualTo(expected));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase("RegisterForStringMessages")]
        [TestCase(@"RegisterForStringMessag\u0065s")]
        public void AnalyzeDefersTheOptInWhenTheComponentHasTheSameName(string componentName)
        {
            string source = @"
sealed class COMPONENT : global::DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref global::DxMessaging.Core.Messages.GlobalStringMessage message) =>
        base.HandleGlobalStringMessage(ref message);
}
".Replace("COMPONENT", componentName);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source.Replace("ref ", "in ")));
            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("component has the same name"));
        }

        [Test]
        public void AnalyzeRequiresSeparateInstalledStringOptInProof()
        {
            const string Source =
                @"
sealed class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref global::DxMessaging.Core.Messages.GlobalStringMessage message) =>
        base.HandleGlobalStringMessage(ref message);
}
";
            const string Symbol = "MessageAwareComponent.RegisterForStringMessages";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                findPackageBindingIssue: symbol =>
                    symbol == Symbol ? "the installed getter is not overridable" : null
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source.Replace("ref ", "in ")));
            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("not overridable"));
        }

        [Test]
        public void AnalyzeRecognizesAnExplicitlyRootedCompilationUnitImport()
        {
            const string Source =
                @"
using global::DxMessaging.Core;
sealed class Receiver
{
    MessageHandler.FastHandler<Pulse> Callback = (ref Pulse message) => { };
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source.Replace("ref Pulse", "in Pulse")));
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AnalyzeRequiresPackageBindingForAQualifiedRegistrationToken(bool proven)
        {
            const string Source =
                @"
sealed class Receiver
{
    void Install(global::DxMessaging.Core.MessageRegistrationToken token) =>
        token.RegisterUntargeted<Pulse>(OnPulse);

    private void OnPulse(ref Pulse pulse) { }
}

readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                _ => null,
                projectSourcesLimitedToAssets: true,
                findPackageBindingIssue: type =>
                    proven || type != "MessageRegistrationToken"
                        ? null
                        : "the compiler reference could not be verified"
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(proven ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(proven ? Source.Replace("ref ", "in ") : Source)
            );
            Assert.That(
                result.ManualReviewMethods.Any(issue =>
                    issue.Contains("line")
                    && issue.Contains("compiler reference could not be verified")
                ),
                Is.EqualTo(!proven)
            );
        }

        [TestCase("=> token.RegisterUntargeted<Pulse>((ref Pulse message) => { });")]
        [TestCase("{ token.RegisterUntargeted<Pulse>((ref Pulse message) => { }); }")]
        public void AnalyzeKeepsAReceiverWhoseTypeIsShadowedByACallableParameter(string body)
        {
            string source = @"
using DxMessaging.Core;
class Receiver
{
    void Install<@MessageRegistrationToken>(MessageRegistrationToken token)
        CALL_BODY
}

readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("CALL_BODY", body);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
        }

        [TestCase("Receiver<@Pulse>", "", "", false)]
        [TestCase(@"Receiver<\u0050ulse>", "", "", false)]
        [TestCase("Receiver<[Probe(typeof(List<int>))] Pulse>", "", "", false)]
        [TestCase("Receiver", "", "<@Pulse>", false)]
        [TestCase(
            "Receiver<Pulse>",
            "where Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage",
            "<@Pulse>",
            false
        )]
        [TestCase(
            "Receiver<Pulse>",
            "where Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage",
            "",
            true
        )]
        [TestCase("Receiver<Other>", "", "", true)]
        public void AnalyzeChecksTheNearestGenericParameterBeforeProvingARoute(
            string receiver,
            string constraint,
            string parameters,
            bool migrate
        )
        {
            string source = @"
class RECEIVER CONSTRAINT
{
    void InstallPARAMETERS(global::DxMessaging.Core.MessageRegistrationToken token) =>
        token.RegisterUntargeted<Pulse>((ref Pulse message) => { });
}

readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("RECEIVER", receiver).Replace("CONSTRAINT", constraint).Replace("PARAMETERS", parameters);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref ", "in ") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase(
            "separate(ref pulse);",
            false,
            TestName = "A writable delegate invocation preserves its initializer"
        )]
        [TestCase(
            "separate?.Invoke(ref pulse);",
            false,
            TestName = "A conditional writable invocation preserves its initializer"
        )]
        [TestCase(
            "var alias = separate; alias(ref pulse);",
            false,
            TestName = "An unproven delegate alias preserves its initializer"
        )]
        [TestCase(
            "token.RegisterUntargeted<Pulse>(priority: 0, untargetedHandler: separate);",
            true,
            TestName = "A proven named registration keeps the delegate initializer eligible"
        )]
        [TestCase(
            "token.RegisterUntargeted(separate);",
            true,
            TestName = "A proven inferred registration keeps the delegate initializer eligible"
        )]
        public void AnalyzeChecksUsesOfAnInitializedFastDelegate(string use, bool migrate)
        {
            string source = @"
using DxMessaging.Core;
class Receiver
{
    void Configure(MessageRegistrationToken token, Pulse pulse)
    {
        MessageHandler.FastHandler<Pulse> separate = Callback;
        USE_DELEGATE
    }

    static void Callback(ref Pulse pulse) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("USE_DELEGATE", use);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse pulse", "in Pulse pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            if (!migrate)
            {
                Assert.That(result.ManualReviewMethods[0], Does.Contain("use on line 8"));
            }
        }

        [Test]
        public void AnalyzeChecksInheritedUsesOfASecondFastDelegateDeclarator()
        {
            const string Source =
                @"
using DxMessaging.Core;
class Base
{
    protected MessageHandler.FastHandler<Pulse> independent = (ref Pulse unused) => { }, separate = Callback;
    private static void Callback(ref Pulse pulse) { }
}
class Receiver : Base
{
    void Forward(Pulse pulse) => separate(ref pulse);
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(Source.Replace("ref Pulse unused", "in Pulse unused"))
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("use on line 10"));
        }

        [Test]
        public void AnalyzeChecksStoredDelegateUsesInAnotherPartialDeclaration()
        {
            const string Source =
                @"
using DxMessaging.Core;
partial class Receiver
{
    private global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> separate = Callback;
    private static void Callback(ref Pulse pulse) { }
}
partial class Receiver
{
    void Forward(Pulse pulse) => separate(ref pulse);
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("use on line 10"));
        }

        [TestCase("Callbacks.OnPulse")]
        [TestCase("global::Callbacks.OnPulse")]
        [TestCase("new MessageHandler.FastHandler<Pulse>(Callbacks.OnPulse)")]
        [TestCase("(MessageHandler.FastHandler<Pulse>)Callbacks.OnPulse")]
        [TestCase("Callbacks.Generic<Pulse>")]
        [TestCase("Callbacks.@OnEscaped")]
        [TestCase("Generic<Pulse>")]
        [TestCase("@OnEscaped")]
        [TestCase("(Callbacks.OnPulse)")]
        [TestCase("enabled ? new MessageHandler.FastHandler<Pulse>(Callbacks.OnPulse) : default")]
        public void AnalyzeReportsWritableCallbackReferencesOnAnUnprovedReceiver(string callback)
        {
            string source = @"
using DxMessaging.Core;
using static Callbacks;
class Receiver
{
    void Configure(Other other, bool enabled)
    {
        other.Token.RegisterUntargeted<Pulse>(untargetedHandler: CALLBACK);
    }
}
class Other { public MessageRegistrationToken Token; }
static class Callbacks
{
    public static void OnPulse(ref Pulse pulse) { }
    public static void Generic<T>(ref T pulse) { }
    public static void On\u0045scaped(ref Pulse pulse) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("CALLBACK", callback);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("receiver whose MessageRegistrationToken type cannot be proven")
            );
        }

        [TestCase(
            "get;",
            "{ }",
            "",
            true,
            TestName = "A proved fast delegate auto-property initializer migrates"
        )]
        [TestCase(
            "get; private set;",
            "{ }",
            "Handler?.Invoke(ref pulse);",
            false,
            TestName = "A writable property delegate invocation preserves its initializer"
        )]
        [TestCase(
            "get; init;",
            "{ message = default; }",
            "",
            false,
            TestName = "A writable property callback is reported at its initializer"
        )]
        public void AnalyzeChecksFastDelegateAutoPropertyInitializers(
            string accessors,
            string lambdaBody,
            string invocation,
            bool migrate
        )
        {
            string source = @"
using DxMessaging.Core;
class Receiver
{
    private MessageHandler.FastHandler<Pulse> Handler { ACCESSORS } =
        (ref Pulse message) => LAMBDA_BODY;
    void Configure(Pulse pulse)
    {
        INVOCATION
    }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("ACCESSORS", accessors).Replace("LAMBDA_BODY", lambdaBody).Replace(
                "INVOCATION",
                invocation
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(
                    migrate ? source.Replace("ref Pulse message", "in Pulse message") : source
                )
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase("value", false)]
        [TestCase("(value)", false)]
        [TestCase("this.value", true)]
        public void AnalyzeDistinguishesAccessorArgumentsFromTheField(
            string priority,
            bool migrateAccessor
        )
        {
            string source = @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
class Receiver
{
    readonly MessageRegistrationToken token = MessageRegistrationToken.Create(new MessageHandler());
    int value;
    string Setting
    {
        set => token.RegisterUntargeted<GlobalStringMessage>(
            (ref GlobalStringMessage accessorMessage) => { }, priority: PRIORITY);
    }
    void Configure()
    {
        token.RegisterUntargeted<GlobalStringMessage>(
            (ref GlobalStringMessage fieldMessage) => { }, priority: (value));
    }
}".Replace("PRIORITY", priority);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            string expected = source.Replace(
                "ref GlobalStringMessage fieldMessage",
                "in GlobalStringMessage fieldMessage"
            );
            if (migrateAccessor)
            {
                expected = expected.Replace(
                    "ref GlobalStringMessage accessorMessage",
                    "in GlobalStringMessage accessorMessage"
                );
            }
            Assert.That(result.UpgradedSource, Is.EqualTo(expected));
            Assert.That(result.ReplacementCount, Is.EqualTo(migrateAccessor ? 2 : 1));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrateAccessor ? 0 : 1));
        }

        [TestCase("value => { }")]
        [TestCase("static value => { }")]
        public void AnalyzeKeepsUnrelatedDelegateEligibleWithByValueGlobalCallbacks(string callback)
        {
            string source = @"
using DxMessaging.Core;
class Receiver
{
    void Configure(MessageRegistrationToken token)
    {
        MessageHandler.FastHandler<Pulse> separate = Callback;
        token.RegisterGlobalAcceptAll(CALLBACK_ARGUMENT, (target, message) => { }, (source, message) => { });
        token.RegisterUntargeted(separate);
    }

    static void Callback(ref Pulse pulse) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("CALLBACK_ARGUMENT", callback);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source.Replace("ref Pulse", "in Pulse")));
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase(
            "using DxMessaging.Core.Messages; namespace Game { public struct Pulse : IUntargetedMessage { } }",
            "Assembly-CSharp",
            true,
            TestName = "An indexed message in another source proves the registration route"
        )]
        [TestCase(
            "namespace Game { public struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { } }",
            "Assembly-CSharp",
            true,
            TestName = "An indexed message can prove its fully qualified route marker"
        )]
        [TestCase(
            "#if LEGACY\nnamespace Game { public struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { } }\n#endif",
            "Assembly-CSharp",
            false,
            TestName = "A conditional project message declaration leaves the route uncertain"
        )]
        [TestCase(
            "namespace Game { file readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { } }",
            "Assembly-CSharp",
            false,
            TestName = "A file-local readonly type cannot prove another script's route"
        )]
        [TestCase(
            "namespace Game { public struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { } }",
            "Other-Assembly",
            false,
            TestName = "A separately compiled script cannot prove the consumer's message route"
        )]
        [TestCase(
            "namespace Game { public struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { } }",
            null,
            false,
            TestName = "A script without a known compilation assembly cannot prove the route"
        )]
        public void AnalyzeProvesAProjectMessageRoute(
            string messageSource,
            string messageAssembly,
            bool migrate
        )
        {
            const string Source =
                @"
using DxMessaging.Core;
class Receiver
{
    void Configure(MessageRegistrationToken token)
    {
        token.RegisterUntargeted<global::Game.Pulse>((ref global::Game.Pulse pulse) => { });
    }
}";
            const string Path = "Assets/Receiver.cs";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            const string AssemblyName = "Assembly-CSharp";
            project.AddScript(Path, Source, AssemblyName);
            project.AddScript("Assets/Pulse.cs", messageSource, messageAssembly);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                name => project.FindOtherUse(Path, name),
                project,
                AssemblyName
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? Source.Replace("ref ", "in ") : Source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase(
            "public class MessageHandler { }",
            false,
            TestName = "A project script can shadow the fully qualified package delegate"
        )]
        [TestCase(
            "public class Outer { public class MessageHandler { } }",
            true,
            TestName = "A nested namesake leaves the package delegate eligible"
        )]
        [TestCase(
            "#if LEGACY\npublic class Outer {\n#endif\npublic class MessageHandler { }\n#if LEGACY\n}\n#endif\n",
            false,
            TestName = "A conditionally nested delegate name can shadow the package in another build"
        )]
        [TestCase(
            "#if true\npublic class Outer {\n#endif\npublic class MessageHandler { }\n#if true\n}\n#endif\n",
            true,
            TestName = "A proven nested delegate name leaves the package eligible"
        )]
        public void AnalyzeChecksOtherScriptsForTheDelegateType(string otherType, bool migrate)
        {
            const string Source =
                @"
class Receiver
{
    void Configure()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler =
            (ref Pulse message) => Observe(message);
    }
    static void Observe(Pulse value) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }";
            const string Path = "Assets/Receiver.cs";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript(Path, Source);
            project.AddScript(
                "Assets/Other.cs",
                "namespace DxMessaging.Core {\n" + otherType + "\n}"
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                name => project.FindOtherUse(Path, name),
                project
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? Source.Replace("ref Pulse", "in Pulse") : Source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase(
            "",
            "namespace Consumer { class MessageHandler { } }",
            false,
            TestName = "A type in the consumer namespace can shadow the imported delegate"
        )]
        [TestCase(
            "DxMessaging.Core.",
            "namespace Consumer.DxMessaging.Core { class MessageHandler { } }",
            false,
            TestName = "A related project namespace can shadow a relative delegate qualifier"
        )]
        [TestCase(
            "global::DxMessaging.Core.",
            "namespace Consumer.DxMessaging.Core { class MessageHandler { } }",
            true,
            TestName = "A global qualifier avoids the related namespace shadow"
        )]
        [TestCase(
            "",
            "global using MessageHandler = Vendor.Handler;",
            false,
            TestName = "A global alias in another source can shadow an imported delegate"
        )]
        public void AnalyzeChecksProjectNamespaceBeforeSelectingAnImportedDelegate(
            string qualifier,
            string otherSource,
            bool migrate
        )
        {
            string source = @"
using DxMessaging.Core;
namespace Consumer
{
    class Receiver
    {
        void Configure()
        {
            QUALIFIERMessageHandler.FastHandler<Pulse> handler = (ref Pulse message) => Observe(message);
        }
        static void Observe(Pulse value) { }
    }
    readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
}".Replace("QUALIFIER", qualifier);
            const string Path = "Assets/Receiver.cs";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript(Path, source);
            project.AddScript("Assets/Other.cs", otherSource);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source,
                name => project.FindOtherUse(Path, name),
                project
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [TestCase(
            true,
            TestName = "A writable registration in an interpolation hole is reported in place"
        )]
        [TestCase(
            false,
            TestName = "Literal registration text in an interpolation stays out of the preview"
        )]
        public void AnalyzeReportsWritablePackageRegistrationsInExecutableHoles(bool executable)
        {
            string contents = executable
                ? "{token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message))}"
                : "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message))";
            string source = @"
using DxMessaging.Core;
class Receiver
{
    void Configure(MessageRegistrationToken token)
    {
        string text = EXPRESSION;
    }
    static void Observe(Pulse value) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }".Replace(
                "EXPRESSION",
                "$\"" + contents + "\""
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(executable ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("line 7"));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("interpolated registration"));
        }

        [Test]
        public void AnalyzeDefersRegistrationWithAnAttributedCompetingExtension()
        {
            const string Source =
                @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    void Configure()
    {
        token.RegisterUntargeted<Pulse>((ref Pulse value) => Observe(value), priority: 0);
    }
    static void Observe(Pulse value) { }
}";
            const string Extension =
                @"
class ProbeAttribute : System.Attribute { }

static class LegacyExtensions
{
    public delegate void Handler<T>(ref T value);
    public static void RegisterUntargeted<T>(
        [Probe] this global::DxMessaging.Core.MessageRegistrationToken token,
        Handler<T> untargetedHandler,
        int priority
    ) { }
}";
            string withExtension = Source + Extension;
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                withExtension
            );
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.UpgradedSource, Is.EqualTo(withExtension));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("overload whose package binding cannot be proven")
            );

            ReadonlyFastHandlerUpgrade.UpgradeResult control = AnalyzeWithFixtureMessageTypes(
                Source
            );
            Assert.That(control.ReplacementCount, Is.EqualTo(1));
            Assert.That(
                control.UpgradedSource,
                Is.EqualTo(Source.Replace("ref Pulse", "in Pulse"))
            );
            Assert.That(control.ManualReviewMethods, Is.Empty);
        }

        [TestCase(
            ", int extra",
            true,
            TestName = "An extension with a missing required argument cannot compete"
        )]
        [TestCase(
            ", int extra, params int[] rest",
            true,
            TestName = "A variadic extension still needs its required arguments"
        )]
        [TestCase(
            ", int extra = 0",
            false,
            TestName = "An extension can omit its defaulted argument"
        )]
        [TestCase(
            ", params int[] rest",
            false,
            TestName = "An extension can omit its variadic argument"
        )]
        [TestCase(
            ", [System.Runtime.InteropServices.Optional] int extra",
            false,
            TestName = "An extension parameter attribute can permit an omitted argument"
        )]
        [TestCase(
            "#if LEGACY\n, int extra\n#endif",
            false,
            TestName = "A conditional extension can have different required arguments"
        )]
        public void AnalyzeRequiresEnoughArgumentsForCompetingExtensions(
            string extraParameters,
            bool migrate
        )
        {
            string source = @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    void Configure()
    {
        token.RegisterUntargeted<Pulse>((ref Pulse value) => Observe(value));
    }
    static void Observe(Pulse value) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }

static class LegacyExtensions
{
    public delegate void Handler<T>(ref T value);
    public static void RegisterUntargeted<T>(
        this global::DxMessaging.Core.MessageRegistrationToken token,
        Handler<T> untargetedHandler
PARAMETERS
    ) { }
}".Replace("PARAMETERS", extraParameters);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
        }

        [Test]
        public void AnalyzeKeepsStringOptInEligibleWhenAnotherPropertyReadsIt()
        {
            AssertStringOptInWithExpression("RegisterForStringMessages");
        }

        [TestCase(
            "!RegisterForStringMessages",
            TestName = "A negated inherited setting read keeps the opt-in eligible"
        )]
        [TestCase(
            "true && RegisterForStringMessages",
            TestName = "A logical inherited setting read keeps the opt-in eligible"
        )]
        [TestCase(
            "true ? RegisterForStringMessages : false",
            TestName = "A conditional inherited setting read keeps the opt-in eligible"
        )]
        public void AnalyzeKeepsStringOptInEligibleWhenAnotherPropertyUsesItInAnExpression(
            string expression
        )
        {
            AssertStringOptInWithExpression(expression);
        }

        private static void AssertStringOptInWithExpression(string expression)
        {
            string source = @"
class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    bool Enabled => EXPRESSION;
    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }
}".Replace("EXPRESSION", expression);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.UpgradedSource, Does.Contain($"bool Enabled => {expression};"));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase(
            false,
            TestName = "A local function outside the binding block cannot migrate an inherited method group"
        )]
        [TestCase(true, TestName = "A local function visible to a package delegate still migrates")]
        public void AnalyzeChecksTheDeclaringBlockOfLocalCallback(bool visible)
        {
            string source = @"
class Parent
{
    protected static void Forward(ref Pulse value) { }
}
class Receiver : Parent
{
    void Configure()
    {
        INSIDE
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> callback = Forward;
    }

    void Elsewhere()
    {
        OUTSIDE
    }
}".Replace("INSIDE", visible ? "static void Forward(ref Pulse value) { }" : "").Replace(
                "OUTSIDE",
                visible ? "" : "static void Forward(ref Pulse value) { }"
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            string expected = visible
                ? source.Replace(
                    "        static void Forward(ref Pulse value)",
                    "        static void Forward(in Pulse value)"
                )
                : source;
            Assert.That(result.UpgradedSource, Is.EqualTo(expected));
            Assert.That(result.ReplacementCount, Is.EqualTo(visible ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(visible ? 0 : 1));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("cannot select the local declaration")
            );
        }

        [Test]
        public void AnalyzeDefersNamedHandlerUsedAsTheArgumentToAnUnprovenNameof()
        {
            const string Source =
                @"
using static LegacyNames;
class Receiver
{
    void Configure()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> callback = Forward;
        string label = nameof(Forward);
    }
    static void Forward(ref Pulse value) { }
}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                Source
            );
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("callback binding at line"));
        }

        [TestCase(
            "Receiver",
            false,
            TestName = "A writable call from a different type defers the selected callback"
        )]
        [TestCase(
            "IndependentReceiver",
            true,
            TestName = "A writable call to a proven independent type still permits migration"
        )]
        public void AnalyzeChecksWritableCallsInOtherTypes(string receiverType, bool migrate)
        {
            string source = @"
class Receiver
{
    void SetUp()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> known = Forward;
    }
    public void Forward(ref Pulse value) { }
}
class Caller
{
    void Deliver(RECEIVER receiver)
    {
        Pulse copy = default;
        receiver.Forward(ref copy);
    }
}
class IndependentReceiver { public void Forward(ref Pulse value) { } }
".Replace("RECEIVER", receiverType);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            string expected = migrate
                ? source.Replace(
                    "    public void Forward(ref Pulse value)",
                    "    public void Forward(in Pulse value)"
                )
                : source;
            Assert.That(result.UpgradedSource, Is.EqualTo(expected));
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("direct call at line"));
        }

        [TestCase(
            true,
            TestName = "An ordinary call can change from an extension to the migrated member"
        )]
        [TestCase(false, TestName = "A named callback used only as a delegate still migrates")]
        public void AnalyzeChecksOrdinaryCallBindingBeforeChangingSignature(bool directCall)
        {
            const string Call = "        this.Forward(default(Pulse));";
            string source = @"
sealed class Receiver
{
    void Configure()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> callback = Forward;
CALL
    }
    void Forward(ref Pulse value) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
static class Extensions
{
    public static void Forward(this Receiver receiver, Pulse value) { }
}".Replace("CALL", directCall ? Call : "");
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(directCall ? 0 : 1));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(directCall ? source : source.Replace("ref Pulse", "in Pulse"))
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(directCall ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("binding can change"));
        }

        [TestCase("Alias", "ref", "LAMBDA", 0, 1)]
        [TestCase("Alias", "in", "LAMBDA", 0, 0)]
        [TestCase("Alias", "ref", "new Alias(LAMBDA)", 0, 1)]
        [TestCase("Alias", "ref", "true ? LAMBDA : LAMBDA", 0, 1)]
        [TestCase(
            "global::DxMessaging.Core.MessageHandler.FastHandler<Pulse>",
            "ref",
            "LAMBDA",
            1,
            0
        )]
        public void AnalyzeReportsAnUnresolvedWritableCallbackValue(
            string type,
            string modifier,
            string initializer,
            int replacements,
            int reviews
        )
        {
            string source = @"
using Alias = global::DxMessaging.Core.MessageHandler.FastHandler<Pulse>;
sealed class Receiver
{
    void Install(global::DxMessaging.Core.MessageRegistrationToken token)
    {
        DELEGATE_TYPE Handle = INITIALIZER;
        token.RegisterUntargeted<Pulse>(Handle);
    }
    private void Handle(ref Pulse message) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("DELEGATE_TYPE", type).Replace(
                "INITIALIZER",
                initializer.Replace("LAMBDA", $"({modifier} Pulse message) => {{ }}")
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(replacements));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(reviews));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("line 8").And.Contains("delegate value")
            );
            Assert.That(
                result.UpgradedSource,
                Does.Contain("private void Handle(ref Pulse message)")
            );
        }

        [TestCase("=> (ref Pulse message) => { };")]
        [TestCase("{ get { return (ref Pulse message) => { }; } }")]
        [TestCase("{ get; } = new Alias((ref Pulse message) => { });")]
        public void AnalyzeReportsAnUnresolvedWritableCallbackProperty(string property)
        {
            string source = @"
using Alias = global::DxMessaging.Core.MessageHandler.FastHandler<Pulse>;
sealed class Receiver
{
    private Alias Handler PROPERTY
    void Install(global::DxMessaging.Core.MessageRegistrationToken token) =>
        token.RegisterUntargeted<Pulse>(Handler);
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("PROPERTY", property);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("line 7").And.Contain("delegate value")
            );
        }

        [TestCase(
            "untargeted",
            false,
            TestName = "A global registration also defers a separate delegate initializer"
        )]
        [TestCase(
            "alias",
            false,
            TestName = "An unresolved global delegate alias preserves its possible initializer"
        )]
        [TestCase(
            "untargeted",
            true,
            TestName = "A local delegate shadows a namesake method in a global callback group"
        )]
        [TestCase(
            "alias",
            true,
            TestName = "A delegate alias shadows a namesake method in a global callback group"
        )]
        public void AnalyzeCouplesGlobalRegistrationsToDelegateInitializers(
            string argument,
            bool namesakeMethod
        )
        {
            string source = @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
class Receiver
{
    MessageRegistrationToken token;
    NAMESAKE
    void Register()
    {
        MessageHandler.FastHandler<IUntargetedMessage> untargeted = (ref IUntargetedMessage value) => { };
        var alias = untargeted;
        token.RegisterGlobalAcceptAll(
            ARGUMENT,
            (ref global::DxMessaging.Core.InstanceId target, ref ITargetedMessage value) => { },
            (ref global::DxMessaging.Core.InstanceId source, ref IBroadcastMessage value) => { });
    }
}".Replace("ARGUMENT", argument).Replace(
                "NAMESAKE",
                namesakeMethod ? $"void {argument}(ref IUntargetedMessage value) {{ }}" : ""
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(
                result.ManualReviewMethods,
                Has.Some.Contains("shares the RegisterGlobalAcceptAll call")
            );
        }

        [TestCase(
            false,
            TestName = "A deconstructed delegate alias preserves the incomplete global callback group"
        )]
        [TestCase(
            true,
            TestName = "An inferred delegate parameter preserves the incomplete global callback group"
        )]
        public void AnalyzePreservesInitializersWhenAnInferredGlobalBindingIsUnresolved(
            bool useInferredParameter
        )
        {
            string source = @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
class Receiver
{
    void Untargeted(ref IUntargetedMessage value) { }
    void Register(MessageRegistrationToken token)
    {
        MessageHandler.FastHandler<IUntargetedMessage> hidden = (ref IUntargetedMessage value) => { };
        BEFORE
        token.RegisterGlobalAcceptAll(
            Untargeted,
            (ref InstanceId target, ref ITargetedMessage value) => { },
            (ref InstanceId source, ref IBroadcastMessage value) => { });
        AFTER
    }
    static void Apply(
        MessageHandler.FastHandler<IUntargetedMessage> value,
        System.Action<MessageHandler.FastHandler<IUntargetedMessage>> action) => action(value);
}".Replace(
                "BEFORE",
                useInferredParameter
                    ? "Apply(hidden, Untargeted => {"
                    : "var (Untargeted, _) = (hidden, 0);"
            ).Replace("AFTER", useInferredParameter ? "});" : "");

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(
                result.ManualReviewMethods,
                Has.Some.Contains("shares the RegisterGlobalAcceptAll call")
            );
        }

        [TestCase(
            "@RegisterGlobalAcceptAll",
            false,
            TestName = "A verbatim global registration holds its separate delegate initializer"
        )]
        [TestCase(
            @"RegisterGlobalAccept\u0041ll",
            false,
            TestName = "A Unicode escaped global registration holds its separate delegate initializer"
        )]
        [TestCase(
            "RegisterGlobalAcceptAll",
            true,
            TestName = "An interpolated global registration holds its separate delegate initializer"
        )]
        public void AnalyzeRecognizesGlobalRegistrationAcrossSourceSpellings(
            string method,
            bool interpolated
        )
        {
            string call =
                $@"token.{method}(
                    untargeted,
                    (ref InstanceId target, ref ITargetedMessage value) => {{ }},
                    (ref InstanceId source, ref IBroadcastMessage value) => {{ }})";
            string statement = interpolated ? "var text = $\"{" + call + "}\";" : call + ";";
            string source = @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
class Receiver
{
    void Register(MessageRegistrationToken token)
    {
        MessageHandler.FastHandler<IUntargetedMessage> untargeted = (ref IUntargetedMessage value) => { };
        STATEMENT
    }
}".Replace("STATEMENT", statement);

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Is.Not.Empty);
        }

        [TestCase(
            false,
            TestName = "A directly exported delegate initializer defers with its external global group"
        )]
        [TestCase(
            true,
            TestName = "An exposed delegate alias defers its private initializer with the external global group"
        )]
        public void AnalyzePreservesDelegateInitializerUsedInAnotherScript(
            bool exposeThroughProperty
        )
        {
            string source = @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
static class Callbacks
{
    ACCESS static readonly MessageHandler.FastHandler<IUntargetedMessage> VALUE =
        (ref IUntargetedMessage value) => { };
    ALIAS
}".Replace("ACCESS", exposeThroughProperty ? "private" : "public").Replace(
                "VALUE",
                exposeThroughProperty ? "hidden" : "Untargeted"
            ).Replace(
                "ALIAS",
                exposeThroughProperty
                    ? "public static MessageHandler.FastHandler<IUntargetedMessage> Untargeted => hidden;"
                    : ""
            );
            const string Other =
                @"
class Receiver
{
    void Register(global::DxMessaging.Core.MessageRegistrationToken token)
    {
        var alias = Callbacks.Untargeted;
        token.RegisterGlobalAcceptAll(alias, Targeted, Broadcast);
    }
}";
            const string Path = "Assets/Callbacks.cs";
            const string OtherPath = "Assets/Receiver.cs";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses uses = new();
            uses.AddScript(Path, source);
            uses.AddScript(OtherPath, Other);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source,
                name => uses.FindOtherUse(Path, name)
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            int expectedLine = exposeThroughProperty ? 7 : 6;
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain($"{OtherPath}, line {expectedLine}")
            );
        }

        [TestCase(
            false,
            TestName = "A proved property alias with no observed use preserves eligibility"
        )]
        [TestCase(
            true,
            TestName = "A writable use through a property alias preserves its initializer"
        )]
        public void AnalyzeChecksUsesOfAnExposedFastDelegateProperty(bool hasExternalInvocation)
        {
            const string Source =
                @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
static class Callbacks
{
    private static readonly MessageHandler.FastHandler<IUntargetedMessage> hidden =
        (ref IUntargetedMessage value) => { };
    public static MessageHandler.FastHandler<IUntargetedMessage> Exposed => hidden;
}";
            const string ExternalPath = "Assets/Consumer.cs";
            const string Path = "Assets/Callbacks.cs";
            string external = hasExternalInvocation
                ? @"
using DxMessaging.Core.Messages;
class Other
{
    void Forward(IUntargetedMessage message) => Callbacks.Exposed(ref message);
}"
                : "class Other { const string Name = \"Exposed\"; }";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript(Path, Source);
            project.AddScript(ExternalPath, external);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                name => project.FindOtherUse(Path, name),
                project
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(hasExternalInvocation ? Source : Source.Replace("ref ", "in "))
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(hasExternalInvocation ? 0 : 1));
            Assert.That(
                result.ManualReviewMethods,
                Has.Count.EqualTo(hasExternalInvocation ? 1 : 0)
            );
            if (hasExternalInvocation)
            {
                Assert.That(result.ManualReviewMethods[0], Does.Contain(ExternalPath + ", line 5"));
            }
        }

        [Test]
        public void AnalyzeKeepsADelegateWhenConditionalPropertiesShareItsInitializerReference()
        {
            const string Source =
                @"
using DxMessaging.Core.Messages;
static class Callbacks
{
    private static readonly global::DxMessaging.Core.MessageHandler.FastHandler<IUntargetedMessage> hidden =
        (ref IUntargetedMessage value) => { };
#if LEGACY
    public static global::DxMessaging.Core.MessageHandler.FastHandler<IUntargetedMessage> LegacyAlias =>
#else
    public static global::DxMessaging.Core.MessageHandler.FastHandler<IUntargetedMessage> UnusedAlias =>
#endif
        hidden;
}";
            const string External =
                @"
#if LEGACY
using DxMessaging.Core.Messages;
class Other
{
    void Forward(IUntargetedMessage message) => Callbacks.LegacyAlias(ref message);
}
#endif
";
            const string Path = "Assets/Callbacks.cs";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript(Path, Source);
            project.AddScript("Assets/Consumer.cs", External);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                name => project.FindOtherUse(Path, name),
                project
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
        }

        [TestCase(
            "owner.GetProperty(\"Exposed\").GetValue(null)",
            TestName = "A reflected property alias keeps the fast delegate writable"
        )]
        [TestCase(
            "owner.GetMethod(\"get_Exposed\").Invoke(null, null)",
            TestName = "A reflected property getter keeps the fast delegate writable"
        )]
        [TestCase(
            "System.Linq.Expressions.Expression.Lambda<System.Func<object>>("
                + "System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Property"
                + "(null, owner, \"Exposed\"), typeof(object))).Compile()()",
            TestName = "An expression built from the property keeps the fast delegate writable"
        )]
        public void AnalyzeReportsReflectedFastDelegatePropertyUse(string lookup)
        {
            const string Source =
                @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
static class Callbacks
{
    private static readonly MessageHandler.FastHandler<IUntargetedMessage> hidden =
        (ref IUntargetedMessage value) => { };
    public static MessageHandler.FastHandler<IUntargetedMessage> Exposed => hidden;
}";
            string external = @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
class Other
{
    void Forward(global::System.Type owner, IUntargetedMessage message)
    {
        var callback = (MessageHandler.FastHandler<IUntargetedMessage>)LOOKUP;
        callback(ref message);
    }
}".Replace("LOOKUP", lookup);
            const string ExternalPath = "Assets/Consumer.cs";
            const string Path = "Assets/Callbacks.cs";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript(Path, Source);
            project.AddScript(ExternalPath, external);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                name => project.FindOtherUse(Path, name),
                project
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("possible indirect use").And.Contain(ExternalPath + ", line 8")
            );
        }

        [Test]
        public void AnalyzeDefersAnAliasWhenTheProjectObservesItsOwner()
        {
            const string Source =
                @"
using DxMessaging.Core;
using DxMessaging.Core.Messages;
class Callbacks
{
    private readonly MessageHandler.FastHandler<IUntargetedMessage> hidden =
        (ref IUntargetedMessage value) => { };
    public MessageHandler.FastHandler<IUntargetedMessage> Exposed => hidden;
}";
            const string External =
                @"
class Other
{
    object ReadOwner() => new Callbacks();
}";
            const string ExternalPath = "Assets/Consumer.cs";
            const string Path = "Assets/Callbacks.cs";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses project = new();
            project.AddScript(Path, Source);
            project.AddScript(ExternalPath, External);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                name => project.FindOtherUse(Path, name),
                project
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain(ExternalPath + ", line 4"));
        }

        [Test]
        public void AnalyzeChecksLocalShadowInLiteralConditionalBaseForwarding()
        {
            const string Source =
                @"
sealed class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref global::DxMessaging.Core.Messages.GlobalStringMessage value)
    {
#if true
        void Relay(global::DxMessaging.Core.Messages.GlobalStringMessage value)
        {
            base.HandleGlobalStringMessage(ref value);
        }
        Relay(default);
#endif
    }
}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("base forwarding argument"));
        }

        [TestCase(
            "class",
            "public",
            false,
            TestName = "A project use can keep a public callback writable"
        )]
        [TestCase(
            "class",
            "internal",
            false,
            TestName = "A project use can keep an internal callback writable"
        )]
        [TestCase(
            "class",
            "protected",
            false,
            TestName = "A project use can keep an inherited callback writable"
        )]
        [TestCase(
            "partial class",
            "private",
            false,
            TestName = "Another part can use a private callback in a partial type"
        )]
        [TestCase(
            "class",
            "private",
            true,
            TestName = "A private callback in a complete type remains eligible across scripts"
        )]
        [TestCase(
            "sealed class",
            "protected",
            true,
            TestName = "A protected callback in a sealed type remains eligible across scripts"
        )]
        public void AnalyzeUsesProjectBindingsForExposedCallbacks(
            string classDeclaration,
            string memberAccess,
            bool migrate
        )
        {
            string source = @"
CLASS Receiver
{
    void Configure()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> callback = Forward;
    }
    ACCESS void Forward(ref Pulse value) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("CLASS", classDeclaration).Replace("ACCESS", memberAccess);

            const string Path = "Assets/Receiver.cs";
            const string OtherPath = "Assets/Other.cs";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses uses = new();
            uses.AddScript(Path, source);
            uses.AddScript(OtherPath, "class Caller { void Run() { Forward(ref value); } }");

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source,
                name => uses.FindOtherUse(Path, name)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            Assert.That(result.ManualReviewMethods, Has.All.Contains($"{OtherPath}, line 1"));
        }

        [Test]
        public void AnalyzeFindsExternalWritableBaseForwarding()
        {
            const string Source =
                @"
class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    protected override void HandleGlobalStringMessage(ref global::DxMessaging.Core.Messages.GlobalStringMessage message) { }
}";
            const string Caller =
                @"
class Child : Receiver
{
    void Send(ref global::DxMessaging.Core.Messages.GlobalStringMessage message)
    {
        base.HandleGlobalStringMessage(ref message);
    }
}";
            const string Path = "Assets/Receiver.cs";
            const string ChildPath = "Assets/Child.cs";
            ReadonlyFastHandlerUpgrade.ProjectCallbackUses uses = new();
            uses.AddScript(ChildPath, Caller);
            uses.AddScript(Path, Source);

            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source,
                name => uses.FindOtherUse(Path, name)
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain($"{ChildPath}, line 6"));
        }

        [TestCase(
            "global::Vendor.MessageRegistrationToken<int>",
            false,
            TestName = "A generic vendor token with the same name defers a shared registration"
        )]
        [TestCase(
            "global::DxMessaging.Core.MessageRegistrationToken",
            true,
            TestName = "The same package token in both configurations permits a shared registration"
        )]
        public void AnalyzeChecksEachTokenTypeInConditionalDeclarations(
            string alternateType,
            bool migrate
        )
        {
            string source = @"
class Receiver
{
#if LEGACY
    ALTERNATE token;
#else
    global::DxMessaging.Core.MessageRegistrationToken token;
#endif
    void SetUp()
    {
        token.RegisterUntargeted<Pulse>((ref Pulse value) => Observe(value));
    }

    static void Observe(Pulse value) { }
}".Replace("ALTERNATE", alternateType);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("receiver whose MessageRegistrationToken type cannot be proven")
            );
        }

        [TestCase(
            "&message",
            "Pulse",
            TestName = "A fixed pointer to callback storage requires review"
        )]
        [TestCase(
            "&(message)",
            "Pulse",
            TestName = "A grouped fixed pointer to callback storage requires review"
        )]
        [TestCase(
            "&message.Value",
            "int",
            TestName = "A fixed pointer to a callback field requires review"
        )]
        public void AnalyzeReportsFixedPointerToCallbackStorage(string operand, string pointerType)
        {
            string source = @"
struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage
{
    public int Value;
}

class Receiver
{
    unsafe void SetUp()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> callback = (ref Pulse message) =>
        {
            fixed (TYPE* pointer = OPERAND)
            {
                *pointer = default;
            }
        };
    }
}".Replace("OPERAND", operand).Replace("TYPE", pointerType);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("takes the address of parameter 'message'")
            );
        }

        [TestCase(
            "Receiver",
            "other.Forward(ref copy)",
            TestName = "A writable call on another instance of the declaring type requires review"
        )]
        [TestCase(
            "var",
            "other.Forward(ref copy)",
            TestName = "An inferred receiver does not prove a writable call is independent"
        )]
        [TestCase(
            "Receiver",
            "Observe($\"{Run(() => other.Forward(ref copy))}\")",
            TestName = "An interpolated call through the declaring type requires review"
        )]
        public void AnalyzeDefersWritableCallsThatMayBindToTheNamedCallback(
            string type,
            string call
        )
        {
            string source = @"
class Receiver
{
    void SetUp()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> known = Forward;
        TYPE other = this;
        Pulse copy = default;
        CALL;
    }
    void Forward(ref Pulse message) => Read(message);
    void Read(Pulse message) { }
}".Replace("TYPE", type).Replace("CALL", call);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("direct call"));
        }

        // cspell:ignore essage
        [TestCase(
            "using @MessageRegistrationToken = global::Vendor.CustomToken;",
            "",
            false,
            TestName = "A verbatim token alias blocks package receiver proof"
        )]
        [TestCase(
            @"using \u004DessageRegistrationToken = global::Vendor.CustomToken;",
            "",
            false,
            TestName = "A Unicode escaped token alias blocks package receiver proof"
        )]
        [TestCase(
            "using @MessageRegistrationToken = global::Vendor.CustomToken;",
            "global::DxMessaging.Core.",
            true,
            TestName = "A globally qualified package token bypasses an escaped simple alias"
        )]
        [TestCase(
            "",
            "",
            true,
            TestName = "An import with no competing alias still proves the token"
        )]
        public void AnalyzeChecksTokenAliasNamesByValue(
            string alias,
            string qualification,
            bool migrate
        )
        {
            string source = @"
using DxMessaging.Core;
namespace Consumer
{
    ALIAS
    class Receiver
    {
        QUALIFICATIONMessageRegistrationToken token;
        void SetUp()
        {
            token.RegisterUntargeted<Pulse>((ref Pulse message) => Read(message));
        }
        static void Read(Pulse value) { }
    }
}".Replace("ALIAS", alias).Replace("QUALIFICATION", qualification);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

        [TestCase(
            "protected new bool RegisterForStringMessages => false;",
            TestName = "A hidden string registration property prevents a duplicate insertion"
        )]
        [TestCase(
            "private Flag @RegisterForStringMessages { get { return false; } }",
            TestName = "A hidden aliased block property prevents a duplicate insertion"
        )]
        [TestCase(
            "#if LEGACY\n    protected new bool RegisterForStringMessages => false;\n#endif",
            TestName = "A conditional hidden property requires opt-in collision review"
        )]
        [TestCase(
            "private new bool RegisterForStringMessages = false;",
            TestName = "A hidden field prevents a duplicate string opt-in insertion"
        )]
        [TestCase(
            "private new System.Collections.Generic.List<int> RegisterForStringMessages;",
            TestName = "A generic field prevents a duplicate string opt-in insertion"
        )]
        [TestCase(
            "private new bool other = true, RegisterForStringMessages = false;",
            TestName = "A following field declarator prevents a duplicate string opt-in insertion"
        )]
        [TestCase(
            "private new bool RegisterForStringMessages() => false;",
            TestName = "A same-name method prevents a duplicate string opt-in insertion"
        )]
        [TestCase(
            "private new unsafe delegate*<void> RegisterForStringMessages;",
            TestName = "A function pointer field prevents a duplicate string opt-in insertion"
        )]
        [TestCase(
            "private new unsafe delegate* unmanaged[Cdecl]<int, void> RegisterForStringMessages;",
            TestName = "An unmanaged function pointer field prevents a duplicate string opt-in insertion"
        )]
        public void AnalyzeReportsHiddenStringPropertyBeforeInsertingOptIn(string property)
        {
            string source =
                "using Flag = global::System.Boolean;\n"
                + "class Receiver : global::DxMessaging.Unity.MessageAwareComponent\n{\n    "
                + property
                + "\n    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }\n}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.StringHandlerOptInCount, Is.Zero);
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(source.Replace("ref GlobalStringMessage", "in GlobalStringMessage"))
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("hides the inherited setting"));
        }

        private static IEnumerable<TestCaseData> SupportedCallbackCases()
        {
            yield return new TestCaseData(
                "token.RegisterUntargeted<Pulse>((ref Pulse message) => Observe(message));",
                "token.RegisterUntargeted<Pulse>((in Pulse message) => Observe(message));",
                1
            ).SetName("Untargeted lambda");
            yield return new TestCaseData(
                "token.RegisterBroadcastWithoutSource<Hit>((ref global::DxMessaging.Core.InstanceId source, ref Hit message) => Observe(source, message));",
                "token.RegisterBroadcastWithoutSource<Hit>((in global::DxMessaging.Core.InstanceId source, in Hit message) => Observe(source, message));",
                2
            ).SetName("Context lambda");
            yield return new TestCaseData(
                "token.RegisterTargetedWithoutTargetingPostProcessor<Hit>((ref global::DxMessaging.Core.InstanceId target, ref Hit message) => Observe(target, message));",
                "token.RegisterTargetedWithoutTargetingPostProcessor<Hit>((in global::DxMessaging.Core.InstanceId target, in Hit message) => Observe(target, message));",
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
    private void OnHit(ref global::DxMessaging.Core.InstanceId source, ref Hit message) { }
}";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
                Does.Contain("OnHit(in global::DxMessaging.Core.InstanceId source, in Hit message)")
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
token.RegisterBroadcastWithoutSource<Hit>((ref global::DxMessaging.Core.InstanceId source, ref Hit message) =>
{
    Forward(ref message);
});";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
                "token.RegisterBroadcastWithoutSource<Pulse>((ref global::DxMessaging.Core.InstanceId source, ref Pulse message)"
                    + " => { source = default; Observe(message); });",
                "source"
            ).SetName("Context assignment keeps both parameters unchanged");
            yield return new TestCaseData(
                "token.RegisterBroadcastWithoutSource<Pulse>((ref global::DxMessaging.Core.InstanceId source, ref Pulse message)"
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                Source
            );

            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(Source.Replace("ref Pulse message", "in Pulse message"))
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [TestCase(
            "item #42",
            false,
            TestName = "A midline hash in a conditional string leaves a later callback eligible"
        )]
        [TestCase(
            "    #if INNER\n    #endif",
            true,
            TestName = "Possible directives in a conditional string still require review"
        )]
        [TestCase(
            "   #if INNER\r\n   #endif",
            true,
            TestName = "Possible directives after CRLF still require review"
        )]
        [TestCase(
            "    #region Documentation\n    #endregion",
            false,
            TestName = "A conditional string with region markers still permits a later callback"
        )]
        public void AnalyzeLimitsUncertaintyToPossibleDirectiveLines(string stringBody, bool defer)
        {
            string source =
                "#if UNKNOWN\nstring note = @\"label\n"
                + stringBody
                + "\n\";\n#endif\n"
                + "global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = (ref Pulse message) => Read(message);";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(defer ? 0 : 1));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(defer ? source : source.Replace("ref Pulse", "in Pulse"))
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(defer ? 1 : 0));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("depend on conditional compilation")
            );
        }

        [TestCase("LEGACY")]
        [TestCase("!LEGACY")]
        public void AnalyzeDefersAnEarlierCallbackWhenConditionalCommentsCanHideItsUse(
            string condition
        )
        {
            string source = @"
public readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;

    void Install() { token.RegisterUntargeted<global::Pulse>(Forward); }

    public static void Forward(ref global::Pulse pulse) { }

#if CONDITION
/*
#endif
#if true
    void Invoke() { global::Pulse pulse = default; Forward(ref pulse); }
/*
*/
#endif
}
".Replace("CONDITION", condition);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("depend on conditional compilation at line 13")
            );
        }

        [Test]
        public void AnalyzeDefersAfterAnUnresolvedConditionalOpeningBrace()
        {
            const string Source =
                "#if UNKNOWN\n{\n#endif\n"
                + "DxMessaging.Core.MessageHandler.FastHandler<Pulse> handler = "
                + "(ref Pulse message) => Observe(message);\n"
                + "#if UNKNOWN\n}\n#endif\n";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            "static delegate(ref Pulse message) { Read(message); }",
            1,
            TestName = "A conditionally bound static anonymous fast delegate requires review"
        )]
        [TestCase(
            "FastHandler<Pulse>",
            "Forward",
            1,
            TestName = "A conditionally bound fast method group requires review"
        )]
        [TestCase(
            "FastHandlerWithContext<Pulse>",
            "(ref global::DxMessaging.Core.InstanceId context, ref Pulse message) => Read(message)",
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

        [TestCase(
            "delegate",
            "Forward",
            0,
            TestName = "A relative fast delegate blocks its globally bound shared method group"
        )]
        [TestCase(
            "receiver",
            "Forward",
            0,
            TestName = "An unproven registration blocks its globally bound shared method group"
        )]
        [TestCase(
            "delegate",
            "Other",
            1,
            TestName = "An unproven delegate leaves a separate global method group eligible"
        )]
        [TestCase(
            "receiver",
            "Other",
            1,
            TestName = "An unproven receiver leaves a separate global method group eligible"
        )]
        [TestCase(
            "vendor",
            "Forward",
            0,
            TestName = "A foreign fast delegate blocks a shared method group"
        )]
        [TestCase(
            "vendor",
            "Other",
            1,
            TestName = "A foreign fast delegate does not report a separate method group"
        )]
        [TestCase(
            "none",
            "Forward",
            1,
            TestName = "A global-only method group stays eligible under a relative alias"
        )]
        public void AnalyzeKeepsUnprovenUsesOfSharedMethodGroups(
            string kind,
            string name,
            int edits
        )
        {
            string setup = kind switch
            {
                "delegate" => "DxMessaging.Core.MessageHandler.FastHandler<Pulse> relative = "
                    + name
                    + ";",
                "receiver" => "relativeToken.RegisterUntargeted<Pulse>(" + name + ");",
                "vendor" => "global::Vendor.Core.MessageHandler.FastHandler<Pulse> foreign = "
                    + name
                    + ";",
                _ => "",
            };
            string source = @"
namespace Consumer
{
#if USE_VENDOR
    using DxMessaging = global::Vendor;
#endif
    class Receiver
    {
        DxMessaging.Core.MessageRegistrationToken relativeToken;
        void SetUp()
        {
            global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> known = Forward;
            SETUP
        }
        static void Forward(ref Pulse message) => Read(message);
        static void Other(ref Pulse message) => Read(message);
        static void Read(Pulse message) { }
    }
}".Replace("SETUP", setup);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(edits));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(edits == 0 ? source : source.Replace("Forward(ref", "Forward(in"))
            );
            bool reports = kind is "delegate" or "receiver" || (kind == "vendor" && edits == 0);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(reports ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("cannot be proven"));
        }

        [TestCase(
            "Forward(ref copy);",
            true,
            TestName = "A direct writable call defers a named fast callback"
        )]
        [TestCase(
            "this.Forward(message: ref copy);",
            true,
            TestName = "A this-qualified writable call defers a named fast callback"
        )]
        [TestCase(
            "other.Forward(ref copy);",
            false,
            TestName = "A direct call on another receiver does not defer the named callback"
        )]
        public void AnalyzePreservesDirectWritableCallsToNamedCallbacks(string use, bool defer)
        {
            string source = @"
class Receiver
{
    OtherReceiver other;
    void SetUp()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> known = Forward;
        Pulse copy = default;
        USE
    }
    void Forward(ref Pulse message) => Read(message);
    void Read(Pulse message) { }
}
class OtherReceiver { public void Forward(ref Pulse value) { } }".Replace("USE", use);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(defer ? 0 : 1));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(
                    defer ? source : source.Replace("    void Forward(ref", "    void Forward(in")
                )
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(defer ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("direct call"));
        }

        [TestCase(
            "$\"{Run(() => Forward(ref copy))}\"",
            true,
            TestName = "A writable named call in an interpolation requires review"
        )]
        [TestCase(
            "$@\"{Run(() => this.Forward(message: ref copy))}\"",
            true,
            TestName = "A named writable call in a verbatim interpolation requires review"
        )]
        [TestCase(
            "$\"{Run(() => Forward(ref copy), Choose<int, string>(first, second))}\"",
            true,
            TestName = "Trailing generic arguments leave the writable interpolated call visible"
        )]
        [TestCase(
            "$\"{{Forward(ref copy)}}\"",
            false,
            TestName = "An interpolated literal does not block a named callback"
        )]
        [TestCase(
            "$\"{42:Forward(ref copy)}\"",
            false,
            TestName = "Interpolation format text does not block a named callback"
        )]
        [TestCase(
            "$\"{Run(() => other.Forward(ref copy))}\"",
            false,
            TestName = "An interpolated call on another receiver does not block the named callback"
        )]
        [TestCase(
            "$$\"\"\"{{Run(() => Forward(ref copy))}}\"\"\"",
            true,
            TestName = "A writable named call in a raw interpolation requires review"
        )]
        public void AnalyzeChecksDirectWritableNamedCallsInInterpolations(
            string expression,
            bool defer
        )
        {
            string source = @"
class Receiver
{
    OtherReceiver other;
    void SetUp()
    {
        global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> known = Forward;
        Pulse copy = default;
        Observe(EXPRESSION);
    }
    void Forward(ref Pulse message) => Read(message);
    void Read(Pulse message) { }
}
class OtherReceiver { public void Forward(ref Pulse value) { } }".Replace("EXPRESSION", expression);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(defer ? 0 : 1));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(
                    defer ? source : source.Replace("    void Forward(ref", "    void Forward(in")
                )
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(defer ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("direct call"));
        }

        [TestCase(
            "spare, token",
            false,
            TestName = "An alternate receiver later in a field list requires review"
        )]
        [TestCase(
            "spare = Create<int, string>(), prep, @token",
            false,
            TestName = "An initialized alternate receiver list requires review"
        )]
        [TestCase(
            "spare = new CustomToken { Count = 1 }, token = null",
            false,
            TestName = "An object initializer does not hide an alternate receiver declarator"
        )]
        [TestCase(
            "spare = Choose((first < second), TrueToken, FalseToken), token",
            false,
            TestName = "A comparison initializer does not hide an alternate receiver declarator"
        )]
        [TestCase(
            "spare, token",
            true,
            TestName = "Equivalent package receivers in field lists remain eligible"
        )]
        [TestCase(
            "spare = Create<int, string>(), @token",
            true,
            TestName = "An initialized package receiver list remains eligible"
        )]
        public void AnalyzeIndexesEveryReceiverDeclarator(string declarations, bool migrate)
        {
            const string Package = "global::DxMessaging.Core.MessageRegistrationToken";
            string source = @"
class Receiver
{
#if USE_DX
    BASE_TYPE FIRST_DECLARATORS;
#else
    OTHER_TYPE DECLARATIONS;
#endif
    void SetUp()
    {
        token.RegisterUntargeted<Pulse>((ref Pulse message) => Read(message));
    }
    static void Read(Pulse message) { }
}".Replace("BASE_TYPE", Package).Replace(
                "FIRST_DECLARATORS",
                migrate ? "firstToken, token" : "token, firstToken"
            ).Replace("OTHER_TYPE", migrate ? Package : "global::Vendor.CustomToken").Replace(
                "DECLARATIONS",
                declarations
            );
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

        [TestCase(
            "global::Vendor.CustomToken<(int, int)>",
            "token",
            TestName = "A tuple type argument in an alternate receiver requires review"
        )]
        [TestCase(
            "global::Vendor.CustomToken<Box<(int Left, int Right)>, int[]>",
            "spare, @token",
            TestName = "Nested tuple type arguments and multiple declarators do not hide a receiver"
        )]
        public void AnalyzeFindsAlternateReceiversWithTupleTypeArguments(
            string type,
            string declarators
        )
        {
            string source = @"
class Receiver
{
#if USE_DX
    global::DxMessaging.Core.MessageRegistrationToken token;
#else
    TYPE DECLARATORS;
#endif
    void SetUp()
    {
        token.RegisterUntargeted<Pulse>((ref Pulse message) => Read(message));
    }
    static void Read(Pulse message) { }
}".Replace("TYPE", type).Replace("DECLARATORS", declarators);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.UpgradedSource, Is.EqualTo(source));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("receiver whose MessageRegistrationToken type cannot be proven")
            );
        }

        [TestCase(
            "other.token",
            false,
            TestName = "A matching field name does not prove the receiver on another object"
        )]
        [TestCase(
            "other.@token",
            false,
            TestName = "An escaped field on another object still has an unproven receiver"
        )]
        [TestCase(
            "this.token",
            true,
            TestName = "An explicitly qualified own receiver still migrates"
        )]
        [TestCase("token", true, TestName = "An unqualified own receiver still migrates")]
        public void AnalyzeChecksTheOwnerOfAQualifiedRegistrationReceiver(
            string receiver,
            bool migrate
        )
        {
            string source = @"
class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken token;
    Other other;
    void SetUp()
    {
        RECEIVER.RegisterUntargeted<Pulse>((ref Pulse message) => Read(message));
    }
    static void Read(Pulse message) { }
}
class Other { public global::Vendor.CustomToken token; }".Replace("RECEIVER", receiver);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

        [TestCase(
            "case { } token: token.RegisterUntargeted<Pulse>((ref Pulse message) => { }); break;",
            false
        )]
        [TestCase(
            "case { } vendor: Observation.Observe(token: vendor); token.RegisterUntargeted<Pulse>((ref Pulse message) => { }); break;",
            true
        )]
        public void AnalyzeDistinguishesSwitchPatternVariablesFromNamedArguments(
            string arm,
            bool migrate
        )
        {
            string source = @"
sealed class Receiver
{
    private global::DxMessaging.Core.MessageRegistrationToken token;
    private void Register(VendorToken candidate)
    {
        switch (candidate)
        {
            ARM
        }
    }
}
static class Observation { public static void Observe(VendorToken token) { } }
delegate void Writable<T>(ref T message);
class VendorToken
{
    public void RegisterUntargeted<T>(Writable<T> callback) { }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
".Replace("ARM", arm);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("line 9"));
        }

        [TestCase("global::Registration", false)]
        [TestCase("Vendor::Registration", false)]
        [TestCase("this.Registration", true)]
        [TestCase("Registration", true)]
        public void AnalyzeDistinguishesAliasQualifiedTypesFromItsOwnRegistrationField(
            string receiver,
            bool migrate
        )
        {
            string source = @"
using Vendor = VendorNamespace;

sealed class Receiver
{
    global::DxMessaging.Core.MessageRegistrationToken Registration;
    void SetUp()
    {
        RECEIVER.RegisterUntargeted<Pulse>((ref Pulse message) => { });
    }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
public delegate void Writable<T>(ref T message);
static class Registration { public static void RegisterUntargeted<T>(Writable<T> handler) { } }
namespace VendorNamespace
{
    static class Registration { public static void RegisterUntargeted<T>(Writable<T> handler) { } }
}
".Replace("RECEIVER", receiver);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
            if (!migrate)
            {
                Assert.That(
                    result.ManualReviewMethods[0],
                    Does.Contain("line 9")
                        .And.Contain(
                            "receiver whose MessageRegistrationToken type cannot be proven"
                        )
                );
            }
        }

        [TestCase(
            "Read(message);",
            false,
            TestName = "A static anonymous handler with a read migrates"
        )]
        [TestCase(
            "message = default;",
            true,
            TestName = "A static anonymous handler with a write requires review"
        )]
        [TestCase(
            "Run((int item) => Read(item)); Read(message);",
            false,
            TestName = "A nested lambda leaves the static anonymous handler eligible"
        )]
        public void AnalyzeChecksStaticAnonymousHandlerParameters(string body, bool writable)
        {
            string source =
                "global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> h = static delegate(ref Pulse message) { "
                + body
                + " };";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(writable ? 0 : 1));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(writable ? source : source.Replace("ref Pulse", "in Pulse"))
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(writable ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.All.Contains("writes to parameter"));
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

        [Test]
        public void AnalyzeReportsDistinctLocationsForUnprovenRegistrations()
        {
            const string Call = "RegisterUntargeted<Pulse>((ref Pulse message) => Read(message));";
            string line = "first." + Call + " second." + Call;
            string source = "CustomToken first, second;\n" + line;
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            int firstColumn = line.IndexOf("RegisterUntargeted", StringComparison.Ordinal) + 1;
            int secondColumn = line.LastIndexOf("RegisterUntargeted", StringComparison.Ordinal) + 1;
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(2));
            Assert.That(
                result.ManualReviewMethods,
                Has.Some.Contains($"line 2, column {firstColumn} ")
            );
            Assert.That(
                result.ManualReviewMethods,
                Has.Some.Contains($"line 2, column {secondColumn} ")
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            "protected override Flag RegisterForStringMessages => false;",
            0,
            0,
            TestName = "A string opt-out with a type alias stays in place"
        )]
        [TestCase(
            "protected override Flag RegisterForStringMessages { get { return false; } }",
            0,
            0,
            TestName = "An aliased string property with a block getter stays in place"
        )]
        [TestCase(
            "#if ACTIVE\n    protected override Flag RegisterForStringMessages => false;\n#endif",
            0,
            1,
            TestName = "A conditional string opt-out with a type alias requires review"
        )]
        [TestCase(
            "class Nested { private Flag RegisterForStringMessages; }",
            1,
            0,
            TestName = "An unrelated nested member does not stop opt-in insertion"
        )]
        public void AnalyzePreservesExplicitStringPropertyTypeAliases(
            string property,
            int insertions,
            int reviews
        )
        {
            string source =
                "using Flag = global::System.Boolean;\n"
                + "class Receiver : global::DxMessaging.Unity.MessageAwareComponent\n{\n    "
                + property
                + "\n    protected override void HandleGlobalStringMessage(ref GlobalStringMessage message) { }\n}";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(insertions));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(reviews));
            Assert.That(result.UpgradedSource, Does.Contain(property));
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            "",
            "[global::System.Runtime.InteropServices.In, global::System.Runtime.InteropServices.Out]",
            false
        )]
        [TestCase(
            "",
            @"[global::System.Runtime.InteropServices.In, global::System.Runtime.InteropServices.\u004futAttribute]",
            false
        )]
        [TestCase(
            "using Marker = System.Runtime.InteropServices.OutAttribute;",
            "[global::System.Runtime.InteropServices.In, Marker]",
            false
        )]
        [TestCase("", "[global::System.Runtime.InteropServices.In]", true)]
        [TestCase("", "[Marker]", true)]
        public void AnalyzePreservesParameterAttributesThatCanRequireWritableRef(
            string import,
            string attribute,
            bool migrate
        )
        {
            string source =
                import
                + @"
sealed class Receiver
{
    void Install(global::DxMessaging.Core.MessageRegistrationToken token) =>
        token.RegisterUntargeted<Pulse>(OnPulse);

    private void OnPulse(ATTRIBUTE ref Pulse pulse) { }
}

readonly struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }
sealed class MarkerAttribute : System.Attribute { }
".Replace("ATTRIBUTE", attribute);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(migrate ? 1 : 0));
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(migrate ? source.Replace("ref ", "in ") : source)
            );
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(migrate ? 0 : 1));
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
                + " ref global::DxMessaging.Core.InstanceId source, ref Pulse message) { }";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
    (ref global::DxMessaging.Core.InstanceId source, ref Hit message) => Observe(source, message.Next)
);
token.RegisterUntargeted<Pulse>((ref Pulse other) => Observe(other));";
            string expected = Source.Replace("ref Pulse other", "in Pulse other");

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
token.RegisterBroadcastWithoutSource<Hit>((ref global::DxMessaging.Core.InstanceId source, ref Hit message) =>
{
    Observe(source.Value);
    message.Value++;
});";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            "Token.RegisterUntargeted<GlobalStringMessage>(handler); Other.Forward(ref copy);",
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
            if (shouldMigrate)
            {
                source =
                    "using DxMessaging.Core.Messages;\n"
                    + source
                    + "\nclass Other { public static void Forward(ref GlobalStringMessage value) { } }\n";
            }
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
                + "        FAST_HANDLER first = Forward; Token.RegisterUntargeted<PAYLOAD>(first);",
            3,
            TestName = "A global override binds a renamed local ref parameter for base forwarding"
        )]
        [TestCase(
            "HandleStringComponentMessage",
            "StringMessage",
            "PAYLOAD copy = outer;\n        if (true) {\n"
                + "            void Forward(ref global::DxMessaging.Core.InstanceId source, ref PAYLOAD forwarded) => base.HANDLER(ref forwarded);\n"
                + "            DxMessaging.Core.MessageHandler.FastHandlerWithContext<PAYLOAD> handler = Forward;\n"
                + "            Token.RegisterTargetedWithoutTargeting<PAYLOAD>(handler);\n        }",
            4,
            TestName = "A component override binds a renamed second local parameter for base forwarding"
        )]
        [TestCase(
            "HandleStringGameObjectMessage",
            "StringMessage",
            "void Forward(ref PAYLOAD first) => base.HANDLER(ref first);\n"
                + "        void Relay(ref PAYLOAD second) { base.HANDLER(ref second); }\n"
                + "        FAST_HANDLER firstHandler = Forward; FAST_HANDLER secondHandler = Relay;\n"
                + "        Token.RegisterTargeted<PAYLOAD>(default, firstHandler);\n"
                + "        Token.RegisterTargeted<PAYLOAD>(default, secondHandler);",
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
            string source =
                "using DxMessaging.Core.Messages;\n"
                + CreateRenamedForwardingSource(method, payload, body);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(expectedReplacements));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            Assert.That(result.UpgradedSource, Does.Contain($"{method}(in {payload} outer)"));
            Assert.That(result.UpgradedSource, Does.Not.Contain("ref "));
            if (source.Contains("ref global::DxMessaging.Core.InstanceId source"))
            {
                Assert.That(
                    result.UpgradedSource,
                    Does.Contain("Forward(in global::DxMessaging.Core.InstanceId source,")
                );
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
    MEMBER
    protected override void HANDLER(ref PAYLOAD outer)
    {
        BODY
    }
}
OTHER_TYPE";
            bool usesIndependentReceiver = body.Contains("@base");
            return Template
                .Replace("MEMBER", usesIndependentReceiver ? "OtherReceiver @base;" : "")
                .Replace(
                    "OTHER_TYPE",
                    usesIndependentReceiver
                        ? "class OtherReceiver { public void HANDLER(ref PAYLOAD message) { } }"
                        : ""
                )
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
        public void AnalyzeReportsInterpolatedBaseCallsOutsideTheOverrideScope()
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                Source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(2));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("base.HandleGlobalStringMessage")
            );
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

        [Test]
        public void AnalyzeReportsABaseCallInAComponentWithoutAnOverride()
        {
            const string Source =
                @"
sealed class Receiver : global::DxMessaging.Unity.MessageAwareComponent
{
    void Forward(global::DxMessaging.Core.Messages.GlobalStringMessage message) =>
        base.HandleGlobalStringMessage(ref message);
}
";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(
                result.ManualReviewMethods[0],
                Does.Contain("line 5").And.Contain("base.HandleGlobalStringMessage")
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );

            Assert.That(result.ReplacementCount, Is.EqualTo(2));
            Assert.That(result.StringHandlerOptInCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
            int opening = source.IndexOf('{');
            string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string expected = source
                .Insert(
                    opening + 1,
                    newline
                        + "    protected override bool RegisterForStringMessages => true;"
                        + newline
                )
                .Replace(
                    "HandleGlobalStringMessage(ref GlobalStringMessage outer)",
                    "HandleGlobalStringMessage(in GlobalStringMessage outer)"
                )
                .Replace(
                    "base.HandleGlobalStringMessage(ref outer);",
                    "base.HandleGlobalStringMessage(in outer);"
                )
                .Replace(
                    "base.@HandleGlobalStringMessage(ref outer);",
                    "base.@HandleGlobalStringMessage(in outer);"
                );
            Assert.That(result.UpgradedSource, Is.EqualTo(expected));
            Assert.That(expected, Does.Contain(expectedCall));
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string expected = source
                .Insert(
                    source.IndexOf('{') + 1,
                    newline
                        + "    protected override bool RegisterForStringMessages => true;"
                        + newline
                )
                .Replace(
                    "HandleStringGameObjectMessage(ref StringMessage outer)",
                    "HandleStringGameObjectMessage(in StringMessage outer)"
                )
                .Replace(
                    "base.HandleStringGameObjectMessage(ref outer)",
                    "base.HandleStringGameObjectMessage(in outer)"
                )
                .Replace(
                    "base.HandleStringComponentMessage(ref outer)",
                    "base.HandleStringComponentMessage(in outer)"
                );
            Assert.That(result.UpgradedSource, Is.EqualTo(expected));
        }

        [Test]
        public void AnalyzeLeavesExpressionBodyWithGenericRefForwardingForManualReview()
        {
            const string Source =
                @"
DxMessaging.Core.MessageRegistrationToken token;
token.RegisterUntargeted<Pulse>(OnPulse);
void OnPulse(ref Pulse message) => Forward<First, Second>(ref message);";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

        [TestCase("count < limit ? first : second", "0")]
        [TestCase("count <= limit ? first : second", "0")]
        [TestCase("Choose(count < limit, first, second)", "0")]
        [TestCase("count < limit ? first : second", "count > limit ? 1 : 0")]
        [TestCase("Choose<int, string>(first)", "0")]
        [TestCase("Choose<(int left, string right), Box<int, string>>(first)", "0")]
        [TestCase("count >= limit ? first : Choose<int[,], Box<int, string>>(second)", "0")]
        [TestCase("count < limit ? Choose<int, string>(first) : second", "0")]
        [TestCase("subject is Box<int, string> && ready ? first : second", "0")]
        public void AnalyzeFindsTargetedCallbackAfterRelationalOperator(
            string target,
            string priority
        )
        {
            string source = @"
global::DxMessaging.Core.MessageRegistrationToken token;
global::DxMessaging.Core.InstanceId first;
global::DxMessaging.Core.InstanceId second;
token.RegisterTargeted<Pulse>(
    TARGET,
    (ref Pulse message) => Observe(message),
    priority: PRIORITY
);".Replace("TARGET", target).Replace("PRIORITY", priority);
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            bool supported = !target.Contains("Choose");
            Assert.That(
                result.UpgradedSource,
                Is.EqualTo(supported ? source.Replace("ref Pulse", "in Pulse") : source)
            );
            Assert.That(result.ReplacementCount, Is.EqualTo(supported ? 1 : 0));
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(supported ? 0 : 1));
            Assert.That(
                result.ManualReviewMethods,
                Has.All.Contains("overload whose package binding cannot be proven")
            );
        }

        [Test]
        public void AnalyzeConvertsTargetedCallbackAfterGenericCondition()
        {
            const string Source =
                @"
using DxMessaging.Core;
class Receiver
{
    static bool Choose<T, U>(InstanceId target) => true;
    static void Observe(Pulse value) { }
    void Register(MessageRegistrationToken token, InstanceId first, InstanceId second)
    {
        token.RegisterTargeted<Pulse>(
            Choose<int, string>(first) ? first : second,
            (ref Pulse message) => Observe(message),
            priority: 0);
    }
}
readonly struct Pulse : global::DxMessaging.Core.Messages.ITargetedMessage { }";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = ReadonlyFastHandlerUpgrade.Analyze(
                Source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source.Replace("ref Pulse", "in Pulse")));
            Assert.That(result.ReplacementCount, Is.EqualTo(1));
            Assert.That(result.ManualReviewMethods, Is.Empty);
        }

        [Test]
        public void AnalyzeFindsWritableMethodGroupAfterRelationalOperator()
        {
            const string Source =
                @"
global::DxMessaging.Core.MessageRegistrationToken token;
global::DxMessaging.Core.InstanceId first;
global::DxMessaging.Core.InstanceId second;
token.RegisterTargeted<Pulse>(count < limit ? first : second, Forward);
void Forward(ref Pulse message) => message = default;";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                Source
            );
            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(1));
            Assert.That(result.ManualReviewMethods[0], Does.Contain("writes to parameter"));
        }

        [TestCase("Observe(from item in message select item + 1)", true)]
        [TestCase("Observe(from int item in (message) select item + 1)", true)]
        [TestCase(
            "Observe(from int item in message! select item + 1)",
            true,
            TestName = "A null-forgiven typed query source still requires review"
        )]
        [TestCase(
            "Observe(from item in outer join int other in message! on item equals other select item)",
            true,
            TestName = "A null-forgiven typed join source still requires review"
        )]
        [TestCase(
            "Observe(from item in outer join int other in (message) on item equals other select item)",
            true
        )]
        [TestCase(
            "Observe(from item in outer join other in message on item equals other select item)",
            false
        )]
        [TestCase("Pulse copy = message; Observe(from item in copy select item + 1)", false)]
        [TestCase(
            "Pulse copy = message; Observe(from int item in copy! select item + 1)",
            false,
            TestName = "A null-forgiven independent query copy still migrates"
        )]
        [TestCase("Observe(from item in Items(message) select item + 1)", false)]
        public void AnalyzeChecksQuerySourceReceiver(string statement, bool requiresReview)
        {
            string source =
                "global::DxMessaging.Core.MessageHandler.FastHandler<Pulse> callback = "
                + "(ref Pulse message) => { "
                + statement
                + "; };";
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                source
            );
            AssertComparisonBoundary(result, source, "OnPulse(ref Pulse message)", requiresReview);
        }

        [Test]
        public void AnalyzeKeepsGenericCommaAfterComparisonInsideExpressionBody()
        {
            const string Source =
                @"
using DxMessaging.Core;
class Receiver
{
    private bool sink;

    void Install(MessageRegistrationToken token)
    {
        token.RegisterUntargeted<Pulse>(OnPulse);
    }

    void OnPulse(ref Pulse message) =>
        sink = message.Value < 10 ? Forward<First, Second>(ref message) : false;

    static bool Forward<TFirst, TSecond>(ref Pulse message) => false;
    class First { }
    class Second { }
}

struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage
{
    public int Value;
    public System.Type MessageType => typeof(Pulse);
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
                Source
            );

            Assert.That(result.UpgradedSource, Is.EqualTo(Source));
            Assert.That(result.ReplacementCount, Is.Zero);
            Assert.That(result.ManualReviewMethods, Has.Count.EqualTo(5));
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
    (global::DxMessaging.Core.InstanceId)(FindTarget((ref Probe probe) => probe.Id)),
    OnHit
);
private void OnHit(ref Hit message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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
void OnUntargeted(ref global::DxMessaging.Core.Messages.IUntargetedMessage message) { }
void OnTargeted(ref global::DxMessaging.Core.InstanceId target, ref global::DxMessaging.Core.Messages.ITargetedMessage message) { }
void OnBroadcast(ref global::DxMessaging.Core.InstanceId source, ref global::DxMessaging.Core.Messages.IBroadcastMessage message) { }";

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult result = AnalyzeWithFixtureMessageTypes(
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

            ReadonlyFastHandlerUpgrade.UpgradeResult first = AnalyzeWithFixtureMessageTypes(Source);
            ReadonlyFastHandlerUpgrade.UpgradeResult second = AnalyzeWithFixtureMessageTypes(
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
                + "void OnPulse(ref Pulse message) { }\r\n"
                + "struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }\r\n";
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
                + "void OnPulse(ref Pulse message) { }\n"
                + "struct Pulse : global::DxMessaging.Core.Messages.IUntargetedMessage { }\n";
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
        [TestCase("Assets/Receiver.g.i.cs", "class Receiver { }")]
        [TestCase("Assets/TemporaryGeneratedFile_Early.cs", "class Receiver { }")]
        [TestCase("Assets/Receiver.cs", "// <auto-generated by Tool>\nclass Receiver { }")]
        [TestCase("Assets/Receiver.cs", "/* <autogenerated by Tool> */\nclass Receiver { }")]
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

        [TestCase("\n")]
        [TestCase("\r\n")]
        [TestCase("\r")]
        [TestCase("\u0085")]
        [TestCase("\u2028")]
        [TestCase("\u2029")]
        public void IsGeneratedSourceFindsTheMarkerAfterABanner(string newline)
        {
            string source = string.Join(
                newline,
                "// Generator banner",
                "// <auto-generated />",
                "class Receiver { }"
            );

            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource("Assets/Receiver.cs", source),
                Is.True
            );
        }

        [TestCase("\n")]
        [TestCase("\r\n")]
        [TestCase("\r")]
        [TestCase("\u0085")]
        [TestCase("\u2028")]
        [TestCase("\u2029")]
        public void IsGeneratedSourceFindsTheMarkerInsideALeadingBlockComment(string newline)
        {
            string source = string.Join(
                newline,
                "/* Generator banner",
                " * <auto-generated />",
                " */",
                "class Receiver { }"
            );

            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource("Assets/Receiver.cs", source),
                Is.True
            );
        }

        [TestCase("\n")]
        [TestCase("\r\n")]
        [TestCase("\r")]
        [TestCase("\u0085")]
        [TestCase("\u2028")]
        [TestCase("\u2029")]
        public void IsGeneratedSourceScansCommentsAfterLeadingDirectives(string newline)
        {
            string source = string.Join(
                newline,
                "#nullable enable",
                "// <auto-generated />",
                "class Receiver { }"
            );
            string consumer = string.Join(
                newline,
                "#nullable enable",
                "class Receiver { }",
                "// <auto-generated />"
            );

            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource("Assets/Receiver.cs", source),
                Is.True
            );
            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource("Assets/Receiver.cs", consumer),
                Is.False
            );
        }

        [TestCase("false", true)]
        [TestCase("(false)", true)]
        [TestCase("!true", true)]
        [TestCase("false && LEGACY", true)]
        [TestCase("false == true", true)]
        [TestCase("true", false)]
        [TestCase("true || LEGACY", false)]
        [TestCase("LEGACY", true)]
        public void IsGeneratedSourceSkipsOnlyProvablyInactiveHeaderCode(
            string condition,
            bool generated
        )
        {
            string source =
                $"#if {condition}\nclass Placeholder {{ }}\n#endif\n// <auto-generated />\nclass Receiver {{ }}";
            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource("Assets/Receiver.cs", source),
                Is.EqualTo(generated)
            );
        }

        [TestCase("LEGACY", false)]
        [TestCase("!LEGACY", true)]
        public void IsGeneratedSourceCorrelatesSeparateConditionalHeaderGroups(
            string markerCondition,
            bool generated
        )
        {
            string source =
                "#if LEGACY\nusing System;\n#endif\n#if "
                + markerCondition
                + "\n// <auto-generated />\n#endif\nclass Receiver { }";
            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource("Assets/Receiver.cs", source),
                Is.EqualTo(generated)
            );
        }

        [TestCase("", "LEGACY && BUILD", "BUILD && LEGACY", false)]
        [TestCase("", "LEGACY || BUILD", "!LEGACY && !BUILD", true)]
        [TestCase("#define LEGACY\n", "LEGACY", "!LEGACY", false)]
        [TestCase("#undef LEGACY\n", "LEGACY", "!LEGACY", true)]
        public void IsGeneratedSourceEvaluatesCompoundConditionsAndLocalSymbols(
            string prelude,
            string codeCondition,
            string markerCondition,
            bool generated
        )
        {
            string source =
                prelude
                + $"#if {codeCondition}\nusing System;\n#endif\n#if {markerCondition}\n"
                + "// <auto-generated />\n#endif\nclass Receiver { }";

            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource("Assets/Receiver.cs", source),
                Is.EqualTo(generated)
            );
        }

        [TestCase("// <autogenerated />\nclass Receiver { }", true)]
        [TestCase("class Receiver { }\n// <autogenerated />", false)]
        [TestCase(
            "#if LEGACY\nusing System;\n#else\n// <auto-generated />\n#endif\nclass Receiver { }",
            true
        )]
        [TestCase(
            "#if LEGACY\n// <auto-generated />\n#else\nusing System;\n#endif\nclass Receiver { }",
            true
        )]
        [TestCase(
            "#if false\n// <auto-generated />\n#else\nusing System;\n#endif\nclass Receiver { }",
            false
        )]
        [TestCase(
            "#if LEGACY\nusing System;\n// <auto-generated />\n#else\nusing System;\n#endif\nclass Receiver { }",
            false
        )]
        [TestCase(
            "#if LEGACY\nusing System;\n#else\n#if BUILD\n// <autogenerated />\n#endif\n#endif\nclass Receiver { }",
            true
        )]
        [TestCase(
            "#if LEGACY\n/*\n#endif\n// <auto-generated />\n#if LEGACY\n*/\n#endif\nclass Receiver { }",
            true
        )]
        [TestCase("/*\n#endif\n// <auto-generated />\n*/\nclass Receiver { }", false)]
        [TestCase(
            "#if true\n/*\n#endif\n// <auto-generated />\n*/\n#endif\nclass Receiver { }",
            false
        )]
        [TestCase(
            "#if LEGACY\n/*\n#endif\nusing System;\n// <auto-generated />\n#if LEGACY\n*/\n#endif\nclass Receiver { }",
            false
        )]
        public void IsGeneratedSourceChecksTheHeaderOfEachFeasibleArm(string source, bool generated)
        {
            Assert.That(
                ReadonlyFastHandlerUpgrade.IsGeneratedSource("Assets/Receiver.cs", source),
                Is.EqualTo(generated)
            );
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
            Assert.That(store.Backups, Has.Count.EqualTo(1));
            Assert.That(store.Backups["first.cs.backup"], Is.EqualTo(new byte[] { 1 }));
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
            Assert.That(store.Backups["first.cs.backup"], Is.EqualTo(new byte[] { 1 }));
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
        public void ApplyAtomicallyRollsBackWhenADependencyChangesBeforeCommit()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]>
                {
                    ["receiver.cs"] = new byte[] { 1 },
                    ["caller.cs"] = new byte[] { 2 },
                }
            );
            ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
            {
                new("receiver.cs", new byte[] { 1 }, new byte[] { 10 }),
            };

            Assert.That(() => ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store, () =>
                    {
                        Assert.That(store.Files["receiver.cs"], Is.EqualTo(new byte[] { 10 }));
                        Assert.That(store.Backups, Has.Count.EqualTo(1));
                        store.Files["caller.cs"] = new byte[] { 3 };
                        throw new IOException("caller.cs changed after the preview");
                    }), Throws.TypeOf<IOException>());

            Assert.That(store.Files["receiver.cs"], Is.EqualTo(new byte[] { 1 }));
            Assert.That(store.Files["caller.cs"], Is.EqualTo(new byte[] { 3 }));
            Assert.That(store.Backups, Has.Count.EqualTo(1));
            Assert.That(store.Backups["receiver.cs.backup"], Is.EqualTo(new byte[] { 1 }));
        }

        [Test]
        public void ApplyAtomicallyKeepsTheBackupForAnEditDuringFinalVerification()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]>
                {
                    ["First.cs"] = new byte[] { 1 },
                    ["Second.cs"] = new byte[] { 2 },
                }
            );
            var upgrades = new[]
            {
                new ReadonlyFastHandlerUpgrade.PendingFileUpgrade(
                    "First.cs",
                    new byte[] { 1 },
                    new byte[] { 11 }
                ),
                new ReadonlyFastHandlerUpgrade.PendingFileUpgrade(
                    "Second.cs",
                    new byte[] { 2 },
                    new byte[] { 22 }
                ),
            };

            Assert.That(
                () =>
                    ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store, () =>
                        store.Files["Second.cs"] = new byte[] { 99 }
                    ),
                Throws.InstanceOf<AggregateException>()
            );
            Assert.That(store.Files["First.cs"], Is.EqualTo(new byte[] { 1 }));
            Assert.That(store.Files["Second.cs"], Is.EqualTo(new byte[] { 99 }));
            Assert.That(store.Backups, Has.Count.EqualTo(2));
            Assert.That(store.Backups["First.cs.backup"], Is.EqualTo(new byte[] { 1 }));
            Assert.That(store.Backups["Second.cs.backup"], Is.EqualTo(new byte[] { 2 }));
        }

        [Test]
        public void ApplyAtomicallyRechecksEachFileDuringBackupCleanup()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]>
                {
                    ["First.cs"] = new byte[] { 1 },
                    ["Second.cs"] = new byte[] { 2 },
                }
            );
            var upgrades = new[]
            {
                new ReadonlyFastHandlerUpgrade.PendingFileUpgrade(
                    "First.cs",
                    new byte[] { 1 },
                    new byte[] { 11 }
                ),
                new ReadonlyFastHandlerUpgrade.PendingFileUpgrade(
                    "Second.cs",
                    new byte[] { 2 },
                    new byte[] { 22 }
                ),
            };
            store.AfterDiscard = () => store.Files["Second.cs"] = new byte[] { 99 };

            Assert.That(ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store), Is.False);
            Assert.That(store.Files["First.cs"], Is.EqualTo(new byte[] { 11 }));
            Assert.That(store.Files["Second.cs"], Is.EqualTo(new byte[] { 99 }));
            Assert.That(store.Backups["Second.cs.backup"], Is.EqualTo(new byte[] { 2 }));
            Assert.That(store.Backups.Count, Is.EqualTo(1));
        }

        [Test]
        public void ApplyAtomicallyPreservesTheBackupWhenTheCleanupWindowChanges()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]> { ["receiver.cs"] = new byte[] { 1 } }
            );
            ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
            {
                new("receiver.cs", new byte[] { 1 }, new byte[] { 10 }),
            };
            store.BeforeDiscard = () => store.Files["receiver.cs"][0] = 99;

            Assert.That(ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store), Is.False);
            Assert.That(store.Files["receiver.cs"], Is.EqualTo(new byte[] { 99 }));
            Assert.That(store.Backups["receiver.cs.backup"], Is.EqualTo(new byte[] { 1 }));
        }

        [Test]
        public void ApplyAtomicallyRechecksNonDestinationsAfterPackageVerification()
        {
            FakeAtomicFileStore store = new(
                new Dictionary<string, byte[]>
                {
                    ["receiver.cs"] = new byte[] { 1 },
                    ["Caller.cs"] = new byte[] { 2 },
                }
            );
            ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
            {
                new("receiver.cs", new byte[] { 1 }, new byte[] { 11 }),
            };

            Assert.That(
                () =>
                    ReadonlyFastHandlerUpgrade.ApplyAtomically(
                        upgrades,
                        store,
                        verifyBeforeCommit: () => store.Files["Caller.cs"] = new byte[] { 99 },
                        verifyBeforeCleanup: () =>
                        {
                            if (store.Files["Caller.cs"][0] != 2)
                            {
                                throw new IOException("Caller changed after package verification.");
                            }
                        }
                    ),
                Throws.TypeOf<IOException>()
            );
            Assert.That(store.Files["receiver.cs"], Is.EqualTo(new byte[] { 1 }));
            Assert.That(store.Files["Caller.cs"], Is.EqualTo(new byte[] { 99 }));
            Assert.That(store.Backups, Has.Count.EqualTo(1));
            Assert.That(store.Backups["receiver.cs.backup"], Is.EqualTo(new byte[] { 1 }));
        }

        [Test]
        public void PhysicalBackupCleanupReportsAConcurrentSaveAfterTheBatchCheck()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "Handler.cs");
                byte[] original = { 1 };
                byte[] upgraded = { 2 };
                byte[] newer = { 3 };
                File.WriteAllBytes(path, original);
                ReadonlyFastHandlerUpgrade.PhysicalAtomicFileStore store = new();
                string backup = store.Replace(path, original, upgraded);
                Assert.That(store.MatchesDestination(path, upgraded), Is.True);
                File.WriteAllBytes(path, newer);

                var failure = Assert.Throws<IOException>(() =>
                    store.TryDiscardBackup(path, backup, upgraded)
                );
                Assert.That(
                    failure!.Message,
                    Does.Contain(path).And.Contain("changed during final upgrade verification")
                );
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(newer));
                Assert.That(File.ReadAllBytes(backup), Is.EqualTo(original));
                Assert.That(
                    Directory.GetFiles(directory, "*.dxmsg-rollback-original-*"),
                    Has.Length.EqualTo(1)
                );
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void PhysicalUpgradeRetainsTheOriginalUntilManualReview()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "Handler.cs");
                byte[] before = { 1 };
                byte[] after = { 2 };
                File.WriteAllBytes(path, before);
                ReadonlyFastHandlerUpgrade.PhysicalAtomicFileStore store = new();
                var upgrades = new[]
                {
                    new ReadonlyFastHandlerUpgrade.PendingFileUpgrade(path, before, after),
                };

                Assert.That(ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store), Is.False);
                string backup = Directory.GetFiles(directory, "*.dxmsg-backup-*").Single();
                Assert.That(File.ReadAllBytes(backup), Is.EqualTo(before));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(after));
                Assert.That(Directory.GetFiles(directory, "*.dxmsg-rollback-original-*"), Is.Empty);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PhysicalReplacementRetainsThePreviewedOriginalWhenASaveRaces(bool secondSave)
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "Handler.cs");
                byte[] before = { 1 };
                byte[] after = { 2 };
                byte[] firstEdit = { 3 };
                byte[] secondEdit = { 4 };
                File.WriteAllBytes(path, before);
                ReadonlyFastHandlerUpgrade.PhysicalAtomicFileStore store = new(
                    replaceFile: (source, destination, displaced) =>
                    {
                        File.WriteAllBytes(destination, firstEdit);
                        File.Replace(source, destination, displaced);
                        if (secondSave)
                        {
                            File.WriteAllBytes(destination, secondEdit);
                        }
                    }
                );

                Assert.That(() => store.Replace(path, before, after), Throws.TypeOf<IOException>());
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(firstEdit));
                string staged = Directory
                    .GetFiles(directory, "*.dxmsg-rollback-original-*")
                    .Single();
                Assert.That(File.ReadAllBytes(staged), Is.EqualTo(before));
                string[] recovery = Directory.GetFiles(directory, "*.dxmsg-recovery-*");
                Assert.That(recovery, Has.Length.EqualTo(secondSave ? 1 : 0));
                if (secondSave)
                {
                    Assert.That(File.ReadAllBytes(recovery[0]), Is.EqualTo(secondEdit));
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void PhysicalUpgradeRecoversWhenAReplacementBackupDisappears(
            bool saveBeforeRecovery,
            bool saveDuringRecovery
        )
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string first = Path.Combine(directory, "First.cs");
                string second = Path.Combine(directory, "Second.cs");
                byte[] firstOriginal = { 1 };
                byte[] secondOriginal = { 2 };
                byte[] concurrentEdit = { 3 };
                File.WriteAllBytes(first, firstOriginal);
                File.WriteAllBytes(second, secondOriginal);
                ReadonlyFastHandlerUpgrade.PhysicalAtomicFileStore store = new(
                    replaceFile: (source, destination, displaced) =>
                    {
                        bool isSecondUpgrade =
                            destination == second
                            && source.Contains(".dxmsg-new-", StringComparison.Ordinal);
                        if (
                            destination == second
                            && saveDuringRecovery
                            && source.Contains(".dxmsg-abort-", StringComparison.Ordinal)
                        )
                        {
                            File.WriteAllBytes(destination, concurrentEdit);
                        }
                        File.Replace(source, destination, displaced);
                        if (isSecondUpgrade)
                        {
                            File.Delete(displaced);
                            if (saveBeforeRecovery)
                            {
                                File.WriteAllBytes(destination, concurrentEdit);
                            }
                        }
                    }
                );
                ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
                {
                    new(first, firstOriginal, new byte[] { 11 }),
                    new(second, secondOriginal, new byte[] { 22 }),
                };

                Assert.That(
                    () => ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store),
                    Throws.Exception
                );
                Assert.That(File.ReadAllBytes(first), Is.EqualTo(firstOriginal));
                Assert.That(
                    File.ReadAllBytes(second),
                    Is.EqualTo(
                        saveBeforeRecovery || saveDuringRecovery ? concurrentEdit : secondOriginal
                    )
                );
                string retainedOriginal = Directory
                    .GetFiles(directory, "Second.cs.dxmsg-rollback-original-*")
                    .Single();
                Assert.That(File.ReadAllBytes(retainedOriginal), Is.EqualTo(secondOriginal));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void PhysicalRollbackRestoresAnEarlierFileWhenItsPrimaryBackupDisappears()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string first = Path.Combine(directory, "First.cs");
                string later = Path.Combine(directory, "Later.cs");
                byte[] firstOriginal = { 1 };
                byte[] laterOriginal = { 2 };
                string firstBackup = string.Empty;
                File.WriteAllBytes(first, firstOriginal);
                File.WriteAllBytes(later, laterOriginal);
                ReadonlyFastHandlerUpgrade.PhysicalAtomicFileStore store = new(
                    replaceFile: (source, destination, displaced) =>
                    {
                        if (destination == later)
                        {
                            File.Delete(firstBackup);
                            throw new IOException("Injected later write failure.");
                        }
                        File.Replace(source, destination, displaced);
                        if (source.Contains(".dxmsg-new-", StringComparison.Ordinal))
                        {
                            firstBackup = displaced;
                        }
                    }
                );
                ReadonlyFastHandlerUpgrade.PendingFileUpgrade[] upgrades =
                {
                    new(first, firstOriginal, new byte[] { 11 }),
                    new(later, laterOriginal, new byte[] { 22 }),
                };

                Assert.That(
                    () => ReadonlyFastHandlerUpgrade.ApplyAtomically(upgrades, store),
                    Throws.Exception
                );
                Assert.That(File.ReadAllBytes(first), Is.EqualTo(firstOriginal));
                Assert.That(File.ReadAllBytes(later), Is.EqualTo(laterOriginal));
                string retainedOriginal = Directory
                    .GetFiles(directory, "First.cs.dxmsg-rollback-original-*")
                    .Single();
                Assert.That(File.ReadAllBytes(retainedOriginal), Is.EqualTo(firstOriginal));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void PhysicalRollbackPreservesTheOriginalAndBothConcurrentEdits()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "Handler.cs");
                byte[] before = { 1 };
                byte[] after = { 2 };
                byte[] firstEdit = { 3 };
                byte[] secondEdit = { 4 };
                int replaces = 0;
                File.WriteAllBytes(path, before);
                ReadonlyFastHandlerUpgrade.PhysicalAtomicFileStore store = new(
                    replaceFile: (source, destination, displaced) =>
                    {
                        if (!source.Contains(".dxmsg-new-", StringComparison.Ordinal))
                        {
                            File.WriteAllBytes(
                                destination,
                                replaces++ == 0 ? firstEdit : secondEdit
                            );
                        }
                        File.Replace(source, destination, displaced);
                    }
                );
                string backup = store.Replace(path, before, after);

                Assert.That(
                    () => store.Restore(path, backup, before, after),
                    Throws.TypeOf<IOException>().With.Message.Contains(backup)
                );
                string recovery = Directory
                    .GetFiles(directory, "*.dxmsg-rollback-recovery-*")
                    .Single();
                Assert.That(replaces, Is.EqualTo(2));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(firstEdit));
                Assert.That(File.ReadAllBytes(recovery), Is.EqualTo(secondEdit));
                Assert.That(File.ReadAllBytes(backup), Is.EqualTo(before));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
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
                string rollbackCopy = Directory
                    .GetFiles(directory, "*.dxmsg-rollback-original-*")
                    .Single();
                Assert.That(File.ReadAllBytes(rollbackCopy), Is.EqualTo(original));

                store.Restore(path, backup, original, upgraded);
                Assert.That(File.Exists(rollbackCopy), Is.False);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
                Assert.That(File.ReadAllBytes(backup), Is.EqualTo(original));
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

            public Action? AfterDiscard { get; set; }
            public Action? BeforeDiscard { get; set; }

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
                Backups.Add(backupPath, Files[fullPath].ToArray());
                Files[fullPath] = upgradedBytes.ToArray();
                return backupPath;
            }

            public void Restore(
                string fullPath,
                string backupPath,
                byte[] originalBytes,
                byte[] expectedCurrentBytes
            )
            {
                if (!Files[fullPath].AsSpan().SequenceEqual(expectedCurrentBytes))
                {
                    throw new InvalidOperationException("File changed before rollback.");
                }
                Files[fullPath] = Backups[backupPath].ToArray();
            }

            public bool MatchesDestination(string fullPath, byte[] expectedCurrentBytes) =>
                Files.TryGetValue(fullPath, out byte[]? current)
                && current.AsSpan().SequenceEqual(expectedCurrentBytes);

            public bool TryDiscardBackup(
                string fullPath,
                string backupPath,
                byte[] expectedCurrentBytes
            )
            {
                if (!MatchesDestination(fullPath, expectedCurrentBytes))
                {
                    return false;
                }
                if (_throwOnDiscard)
                {
                    throw new InvalidOperationException("Injected cleanup failure.");
                }
                BeforeDiscard?.Invoke();
                if (!MatchesDestination(fullPath, expectedCurrentBytes))
                {
                    return false;
                }
                Backups.Remove(backupPath);
                AfterDiscard?.Invoke();
                return true;
            }
        }
    }
}
#endif
