using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Retains valid default initialization of unconstrained generic struct state without suppressions.
/// </summary>
[TestFixture]
internal sealed class GeneratedUnconstrainedStorageTests
{
    [TestCaseSource(nameof(Cases))]
    public void GeneratedConstructorsPreserveUnconstrainedStorage(
        string target,
        int language,
        string initializer,
        bool userConstructor,
        bool globalWarnings
    )
    {
        string context =
            $"target={target}, language={language}, initializer={initializer}, userConstructor={userConstructor}, globalWarnings={globalWarnings}";
        CSharpParseOptions options = new(
            language switch
            {
                9 => LanguageVersion.CSharp9,
                10 => LanguageVersion.CSharp10,
                _ => LanguageVersion.CSharp11,
            }
        );
        string manual = CreateSource(target, initializer, userConstructor, manual: true);
        AssertCallerExecutes(
            CreateCompilation(manual, options, globalWarnings),
            initializer,
            false,
            "Handwritten oracle: " + context
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: options
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(
                CreateSource(target, initializer, userConstructor, manual: false),
                options,
                globalWarnings
            ),
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
        AssertCallerExecutes(generated, initializer, false, "Generated caller: " + context);
    }

    [TestCase("struct", 10, false)]
    [TestCase("struct", 10, true)]
    [TestCase("struct", 11, false)]
    [TestCase("struct", 11, true)]
    [TestCase("readonly struct", 10, false)]
    [TestCase("readonly struct", 10, true)]
    [TestCase("readonly struct", 11, false)]
    [TestCase("readonly struct", 11, true)]
    public void HandwrittenOpaqueGenericInitializationRetainsCompilerRestrictions(
        string target,
        int language,
        bool user
    )
    {
        string context = $"target={target}, language={language}, user={user}";
        CSharpParseOptions options = new(
            language == 10 ? LanguageVersion.CSharp10 : LanguageVersion.CSharp11
        );
        CSharpCompilation manual = CreateCompilation(
            CreateSource(target, "Opaque", user, manual: true),
            options,
            true
        );
        if (user)
        {
            AssertCallerExecutes(
                manual,
                "Opaque",
                true,
                context + ": user chaining erases initializer and calls user code"
            );
            return;
        }
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = manual.Emit(image);
        Assert.That(
            emitted.Success,
            Is.False,
            context + ": implicit chaining does not establish the non-null contract"
        );
        Diagnostic[] errors = emitted
            .Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.That(
            errors,
            Has.Length.EqualTo(1),
            context + "\n" + string.Join("\n", emitted.Diagnostics)
        );
        Assert.That(errors[0].Id, Is.EqualTo("CS8618"), context);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (string target in new[] { "struct", "readonly struct" })
        foreach (bool global in new[] { false, true })
        {
            yield return Case(target, 9, "None", false, global);
            foreach (int language in new[] { 10, 11 })
            foreach (string initializer in new[] { "None", "Named" })
            foreach (bool user in new[] { false, true })
            {
                yield return Case(target, language, initializer, user, global);
            }
        }
    }

    private static TestCaseData Case(
        string target,
        int language,
        string initializer,
        bool user,
        bool global
    ) =>
        new TestCaseData(target, language, initializer, user, global).SetName(
            $"GeneratedConstructorsPreserveUnconstrainedStorage({target},{language},{initializer},{user},{global})"
        );

    private static string CreateSource(string target, string initializer, bool user, bool manual)
    {
        string declaration =
            target == "readonly struct" ? "readonly partial struct" : "partial struct";
        string storage = initializer switch
        {
            "Named" =>
                "private readonly int _first = Probe.Initialize(); public int First => _first;",
            "Opaque" =>
                "int IState.First {get;} = Probe.Initialize(); public int First => ((IState)this).First;",
            _ => "public int First => 0;",
        };
        string contract = initializer == "Opaque" ? ": IState" : "";
        string initialization =
            initializer == "Named"
                ? "int saved = _first; this = default; _first = saved;"
                : "this = default;";
        string defaultConstructor = user
            ? "public Packet() { this = default; Value = -2; Probe.Ctors++; }"
            : "";
        string constructor = manual
            ? initializer == "Opaque"
                ? "public Packet(int Value) : this() { this.Value = Value; }"
                : $"public Packet(int Value) {{ {initialization} this.Value = Value; }}"
            : "";
        return $$"""
            #nullable enable
            using DxMessaging.Core.Attributes;
            public interface IState {int First {get;} }
            {{(manual ? "" : "[DxAutoConstructor]")}}
            public {{declaration}} Packet<T> {{contract}}
            {
                public readonly int Value;
                private readonly T _state;
                {{storage}}
                public bool IsDefault => System.Collections.Generic.EqualityComparer<T>.Default.Equals(_state, default);
                public Packet(bool seeded, T state) { this = default; Value = -1; _state = state; }
                {{defaultConstructor}}
                {{constructor}}
            }
            public static class Probe
            {
                public static int Initializers, Ctors;
                public static int Initialize() { Initializers++; return 17; }
                public static int[] Read()
                {
                    Packet<int> number = new Packet<int>(42);
                    int count = Initializers;
                    int constructors = Ctors;
                    Initializers = 0;
                    Ctors = 0;
                    Packet<string> text = new Packet<string>(43);
                    return new[] {number.Value, number.First, number.IsDefault ? 1 : 0, count, constructors,
                        text.Value, text.First, text.IsDefault ? 1 : 0, Initializers, Ctors};
                }
            }
            """;
    }

    private static CSharpCompilation CreateCompilation(
        string source,
        CSharpParseOptions options,
        bool globalWarnings
    )
    {
        CSharpCompilation input =
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(source);
        CSharpCompilation attributes = input
            .RemoveSyntaxTrees(input.SyntaxTrees.Single(tree => tree.FilePath == "UserSource.cs"))
            .WithAssemblyName("DxMessagingAttributes");
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = attributes.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            "Compile shipped attribute contracts in their normal assembly context: "
                + string.Join("\n", emitted.Diagnostics)
        );
        MetadataReference contract = MetadataReference.CreateFromImage(image.ToArray());
        CSharpCompilationOptions compileOptions = input.Options.WithNullableContextOptions(
            globalWarnings ? NullableContextOptions.Enable : NullableContextOptions.Disable
        );
        return CSharpCompilation.Create(
            "UnconstrainedStorage",
            new[] { CSharpSyntaxTree.ParseText(source, options, "UserSource.cs") },
            input.References.Append(contract),
            compileOptions
        );
    }

    private static void AssertCallerExecutes(
        Compilation compilation,
        string initializer,
        bool callsUser,
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
        AssemblyLoadContext load = new("UnconstrainedStorage", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? read = assembly.GetType("Probe")?.GetMethod("Read");
            Assert.That(read, Is.Not.Null, context + ": caller exists");
            int first = initializer == "None" || callsUser ? 0 : 17;
            int initializers = initializer == "None" ? 0 : 1;
            int calls = callsUser ? 1 : 0;
            int[] expected =
            {
                42,
                first,
                1,
                initializers,
                calls,
                43,
                first,
                1,
                initializers,
                calls,
            };
            Assert.That(
                read!.Invoke(null, null),
                Is.EqualTo(expected),
                context
                    + ": preserve both int and string values, default state and side-effect counts"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
