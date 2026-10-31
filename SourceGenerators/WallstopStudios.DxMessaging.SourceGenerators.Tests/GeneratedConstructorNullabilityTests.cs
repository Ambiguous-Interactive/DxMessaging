using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
internal sealed class GeneratedConstructorNullabilityTests
{
    [TestCaseSource(nameof(NullableFieldCases))]
    public void GeneratedParametersPreserveFieldNullability(
        string fieldType,
        string argument,
        bool optional,
        bool accepted
    )
    {
        string consumer = $$"""
public static class Consumer
{
    public static object? Read() => new Packet(1, {{argument}}).Value;
}
""";
        string oracleSource = $$"""
#nullable enable
public readonly struct Packet
{
    public readonly {{fieldType}} Value;
    public Packet(int required, {{fieldType}} value) { Value = value; }
}
{{consumer}}
""";
        string context =
            $"{fieldType}, argument={argument}, optional={optional}, accepted={accepted}";
        AssertCompilationMatchesCallerContract(
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(
                oracleSource
            ),
            accepted,
            "The handwritten parameter must establish the annotation contract: " + context
        );
        string attribute = optional ? "[DxOptionalParameter(null)]" : "";
        string source = $$"""
#nullable enable
using DxMessaging.Core.Attributes;
[DxAutoConstructor]
public readonly partial struct Packet
{
    public readonly int Required;
    {{attribute}} public readonly {{fieldType}} Value;
}
{{consumer}}
""";
        CSharpCompilation input =
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            input,
            out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Single().Exception,
            Is.Null,
            "Complete constructor generation: " + context
        );
        Assert.That(
            diagnostics,
            Is.Empty,
            "Null defaults remain valid for reference fields: " + context
        );
        AssertCompilationMatchesCallerContract(
            output,
            accepted,
            "Preserve the field's nullable annotations in the generated parameter: " + context
        );
    }

    [TestCaseSource(nameof(DisabledContextCases))]
    public void GeneratedParametersRetainFieldAnnotationContext(
        string fieldType,
        string argument,
        bool optional,
        bool annotationsEnabled,
        bool accepted
    )
    {
        string directive = annotationsEnabled ? "#nullable enable" : "#nullable disable";
        string consumer = $$"""
#nullable enable
public static class Consumer
{
    public static object? Read() => new Packet(1, {{argument}}).Value;
}
""";
        string oracleSource = $$"""
{{directive}}
public readonly struct Packet
{
    public readonly {{fieldType}} Value;
    public Packet(int Required, {{fieldType}} Value) { this.Value = Value; }
}
{{consumer}}
""";
        string context =
            $"type={fieldType}, argument={argument}, optional={optional}, annotationsEnabled={annotationsEnabled}, accepted={accepted}";
        AssertCompilationMatchesCallerContract(
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(
                oracleSource
            ),
            accepted,
            "Establish the handwritten annotation context: " + context
        );
        string attribute = optional
            ? "[DxMessaging.Core.Attributes.DxOptionalParameter(null)]"
            : "";
        string source = $$"""
{{directive}}
[DxMessaging.Core.Attributes.DxAutoConstructor]
public readonly partial struct Packet
{
    public readonly int Required;
    {{attribute}} public readonly {{fieldType}} Value;
}
{{consumer}}
""";
        AssertGeneratedCallerMatchesContract(source, accepted, context);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void MixedAnnotationContextsKeepOnlyTheStrictCallerChecked(
        bool optional,
        bool obliviousFirst
    )
    {
        string attribute = optional
            ? "[DxMessaging.Core.Attributes.DxOptionalParameter(null)]"
            : "";
        string obliviousField =
            $"\n#nullable disable\n{attribute} public readonly string Oblivious;\n";
        string strictField = $"\n#nullable enable\n{attribute} public readonly string Strict;\n";
        string fields = obliviousFirst
            ? obliviousField + strictField
            : strictField + obliviousField;
        string parameters = obliviousFirst
            ? "\n#nullable disable\nstring Oblivious,\n#nullable enable\nstring Strict\n"
            : "\n#nullable enable\nstring Strict,\n#nullable disable\nstring Oblivious\n";
        foreach (bool accepted in new[] { false, true })
        {
            string strictArgument = accepted ? "\"value\"" : "null";
            string consumer = $$"""
#nullable enable
public static class Consumer
{
    public static object? Read() => new Packet(1, Oblivious: null, Strict: {{strictArgument}}).Oblivious;
}
""";
            string oracleSource = $$"""
#nullable enable
public readonly struct Packet
{
    public readonly int Required;
#nullable disable
    public readonly string Oblivious;
#nullable enable
    public readonly string Strict;
    public Packet(int Required, {{parameters}}
#nullable enable
    ) { this.Required = Required; this.Oblivious = Oblivious; this.Strict = Strict; }
}
{{consumer}}
""";
            string context =
                $"optional={optional}, obliviousFirst={obliviousFirst}, accepted={accepted}";
            AssertCompilationMatchesCallerContract(
                GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(
                    oracleSource
                ),
                accepted,
                "Establish mixed handwritten parameter contexts: " + context
            );
            string source = $$"""
#nullable enable
[DxMessaging.Core.Attributes.DxAutoConstructor]
public readonly partial struct Packet
{
    public readonly int Required;
{{fields}}
}
{{consumer}}
""";
            AssertGeneratedCallerMatchesContract(source, accepted, context);
        }
    }

    private static void AssertGeneratedCallerMatchesContract(
        string source,
        bool accepted,
        string context
    )
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(source),
            out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Single().Exception,
            Is.Null,
            "Complete context-preserving constructor generation: " + context
        );
        Assert.That(diagnostics, Is.Empty, "Keep reference defaults valid: " + context);
        AssertCompilationMatchesCallerContract(
            output,
            accepted,
            "Retain each field's declared annotation context: " + context
        );
    }

    private static IEnumerable<TestCaseData> DisabledContextCases()
    {
        foreach (
            (string type, string argument, string name) in new (string, string, string)[]
            {
                ("string", "null", "StringNull"),
                ("object", "null", "ObjectNull"),
                ("int[]", "null", "ArrayNull"),
                ("System.Collections.Generic.List<string>", "null", "ListNull"),
                ("string[]", "new string?[] { null }", "ArrayElements"),
                (
                    "System.Collections.Generic.List<string>",
                    "new System.Collections.Generic.List<string?> { null }",
                    "ListElements"
                ),
            }
        )
        {
            foreach (bool optional in new[] { false, true })
            {
                yield return new TestCaseData(type, argument, optional, false, true).SetName(
                    "DisabledContext" + name + (optional ? "Optional" : "Required")
                );
            }
        }
        foreach (string type in new[] { "string", "object" })
        {
            foreach (bool optional in new[] { false, true })
            {
                yield return new TestCaseData(type, "null", optional, true, false).SetName(
                    "EnabledContext" + type + (optional ? "Optional" : "Required")
                );
            }
        }
    }

    private static void AssertCompilationMatchesCallerContract(
        Compilation compilation,
        bool accepted,
        string context
    )
    {
        Diagnostic[] diagnostics = compilation.GetDiagnostics().ToArray();
        if (accepted)
        {
            Assert.That(
                diagnostics,
                Is.Empty,
                context + "\n" + string.Join("\n", diagnostics.Select(d => d.ToString()))
            );
            return;
        }
        Assert.That(diagnostics, Is.Not.Empty, "Reject the nonnullable caller: " + context);
        Assert.That(
            diagnostics.All(d => d.Id is "CS8604" or "CS8620" or "CS8625" or "CS8619"),
            Is.True,
            "Only the intended nullability errors may reject the caller: "
                + context
                + "\n"
                + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );
    }

    private static IEnumerable<TestCaseData> NullableFieldCases()
    {
        foreach (
            (string type, string argument, bool accepted, string name) in new (
                string,
                string,
                bool,
                string
            )[]
            {
                ("string?", "null", true, "NullableString"),
                ("object?", "null", true, "NullableObject"),
                ("int[]?", "null", true, "NullableArray"),
                ("string?[]?", "null", true, "NullableArrayAndElements"),
                (
                    "System.Collections.Generic.List<string?>?",
                    "null",
                    true,
                    "NullableListAndElements"
                ),
                ("string?[]", "new string?[] { null }", true, "NullableArrayElements"),
                (
                    "System.Collections.Generic.List<string?>",
                    "new System.Collections.Generic.List<string?> { null }",
                    true,
                    "NullableListElements"
                ),
                ("string", "null", false, "NonNullableString"),
                ("object", "null", false, "NonNullableObject"),
                ("int[]", "null", false, "NonNullableArray"),
                ("string[]", "new string?[] { null }", false, "NonNullableArrayElements"),
                (
                    "System.Collections.Generic.List<string>",
                    "new System.Collections.Generic.List<string?> { null }",
                    false,
                    "NonNullableListElements"
                ),
            }
        )
        {
            yield return new TestCaseData(type, argument, false, accepted).SetName(
                name + "RequiredConstructorPreservesNullability"
            );
            yield return new TestCaseData(type, argument, true, accepted).SetName(
                name + "OptionalConstructorPreservesNullability"
            );
        }
    }
}
