using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Exercises excluded generic, nullable, escaped and compiler-owned storage through real callers.
/// </summary>
[TestFixture]
internal sealed class GeneratedExcludedStorageShapeTests
{
    [TestCaseSource(nameof(Cases))]
    public void GeneratedConstructorsPreserveExcludedStorageShapes(string target, string shape)
    {
        string context = $"target={target}, shape={shape}, language=CSharp9";
        (string first, string? second) = CreateSources(target, shape, manual: true);
        AssertCallerExecutes(
            CreateCompilation(first, second),
            shape,
            "Handwritten oracle: " + context
        );
        (first, second) = CreateSources(target, shape, manual: false);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(first, second),
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            context
        );
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), context);
        Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics));
        AssertCallerExecutes(generated, shape, "Generated caller: " + context);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (string target in new[] { "struct", "readonly struct", "class" })
        {
            foreach (
                string shape in new[]
                {
                    "Reference",
                    "NullableValue",
                    "GenericValue",
                    "GenericReference",
                    "Nested",
                    "Split",
                    "EscapedField",
                    "EscapedProperty",
                    "ExplicitProperty",
                }
            )
            {
                yield return new TestCaseData(target, shape).SetName(
                    $"GeneratedConstructorsPreserveExcludedStorageShapes({target},{shape})"
                );
            }
        }
    }

    private sealed record SourceShape(
        string TypeName,
        string Constraint,
        string ClosedType,
        string Storage,
        string IsDefault,
        string Initial,
        string Seeded,
        string SeedParameter,
        string SeedArguments,
        string ContainerOpen = "",
        string ContainerClose = "",
        bool ExplicitProperty = false
    );

    private static SourceShape GetSourceShape(string shape) =>
        shape switch
        {
            "Reference" => new(
                "Packet",
                "",
                "Packet",
                "private readonly string? _state;",
                "_state is null",
                "_state = default;",
                "_state = \"seeded\";",
                "",
                ""
            ),
            "NullableValue" => new(
                "Packet",
                "",
                "Packet",
                "private readonly int? _state;",
                "_state is null",
                "_state = default;",
                "_state = 73;",
                "",
                ""
            ),
            "GenericValue" => new(
                "Packet<T>",
                " where T : struct",
                "Packet<int>",
                "private readonly T _state;",
                "System.Collections.Generic.EqualityComparer<T>.Default.Equals(_state, default)",
                "_state = default;",
                "_state = state;",
                ", T state",
                ", 73"
            ),
            "GenericReference" => new(
                "Packet<T>",
                " where T : class",
                "Packet<string>",
                "private readonly T? _state;",
                "_state is null",
                "_state = default;",
                "_state = state;",
                ", T? state",
                ", \"seeded\""
            ),
            "Nested" => new(
                "Packet",
                "",
                "Container<int>.Packet",
                "private readonly T _state;",
                "System.Collections.Generic.EqualityComparer<T>.Default.Equals(_state, default)",
                "_state = default;",
                "_state = state;",
                ", T state",
                ", 73",
                "public partial interface Container<T> where T : struct {",
                "}"
            ),
            "Split" => new(
                "Packet",
                "",
                "Packet",
                "private readonly int _state;",
                "_state == 0",
                "_state = default;",
                "_state = 73;",
                "",
                ""
            ),
            "EscapedField" => new(
                "Packet",
                "",
                "Packet",
                "private readonly int @event;",
                "@event == 0",
                "@event = default;",
                "@event = 73;",
                "",
                ""
            ),
            "EscapedProperty" => new(
                "Packet",
                "",
                "Packet",
                "public int @event { get; }",
                "@event == 0",
                "@event = default;",
                "@event = 73;",
                "",
                ""
            ),
            "ExplicitProperty" => new(
                "Packet",
                "",
                "Packet",
                "int IState.State { get; }",
                "((IState)this).State == 0",
                "",
                "",
                "",
                "",
                ExplicitProperty: true
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(shape),
                shape,
                "Unknown storage shape"
            ),
        };

    private static (string First, string? Second) CreateSources(
        string target,
        string shape,
        bool manual
    )
    {
        SourceShape data = GetSourceShape(shape);
        bool explicitProperty = data.ExplicitProperty;
        string declaration =
            target == "readonly struct" ? "readonly partial struct" : "partial " + target;
        string typeName = data.TypeName;
        string constraint = data.Constraint;
        string closedType = data.ClosedType;
        string reset = explicitProperty && target != "class" ? "this = default;" : "";
        string initial = data.Initial;
        string seeded = data.Seeded;
        string seedParameter = data.SeedParameter;
        string seedArguments = data.SeedArguments;
        string constructor = manual
            ? $"public Packet(int Value) {{ {reset} this.Value = Value; {initial} }}"
            : "";
        string members = $"{data.Storage} public bool IsDefault => {data.IsDefault};";
        string containerOpen = data.ContainerOpen;
        string containerClose = data.ContainerClose;
        string first = $$"""
            #nullable enable
            using DxMessaging.Core.Attributes;
            public interface IState { int State { get; } }
            {{containerOpen}}
            {{(manual ? "" : "[DxAutoConstructor]")}}
            public {{declaration}} {{typeName}}{{(
                explicitProperty ? " : IState" : ""
            )}}{{constraint}}
            {
                {{(shape == "Split" ? "" : "public readonly int Value;")}}
                public static int StaticState = 91;
                {{(shape == "Split" ? "" : members)}}
                public Packet(bool seeded{{seedParameter}}) { {{reset}} Value = -1; {{seeded}} }
                {{constructor}}
            }
            {{containerClose}}
            public static class Consumer
            {
                public static int[] Read()
                {
                    {{closedType}} value = new {{closedType}}(42);
                    {{closedType}} seeded = new {{closedType}}(true{{seedArguments}});
                    return new[] { value.Value, value.IsDefault ? 1 : 0, seeded.Value,
                        seeded.IsDefault ? 1 : 0, {{closedType}}.StaticState };
                }
            }
            """;
        string? second =
            shape == "Split"
                ? $$"""
                    #nullable enable
                    public {{declaration}} Packet { public readonly int Value; {{members}} }
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
                    "OtherPart.cs"
                )
            );
    }

    private static void AssertCallerExecutes(Compilation compilation, string shape, string context)
    {
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            context + "\n" + string.Join("\n", emitted.Diagnostics)
        );
        image.Position = 0;
        AssemblyLoadContext load = new("ExcludedStorageConsumer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? read = assembly.GetType("Consumer")?.GetMethod("Read");
            Assert.That(read, Is.Not.Null, context + ": caller exists");
            Assert.That(
                read!.Invoke(null, null),
                Is.EqualTo(new[] { 42, 1, -1, shape == "ExplicitProperty" ? 1 : 0, 91 }),
                context
                    + ": retain public payload, excluded defaults, seeded state and static state"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
