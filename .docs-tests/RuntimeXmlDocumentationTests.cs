using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

[TestFixture]
internal sealed class RuntimeXmlDocumentationTests
{
    [TestCase(
        false,
        false,
        TestName = "Runtime XML grammar, attachment and parameter tags: plain .NET"
    )]
    [TestCase(
        true,
        false,
        TestName = "Runtime XML grammar, attachment and parameter tags: Unity player projection"
    )]
    [TestCase(
        true,
        true,
        TestName = "Runtime XML grammar, attachment and parameter tags: Unity Editor projection"
    )]
    public void RuntimeDocumentationHasValidXmlAndParameterDocumentation(bool unity, bool editor)
    {
        string root = ResolveRepoRoot();
        string[] symbols = GetSymbols(unity, editor);
        SyntaxTree[] trees = Directory
            .EnumerateFiles(Path.Combine(root, "Runtime"), "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Parse(File.ReadAllText(path), path, symbols))
            .ToArray();

        ImmutableArray<Diagnostic> diagnostics = GetDocumentationStructureDiagnostics(trees);

        Assert.That(
            diagnostics,
            Is.Empty,
            string.Join(
                Environment.NewLine,
                diagnostics.Select(diagnostic => diagnostic.ToString())
            )
        );
    }

    [TestCase(
        "/// <summary>A & B.</summary>\npublic sealed class Sample { }",
        "CS1570",
        TestName = "Runtime XML guard rejects unescaped text"
    )]
    [TestCase(
        "[System.Obsolete]\n/// <summary>Sample.</summary>\npublic sealed class Sample { }",
        "CS1587",
        TestName = "Runtime XML guard rejects documentation after an attribute"
    )]
    [TestCase(
        "/// <summary>Sample.</summary>\n[System.Obsolete]\npublic sealed class Sample { }",
        "",
        TestName = "Runtime XML guard accepts documentation before attributes"
    )]
    [TestCase(
        "/// <summary>List&lt;string&gt; holds A &amp; B.</summary>\npublic sealed class Sample { }",
        "",
        TestName = "Runtime XML guard accepts escaped generic and text characters"
    )]
    [TestCase(
        "/// <summary>Sample.</summary>\npublic sealed class Sample {\n/// <param name=\"missing\">Input.</param>\npublic void Run(int value) { }\n}",
        "CS1572",
        TestName = "Runtime XML guard rejects an unknown parameter tag"
    )]
    [TestCase(
        "/// <summary>Sample.</summary>\npublic sealed class Sample {\n/// <param name=\"first\">First input.</param>\npublic void Run(int first, int second) { }\n}",
        "CS1573",
        TestName = "Runtime XML guard rejects a partially documented parameter list"
    )]
    [TestCase(
        "/// <summary>Sample.</summary>\n/// <typeparam name=\"Wrong\">Type.</typeparam>\npublic sealed class Sample<T> { }",
        "CS1711",
        TestName = "Runtime XML guard rejects an unknown type parameter tag"
    )]
    [TestCase(
        "/// <summary>Sample.</summary>\n/// <typeparam name=\"T\">First type.</typeparam>\npublic sealed class Sample<T, U> { }",
        "CS1712",
        TestName = "Runtime XML guard rejects a partially documented type parameter list"
    )]
    [TestCase(
        "/// <summary>Sample.</summary>\n/// <typeparam name=\"T\">Type.</typeparam>\npublic sealed class Sample<T> {\n/// <summary>Run.</summary>\n/// <typeparam name=\"U\">Method type.</typeparam>\n/// <param name=\"value\">Input.</param>\npublic void Run<U>(int value) { }\n}",
        "",
        TestName = "Runtime XML guard accepts matching parameter and type parameter tags"
    )]
    [TestCase(
        "public sealed class Sample { public int Value { get; } }",
        "CS1591",
        TestName = "Runtime XML guard rejects missing public documentation"
    )]
    public void GuardDistinguishesInvalidDocumentation(string source, string expectedCode)
    {
        SyntaxTree tree = Parse(source, "Control.cs", Array.Empty<string>());
        ImmutableArray<Diagnostic> diagnostics = GetDocumentationStructureDiagnostics(
            new[] { tree }
        );

        if (expectedCode.Length == 0)
        {
            Assert.That(diagnostics, Is.Empty);
        }
        else
        {
            Assert.That(
                diagnostics,
                Has.Some.Matches<Diagnostic>(diagnostic =>
                    diagnostic is not null && diagnostic.Id == expectedCode
                )
            );
        }
    }

    private static SyntaxTree Parse(string source, string path, string[] symbols)
    {
        CSharpParseOptions options = new(
            LanguageVersion.CSharp9,
            documentationMode: DocumentationMode.Diagnose,
            preprocessorSymbols: symbols
        );
        return CSharpSyntaxTree.ParseText(source, options, path);
    }

    private static ImmutableArray<Diagnostic> GetDocumentationStructureDiagnostics(
        IEnumerable<SyntaxTree> trees
    )
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "RuntimeXmlGrammar",
            syntaxTrees: trees,
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        /*
            These checks cover XML grammar, attachment, tag names, partial lists and public comment presence.
            Member links and supported Unity compilation require complete reference inputs.
        */
        return compilation
            .GetDiagnostics()
            .Where(diagnostic =>
                diagnostic.Id
                    is "CS1570"
                        or "CS1587"
                        or "CS1572"
                        or "CS1573"
                        or "CS1711"
                        or "CS1712"
                        or "CS1591"
            )
            .ToImmutableArray();
    }

    private static string[] GetSymbols(bool unity, bool editor)
    {
        if (!unity)
        {
            return Array.Empty<string>();
        }

        return editor
            ? new[] { "UNITY_2021_3_OR_NEWER", "UNITY_6000_4_OR_NEWER", "UNITY_EDITOR" }
            : new[] { "UNITY_2021_3_OR_NEWER", "UNITY_6000_4_OR_NEWER" };
    }

    private static string ResolveRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
