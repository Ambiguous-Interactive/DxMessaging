using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Preserves mixed explicit-interface initializers without invoking a user constructor.
/// </summary>
[TestFixture]
internal sealed class GeneratedMixedOpaqueStorageTests
{
    private static readonly int[] Preserved = { 42, 17, 0, 1, 0 };
    private static readonly int[] ChainedUser = { 42, 0, 0, 1, 1 };
    private static readonly string[] UnsupportedIds = { "DXMSG011" };

    [TestCaseSource(nameof(Cases))]
    public void GeneratedConstructorsPreserveMixedOpaqueInitializers(
        string target,
        int language,
        bool userConstructor,
        bool reverse
    )
    {
        string context =
            $"target={target}, language={language}, userConstructor={userConstructor}, reverse={reverse}";
        bool unsupported = target != "class" && language == 10 && userConstructor;
        CSharpParseOptions options = new(
            language == 10 ? LanguageVersion.CSharp10 : LanguageVersion.CSharp11
        );
        string source = CreateSource(target, userConstructor, reverse);
        bool chain = target != "class" && language == 10;
        string manual = source
            .Replace("[DxAutoConstructor]", "", StringComparison.Ordinal)
            .Replace(
                "/* constructor */",
                $"public Packet(int Value){(chain ? " : this()" : "")} {{ this.Value = Value; }}",
                StringComparison.Ordinal
            );
        AssertCallerExecutes(
            CreateCompilation(manual, options),
            unsupported ? ChainedUser : Preserved,
            "Handwritten oracle: " + context
        );

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: options
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(source, options),
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            context
        );
        if (unsupported)
        {
            Assert.That(
                diagnostics.Select(item => item.Id),
                Is.EqualTo(UnsupportedIds),
                context + "\n" + string.Join("\n", diagnostics)
            );
            Diagnostic diagnostic = diagnostics.Single();
            Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Error), context);
            Assert.That(
                diagnostic.Location.SourceTree?.FilePath,
                Is.EqualTo("UserSource.cs"),
                context
            );
            Assert.That(
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                Does.Contain("initializer")
                    .And.Contain("parameterless")
                    .And.Contain("explicit-interface"),
                context
            );
            Assert.That(
                driver.GetRunResult().GeneratedTrees,
                Is.Empty,
                context + ": do not emit a constructor that erases state or invokes user code"
            );
            return;
        }
        Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics));
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), context);
        AssertCallerExecutes(generated, Preserved, "Generated caller: " + context);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (string target in new[] { "struct", "readonly struct", "class" })
        foreach (int language in new[] { 10, 11 })
        foreach (bool user in new[] { false, true })
        foreach (bool reverse in new[] { false, true })
        {
            yield return new TestCaseData(target, language, user, reverse).SetName(
                $"GeneratedConstructorsPreserveMixedOpaqueInitializers({target},{language},{user},{reverse})"
            );
        }
    }

    private static string CreateSource(string target, bool user, bool reverse)
    {
        bool isClass = target == "class";
        string initialized = "int IState.First { get; } = Probe.Initialize();";
        string uninitialized = "int IState.Second { get; }";
        string members = reverse ? uninitialized + initialized : initialized + uninitialized;
        string constructor = user
            ? $"public Packet() {{ {(isClass ? "" : "this = default;")} Value = -2; Probe.Ctors++; }}"
            : "";
        string declaration =
            target == "readonly struct" ? "readonly partial struct" : "partial " + target;
        return $$"""
            using DxMessaging.Core.Attributes;
            public interface IState { int First {get;} int Second {get;} }
            [DxAutoConstructor]
            public {{declaration}} Packet : IState
            {
                public readonly int Value;
                {{members}}
                {{constructor}}
                /* constructor */
            }
            public static class Probe
            {
                public static int Initializers, Ctors;
                public static int Initialize() { Initializers++; return 17; }
                public static int[] Read()
                {
                    Packet packet = new Packet(42);
                    return new[] {packet.Value, ((IState)packet).First, ((IState)packet).Second, Initializers, Ctors};
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
            "MixedOpaqueStorage",
            trees,
            GeneratorTestUtilities.CompilationReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
    }

    private static void AssertCallerExecutes(
        Compilation compilation,
        int[] expected,
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
        AssemblyLoadContext load = new("MixedOpaqueStorage", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? read = assembly.GetType("Probe")?.GetMethod("Read");
            Assert.That(read, Is.Not.Null, context + ": caller exists");
            Assert.That(
                read!.Invoke(null, null),
                Is.EqualTo(expected),
                context
                    + ": payload, initialized/default state, initializer count, user constructor count"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
