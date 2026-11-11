using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Preserves field initializers and user constructor effects while initializing excluded storage.
/// </summary>
[TestFixture]
internal sealed class GeneratedStructInitializerCompatibilityTests
{
    private sealed record Shape(
        bool Properties,
        bool FirstInitializer,
        bool SecondInitializer,
        bool UserConstructor
    );

    [TestCaseSource(nameof(Cases))]
    public void GeneratedConstructorsPreserveInitializersAndConstructorEffects(
        string target,
        string shape,
        int language
    )
    {
        Shape item = GetShape(shape);
        string context = $"target={target}, shape={shape}, language={language}";
        AssertCallerExecutes(
            CreateCompilation(CreateSource(target, item, manual: true), language),
            item,
            "Handwritten oracle: " + context
        );
        CSharpParseOptions options = ParseOptions(language);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: options
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(CreateSource(target, item, manual: false), language),
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
        {
            foreach (
                string shape in new[]
                {
                    "FieldsNoInitializers",
                    "FieldsAllInitialized",
                    "FieldsMixedInitialized",
                    "FieldsAllInitializedUserConstructor",
                    "FieldsMixedUserConstructor",
                    "PropertiesAllInitialized",
                    "PropertiesMixedInitialized",
                    "PropertiesMixedUserConstructor",
                }
            )
            {
                foreach (int language in new[] { 10, 11 })
                {
                    yield return new TestCaseData(target, shape, language).SetName(
                        $"GeneratedConstructorsPreserveInitializersAndConstructorEffects({target},{shape},{language})"
                    );
                }
            }
        }
    }

    private static Shape GetShape(string name) =>
        name switch
        {
            "FieldsNoInitializers" => new(false, false, false, false),
            "FieldsAllInitialized" => new(false, true, true, false),
            "FieldsMixedInitialized" => new(false, true, false, false),
            "FieldsAllInitializedUserConstructor" => new(false, true, true, true),
            "FieldsMixedUserConstructor" => new(false, true, false, true),
            "PropertiesAllInitialized" => new(true, true, true, false),
            "PropertiesMixedInitialized" => new(true, true, false, false),
            "PropertiesMixedUserConstructor" => new(true, true, false, true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(name),
                name,
                "Unknown initialization shape"
            ),
        };

    private static string CreateSource(string target, Shape shape, bool manual)
    {
        string first = shape.Properties ? "First" : "_first";
        string second = shape.Properties ? "Second" : "_second";
        string declaration =
            target == "readonly struct" ? "readonly partial struct" : "partial " + target;
        string firstInitializer = shape.FirstInitializer ? " = Probe.Initialize(17);" : "";
        string secondInitializer = shape.SecondInitializer ? " = Probe.Initialize(23);" : "";
        string members = shape.Properties
            ? $"public int First {{ get; }}{firstInitializer} public int Second {{ get; }}{secondInitializer}"
            : $"private readonly int _first{(shape.FirstInitializer ? firstInitializer : ";")} private readonly int _second{(shape.SecondInitializer ? secondInitializer : ";")} public int First => _first; public int Second => _second;";
        string assignments =
            (shape.FirstInitializer ? "" : $"{first} = 0;")
            + (shape.SecondInitializer ? "" : $"{second} = 0;");
        string constructor = manual
            ? $"public Packet(int Value) {{ this.Value = Value; {assignments} }}"
            : "";
        string userConstructor = shape.UserConstructor
            ? $"public Packet() {{ Probe.DefaultCalls++; Value = -2; {first} = 101; {second} = 103; }}"
            : "";
        return $$"""
            using DxMessaging.Core.Attributes;
            {{(manual ? "" : "[DxAutoConstructor]")}}
            public {{declaration}} Packet
            {
                public readonly int Value;
                {{members}}
                public Packet(bool seeded) { Value = -1; {{first}} = 73; {{second}} = 79; }
                {{userConstructor}}
                {{constructor}}
            }
            public static class Probe
            {
                public static int Initializers;
                public static int DefaultCalls;
                public static int Initialize(int value) { Initializers++; return value; }
            }
            public static class Consumer
            {
                public static int[] Read()
                {
                    Packet value = new Packet(42);
                    return new[] { value.Value, value.First, value.Second, Probe.Initializers, Probe.DefaultCalls };
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
            "StructInitializerCompatibility",
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
        AssemblyLoadContext load = new("StructInitializerCompatibility", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? read = assembly.GetType("Consumer")?.GetMethod("Read");
            Assert.That(read, Is.Not.Null, context + ": caller exists");
            Assert.That(
                read!.Invoke(null, null),
                Is.EqualTo(
                    new[]
                    {
                        42,
                        shape.FirstInitializer ? 17 : 0,
                        shape.SecondInitializer ? 23 : 0,
                        (shape.FirstInitializer ? 1 : 0) + (shape.SecondInitializer ? 1 : 0),
                        0,
                    }
                ),
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
