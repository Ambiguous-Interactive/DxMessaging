using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DxMessaging.Editor.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Tests for the scanner-level classification logic that powers the inspector overlay's data
/// source.
/// </summary>
/// <remarks>
/// <para>
/// The Unity-coupled wrapper <c>BaseCallTypeScanner</c> lives behind <c>#if UNITY_EDITOR</c> and
/// cannot be loaded outside the Editor. The pure classification core
/// (<see cref="BaseCallTypeScannerCore"/>) is linked into this test project via
/// <c>&lt;Compile Include="..\..\Editor\Analyzers\BaseCallTypeScannerCore.cs" Link="..." /&gt;</c>
/// and tested via Roslyn-compiled in-memory fixtures (the same harness <c>BaseCallIlInspectorTests</c>
/// uses).
/// </para>
/// <para>
/// These tests pin the contracts the inspector overlay depends on:
/// </para>
/// <list type="bullet">
/// <item><description>DXMSG006 attribution when an override does not call base.</description></item>
/// <item><description>DXMSG007 attribution for <c>new</c>-shadowed lifecycle methods (and the
/// fact that DXMSG009 is conservatively classified the same way -- IL alone cannot distinguish
/// the two).</description></item>
/// <item><description>DXMSG010 attribution at the LEAF when an intermediate ancestor's override
/// fails to call base. Includes the four-level <c>Leaf : Middle : ddd : MAC</c> case to prove the
/// chain walk skips intermediate types that don't declare the slot directly.</description></item>
/// <item><description>Opt-out paths via class-level <c>[DxIgnoreMissingBaseCall]</c> and via the
/// project-level ignored-types list.</description></item>
/// <item><description>Skipping abstract types and generic-type definitions (they aren't
/// instantiable so their override shape doesn't matter to the runtime overlay).</description></item>
/// <item><description>FQN normalisation (<c>Outer+Nested</c>  to  <c>Outer.Nested</c>) so the
/// snapshot key matches what the analyzer emits.</description></item>
/// <item><description>Independence across types -- two broken types both appear in the snapshot.</description></item>
/// <item><description>Healthy chains report nothing (no false positives).</description></item>
/// </list>
/// </remarks>
[TestFixture]
internal sealed class BaseCallTypeScannerTests
{
    // ---- Per-classification tests -----------------------------------------------------------

