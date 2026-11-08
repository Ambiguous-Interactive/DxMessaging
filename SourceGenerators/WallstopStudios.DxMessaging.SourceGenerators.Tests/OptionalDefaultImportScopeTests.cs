using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>Executes optional defaults in the lexical scope where each field was declared.</summary>
[TestFixture]
internal sealed class OptionalDefaultImportScopeTests
{
    /// <summary>Defines an imported expression and the independently expected caller result.</summary>
    private sealed record Case(
        string Imports,
        string FieldType,
        string Expression,
        string Read,
        object? Expected,
        bool Valid = true
    );

    /// <summary>Compares real generated callers with handwritten constructors using the same imports.</summary>
    /// <param name="name">The expression and import scenario.</param>
    /// <param name="target">The class or value-type declaration.</param>
    /// <param name="split">Whether fields and the attribute use separate partial files.</param>
    [TestCaseSource(nameof(Cases))]
    public void GeneratedDefaultsPreserveTheirFieldsImports(string name, string target, bool split)
    {
        Case item = GetCase(name);
        string context = $"name={name}, target={target}, split={split}";
        CSharpCompilation manual = CreateCompilation(item, target, split, manual: true);
        object[] expected = { 42, item.Expected!, 99 };
        if (item.Valid)
        {
            AssertCallerExecutes(manual, expected, "Handwritten control: " + context);
        }
        else
        {
            Assert.That(
                manual
                    .GetDiagnostics()
                    .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                Is.True,
                "The independent compiler must reject this invalid default: " + context
            );
        }

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(item, target, split, manual: false),
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            "Scope repair must not crash the generator: " + context
        );
        Assert.That(
            driver.GetRunResult().GeneratedTrees,
            Has.Length.EqualTo(1),
            "Keep the field constructor for supported declarations: " + context
        );
        if (item.Valid)
        {
            Assert.That(
                diagnostics,
                Is.Empty,
                "Do not reject a valid field-scope expression: "
                    + context
                    + "\n"
                    + string.Join("\n", diagnostics)
            );
            AssertCallerExecutes(generated, expected, "Generated caller: " + context);
        }
        else
        {
            Assert.That(
                generated
                    .GetDiagnostics()
                    .Concat(diagnostics)
                    .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                Is.True,
                "Do not hide an invalid default when qualifying names or preserving nameof: "
                    + context
            );
        }
    }

    /// <summary>Provides imports, controls, and invalid neighbors across both partial-file layouts.</summary>
    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (
            string name in new[]
            {
                "ImportedEnum",
                "AliasEnum",
                "StaticConstant",
                "AliasNameof",
                "ImportedNameof",
                "DefaultStruct",
                "DefaultArray",
                "DefaultGenericAlias",
                "AliasCast",
                "StaticArithmetic",
                "StaticMethodNameof",
                "BindingNameCollisions",
                "PointerDefault",
                "FunctionPointerDefault",
                "NullableContextChangesAfterField",
                "QualifiedControl",
                "LiteralControl",
                "FloatingNegativeZero",
                "DecimalNegativeZero",
                "DecimalPrecision",
                "MissingMember",
                "Nonconstant",
                "WrongType",
                "Overflow",
                "InvalidNameofMember",
                "InvalidNameofExpression",
            }
        )
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (bool split in new[] { false, true })
                {
                    yield return new TestCaseData(name, target, split).SetName(
                        $"GeneratedDefaultsPreserveTheirFieldsImports({name},{target},{split})"
                    );
                }
            }
        }
    }

    /// <summary>Maps each scenario to its actual source expression and observable value.</summary>
    private static Case GetCase(string name) =>
        name switch
        {
            "ImportedEnum" => new("using Foreign;", "Mode", "Mode.Other", "(int)packet.Value", 37),
            "AliasEnum" => new(
                "using Option = Foreign.Mode;",
                "Option",
                "Option.Other",
                "(int)packet.Value",
                37
            ),
            "StaticConstant" => new(
                "using static Foreign.Defaults;",
                "int",
                "Number",
                "packet.Value",
                37
            ),
            "AliasNameof" => new(
                "using Alias = Foreign.Mode;",
                "string",
                "nameof(Alias)",
                "packet.Value",
                "Alias"
            ),
            "ImportedNameof" => new(
                "using Foreign;",
                "string",
                "nameof(Mode.Other)",
                "packet.Value",
                "Other"
            ),
            "DefaultStruct" => new(
                "using Alias = Foreign.State;",
                "Alias",
                "default(Alias)",
                "packet.Value.Number",
                0
            ),
            "DefaultArray" => new(
                "using Alias = Foreign.Mode;",
                "Alias[]",
                "default(Alias[])",
                "packet.Value == null",
                true
            ),
            "DefaultGenericAlias" => new(
                "using Alias = System.Collections.Generic.List<int>;",
                "Alias",
                "default(Alias)",
                "packet.Value == null",
                true
            ),
            "AliasCast" => new(
                "using Alias = Foreign.Mode;",
                "Alias",
                "(Alias)37",
                "(int)packet.Value",
                37
            ),
            "StaticArithmetic" => new(
                "using static Foreign.Defaults;",
                "int",
                "Number + 5",
                "packet.Value",
                42
            ),
            "StaticMethodNameof" => new(
                "using static Foreign.Defaults;",
                "string",
                "nameof(Read)",
                "packet.Value",
                "Read"
            ),
            "BindingNameCollisions" => new(
                "using static Foreign.Defaults;",
                "int",
                "__dxMessagingOptionalDefault",
                "packet.Value",
                37
            ),
            "QualifiedControl" => new(
                "",
                "global::Foreign.Mode",
                "global::Foreign.Mode.Other",
                "(int)packet.Value",
                37
            ),
            "PointerDefault" => new(
                "using Foreign;",
                "Mode*",
                "default(Mode*)",
                "packet.Value == null",
                true
            ),
            "NullableContextChangesAfterField" => new(
                "using Alias = Foreign.Mode;",
                "string?",
                "nameof(Alias)",
                "packet.Value",
                "Alias"
            ),
            "FunctionPointerDefault" => new(
                "using Foreign;",
                "delegate*<Mode, int>",
                "default(delegate*<Mode, int>)",
                "packet.Value == null",
                true
            ),
            "LiteralControl" => new("", "int", "37", "packet.Value", 37),
            "FloatingNegativeZero" => new(
                "using static Foreign.Defaults;",
                "float",
                "NegativeZero",
                "global::System.BitConverter.SingleToInt32Bits(packet.Value)",
                int.MinValue
            ),
            "DecimalNegativeZero" => new(
                "using static Foreign.Defaults;",
                "decimal",
                "NegativeDecimalZero",
                "global::System.Decimal.GetBits(packet.Value)[3]",
                unchecked((int)0x80010000)
            ),
            "DecimalPrecision" => new(
                "using static Foreign.Defaults;",
                "decimal",
                "PreciseDecimal",
                "packet.Value",
                1.2345678901234567890123456789m
            ),
            "MissingMember" => new(
                "using Alias = Foreign.Defaults;",
                "int",
                "Alias.Missing",
                "packet.Value",
                null,
                false
            ),
            "Nonconstant" => new(
                "using Alias = Foreign.Defaults;",
                "int",
                "Alias.Read()",
                "packet.Value",
                null,
                false
            ),
            "WrongType" => new(
                "using Alias = Foreign.Mode;",
                "string",
                "Alias.Other",
                "packet.Value",
                null,
                false
            ),
            "Overflow" => new(
                "using static Foreign.Defaults;",
                "byte",
                "TooLarge",
                "packet.Value",
                null,
                false
            ),
            "InvalidNameofMember" => new(
                "using Alias = Foreign.Mode;",
                "string",
                "nameof(Alias.Missing)",
                "packet.Value",
                null,
                false
            ),
            "InvalidNameofExpression" => new(
                "",
                "string",
                "nameof(1 + 2)",
                "packet.Value",
                null,
                false
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(name),
                name,
                "Unknown import scenario"
            ),
        };

    /// <summary>Builds legal consumer declarations with each constructor beside its own field imports.</summary>
    private static CSharpCompilation CreateCompilation(
        Case item,
        string target,
        bool split,
        bool manual
    )
    {
        string declaration = target == "class" ? "partial class" : "readonly partial struct";
        string unsafeModifier = item.FieldType.Contains('*', StringComparison.Ordinal)
            ? "unsafe "
            : "";
        bool nullableReference = item.FieldType == "string?";
        string fields = $$"""
public readonly int Required;
{{(nullableReference ? "#nullable enable annotations" : "")}}
[DxMessaging.Core.Attributes.DxOptionalParameter(Expression = {{SyntaxFactory.Literal(
                item.Expression
            )}})]
public {{unsafeModifier}}readonly {{item.FieldType}} Value;
{{(nullableReference ? "#nullable disable annotations" : "")}}
{{(nullableReference ? "public const int EndMarker = 0;" : "")}}
{{(nullableReference && manual ? "#nullable enable annotations" : "")}}
{{(
                manual
                    ? $"public {unsafeModifier}Packet(int Required, {item.FieldType} Value = {item.Expression}) {{ this.Required = Required; this.Value = Value; }}"
                    : ""
            )}}
{{(nullableReference && manual ? "#nullable disable annotations" : "")}}
""";
        string main = $$"""
{{(split ? "" : item.Imports)}}
namespace Foreign
{
    public enum Mode { Zero, Other = 37 }
    public static class Defaults
    {
        public const int Number = 37;
        public const int TooLarge = 256;
        public const float NegativeZero = -0.0f;
        public const decimal NegativeDecimalZero = -0.0m;
        public const decimal PreciseDecimal = 1.2345678901234567890123456789m;
        public const int __dxMessagingOptionalDefault = 37;
        public const int __dxMessagingOptionalDefault_Value = 99;
        public static int Read() { return Number; }
    }
    public readonly struct State { public readonly int Number; }
}
namespace ConsumerScope
{
    {{(manual ? "" : "[DxMessaging.Core.Attributes.DxAutoConstructor]")}}
    public {{declaration}} Packet
    {
        {{(split ? "" : fields)}}
    }
    public static class Consumer
    {
        public static {{unsafeModifier}}object[] Read()
        {
            Packet packet = new Packet(42);
            Packet explicitValue = new Packet(99, default);
            return new object[] { packet.Required, {{item.Read}}, explicitValue.Required };
        }
    }
}
""";
        CSharpCompilation compilation =
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(main);
        compilation = compilation.WithOptions(
            compilation.Options.WithAllowUnsafe(unsafeModifier.Length != 0)
        );
        if (!split)
        {
            return compilation;
        }
        string other =
            item.Imports
            + $"namespace ConsumerScope {{ public {declaration} Packet {{\n{fields}\n}} }}";
        return compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(
                other,
                new CSharpParseOptions(LanguageVersion.CSharp9),
                "FieldImports.cs"
            )
        );
    }

    /// <summary>Preserves distinct meanings of the same alias in separate field declarations.</summary>
    /// <param name="target">The reference or value-type declaration.</param>
    [TestCase("class")]
    [TestCase("readonly struct")]
    public void SeparateFieldAliasesKeepDistinctDefaultValues(string target)
    {
        string declaration = target == "class" ? "partial class" : "readonly partial struct";
        string main = $$"""
using Alias = Foreign.FirstDefaults;
namespace Foreign
{
    public static class FirstDefaults { public const int Number = 37; }
    public static class SecondDefaults { public const int Number = 99; }
}
namespace ConsumerScope
{
    [DxMessaging.Core.Attributes.DxAutoConstructor]
    {{(
                target == "class"
                    ? ""
                    : "[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Auto)]"
            )}}
    public {{declaration}} Packet
    {
        public readonly int Required;
        [DxMessaging.Core.Attributes.DxOptionalParameter(Expression = "Alias.Number")]
        public readonly int First;
    }
    public static class Consumer
    {
        public static object[] Read()
        {
            Packet packet = new Packet(Required: 42);
            Packet explicitValue = new Packet(Required: 84, First: -1, Second: -2);
            return new object[] { packet.Required, packet.First, packet.Second, explicitValue.Required, explicitValue.First, explicitValue.Second };
        }
    }
}
""";
        string fields = $$"""
using Alias = Foreign.SecondDefaults;
namespace ConsumerScope
{
    public {{declaration}} Packet
    {
        [DxMessaging.Core.Attributes.DxOptionalParameter(Expression = "Alias.Number")]
        public readonly int Second;
    }
}
""";
        string manual = main.Replace(
                "[DxMessaging.Core.Attributes.DxAutoConstructor]",
                "",
                StringComparison.Ordinal
            )
            .Replace(
                "public readonly int First;",
                "public readonly int First; public Packet(int Required, int First = Alias.Number, int Second = global::Foreign.SecondDefaults.Number) { this.Required = Required; this.First = First; this.Second = Second; }",
                StringComparison.Ordinal
            );
        CSharpParseOptions parse = new(LanguageVersion.CSharp9);
        CSharpCompilation Create(string source) =>
            GeneratorTestUtilities
                .CreateAutoConstructorCompilationFromShippedAttributes(source)
                .AddSyntaxTrees(CSharpSyntaxTree.ParseText(fields, parse, "SecondAlias.cs"));
        object[] expected = { 42, 37, 99, 84, -1, -2 };
        AssertCallerExecutes(Create(manual), expected, "Handwritten distinct aliases: " + target);
        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(
                generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
                parseOptions: parse
            )
            .RunGeneratorsAndUpdateCompilation(
                Create(main),
                out Compilation generated,
                out ImmutableArray<Diagnostic> diagnostics
            );
        Assert.That(diagnostics, Is.Empty, target + "\n" + string.Join("\n", diagnostics));
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            "Conflicting alias spellings must not crash generation: " + target
        );
        AssertCallerExecutes(generated, expected, "Generated distinct aliases: " + target);
    }

    /// <summary>Compiles and executes omitted and explicit arguments without modeling the generator.</summary>
    private static void AssertCallerExecutes(
        Compilation compilation,
        object[] expected,
        string context
    )
    {
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            context + "\n" + string.Join("\n", emitted.Diagnostics)
        );
        image.Position = 0;
        AssemblyLoadContext load = new(context, isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            object[] actual = (object[])
                assembly.GetType("ConsumerScope.Consumer")!.GetMethod("Read")!.Invoke(null, null)!;
            Assert.That(actual, Is.EqualTo(expected), "Preserve every caller value: " + context);
        }
        finally
        {
            load.Unload();
        }
    }
}
