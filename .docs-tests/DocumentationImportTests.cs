using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

/// <summary>
/// Checks documented imports against compiled production types rather than snippet stubs.
/// </summary>
[TestFixture]
internal sealed class DocumentationImportTests
{
    private static readonly Lazy<MetadataReference> PortableRuntime = new(() =>
        MetadataReference.CreateFromImage(
            RuntimeHashCompilationTests.CompilePortableRuntimeImage(false)
        )
    );

    [TestCase(false)]
    [TestCase(true)]
    public void BuilderCallbackSignatureResolvesFromDocumentedImports(bool qualifyParameter)
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Locate the repository containing the guide.");
        string path = Path.Combine(root!.FullName, "docs", "advanced", "registration-builders.md");
        string markdown = File.ReadAllText(path);
        const string Start = "### DI Integration Pattern";
        const string End = "### Custom Lifecycle Hooks";
        int start = markdown.IndexOf(Start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), path + ": " + Start);
        int end = markdown.IndexOf(End, start + Start.Length, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start), path + ": " + End);
        Match[] blocks = Regex
            .Matches(markdown[start..end], @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .ToArray();
        Assert.That(blocks, Has.Length.EqualTo(1), path + ": one complete DI example");
        CSharpParseOptions options = new(LanguageVersion.CSharp9);
        CompilationUnitSyntax published = CSharpSyntaxTree
            .ParseText(blocks[0].Groups["code"].Value, options)
            .GetCompilationUnitRoot();
        MethodDeclarationSyntax[] callbacks = published
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "ConfigureRegistrations")
            .ToArray();
        Assert.That(callbacks, Has.Length.EqualTo(1), path + ": declared registration callback");
        MethodDeclarationSyntax signature = callbacks[0].WithBody(SyntaxFactory.Block());
        Assert.That(
            signature.ParameterList.Parameters,
            Has.Count.EqualTo(1),
            path + ": token parameter"
        );
        if (qualifyParameter)
        {
            ParameterSyntax parameter = signature
                .ParameterList.Parameters[0]
                .WithType(
                    SyntaxFactory.ParseTypeName("global::DxMessaging.Core.MessageRegistrationToken")
                );
            signature = signature.WithParameterList(
                signature.ParameterList.WithParameters(
                    SyntaxFactory.SingletonSeparatedList(parameter)
                )
            );
        }
        string imports = string.Join(
            "\n",
            published
                .Usings.Where(import =>
                    import.Name?.ToString().StartsWith("DxMessaging.", StringComparison.Ordinal)
                    == true
                )
                .Select(import => import.ToFullString())
        );
        string source =
            imports
            + "\npublic sealed class DocumentedCallback { "
            + signature.NormalizeWhitespace().ToFullString()
            + " }";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, options, "DocumentedCallback.cs");
        string references =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "DocumentationImports",
            new[] { tree },
            references
                .Split(Path.PathSeparator)
                .Select(file => MetadataReference.CreateFromFile(file))
                .Append(PortableRuntime.Value),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            $"qualified control={qualifyParameter}; imports must resolve the actual token type: \n"
                + string.Join("\n", emitted.Diagnostics)
        );
        ParameterSyntax compiledParameter = tree.GetRoot()
            .DescendantNodes()
            .OfType<ParameterSyntax>()
            .Single();
        ITypeSymbol? token = compilation
            .GetSemanticModel(tree)
            .GetTypeInfo(compiledParameter.Type!)
            .Type;
        Assert.That(
            token?.ToDisplayString(),
            Is.EqualTo("DxMessaging.Core.MessageRegistrationToken"),
            path + ": actual production token identity"
        );
    }
}