    [Test]
    public void ScanOverrideWithoutBaseReportsDxmsg006AndAddsMethodToMissingBaseFor()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class Broken : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // No base call.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("Broken"));
        BaseCallTypeScannerCore.ScanEntry entry = snapshot["Broken"];
        Assert.That(entry.MissingBaseFor, Is.EquivalentTo(expected));
        Assert.That(entry.DiagnosticIds, Is.EquivalentTo(expectedArray));
    }

    [Test]
    public void ScanOverrideWithBaseReportsNothing()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class Clean : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    base.OnEnable();
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Is.Empty);
    }

    [Test]
    public void ScanNoModifierOnGuardedNameReportsHidingDiagnostic()
    {
        // Acceptance contract: declaring a same-named lifecycle method without override or new
        // (C# CS0114) compiles to the same IL shape as `new void X()`. The scanner's IL-only
        // probe cannot distinguish DXMSG007 from DXMSG009; it conservatively classifies as
        // DXMSG007. The compile-time analyzer is authoritative for the precise ID. This test
        // pins the conservative-classification contract; if a future scanner gains semantic
        // insight, the assertion below should be updated alongside the doc note.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class ImplicitHider : MessageAwareComponent
            {
                protected void OnEnable()
                {
                    // No `override`, no `new`. C# emits CS0114; the analyzer would emit DXMSG009.
                    // The scanner classifies as DXMSG007 because the IL is indistinguishable.
                }
            }
            """,
            "CS0114"
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("ImplicitHider"));
        BaseCallTypeScannerCore.ScanEntry entry = snapshot["ImplicitHider"];
        Assert.That(entry.MissingBaseFor, Is.EquivalentTo(expected));
        Assert.That(
            entry.DiagnosticIds,
            Is.EquivalentTo(expectedArray0),
            "Scanner should conservatively classify DXMSG009 as DXMSG007 (IL ambiguity)."
        );
    }

    [Test]
    public void ScanExplicitNewOnGuardedNameReportsDxmsg007()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class ExplicitHider : MessageAwareComponent
            {
                protected new void OnEnable()
                {
                    // Hides via explicit `new`.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("ExplicitHider"));
        BaseCallTypeScannerCore.ScanEntry entry = snapshot["ExplicitHider"];
        Assert.That(entry.MissingBaseFor, Is.EquivalentTo(expectedArray1));
        Assert.That(entry.DiagnosticIds, Is.EquivalentTo(expectedArray0));
    }

    [Test]
    public void ScanBrokenIntermediateReportsDxmsg010OnLeaf()
    {
        // The user's canonical `BrokenThing : ddd : MessageAwareComponent` case: ddd's override
        // does not call base, BrokenThing's override does. The leaf is the type the user is
        // actively editing, so DXMSG010 should land on BrokenThing; not on ddd, which gets its
        // own DXMSG006 row.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class ddd : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // No base call; chain dies here.
                }
            }

            public class BrokenThing : ddd
            {
                protected override void OnEnable()
                {
                    base.OnEnable();
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("ddd"));
        Assert.That(snapshot["ddd"].DiagnosticIds, Is.EquivalentTo(expectedArray));

        Assert.That(snapshot, Contains.Key("BrokenThing"));
        Assert.That(
            snapshot["BrokenThing"].DiagnosticIds,
            Is.EquivalentTo(expectedArray2),
            "DXMSG010 must land on the leaf (the type the user is editing)."
        );
        Assert.That(snapshot["BrokenThing"].MissingBaseFor, Is.EquivalentTo(expectedArray1));
    }

    [Test]
    public void ScanChainSkippingMiddleTypeReportsDxmsg010OnLeaf()
    {
        // Four-level chain: BrokenThing : Middle : ddd : MessageAwareComponent. Middle does NOT
        // declare OnEnable, but ddd's override is broken. BrokenThing calls base correctly. The
        // chain walker's GetOverriddenMethod must walk PAST Middle (which doesn't declare the
        // slot directly) to find ddd's broken override. If the walker stopped at Middle without
        // finding the method on it, we would report nothing on BrokenThing; a missed DXMSG010.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class ddd : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // Broken; no base call.
                }
            }

            public class Middle : ddd
            {
                // Intentionally does not declare OnEnable.
                public int _unused;
            }

            public class BrokenThing : Middle
            {
                protected override void OnEnable()
                {
                    base.OnEnable();
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("ddd"));
        Assert.That(snapshot["ddd"].DiagnosticIds, Is.EquivalentTo(expectedArray3));

        // Middle declares neither override nor new → no entry.
        Assert.That(snapshot, Does.Not.ContainKey("Middle"));

        Assert.That(snapshot, Contains.Key("BrokenThing"));
        Assert.That(
            snapshot["BrokenThing"].DiagnosticIds,
            Is.EquivalentTo(expectedArray2),
            "Chain walker must skip Middle and detect ddd's broken override."
        );
    }

    [Test]
    public void ScanClassLevelDxIgnoreMissingBaseCallAttributeExcludesFromSnapshot()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;
            using DxMessaging.Core.Attributes;

            [DxIgnoreMissingBaseCall]
            public class IgnoredBroken : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // Broken, but opted out.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Is.Empty);
    }

    private static readonly string[] ignoredTypeNames = new[] { "ProjectIgnoredBroken" };
    private static readonly string[] expected = new[] { "OnEnable" };
    private static readonly string[] expectedArray = new[] { "DXMSG006" };
    private static readonly string[] expectedArray0 = new[] { "DXMSG007" };
    private static readonly string[] expectedArray1 = new[] { "OnEnable" };
    private static readonly string[] expectedArray2 = new[] { "DXMSG010" };
    private static readonly string[] expectedArray3 = new[] { "DXMSG006" };
    private static readonly string[] expectedArray4 = new[] { "OnEnable" };
    private static readonly string[] expectedArray5 = new[] { "OnDisable" };
    private static readonly string[] expectedArray6 = new[] { "OnApplicationFocus" };
    private static readonly string[] expectedArray7 = new[] { "DXMSG006" };
    private static readonly string[] expectedArray8 = new[] { "OnApplicationPause" };
    private static readonly string[] expectedArray9 = new[] { "DXMSG007" };
    private static readonly string[] expectedArray10 = new[] { "DXMSG010" };
    private static readonly string[] expectedArray11 = new[] { "Awake", "OnEnable" };
    private static readonly string[] expectedArray12 = new[] { "DXMSG006" };

    [Test]
    public void ScanTypeInProjectIgnoreListExcludesFromSnapshot()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class ProjectIgnoredBroken : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // Broken, but opted out at the project level.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), ignoredTypeNames);

        Assert.That(snapshot, Is.Empty);
    }

    [Test]
    public void ScanAbstractTypeIsSkipped()
    {
        // Abstract subclasses cannot exist as MonoBehaviour instances, so the inspector overlay
        // never shows their HelpBox. The scanner should not include them in the snapshot even if
        // they technically have a broken override.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public abstract class AbstractBroken : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // Broken; but abstract types are skipped.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Is.Empty);
    }

    [Test]
    public void ScanGenericTypeDefinitionIsSkipped()
    {
        // Open generic-type definitions cannot be instantiated as MonoBehaviour components.
        // Closed generic instantiations would be classified separately; but the open definition
        // itself is a TypeCache artifact we should not surface.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class GenericBroken<T> : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // Broken; but the generic-type-definition is skipped.
                }
            }
            """
        );

        // Feed in only the generic-type-definition (no closed instantiation exists).
        IEnumerable<Type> candidates = fixture.GetTypes().Where(t => t.IsGenericTypeDefinition);

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(candidates, null);

        Assert.That(snapshot, Is.Empty);
    }

    [Test]
    public void ScanSkipsMessageAwareComponentWhenCandidateListIncludesBaseType()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class BrokenLeaf : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // No base call.
                }
            }
            """
        );

        Type messageAwareComponent = fixture.GetType("DxMessaging.Unity.MessageAwareComponent")!;
        Type brokenLeaf = fixture.GetType("BrokenLeaf")!;

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(new[] { messageAwareComponent, brokenLeaf }, null);

        Assert.That(snapshot, Does.Not.ContainKey("DxMessaging.Unity.MessageAwareComponent"));
        Assert.That(snapshot, Contains.Key("BrokenLeaf"));
    }

    [Test]
    public void ScanNestedTypeFqnUsesDotsNotPlusSign()
    {
        // System.Type.FullName for nested types uses '+' as the separator (e.g.
        // "Outer+Nested"); the analyzer emits the dotted form so the inspector overlay can
        // round-trip the FQN through the JSON cache and reflect on it as a CSharp identifier.
        // The scanner must normalise '+' → '.' so its snapshot key matches the analyzer.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class Outer
            {
                public class Nested : MessageAwareComponent
                {
                    protected override void OnEnable()
                    {
                        // Broken to ensure the entry is produced.
                    }
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(
            snapshot,
            Contains.Key("Outer.Nested"),
            "Nested type FQN must use '.' (analyzer form), not '+' (Reflection form)."
        );
        Assert.That(
            snapshot,
            Does.Not.ContainKey("Outer+Nested"),
            "Plus-form FQN must not appear in the snapshot."
        );
    }

    [Test]
    public void ScanTwoTypesWithSameMethodIssuesBothInSnapshot()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class FirstBroken : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // No base call.
                }
            }

            public class SecondBroken : MessageAwareComponent
            {
                protected override void OnDisable()
                {
                    // No base call.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("FirstBroken"));
        Assert.That(snapshot, Contains.Key("SecondBroken"));
        Assert.That(snapshot["FirstBroken"].MissingBaseFor, Is.EquivalentTo(expectedArray4));
        Assert.That(snapshot["SecondBroken"].MissingBaseFor, Is.EquivalentTo(expectedArray5));
    }

    [Test]
    public void ScanMethodLevelDxIgnoreMissingBaseCallAttributeSuppressesOnlyAnnotatedMethod()
    {
        // A method-level suppression applies only to that annotated guarded method. Other
        // broken methods on the same type must still appear in the snapshot.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;
            using DxMessaging.Core.Attributes;

            public class PartiallyIgnored : MessageAwareComponent
            {
                [DxIgnoreMissingBaseCall]
                protected override void OnEnable()
                {
                    // Broken, but explicitly suppressed at the method level.
                }

                protected override void OnDisable()
                {
                    // Broken and not suppressed.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("PartiallyIgnored"));
        BaseCallTypeScannerCore.ScanEntry entry = snapshot["PartiallyIgnored"];
        Assert.That(entry.MissingBaseFor, Is.EquivalentTo(expectedArray5));
        Assert.That(entry.DiagnosticIds, Is.EquivalentTo(expectedArray3));
    }

    [Test]
    public void ScanAllBrokenMethodsSuppressedByMethodLevelAttributesProducesNoEntry()
    {
        // If every broken method is method-level suppressed, the scanner should produce no row
        // because MissingBaseFor is empty after suppression filtering.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;
            using DxMessaging.Core.Attributes;

            public class AllSuppressed : MessageAwareComponent
            {
                [DxIgnoreMissingBaseCall]
                protected override void OnEnable()
                {
                    // Broken, but explicitly suppressed.
                }

                [DxIgnoreMissingBaseCall]
                protected override void OnDisable()
                {
                    // Broken, but explicitly suppressed.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Does.Not.ContainKey("AllSuppressed"));
    }

    [Test]
    public void ScanBoolSignatureOverrideWithoutBaseReportsDxmsg006()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class FocusBase : MessageAwareComponent
            {
                protected virtual void OnApplicationFocus(bool focused) { }
            }

            public class BrokenFocusLeaf : FocusBase
            {
                protected override void OnApplicationFocus(bool focused)
                {
                    // No base call.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("BrokenFocusLeaf"));
        BaseCallTypeScannerCore.ScanEntry entry = snapshot["BrokenFocusLeaf"];
        Assert.That(entry.MissingBaseFor, Is.EquivalentTo(expectedArray6));
        Assert.That(entry.DiagnosticIds, Is.EquivalentTo(expectedArray7));
    }

    [Test]
    public void ScanBoolSignatureHidingWithNewReportsDxmsg007()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class PauseBase : MessageAwareComponent
            {
                protected virtual void OnApplicationPause(bool paused) { }
            }

            public class HiddenPauseLeaf : PauseBase
            {
                protected new void OnApplicationPause(bool paused)
                {
                    // Hides via explicit `new`.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("HiddenPauseLeaf"));
        BaseCallTypeScannerCore.ScanEntry entry = snapshot["HiddenPauseLeaf"];
        Assert.That(entry.MissingBaseFor, Is.EquivalentTo(expectedArray8));
        Assert.That(entry.DiagnosticIds, Is.EquivalentTo(expectedArray9));
    }

    [Test]
    public void ScanBoolSignatureBrokenIntermediateReportsDxmsg010OnLeaf()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class FocusRoot : MessageAwareComponent
            {
                protected virtual void OnApplicationFocus(bool focused) { }
            }

            public class BrokenFocusMiddle : FocusRoot
            {
                protected override void OnApplicationFocus(bool focused)
                {
                    // No base call; chain breaks here.
                }
            }

            public class FocusLeaf : BrokenFocusMiddle
            {
                protected override void OnApplicationFocus(bool focused)
                {
                    base.OnApplicationFocus(focused);
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Contains.Key("BrokenFocusMiddle"));
        Assert.That(snapshot["BrokenFocusMiddle"].DiagnosticIds, Is.EquivalentTo(expectedArray7));

        Assert.That(snapshot, Contains.Key("FocusLeaf"));
        Assert.That(snapshot["FocusLeaf"].DiagnosticIds, Is.EquivalentTo(expectedArray10));
        Assert.That(snapshot["FocusLeaf"].MissingBaseFor, Is.EquivalentTo(expectedArray6));
    }

    [Test]
    public void ScanBoolSignatureMethodLevelIgnoreAttributeSuppressesAnnotatedMethod()
    {
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;
            using DxMessaging.Core.Attributes;

            public class FocusBase : MessageAwareComponent
            {
                protected virtual void OnApplicationFocus(bool focused) { }
            }

            public class FocusIgnoredLeaf : FocusBase
            {
                [DxIgnoreMissingBaseCall]
                protected override void OnApplicationFocus(bool focused)
                {
                    // Broken but explicitly suppressed.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Does.Not.ContainKey("FocusIgnoredLeaf"));
    }

    [Test]
    public void ScanTwoBrokenMethodsOnSameTypeFoldedIntoSingleEntry()
    {
        // Spec 2b: a single type with TWO broken overrides (Awake AND OnEnable) must produce
        // exactly ONE entry whose MissingBaseFor lists both methods. DiagnosticIds is the
        // deduplicated union (DXMSG006 once even though both methods contribute it).
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class BrokenThing : MessageAwareComponent
            {
                protected override void Awake()
                {
                    // No base call.
                }

                protected override void OnEnable()
                {
                    // No base call.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Has.Count.EqualTo(1));
        Assert.That(snapshot, Contains.Key("BrokenThing"));
        BaseCallTypeScannerCore.ScanEntry entry = snapshot["BrokenThing"];
        Assert.That(
            entry.MissingBaseFor,
            Is.EquivalentTo(expectedArray11),
            "Both broken methods must appear in MissingBaseFor on a single entry."
        );
        Assert.That(
            entry.DiagnosticIds,
            Is.EquivalentTo(expectedArray12),
            "DXMSG006 must be deduped to a single id even though both methods contribute it."
        );
    }

    [Test]
    public void ScanNullSettingsTreatsOptOutListAsEmptyNoNullReferenceException()
    {
        // Spec 2e: passing null for ignoredTypeNames must be treated as an empty opt-out list and
        // must not throw. This pins the defensive null-handling at the API boundary.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class BrokenLeaf : MessageAwareComponent
            {
                protected override void OnEnable()
                {
                    // No base call.
                }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry>? snapshot = null;
        Assert.DoesNotThrow(() =>
        {
            snapshot = BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);
        });
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!, Contains.Key("BrokenLeaf"));
    }

    [Test]
    public void ScanOnExternMethodTreatedAsCleanCrossAssembly()
    {
        // Spec 2d: a concrete MessageAwareComponent subclass whose override is extern (no IL body)
        // must be treated as assume-clean. GetMethodBody() returns null just like it does for
        // cross-assembly closed-source code; the scanner's defensive bias means no diagnostic is
        // emitted. CS0626 is intrinsic to this exact no-body fixture and is the sole allowed
        // compiler warning.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class ExternLeaf : MessageAwareComponent
            {
                protected override extern void OnEnable();
            }
            """,
            "CS0626"
        );

        // Sanity-check the precondition: the method has no body.
        Type leaf = fixture.GetType("ExternLeaf")!;
        MethodInfo? externMethod = leaf.GetMethod(
            "OnEnable",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null,
            Type.EmptyTypes,
            null
        );
        Assert.That(externMethod, Is.Not.Null);
        Assert.That(externMethod!.GetMethodBody(), Is.Null);

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        // The IL inspector returns true (assume clean) when the body is null. The scanner records
        // an entry only when MissingBaseFor is non-empty; so an assume-clean override produces
        // no entry.
        Assert.That(
            snapshot,
            Does.Not.ContainKey("ExternLeaf"),
            "Extern (no IL body) override must be treated as assume-clean and produce no entry."
        );
    }

    [Test]
    public void ScanHealthyChainReportsNothing()
    {
        // Three-deep healthy chain: every link calls base, no DXMSG006 / DXMSG010 should fire.
        Assembly fixture = CompileFixture(
            """
            using DxMessaging.Unity;

            public class HealthyA : MessageAwareComponent
            {
                protected override void OnEnable() { base.OnEnable(); }
            }

            public class HealthyB : HealthyA
            {
                protected override void OnEnable() { base.OnEnable(); }
            }

            public class HealthyC : HealthyB
            {
                protected override void OnEnable() { base.OnEnable(); }
            }
            """
        );

        Dictionary<string, BaseCallTypeScannerCore.ScanEntry> snapshot =
            BaseCallTypeScannerCore.Scan(EnumerateMacSubclasses(fixture), null);

        Assert.That(snapshot, Is.Empty);
    }

    // ---- Compilation harness ---------------------------------------------------------------

    /// <summary>
    /// Enumerate every concrete + abstract type in the fixture that derives (transitively) from
    /// the stub <c>MessageAwareComponent</c>. The scanner's own filtering (abstract /
    /// generic-definition skipping, MAC-itself skipping, FQN normalisation) is the contract under
    /// test, so we feed in a deliberately permissive candidate set.
    /// </summary>
    private static IEnumerable<Type> EnumerateMacSubclasses(Assembly fixture)
    {
        Type mac = fixture.GetType("DxMessaging.Unity.MessageAwareComponent")!;
        return fixture.GetTypes().Where(t => t != mac && mac.IsAssignableFrom(t));
    }

    private static Assembly CompileFixture(string userSource, params string[] allowedWarningIds)
    {
        // Build a self-contained assembly that defines a MessageAwareComponent stub plus the
        // user's classes on top. The stub's chain terminator FQN ("DxMessaging.Unity.MessageAware
        // Component") is the literal string the Core checks for to terminate the chain walk;
        // keep them in lock-step.
        const string Stubs = """
namespace UnityEngine
{
    public class MonoBehaviour { }
}

namespace DxMessaging.Core.Attributes
{
    using System;

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class DxIgnoreMissingBaseCallAttribute : System.Attribute { }
}

namespace DxMessaging.Unity
{
    using UnityEngine;

    public class MessageAwareComponent : MonoBehaviour
    {
        protected virtual void Awake() { }
        protected virtual void OnEnable() { }
        protected virtual void OnDisable() { }
        protected virtual void OnDestroy() { }
        protected virtual void RegisterMessageHandlers() { }
    }
}
""";
        SyntaxTree stubs = CSharpSyntaxTree.ParseText(Stubs);
        SyntaxTree user = CSharpSyntaxTree.ParseText(userSource);

        List<MetadataReference> references = new()
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        // Ensure System.Runtime is loaded; required for MetadataReference resolution on net9.0.
        Assembly runtime = Assembly.Load("System.Runtime");
        if (!string.IsNullOrEmpty(runtime.Location))
        {
            references.Add(MetadataReference.CreateFromFile(runtime.Location));
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "BaseCallTypeScannerCoreFixture_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: new[] { stubs, user },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        using MemoryStream stream = new();
        EmitResult emit = compilation.Emit(stream);
        Diagnostic[] blockingDiagnostics = emit
            .Diagnostics.Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error
                || (
                    diagnostic.Severity == DiagnosticSeverity.Warning
                    && !allowedWarningIds.Contains(diagnostic.Id, StringComparer.Ordinal)
                )
            )
            .ToArray();
        if (blockingDiagnostics.Length != 0)
        {
            string errors = string.Join("\n", blockingDiagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException(
                $"Test fixture failed to compile:\n{errors}\n\nUser source:\n{userSource}"
            );
        }

        string[] emittedWarningIds = emit
            .Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
            .Select(diagnostic => diagnostic.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        string[] expectedWarningIds = allowedWarningIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (!emittedWarningIds.SequenceEqual(expectedWarningIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fixture warning allowlist drifted. Expected [{string.Join(", ", expectedWarningIds)}], emitted [{string.Join(", ", emittedWarningIds)}]."
            );
        }

        stream.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(stream.ToArray());
    }
}
