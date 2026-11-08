using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Covers excluded pointer/event storage, ref and record types, and preservation-local collisions.
/// </summary>
[TestFixture]
internal sealed class GeneratedExcludedStorageBoundaryTests
{
    [TestCaseSource(nameof(Cases))]
    public void GeneratedConstructorsInitializeBoundaryStorage(string target, string shape)
    {
        string context = $"target={target}, shape={shape}";
        LanguageVersion language =
            shape is "Collision" or "EventOpaque"
            || target.Contains("record", StringComparison.Ordinal)
                ? LanguageVersion.CSharp10
                : LanguageVersion.CSharp9;
        CSharpParseOptions options = new(language);
        AssertCallerExecutes(
            CreateCompilation(CreateSource(target, shape, manual: true), options),
            context + ": handwritten oracle"
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: options
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(CreateSource(target, shape, manual: false), options),
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            context
        );
        Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics));
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), context);
        AssertCallerExecutes(generated, context + ": generated caller");
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (
            string target in new[]
            {
                "struct",
                "readonly struct",
                "ref struct",
                "readonly ref struct",
                "class",
            }
        )
        foreach (string shape in new[] { "Pointer", "FunctionPointer" })
        {
            yield return Case(target, shape);
        }
        foreach (string target in new[] { "struct", "ref struct", "class" })
        {
            yield return Case(target, "Event");
        }
        foreach (string target in new[] { "struct", "class" })
        {
            yield return Case(target, "EventOpaque");
        }
        foreach (
            string target in new[]
            {
                "struct",
                "readonly struct",
                "record struct",
                "readonly record struct",
                "class",
            }
        )
        {
            yield return Case(target, "Collision");
        }
        foreach (string target in new[] { "ref struct", "readonly ref struct" })
        {
            yield return Case(target, "Span");
        }
        foreach (string target in new[] { "record struct", "readonly record struct" })
        {
            yield return Case(target, "Property");
        }
    }

    private static TestCaseData Case(string target, string shape) =>
        new TestCaseData(target, shape).SetName(
            $"GeneratedConstructorsInitializeBoundaryStorage({target},{shape})"
        );

    private static string CreateSource(string target, string shape, bool manual)
    {
        bool isClass = target == "class";
        (string storage, string assignments, string hidden) = shape switch
        {
            "Pointer" => (
                "private readonly int* _state;",
                "_state = default;",
                "_state == null ? 0 : 1"
            ),
            "FunctionPointer" => (
                "private readonly delegate*<int, int> _state;",
                "_state = default;",
                "_state == null ? 0 : 1"
            ),
            "Span" => (
                "private readonly System.Span<int> _state;",
                "_state = default;",
                "_state.Length"
            ),
            "Property" => ("public int State {get;}", "State = default;", "State"),
            "Event" => (
                "public event System.Action? Callbacks;",
                "Callbacks = default;",
                "Callbacks == null ? 0 : 1"
            ),
            "EventOpaque" => (
                "public event System.Action? Callbacks = Probe.Handler(); int IState.State {get;}",
                "System.Action? saved = Callbacks; this = default; Callbacks = saved;",
                "Callbacks != null && ((IState)this).State == 0 ? 0 : 1"
            ),
            "Collision" => (
                "private readonly int __dxMessagingInitialized = Probe.Initialize(); public readonly int __dxMessagingInitialized_ = 23; int IState.State {get;}",
                "int saved = __dxMessagingInitialized; this = default; __dxMessagingInitialized = saved;",
                "__dxMessagingInitialized == 17 && __dxMessagingInitialized_ == 29 && ((IState)this).State == 0 ? 0 : 1"
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(shape),
                shape,
                "Unknown boundary shape"
            ),
        };
        bool opaque = shape is "EventOpaque" or "Collision";
        string contract = opaque ? ": IState" : "";
        string type = target switch
        {
            "ref struct" => "ref partial struct",
            "readonly ref struct" => "readonly ref partial struct",
            "readonly struct" => "readonly partial struct",
            "readonly record struct" => "readonly partial record struct",
            _ => "partial " + target,
        };
        if (isClass && shape is "Pointer" or "FunctionPointer")
        {
            storage = storage.Replace("_state;", "_state = default;", StringComparison.Ordinal);
        }
        string extraParameter = shape == "Collision" ? ", int __dxMessagingInitialized_" : "";
        string extraAssignment =
            shape == "Collision"
                ? "this.__dxMessagingInitialized_ = __dxMessagingInitialized_;"
                : "";
        string constructor = manual
            ? $"public Packet(int Value{extraParameter}) {{ {(isClass ? "" : assignments)} this.Value = Value; {extraAssignment} }}"
            : "";
        string open =
            shape == "Collision" ? "public partial class Outer<__dxMessagingInitialized__> {" : "";
        string close = shape == "Collision" ? "}" : "";
        string callerType = shape == "Collision" ? "Outer<int>.Packet" : "Packet";
        string callerArguments = shape == "Collision" ? "42, 29" : "42";
        return $$"""
            #nullable enable
            using DxMessaging.Core.Attributes;
            public interface IState { int State {get;} }
            {{open}}
            {{(manual ? "" : "[DxAutoConstructor]")}}
            public unsafe {{type}} Packet {{contract}}
            {
                public readonly int Value;
                {{storage}}
                public int Hidden => {{hidden}};
                {{constructor}}
            }
            {{close}}
            public static class Probe
            {
                public static int Calls;
                public static int Initialize() { Calls++; return 17; }
                public static System.Action Handler() { Calls++; return () => {}; }
                public static int[] Read()
                {
                    {{callerType}} packet = new {{callerType}}({{callerArguments}});
                    return new[] {packet.Value, packet.Hidden, Calls};
                }
            }
            """;
    }

    private static CSharpCompilation CreateCompilation(string source, CSharpParseOptions options)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Find shipped constructor attributes.");
        List<SyntaxTree> trees = new();
        foreach (
            string name in new[]
            {
                "DxAutoConstructorAttribute.cs",
                "DxOptionalParameterAttribute.cs",
            }
        )
        {
            string path = Path.Combine(root!.FullName, "Runtime", "Core", "Attributes", name);
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path));
        }
        trees.Add(CSharpSyntaxTree.ParseText(source, options, "UserSource.cs"));
        return CSharpCompilation.Create(
            "BoundaryStorage",
            trees,
            GeneratorTestUtilities.CompilationReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
    }

    private static void AssertCallerExecutes(Compilation compilation, string context)
    {
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            context + "\n" + string.Join("\n", emitted.Diagnostics)
        );
        image.Position = 0;
        AssemblyLoadContext load = new("BoundaryStorage", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? read = assembly.GetType("Probe")?.GetMethod("Read");
            Assert.That(read, Is.Not.Null, context + ": caller exists");
            int[] expected =
            {
                42,
                0,
                context.Contains("Collision", StringComparison.Ordinal)
                || context.Contains("EventOpaque", StringComparison.Ordinal)
                    ? 1
                    : 0,
            };
            Assert.That(
                read!.Invoke(null, null),
                Is.EqualTo(expected),
                context + ": payload, hidden state and initializer count"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
