using System;
using System.Collections.Generic;
using System.Linq;
using DxMessaging.Editor.Analyzers;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Regression coverage for the dual-source console harvester. Two layers are covered here:
/// <list type="number">
/// <item><description>The Unity 2021 CompilerMessage parse path (fed through
/// <see cref="BaseCallLogMessageParser.Aggregate"/>) -- the wire format the harvester sees on
/// 2021 builds.</description></item>
/// <item><description>The per-assembly merge + retirement bookkeeping in
/// <see cref="BaseCallReportAggregator"/> -- the most novel slice of the dual-source design and
/// the part most likely to silently corrupt the snapshot if regressed.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The harvester itself lives in the Editor assembly (which dotnet-test cannot load -- it depends
/// on UnityEditor types), so we test the slices that ARE pure: feeding synthetic
/// `CompilerMessage`-shaped log strings through the parser, and exercising the aggregator
/// directly via its public static API.
/// </remarks>
[TestFixture]
internal sealed class CompilationMessageHarvestTests
{
    // Verbatim shape of what `CompilerMessage.message` carries on Unity 2021 for a Roslyn
    // analyzer warning. The harvester's prefilter only checks for the substring "DXMSG00".
    private const string Unity2021Dxmsg009 =
        "Assets/Sample/BrokenThing.cs(12,21): warning DXMSG009: 'Sample.BrokenThing' declares OnEnable without 'override' or 'new'; "
        + "this implicitly hides MessageAwareComponent.OnEnable (CS0114) and the messaging system will not function. "
        + "Add 'override' and call base.OnEnable(), or add 'new' if the hiding is intentional.";

    private const string Unity2021Dxmsg006 =
        "Assets/Sample/Player.cs(8,29): warning DXMSG006: 'Sample.Player' overrides MessageAwareComponent.Awake but does not call base.Awake(); "
        + "the messaging system may not function correctly on this component.";

    private const string Unity2021Dxmsg007 =
        "Assets/Sample/Player.cs(15,21): warning DXMSG007: 'Sample.Player' hides MessageAwareComponent.OnEnable with 'new'; "
        + "replace with 'override' and call base.OnEnable() so the messaging system continues to function.";
    private static readonly string[] expected = new[] { "OnEnable" };
    private static readonly string[] expectedArray = new[] { "Awake", "OnEnable" };
    private static readonly string[] expectedArray0 = new[] { "DXMSG006", "DXMSG007" };
    private static readonly string[] stringArray = new[] { "Awake" };
    private static readonly string[] expectedArray1 = new[] { "Sample.Player" };
    private static readonly string[] expectedArray2 = new[] { "Awake" };
    private static readonly string[] stringArray0 = new[] { "Awake" };
    private static readonly string[] expectedArray3 = new[] { "X" };
    private static readonly string[] stringArray1 = new[] { "Awake" };
    private static readonly string[] stringArray2 = new[] { "OnEnable" };
    private static readonly string[] stringArray3 = new[] { "Awake" };
    private static readonly string[] stringArray4 = new[] { "Awake" };
    private static readonly string[] expectedArray4 = new[] { "DXMSG006" };
    private static readonly string[] expectedArray5 = new[] { "DXMSG009" };
    private static readonly string[] stringArray5 = new[] { "Awake" };
    private static readonly string[] stringArray6 = new[] { "Awake" };
    private static readonly string[] stringArray7 = new[] { "OnEnable" };
    private static readonly string[] expectedArray6 = new[] { "Awake", "OnEnable" };
    private static readonly string[] expectedArray7 = new[] { "DXMSG006", "DXMSG009" };
    private static readonly string[] stringArray8 = new[] { "OnEnable" };
    private static readonly string[] stringArray9 = new[] { "OnDisable" };
    private static readonly string[] stringArray10 = new[] { "OnDestroy" };
    private static readonly string[] expectedArray8 = new[] { "W", "X", "Y", "Z" };
    private static readonly string[] expectedArray9 = new[] { "W", "Y", "Z" };
    private static readonly string[] expectedArray10 = new[] { "Y" };
    private static readonly string[] expectedArray11 = new[] { "Y", "Z" };
    private static readonly string[] expectedArray12 = new[] { "W" };
    private static readonly string[] stringArray11 = new[] { "OnEnable" };
    private static readonly string[] expectedArray13 = new[] { "Sample.Y" };
    private static readonly string[] stringArray12 = new[] { "Awake" };

