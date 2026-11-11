using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.Docs.Tests;

[TestFixture]
internal sealed class OptionalStructDocumentationTests
{
    [TestCase(false, "new Packet()")]
    [TestCase(false, "default(Packet)")]
    [TestCase(false, "new Packet(2f)")]
    [TestCase(false, "new Packet(Label: \"custom\")")]
    [TestCase(true, "new Packet()")]
    [TestCase(true, "default(Packet)")]
    [TestCase(true, "new Packet(2f)")]
    [TestCase(true, "new Packet(Label: \"custom\")")]
    public void OptionalConstructorsRetainOrdinaryCSharpConstruction(
        bool referenceType,
        string creation
    )
    {
        foreach (bool generated in new[] { false, true })
        {
            string members = generated
                ? ""
                : "public Packet(float Value = 1.25f, string Label = \"ready\") { this.Value = Value; this.Label = Label; }";
            string source = $$"""
                using DxMessaging.Core.Attributes;
                {{(generated ? "[DxAutoConstructor]" : "")}}
                public partial {{(referenceType ? "class" : "struct")}} Packet
                {
                    [DxOptionalParameter(1.25f)] public readonly float Value;
                    [DxOptionalParameter("ready")] public readonly string Label;
                    {{members}}
                }
                public static class DocProbe { public static object Read() => {{creation}}; }
                """;
            WithCompiledCaller(source, assembly =>
            {
                object? value = assembly.GetType("DocProbe")!.GetMethod("Read")!.Invoke(null, null);
                string context =
                    $"referenceType={referenceType}; generated={generated}; creation={creation}";
                if (referenceType && creation == "default(Packet)")
                {
                    Assert.That(value, Is.Null, context);
                    return;
                }
                Assert.That(value, Is.Not.Null, context);
                bool zero = !referenceType && creation is "new Packet()" or "default(Packet)";
                AssertField(
                    value!,
                    "Value",
                    zero ? 0f
                        : creation == "new Packet(2f)" ? 2f
                        : 1.25f,
                    context
                );
                AssertField(
                    value!,
                    "Label",
                    zero ? null
                        : creation.Contains("custom", StringComparison.Ordinal) ? "custom"
                        : "ready",
                    context
                );
            });
        }
    }

