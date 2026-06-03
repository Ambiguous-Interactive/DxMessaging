using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators.Analyzers;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
public sealed class MessageAwareComponentBaseCallAnalyzerTests
{
    // S2. Reference the analyzer's source-of-truth constant directly via InternalsVisibleTo;
    // no more duplicated literal in the tests. Drift risk eliminated.
    private static readonly string IgnoreFileName = IgnoreListReader.IgnoreFileName;

    [TestCase("DXMSG006", "dxmsg006")]
    [TestCase("DXMSG007", "dxmsg007")]
    [TestCase("DXMSG008", "dxmsg008")]
    [TestCase("DXMSG009", "dxmsg009")]
    [TestCase("DXMSG010", "dxmsg010")]
    public void SupportedDiagnosticsUseCanonicalAnalyzerDocsLinks(
        string diagnosticId,
        string expectedAnchor
    )
    {
        MessageAwareComponentBaseCallAnalyzer analyzer = new();
        DiagnosticDescriptor descriptor = analyzer.SupportedDiagnostics.Single(d =>
            d.Id == diagnosticId
        );

        string expectedLink =
            $"https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/docs/reference/analyzers.md#{expectedAnchor}";
        Assert.That(
            descriptor.HelpLinkUri,
            Is.EqualTo(expectedLink),
            $"Unexpected help link for {diagnosticId}."
        );
    }

