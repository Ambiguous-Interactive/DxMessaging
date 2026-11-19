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

/// <summary>Checks nullable enum presence and full-width defaults against handwritten constructors.</summary>
[TestFixture]
internal sealed class NullableEnumDefaultBoundaryTests
{
    /// <summary>Runs the handwritten caller and metadata oracle before the real generator.</summary>
    [TestCaseSource(nameof(Cases))]
    public void NullableEnumDefaultsPreservePresenceAndUnderlyingValue(
        string label,
        string underlying,
        string literal,
        string expected,
        string metadataType,
        string target,
        bool named
    )
    {
        string context =
            $"label={label}, underlying={underlying}, literal={literal}, expected={expected}, metadataType={metadataType}, target={target}, named={named}";
        string expression = named ? "Choice.Endpoint" : "(Choice)(" + literal + ")";
        CSharpCompilation Create(bool manual)
        {
            string declaration = target == "class" ? "partial class" : "readonly partial struct";
            string source = $$"""
using DxMessaging.Core.Attributes;
public enum Choice : {{underlying}} { Endpoint = {{literal}} }
{{(manual ? "" : "[DxAutoConstructor]")}}
public {{declaration}} Packet
{
    public readonly int Required;
    [DxOptionalParameter(Expression = {{SyntaxFactory.Literal(
                    expression
                )}})] public readonly Choice? Value;
    [DxOptionalParameter] public readonly Choice? Empty;
    {{(
                    manual
                        ? $"public Packet(int Required, Choice? Value = {expression}, Choice? Empty = null) {{ this.Required = Required; this.Value = Value; this.Empty = Empty; }}"
                        : ""
                )}}
}
public static class Consumer
{
    public static object[] Read()
    {
        Packet omitted = new Packet(1);
        Packet explicitNull = new Packet(2, null);
        Packet explicitZero = new Packet(3, default(Choice));
        return new object[] { omitted.Required, omitted.Value, explicitNull.Required, explicitNull.Value, explicitZero.Required, explicitZero.Value, omitted.Empty };
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
                "Preserve target shape: " + context
            );
            return compilation;
        }

        AssertCallerAndMetadata(Create(manual: true), expected, metadataType, "Manual: " + context);
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
        AssertCallerAndMetadata(generated, expected, metadataType, "Generated: " + context);
    }

    // SYNC: GeneratedNullableEnumDefaultTests.Boundaries uses the same fixed endpoint roster.
    private static IEnumerable<TestCaseData> Cases()
    {
        (
            string Label,
            string Underlying,
            string Literal,
            string Value,
            string MetadataType
        )[] values =
        {
            ("SByteMin", "sbyte", "-128", "-128", "System.SByte"),
            ("SByteMax", "sbyte", "127", "127", "System.SByte"),
            ("ByteMin", "byte", "0", "0", "System.Byte"),
            ("ByteMax", "byte", "255", "255", "System.Byte"),
            ("Int16Min", "short", "-32768", "-32768", "System.Int16"),
            ("Int16Max", "short", "32767", "32767", "System.Int16"),
            ("UInt16Min", "ushort", "0", "0", "System.UInt16"),
            ("UInt16Max", "ushort", "65535", "65535", "System.UInt16"),
            ("Int32Min", "int", "-2147483648", "-2147483648", "System.Int32"),
            ("Int32Max", "int", "2147483647", "2147483647", "System.Int32"),
            ("UInt32Min", "uint", "0u", "0", "System.UInt32"),
            ("UInt32Max", "uint", "4294967295u", "4294967295", "System.UInt32"),
            ("Int64Min", "long", "-9223372036854775808L", "-9223372036854775808", "System.Int64"),
            ("Int64Max", "long", "9223372036854775807L", "9223372036854775807", "System.Int64"),
            ("UInt64Min", "ulong", "0UL", "0", "System.UInt64"),
            (
                "UInt64Max",
                "ulong",
                "18446744073709551615UL",
                "18446744073709551615",
                "System.UInt64"
            ),
        };
        foreach (
            (
                string label,
                string underlying,
                string literal,
                string value,
                string metadataType
            ) in values
        )
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (bool named in new[] { false, true })
                {
                    yield return new TestCaseData(
                        label,
                        underlying,
                        literal,
                        value,
                        metadataType,
                        target,
                        named
                    ).SetName($"NullableEnumDefault({label},{target},Named={named})");
                }
            }
        }
    }

    private static void AssertCallerAndMetadata(
        Compilation compilation,
        string expected,
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
            Type enumType = assembly.GetType("Choice")!;
            Assert.That(enumType.IsEnum, Is.True, "Retain nominal enum declaration: " + context);
            Assert.That(
                Enum.GetUnderlyingType(enumType).FullName,
                Is.EqualTo(metadataType),
                "Declared width: " + context
            );
            Type packet = assembly.GetType("Packet")!;
            object[] values = (object[])
                assembly.GetType("Consumer")!.GetMethod("Read")!.Invoke(null, null)!;
            Assert.That(values[0], Is.EqualTo(1), "Omitted call runs constructor: " + context);
            AssertEnumValue(values[1], enumType, expected, metadataType, "Caller: " + context);
            Assert.That(
                values[2],
                Is.EqualTo(2),
                "Explicit null call runs constructor: " + context
            );
            Assert.That(values[3], Is.Null, "Explicit null stays absent: " + context);
            Assert.That(
                values[4],
                Is.EqualTo(3),
                "Explicit zero call runs constructor: " + context
            );
            AssertEnumValue(values[5], enumType, "0", metadataType, "Explicit zero: " + context);
            Assert.That(values[6], Is.Null, "Omitted null default stays absent: " + context);
            ParameterInfo parameter = packet.GetConstructors().Single().GetParameters()[1];
            Assert.That(
                Nullable.GetUnderlyingType(parameter.ParameterType),
                Is.EqualTo(enumType),
                "Nominal parameter type: " + context
            );
            Assert.That(
                Nullable.GetUnderlyingType(packet.GetField("Value")!.FieldType),
                Is.EqualTo(enumType),
                "Nominal field type: " + context
            );
            Assert.That(parameter.IsOptional, Is.True, "Optional metadata: " + context);
            Assert.That(parameter.HasDefaultValue, Is.True, "Default metadata exists: " + context);
            AssertNumber(parameter.DefaultValue!, expected, metadataType, "Default: " + context);
            AssertNumber(
                parameter.RawDefaultValue!,
                expected,
                metadataType,
                "Raw default: " + context
            );
            ParameterInfo empty = packet.GetConstructors().Single().GetParameters()[2];
            Assert.That(
                Nullable.GetUnderlyingType(empty.ParameterType),
                Is.EqualTo(enumType),
                "Null parameter type: " + context
            );
            Assert.That(
                Nullable.GetUnderlyingType(packet.GetField("Empty")!.FieldType),
                Is.EqualTo(enumType),
                "Null field type: " + context
            );
            Assert.That(empty.IsOptional, Is.True, "Null default is optional: " + context);
            Assert.That(empty.HasDefaultValue, Is.True, "Null default exists: " + context);
            Assert.That(empty.DefaultValue, Is.Null, "Null default metadata: " + context);
            Assert.That(empty.RawDefaultValue, Is.Null, "Null raw metadata: " + context);
        }
        finally
        {
            load.Unload();
        }
    }

    private static void AssertEnumValue(
        object value,
        Type enumType,
        string expected,
        string metadataType,
        string context
    )
    {
        Assert.That(value, Is.Not.Null, "Present nullable enum: " + context);
        Assert.That(value.GetType(), Is.EqualTo(enumType), "Exact boxed enum type: " + context);
        AssertNumber(
            Convert.ChangeType(
                value,
                Enum.GetUnderlyingType(enumType),
                CultureInfo.InvariantCulture
            ),
            expected,
            metadataType,
            context
        );
    }

    private static void AssertNumber(
        object value,
        string expected,
        string metadataType,
        string context
    )
    {
        Assert.That(
            value.GetType().FullName,
            Is.EqualTo(metadataType),
            "Exact boxed underlying type: " + context
        );
        Assert.That(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            Is.EqualTo(expected),
            "Exact value: " + context
        );
    }
}