    [Test]
    public void AggregateOnUnity2021Dxmsg009LineProducesEntryWithFilePathAndLine()
    {
        Dictionary<string, ParsedTypeReport> aggregated = BaseCallLogMessageParser.Aggregate(
            new[] { Unity2021Dxmsg009 }
        );

        Assert.That(aggregated, Has.Count.EqualTo(1));
        Assert.That(aggregated.ContainsKey("Sample.BrokenThing"), Is.True);
        ParsedTypeReport report = aggregated["Sample.BrokenThing"];
        Assert.That(report.MissingBaseFor, Is.EquivalentTo(expected));
        Assert.That(report.DiagnosticIds, Contains.Item("DXMSG009"));
        Assert.That(report.FilePath, Is.EqualTo("Assets/Sample/BrokenThing.cs"));
        Assert.That(report.Line, Is.EqualTo(12));
    }

    [Test]
    public void AggregateOnMixedDiagnosticsForSameTypeDedupesMethodsAndUnionsIds()
    {
        // Player.cs raises both a DXMSG006 (override missing base) on Awake and a DXMSG007
        // (new hides) on OnEnable. The same type FQN appears in both, so the per-type report
        // should fold both methods into one entry while keeping both diagnostic ids.
        Dictionary<string, ParsedTypeReport> aggregated = BaseCallLogMessageParser.Aggregate(
            new[] { Unity2021Dxmsg006, Unity2021Dxmsg007 }
        );

        Assert.That(aggregated, Has.Count.EqualTo(1));
        ParsedTypeReport report = aggregated["Sample.Player"];
        Assert.That(report.MissingBaseFor, Is.EquivalentTo(expectedArray));
        Assert.That(report.DiagnosticIds, Is.EquivalentTo(expectedArray0));
        // First-occurrence file path is stable so "Open Script" jumps to the first reported
        // location; which is what the user's eye lands on first in the console.
        Assert.That(report.FilePath, Is.EqualTo("Assets/Sample/Player.cs"));
        Assert.That(report.Line, Is.EqualTo(8));
    }

    [Test]
    public void AggregateDropsLinesWithoutAnyDxmsgPrefix()
    {
        // The harvester's hot-path filter on `OnAssemblyCompilationFinished` skips lines that
        // don't contain "DXMSG00" before parsing. The parser itself must also be tolerant of
        // unrelated lines (the LogEntries scan path doesn't pre-filter as aggressively).
        Dictionary<string, ParsedTypeReport> aggregated = BaseCallLogMessageParser.Aggregate(
            new[]
            {
                "Assets/Foo.cs(1,1): warning CS0162: Unreachable code detected",
                "[BUILD] Cooked some assets in 12.3s",
                Unity2021Dxmsg009,
            }
        );

        Assert.That(aggregated, Has.Count.EqualTo(1));
        Assert.That(aggregated.ContainsKey("Sample.BrokenThing"), Is.True);
    }

    [Test]
    public void AggregateOnEmptyInputReturnsEmptyDictionary()
    {
        // The harvester calls Aggregate even when an assembly produced zero matching messages;
        // the empty result is then used by ApplyCompilerMessageDrain to RETIRE the previous
        // attribution for that assembly. Stable empty handling is load-bearing for that flow.
        Dictionary<string, ParsedTypeReport> aggregated = BaseCallLogMessageParser.Aggregate(
            Array.Empty<string>()
        );

        Assert.That(aggregated, Is.Empty);
    }

    [Test]
    public void AggregateOnNullInputReturnsEmptyDictionary()
    {
        Dictionary<string, ParsedTypeReport> aggregated = BaseCallLogMessageParser.Aggregate(null);

        Assert.That(aggregated, Is.Empty);
    }

