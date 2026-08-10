using System.Collections.Generic;
using DxMessaging.Editor.Analyzers;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
internal sealed class BaseCallLogMessageParserTests
{
    // The exact format strings the analyzer uses today. If these drift, both this test and the
    // parser regexes must be updated in lockstep; the parser is downstream of the analyzer.
    private const string Dxmsg006Bare =
        "'Sample.Player' overrides MessageAwareComponent.Awake but does not call base.Awake(); "
        + "the messaging system may not function correctly on this component.";

    private const string Dxmsg007Bare =
        "'Sample.Player' hides MessageAwareComponent.OnEnable with 'new'; "
        + "replace with 'override' and call base.OnEnable() so the messaging system continues to function.";

    private const string Dxmsg008Bare =
        "'Sample.Player' is excluded from the DxMessaging base-call check ([DxIgnoreMissingBaseCall]).";

    private const string Dxmsg009Bare =
        "'Sample.BrokenThing' declares OnEnable without 'override' or 'new'; "
        + "this implicitly hides MessageAwareComponent.OnEnable (CS0114) and the messaging system will not function. "
        + "Add 'override' and call base.OnEnable(), or add 'new' if the hiding is intentional.";

    private const string Dxmsg010Bare =
        "'Sample.BrokenThing' calls base.OnEnable() but the inherited override on 'Sample.ddd' "
        + "does not chain to MessageAwareComponent.OnEnable; the messaging system will not function correctly on this component.";
    private static readonly string[] expected = new[] { "DXMSG006" };
    private static readonly string[] expectedArray = new[] { "Awake" };
    private static readonly string[] expectedArray0 = new[] { "OnEnable" };
    private static readonly string[] expectedArray1 = new[] { "DXMSG006", "DXMSG007" };
    private static readonly string[] expectedArray2 = new[] { "DXMSG008" };
    private static readonly string[] expectedArray3 = new[] { "DXMSG006", "DXMSG008" };
    private static readonly string[] expectedArray4 = new[] { "DXMSG009" };
    private static readonly string[] expectedArray5 = new[] { "DXMSG006", "DXMSG009" };
    private static readonly string[] expectedArray6 = new[] { "DXMSG010" };
    private static readonly string[] expectedArray7 = new[]
    {
        "DiagnosticId",
        "FilePath",
        "Line",
        "MethodName",
        "TypeFullName",
    };

    [Test]
    public void ParseLineBareDxmsg006CapturesIdTypeAndMethod()
    {
        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(Dxmsg006Bare);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG006"));
        Assert.That(entry.TypeFullName, Is.EqualTo("Sample.Player"));
        Assert.That(entry.MethodName, Is.EqualTo("Awake"));
        Assert.That(entry.FilePath, Is.EqualTo(string.Empty));
        Assert.That(entry.Line, Is.EqualTo(0));
    }

    [Test]
    public void ParseLineBareDxmsg007CapturesIdTypeAndMethod()
    {
        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(Dxmsg007Bare);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG007"));
        Assert.That(entry.TypeFullName, Is.EqualTo("Sample.Player"));
        Assert.That(entry.MethodName, Is.EqualTo("OnEnable"));
    }

    [Test]
    public void ParseLineBareDxmsg008CapturesIdAndTypeWithEmptyMethod()
    {
        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(Dxmsg008Bare);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG008"));
        Assert.That(entry.TypeFullName, Is.EqualTo("Sample.Player"));
        Assert.That(entry.MethodName, Is.EqualTo(string.Empty));
    }

    [Test]
    public void ParseLinePrefixedDxmsg006PopulatesPathAndLine()
    {
        const string line = "Assets/Sample/Player.cs(12,9): warning DXMSG006: " + Dxmsg006Bare;

        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(line);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG006"));
        Assert.That(entry.TypeFullName, Is.EqualTo("Sample.Player"));
        Assert.That(entry.MethodName, Is.EqualTo("Awake"));
        Assert.That(entry.FilePath, Is.EqualTo("Assets/Sample/Player.cs"));
        Assert.That(entry.Line, Is.EqualTo(12));
    }

