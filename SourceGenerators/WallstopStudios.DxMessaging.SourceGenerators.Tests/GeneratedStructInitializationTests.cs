using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Checks public-field constructors when a type also owns excluded instance storage.
/// </summary>
[TestFixture]
internal sealed class GeneratedStructInitializationTests
{
    [TestCaseSource(nameof(Cases))]
    public void GeneratedConstructorsInitializeExcludedStorage(string target, string storage)
    {
        string context = $"target={target}, storage={storage}, language=CSharp9";
        AssertCallerExecutes(
            CreateCompilation(CreateSource(target, storage, manual: true)),
            storage,
            "Handwritten oracle: " + context
        );

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(CreateSource(target, storage, manual: false)),
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
        AssertCallerExecutes(generated, storage, "Generated caller: " + context);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (string target in new[] { "struct", "readonly struct", "class" })
        {
            foreach (
                string storage in new[]
                {
                    "PrivateField",
                    "InternalField",
                    "AutoProperty",
                    "PublicOnly",
                }
            )
            {
                yield return new TestCaseData(target, storage).SetName(
                    $"GeneratedConstructorsInitializeExcludedStorage({target},{storage})"
                );
            }
        }
    }

    private static string CreateSource(string target, string storage, bool manual)
    {
        (string members, string initial, string seeded) = storage switch
        {
            "PrivateField" => (
                "private readonly int _state; public int Hidden => _state;",
                "_state = 0;",
                "_state = 73;"
            ),
            "InternalField" => (
                "internal readonly int State; public int Hidden => State;",
                "State = 0;",
                "State = 73;"
            ),
            "AutoProperty" => ("public int Hidden { get; }", "Hidden = 0;", "Hidden = 73;"),
            "PublicOnly" => ("public int Hidden => 0;", "", ""),
            _ => throw new ArgumentOutOfRangeException(
                nameof(storage),
                storage,
                "Unknown storage shape"
            ),
        };
        string declaration =
            target == "readonly struct" ? "readonly partial struct" : "partial " + target;
        string constructor = manual
            ? $"public Packet(int Value) {{ this.Value = Value; {initial} }}"
            : "";
        return $$"""
            using DxMessaging.Core.Attributes;
            {{(manual ? "" : "[DxAutoConstructor]")}}
            public {{declaration}} Packet
            {
                public readonly int Value;
                {{members}}
                public Packet(bool seeded) { Value = -1; {{seeded}} }
                {{constructor}}
            }
            public static class Consumer
            {
                public static int[] Read()
                {
                    Packet value = new Packet(42);
                    Packet seeded = new Packet(true);
                    return new[] { value.Value, value.Hidden, seeded.Value, seeded.Hidden };
                }
            }
            """;
    }

    private static CSharpCompilation CreateCompilation(string source) =>
        GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(source);

    private static void AssertCallerExecutes(
        Compilation compilation,
        string storage,
        string context
    )
    {
        using MemoryStream image = new();
        EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            context + "\n" + string.Join("\n", emitted.Diagnostics)
        );
        image.Position = 0;
        AssemblyLoadContext load = new("StructInitializationConsumer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? read = assembly.GetType("Consumer")?.GetMethod("Read");
            Assert.That(read, Is.Not.Null, context + ": caller exists");
            Assert.That(
                read!.Invoke(null, null),
                Is.EqualTo(new[] { 42, 0, -1, storage == "PublicOnly" ? 0 : 73 }),
                context
                    + ": generated public payload and excluded storage match ordinary construction"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