    [Test]
    public void OverrideAwakeWithoutBaseEmitsDxmsg006()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            int x = 0;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        // E. Diagnostic location must point at the method identifier so the IDE squiggly
        // appears under the method name (not the body, modifier list, or whole declaration).
        Assert.That(
            dxmsg006
                .Location.SourceTree!.GetText()
                .GetSubText(dxmsg006.Location.SourceSpan)
                .ToString(),
            Is.EqualTo("Awake")
        );
    }

    [Test]
    public void OverrideAwakeWithBaseCallIsClean()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            base.Awake();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void ExpressionBodiedAwakeWithoutBaseEmitsDxmsg006()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        private void DoStuff() { }
        protected override void Awake() => DoStuff();
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    [Test]
    public void ExpressionBodiedAwakeWithBaseCallIsClean()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake() => base.Awake();
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void ConditionalBaseCallIsAcceptedAsGoodFaith()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        public bool flag;
        protected override void Awake()
        {
            if (flag)
            {
                base.Awake();
            }
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [TestCase("OnEnable")]
    [TestCase("OnDisable")]
    [TestCase("OnDestroy")]
    [TestCase("RegisterMessageHandlers")]
    public void EachOtherGuardedMethodEmitsDxmsg006WhenMissingBase(string methodName)
    {
        string source = $$"""
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void {{methodName}}()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        // E. Each guarded method's diagnostic squiggly must land on the method identifier.
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        Assert.That(
            dxmsg006
                .Location.SourceTree!.GetText()
                .GetSubText(dxmsg006.Location.SourceSpan)
                .ToString(),
            Is.EqualTo(methodName)
        );
    }

    // -- G3: DXMSG007 (new modifier) parametric coverage across all five guarded methods ------

    [TestCase("Awake")]
    [TestCase("OnEnable")]
    [TestCase("OnDisable")]
    [TestCase("OnDestroy")]
    [TestCase("RegisterMessageHandlers")]
    public void EachGuardedMethodWithNewModifierEmitsDxmsg007(string methodName)
    {
        // The pre-existing focused DXMSG007 test only covers Awake. This parametric test
        // pins the same contract for every guarded method so a regression that changes the
        // guarded set or the new-modifier classification cannot land silently for the other
        // four methods.
        string source = $$"""
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected new void {{methodName}}() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG007", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG007");
        Diagnostic dxmsg007 = diagnostics.Single(d => d.Id == "DXMSG007");
        Assert.That(
            dxmsg007
                .Location.SourceTree!.GetText()
                .GetSubText(dxmsg007.Location.SourceSpan)
                .ToString(),
            Is.EqualTo(methodName)
        );
        Assert.That(dxmsg007.GetMessage(), Does.Contain(methodName));
    }

    // -- G4: focused DXMSG009 tests for OnDisable and OnDestroy --------------------------------
    // Pre-existing focused tests cover OnEnable, Awake, RegisterMessageHandlers, and the no-
    // accessibility variant. The two below mirror the OnEnable pattern so a regression that
    // only breaks OnDisable or OnDestroy classification fails LOUDLY in a focused test rather
    // than only in the parametric one. (Focused tests pin which method regressed; the
    // parametric covers all methods uniformly.)

    [Test]
    public void PrivateOnDisableWithoutModifierEmitsDxmsg009()
    {
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        private void OnDisable() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG009", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG009");
        Diagnostic dxmsg009 = diagnostics.Single(d => d.Id == "DXMSG009");
        Assert.That(dxmsg009.GetMessage(), Does.Contain("Sample.BrokenThing"));
        Assert.That(dxmsg009.GetMessage(), Does.Contain("OnDisable"));
        Assert.That(dxmsg009.GetMessage(), Does.Contain("CS0114"));
        string spanText = dxmsg009
            .Location.SourceTree!.GetText()
            .GetSubText(dxmsg009.Location.SourceSpan)
            .ToString();
        Assert.That(spanText, Is.EqualTo("OnDisable"));
    }

    [Test]
    public void PrivateOnDestroyWithoutModifierEmitsDxmsg009()
    {
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        private void OnDestroy() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG009", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG009");
        Diagnostic dxmsg009 = diagnostics.Single(d => d.Id == "DXMSG009");
        Assert.That(dxmsg009.GetMessage(), Does.Contain("Sample.BrokenThing"));
        Assert.That(dxmsg009.GetMessage(), Does.Contain("OnDestroy"));
        Assert.That(dxmsg009.GetMessage(), Does.Contain("CS0114"));
        string spanText = dxmsg009
            .Location.SourceTree!.GetText()
            .GetSubText(dxmsg009.Location.SourceSpan)
            .ToString();
        Assert.That(spanText, Is.EqualTo("OnDestroy"));
    }

    // -- G5: DXMSG006 per-method consequence text ---------------------------------------------
    // The diagnostic message is per-method (G1); these tests pin the load-bearing consequence
    // phrase for each method so a future generic rewrite cannot drop the actionable wording.

    [Test]
    public void Dxmsg006MessageForAwakeMentionsRegistrationToken()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        Assert.That(
            dxmsg006.GetMessage(),
            Does.Contain("the message registration token will never be created")
        );
        Assert.That(dxmsg006.GetMessage(), Does.Contain("Sample.Player"));
        Assert.That(dxmsg006.GetMessage(), Does.Contain("base.Awake()"));
    }

    [Test]
    public void Dxmsg006MessageForOnEnableMentionsHandlersNotReEnabled()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnEnable() { int x = 1; }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        Assert.That(dxmsg006.GetMessage(), Does.Contain("handlers will not be re-enabled"));
        Assert.That(dxmsg006.GetMessage(), Does.Contain("base.OnEnable()"));
    }

    [Test]
    public void Dxmsg006MessageForOnDisableMentionsUnwantedMessageProcessing()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnDisable() { int x = 1; }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        Assert.That(dxmsg006.GetMessage(), Does.Contain("unwanted message processing"));
        Assert.That(dxmsg006.GetMessage(), Does.Contain("base.OnDisable()"));
    }

    [Test]
    public void Dxmsg006MessageForOnDestroyMentionsMemoryLeak()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnDestroy() { int x = 1; }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        Assert.That(dxmsg006.GetMessage(), Does.Contain("memory leak"));
        Assert.That(dxmsg006.GetMessage(), Does.Contain("base.OnDestroy()"));
    }

    [Test]
    public void Dxmsg006MessageForRegisterMessageHandlersMentionsStringMessageHandlers()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void RegisterMessageHandlers() { int x = 1; }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        Assert.That(
            dxmsg006.GetMessage(),
            Does.Contain("default string-message handlers will not be registered")
        );
        Assert.That(dxmsg006.GetMessage(), Does.Contain("RegisterForStringMessages"));
    }

    [Test]
    public void RegisterMessageHandlersWithoutBaseAndStringMessagesDisabledIsLoweredToInfo()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => false;

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Info);
        AssertSmartCaseMessageMentionsTypeAndMethod(diagnostics, "Sample.Player");
        AssertNoSiblings(diagnostics, "DXMSG006");
    }

    [Test]
    public void RegisterMessageHandlersInfoSmartCaseRespectsBlockBodiedFalseGetter()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages
        {
            get { return false; }
        }

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Info);
        AssertSmartCaseMessageMentionsTypeAndMethod(diagnostics, "Sample.Player");
        AssertNoSiblings(diagnostics, "DXMSG006");
    }

    [Test]
    public void RegisterMessageHandlersInfoSmartCaseRespectsArrowGetter()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages
        {
            get => false;
        }

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Info);
        AssertSmartCaseMessageMentionsTypeAndMethod(diagnostics, "Sample.Player");
        AssertNoSiblings(diagnostics, "DXMSG006");
    }

    [Test]
    public void NewKeywordOnGuardedMethodEmitsDxmsg007()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected new void Awake() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG007", DiagnosticSeverity.Warning);
        // E. DXMSG007's squiggly must also land on the method identifier.
        Diagnostic dxmsg007 = diagnostics.Single(d => d.Id == "DXMSG007");
        Assert.That(
            dxmsg007
                .Location.SourceTree!.GetText()
                .GetSubText(dxmsg007.Location.SourceSpan)
                .ToString(),
            Is.EqualTo("Awake")
        );
    }

    [Test]
    public void TwoDeepInheritanceFlagsOnlyDescendantWithMissingBase()
    {
        string source = """
namespace Sample
{
    public class A : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            base.Awake();
        }
    }

    public sealed class B : A
    {
        protected override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Diagnostic[] dxmsg006 = diagnostics.Where(d => d.Id == "DXMSG006").ToArray();
        Assert.That(dxmsg006, Has.Length.EqualTo(1));
        Assert.That(dxmsg006[0].GetMessage(), Does.Contain("Sample.B"));
    }

    [Test]
    public void IgnoreAttributeOnClassEmitsDxmsg008Only()
    {
        // B5. Two overrides: one clean, one dirty. DXMSG008 must fire ONCE; on the dirty one.
        // Clean overrides on opted-out classes must produce zero diagnostics (no noise).
        string source = """
namespace Sample
{
    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            base.Awake();
        }

        protected override void OnEnable()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
        Assert.That(
            diagnostics
                .Single(d => d.Id == "DXMSG008")
                .Location.SourceTree!.GetText()
                .GetSubText(diagnostics.Single(d => d.Id == "DXMSG008").Location.SourceSpan)
                .ToString(),
            Is.EqualTo("OnEnable")
        );
    }

    [Test]
    public void IgnoreAttributeOnMethodEmitsDxmsg008Only()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
        protected override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
    }

    [Test]
    public void ClassFullNameInIgnoreListEmitsDxmsg008Only()
    {
        // B5. Two overrides; clean and dirty. DXMSG008 fires ONCE on the dirty one.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            base.Awake();
        }

        protected override void OnDisable()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            (IgnoreFileName, "# header line\n\nSample.Player\n")
        );

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
        Assert.That(
            diagnostics
                .Single(d => d.Id == "DXMSG008")
                .Location.SourceTree!.GetText()
                .GetSubText(diagnostics.Single(d => d.Id == "DXMSG008").Location.SourceSpan)
                .ToString(),
            Is.EqualTo("OnDisable")
        );
    }

    [Test]
    public void PartialClassWithBaseCallInOtherPartialIsClean()
    {
        string source = """
namespace Sample
{
    public partial class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            base.Awake();
        }
    }

    public partial class Player
    {
        private void Helper() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void MessageAwareComponentItselfIsNeverFlagged()
    {
        // A type with the same simple name as the base class but residing outside the
        // DxMessaging.Unity namespace must be ignored: the analyzer's strict-inheritance check
        // walks BaseType (not name comparisons), so this class never inherits from the real MAC.
        string source = """
namespace Sample
{
    public abstract class MessageAwareComponent
    {
        protected virtual void Awake() { }
    }

    public sealed class Player : MessageAwareComponent
    {
        protected override void Awake() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void SealedOverrideWithoutBaseEmitsDxmsg006()
    {
        string source = """
namespace Sample
{
    public class A : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            base.Awake();
        }
    }

    public sealed class B : A
    {
        protected sealed override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Diagnostic[] dxmsg006 = diagnostics.Where(d => d.Id == "DXMSG006").ToArray();
        Assert.That(dxmsg006, Has.Length.EqualTo(1));
        Assert.That(dxmsg006[0].GetMessage(), Does.Contain("Sample.B"));
        Assert.That(dxmsg006[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
    }

    [Test]
    public void HelperIndirectionFalsePositiveStillFires()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake() => CallBaseAwake();

        private void CallBaseAwake()
        {
            base.Awake();
        }
    }
}
""";

        // Documented false positive; analyzer is good-faith and only inspects the override body.
        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    [Test]
    public void AsyncVoidAwakeWithoutBaseEmitsDxmsg006()
    {
        string source = """
namespace Sample
{
    using System.Threading.Tasks;

    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override async void Awake()
        {
            await Task.Yield();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Diagnostic[] dxmsg006 = diagnostics.Where(d => d.Id == "DXMSG006").ToArray();
        Assert.That(dxmsg006, Has.Length.EqualTo(1));
        Assert.That(dxmsg006[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
    }

    [Test]
    public void GenericIntermediaryWithoutBaseCallIsFlagged()
    {
        string source = """
namespace Sample
{
    public abstract class MyBase<T> : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            base.Awake();
        }
    }

    public sealed class MyConcrete : MyBase<int>
    {
        protected override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Diagnostic[] dxmsg006 = diagnostics.Where(d => d.Id == "DXMSG006").ToArray();
        Assert.That(dxmsg006, Has.Length.EqualTo(1));
        Assert.That(dxmsg006[0].GetMessage(), Does.Contain("Sample.MyConcrete"));
    }

    [Test]
    public void NonLiteralRegisterForStringMessagesKeepsWarningSeverity()
    {
        string source = """
namespace Sample
{
    public static class SomeStaticConfig
    {
        public static bool Disable = false;
    }

    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => SomeStaticConfig.Disable;

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    [Test]
    public void MethodWithoutOverrideOrNewIsIgnored()
    {
        // A new declaration named Awake that neither overrides nor hides; should not be flagged.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        public void Awake(int discriminator)
        {
            int x = discriminator;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void IgnoreListReaderTreatsCommentsAndBlankLinesAsNoise()
    {
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        // Lines with leading whitespace, commented lines and blank lines should be skipped.
        const string ignoreContents =
            "# This file is auto-generated; do not hand-edit\n"
            + "\n"
            + "  Sample.Player  \n"
            + "# Sample.Player.Other\n";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            (IgnoreFileName, ignoreContents)
        );

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
    }

    // -- B2 ----------------------------------------------------------------------------------

    [Test]
    public void InheritedRegisterForStringMessagesOverrideTriggersSmartCaseLowering()
    {
        // B2. The override of RegisterForStringMessages lives on the base, NOT the most-derived
        // class. The smart-case lowering must walk the inheritance chain to find it. Without the
        // fix, this test would assert Warning; with the fix, Info.
        string source = """
namespace Sample
{
    public abstract class MyBase : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => false;
    }

    public sealed class MyConcrete : MyBase
    {
        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Info);
        AssertSmartCaseMessageMentionsTypeAndMethod(diagnostics, "Sample.MyConcrete");
        AssertNoSiblings(diagnostics, "DXMSG006");
    }

    [Test]
    public void MoreDerivedRegisterForStringMessagesOverrideWinsAndPreventsSmartCase()
    {
        // B2. Most-derived override wins. Even though the grandparent returns literal false, the
        // intermediate overrides it back to literal true; so the smart-case must NOT apply.
        string source = """
namespace Sample
{
    public abstract class GrandParent : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => false;
    }

    public abstract class Parent : GrandParent
    {
        protected override bool RegisterForStringMessages => true;
    }

    public sealed class Child : Parent
    {
        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    // -- B3 ----------------------------------------------------------------------------------

    [Test]
    public void ConditionalReturnFalseInRegisterForStringMessagesGetterKeepsWarning()
    {
        // B3. Block-bodied getter with `if (...) return false; return true;` is NOT
        // unconditional; smart-case must NOT apply.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        public bool specialCondition;

        protected override bool RegisterForStringMessages
        {
            get
            {
                if (specialCondition) return false;
                return true;
            }
        }

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    // -- B4 ----------------------------------------------------------------------------------

    [TestCase("default")]
    [TestCase("false || false")]
    [TestCase("!true")]
    [TestCase("SomeStaticConfig.Disable")]
    public void NonLiteralRegisterForStringMessagesExpressionsKeepWarningSeverity(string expression)
    {
        string source = $$"""
namespace Sample
{
    public static class SomeStaticConfig
    {
        public static bool Disable = false;
    }

    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => {{expression}};

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    // -- B (strong, ordering) ----------------------------------------------------------------

    [Test]
    public void NonOverrideAwakeOnOptedOutClassProducesZeroDiagnostics()
    {
        // B (strong). A method named `Awake` with neither `override` nor `new` on an opted-out
        // class must produce zero diagnostics; including DXMSG008. Before the reorder fix,
        // DXMSG008 could fire here.
        string source = """
namespace Sample
{
    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        public void Awake(int discriminator)
        {
            int x = discriminator;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    // -- G (using alias) ---------------------------------------------------------------------

    [Test]
    public void UsingAliasForMessageAwareComponentResolvesViaFullyQualifiedName()
    {
        // G. Confirms FQN resolution survives `using` aliases; the analyzer walks BaseType
        // and compares against the symbol's display name, so aliases are transparent.
        string source = """
using MAC = DxMessaging.Unity.MessageAwareComponent;

namespace Sample
{
    public sealed class Player : MAC
    {
        protected override void Awake()
        {
            int x = 0;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    // -- H (.editorconfig severity overrides) ------------------------------------------------

    [Test]
    public void EditorConfigSuppressOnDxmsg006SilencesCanonicalCase()
    {
        // H.1; descriptor-based DXMSG006 path: WithSpecificDiagnosticOptions(Suppress) yields zero.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            int x = 0;
        }
    }
}
""";

        CSharpCompilationOptions options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary
        ).WithSpecificDiagnosticOptions(
            ImmutableDictionary.CreateRange(
                new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, ReportDiagnostic>(
                        "DXMSG006",
                        ReportDiagnostic.Suppress
                    ),
                }
            )
        );

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            options
        );

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
    }

    [Test]
    public void EditorConfigSuppressOnDxmsg006AlsoSilencesSmartCaseInfoPath()
    {
        // H.2; the runtime-built `Diagnostic.Create(string id, ...)` path used for the
        // smart-case Info lowering must also honour editorconfig severity overrides. This is the
        // rubric-flagged "real gotcha"; without proper threading the Info diagnostic would slip
        // through `Suppress`.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => false;

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        CSharpCompilationOptions options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary
        ).WithSpecificDiagnosticOptions(
            ImmutableDictionary.CreateRange(
                new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, ReportDiagnostic>(
                        "DXMSG006",
                        ReportDiagnostic.Suppress
                    ),
                }
            )
        );

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            options
        );

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
    }

    [Test]
    public void EditorConfigPromoteOnDxmsg006ProducesError()
    {
        // H.3; promotion (`Error`) must thread through the descriptor path.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            int x = 0;
        }
    }
}
""";

        CSharpCompilationOptions options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary
        ).WithSpecificDiagnosticOptions(
            ImmutableDictionary.CreateRange(
                new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, ReportDiagnostic>(
                        "DXMSG006",
                        ReportDiagnostic.Error
                    ),
                }
            )
        );

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            options
        );

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Error);
    }

    // -- I (good-faith policy: lambda / local function) --------------------------------------

    [Test]
    public void BaseCallInsideLocalFunctionIsAcceptedAsGoodFaith()
    {
        // I. base.X() inside a nested local function still satisfies the good-faith textual
        // search; DescendantNodes() walks lambdas and local functions. Documents the policy.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            void Local() { base.Awake(); }
            Local();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
    }

    // -- J (global:: prefix in ignore list) --------------------------------------------------

    [Test]
    public void GlobalPrefixedFqnInIgnoreListMatchesAfterPrefixStripping()
    {
        // J. Friendlier UX: a `global::` prefix on an ignore-list entry is stripped so it still
        // matches the symbol's omitted-global FQN comparison.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            (IgnoreFileName, "global::Sample.Player\n")
        );

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
    }

    [Test]
    public void NonPrefixedFqnInIgnoreListAlsoMatches()
    {
        // J. Sanity: the without-prefix form continues to match. Pairs with the prefixed test.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            (IgnoreFileName, "Sample.Player\n")
        );

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
    }

    // -- S1 (cache contract under repeated calls / mismatched tokens) -----------------------

    [Test]
    public void IgnoreListReaderRepeatLoadOnSameOptionsReturnsSameInstanceAndIsTokenSafe()
    {
        // S1. Verify the Lazy<T> cache contract: two calls to Load(...) with the same
        // AnalyzerOptions must return the same hashset instance (single-shot memoization),
        // and passing a different (or even already-cancelled) CancellationToken on the
        // second call must NOT crash; the factory deliberately drops the outer token via
        // CancellationToken.None to avoid the cached-cancellation-exception footgun.
        AnalyzerOptions options = GeneratorTestUtilities.BuildAnalyzerOptions(
            (IgnoreFileName, "Sample.Player\nSample.Other\n")
        );

        ImmutableHashSet<string> first = IgnoreListReader.Load(options, CancellationToken.None);
        Assert.That(first, Has.Count.EqualTo(2));
        Assert.That(first, Does.Contain("Sample.Player"));
        Assert.That(first, Does.Contain("Sample.Other"));

        // Second call uses an already-cancelled token. If the factory closure baked the
        // first call's token, this would still return the cached value; fine. The footgun
        // (which the fix prevents) is the inverse: a cancelled FIRST call caches an
        // OperationCanceledException and re-throws it forever. We can't easily simulate
        // that here without racing, so we settle for asserting the cache returns the same
        // immutable hashset reference and never throws on a token mismatch.
        using CancellationTokenSource cts = new();
        cts.Cancel();
        ImmutableHashSet<string>? second = null;
        Assert.DoesNotThrow(() => second = IgnoreListReader.Load(options, cts.Token));
        Assert.That(second, Is.SameAs(first));
    }

    // -- S4 (combined opt-out + literal-false RegisterForStringMessages) ---------------------

    [Test]
    public void OptOutAttributePlusFalseStringMessagesSettingProducesSingleDxmsg008()
    {
        // S4. When a class has BOTH the [DxIgnoreMissingBaseCall] opt-out AND a literal-false
        // RegisterForStringMessages override AND a missing-base RegisterMessageHandlers, the
        // opt-out path wins: exactly ONE DXMSG008 fires (because the underlying check would
        // have produced a DXMSG006 diagnostic at *some* severity; would-have-fired counts as
        // needing suppression), and the smart-case Info-lowering path is bypassed entirely.
        string source = """
namespace Sample
{
    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => false;

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
    }

    // -- helpers -----------------------------------------------------------------------------

    private static void AssertSingle(
        ImmutableArray<Diagnostic> diagnostics,
        string expectedId,
        DiagnosticSeverity expectedSeverity
    )
    {
        Diagnostic[] matching = diagnostics.Where(d => d.Id == expectedId).ToArray();
        Assert.That(
            matching,
            Has.Length.EqualTo(1),
            $"Expected exactly one {expectedId} diagnostic; got: "
                + string.Join(", ", diagnostics.Select(d => d.Id + "(" + d.Severity + ")"))
        );
        Assert.That(
            matching[0].Severity,
            Is.EqualTo(expectedSeverity),
            $"Expected {expectedId} to have severity {expectedSeverity}."
        );
    }

    /// <summary>
    /// D. Smart-case tests must verify the formatted message threads the type display name and
    /// the `RegisterMessageHandlers` method name correctly through the runtime-built diagnostic.
    /// </summary>
    private static void AssertSmartCaseMessageMentionsTypeAndMethod(
        ImmutableArray<Diagnostic> diagnostics,
        string expectedTypeDisplayName
    )
    {
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        string message = dxmsg006.GetMessage();
        Assert.That(
            message,
            Does.Contain(expectedTypeDisplayName),
            $"Smart-case message must include the containing type name '{expectedTypeDisplayName}'."
        );
        Assert.That(
            message,
            Does.Contain("RegisterMessageHandlers"),
            "Smart-case message must include the method name 'RegisterMessageHandlers'."
        );
    }

    /// <summary>
    /// F. Belt-and-braces: assert no spurious sibling diagnostics from other DXMSG ids when the
    /// canonical id under test is fixed.
    /// </summary>
    private static void AssertNoSiblings(ImmutableArray<Diagnostic> diagnostics, string canonicalId)
    {
        if (canonicalId != "DXMSG007")
        {
            Assert.That(
                diagnostics.Count(d => d.Id == "DXMSG007"),
                Is.Zero,
                "Did not expect any DXMSG007 diagnostics."
            );
        }
        if (canonicalId != "DXMSG008")
        {
            Assert.That(
                diagnostics.Count(d => d.Id == "DXMSG008"),
                Is.Zero,
                "Did not expect any DXMSG008 diagnostics."
            );
        }
        if (canonicalId != "DXMSG006")
        {
            Assert.That(
                diagnostics.Count(d => d.Id == "DXMSG006"),
                Is.Zero,
                "Did not expect any DXMSG006 diagnostics."
            );
        }
        if (canonicalId != "DXMSG009")
        {
            Assert.That(
                diagnostics.Count(d => d.Id == "DXMSG009"),
                Is.Zero,
                "Did not expect any DXMSG009 diagnostics."
            );
        }
    }

    // -- DXMSG009 (implicit-hide / missing-modifier) coverage --------------------------------

    [Test]
    public void PrivateOnEnableWithoutModifierEmitsDxmsg009()
    {
        // The user-reported case: `private void OnEnable() {}` on a MessageAwareComponent subclass.
        // C# emits CS0114; our analyzer must surface DXMSG009 so the inspector overlay also shows it.
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        private void OnEnable() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG009", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG009");
        Diagnostic dxmsg009 = diagnostics.Single(d => d.Id == "DXMSG009");
        Assert.That(dxmsg009.GetMessage(), Does.Contain("Sample.BrokenThing"));
        Assert.That(dxmsg009.GetMessage(), Does.Contain("OnEnable"));
        // S1: pin the CS0114 cross-reference into the message so a future refactor that drops the
        // parenthetical doesn't silently lose the canonical compiler-warning anchor.
        Assert.That(dxmsg009.GetMessage(), Does.Contain("CS0114"));
        string spanText = dxmsg009
            .Location.SourceTree!.GetText()
            .GetSubText(dxmsg009.Location.SourceSpan)
            .ToString();
        Assert.That(spanText, Is.EqualTo("OnEnable"));
    }

    [Test]
    public void GenericMethodWithGuardedNameDoesNotFireDxmsg009()
    {
        // C# does NOT emit CS0114 for `void Awake<T>()` because the type-parameter arity differs
        // from the base; both methods coexist. DXMSG009 must not fire either; flagging it would
        // be a false positive misleading the user toward an incorrect "fix".
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        private void Awake<T>() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void ExpressionBodiedNonOverrideOnEnableEmitsDxmsg009()
    {
        // Expression-bodied form of the implicit-hide pattern. The method is parameter-less,
        // returns void, non-static, and has no override/new modifier; so DXMSG009 fires.
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        private void DoStuff() { }
        private void OnEnable() => DoStuff();
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG009", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG009");
    }

    [Test]
    public void Dxmsg009CoexistsWithDxmsg006OnSameClass()
    {
        // S3: a class can have one method that genuinely overrides without base (DXMSG006) AND
        // another that implicitly hides (DXMSG009). Both diagnostics must fire on the same class
        // so the inspector overlay surfaces both methods in its HelpBox.
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake() { /* missing base.Awake() */ }
        private void OnEnable() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Count(d => d.Id == "DXMSG006"), Is.EqualTo(1));
        Assert.That(diagnostics.Count(d => d.Id == "DXMSG009"), Is.EqualTo(1));
        Assert.That(diagnostics.Count(d => d.Id == "DXMSG007"), Is.Zero);
        Assert.That(diagnostics.Count(d => d.Id == "DXMSG008"), Is.Zero);
    }

    [Test]
    public void NoAccessibilityOnEnableWithoutModifierEmitsDxmsg009()
    {
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        void OnEnable() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG009", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG009");
    }

    [Test]
    public void ProtectedAwakeWithoutModifierEmitsDxmsg009()
    {
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        protected void Awake() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG009", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG009");
    }

    [Test]
    public void PublicRegisterMessageHandlersWithoutModifierEmitsDxmsg009()
    {
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        public void RegisterMessageHandlers() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG009", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG009");
    }

    [TestCase("Awake")]
    [TestCase("OnEnable")]
    [TestCase("OnDisable")]
    [TestCase("OnDestroy")]
    [TestCase("RegisterMessageHandlers")]
    public void EachGuardedMethodWithoutModifierEmitsDxmsg009(string methodName)
    {
        string source = $$"""
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        private void {{methodName}}() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG009", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG009");
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG009").GetMessage(),
            Does.Contain(methodName)
        );
    }

    [Test]
    public void OnEnableOverloadWithParameterDoesNotFireDxmsg009()
    {
        // A method named OnEnable that takes a parameter is NOT a Unity lifecycle override and
        // does NOT hide the base. Signature filter must keep this silent.
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        public void OnEnable(int discriminator) { int x = discriminator; }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void StaticAwakeDoesNotFireDxmsg009()
    {
        // Unity ignores static lifecycle methods; the analyzer should as well.
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        private static void Awake() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void NonVoidOnEnableDoesNotFireDxmsg009()
    {
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        public int OnEnable() => 0;
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void Dxmsg009RespectsClassLevelDxIgnoreMissingBaseCall()
    {
        string source = """
namespace Sample
{
    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        private void OnEnable() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
        AssertNoSiblings(diagnostics, "DXMSG008");
        // S6: pin the suppression-source string so a future change to the analyzer's argument
        // passing (the literal `[DxIgnoreMissingBaseCall]` for attribute-driven opt-outs vs the
        // ignore-list filename) does not silently drift.
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG008").GetMessage(),
            Does.Contain("[DxIgnoreMissingBaseCall]")
        );
    }

    [Test]
    public void Dxmsg009RespectsMethodLevelDxIgnoreMissingBaseCall()
    {
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
        private void OnEnable() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
        AssertNoSiblings(diagnostics, "DXMSG008");
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG008").GetMessage(),
            Does.Contain("[DxIgnoreMissingBaseCall]")
        );
    }

    [Test]
    public void Dxmsg009RespectsProjectIgnoreList()
    {
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        private void OnEnable() { }
    }
}
""";
        (string path, string contents)[] additionalFiles = new[]
        {
            ($"some/path/{IgnoreListReader.IgnoreFileName}", "Sample.BrokenThing\n"),
        };

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            additionalFiles
        );

        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
        AssertNoSiblings(diagnostics, "DXMSG008");
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG008").GetMessage(),
            Does.Contain(IgnoreListReader.IgnoreFileName)
        );
    }

    [Test]
    public void Dxmsg009DoesNotFireOnUnrelatedClass()
    {
        // S9: an UNRELATED MonoBehaviour subclass (NOT inheriting from MessageAwareComponent) must
        // never receive DXMSG009 even when it declares same-named methods. The strict-inheritance
        // walk is what gates the analyzer; using a MonoBehaviour base instead of a bare class is a
        // stronger pin against future regressions where a looser "any MonoBehaviour" rule would
        // over-fire.
        string source = """
namespace Sample
{
    public class Unrelated : UnityEngine.MonoBehaviour
    {
        private void OnEnable() { }
        private void Awake() { }
        private void RegisterMessageHandlers() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void Dxmsg009SmartCaseDoesNotApply()
    {
        // Smart-case (literal-`false` RegisterForStringMessages → Info) is DXMSG006-only.
        // Even when the same class overrides RegisterForStringMessages => false, a missing-modifier
        // RegisterMessageHandlers must stay at Warning severity (DXMSG009), not Info.
        string source = """
namespace Sample
{
    public class BrokenThing : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => false;
        private void RegisterMessageHandlers() { }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG009", DiagnosticSeverity.Warning);
        AssertNoSiblings(diagnostics, "DXMSG009");
    }

    [Test]
    public void NestedTypeFullyQualifiedNameUsesDotSeparatorForOverlayLookup()
    {
        // S6 regression: System.Type.FullName renders nested types as `Outer+Nested`, but the
        // analyzer's `containingType.ToDisplayString()` (which produces the FQN the harvester
        // keys snapshot rows by) renders them as `Outer.Nested`. The inspector overlay normalises
        // FullName to dot-form before the lookup; this test pins the analyzer's output shape so
        // a future Roslyn or analyzer change that flips the format breaks LOUDLY here rather
        // than silently breaking the inspector for every nested MessageAwareComponent subclass.
        string source = """
namespace Sample
{
    public sealed class Outer
    {
        public sealed class Nested : DxMessaging.Unity.MessageAwareComponent
        {
            protected override void Awake()
            {
                int x = 0;
            }
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        // The emitted message must contain the dot-form of the nested FQN; that is the form
        // the harvester ingests and keys the snapshot by.
        Assert.That(dxmsg006.GetMessage(), Does.Contain("Sample.Outer.Nested"));
        Assert.That(dxmsg006.GetMessage(), Does.Not.Contain("Outer+Nested"));
    }

    // -- DXMSG010 (transitive broken base-call chain) ----------------------------------------

    [Test]
    public void BrokenIntermediateAncestorEmitsDxmsg010OnDescendant()
    {
        // The exact user-reported case. `BrokenThing.OnEnable` correctly calls base.OnEnable(),
        // but the inherited override on `ddd` has an empty body; so the chain stops at `ddd`
        // and never reaches `MessageAwareComponent.OnEnable`. DXMSG006 fires on `ddd`; DXMSG010
        // fires on `BrokenThing` so the user editing `BrokenThing` is told the chain is broken.
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        // Field included in the user's literal report shape; exercised here for fidelity.
        public int a;
        protected override void OnEnable() { }
    }

    public class BrokenThing : ddd
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        AssertSingle(diagnostics, "DXMSG010", DiagnosticSeverity.Warning);

        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        Assert.That(dxmsg006.GetMessage(), Does.Contain("Sample.ddd"));

        Diagnostic dxmsg010 = diagnostics.Single(d => d.Id == "DXMSG010");
        string msg010 = dxmsg010.GetMessage();
        Assert.That(msg010, Does.Contain("Sample.BrokenThing"));
        Assert.That(msg010, Does.Contain("OnEnable"));
        Assert.That(msg010, Does.Contain("Sample.ddd"));
    }

    [Test]
    public void ThreeDeepBrokenIntermediateEmitsDxmsg010OnEveryDescendant()
    {
        // `ddd.OnEnable` is empty → DXMSG006 on ddd. Both `Middle` and `BrokenThing` correctly
        // call base.OnEnable() but the chain dies at `ddd`. DXMSG010 must fire on BOTH descendants
        // so each user editing either type sees the warning.
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnEnable() { }
    }

    public class Middle : ddd
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }

    public class BrokenThing : Middle
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(
            diagnostics.Count(d => d.Id == "DXMSG006"),
            Is.EqualTo(1),
            "Exactly one DXMSG006 expected (on ddd)."
        );
        Diagnostic dxmsg006 = diagnostics.Single(d => d.Id == "DXMSG006");
        Assert.That(dxmsg006.GetMessage(), Does.Contain("Sample.ddd"));
        Assert.That(dxmsg006.Severity, Is.EqualTo(DiagnosticSeverity.Warning));

        Diagnostic[] dxmsg010 = diagnostics.Where(d => d.Id == "DXMSG010").ToArray();
        Assert.That(
            dxmsg010,
            Has.Length.EqualTo(2),
            "Exactly two DXMSG010 expected (on Middle and BrokenThing)."
        );
        Assert.That(dxmsg010.All(d => d.Severity == DiagnosticSeverity.Warning), Is.True);
        string[] messages = dxmsg010.Select(d => d.GetMessage()).ToArray();
        Assert.That(messages.Any(m => m.Contains("Sample.Middle")), Is.True);
        Assert.That(messages.Any(m => m.Contains("Sample.BrokenThing")), Is.True);
        // Both DXMSG010 messages should mention `Sample.ddd` as the first broken ancestor.
        Assert.That(messages.All(m => m.Contains("Sample.ddd")), Is.True);
    }

    [Test]
    public void HealthyChainEmitsNoDiagnostics()
    {
        // Sanity: when every override correctly calls base, no diagnostics fire; the chain
        // walk must not produce false positives on a clean inheritance graph.
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }

    public class BrokenThing : ddd
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void IntermediateDoesNotOverrideAtAllIsClean()
    {
        // When `ddd` has no OnEnable override at all, BrokenThing.OnEnable's OverriddenMethod
        // resolves directly to MessageAwareComponent.OnEnable (which is virtual + chain-
        // terminating). No DXMSG010 should fire.
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        public int a;
    }

    public class BrokenThing : ddd
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void Dxmsg010RespectsClassLevelDxIgnoreMissingBaseCall()
    {
        // Class-level [DxIgnoreMissingBaseCall] on `BrokenThing` must convert the would-be
        // DXMSG010 into DXMSG008. DXMSG006 on `ddd` is unaffected (different type).
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnEnable() { }
    }

    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public class BrokenThing : ddd
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Count(d => d.Id == "DXMSG010"), Is.Zero);
        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG006").GetMessage(),
            Does.Contain("Sample.ddd")
        );
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
        Diagnostic dxmsg008 = diagnostics.Single(d => d.Id == "DXMSG008");
        Assert.That(dxmsg008.GetMessage(), Does.Contain("Sample.BrokenThing"));
        Assert.That(dxmsg008.GetMessage(), Does.Contain("[DxIgnoreMissingBaseCall]"));
    }

    [Test]
    public void Dxmsg010RespectsProjectIgnoreList()
    {
        // Project-wide ignore-list entry for `BrokenThing` must lower DXMSG010 to DXMSG008.
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnEnable() { }
    }

    public class BrokenThing : ddd
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            (IgnoreFileName, "Sample.BrokenThing\n")
        );

        Assert.That(diagnostics.Count(d => d.Id == "DXMSG010"), Is.Zero);
        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG006").GetMessage(),
            Does.Contain("Sample.ddd")
        );
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
        Diagnostic dxmsg008 = diagnostics.Single(d => d.Id == "DXMSG008");
        Assert.That(dxmsg008.GetMessage(), Does.Contain("Sample.BrokenThing"));
        Assert.That(dxmsg008.GetMessage(), Does.Contain(IgnoreListReader.IgnoreFileName));
    }

    [Test]
    public void Dxmsg010ChainSurvivesGenericIntermediate()
    {
        // The chain-walk normalizes via OriginalDefinition so a generic intermediate doesn't
        // confuse the lookup. `MyBase<T>.OnEnable` is broken; `BrokenThing : MyBase<int>` calls
        // base correctly. DXMSG006 fires on MyBase, DXMSG010 fires on BrokenThing.
        string source = """
namespace Sample
{
    public class MyBase<T> : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnEnable() { }
    }

    public sealed class BrokenThing : MyBase<int>
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG006").GetMessage(),
            Does.Contain("Sample.MyBase")
        );
        AssertSingle(diagnostics, "DXMSG010", DiagnosticSeverity.Warning);
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG010").GetMessage(),
            Does.Contain("Sample.BrokenThing")
        );
    }

    [Test]
    public void Dxmsg010StillFiresAtWarningEvenWhenSmartCaseLowersDxmsg006OnAncestor()
    {
        // The smart-case lowering (literal `RegisterForStringMessages => false`) takes DXMSG006
        // on `ddd.RegisterMessageHandlers` from Warning to Info; but the chain is GENUINELY
        // broken from BrokenThing's perspective. DXMSG010 must still fire at Warning on
        // BrokenThing: smart-case is a per-method per-class courtesy, descendants still need
        // the chain to be unbroken.
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => false;
        protected override void RegisterMessageHandlers() { }
    }

    public class BrokenThing : ddd
    {
        protected override void RegisterMessageHandlers()
        {
            base.RegisterMessageHandlers();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Info);
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG006").GetMessage(),
            Does.Contain("Sample.ddd")
        );
        AssertSingle(diagnostics, "DXMSG010", DiagnosticSeverity.Warning);
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG010").GetMessage(),
            Does.Contain("Sample.BrokenThing")
        );
    }

    // Cross-assembly assume-clean policy: when an ancestor's override has no
    // DeclaringSyntaxReferences (e.g. lives in a binary-only third-party package), the analyzer
    // trusts it and does not emit DXMSG010. Emitting DXMSG010 against a type the user can't
    // edit would be unactionable. This branch is exercised at runtime against compiled
    // dependencies; it cannot be unit-tested here because every Roslyn fixture in this
    // dotnet-test project compiles all sources into a single in-memory assembly. The policy is
    // documented in `docs/reference/analyzers.md` under DXMSG010 and pinned by
    // `ChainReachesMessageAwareComponent`'s remarks.

    [Test]
    public void Dxmsg010MessageMentionsBrokenAncestorTypeName()
    {
        // The DXMSG010 message must include the FQN of the broken ancestor; not a generic
        // "an ancestor" placeholder; so the user knows exactly where the chain is broken.
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnEnable() { }
    }

    public sealed class BrokenThing : ddd
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Diagnostic dxmsg010 = diagnostics.Single(d => d.Id == "DXMSG010");
        string message = dxmsg010.GetMessage();
        Assert.That(message, Does.Contain("Sample.ddd"));
        Assert.That(message, Does.Not.Contain("an ancestor"));
        Assert.That(message, Does.Not.Contain("{2}"));
    }

    [Test]
    public void Dxmsg010LocationIsOnDescendantMethodIdentifier()
    {
        // The squiggle should land on the method identifier of the type the user can edit (the
        // descendant), not on the broken ancestor's identifier. Pin the source-span text.
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnEnable() { }
    }

    public sealed class BrokenThing : ddd
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Diagnostic dxmsg010 = diagnostics.Single(d => d.Id == "DXMSG010");
        string spanText = dxmsg010
            .Location.SourceTree!.GetText()
            .GetSubText(dxmsg010.Location.SourceSpan)
            .ToString();
        Assert.That(spanText, Is.EqualTo("OnEnable"));
        // Sanity: the source span for DXMSG010 must NOT point inside `ddd`; confirm by
        // verifying the surrounding source contains "BrokenThing" (the descendant) within a
        // small window around the span.
        string fullText = dxmsg010.Location.SourceTree.GetText().ToString();
        int spanStart = dxmsg010.Location.SourceSpan.Start;
        int windowStart = System.Math.Max(0, spanStart - 200);
        string window = fullText.Substring(windowStart, spanStart - windowStart);
        Assert.That(window, Does.Contain("BrokenThing"));
    }

    // -- Adversarial-audit additions ---------------------------------------------------------

    [Test]
    public void Dxmsg008AttributeAndIgnoreListBothPresentFiresOnce()
    {
        // Adversarial: BOTH the class-level [DxIgnoreMissingBaseCall] attribute AND the project
        // ignore list claim Sample.Player. The opt-out path must coalesce; exactly ONE DXMSG008
        // is emitted for the offending method, not two competing entries (one per opt-out source).
        string source = """
namespace Sample
{
    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(
            source,
            (IgnoreFileName, "Sample.Player\n")
        );

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
    }

    [Test]
    public void Dxmsg008MethodAndClassAttributeBothPresentFiresOnce()
    {
        // Adversarial: BOTH the class-level AND method-level [DxIgnoreMissingBaseCall] are set.
        // Exactly ONE DXMSG008 should fire; the opt-out is binary, so duplicate sources do not
        // duplicate the diagnostic.
        string source = """
namespace Sample
{
    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
        protected override void Awake()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
    }

    [Test]
    public void Dxmsg008ClassAttributeWithMixedCleanAndDirtyMethodsOnlyFiresForDirty()
    {
        // The class is opted out via [DxIgnoreMissingBaseCall]; one method is broken (would emit
        // DXMSG006), another method is clean (calls base). DXMSG008 must fire EXACTLY ONCE; on
        // the would-have-fired method only; and the clean method must not produce noise.
        string source = """
namespace Sample
{
    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake()
        {
            base.Awake();
        }

        protected override void OnEnable()
        {
            int x = 1;
        }

        protected override void OnDisable()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG009"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG010"), Is.Empty);
        // Expect a DXMSG008 for each broken method that would have fired (OnEnable, OnDisable),
        // but not the clean Awake. Pin the count so a regression that fires for clean methods
        // (or fires at type-granularity instead of method-granularity) breaks loudly.
        Diagnostic[] dxmsg008 = diagnostics.Where(d => d.Id == "DXMSG008").ToArray();
        Assert.That(
            dxmsg008,
            Has.Length.EqualTo(2),
            "Expected one DXMSG008 per dirty method; clean methods must not contribute."
        );
        string[] spans = dxmsg008
            .Select(d =>
                d.Location.SourceTree!.GetText().GetSubText(d.Location.SourceSpan).ToString()
            )
            .ToArray();
        Assert.That(spans, Is.EquivalentTo(new[] { "OnEnable", "OnDisable" }));
    }

    [Test]
    public void Dxmsg010DoesNotFireWhenAncestorHasSmartCaseAndCallsBaseCorrectly()
    {
        // Spec 1b (clean variant): ancestor has literal `RegisterForStringMessages => false` AND
        // its RegisterMessageHandlers correctly calls base. Descendant overrides and calls base.
        // The chain is genuinely clean; DXMSG010 must NOT fire (and DXMSG006 must NOT fire).
        string source = """
namespace Sample
{
    public class Ancestor : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => false;

        protected override void RegisterMessageHandlers()
        {
            base.RegisterMessageHandlers();
        }
    }

    public sealed class Descendant : Ancestor
    {
        protected override void RegisterMessageHandlers()
        {
            base.RegisterMessageHandlers();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG010"), Is.Empty);
    }

    [Test]
    public void Dxmsg010ChainSurvivesUnusuallyShapedFourLevelChain()
    {
        // Spec 1c (defensive): unusually shaped chain across four levels, with the broken link at
        // the deepest level, an intermediate that does NOT declare the slot, and a leaf that calls
        // base. The chain walker must terminate without infinite-looping. C# does not allow
        // partial-class self-references that would form a true cycle, but this is the closest
        // shape we can construct: the walker must skip Middle (no declaration) and find the broken
        // ddd override.
        string source = """
namespace Sample
{
    public class ddd : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void OnEnable() { }
    }

    public class Middle : ddd
    {
        // Intentionally does NOT declare OnEnable.
        public int unused;
    }

    public class Inner : Middle
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }

    public sealed class Leaf : Inner
    {
        protected override void OnEnable()
        {
            base.OnEnable();
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        // ddd has DXMSG006. Inner and Leaf both have DXMSG010; chain dies at ddd.
        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
        Assert.That(
            diagnostics.Single(d => d.Id == "DXMSG006").GetMessage(),
            Does.Contain("Sample.ddd")
        );
        Diagnostic[] dxmsg010 = diagnostics.Where(d => d.Id == "DXMSG010").ToArray();
        Assert.That(dxmsg010, Has.Length.EqualTo(2));
        Assert.That(
            dxmsg010.Select(d => d.GetMessage()).Any(m => m.Contains("Sample.Inner")),
            Is.True
        );
        Assert.That(
            dxmsg010.Select(d => d.GetMessage()).Any(m => m.Contains("Sample.Leaf")),
            Is.True
        );
    }

    [Test]
    public void TernaryReturningFalseOnRegisterForStringMessagesDoesNotApplySmartCase()
    {
        // Spec 1d: smart-case lowering applies ONLY for a literal `false`. A ternary expression;
        // even one that always evaluates to false at runtime; must NOT lower DXMSG006 to Info.
        // The analyzer's literal-shape check is syntactic; runtime evaluation is irrelevant.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        public bool flag;
        protected override bool RegisterForStringMessages => flag ? false : false;

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    [Test]
    public void IsFalsePatternOnRegisterForStringMessagesDoesNotApplySmartCase()
    {
        // Spec 1d: an `is false` pattern is not a literal-false return; smart-case must not apply.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        public bool flag;
        protected override bool RegisterForStringMessages => flag is false;

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    [Test]
    public void SwitchExpressionReturningFalseOnRegisterForStringMessagesDoesNotApplySmartCase()
    {
        // Spec 1d: a switch expression whose only arm returns literal false is still NOT a literal
        // false return; smart-case must not apply.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override bool RegisterForStringMessages => 0 switch { _ => false };

        protected override void RegisterMessageHandlers()
        {
            int x = 1;
        }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        AssertSingle(diagnostics, "DXMSG006", DiagnosticSeverity.Warning);
    }

    // ------------------------------------------------------------------------------------------
    // Phase D: positive (no-warning-noise) coverage of [DxIgnoreMissingBaseCall].
    //
    // Contract pinned (NOT brief's "ZERO diagnostics"): opt-out via [DxIgnoreMissingBaseCall]
    // suppresses DXMSG006/007/009/010 entirely; the analyzer instead emits DXMSG008 (Info) on
    // each method that WOULD otherwise have fired, so the user can still see the suppression is
    // active during build. Clean methods on opted-out classes produce zero diagnostics.
    // ------------------------------------------------------------------------------------------

    [Test]
    public void IgnoreMissingBaseCallAttributeAtClassScopeSuppressesAllGuardedMethods()
    {
        // Class-level attribute on a class that overrides EVERY guarded method without calling
        // base. Expect: no DXMSG006/007/009/010, and one DXMSG008 (Info) per dirty override.
        string source = """
namespace Sample
{
    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake() { int a = 0; }
        protected override void OnEnable() { int b = 0; }
        protected override void OnDisable() { int c = 0; }
        protected override void OnDestroy() { int d = 0; }
        protected override void RegisterMessageHandlers() { int e = 0; }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG009"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG010"), Is.Empty);

        Diagnostic[] dxmsg008 = diagnostics.Where(d => d.Id == "DXMSG008").ToArray();
        Assert.That(
            dxmsg008,
            Has.Length.EqualTo(5),
            "Class-level [DxIgnoreMissingBaseCall] should produce one DXMSG008 per dirty override."
        );
        string[] spans = dxmsg008
            .Select(d =>
                d.Location.SourceTree!.GetText().GetSubText(d.Location.SourceSpan).ToString()
            )
            .ToArray();
        Assert.That(
            spans,
            Is.EquivalentTo(
                new[] { "Awake", "OnEnable", "OnDisable", "OnDestroy", "RegisterMessageHandlers" }
            )
        );
        foreach (Diagnostic d in dxmsg008)
        {
            Assert.That(d.Severity, Is.EqualTo(DiagnosticSeverity.Info));
            Assert.That(d.GetMessage(), Does.Contain("[DxIgnoreMissingBaseCall]"));
        }
    }

    [Test]
    public void IgnoreMissingBaseCallAttributeAtMethodScopeSuppressesOnlyAnnotatedMethod()
    {
        // Class is NOT opted out; ONE method has the attribute. The annotated method must be
        // exempt (DXMSG008 Info), the others must still fire DXMSG006.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
        protected override void Awake() { int a = 0; }

        protected override void OnEnable() { int b = 0; }
        protected override void OnDisable() { int c = 0; }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        // The annotated `Awake` becomes DXMSG008 (Info). The two unannotated methods stay as
        // DXMSG006 (Warning).
        Diagnostic[] dxmsg008 = diagnostics.Where(d => d.Id == "DXMSG008").ToArray();
        Assert.That(dxmsg008, Has.Length.EqualTo(1));
        Assert.That(
            dxmsg008[0]
                .Location.SourceTree!.GetText()
                .GetSubText(dxmsg008[0].Location.SourceSpan)
                .ToString(),
            Is.EqualTo("Awake")
        );

        Diagnostic[] dxmsg006 = diagnostics.Where(d => d.Id == "DXMSG006").ToArray();
        Assert.That(
            dxmsg006,
            Has.Length.EqualTo(2),
            "Method-level opt-out must NOT suppress DXMSG006 on its sibling overrides."
        );
        string[] dxmsg006Spans = dxmsg006
            .Select(d =>
                d.Location.SourceTree!.GetText().GetSubText(d.Location.SourceSpan).ToString()
            )
            .ToArray();
        Assert.That(dxmsg006Spans, Is.EquivalentTo(new[] { "OnEnable", "OnDisable" }));
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG009"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG010"), Is.Empty);
    }

    [Test]
    public void IgnoreMissingBaseCallAttributeAtMethodScopeOnHiddenWithNewSuppressesDxmsg007()
    {
        // Method-level [DxIgnoreMissingBaseCall] on a `new`-hidden guarded method must downgrade
        // the would-be DXMSG007 to a single DXMSG008 Info, with no other diagnostics.
        string source = """
namespace Sample
{
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
        protected new void Awake() { int x = 0; }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics.Where(d => d.Id == "DXMSG006"), Is.Empty);
        Assert.That(diagnostics.Where(d => d.Id == "DXMSG007"), Is.Empty);
        AssertSingle(diagnostics, "DXMSG008", DiagnosticSeverity.Info);
        Diagnostic dxmsg008 = diagnostics.Single(d => d.Id == "DXMSG008");
        Assert.That(
            dxmsg008
                .Location.SourceTree!.GetText()
                .GetSubText(dxmsg008.Location.SourceSpan)
                .ToString(),
            Is.EqualTo("Awake")
        );
        Assert.That(dxmsg008.GetMessage(), Does.Contain("[DxIgnoreMissingBaseCall]"));
    }

    [Test]
    public void IgnoreMissingBaseCallAttributeAtClassScopeOnCleanOverridesProducesZeroDiagnostics()
    {
        // Belt-and-braces: class-level opt-out plus EVERY override calls base. No would-be
        // DXMSG006/007/009/010 means no DXMSG008 either; clean overrides on opted-out classes
        // must produce zero noise (matches the IgnoreAttributeOnClassEmitsDxmsg008Only contract).
        string source = """
namespace Sample
{
    [DxMessaging.Core.Attributes.DxIgnoreMissingBaseCall]
    public sealed class Player : DxMessaging.Unity.MessageAwareComponent
    {
        protected override void Awake() { base.Awake(); }
        protected override void OnEnable() { base.OnEnable(); }
        protected override void OnDisable() { base.OnDisable(); }
        protected override void OnDestroy() { base.OnDestroy(); }
        protected override void RegisterMessageHandlers() { base.RegisterMessageHandlers(); }
    }
}
""";

        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.RunBaseCallAnalyzer(source);

        Assert.That(diagnostics, Is.Empty);
    }

    // -- G2: meta-test pinning the guarded-method invariant -----------------------------------

    /// <summary>
    /// Names recognised by Unity as engine-driven lifecycle hooks. The analyzer's guarded set is a
    /// strict subset; any virtual-with-body lifecycle method on <c>MessageAwareComponent</c> that
    /// is in this list AND not in the analyzer's allow-list MUST be in the guarded set.
    /// </summary>
    private static readonly ImmutableHashSet<string> KnownUnityLifecycleNames =
        ImmutableHashSet.Create(
            "Awake",
            "Start",
            "OnEnable",
            "OnDisable",
            "OnDestroy",
            "OnApplicationQuit",
            "OnApplicationPause",
            "OnApplicationFocus",
            "Reset",
            "OnValidate",
            "OnTransformParentChanged",
            "OnTransformChildrenChanged",
            "OnBecameVisible",
            "OnBecameInvisible"
        );

    /// <summary>
    /// Unity lifecycle hooks whose canonical signature takes a single <c>bool</c> argument.
    /// All other Unity lifecycle hooks are zero-argument; the meta-test parameter filter accepts
    /// either zero parameters or exactly one <c>bool</c> for these specific names.
    /// </summary>
    private static readonly ImmutableHashSet<string> KnownOneArgBoolLifecycleNames =
        ImmutableHashSet.Create("OnApplicationFocus", "OnApplicationPause");

    [Test]
    public void GuardedMethodListMatchesAllVirtualLifecycleMethodsOnPublicBaseClasses()
    {
        // Locate the MessageAwareComponent source file by walking up from the test assembly's
        // build output toward the repo root. The test project links the editor IL helpers; the
        // runtime file lives at Runtime/Unity/MessageAwareComponent.cs at repo root. Going via
        // the source file (rather than reflecting on a compiled assembly) avoids needing a Unity
        // reference inside the dotnet-test project.
        string macSourcePath = LocateRuntimeMessageAwareComponentSource();
        Assert.That(
            File.Exists(macSourcePath),
            Is.True,
            $"Could not locate Runtime/Unity/MessageAwareComponent.cs at expected path '{macSourcePath}'."
        );

        // The runtime file is gated on UNITY_2021_3_OR_NEWER; without that symbol the class
        // declaration is preprocessed away and the test misses every method. Define the symbol
        // explicitly so we see the same syntax tree the Unity compiler does.
        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithPreprocessorSymbols(
            "UNITY_2021_3_OR_NEWER",
            "UNITY_EDITOR",
            "DEBUG"
        );
        SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(macSourcePath), parseOptions);
        ClassDeclarationSyntax macClass = tree.GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "MessageAwareComponent");

        // Enumerate every method declaration that LOOKS like a Unity lifecycle hook on the
        // MessageAwareComponent class itself (not nested types). "Looks like" means: parameter-
        // less, void-returning, non-static, non-generic, with a known Unity lifecycle name OR the
        // project-specific RegisterMessageHandlers method (a framework hook the analyzer also
        // guards). For each, classify as virtual-with-body (must be guarded) or empty
        // intentionally (must be in the allow list).
        HashSet<string> mustBeGuarded = new(System.StringComparer.Ordinal);
        HashSet<string> emptyVirtuals = new(System.StringComparer.Ordinal);

        foreach (
            MethodDeclarationSyntax methodDecl in macClass.Members.OfType<MethodDeclarationSyntax>()
        )
        {
            string name = methodDecl.Identifier.ValueText;
            bool isLifecycleName = KnownUnityLifecycleNames.Contains(name);
            bool isFrameworkHook = string.Equals(
                name,
                "RegisterMessageHandlers",
                System.StringComparison.Ordinal
            );
            if (!isLifecycleName && !isFrameworkHook)
            {
                continue;
            }

            // Signature filter: only zero-arg void instance non-generic methods are Unity engine
            // targets, EXCEPT for the canonical 1-arg-bool lifecycle hooks
            // (`OnApplicationFocus(bool)`, `OnApplicationPause(bool)`) which are also valid Unity
            // signatures. We accept either zero parameters or, for those specific names, exactly
            // one bool parameter; everything else (e.g. `void OnEnable(int)`) stays filtered out.
            int paramCount = methodDecl.ParameterList.Parameters.Count;
            bool isOneArgBoolLifecycleHook =
                paramCount == 1
                && KnownOneArgBoolLifecycleNames.Contains(name)
                && methodDecl.ParameterList.Parameters[0].Type is PredefinedTypeSyntax paramType
                && paramType.Keyword.ValueText == "bool";
            if (paramCount != 0 && !isOneArgBoolLifecycleHook)
            {
                continue;
            }
            if (
                methodDecl.ReturnType is not PredefinedTypeSyntax pts
                || pts.Keyword.ValueText != "void"
            )
            {
                continue;
            }
            if (methodDecl.TypeParameterList is not null)
            {
                continue;
            }
            bool isStatic = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            bool isVirtual = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword));
            if (isStatic || !isVirtual)
            {
                continue;
            }

            bool bodyDoesFrameworkWork = MethodBodyHasMeaningfulStatements(methodDecl);
            if (bodyDoesFrameworkWork)
            {
                mustBeGuarded.Add(name);
            }
            else
            {
                emptyVirtuals.Add(name);
            }
        }

        // Assertion 1: every virtual method that performs framework work is in the guarded set.
        // If a future contributor adds a new virtual `void OnApplicationFocus()` body that does
        // framework work, this assertion fails until the guarded set, the consequence-text
        // dictionary, and the IL scanner GuardedMethodNames are all updated together.
        IEnumerable<string> shouldBeGuardedButIsNot = mustBeGuarded.Where(n =>
            !MessageAwareComponentBaseCallAnalyzer.AllowListIntentionallyUnguarded.Contains(n)
            && !ContainsOrdinal(GetGuardedMethodNamesViaReflection(), n)
        );
        Assert.That(
            shouldBeGuardedButIsNot,
            Is.Empty,
            "MessageAwareComponent declares virtual lifecycle method(s) with a non-empty body that the analyzer does not guard. "
                + "Add the method to GuardedMethodNames, populate MissingBaseCallMessageFormatsByMethod, "
                + "mirror in BaseCallTypeScannerCore.GuardedMethodNames + MissingBaseCallMessageFormatsByMethod, "
                + "OR add it to AllowListIntentionallyUnguarded if missing the base call is genuinely harmless. "
                + "Offenders: "
                + string.Join(", ", shouldBeGuardedButIsNot)
        );

        // Assertion 2: every method on the allow list is actually present on
        // MessageAwareComponent and has an empty (intentionally-no-op) body. This catches the
        // inverse drift: a refactor that renames or removes the OnApplicationQuit hook without
        // updating the allow list.
        foreach (
            string allowed in MessageAwareComponentBaseCallAnalyzer.AllowListIntentionallyUnguarded
        )
        {
            Assert.That(
                emptyVirtuals.Contains(allowed) || mustBeGuarded.Contains(allowed),
                Is.True,
                $"Allow-list entry '{allowed}' is no longer declared as a virtual lifecycle method on MessageAwareComponent; "
                    + "either remove it from AllowListIntentionallyUnguarded or restore the declaration."
            );
            Assert.That(
                mustBeGuarded.Contains(allowed),
                Is.False,
                $"Allow-list entry '{allowed}' has acquired a non-empty body and now performs framework work; "
                    + "remove from AllowListIntentionallyUnguarded and add to GuardedMethodNames + per-method consequence text."
            );
        }

        // Assertion 3: every guarded method must have a per-method consequence-text entry. This
        // is the safety net behind the fall-back-to-generic logic in AnalyzeMethodDeclaration:
        // a guarded method without consequence text would emit the generic message (still useful)
        // but signals incomplete authoring; we want this loud at test time.
        IEnumerable<string> guardedWithoutConsequenceText = GetGuardedMethodNamesViaReflection()
            .Where(n =>
                !MessageAwareComponentBaseCallAnalyzer.MissingBaseCallMessageFormatsByMethod.ContainsKey(
                    n
                )
            );
        Assert.That(
            guardedWithoutConsequenceText,
            Is.Empty,
            "Guarded method(s) lack a per-method consequence message in MissingBaseCallMessageFormatsByMethod. "
                + "Add an entry describing what breaks when base.<method>() is missed. Offenders: "
                + string.Join(", ", guardedWithoutConsequenceText)
        );
    }

    /// <summary>
    /// True if the method has a non-empty body that performs more than a single comment / empty
    /// statement. We use a lenient definition: a body containing any statement at all counts as
    /// "framework work" for the purposes of the guard contract. The only intentional zero-body
    /// virtuals on MessageAwareComponent are <c>OnApplicationQuit</c> (which the comment marks
    /// as "Intentionally left blank") and the demo handlers (which take a parameter and are
    /// therefore filtered out at the signature stage).
    /// </summary>
    private static bool MethodBodyHasMeaningfulStatements(MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody is not null)
        {
            return true;
        }
        if (method.Body is null)
        {
            return false;
        }
        return method.Body.Statements.Count > 0;
    }

    /// <summary>
    /// Reflects on the analyzer's <c>GuardedMethodNames</c> field via the
    /// InternalsVisibleTo bridge; the field is internal-static-readonly. Direct access
    /// (without reflection) is also possible because the field is internal and the test
    /// project sees it via InternalsVisibleTo; this helper kept the original meta-test
    /// resilient to a rename of the field. Falls back to hard-coded names if reflection
    /// fails (would only happen if the analyzer source rename breaks the field name; in
    /// that case the meta-test must be updated alongside).
    /// </summary>
    private static IReadOnlyCollection<string> GetGuardedMethodNamesViaReflection()
    {
        System.Reflection.FieldInfo? field = typeof(MessageAwareComponentBaseCallAnalyzer).GetField(
            "GuardedMethodNames",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        );
        if (field?.GetValue(null) is ImmutableHashSet<string> set)
        {
            return set;
        }
        return new[] { "Awake", "OnEnable", "OnDisable", "OnDestroy", "RegisterMessageHandlers" };
    }

    private static bool ContainsOrdinal(IReadOnlyCollection<string> set, string value)
    {
        foreach (string item in set)
        {
            if (string.Equals(item, value, System.StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Locates <c>Runtime/Unity/MessageAwareComponent.cs</c> by walking up from the test
    /// assembly's load directory until a sibling <c>Runtime</c> folder appears. Resilient to
    /// running in either <c>SourceGenerators/.../bin/Debug/...</c> or via <c>dotnet test</c>'s
    /// alternative output layout.
    /// </summary>
    private static string LocateRuntimeMessageAwareComponentSource()
    {
        string? current =
            Path.GetDirectoryName(
                typeof(MessageAwareComponentBaseCallAnalyzerTests).Assembly.Location
            ) ?? Directory.GetCurrentDirectory();
        for (int hop = 0; hop < 10 && current is not null; hop++)
        {
            string candidate = Path.Combine(
                current,
                "Runtime",
                "Unity",
                "MessageAwareComponent.cs"
            );
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = Path.GetDirectoryName(current);
        }
        // Final fallback: assume tests run from repo root.
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "Runtime",
            "Unity",
            "MessageAwareComponent.cs"
        );
    }

    // -- M2: parity tests between the analyzer's tables and the IL scanner's tables -----------

    /// <summary>
    /// The Roslyn analyzer (<see cref="MessageAwareComponentBaseCallAnalyzer"/>) and the IL
    /// scanner (<see cref="global::DxMessaging.Editor.Analyzers.BaseCallTypeScannerCore"/>) each carry
    /// their own copy of the per-method consequence dictionary because they live in different
    /// assemblies (Roslyn analyzers must be self-contained; the editor scanner cannot reference
    /// the analyzer DLL). Drift between the two would mean the inspector overlay HelpBox and
    /// the compile-time diagnostic say different things for the same method. This test asserts
    /// the two dictionaries are byte-for-byte equal so a single skill-page recommendation keeps
    /// both surfaces aligned.
    /// </summary>
    [Test]
    public void AnalyzerAndScannerCoreShareIdenticalMissingBaseCallMessageFormats()
    {
        IReadOnlyDictionary<string, string> analyzerFormats =
            MessageAwareComponentBaseCallAnalyzer.MissingBaseCallMessageFormatsByMethod;
        IReadOnlyDictionary<string, string> scannerFormats = global::DxMessaging
            .Editor
            .Analyzers
            .BaseCallTypeScannerCore
            .MissingBaseCallMessageFormatsByMethod;

        // Set equality on keys.
        HashSet<string> analyzerKeys = new(analyzerFormats.Keys, System.StringComparer.Ordinal);
        HashSet<string> scannerKeys = new(scannerFormats.Keys, System.StringComparer.Ordinal);
        Assert.That(
            analyzerKeys.SetEquals(scannerKeys),
            Is.True,
            "MissingBaseCallMessageFormatsByMethod key sets diverged. Analyzer-only keys: ["
                + string.Join(", ", analyzerKeys.Except(scannerKeys))
                + "]; Scanner-only keys: ["
                + string.Join(", ", scannerKeys.Except(analyzerKeys))
                + "]. Update both dictionaries together when adding or removing a guarded method."
        );

        // Byte-for-byte value equality per key (Ordinal).
        foreach (string key in analyzerKeys)
        {
            string analyzerValue = analyzerFormats[key];
            string scannerValue = scannerFormats[key];
            Assert.That(
                string.Equals(analyzerValue, scannerValue, System.StringComparison.Ordinal),
                Is.True,
                $"MissingBaseCallMessageFormatsByMethod['{key}'] diverged between analyzer and scanner.\n"
                    + $"  Analyzer: {analyzerValue}\n"
                    + $"  Scanner:  {scannerValue}\n"
                    + "Update both dictionaries to share the same per-method consequence text."
            );
        }
    }

    /// <summary>
    /// Same parity contract for the guarded-method name list: the analyzer's
    /// <c>GuardedMethodNames</c> immutable hash set must equal the scanner's
    /// <c>GuardedMethodNames</c> array (treated as a set). Adding a guarded method without
    /// updating both sides would mean the analyzer flags subclasses at compile time that the
    /// IL scanner never sees, or vice versa.
    /// </summary>
    [Test]
    public void AnalyzerAndScannerCoreShareIdenticalGuardedMethodNames()
    {
        IReadOnlyCollection<string> analyzerGuarded =
            MessageAwareComponentBaseCallAnalyzer.GuardedMethodNames;
        IReadOnlyCollection<string> scannerGuarded = global::DxMessaging
            .Editor
            .Analyzers
            .BaseCallTypeScannerCore
            .GuardedMethodNames;

        HashSet<string> analyzerSet = new(analyzerGuarded, System.StringComparer.Ordinal);
        HashSet<string> scannerSet = new(scannerGuarded, System.StringComparer.Ordinal);
        Assert.That(
            analyzerSet.SetEquals(scannerSet),
            Is.True,
            "GuardedMethodNames sets diverged. Analyzer-only: ["
                + string.Join(", ", analyzerSet.Except(scannerSet))
                + "]; Scanner-only: ["
                + string.Join(", ", scannerSet.Except(analyzerSet))
                + "]. Adding a guarded method requires updating both lists."
        );
    }
}
