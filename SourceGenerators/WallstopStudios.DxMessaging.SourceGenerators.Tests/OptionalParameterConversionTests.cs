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
internal sealed class OptionalParameterConversionTests
{
    [TestCaseSource(nameof(ConversionCases))]
    public void OptionalDefaultsFollowConstantExpressionConversions(
        string fieldType,
        string expression,
        bool valid,
        bool namedExpression
    )
    {
        string argument = namedExpression
            ? "Expression = " + SyntaxFactory.Literal(expression)
            : expression;
        string source = $$"""
using DxMessaging.Core.Attributes;
public enum Choice { None, Other }
public static class Defaults { public const int Low = 7; public const int High = 256; }
[DxAutoConstructor]
public readonly partial struct Packet
{
    public readonly int Required;
    [DxOptionalParameter({{argument}})]
    public readonly {{fieldType}} Value;
}
public static class Consumer
{
    public static object ReadDefault() => new Packet(1).Value;
    public static object ReadExplicit() => new Packet(1, ({{fieldType}})({{expression}})).Value;
}
""";
        string context = $"{fieldType}, {expression}, namedExpression={namedExpression}";
        CSharpCompilation compilation = CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation generatedCompilation,
            out ImmutableArray<Diagnostic> generatorDiagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Single().Exception,
            Is.Null,
            "The real generator must complete: " + context
        );
        Assert.That(
            driver.GetRunResult().GeneratedTrees,
            Has.Length.EqualTo(1),
            "The attributed packet must produce one constructor: " + context
        );
        Diagnostic[] diagnostics = generatedCompilation.GetDiagnostics().ToArray();
        Diagnostic[] defaults = generatorDiagnostics.Where(d => d.Id == "DXMSG005").ToArray();
        if (!valid)
        {
            Assert.That(defaults, Has.Length.EqualTo(1), "Reject invalid default: " + context);
            Assert.That(
                defaults[0].Location.SourceTree,
                Is.EqualTo(compilation.SyntaxTrees.Last()),
                "Report the invalid field attribute in user source: " + context
            );
            Assert.That(
                diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error),
                Is.True,
                "The independent compiler must also reject the default: " + context
            );
            return;
        }

        Assert.That(
            diagnostics,
            Is.Empty,
            "The consumer must compile with C#9 and fatal warnings: "
                + context
                + "\n"
                + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );
        Assert.That(generatorDiagnostics, Is.Empty, "Accept compiler-valid default: " + context);
        using MemoryStream stream = new();
        EmitResult emit = generatedCompilation.Emit(stream);
        Assert.That(emit.Success, Is.True, "Emit the compiled consumer: " + context);
        stream.Position = 0;
        AssemblyLoadContext loadContext = new("OptionalParameterConsumer", isCollectible: true);
        try
        {
            Assembly assembly = loadContext.LoadFromStream(stream);
            object? actual = assembly
                .GetType("Consumer")!
                .GetMethod("ReadDefault")!
                .Invoke(null, null);
            Assert.That(
                actual,
                Is.Not.Null,
                "The omitted argument must supply its value: " + context
            );
            object? expected = assembly
                .GetType("Consumer")!
                .GetMethod("ReadExplicit")!
                .Invoke(null, null);
            Assert.That(
                actual,
                Is.EqualTo(expected),
                "Use the original expression's value: " + context
            );
            ParameterInfo parameter = assembly
                .GetType("Packet")!
                .GetConstructors()
                .Single()
                .GetParameters()[1];
            Assert.That(
                actual,
                Is.EqualTo(parameter.DefaultValue),
                "The consumer must receive the emitted optional default: " + context
            );
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static IEnumerable<TestCaseData> ConversionCases()
    {
        foreach (
            (string type, string expression, bool valid) in new (string, string, bool)[]
            {
                ("byte", "Defaults.Low", true),
                ("byte", "Defaults.High", false),
                ("byte", "1 + 2", true),
                ("byte", "255 + 1", false),
                ("byte", "0", true),
                ("byte", "255", true),
                ("byte", "-1", false),
                ("byte", "256", false),
                ("sbyte", "-128", true),
                ("sbyte", "127", true),
                ("sbyte", "-129", false),
                ("sbyte", "128", false),
                ("short", "-32768", true),
                ("short", "32767", true),
                ("short", "-32769", false),
                ("short", "32768", false),
                ("ushort", "0", true),
                ("ushort", "65535", true),
                ("ushort", "-1", false),
                ("ushort", "65536", false),
                ("uint", "0", true),
                ("uint", "2147483647", true),
                ("uint", "-1", false),
                ("ulong", "0", true),
                ("ulong", "1L", true),
                ("ulong", "-1L", false),
                ("char", "65", false),
                ("Choice", "0", true),
                ("Choice", "1", false),
                ("byte?", "7", true),
                ("byte?", "256", false),
                ("bool", "true", true),
                ("bool", "1", false),
                ("string", "\"hello\"", true),
                ("string", "1", false),
                ("float", "3", true),
                ("float", "3.5", false),
            }
        )
        {
            yield return new TestCaseData(type, expression, valid, false);
            yield return new TestCaseData(type, expression, valid, true);
        }
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Find the repository containing the test assembly.");
        string attributes = Path.Combine(root!.FullName, "Runtime", "Core", "Attributes");
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp9);
        List<SyntaxTree> trees = new();
        foreach (
            string file in new[]
            {
                "DxAutoConstructorAttribute.cs",
                "DxOptionalParameterAttribute.cs",
            }
        )
        {
            string path = Path.Combine(attributes, file);
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(path), parseOptions, path));
        }
        trees.Add(CSharpSyntaxTree.ParseText(source, parseOptions, "UserSource.cs"));
        return CSharpCompilation.Create(
            "OptionalParameterConsumer",
            trees,
            GeneratorTestUtilities.CompilationReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
    }
}
