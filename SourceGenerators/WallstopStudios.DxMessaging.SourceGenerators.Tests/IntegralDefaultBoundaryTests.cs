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

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>Checks integral endpoint defaults against handwritten callers and fixed decimal values.</summary>
[TestFixture]
internal sealed class IntegralDefaultBoundaryTests
{
    /// <summary>Executes a handwritten oracle before generating each constructor.</summary>
    [TestCaseSource(nameof(Cases))]
    public void IntegralDefaultsPreserveCallerAndMetadataValues(
        string label,
        string fieldType,
        string expression,
        string expectedValue,
        string metadataType,
        string target,
        bool namedExpression,
        bool hostileCulture
    )
    {
        string context =
            $"label={label}, fieldType={fieldType}, expression={expression}, expectedValue={expectedValue}, target={target}, namedExpression={namedExpression}, hostileCulture={hostileCulture}, metadataType={metadataType}";
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo testCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            if (hostileCulture)
            {
                testCulture.NumberFormat.NegativeSign = "NEG";
            }
            CultureInfo.CurrentCulture = testCulture;
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
                expectedValue,
                metadataType,
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
            AssertCallerAndMetadata(
                generated,
                expectedValue,
                metadataType,
                "Generated: " + context
            );
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>Declares signed and unsigned endpoints independently of emitted literals.</summary>
    // SYNC: GeneratedIntegralDefaultTests.Boundaries uses this fixed endpoint roster.
    private static IEnumerable<TestCaseData> Cases()
    {
        (string Label, string Type, string Expression, string Value, string MetadataType)[] values =
        {
            ("SByteMin", "sbyte", "sbyte.MinValue", "-128", "System.SByte"),
            ("SByteMax", "sbyte", "sbyte.MaxValue", "127", "System.SByte"),
            ("ByteMin", "byte", "byte.MinValue", "0", "System.Byte"),
            ("ByteMax", "byte", "byte.MaxValue", "255", "System.Byte"),
            ("Int16Min", "short", "short.MinValue", "-32768", "System.Int16"),
            ("Int16Max", "short", "short.MaxValue", "32767", "System.Int16"),
            ("UInt16Min", "ushort", "ushort.MinValue", "0", "System.UInt16"),
            ("UInt16Max", "ushort", "ushort.MaxValue", "65535", "System.UInt16"),
            ("Int32Min", "int", "int.MinValue", "-2147483648", "System.Int32"),
            ("Int32Max", "int", "int.MaxValue", "2147483647", "System.Int32"),
            ("UInt32Min", "uint", "uint.MinValue", "0", "System.UInt32"),
            ("UInt32Max", "uint", "uint.MaxValue", "4294967295", "System.UInt32"),
            ("Int64Min", "long", "long.MinValue", "-9223372036854775808", "System.Int64"),
            ("Int64Max", "long", "long.MaxValue", "9223372036854775807", "System.Int64"),
            ("UInt64Min", "ulong", "ulong.MinValue", "0", "System.UInt64"),
            ("UInt64Max", "ulong", "ulong.MaxValue", "18446744073709551615", "System.UInt64"),
        };
        foreach (
            (
                string label,
                string type,
                string expression,
                string value,
                string metadataType
            ) in values
        )
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (bool named in new[] { false, true })
                {
                    foreach (bool hostileCulture in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            label,
                            type,
                            expression,
                            value,
                            metadataType,
                            target,
                            named,
                            hostileCulture
                        ).SetName(
                            $"IntegralDefault({label},{target},Expression={named},HostileCulture={hostileCulture})"
                        );
                    }
                }
            }
        }
    }

    /// <summary>Checks compiled calls and reflected optional metadata against fixed values.</summary>
    private static void AssertCallerAndMetadata(
        Compilation compilation,
        string expectedValue,
        string metadataType,
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
                NumericValue(values[1], metadataType, context),
                Is.EqualTo(expectedValue),
                "Caller value: " + context
            );
            Assert.That(values[2], Is.EqualTo(2), "Explicit call runs the constructor: " + context);
            Assert.That(
                NumericValue(values[3], metadataType, context),
                Is.EqualTo("0"),
                "Explicit zero: " + context
            );
            ParameterInfo parameter = assembly
                .GetType("Packet")!
                .GetConstructors()
                .Single()
                .GetParameters()[1];
            Assert.That(
                parameter.ParameterType.FullName,
                Is.EqualTo(metadataType),
                "Declared parameter type: " + context
            );
            Assert.That(
                assembly.GetType("Packet")!.GetField("Value")!.FieldType.FullName,
                Is.EqualTo(metadataType),
                "Declared field type: " + context
            );
            Assert.That(parameter.IsOptional, Is.True, "Optional metadata: " + context);
            Assert.That(parameter.HasDefaultValue, Is.True, "Default metadata exists: " + context);
            Assert.That(
                NumericValue(parameter.DefaultValue!, metadataType, context),
                Is.EqualTo(expectedValue),
                "Default metadata value: " + context
            );
        }
        finally
        {
            load.Unload();
        }
    }

    /// <summary>Checks exact boxed type before reporting the observed integral value.</summary>
    private static string NumericValue(object value, string metadataType, string context)
    {
        Assert.That(
            value.GetType().FullName,
            Is.EqualTo(metadataType),
            "Boxed integral type: " + context
        );
        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }
}
