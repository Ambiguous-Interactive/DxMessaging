using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

[TestFixture]
internal sealed class RuntimeXmlDocumentationTests
{
    [TestCase(false, false, TestName = "Runtime XML grammar and attachment: plain .NET")]
    [TestCase(
        true,
        false,
        TestName = "Runtime XML grammar and attachment: Unity player projection"
    )]
    [TestCase(true, true, TestName = "Runtime XML grammar and attachment: Unity Editor projection")]
    public void RuntimeDocumentationHasValidXmlAndDeclarationAttachment(bool unity, bool editor)
    {
        string root = ResolveRepoRoot();
        string[] symbols = GetSymbols(unity, editor);
        SyntaxTree[] trees = Directory
            .EnumerateFiles(Path.Combine(root, "Runtime"), "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Parse(File.ReadAllText(path), path, symbols))
            .ToArray();

        ImmutableArray<Diagnostic> diagnostics = GetGrammarAndAttachmentDiagnostics(trees);

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
    public void GuardDistinguishesInvalidDocumentation(string source, string expectedCode)
    {
        SyntaxTree tree = Parse(source, "Control.cs", Array.Empty<string>());
        ImmutableArray<Diagnostic> diagnostics = GetGrammarAndAttachmentDiagnostics(new[] { tree });

        if (expectedCode.Length == 0)
        {
            Assert.That(diagnostics, Is.Empty);
        }
        else
        {
            Assert.That(
                diagnostics,
                Has.Some.Matches<Diagnostic>(diagnostic => diagnostic.Id == expectedCode)
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

    private static ImmutableArray<Diagnostic> GetGrammarAndAttachmentDiagnostics(
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
            These checks cover XML grammar and comment attachment. Semantic links and
            supported Unity compilation require their own complete reference inputs.
        */
        return compilation
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Id is "CS1570" or "CS1587")
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
