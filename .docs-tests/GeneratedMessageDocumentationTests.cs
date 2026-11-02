using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.Docs.Tests;

/// <summary>
/// Compares documented generated types with the generator and shipped message contracts.
/// </summary>
[TestFixture]
internal sealed class GeneratedMessageDocumentationTests
{
    [TestCase(
        "Heal",
        "## Why Use Attributes Instead of Manual Implementation",
        "## Complete Example:"
    )]
    [TestCase("PlayerDamaged", "## Complete Example:", "## Advanced:")]
    [TestCase(
        "MyMessage",
        "### \"Why `partial` when using attributes?\"",
        "### \"Can I see the generated code?\""
    )]
    public void DocumentedGeneratedTypesExposeTheActualInterfaces(
        string typeName,
        string sectionStart,
        string sectionEnd
    )
    {
        string root = ResolveRoot();
        string path = Path.Combine(root, "docs", "reference", "helpers.md");
        string markdown = File.ReadAllText(path);
        int start = markdown.IndexOf(sectionStart, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), sectionStart);
        int end = markdown.IndexOf(
            sectionEnd,
            start + sectionStart.Length,
            StringComparison.Ordinal
        );
        Assert.That(end, Is.GreaterThan(start), sectionEnd);
        CSharpParseOptions options = new(LanguageVersion.CSharp9);
        CompilationUnitSyntax[] snippets = Regex
            .Matches(markdown[start..end], @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .Select(match =>
                CSharpSyntaxTree
                    .ParseText(match.Groups["code"].Value, options)
                    .GetCompilationUnitRoot()
            )
            .ToArray();
        TypeDeclarationSyntax[] declarations = snippets
            .SelectMany(snippet => snippet.Members.OfType<TypeDeclarationSyntax>())
            .Where(type => type.Identifier.ValueText == typeName)
            .ToArray();
        Assert.That(declarations, Has.Length.EqualTo(2), path + ": " + typeName);

        CSharpCompilation documented = CreateCompilation(root, new[] { declarations[1] }, options);
        AssertCompiles(documented, "Documented public type: " + typeName);

        CSharpCompilation input = CreateCompilation(root, new[] { declarations[0] }, options);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxMessageIdGenerator(), new DxAutoConstructorGenerator() },
            parseOptions: options
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            input,
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            typeName
        );
        Assert.That(diagnostics, Is.Empty, typeName + "\n" + string.Join("\n", diagnostics));
        AssertCompiles(generated, "Actual generated public type: " + typeName);

        INamedTypeSymbol documentedType = documented.GetTypeByMetadataName(typeName)!;
        INamedTypeSymbol generatedType = generated.GetTypeByMetadataName(typeName)!;
        Assert.That(documentedType, Is.Not.Null, typeName);
        Assert.That(generatedType, Is.Not.Null, typeName);
        Assert.That(
            documentedType
                .AllInterfaces.Select(type => type.ToDisplayString())
                .OrderBy(name => name, StringComparer.Ordinal),
            Is.EqualTo(
                generatedType
                    .AllInterfaces.Select(type => type.ToDisplayString())
                    .OrderBy(name => name, StringComparer.Ordinal)
            ),
            "The documented generated type must expose the same interfaces as the shipped generator: "
                + typeName
        );
    }

    private static CSharpCompilation CreateCompilation(
        string root,
        IEnumerable<TypeDeclarationSyntax> declarations,
        CSharpParseOptions options
    )
    {
        string[] contracts =
        {
            "Runtime/Core/IMessage.cs",
            "Runtime/Core/Messages/IUntargetedMessage.cs",
            "Runtime/Core/Messages/ITargetedMessage.cs",
            "Runtime/Core/Messages/IBroadcastMessage.cs",
            "Runtime/Core/Attributes/DxUntargetedMessageAttribute.cs",
            "Runtime/Core/Attributes/DxTargetedMessageAttribute.cs",
            "Runtime/Core/Attributes/DxBroadcastMessageAttribute.cs",
            "Runtime/Core/Attributes/DxAutoConstructorAttribute.cs",
            "Runtime/Core/Attributes/DxOptionalParameterAttribute.cs",
        };
        List<SyntaxTree> trees = contracts
            .Select(relative =>
            {
                string path = Path.Combine(root, relative);
                return CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path);
            })
            .ToList<SyntaxTree>();
        string source =
            "using System;\nusing DxMessaging.Core;\nusing DxMessaging.Core.Attributes;\nusing DxMessaging.Core.Messages;\n"
            + string.Join("\n", declarations.Select(declaration => declaration.ToFullString()));
        trees.Add(CSharpSyntaxTree.ParseText(source, options, "PublishedExample.cs"));
        string references =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        return CSharpCompilation.Create(
            "PublishedMessageExample",
            trees,
            references
                .Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
    }

    private static void AssertCompiles(Compilation compilation, string context)
    {
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(image);
        Assert.That(
            result.Success,
            Is.True,
            context + "\n" + string.Join("\n", result.Diagnostics)
        );
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
                "Find the repository containing the documentation tests."
            );
    }
}
