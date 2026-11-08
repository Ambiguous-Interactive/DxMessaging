using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
internal sealed class GeneratedConstructorSemanticsTests
{
    private static IEnumerable<(string Kind, LanguageVersion Version)> Declarations()
    {
        yield return ("record", LanguageVersion.CSharp9);
        yield return ("record struct", LanguageVersion.CSharp10);
        yield return ("class", LanguageVersion.CSharp12);
        yield return ("struct", LanguageVersion.CSharp12);
    }

    [Test]
    public void RequestedFieldConstructorReportsPrimaryConstructorRestriction(
        [ValueSource(nameof(Declarations))] (string Kind, LanguageVersion Version) declaration,
        [Values(false, true)] bool separatePartial
    )
    {
        string source = CreateSource(
            declaration.Kind,
            separatePartial,
            emptyParameters: false,
            fields: true
        );
        CSharpCompilation input = CreateCompilation(source, declaration.Version);
        string context = $"{declaration}, separatePartial={separatePartial}";
        Assert.That(input.GetDiagnostics(), Is.Empty, "Legal input: " + context);
        GeneratorDriver driver = CreateDriver(declaration.Version)
            .RunGeneratorsAndUpdateCompilation(
                input,
                out Compilation output,
                out ImmutableArray<Diagnostic> diagnostics
            );
        GeneratorDriverRunResult result = driver.GetRunResult();
        Assert.That(result.Results.Select(run => run.Exception), Has.All.Null, context);
        Assert.That(diagnostics, Has.Length.EqualTo(1), context);
        Diagnostic diagnostic = diagnostics.Single();
        Assert.That(diagnostic.Id, Is.EqualTo("DXMSG012"), context);
        Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Error), context);
        Assert.That(diagnostic.Location.SourceTree, Is.EqualTo(input.SyntaxTrees.Last()), context);
        Assert.That(
            source.Substring(
                diagnostic.Location.SourceSpan.Start,
                diagnostic.Location.SourceSpan.Length
            ),
            Is.EqualTo("Packet"),
            context
        );
        Assert.That(
            diagnostic.GetMessage(CultureInfo.InvariantCulture),
            Does.Contain("remove DxAutoConstructor").And.Contain("this(...)"),
            context
        );
        Assert.That(
            result.GeneratedTrees,
            Is.Empty,
            "Do not emit an invalid secondary constructor: " + context
        );
        Assert.That(output.GetDiagnostics(), Is.Empty, "No generated compiler errors: " + context);
    }

    [Test]
    public void GeneratedEmptyPrimaryConstructorChainsWithoutRepeatingInitializers(
        [ValueSource(nameof(Declarations))] (string Kind, LanguageVersion Version) declaration,
        [Values(false, true)] bool separatePartial
    )
    {
        string source =
            CreateSource(declaration.Kind, separatePartial, emptyParameters: true, fields: true)
            + "public static class Consumer { public static bool Verify() { Packet packet = new Packet(42); return packet.Extra == 42 && packet.Stamp == 14 && Packet.Initializations == 1; } }";
        GeneratorDriver driver = CreateDriver(declaration.Version)
            .RunGeneratorsAndUpdateCompilation(
                CreateCompilation(source, declaration.Version),
                out Compilation output,
                out ImmutableArray<Diagnostic> diagnostics
            );
        string context = $"{declaration}, separatePartial={separatePartial}";
        Assert.That(diagnostics, Is.Empty, context);
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), context);
        AssertCompilationExecutes(output, context);
    }

    [Test]
    public void PrimaryConstructorWithoutPublicFieldsNeedsNoSecondaryConstructor(
        [ValueSource(nameof(Declarations))] (string Kind, LanguageVersion Version) declaration,
        [Values(false, true)] bool separatePartial
    )
    {
        string source = CreateSource(
            declaration.Kind,
            separatePartial,
            emptyParameters: false,
            fields: false
        );
        GeneratorDriver driver = CreateDriver(declaration.Version)
            .RunGeneratorsAndUpdateCompilation(
                CreateCompilation(source, declaration.Version),
                out Compilation output,
                out ImmutableArray<Diagnostic> diagnostics
            );
        Assert.That(diagnostics, Is.Empty, declaration.ToString());
        Assert.That(driver.GetRunResult().GeneratedTrees, Is.Empty, declaration.ToString());
        Assert.That(output.GetDiagnostics(), Is.Empty, declaration.ToString());
    }

    [Test]
    public void ExplicitChainingRemedyPreservesPrimaryStateAndRunsInitializersOnce(
        [ValueSource(nameof(Declarations))] (string Kind, LanguageVersion Version) declaration,
        [Values(false, true)] bool emptyParameters
    )
    {
        string source = CreateSource(
                declaration.Kind,
                separatePartial: true,
                emptyParameters,
                fields: true
            )
            .Replace(
                "[DxMessaging.Core.Attributes.DxAutoConstructor]",
                "",
                StringComparison.Ordinal
            )
            .Replace(
                "public readonly int Extra = 0;",
                "public readonly int Extra = 0; public Packet("
                    + (emptyParameters ? "" : "int primary, string name, ")
                    + "int extra) : this("
                    + (emptyParameters ? "" : "primary, name")
                    + ") { Extra = extra; }",
                StringComparison.Ordinal
            );
        string primaryCheck =
            declaration.Kind.StartsWith("record", StringComparison.Ordinal) && !emptyParameters
                ? " && packet.Primary == 7 && packet.Name == \"primary\""
                : "";
        source +=
            "public static class Consumer { public static bool Verify() { Packet packet = new Packet("
            + (emptyParameters ? "" : "7, \"primary\", ")
            + "42); return packet.Extra == 42 && packet.Stamp == 14 && Packet.Initializations == 1"
            + primaryCheck
            + "; } }";
        AssertCompilationExecutes(
            CreateCompilation(source, declaration.Version),
            declaration.ToString()
        );
    }

    [Test]
    public void PrimaryConstructorOnContainerDoesNotBlockNestedFieldConstructor(
        [ValueSource(nameof(Declarations))] (string Kind, LanguageVersion Version) declaration
    )
    {
        string source = $$"""
public partial {{declaration.Kind}} Envelope(int Primary, string Name)
{
    public int Stamp { get; } = Primary + Name.Length;
    [DxMessaging.Core.Attributes.DxAutoConstructor]
    public readonly partial struct Packet { public readonly int Value; }
}
public static class Consumer
{
    public static bool Verify() => new Envelope.Packet(42).Value == 42;
}
""";
        GeneratorDriver driver = CreateDriver(declaration.Version)
            .RunGeneratorsAndUpdateCompilation(
                CreateCompilation(source, declaration.Version),
                out Compilation output,
                out ImmutableArray<Diagnostic> diagnostics
            );
        Assert.That(diagnostics, Is.Empty, declaration.ToString());
        Assert.That(
            driver.GetRunResult().GeneratedTrees,
            Has.Length.EqualTo(1),
            declaration.ToString()
        );
        AssertCompilationExecutes(output, declaration.ToString());
    }

    [Test]
    public void RecordCopySignatureReportsRestrictionWithoutReplacingCompilerCopying(
        [Values(false, true)] bool sealedRecord,
        [Values(false, true)] bool generic,
        [Values(false, true)] bool nullable,
        [Values(false, true)] bool separatePartial,
        [Values(false, true)] bool emptyPrimary
    )
    {
        string name = generic ? "Packet<T>" : "Packet";
        string modifier = sealedRecord ? "sealed " : "";
        string attribute = "[DxMessaging.Core.Attributes.DxAutoConstructor]";
        string declaration =
            $"public {modifier}partial record {name}{(emptyPrimary ? "()" : "")} {{ public readonly {name}{(nullable ? "?" : "")} Next = null!; }}";
        string source =
            "#nullable enable\n"
            + (
                separatePartial
                    ? declaration + attribute + $" public partial record {name} {{ }}"
                    : attribute + declaration
            );
        string context =
            $"sealed={sealedRecord}, generic={generic}, nullable={nullable}, partial={separatePartial}, primary={emptyPrimary}";
        CSharpCompilation input = CreateCompilation(source, LanguageVersion.CSharp9);
        Assert.That(input.GetDiagnostics(), Is.Empty, "Legal input: " + context);
        GeneratorDriver driver = CreateDriver(LanguageVersion.CSharp9)
            .RunGeneratorsAndUpdateCompilation(
                input,
                out Compilation output,
                out ImmutableArray<Diagnostic> diagnostics
            );
        Assert.That(
            driver.GetRunResult().Results.Select(run => run.Exception),
            Has.All.Null,
            context
        );
        Assert.That(diagnostics, Has.Length.EqualTo(1), context);
        Assert.That(diagnostics.Single().Id, Is.EqualTo("DXMSG013"), context);
        Assert.That(diagnostics.Single().Severity, Is.EqualTo(DiagnosticSeverity.Error), context);
        Assert.That(
            diagnostics.Single().GetMessage(CultureInfo.InvariantCulture),
            Does.Contain("copy constructor").And.Contain("different signature"),
            context
        );
        Assert.That(
            driver.GetRunResult().GeneratedTrees,
            Is.Empty,
            "Keep compiler-owned copying: " + context
        );
        Assert.That(output.GetDiagnostics(), Is.Empty, context);
    }

    [Test]
    public void RecordPropertyRemedyPreservesWithCopyState(
        [Values(false, true)] bool sealedRecord,
        [Values(false, true)] bool generic
    )
    {
        string name = generic ? "Packet<T>" : "Packet";
        string closed = generic ? "Packet<int>" : "Packet";
        string source = $$"""
#nullable enable
public {{(sealedRecord ? "sealed " : "")}}partial record {{name}}
{
    public {{name}}? Next { get; init; }
}
public static class Consumer
{
    public static bool Verify()
    {
        {{closed}} leaf = new {{closed}}();
        {{closed}} root = new {{closed}} { Next = leaf };
        {{closed}} copy = root with { };
        return ReferenceEquals(copy.Next, leaf) && ReferenceEquals(root.Next, leaf) && !ReferenceEquals(copy, root);
    }
}
""";
        AssertCompilationExecutes(
            CreateCompilation(source, LanguageVersion.CSharp9),
            $"sealed={sealedRecord}, generic={generic}"
        );
    }

    [Test]
    public void NeighboringFieldSignaturesPreserveConstructionAndCopying(
        [Values(
            "ClassSelf",
            "RecordObject",
            "RecordArray",
            "RecordMultiple",
            "RecordOtherConstruction"
        )]
            string shape,
        [Values(false, true)] bool generic
    )
    {
        string name = generic ? "Packet<T>" : "Packet";
        string closed = generic ? "Packet<int>" : "Packet";
        string kind = shape == "ClassSelf" ? "class" : "record";
        string fieldType = shape switch
        {
            "RecordObject" => "object",
            "RecordOtherConstruction" => generic ? "Packet<string>" : "object",
            "RecordArray" => name + "[]",
            _ => name,
        };
        string extraField = shape == "RecordMultiple" ? "public readonly int Value;" : "";
        string argument = shape == "RecordMultiple" ? "marker, 42" : "marker";
        string marker = shape switch
        {
            "RecordObject" => "object marker = new object();",
            "RecordOtherConstruction" => generic
                ? "Packet<string> marker = new Packet<string>(null);"
                : "object marker = new object();",
            "RecordArray" => $"{closed}[] marker = new {closed}[0];",
            _ => $"{closed} marker = new {closed}(null{(shape == "RecordMultiple" ? ", 7" : "")});",
        };
        string copy =
            shape == "ClassSelf"
                ? "PacketPlaceholder copy = root;"
                : "PacketPlaceholder copy = root with { };";
        string valueCheck = shape == "RecordMultiple" ? " && copy.Value == 42" : "";
        string source = $$"""
#nullable enable
[DxMessaging.Core.Attributes.DxAutoConstructor]
public partial {{kind}} {{name}} { public readonly {{fieldType}}? Next; {{extraField}} }
public static class Consumer
{
    public static bool Verify()
    {
        {{marker}}
        {{closed}} root = new {{closed}}({{argument}});
        {{copy.Replace("PacketPlaceholder", closed, StringComparison.Ordinal)}}
        return ReferenceEquals(root.Next, marker) && ReferenceEquals(copy.Next, marker){{valueCheck}};
    }
}
""";
        GeneratorDriver driver = CreateDriver(LanguageVersion.CSharp9)
            .RunGeneratorsAndUpdateCompilation(
                CreateCompilation(source, LanguageVersion.CSharp9),
                out Compilation output,
                out ImmutableArray<Diagnostic> diagnostics
            );
        Assert.That(diagnostics, Is.Empty, $"shape={shape}, generic={generic}");
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), shape);
        AssertCompilationExecutes(output, $"shape={shape}, generic={generic}");
    }

    private static string CreateSource(
        string kind,
        bool separatePartial,
        bool emptyParameters,
        bool fields
    )
    {
        string parameters = emptyParameters ? "" : "int Primary, string Name";
        string stamp = emptyParameters ? "14" : "Primary + Name.Length";
        string field = fields ? "public readonly int Extra = 0;" : "";
        string attributes = "[DxMessaging.Core.Attributes.DxAutoConstructor]";
        string members =
            $"public static int Initializations; private static int Initialize(int value) {{ Initializations++; return value; }} public int Stamp {{ get; }} = Initialize({stamp});";
        return separatePartial
            ? $"public partial {kind} Packet({parameters}) {{ {members} {field} }} {attributes} public partial {kind} Packet {{ }}"
            : $"{attributes} public partial {kind} Packet({parameters}) {{ {members} {field} }}";
    }

    private static CSharpGeneratorDriver CreateDriver(LanguageVersion version) =>
        CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(version)
        );

    private static CSharpCompilation CreateCompilation(string source, LanguageVersion version)
    {
        CSharpCompilation compilation =
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(source);
        CSharpParseOptions options = new(version);
        return compilation
            .RemoveAllSyntaxTrees()
            .AddSyntaxTrees(
                compilation.SyntaxTrees.Select(tree =>
                    CSharpSyntaxTree.ParseText(tree.GetText(), options, tree.FilePath)
                )
            );
    }

    private static void AssertCompilationExecutes(Compilation compilation, string context)
    {
        Assert.That(compilation.GetDiagnostics(), Is.Empty, context);
        using MemoryStream image = new();
        EmitResult result = compilation.Emit(image);
        Assert.That(
            result.Success,
            Is.True,
            context + "\n" + string.Join("\n", result.Diagnostics)
        );
        image.Position = 0;
        AssemblyLoadContext load = new("PrimaryConstructorConsumer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? verify = assembly.GetType("Consumer")?.GetMethod("Verify");
            Assert.That(verify, Is.Not.Null, context);
            Assert.That(verify!.Invoke(null, null), Is.EqualTo(true), context);
        }
        finally
        {
            load.Unload();
        }
    }
}