    [TestCase("SpawnEffect")]
    [TestCase("AudioSettingsChanged")]
    public void DocumentedOptionalStructCallersApplyTheirStatedDefaults(string typeName)
    {
        string path = Path.Combine(ResolveRoot(), "docs", "reference", "helpers.md");
        string[] snippets = Regex
            .Matches(File.ReadAllText(path), @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .Select(match => match.Groups["code"].Value)
            .Where(code => code.Contains("partial struct " + typeName, StringComparison.Ordinal))
            .ToArray();
        Assert.That(snippets, Has.Length.EqualTo(1), path + ": " + typeName);
        CompilationUnitSyntax root = CSharpSyntaxTree
            .ParseText(snippets[0], new CSharpParseOptions(LanguageVersion.CSharp9))
            .GetCompilationUnitRoot();
        string declarations = string.Join(
            "\n",
            root.Members.Where(member => member is not GlobalStatementSyntax)
        );
        string statements = string.Join("\n", root.Members.OfType<GlobalStatementSyntax>());
        string result;
        if (typeName == "SpawnEffect")
        {
            ObjectCreationExpressionSyntax first = root
                .Members.OfType<GlobalStatementSyntax>()
                .SelectMany(statement =>
                    statement.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                )
                .First();
            result = $"return new object[] {{ {first}, largeEffect, redEffect, customEffect }};";
        }
        else
        {
            result = "return EmissionFixture.Values.ToArray();";
        }
        string[] imports =
        {
            "using DxMessaging.Core.Attributes;",
            "using DxMessaging.Core.Extensions;",
            "using UnityEngine;",
        };
        string source =
            string.Join(
                "\n",
                imports
                    .Concat(root.Usings.Select(directive => directive.ToString()))
                    .Distinct(StringComparer.Ordinal)
            )
            + "\n"
            + declarations
            + "\npublic static class DocProbe { public static object[] Read() {\n"
            + statements
            + "\n"
            + result
            + "\n} }\n"
            + Fixtures;
        WithCompiledCaller(source, assembly =>
        {
            object[] values = (object[])
                assembly.GetType("DocProbe")!.GetMethod("Read")!.Invoke(null, null)!;
            if (typeName == "SpawnEffect")
            {
                Assert.That(values, Has.Length.EqualTo(4));
                AssertField(values[0], "scale", 1f, typeName + " documented defaults");
                AssertField(values[0], "duration", 5f, typeName + " documented defaults");
                AssertField(values[0], "looping", false, typeName + " documented defaults");
                AssertField(values[1], "scale", 2.5f, typeName + " positional argument");
                AssertField(values[1], "duration", 5f, typeName + " omitted argument");
                AssertField(
                    values[2],
                    "color",
                    assembly.GetType("UnityEngine.Color")!.GetProperty("red")!.GetValue(null),
                    typeName + " custom color"
                );
                AssertField(values[3], "duration", 10f, typeName + " explicit arguments");
                AssertField(values[3], "looping", true, typeName + " explicit arguments");
            }
            else
            {
                Assert.That(values, Has.Length.EqualTo(3));
                AssertField(values[0], "masterVolume", 1f, typeName + " documented defaults");
                AssertField(values[0], "musicVolume", 0.8f, typeName + " documented defaults");
                AssertField(values[0], "sfxVolume", 1f, typeName + " documented defaults");
                AssertField(
                    values[0],
                    "muteWhenUnfocused",
                    true,
                    typeName + " documented defaults"
                );
                AssertField(values[1], "masterVolume", 1f, typeName + " named argument");
                AssertField(values[1], "musicVolume", 0.5f, typeName + " named argument");
                AssertField(values[1], "muteWhenUnfocused", true, typeName + " omitted argument");
                AssertField(values[2], "masterVolume", 0.7f, typeName + " explicit arguments");
                AssertField(
                    values[2],
                    "muteWhenUnfocused",
                    false,
                    typeName + " explicit arguments"
                );
            }
        });
    }

    private static void AssertField(object instance, string name, object? expected, string context)
    {
        Assert.That(
            instance.GetType().GetField(name)!.GetValue(instance),
            Is.EqualTo(expected),
            context + ": " + name
        );
    }

    private static void WithCompiledCaller(string source, Action<Assembly> check)
    {
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp9);
        string root = ResolveRoot();
        List<SyntaxTree> trees = new()
        {
            CSharpSyntaxTree.ParseText(source, parseOptions, "DocumentedCaller.cs"),
        };
        foreach (
            string file in new[]
            {
                "DxAutoConstructorAttribute.cs",
                "DxOptionalParameterAttribute.cs",
                "DxBroadcastMessageAttribute.cs",
                "DxUntargetedMessageAttribute.cs",
            }
        )
        {
            string path = Path.Combine(root, "Runtime", "Core", "Attributes", file);
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(path), parseOptions, path));
        }
        string paths = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "OptionalStructDocs_" + Guid.NewGuid().ToString("N"),
            trees,
            paths.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: parseOptions
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(driver.GetRunResult().Results.Select(result => result.Exception), Has.All.Null);
        Assert.That(diagnostics, Is.Empty, string.Join("\n", diagnostics));
        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = generated.Emit(stream);
        Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));
        AssemblyLoadContext load = new("OptionalStructDocs", isCollectible: true);
        try
        {
            stream.Position = 0;
            check(load.LoadFromStream(stream));
        }
        finally
        {
            load.Unload();
        }
    }

    private static string ResolveRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        return root?.FullName
            ?? throw new DirectoryNotFoundException(
                "Find the repository containing the test assembly."
            );
    }

    /*
        Only Unity color values and emission capture are fixtures. Constructor generation,
        attribute overloads, document declarations and caller statements are actual sources.
        This test does not establish native Unity rendering or message dispatch behavior.
    */
    private const string Fixtures = """
        namespace UnityEngine
        {
            public struct Color
            {
                public float R;
                public static Color red => new Color { R = 1f };
                public static Color blue => new Color { R = 2f };
            }
        }
        public static class EmissionFixture { public static readonly System.Collections.Generic.List<object> Values = new System.Collections.Generic.List<object>(); }
        namespace DxMessaging.Core.Extensions
        {
            public static class CaptureExtensions
            {
                public static void Emit<T>(this ref T message) where T : struct => EmissionFixture.Values.Add(message);
            }
        }
        """;
}