    // -- BaseCallReportAggregator.ApplyAssemblyReports tests
    // -------------------------------------------------------
    // These exercise the merge + retirement contract directly. They're the single most novel
    // slice of the dual-source design and have failed repeatedly in adversarial review; locking
    // them in with deterministic dotnet-test coverage closes the gap.

    [Test]
    public void ApplyAssemblyReportsNewTypeAddedToBoth()
    {
        Dictionary<string, HashSet<string>> typesByAssembly = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ParsedTypeReport> mergedReports = new(StringComparer.Ordinal);

        Dictionary<string, ParsedTypeReport> reports = MakeReports(
            ("Sample.Player", stringArray, "DXMSG006", "Assets/Player.cs", 8)
        );

        BaseCallReportAggregator.ApplyAssemblyReports(
            "Sample.dll",
            reports,
            typesByAssembly,
            mergedReports
        );

        Assert.That(typesByAssembly.ContainsKey("Sample.dll"), Is.True);
        Assert.That(typesByAssembly["Sample.dll"], Is.EquivalentTo(expectedArray1));
        Assert.That(mergedReports, Has.Count.EqualTo(1));
        Assert.That(mergedReports.ContainsKey("Sample.Player"), Is.True);
        Assert.That(mergedReports["Sample.Player"].MissingBaseFor, Is.EquivalentTo(expectedArray2));
        Assert.That(mergedReports["Sample.Player"].FilePath, Is.EqualTo("Assets/Player.cs"));
        Assert.That(mergedReports["Sample.Player"].Line, Is.EqualTo(8));
    }

    [Test]
    public void ApplyAssemblyReportsRecompileSameAssemblyDropsRetiredTypes()
    {
        // Assembly A reports type X with method Awake. The user fixes the issue and recompiles;
        // A's next batch is empty. X must be removed from BOTH mergedReports AND
        // typesByAssembly[A] (otherwise the inspector shows a phantom HelpBox for a fixed type).
        Dictionary<string, HashSet<string>> typesByAssembly = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ParsedTypeReport> mergedReports = new(StringComparer.Ordinal);

        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            MakeReports(("X", stringArray, "DXMSG006", "X.cs", 1)),
            typesByAssembly,
            mergedReports
        );
        Assume.That(
            mergedReports.ContainsKey("X"),
            Is.True,
            "Precondition: X must be in the merged map after first apply."
        );

