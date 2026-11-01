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
internal sealed class NameofOptionalDefaultsTests
{
    private sealed record Case(
        string Type,
        string Expression,
        object? Expected,
        bool Valid = true,
        bool RequiresDiagnostic = false
    );

    [TestCaseSource(nameof(Cases))]
    public void NameofDefaultsPreserveValidConstantsAndRejectInvalidDefaults(
        string name,
        string target,
        bool split
    )
    {
        string context = $"name={name}, target={target}, split={split}";
        Case item = GetCase(name);
        (string first, string? second) = CreateSources(item, target, split, manual: true);
        CSharpCompilation manual = CreateCompilation(first, second);
        if (item.Valid)
        {
            AssertCallerExecutes(manual, item.Expected!, "Handwritten oracle: " + context);
        }
        else
        {
            Assert.That(
                manual.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error),
                Is.True,
                "The independent compiler rejects this invalid default: " + context
            );
        }

        (first, second) = CreateSources(item, target, split, manual: false);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(first, second),
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Select(run => run.Exception),
            Has.All.Null,
            "The generator must preserve normal compiler diagnostics: " + context
        );
        Assert.That(
            driver.GetRunResult().GeneratedTrees,
            Has.Length.EqualTo(1),
            "Retain the generated constructor: " + context
        );
        if (item.Valid)
        {
            Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics));
            AssertCallerExecutes(generated, item.Expected!, "Generated caller: " + context);
        }
        else
        {
            Assert.That(
                generated.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error),
                Is.True,
                "Invalid defaults must remain compiler errors: " + context
            );
            Assert.That(
                diagnostics.Count(d => d.Id == "DXMSG005"),
                Is.EqualTo(item.RequiresDiagnostic ? 1 : 0),
                "Retain conversion diagnostics without converting other compiler failures into generator crashes: "
                    + context
            );
            Assert.That(
                diagnostics.All(d => d.Id == "DXMSG005"),
                Is.True,
                "Do not emit unrelated diagnostics: "
                    + context
                    + "\n"
                    + string.Join("\n", diagnostics)
            );
        }
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (
            string name in new[]
            {
                "Type",
                "FrameworkType",
                "GenericType",
                "EnumMember",
                "Concat",
                "ConditionalString",
                "ConditionalByte",
                "ConditionalEnum",
                "WrongType",
                "OutOfRangeByte",
                "MissingName",
                "NonconstantCall",
                "LiteralWord",
                "OrdinaryConstant",
            }
        )
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (bool split in new[] { false, true })
                {
                    yield return new TestCaseData(name, target, split).SetName(
                        $"NameofDefaultsPreserveValidConstantsAndRejectInvalidDefaults({name},{target},{split})"
                    );
                }
            }
        }
    }

    private static Case GetCase(string name) =>
        name switch
        {
            "Type" => new("string", "nameof(Packet)", "Packet"),
            "FrameworkType" => new("string", "nameof(System.String)", "String"),
            "GenericType" => new("string", "nameof(System.Collections.Generic.List<int>)", "List"),
            "EnumMember" => new("string", "nameof(Mode.Other)", "Other"),
            "Concat" => new("string", "nameof(DefaultValue) + \"Suffix\"", "DefaultValueSuffix"),
            "ConditionalString" => new(
                "string",
                "nameof(DefaultValue) == \"DefaultValue\" ? \"ok\" : \"wrong\"",
                "ok"
            ),
            "ConditionalByte" => new(
                "byte",
                "nameof(DefaultValue) == \"DefaultValue\" ? 7 : 256",
                7
            ),
            "ConditionalEnum" => new(
                "Mode",
                "nameof(DefaultValue) == \"DefaultValue\" ? Mode.Other : Mode.Zero",
                37
            ),
            "WrongType" => new("int", "nameof(DefaultValue)", null, false, true),
            "OutOfRangeByte" => new(
                "byte",
                "nameof(DefaultValue) != \"DefaultValue\" ? 7 : 256",
                null,
                false,
                true
            ),
            "MissingName" => new("string", "nameof(MissingMember)", null, false),
            "NonconstantCall" => new("string", "nameof(DefaultValue).ToString()", null, false),
            "LiteralWord" => new("string", "\"nameof\"", "nameof"),
            "OrdinaryConstant" => new("int", "DefaultValue", 37),
            _ => throw new ArgumentOutOfRangeException(
                nameof(name),
                name,
                "Unknown expression shape"
            ),
        };

    private static (string First, string? Second) CreateSources(
        Case item,
        string target,
        bool split,
        bool manual
    )
    {
        string declaration = target == "class" ? "partial class" : "readonly partial struct";
        string constructor = manual
            ? $"public Packet(int Required, {item.Type} Value = {item.Expression}) {{ this.Required = Required; this.Value = Value; }}"
            : "";
        string fields = $$"""
public enum Mode { Zero = 0, Other = 37 }
public const int DefaultValue = 37;
public readonly int Required;
[DxMessaging.Core.Attributes.DxOptionalParameter(Expression = {{SyntaxFactory.Literal(
                item.Expression
            )}})]
public readonly {{item.Type}} Value;
{{constructor}}
""";
        string first = $$"""
namespace NameofDefaults
{
    [DxMessaging.Core.Attributes.DxAutoConstructor]
    public {{declaration}} Packet
    {
        {{(split ? "" : fields)}}
    }
    public static class Consumer
    {
        public static object[] Read()
        {
            Packet omitted = new Packet(42);
            Packet explicitValue = new Packet(99, default);
            return new object[] { omitted.Required, omitted.Value, explicitValue.Required };
        }
    }
}
""";
        string? second = split
            ? $$"""
namespace NameofDefaults
{
    public {{declaration}} Packet
    {
        {{fields}}
    }
}
"""
            : null;
        return (first, second);
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
        AssemblyLoadContext load = new("NameofDefaultConsumer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? read = assembly.GetType("NameofDefaults.Consumer")?.GetMethod("Read");
            Assert.That(read, Is.Not.Null, "The caller must be emitted: " + context);
            object[] values = (object[])read!.Invoke(null, null)!;
            Assert.That(
                values[0],
                Is.EqualTo(42),
                "Omitted calls use the generated constructor: " + context
            );
            object actual = values[1].GetType().IsEnum
                ? Convert.ToInt32(values[1], System.Globalization.CultureInfo.InvariantCulture)
                : values[1];
            Assert.That(
                actual,
                Is.EqualTo(expected),
                "Preserve the exact default value: " + context
            );
            Assert.That(
                values[2],
                Is.EqualTo(99),
                "Explicit calls keep their required argument: " + context
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
