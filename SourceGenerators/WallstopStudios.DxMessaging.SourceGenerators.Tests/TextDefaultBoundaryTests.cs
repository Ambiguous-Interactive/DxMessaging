using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

// cspell:ignore DFFF
namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>Checks text default values against compiled callers and fixed UTF16 code units.</summary>
[TestFixture]
internal sealed class TextDefaultBoundaryTests
{
    /// <summary>Runs a handwritten constructor control before the real generator for each boundary.</summary>
    [TestCaseSource(nameof(Cases))]
    public void TextDefaultsPreserveCallerAndMetadataCodeUnits(
        string label,
        string fieldType,
        string expression,
        string expectedUnits,
        string target,
        bool namedExpression
    )
    {
        string context =
            $"label={label}, fieldType={fieldType}, expression={expression}, expectedUnits={expectedUnits}, target={target}, namedExpression={namedExpression}";
        CSharpCompilation Create(bool manual)
        {
            string declaration = target == "class" ? "partial class" : "readonly partial struct";
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
        Packet explicitDefault = new Packet(2, default({{fieldType}}));
        return new object[] { omitted.Required, omitted.Value, explicitDefault.Required, explicitDefault.Value };
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
                "Retain the declared target shape: " + context
            );
            return compilation;
        }

        AssertCallerAndMetadata(
            Create(manual: true),
            fieldType,
            expectedUnits,
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
        AssertCallerAndMetadata(generated, fieldType, expectedUnits, "Generated: " + context);
    }

    /// <summary>Declares independent expected code units for C#9 literal boundaries.</summary>
    // SYNC: GeneratedTextDefaultTests.DefaultCases consumes the same fixed boundary roster.
    private static IEnumerable<TestCaseData> Cases()
    {
        (string Label, string Type, string Expression, string Units)[] values =
        {
            ("CharZero", "char", "'\\0'", "0000"),
            ("CharSingleQuote", "char", "'\\\''", "0027"),
            ("CharDoubleQuote", "char", "'\"'", "0022"),
            ("CharBackslash", "char", "'\\\\'", "005C"),
            ("CharTab", "char", "'\\t'", "0009"),
            ("CharLineFeed", "char", "'\\n'", "000A"),
            ("CharCarriageReturn", "char", "'\\r'", "000D"),
            ("CharNextLine", "char", "'\\u0085'", "0085"),
            ("CharLineSeparator", "char", "'\\u2028'", "2028"),
            ("CharParagraphSeparator", "char", "'\\u2029'", "2029"),
            ("CharHighSurrogate", "char", "'\\uD800'", "D800"),
            ("CharLowSurrogate", "char", "'\\uDFFF'", "DFFF"),
            ("CharMaximum", "char", "'\\uFFFF'", "FFFF"),
            ("CharOrdinary", "char", "'A'", "0041"),
            ("StringNull", "string", "(string)null", "<null>"),
            ("StringEmpty", "string", "\"\"", ""),
            ("StringQuoteAndBackslash", "string", "\"\\\"\\\\\"", "0022-005C"),
            ("StringEmbeddedZero", "string", "\"A\\0B\"", "0041-0000-0042"),
            ("StringCarriageReturnLineFeed", "string", "\"A\\r\\nB\"", "0041-000D-000A-0042"),
            (
                "StringLiteralBackslashUnicodeText",
                "string",
                "@\"\\u0041\"",
                "005C-0075-0030-0030-0034-0031"
            ),
            ("StringSeparators", "string", "\"\\u0085\\u2028\\u2029\"", "0085-2028-2029"),
            ("StringHighSurrogate", "string", "\"\\uD800\"", "D800"),
            ("StringLowSurrogate", "string", "\"\\uDFFF\"", "DFFF"),
            ("StringSupplementaryPair", "string", "\"\\U0001F680\"", "D83D-DE80"),
        };
        foreach ((string label, string type, string expression, string units) in values)
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (bool named in new[] { false, true })
                {
                    yield return new TestCaseData(
                        label,
                        type,
                        expression,
                        units,
                        target,
                        named
                    ).SetName($"TextDefault({label},{target},Expression={named})");
                }
            }
        }
    }

    /// <summary>Checks execution and reflected metadata without comparing emitted source text.</summary>
    private static void AssertCallerAndMetadata(
        Compilation compilation,
        string fieldType,
        string expectedUnits,
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
                Units(values[1]),
                Is.EqualTo(expectedUnits),
                "Caller code units: " + context
            );
            Assert.That(values[2], Is.EqualTo(2), "Explicit call runs the constructor: " + context);
            Assert.That(
                Units(values[3]),
                Is.EqualTo(fieldType == "char" ? "0000" : "<null>"),
                "Explicit default: " + context
            );
            ParameterInfo parameter = assembly
                .GetType("Packet")!
                .GetConstructors()
                .Single()
                .GetParameters()[1];
            Assert.That(parameter.IsOptional, Is.True, "Optional metadata: " + context);
            Assert.That(parameter.HasDefaultValue, Is.True, "Default metadata exists: " + context);
            Assert.That(
                Units(parameter.DefaultValue),
                Is.EqualTo(expectedUnits),
                "Default metadata code units: " + context
            );
        }
        finally
        {
            load.Unload();
        }
    }

    /// <summary>Reads raw UTF16 units directly, preserving isolated surrogates and null.</summary>
    private static string Units(object? value)
    {
        if (value == null)
        {
            return "<null>";
        }
        if (value is char character)
        {
            return ((int)character).ToString("X4", CultureInfo.InvariantCulture);
        }
        Assert.That(
            value,
            Is.TypeOf<string>(),
            "Text boundary values must be char, string, or null."
        );
        StringBuilder units = new();
        foreach (char unit in (string)value)
        {
            if (units.Length != 0)
            {
                units.Append('-');
            }
            units.Append(((int)unit).ToString("X4", CultureInfo.InvariantCulture));
        }
        return units.ToString();
    }
}
