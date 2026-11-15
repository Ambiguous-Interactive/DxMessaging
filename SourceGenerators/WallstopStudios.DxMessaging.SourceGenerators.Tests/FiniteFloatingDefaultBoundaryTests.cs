using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

// cspell:ignore FFEFFFFFFFFFFFFF
namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>Checks finite endpoint defaults against handwritten callers and fixed bit patterns.</summary>
[TestFixture]
internal sealed class FiniteFloatingDefaultBoundaryTests
{
    /// <summary>Executes a handwritten oracle before generating each constructor.</summary>
    [TestCaseSource(nameof(Cases))]
    public void FiniteDefaultsPreserveCallerAndMetadataBits(
        string label,
        string fieldType,
        string expression,
        string expectedBits,
        string target,
        bool namedExpression,
        string culture
    )
    {
        string context =
            $"label={label}, fieldType={fieldType}, expression={expression}, expectedBits={expectedBits}, target={target}, namedExpression={namedExpression}, culture={culture}";
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CSharpCompilation Create(bool manual)
            {
                string declaration =
                    target == "class" ? "partial class" : "readonly partial struct";
                string argument = namedExpression
                    ? "Expression = " + SyntaxFactory.Literal(expression)
                    : expression;
                string source = $$"""
using DxMessaging.Core.Attributes;
{{(manual ? "" : "[DxAutoConstructor]")}}
public {{declaration}} Packet
{
    public readonly int Required;
    [DxOptionalParameter({{argument}})] public readonly {{fieldType}} Value;
    {{(
                        manual
                            ? $"public Packet(int Required, {fieldType} Value = {expression}) {{ this.Required = Required; this.Value = Value; }}"
                            : ""
                    )}}
}
public static class Consumer
{
    public static object[] Read()
    {
        Packet omitted = new Packet(1);
        Packet explicitZero = new Packet(2, default({{fieldType}}));
        return new object[] { omitted.Required, omitted.Value, explicitZero.Required, explicitZero.Value };
    }
}
""";
                CSharpCompilation compilation =
                    GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(
                        source
                    );
                Assert.That(
                    compilation.GetTypeByMetadataName("Packet")!.IsReadOnly,
                    Is.EqualTo(target == "readonly struct"),
                    "Retain the declared readonly target shape: " + context
                );
                return compilation;
            }

            AssertCallerAndMetadata(
                Create(manual: true),
                fieldType,
                expectedBits,
                "Manual: " + context
            );
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
                parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
            );
            driver = driver.RunGeneratorsAndUpdateCompilation(
                Create(manual: false),
                out Compilation generated,
                out ImmutableArray<Diagnostic> diagnostics
            );
            Assert.That(driver.GetRunResult().Results.Single().Exception, Is.Null, context);
            Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), context);
            Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics));
            AssertCallerAndMetadata(generated, fieldType, expectedBits, "Generated: " + context);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>Declares finite boundary representations independently of emitted source.</summary>
    private static IEnumerable<TestCaseData> Cases()
    {
        (string Label, string Type, string Expression, string Bits)[] values =
        {
            ("FloatSmallestSubnormal", "float", "float.Epsilon", "00000001"),
            ("FloatLargestSubnormal", "float", "1.1754942E-38f", "007FFFFF"),
            ("FloatSmallestNormal", "float", "1.17549435E-38f", "00800000"),
            ("FloatLargestFinite", "float", "float.MaxValue", "7F7FFFFF"),
            ("FloatNegativeSmallestSubnormal", "float", "-float.Epsilon", "80000001"),
            ("FloatNegativeLargestSubnormal", "float", "-1.1754942E-38f", "807FFFFF"),
            ("FloatNegativeSmallestNormal", "float", "-1.17549435E-38f", "80800000"),
            ("FloatNegativeLargestFinite", "float", "float.MinValue", "FF7FFFFF"),
            ("DoubleSmallestSubnormal", "double", "double.Epsilon", "0000000000000001"),
            ("DoubleLargestSubnormal", "double", "2.2250738585072009E-308d", "000FFFFFFFFFFFFF"),
            ("DoubleSmallestNormal", "double", "2.2250738585072014E-308d", "0010000000000000"),
            ("DoubleLargestFinite", "double", "double.MaxValue", "7FEFFFFFFFFFFFFF"),
            ("DoubleNegativeSmallestSubnormal", "double", "-double.Epsilon", "8000000000000001"),
            (
                "DoubleNegativeLargestSubnormal",
                "double",
                "-2.2250738585072009E-308d",
                "800FFFFFFFFFFFFF"
            ),
            (
                "DoubleNegativeSmallestNormal",
                "double",
                "-2.2250738585072014E-308d",
                "8010000000000000"
            ),
            ("DoubleNegativeLargestFinite", "double", "double.MinValue", "FFEFFFFFFFFFFFFF"),
        };
        foreach ((string label, string type, string expression, string bits) in values)
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (bool named in new[] { false, true })
                {
                    foreach (string culture in new[] { "en-US", "fr-FR" })
                    {
                        yield return new TestCaseData(
                            label,
                            type,
                            expression,
                            bits,
                            target,
                            named,
                            culture
                        ).SetName($"FiniteDefault({label},{target},Expression={named},{culture})");
                    }
                }
            }
        }
    }

    /// <summary>Checks compiled calls and reflected optional metadata, without source-text oracles.</summary>
    private static void AssertCallerAndMetadata(
        Compilation compilation,
        string fieldType,
        string expectedBits,
        string context
    )
    {
        Assert.That(
            compilation.GetDiagnostics(),
            Is.Empty,
            context + "\n" + string.Join("\n", compilation.GetDiagnostics())
        );
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emit = compilation.Emit(image);
        Assert.That(emit.Success, Is.True, context + "\n" + string.Join("\n", emit.Diagnostics));
        image.Position = 0;
        AssemblyLoadContext load = new(context, isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            object[] values = (object[])
                assembly.GetType("Consumer")!.GetMethod("Read")!.Invoke(null, null)!;
            Assert.That(values[0], Is.EqualTo(1), "Omitted call runs the constructor: " + context);
            Assert.That(
                Bits(values[1], fieldType),
                Is.EqualTo(expectedBits),
                "Caller bits: " + context
            );
            Assert.That(values[2], Is.EqualTo(2), "Explicit call runs the constructor: " + context);
            Assert.That(
                Bits(values[3], fieldType),
                Is.EqualTo(new string('0', expectedBits.Length)),
                "Explicit zero: " + context
            );
            ParameterInfo parameter = assembly
                .GetType("Packet")!
                .GetConstructors()
                .Single()
                .GetParameters()[1];
            Assert.That(parameter.IsOptional, Is.True, "Optional metadata: " + context);
            Assert.That(parameter.HasDefaultValue, Is.True, "Default metadata exists: " + context);
            Assert.That(
                Bits(parameter.DefaultValue!, fieldType),
                Is.EqualTo(expectedBits),
                "Default metadata bits: " + context
            );
        }
        finally
        {
            load.Unload();
        }
    }

    /// <summary>Reports the observed representation without parsing generated numeric literals.</summary>
    private static string Bits(object value, string fieldType) =>
        fieldType == "float"
            ? BitConverter
                .SingleToInt32Bits((float)value)
                .ToString("X8", CultureInfo.InvariantCulture)
            : BitConverter
                .DoubleToInt64Bits((double)value)
                .ToString("X16", CultureInfo.InvariantCulture);
}
