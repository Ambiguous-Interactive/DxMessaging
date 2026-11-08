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
internal sealed class OptionalDefaultFieldScopeTests
{
    [TestCaseSource(nameof(ScopeCases))]
    public void OptionalDefaultsResolveInTheirFieldsDeclarationScope(
        string shape,
        string target,
        bool split
    )
    {
        string context = $"shape={shape}, target={target}, split={split}";
        (string first, string? second, object expected) = CreateSources(
            shape,
            target,
            split,
            manual: true
        );
        AssertCallerExecutes(
            CreateCompilation(first, second),
            expected,
            "Handwritten oracle: " + context
        );

        (first, second, expected) = CreateSources(shape, target, split, manual: false);
        CSharpCompilation original = CreateCompilation(first, second);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            original,
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Select(run => run.Exception),
            Has.All.Null,
            context
        );
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), context);
        AssertCallerExecutes(generated, expected, "Generated caller: " + context);
        Assert.That(
            diagnostics,
            Is.Empty,
            "A valid field-scope default must not be rejected: "
                + context
                + "\n"
                + string.Join("\n", diagnostics)
        );
    }

    private static IEnumerable<TestCaseData> ScopeCases()
    {
        foreach (
            string shape in new[]
            {
                "OwnEnumBefore",
                "OwnEnumAfter",
                "OwnConstantBefore",
                "OwnConstantAfter",
                "QualifiedOwnEnumBefore",
                "QualifiedOwnEnumAfter",
                "QualifiedOwnConstantBefore",
                "QualifiedOwnConstantAfter",
                "NamespaceEnum",
                "NamespaceConstant",
                "ContainingConstant",
                "NameofOwnConstant",
            }
        )
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (bool split in new[] { false, true })
                {
                    yield return new TestCaseData(shape, target, split).SetName(
                        $"OptionalDefaultsResolveInTheirFieldsDeclarationScope({shape},{target},{split})"
                    );
                }
            }
        }
    }

    private static (string First, string? Second, object Expected) CreateSources(
        string shape,
        string target,
        bool split,
        bool manual
    )
    {
        const string OwnEnum = "public enum Mode { Zero = 0, Other = 37 }";
        const string OwnConstant = "public const int DefaultValue = 37;";
        const string QualifiedPacket = "global::ConsumerScope.ScopeContainer.Packet";
        (string fieldType, string expression, string member, bool after, object expected) =
            shape switch
            {
                "OwnEnumBefore" => ("Mode", "Mode.Other", OwnEnum, false, (object)37),
                "OwnEnumAfter" => ("Mode", "Mode.Other", OwnEnum, true, (object)37),
                "OwnConstantBefore" => ("int", "DefaultValue", OwnConstant, false, (object)37),
                "OwnConstantAfter" => ("int", "DefaultValue", OwnConstant, true, (object)37),
                "QualifiedOwnEnumBefore" => (
                    "Mode",
                    QualifiedPacket + ".Mode.Other",
                    OwnEnum,
                    false,
                    37
                ),
                "QualifiedOwnEnumAfter" => (
                    "Mode",
                    QualifiedPacket + ".Mode.Other",
                    OwnEnum,
                    true,
                    37
                ),
                "QualifiedOwnConstantBefore" => (
                    "int",
                    QualifiedPacket + ".DefaultValue",
                    OwnConstant,
                    false,
                    37
                ),
                "QualifiedOwnConstantAfter" => (
                    "int",
                    QualifiedPacket + ".DefaultValue",
                    OwnConstant,
                    true,
                    37
                ),
                "NamespaceEnum" => ("NamespaceMode", "NamespaceMode.Other", "", false, (object)37),
                "NamespaceConstant" => ("int", "NamespaceDefaults.Value", "", false, (object)37),
                "ContainingConstant" => ("int", "EnclosingDefault", "", false, (object)37),
                "NameofOwnConstant" => (
                    "string",
                    "nameof(DefaultValue)",
                    OwnConstant,
                    false,
                    "DefaultValue"
                ),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(shape),
                    shape,
                    "Unknown scope shape"
                ),
            };
        string declaration = target == "class" ? "partial class" : "readonly partial struct";
        string constructor = manual
            ? $"public Packet(int Required, {fieldType} Value = {expression}) {{ this.Required = Required; this.Value = Value; }}"
            : "";
        string fields = $$"""
{{(after ? "" : member)}}
public readonly int Required;
[DxMessaging.Core.Attributes.DxOptionalParameter(Expression = {{SyntaxFactory.Literal(
                expression
            )}})]
public readonly {{fieldType}} Value;
{{(after ? member : "")}}
{{constructor}}
""";
        string outerConstant =
            shape == "ContainingConstant" ? "public const int EnclosingDefault = 37;" : "";
        string first = $$"""
namespace ConsumerScope
{
    public enum NamespaceMode { Zero = 0, Other = 37 }
    public static class NamespaceDefaults { public const int Value = 37; }
    public partial class ScopeContainer
    {
        {{outerConstant}}
        [DxMessaging.Core.Attributes.DxAutoConstructor]
        public {{declaration}} Packet
        {
            {{(split ? "" : fields)}}
        }
    }
    public static class Consumer
    {
        public static object[] Read()
        {
            ScopeContainer.Packet omitted = new ScopeContainer.Packet(42);
            ScopeContainer.Packet explicitValue = new ScopeContainer.Packet(99, default);
            return new object[] { omitted.Required, omitted.Value, explicitValue.Required };
        }
    }
}
""";
        string? second = split
            ? $$"""
namespace ConsumerScope
{
    public partial class ScopeContainer
    {
        public {{declaration}} Packet
        {
            {{fields}}
        }
    }
}
"""
            : null;
        return (first, second, expected);
    }

    private static CSharpCompilation CreateCompilation(string first, string? second)
    {
        CSharpCompilation compilation =
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(first);
        return second == null
            ? compilation
            : compilation.AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(
                    second,
                    new CSharpParseOptions(LanguageVersion.CSharp9),
                    "Fields.cs"
                )
            );
    }

    private static void AssertCallerExecutes(
        Compilation compilation,
        object expected,
        string context
    )
    {
        Diagnostic[] diagnostics = compilation.GetDiagnostics().ToArray();
        Assert.That(
            diagnostics,
            Is.Empty,
            context + "\n" + string.Join("\n", diagnostics.AsEnumerable())
        );
        using MemoryStream image = new();
        EmitResult emit = compilation.Emit(image);
        Assert.That(emit.Success, Is.True, context + "\n" + string.Join("\n", emit.Diagnostics));
        image.Position = 0;
        AssemblyLoadContext load = new("FieldScopeConsumer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? read = assembly.GetType("ConsumerScope.Consumer")?.GetMethod("Read");
            Assert.That(read, Is.Not.Null, "The direct caller must be emitted: " + context);
            object[] values = (object[])read!.Invoke(null, null)!;
            Assert.That(
                values[0],
                Is.EqualTo(42),
                "Omitted call uses the generated constructor: " + context
            );
            object actual = values[1].GetType().IsEnum
                ? Convert.ToInt32(values[1], System.Globalization.CultureInfo.InvariantCulture)
                : values[1];
            Assert.That(
                actual,
                Is.EqualTo(expected),
                "The omitted argument has its declared constant: " + context
            );
            Assert.That(
                values[2],
                Is.EqualTo(99),
                "Explicit calls retain the required argument: " + context
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
