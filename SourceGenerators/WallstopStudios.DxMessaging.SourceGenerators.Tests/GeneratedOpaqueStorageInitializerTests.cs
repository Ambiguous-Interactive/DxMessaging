using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Preserves initialized state when excluded storage cannot be named directly.
/// </summary>
[TestFixture]
internal sealed class GeneratedOpaqueStorageInitializerTests
{
    private sealed record Shape(
        string Members,
        string First,
        string DefaultAssignments,
        string ManualAssignments,
        bool Event
    );

    [TestCaseSource(nameof(Cases))]
    public void GeneratedConstructorsPreserveOpaqueStateAndEvents(
        string target,
        string shape,
        int language,
        bool userConstructor
    )
    {
        Shape item = GetShape(shape);
        string context =
            $"target={target}, shape={shape}, language={language}, userConstructor={userConstructor}";
        AssertCallerExecutes(
            CreateCompilation(CreateSource(target, item, userConstructor, manual: true), language),
            item,
            "Handwritten oracle: " + context
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: ParseOptions(language)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(CreateSource(target, item, userConstructor, manual: false), language),
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
        AssertCallerExecutes(generated, item, "Generated caller: " + context);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (string target in new[] { "struct", "readonly struct", "class" })
        foreach (
            string shape in new[] { "FieldOpaque", "PropertyOpaque", "OpaqueField", "EventField" }
        )
        foreach (int language in new[] { 10, 11 })
        foreach (bool userConstructor in new[] { false, true })
        {
            if (target == "readonly struct" && shape == "EventField")
                continue;
            yield return new TestCaseData(target, shape, language, userConstructor).SetName(
                $"GeneratedConstructorsPreserveOpaqueStateAndEvents({target},{shape},{language},{userConstructor})"
            );
        }
    }

    private static Shape GetShape(string shape) =>
        shape switch
        {
            "FieldOpaque" => new(
                "private readonly int _first = Probe.Initialize(); int IState.Second { get; } public int First => _first; public int Second => ((IState)this).Second;",
                "_first",
                "_first = 101;",
                "int preserved = _first; this = default; _first = preserved;",
                false
            ),
            "PropertyOpaque" => new(
                "public int First { get; } = Probe.Initialize(); int IState.Second { get; } public int Second => ((IState)this).Second;",
                "First",
                "First = 101;",
                "int preserved = First; this = default; First = preserved;",
                false
            ),
            "OpaqueField" => new(
                "int IState.First { get; } = Probe.Initialize(); private readonly int _second; public int First => ((IState)this).First; public int Second => _second;",
                "",
                "_second = 103;",
                "_second = 0;",
                false
            ),
            "EventField" => new(
                "private readonly int _second; public int First => 0; public int Second => _second; public event System.Action? Callbacks = Probe.CreateHandler(); public void Ping() { Callbacks?.Invoke(); }",
                "",
                "_second = 103;",
                "_second = 0;",
                true
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(shape),
                shape,
                "Unknown storage shape"
            ),
        };

    private static string CreateSource(
        string target,
        Shape shape,
        bool userConstructor,
        bool manual
    )
    {
        bool isClass = target == "class";
        string clear = !isClass && shape.First.Length != 0 ? "this = default;" : "";
        string declaration =
            target == "readonly struct" ? "readonly partial struct" : "partial " + target;
        string defaultConstructor = userConstructor
            ? $"public Packet() {{ {clear} Value = -2; {shape.DefaultAssignments} Probe.DefaultCalls++; }}"
            : "";
        string manualAssignments = isClass ? "" : shape.ManualAssignments;
        string constructor = manual
            ? $"public Packet(int Value) {{ {manualAssignments} this.Value = Value; }}"
            : "";
        string contract = shape.Event ? "" : ": IState";
        return $$"""
            #nullable enable
            using DxMessaging.Core.Attributes;
            public interface IState { int First { get; } int Second { get; } }
            {{(manual ? "" : "[DxAutoConstructor]")}}
            public {{declaration}} Packet {{contract}}
            {
                public readonly int Value;
                {{shape.Members}}
                public Packet(bool seeded) { {{clear}} Value = -1; {{shape.DefaultAssignments}} }
                {{defaultConstructor}}
                {{constructor}}
            }
            public static class Probe
            {
                public static int Initializers;
                public static int DefaultCalls;
                public static int EventCalls;
                public static int Initialize() { Initializers++; return 17; }
                public static System.Action CreateHandler() { Initializers++; return () => EventCalls++; }
            }
            public static class Consumer
            {
                public static int[] Read()
                {
                    Packet value = new Packet(42);
                    {{(shape.Event ? "value.Ping();" : "")}}
                    return new[] { value.Value, value.First, value.Second, Probe.Initializers, Probe.DefaultCalls, Probe.EventCalls };
                }
            }
            """;
    }

    private static CSharpParseOptions ParseOptions(int language) =>
        new(language == 10 ? LanguageVersion.CSharp10 : LanguageVersion.CSharp11);

    private static CSharpCompilation CreateCompilation(string source, int language)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Find the repository containing the shipped attributes.");
        CSharpParseOptions options = ParseOptions(language);
        List<SyntaxTree> trees = new();
        foreach (
            string name in new[]
            {
                "DxAutoConstructorAttribute.cs",
                "DxOptionalParameterAttribute.cs",
            }
        )
        {
            string path = Path.Combine(root!.FullName, "Runtime", "Attributes", name);
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path));
        }
        trees.Add(CSharpSyntaxTree.ParseText(source, options, "UserSource.cs"));
        return CSharpCompilation.Create(
            "OpaqueStorageInitializer",
            trees,
            GeneratorTestUtilities.CompilationReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
    }

    private static void AssertCallerExecutes(Compilation compilation, Shape shape, string context)
    {
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            context + "\n" + string.Join("\n", emitted.Diagnostics)
        );
        image.Position = 0;
        AssemblyLoadContext load = new("OpaqueStorageInitializer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? read = assembly.GetType("Consumer")?.GetMethod("Read");
            Assert.That(read, Is.Not.Null, context + ": caller exists");
            Assert.That(
                read!.Invoke(null, null),
                Is.EqualTo(new[] { 42, shape.Event ? 0 : 17, 0, 1, 0, shape.Event ? 1 : 0 }),
                context
                    + ": retain initialized/default values, initializer counts and zero unintended constructor calls"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