        // Re-call without X.
        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal),
            typesByAssembly,
            mergedReports
        );

        Assert.That(mergedReports, Is.Empty, "X must be retired from mergedReports.");
        Assert.That(
            typesByAssembly["A.dll"],
            Is.Empty,
            "A.dll's FQN set must drop X after the empty recompile."
        );
    }

    [Test]
    public void ApplyAssemblyReportsTwoAssembliesReportSameTypeRetainAfterOneDrops()
    {
        // Cross-assembly survival: A and B both report type X (e.g., partial classes split across
        // assemblies, or duplicate type-name across modules). When A re-compiles without X, X
        // must SURVIVE in mergedReports because B still claims it. Then when B drops X, X must
        // disappear.
        Dictionary<string, HashSet<string>> typesByAssembly = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ParsedTypeReport> mergedReports = new(StringComparer.Ordinal);

        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            MakeReports(("X", stringArray0, "DXMSG006", "A/X.cs", 5)),
            typesByAssembly,
            mergedReports
        );
        BaseCallReportAggregator.ApplyAssemblyReports(
            "B.dll",
            MakeReports(("X", stringArray0, "DXMSG006", "B/X.cs", 9)),
            typesByAssembly,
            mergedReports
        );
        Assume.That(mergedReports.ContainsKey("X"), Is.True);

        // A drops X.
        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal),
            typesByAssembly,
            mergedReports
        );

        Assert.That(
            mergedReports.ContainsKey("X"),
            Is.True,
            "X must survive while B still reports it."
        );
        Assert.That(typesByAssembly["A.dll"], Is.Empty);
        Assert.That(typesByAssembly["B.dll"], Is.EquivalentTo(expectedArray3));

        // Now B drops X too.
        BaseCallReportAggregator.ApplyAssemblyReports(
            "B.dll",
            new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal),
            typesByAssembly,
            mergedReports
        );

        Assert.That(mergedReports, Is.Empty, "X must be retired once both assemblies drop it.");
        Assert.That(typesByAssembly["B.dll"], Is.Empty);
    }

    [Test]
    public void ApplyAssemblyReportsDifferentMethodsOnSameTypeAcrossAssemblies()
    {
        // A reports X.Awake; B reports X.OnEnable. The merged view must carry both methods on a
        // single X entry; this is the partial-class / split-assembly case.
        Dictionary<string, HashSet<string>> typesByAssembly = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ParsedTypeReport> mergedReports = new(StringComparer.Ordinal);

        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            MakeReports(("X", stringArray1, "DXMSG006", "A/X.cs", 5)),
            typesByAssembly,
            mergedReports
        );
        BaseCallReportAggregator.ApplyAssemblyReports(
            "B.dll",
            MakeReports(("X", stringArray2, "DXMSG006", "B/X.cs", 9)),
            typesByAssembly,
            mergedReports
        );

        Assert.That(mergedReports.ContainsKey("X"), Is.True);
        Assert.That(mergedReports["X"].MissingBaseFor, Is.EquivalentTo(expectedArray));
        // First-seen file path wins so the "Open Script" jump is stable.
        Assert.That(mergedReports["X"].FilePath, Is.EqualTo("A/X.cs"));
        Assert.That(mergedReports["X"].Line, Is.EqualTo(5));
    }

    [Test]
    public void ApplyAssemblyReportsUnknownAssemblyKeyDoesNotDisturbExistingState()
    {
        // Sanity check: applying an empty batch for an assembly we've never seen leaves the
        // merged map untouched. A common refresh path on Unity 2021 is "every assembly fires
        // assemblyCompilationFinished, even ones with no warnings"; those calls must not
        // accidentally zero out the snapshot.
        Dictionary<string, HashSet<string>> typesByAssembly = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ParsedTypeReport> mergedReports = new(StringComparer.Ordinal);

        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            MakeReports(("X", stringArray1, "DXMSG006", "A/X.cs", 5)),
            typesByAssembly,
            mergedReports
        );

        BaseCallReportAggregator.ApplyAssemblyReports(
            "Empty.dll",
            new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal),
            typesByAssembly,
            mergedReports
        );

        Assert.That(mergedReports.ContainsKey("X"), Is.True);
        Assert.That(typesByAssembly.ContainsKey("Empty.dll"), Is.True);
        Assert.That(typesByAssembly["Empty.dll"], Is.Empty);
    }

    [Test]
    public void ApplyAssemblyReportsNullArgumentsThrowOrTreatNullPayloadAsRetirement()
    {
        Dictionary<string, HashSet<string>> typesByAssembly = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ParsedTypeReport> mergedReports = new(StringComparer.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            BaseCallReportAggregator.ApplyAssemblyReports(
                string.Empty,
                new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal),
                typesByAssembly,
                mergedReports
            )
        );
        Assert.Throws<ArgumentNullException>(() =>
            BaseCallReportAggregator.ApplyAssemblyReports(
                "A.dll",
                new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal),
                null!,
                mergedReports
            )
        );
        Assert.Throws<ArgumentNullException>(() =>
            BaseCallReportAggregator.ApplyAssemblyReports(
                "A.dll",
                new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal),
                typesByAssembly,
                null!
            )
        );

        // A null `latestReportsForAssembly` is the harvester's "this assembly produced zero
        // matching messages" sentinel and must behave as the retirement path (same as an empty
        // dict).
        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            MakeReports(("X", stringArray3, "DXMSG006", "A/X.cs", 1)),
            typesByAssembly,
            mergedReports
        );
        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            null,
            typesByAssembly,
            mergedReports
        );
        Assert.That(mergedReports, Is.Empty);
        Assert.That(typesByAssembly["A.dll"], Is.Empty);
    }

    // -- BaseCallReportAggregator.BuildSnapshot tests
    // ---------------------------------------------

    [Test]
    public void BuildSnapshotLogEntriesAndCompilerMessageAgree()
    {
        // Same type + same method reported via both paths: one entry, dedup'd diagnostic IDs,
        // method appears once.
        Dictionary<string, ParsedTypeReport> logEntries = MakeReports(
            ("Sample.Player", stringArray3, "DXMSG006", "Assets/Player.cs", 8)
        );
        Dictionary<string, ParsedTypeReport> merged = MakeReports(
            ("Sample.Player", stringArray4, "DXMSG006", "Assets/Player.cs", 8)
        );

        Dictionary<string, BaseCallReportEntryDto> snapshot =
            BaseCallReportAggregator.BuildSnapshot(logEntries, merged);

        Assert.That(snapshot, Has.Count.EqualTo(1));
        BaseCallReportEntryDto entry = snapshot["Sample.Player"];
        Assert.That(entry.MissingBaseFor, Is.EquivalentTo(expectedArray2));
        Assert.That(entry.DiagnosticIds, Is.EquivalentTo(expectedArray4));
        Assert.That(entry.FilePath, Is.EqualTo("Assets/Player.cs"));
        Assert.That(entry.Line, Is.EqualTo(8));
    }

    [Test]
    public void BuildSnapshotLogEntriesOnlyVsCompilerMessageOnly()
    {
        // Each source independently produces a non-empty snapshot. Both halves of the dual-source
        // contract must work in isolation; Unity 2021 only feeds the CompilerMessage path,
        // Unity 2022+ predominantly feeds LogEntries.
        Dictionary<string, ParsedTypeReport> logOnly = MakeReports(
            ("Sample.A", stringArray4, "DXMSG006", "A.cs", 1)
        );
        Dictionary<string, BaseCallReportEntryDto> logSnapshot =
            BaseCallReportAggregator.BuildSnapshot(logOnly, mergedReports: null);
        Assert.That(logSnapshot, Has.Count.EqualTo(1));
        Assert.That(logSnapshot.ContainsKey("Sample.A"), Is.True);

        Dictionary<string, ParsedTypeReport> mergedOnly = MakeReports(
            ("Sample.B", stringArray2, "DXMSG009", "B.cs", 2)
        );
        Dictionary<string, BaseCallReportEntryDto> mergedSnapshot =
            BaseCallReportAggregator.BuildSnapshot(logEntriesReports: null, mergedOnly);
        Assert.That(mergedSnapshot, Has.Count.EqualTo(1));
        Assert.That(mergedSnapshot.ContainsKey("Sample.B"), Is.True);
        Assert.That(mergedSnapshot["Sample.B"].DiagnosticIds, Is.EquivalentTo(expectedArray5));
    }

    [Test]
    public void BuildSnapshotKeepsFirstSeenFilePathLine()
    {
        // First seen wins. LogEntries reports first → its path/line stick even though merged
        // also has data for the same type with a different path/line.
        Dictionary<string, ParsedTypeReport> logEntries = MakeReports(
            ("X", stringArray5, "DXMSG006", "First.cs", 3)
        );
        Dictionary<string, ParsedTypeReport> merged = MakeReports(
            ("X", stringArray5, "DXMSG006", "Second.cs", 99)
        );

        Dictionary<string, BaseCallReportEntryDto> snapshot =
            BaseCallReportAggregator.BuildSnapshot(logEntries, merged);

        Assert.That(snapshot["X"].FilePath, Is.EqualTo("First.cs"));
        Assert.That(snapshot["X"].Line, Is.EqualTo(3));
    }

    [Test]
    public void BuildSnapshotUnionsMethodsAndDiagnosticIdsAcrossSources()
    {
        // LogEntries says X.Awake / DXMSG006; merged says X.OnEnable / DXMSG009. The snapshot
        // union must carry both methods and both diagnostic IDs on a single X entry.
        Dictionary<string, ParsedTypeReport> logEntries = MakeReports(
            ("X", stringArray6, "DXMSG006", "A.cs", 1)
        );
        Dictionary<string, ParsedTypeReport> merged = MakeReports(
            ("X", stringArray7, "DXMSG009", "B.cs", 2)
        );

        Dictionary<string, BaseCallReportEntryDto> snapshot =
            BaseCallReportAggregator.BuildSnapshot(logEntries, merged);

        Assert.That(snapshot["X"].MissingBaseFor, Is.EquivalentTo(expectedArray6));
        Assert.That(snapshot["X"].DiagnosticIds, Is.EquivalentTo(expectedArray7));
    }

    [Test]
    public void BuildSnapshotEmptyInputsReturnsEmptySnapshot()
    {
        Dictionary<string, BaseCallReportEntryDto> snapshot =
            BaseCallReportAggregator.BuildSnapshot(null, null);
        Assert.That(snapshot, Is.Empty);

        snapshot = BaseCallReportAggregator.BuildSnapshot(
            new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal),
            new Dictionary<string, ParsedTypeReport>(StringComparer.Ordinal)
        );
        Assert.That(snapshot, Is.Empty);
    }

    [Test]
    public void ApplyAssemblyReportsThreeAssembliesDisjointSetsRetireOneAndOverlap()
    {
        // Spec 3a: A reports {X, Y}, B reports {Y, Z}, C reports {W}. The merged snapshot must
        // contain {W, X, Y, Z}. Then A retires X (recompiles without it). The merged snapshot must
        // still contain {W, Y, Z}: Y survives because B still claims it; X disappears entirely.
        Dictionary<string, HashSet<string>> typesByAssembly = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ParsedTypeReport> mergedReports = new(StringComparer.Ordinal);

        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            MakeReports(
                ("X", stringArray6, "DXMSG006", "A/X.cs", 1),
                ("Y", stringArray7, "DXMSG006", "A/Y.cs", 2)
            ),
            typesByAssembly,
            mergedReports
        );
        BaseCallReportAggregator.ApplyAssemblyReports(
            "B.dll",
            MakeReports(
                ("Y", stringArray8, "DXMSG006", "B/Y.cs", 3),
                ("Z", stringArray9, "DXMSG006", "B/Z.cs", 4)
            ),
            typesByAssembly,
            mergedReports
        );
        BaseCallReportAggregator.ApplyAssemblyReports(
            "C.dll",
            MakeReports(("W", stringArray10, "DXMSG006", "C/W.cs", 5)),
            typesByAssembly,
            mergedReports
        );

        Assert.That(
            mergedReports.Keys,
            Is.EquivalentTo(expectedArray8),
            "Pre-retirement snapshot must contain all four FQNs across the three assemblies."
        );

        // A retires X by recompiling and reporting only Y.
        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            MakeReports(("Y", stringArray8, "DXMSG006", "A/Y.cs", 2)),
            typesByAssembly,
            mergedReports
        );

        Assert.That(
            mergedReports.Keys,
            Is.EquivalentTo(expectedArray9),
            "X must be retired; W/Y/Z must remain (Y survives via B's claim)."
        );
        Assert.That(typesByAssembly["A.dll"], Is.EquivalentTo(expectedArray10));
        Assert.That(typesByAssembly["B.dll"], Is.EquivalentTo(expectedArray11));
        Assert.That(typesByAssembly["C.dll"], Is.EquivalentTo(expectedArray12));
    }

    [Test]
    public void AggregateSameAssemblyReportsSameFqnMultipleTimesInOneDrainDedupesMethods()
    {
        // Spec 3b: a single drain that contains the SAME line three times for the same FQN must
        // dedupe; Aggregate-then-merge always produces a single MissingBaseFor entry per method.
        // This pins the parser-level dedup contract that the harvester depends on.
        Dictionary<string, ParsedTypeReport> aggregated = BaseCallLogMessageParser.Aggregate(
            new[] { Unity2021Dxmsg009, Unity2021Dxmsg009, Unity2021Dxmsg009 }
        );

        Assert.That(aggregated, Has.Count.EqualTo(1));
        ParsedTypeReport report = aggregated["Sample.BrokenThing"];
        Assert.That(
            report.MissingBaseFor,
            Is.EquivalentTo(expected),
            "Triple-reported same FQN.method must collapse to a single MissingBaseFor entry."
        );
        Assert.That(report.DiagnosticIds, Is.EquivalentTo(expectedArray5));
    }

    [Test]
    public void BuildSnapshotDictionaryWithNullValueDoesNotCrash()
    {
        // Spec 3c: defensive; if either source dictionary contains a null ParsedTypeReport value
        // (a defensive shape we may see if the harvester's internal state ever decays), the
        // snapshot builder must not crash. The null entry is silently skipped.
        Dictionary<string, ParsedTypeReport> logEntries = new(StringComparer.Ordinal)
        {
            { "Sample.X", null! },
        };
        Dictionary<string, ParsedTypeReport> merged = MakeReports(
            ("Sample.Y", stringArray11, "DXMSG006", "Y.cs", 1)
        );

        Dictionary<string, BaseCallReportEntryDto>? snapshot = null;
        Assert.DoesNotThrow(() =>
        {
            snapshot = BaseCallReportAggregator.BuildSnapshot(logEntries, merged);
        });
        Assert.That(snapshot, Is.Not.Null);
        // The null-valued Sample.X is skipped; only Sample.Y survives.
        Assert.That(snapshot!.Keys, Is.EquivalentTo(expectedArray13));
    }

    [Test]
    public void ApplyAssemblyReportsFilePathStickinessFirstSeenWinsAcrossAssemblies()
    {
        // Spec 3d: A reports type X with path=A.cs line=10. B then ALSO reports X with path=B.cs
        // line=20. The merged snapshot must keep A.cs/10 (first-assembly-seen wins). This pins
        // the cross-assembly first-seen contract; same-assembly recompile uses latest payload
        // (different code path; pinned implicitly by ApplyAssemblyReports_RecompileSameAssembly...).
        Dictionary<string, HashSet<string>> typesByAssembly = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ParsedTypeReport> mergedReports = new(StringComparer.Ordinal);

        BaseCallReportAggregator.ApplyAssemblyReports(
            "A.dll",
            MakeReports(("X", stringArray12, "DXMSG006", "A.cs", 10)),
            typesByAssembly,
            mergedReports
        );
        BaseCallReportAggregator.ApplyAssemblyReports(
            "B.dll",
            MakeReports(("X", stringArray12, "DXMSG006", "B.cs", 20)),
            typesByAssembly,
            mergedReports
        );

        Assert.That(mergedReports, Contains.Key("X"));
        Assert.That(
            mergedReports["X"].FilePath,
            Is.EqualTo("A.cs"),
            "First-assembly-seen file path must persist when a second assembly also reports the FQN."
        );
        Assert.That(
            mergedReports["X"].Line,
            Is.EqualTo(10),
            "First-assembly-seen line must persist when a second assembly also reports the FQN."
        );
    }

    private static Dictionary<string, ParsedTypeReport> MakeReports(
        params (
            string Fqn,
            string[] Methods,
            string DiagnosticId,
            string FilePath,
            int Line
        )[] entries
    )
    {
        Dictionary<string, ParsedTypeReport> result = new(StringComparer.Ordinal);
        foreach ((string fqn, string[] methods, string id, string path, int line) in entries)
        {
            ParsedTypeReport report = new()
            {
                TypeFullName = fqn,
                FilePath = path,
                Line = line,
            };
            foreach (string method in methods.Distinct(StringComparer.Ordinal))
            {
                report.MissingBaseFor.Add(method);
            }
            report.DiagnosticIds.Add(id);
            result[fqn] = report;
        }
        return result;
    }
}
