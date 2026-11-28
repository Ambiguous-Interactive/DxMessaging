using System.Collections.Generic;
using System.Collections.Immutable;
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
internal sealed class NullableOptionalDefaultTests
{
    [TestCaseSource(nameof(NullDefaultCases))]
    public void OptionalNullDefaultsMatchHandwrittenConstructors(string fieldType, string explicitValue, int attributeForm, bool nullable, bool valueTarget)
    {
        string context = $"type={fieldType},value={explicitValue},attribute={attributeForm},nullable={nullable},struct={valueTarget}";
        string kind = valueTarget ? "readonly partial struct" : "partial class";
        string defaultExpression = attributeForm == 2 ? $"default({fieldType})" : "null";
        string attribute = attributeForm switch
        {
            0 => "[DxOptionalParameter(null)]",
            1 => "[DxOptionalParameter(Expression = \"null\")]",
            _ => "[DxOptionalParameter]",
        };
        string consumer = nullable
            ? $$"""
public static class Consumer
{
    public static bool Verify()
    {
        Packet omitted = new Packet(17);
        Packet explicitNull = new Packet(17, null);
        Packet supplied = new Packet(17, {{explicitValue}});
        return omitted.Required == 17 && !omitted.Value.HasValue
            && explicitNull.Required == 17 && !explicitNull.Value.HasValue
            && supplied.Required == 17 && supplied.Value.HasValue
            && supplied.Value.Equals({{explicitValue}});
    }
}
"""
            : "";
        string manualSource = Source(
            kind,
            fieldType,
            "",
            $"public Packet(int Required, {fieldType} Value = {defaultExpression}) {{ this.Required = Required; this.Value = Value; }}",
            consumer
        );
        CSharpCompilation manual = Compile(manualSource);
        Diagnostic[] manualDiagnostics = manual.GetDiagnostics().ToArray();
        if (nullable)
        {
            Assert.That(manualDiagnostics, Is.Empty, "Compile handwritten nullable default: " + context);
            VerifyConsumer(manual, "handwritten", context);
        }
        else
        {
            Assert.That(
                manualDiagnostics.Any(diagnostic => diagnostic.Id == "CS1750"),
                Is.True,
                "The handwritten compiler must reject a null default for a value type: " + context
            );
        }

        string generatedSource = Source(kind, fieldType, attribute, "", consumer);
        CSharpCompilation input = Compile(generatedSource);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out Compilation output, out ImmutableArray<Diagnostic> generatorDiagnostics);
        Assert.That(driver.GetRunResult().Results.Single().Exception, Is.Null, "Complete real nullable-default generation: " + context);
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), "Generate one constructor: " + context);
        if (!nullable)
        {
            Assert.That(generatorDiagnostics, Has.Length.EqualTo(1), "Report one invalid null default: " + context);
            Assert.That(generatorDiagnostics[0].Id, Is.EqualTo("DXMSG005"), "Use the default diagnostic: " + context);
            Assert.That(generatorDiagnostics[0].Location.SourceTree, Is.EqualTo(input.SyntaxTrees.Last()), "Report the caller's attribute: " + context);
            Assert.That(output.GetDiagnostics().Any(diagnostic => diagnostic.Id == "CS1750"), Is.True, "Keep compiler rejection of the invalid generated default: " + context);
            return;
        }

        Assert.That(generatorDiagnostics, Is.Empty, "Accept the nullable null default: " + context);
        Assert.That(output.GetDiagnostics(), Is.Empty, "Compile generated nullable consumer: " + context);
        IParameterSymbol expected = Parameter(manual);
        IParameterSymbol actual = Parameter(output);
        Assert.That(actual.IsOptional, Is.EqualTo(expected.IsOptional), "Preserve optional parameter metadata: " + context);
        Assert.That(actual.HasExplicitDefaultValue, Is.True, "Expose a compiled default: " + context);
        Assert.That(actual.ExplicitDefaultValue, Is.EqualTo(expected.ExplicitDefaultValue), "Preserve the handwritten null metadata: " + context);
        Assert.That(
            actual.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Is.EqualTo(expected.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            "Preserve the nullable parameter type: " + context
        );
        VerifyConsumer(output, "generated", context);
    }

    private static IParameterSymbol Parameter(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Packet")!.InstanceConstructors.Single(constructor => constructor.Parameters.Length == 2).Parameters[1];

    private static void VerifyConsumer(Compilation compilation, string arm, string context)
    {
        using MemoryStream stream = new();
        EmitResult emit = compilation.Emit(stream);
        Assert.That(emit.Success, Is.True, $"Emit {arm} nullable consumer: {context}");
        stream.Position = 0;
        AssemblyLoadContext loadContext = new("NullableDefaultConsumer", isCollectible: true);
        try
        {
            Assembly assembly = loadContext.LoadFromStream(stream);
            object? result = assembly.GetType("Consumer")!.GetMethod("Verify")!.Invoke(null, null);
            Assert.That(result, Is.EqualTo(true), $"Preserve omitted, explicit null and supplied values in {arm}: {context}");
            ParameterInfo parameter = assembly.GetType("Packet")!.GetConstructors().Single(constructor => constructor.GetParameters().Length == 2).GetParameters()[1];
            Assert.That(parameter.IsOptional, Is.True, $"Retain optional metadata in {arm}: {context}");
            Assert.That(parameter.HasDefaultValue, Is.True, $"Retain default metadata in {arm}: {context}");
            Assert.That(parameter.DefaultValue, Is.Null, $"Retain null as the nullable default in {arm}: {context}");
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static CSharpCompilation Compile(string source) => GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(source);

    private static string Source(string kind, string fieldType, string fieldAttribute, string constructor, string consumer) => $$"""
{{(constructor.Length == 0 ? "using DxMessaging.Core.Attributes;" : "")}}
public enum Choice { None, Other }
public readonly struct Payload
{
    public readonly int Number;
    public Payload(int number) { Number = number; }
}
{{(constructor.Length == 0 ? "[DxAutoConstructor]" : "")}}
public {{kind}} Packet
{
    public readonly int Required;
    {{fieldAttribute}} public readonly {{fieldType}} Value;
    {{constructor}}
}
{{consumer}}
""";

    private static IEnumerable<TestCaseData> NullDefaultCases()
    {
        foreach (
            (string type, string value) in new (string, string)[]
            {
                ("int", "(int?)41"),
                ("Choice", "(Choice?)Choice.Other"),
                ("System.DateTime", "(System.DateTime?)new System.DateTime(2026, 11, 14)"),
                ("Payload", "(Payload?)new Payload(41)"),
            }
        )
        {
            foreach (bool nullable in new[] { false, true })
            {
                foreach (bool valueTarget in new[] { false, true })
                {
                    for (int form = 0; form < (nullable ? 3 : 2); ++form)
                    {
                        string fieldType = type + (nullable ? "?" : "");
                        yield return new TestCaseData(fieldType, value, form, nullable, valueTarget).SetName(
                            $"OptionalNullDefaultsMatchHandwrittenConstructors({fieldType},{form},{nullable},{valueTarget})"
                        );
                    }
                }
            }
        }
    }
}