    [Test]
    public void ParseLinePrefixedDxmsg007PopulatesPathAndLine()
    {
        const string line = "Assets/Sample/Player.cs(34,5): warning DXMSG007: " + Dxmsg007Bare;

        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(line);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG007"));
        Assert.That(entry.FilePath, Is.EqualTo("Assets/Sample/Player.cs"));
        Assert.That(entry.Line, Is.EqualTo(34));
    }

    [Test]
    public void ParseLinePrefixedDxmsg008PopulatesPathAndLine()
    {
        const string line = "Assets/Sample/Player.cs(7,5): info DXMSG008: " + Dxmsg008Bare;

        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(line);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG008"));
        Assert.That(entry.MethodName, Is.EqualTo(string.Empty));
        Assert.That(entry.FilePath, Is.EqualTo("Assets/Sample/Player.cs"));
        Assert.That(entry.Line, Is.EqualTo(7));
    }

    [Test]
    public void ParseLineNullInputReturnsNull()
    {
        Assert.That(BaseCallLogMessageParser.ParseLine(null!), Is.Null);
    }

    [Test]
    public void ParseLineEmptyOrWhitespaceInputReturnsNull()
    {
        Assert.That(BaseCallLogMessageParser.ParseLine(string.Empty), Is.Null);
        Assert.That(BaseCallLogMessageParser.ParseLine("    "), Is.Null);
        Assert.That(BaseCallLogMessageParser.ParseLine("\t\r\n  "), Is.Null);
    }

    [Test]
    public void ParseLineUnrelatedCompilerWarningReturnsNull()
    {
        Assert.That(
            BaseCallLogMessageParser.ParseLine(
                "Assets/Sample/Other.cs(3,5): warning CS0168: The variable 'x' is declared but never used"
            ),
            Is.Null
        );
    }

    [Test]
    public void ParseLineDebugLogStyleTextReturnsNull()
    {
        Assert.That(
            BaseCallLogMessageParser.ParseLine(
                "Hello from Debug.Log; nothing analyzer-related here."
            ),
            Is.Null
        );
    }

    [Test]
    public void ParseLineDiagnosticIdInIsolationOrCommentFormReturnsNull()
    {
        // A bare token mention in a comment / random log line must NOT match.
        Assert.That(BaseCallLogMessageParser.ParseLine("DXMSG006"), Is.Null);
        Assert.That(BaseCallLogMessageParser.ParseLine("// see DXMSG006 in the docs"), Is.Null);
        // A line that says DXMSG006 but is not the analyzer's wording.
        Assert.That(
            BaseCallLogMessageParser.ParseLine(
                "DXMSG006 fired earlier today on this assembly; investigate."
            ),
            Is.Null
        );
    }

    [Test]
    public void ParseLineAnchorRejectsAnalyzerWordingMidString()
    {
        // S7: body regexes anchor to ^ so a Debug.Log payload that happens to embed the
        // analyzer's wording mid-string is NOT surfaced as a real DXMSG006/007/008.
        Assert.That(
            BaseCallLogMessageParser.ParseLine(
                "Custom log: see ('Sample.Player' overrides MessageAwareComponent.Awake but does not call base.Awake(); "
                    + "the messaging system may not function correctly on this component.)"
            ),
            Is.Null
        );
        Assert.That(
            BaseCallLogMessageParser.ParseLine(
                "Note: 'Sample.Player' hides MessageAwareComponent.OnEnable with 'new'; "
                    + "replace with 'override' and call base.OnEnable() so the messaging system continues to function."
            ),
            Is.Null
        );
        Assert.That(
            BaseCallLogMessageParser.ParseLine(
                "Reminder: 'Sample.Player' is excluded from the DxMessaging base-call check ([DxIgnoreMissingBaseCall])."
            ),
            Is.Null
        );
    }

    [Test]
    public void AggregateDedupesSameTypeAndMethod()
    {
        List<string> lines = new() { Dxmsg006Bare, Dxmsg006Bare, Dxmsg006Bare };

        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(lines);

        Assert.That(result.Count, Is.EqualTo(1));
        ParsedTypeReport report = result["Sample.Player"];
        Assert.That(report.MissingBaseFor, Is.EqualTo(new List<string> { "Awake" }));
        Assert.That(report.DiagnosticIds, Is.EquivalentTo(expected));
    }

