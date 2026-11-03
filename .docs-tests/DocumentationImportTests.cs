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
    private static readonly CSharpParseOptions ProviderOptions = new(
        LanguageVersion.CSharp9,
        preprocessorSymbols: new[] { "UNITY_2021_3_OR_NEWER" }
    );

    private static readonly Lazy<MetadataReference> PortableRuntime = new(() =>
        MetadataReference.CreateFromImage(
            RuntimeHashCompilationTests.CompilePortableRuntimeImage(false)
        )
    );

    [TestCase("DiagnosticLogger", false)]
    [TestCase("DiagnosticLogger", true)]
    [TestCase("PlayerController", false)]
    [TestCase("PlayerController", true)]
    public void ProviderExamplesResolveFromDocumentedImports(string exampleName, bool qualifyName)
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Locate the repository containing the provider guide.");
        string path = Path.Combine(root!.FullName, "docs", "advanced", "message-bus-providers.md");
        CSharpParseOptions options = ProviderOptions;
        CompilationUnitSyntax[] examples = Regex
            .Matches(File.ReadAllText(path), @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .Select(match =>
                CSharpSyntaxTree
                    .ParseText(match.Groups["code"].Value, options)
                    .GetCompilationUnitRoot()
            )
            .Where(unit =>
                unit.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Any(type => type.Identifier.ValueText == exampleName)
            )
            .ToArray();
        Assert.That(examples, Has.Length.EqualTo(1), path + ": " + exampleName);
        CompilationUnitSyntax published = examples[0];
        bool scriptable = exampleName == "DiagnosticLogger";
        string expectedType = scriptable
            ? "DxMessaging.Unity.InitialGlobalMessageBusProvider"
            : "DxMessaging.Core.MessageBus.MessageBusRebindMode";
        string expression;
        if (scriptable)
        {
            FieldDeclarationSyntax field = published
                .DescendantNodes()
                .OfType<FieldDeclarationSyntax>()
                .Single(candidate =>
                    candidate.Declaration.Variables.Any(variable =>
                        variable.Identifier.ValueText == "initialProvider"
                    )
                );
            expression = qualifyName
                ? "global::" + expectedType
                : field.Declaration.Type.ToString();
        }
        else
        {
            InvocationExpressionSyntax configure = published
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Single(call => call.Expression.ToString() == "ConfigureMessageBus");
            expression = qualifyName
                ? "global::" + expectedType + ".RebindActive"
                : configure.ArgumentList.Arguments[1].Expression.ToString();
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
        string member = scriptable
            ? "public void Use(" + expression + " provider) { }"
            : "public object Use() => " + expression + ";";
        SyntaxTree probe = CSharpSyntaxTree.ParseText(
            imports + "\npublic sealed class DocumentedProvider { " + member + " }",
            options,
            "DocumentedProvider.cs"
        );
        // Compile the actual provider declarations. Only their external Unity base and
        // attribute are supplied here; no DxMessaging declaration is replaced by a stub.
        List<SyntaxTree> trees = new() { probe };
        foreach (
            string file in new[]
            {
                "ScriptableMessageBusProvider.cs",
                "InitialGlobalMessageBusProvider.cs",
            }
        )
        {
            trees.Add(
                CSharpSyntaxTree.ParseText(
                    File.ReadAllText(Path.Combine(root.FullName, "Runtime", "Unity", file)),
                    options,
                    file
                )
            );
        }
        trees.Add(
            CSharpSyntaxTree.ParseText(
                "namespace UnityEngine { public abstract class ScriptableObject { } "
                    + "public sealed class CreateAssetMenuAttribute : System.Attribute { "
                    + "public string fileName = string.Empty; public string menuName = string.Empty; } }",
                options,
                "ExternalUnityTypes.cs"
            )
        );
        string references =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ProviderImports",
            trees,
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
            $"{exampleName}; qualified control={qualifyName}: \n"
                + string.Join("\n", emitted.Diagnostics)
        );
        ITypeSymbol? resolved;
        if (scriptable)
        {
            TypeSyntax type = probe
                .GetRoot()
                .DescendantNodes()
                .OfType<ParameterSyntax>()
                .Single()
                .Type!;
            resolved = compilation.GetSemanticModel(probe).GetTypeInfo(type).Type;
        }
        else
        {
            ExpressionSyntax value = probe
                .GetRoot()
                .DescendantNodes()
                .OfType<ArrowExpressionClauseSyntax>()
                .Single()
                .Expression;
            resolved = compilation.GetSemanticModel(probe).GetTypeInfo(value).Type;
        }
        Assert.That(
            resolved?.ToDisplayString(),
            Is.EqualTo(expectedType),
            path + ": actual production type identity"
        );
    }

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