    [Test]
    public void AggregateMergesDifferentMethodsOnSameType()
    {
        const string awakeLine =
            "'Sample.Player' overrides MessageAwareComponent.Awake but does not call base.Awake(); "
            + "the messaging system may not function correctly on this component.";
        const string onEnableLine =
            "'Sample.Player' overrides MessageAwareComponent.OnEnable but does not call base.OnEnable(); "
            + "the messaging system may not function correctly on this component.";

        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(
            new[] { awakeLine, onEnableLine }
        );

        Assert.That(result.Count, Is.EqualTo(1));
        ParsedTypeReport report = result["Sample.Player"];
        Assert.That(report.MissingBaseFor, Is.EqualTo(new List<string> { "Awake", "OnEnable" }));
    }

    [Test]
    public void AggregateSeparatesDifferentTypes()
    {
        const string a =
            "'Sample.PlayerA' overrides MessageAwareComponent.Awake but does not call base.Awake(); "
            + "the messaging system may not function correctly on this component.";
        const string b =
            "'Sample.PlayerB' overrides MessageAwareComponent.OnEnable but does not call base.OnEnable(); "
            + "the messaging system may not function correctly on this component.";

        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(
            new[] { a, b }
        );

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result["Sample.PlayerA"].MissingBaseFor, Is.EqualTo(expectedArray));
        Assert.That(result["Sample.PlayerB"].MissingBaseFor, Is.EqualTo(expectedArray0));
    }

    [Test]
    public void AggregateAccumulatesIdsAcrossDxmsg006And007()
    {
        const string awake006 =
            "'Sample.Player' overrides MessageAwareComponent.Awake but does not call base.Awake(); "
            + "the messaging system may not function correctly on this component.";
        const string onEnable007 =
            "'Sample.Player' hides MessageAwareComponent.OnEnable with 'new'; "
            + "replace with 'override' and call base.OnEnable() so the messaging system continues to function.";

        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(
            new[] { awake006, onEnable007 }
        );

        Assert.That(result.Count, Is.EqualTo(1));
        ParsedTypeReport report = result["Sample.Player"];
        Assert.That(report.DiagnosticIds, Is.EquivalentTo(expectedArray1));
        Assert.That(report.MissingBaseFor, Is.EqualTo(new List<string> { "Awake", "OnEnable" }));
    }

    [Test]
    public void AggregateDxmsg008ContributesIdButNoMethod()
    {
        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(
            new[] { Dxmsg008Bare }
        );

        Assert.That(result.Count, Is.EqualTo(1));
        ParsedTypeReport report = result["Sample.Player"];
        Assert.That(report.DiagnosticIds, Is.EquivalentTo(expectedArray2));
        Assert.That(report.MissingBaseFor, Is.Empty);
    }

    [Test]
    public void AggregateEmptyReturnsEmptyDictionary()
    {
        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(
            System.Array.Empty<string>()
        );

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void AggregateOrderIndependentFor008ThenVs006Then008()
    {
        const string awake006 =
            "'Sample.Player' overrides MessageAwareComponent.Awake but does not call base.Awake(); "
            + "the messaging system may not function correctly on this component.";

        Dictionary<string, ParsedTypeReport> forward = BaseCallLogMessageParser.Aggregate(
            new[] { awake006, Dxmsg008Bare }
        );
        Dictionary<string, ParsedTypeReport> reverse = BaseCallLogMessageParser.Aggregate(
            new[] { Dxmsg008Bare, awake006 }
        );

        Assert.That(forward.Count, Is.EqualTo(1));
        Assert.That(reverse.Count, Is.EqualTo(1));

        ParsedTypeReport fwd = forward["Sample.Player"];
        ParsedTypeReport rev = reverse["Sample.Player"];
        Assert.That(fwd.MissingBaseFor, Is.EqualTo(rev.MissingBaseFor));
        Assert.That(fwd.DiagnosticIds, Is.EquivalentTo(rev.DiagnosticIds));
        Assert.That(fwd.DiagnosticIds, Is.EquivalentTo(expectedArray3));
        Assert.That(fwd.MissingBaseFor, Is.EqualTo(new List<string> { "Awake" }));
    }

    [Test]
    public void ParseLineBareDxmsg009CapturesIdTypeAndMethod()
    {
        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(Dxmsg009Bare);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG009"));
        Assert.That(entry.TypeFullName, Is.EqualTo("Sample.BrokenThing"));
        Assert.That(entry.MethodName, Is.EqualTo("OnEnable"));
        Assert.That(entry.FilePath, Is.Empty);
        Assert.That(entry.Line, Is.EqualTo(0));
    }

    [Test]
    public void ParseLinePrefixedDxmsg009CapturesPathAndLine()
    {
        const string line =
            "Assets/Scripts/BrokenThing.cs(7,22): warning DXMSG009: " + Dxmsg009Bare;

        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(line);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG009"));
        Assert.That(entry.TypeFullName, Is.EqualTo("Sample.BrokenThing"));
        Assert.That(entry.MethodName, Is.EqualTo("OnEnable"));
        Assert.That(entry.FilePath, Is.EqualTo("Assets/Scripts/BrokenThing.cs"));
        Assert.That(entry.Line, Is.EqualTo(7));
    }

    [Test]
    public void ParseLineAnchorRejectsDxmsg009MidString()
    {
        // Adversarial: the analyzer's wording embedded in a Debug.Log payload must not be parsed
        // as a real DXMSG009 warning.
        const string line =
            "Hello world. " + "'Sample.BrokenThing' declares OnEnable without 'override' or 'new'.";

        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(line);

        Assert.That(parsed, Is.Null);
    }

    [Test]
    public void AggregateDxmsg009ContributesToMissingBaseFor()
    {
        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(
            new[] { Dxmsg009Bare }
        );

        Assert.That(result.Count, Is.EqualTo(1));
        ParsedTypeReport report = result["Sample.BrokenThing"];
        Assert.That(report.MissingBaseFor, Is.EqualTo(new List<string> { "OnEnable" }));
        Assert.That(report.DiagnosticIds, Is.EquivalentTo(expectedArray4));
    }

    [Test]
    public void AggregateDxmsg009AccumulatesAlongsideDxmsg006()
    {
        // Same type, two diagnostics on different methods → one entry, two methods, two ids.
        const string awake006 =
            "'Sample.BrokenThing' overrides MessageAwareComponent.Awake but does not call base.Awake(); "
            + "the messaging system may not function correctly on this component.";

        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(
            new[] { awake006, Dxmsg009Bare }
        );

        Assert.That(result.Count, Is.EqualTo(1));
        ParsedTypeReport report = result["Sample.BrokenThing"];
        Assert.That(report.MissingBaseFor, Is.EqualTo(new List<string> { "Awake", "OnEnable" }));
        Assert.That(report.DiagnosticIds, Is.EquivalentTo(expectedArray5));
    }

    [Test]
    public void AggregateDxmsg009Dedups()
    {
        List<string> lines = new() { Dxmsg009Bare, Dxmsg009Bare, Dxmsg009Bare };

        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(lines);

        Assert.That(result.Count, Is.EqualTo(1));
        ParsedTypeReport report = result["Sample.BrokenThing"];
        Assert.That(report.MissingBaseFor, Is.EqualTo(new List<string> { "OnEnable" }));
        Assert.That(report.DiagnosticIds.Count, Is.EqualTo(1));
    }

    [Test]
    public void ParseLineBareDxmsg010CapturesIdTypeAndMethod()
    {
        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(Dxmsg010Bare);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG010"));
        Assert.That(entry.TypeFullName, Is.EqualTo("Sample.BrokenThing"));
        Assert.That(entry.MethodName, Is.EqualTo("OnEnable"));
        Assert.That(entry.FilePath, Is.Empty);
        Assert.That(entry.Line, Is.EqualTo(0));
    }

    [Test]
    public void ParseLinePrefixedDxmsg010CapturesPathAndLine()
    {
        const string line =
            "Assets/Scripts/BrokenThing.cs(11,33): warning DXMSG010: " + Dxmsg010Bare;

        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(line);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG010"));
        Assert.That(entry.TypeFullName, Is.EqualTo("Sample.BrokenThing"));
        Assert.That(entry.MethodName, Is.EqualTo("OnEnable"));
        Assert.That(entry.FilePath, Is.EqualTo("Assets/Scripts/BrokenThing.cs"));
        Assert.That(entry.Line, Is.EqualTo(11));
    }

    [Test]
    public void ParseLineAnchorRejectsDxmsg010MidString()
    {
        // Adversarial: the analyzer's wording embedded in a Debug.Log payload must not be parsed
        // as a real DXMSG010 warning. The body regex is anchored at ^ so any leading text
        // disqualifies the match.
        const string line =
            "Custom log: see ('Sample.BrokenThing' calls base.OnEnable() but the inherited override on 'Sample.ddd' "
            + "does not chain to MessageAwareComponent.OnEnable; the messaging system will not function correctly on this component.)";

        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(line);

        Assert.That(parsed, Is.Null);
    }

    [Test]
    public void AggregateDxmsg010ContributesToMissingBaseFor()
    {
        // DXMSG010 must contribute its method name to MissingBaseFor so the inspector overlay
        // surfaces it just like DXMSG006/007/009.
        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(
            new[] { Dxmsg010Bare }
        );

        Assert.That(result.Count, Is.EqualTo(1));
        ParsedTypeReport report = result["Sample.BrokenThing"];
        Assert.That(report.MissingBaseFor, Is.EqualTo(new List<string> { "OnEnable" }));
        Assert.That(report.DiagnosticIds, Is.EquivalentTo(expectedArray6));
    }

    [Test]
    public void ParseLineDxmsg010BrokenAncestorIsNotSurfacedOnParsedEntry()
    {
        // Spec 5a: the DXMSG010 regex captures the broken-ancestor name in a `broken` group, but
        // the `ParsedEntry` struct does NOT expose it as a field. This test PINS the current
        // limitation: future readers of the parsed entry have no way to surface the broken-ancestor
        // FQN to the inspector overlay's "broken chain via {broken}" message. If the struct gains
        // a BrokenAncestor field in a future change, this test should be updated to assert the
        // captured value rather than the absence; but until then, this test keeps the limitation
        // visible to drive a future enhancement and prevent silent regressions of the regex itself.
        ParsedEntry? parsed = BaseCallLogMessageParser.ParseLine(Dxmsg010Bare);

        Assert.That(parsed, Is.Not.Null);
        ParsedEntry entry = parsed!.Value;
        Assert.That(entry.DiagnosticId, Is.EqualTo("DXMSG010"));
        Assert.That(entry.TypeFullName, Is.EqualTo("Sample.BrokenThing"));
        Assert.That(entry.MethodName, Is.EqualTo("OnEnable"));

        // ParsedEntry's public surface is exactly five members (DiagnosticId, TypeFullName,
        // MethodName, FilePath, Line). Confirm the struct shape is unchanged so a future addition
        // of a BrokenAncestor property is detected here as a deliberate API change.
        System.Reflection.PropertyInfo[] properties = typeof(ParsedEntry).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
        );
        string[] propertyNames = properties.Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.That(
            propertyNames,
            Is.EqualTo(expectedArray7),
            "ParsedEntry must expose exactly DiagnosticId/TypeFullName/MethodName/FilePath/Line. "
                + "The DXMSG010 regex captures `broken` but the struct does NOT yet surface it. "
                + "If you're seeing this assertion fail because you added BrokenAncestor, update "
                + "this test and add an assertion on the captured value."
        );
    }

    [Test]
    public void AggregateKeepsFirstSeenFilePathAndLine()
    {
        const string prefixed =
            "Assets/Sample/Player.cs(12,9): warning DXMSG006: "
            + "'Sample.Player' overrides MessageAwareComponent.Awake but does not call base.Awake(); "
            + "the messaging system may not function correctly on this component.";
        const string bareLater =
            "'Sample.Player' overrides MessageAwareComponent.OnEnable but does not call base.OnEnable(); "
            + "the messaging system may not function correctly on this component.";

        Dictionary<string, ParsedTypeReport> result = BaseCallLogMessageParser.Aggregate(
            new[] { prefixed, bareLater }
        );

        Assert.That(result.Count, Is.EqualTo(1));
        ParsedTypeReport report = result["Sample.Player"];
        Assert.That(report.FilePath, Is.EqualTo("Assets/Sample/Player.cs"));
        Assert.That(report.Line, Is.EqualTo(12));
        Assert.That(report.MissingBaseFor, Is.EqualTo(new List<string> { "Awake", "OnEnable" }));
    }
}
