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

    [TestCase(
        "getting-started/getting-started.md",
        "## Message Types",
        "## Common Patterns",
        "GamePaused",
        "IUntargetedMessage"
    )]
    [TestCase(
        "getting-started/getting-started.md",
        "## Message Types",
        "## Common Patterns",
        "Heal",
        "ITargetedMessage"
    )]
    [TestCase(
        "getting-started/getting-started.md",
        "## Message Types",
        "## Common Patterns",
        "EnemyDied",
        "IBroadcastMessage"
    )]
    [TestCase(
        "getting-started/visual-guide.md",
        "### 1. Untargeted",
        "### 2. Targeted",
        "GamePaused",
        "IUntargetedMessage"
    )]
    [TestCase(
        "getting-started/visual-guide.md",
        "### 2. Targeted",
        "### 3. Broadcast",
        "Heal",
        "ITargetedMessage"
    )]
    [TestCase(
        "getting-started/visual-guide.md",
        "### 3. Broadcast",
        "## The Message Journey",
        "TookDamage",
        "IBroadcastMessage"
    )]
    [TestCase(
        "guides/patterns.md",
        "##### Anti-pattern to avoid",
        "### Pattern: Priority-Ordered Execution",
        "HealthChanged",
        "IBroadcastMessage"
    )]
    [TestCase(
        "guides/patterns.md",
        "##### Anti-pattern to avoid",
        "### Pattern: Priority-Ordered Execution",
        "ManaChanged",
        "IBroadcastMessage"
    )]
    [TestCase(
        "guides/patterns.md",
        "##### Anti-pattern to avoid",
        "### Pattern: Priority-Ordered Execution",
        "GoldChanged",
        "IBroadcastMessage"
    )]
    [TestCase(
        "guides/patterns.md",
        "##### Anti-pattern to avoid",
        "### Pattern: Priority-Ordered Execution",
        "PlayerStatsChanged",
        "IBroadcastMessage"
    )]
    public void IntroductoryMessageDeclarationsCompileAgainstShippedContracts(
        string page,
        string sectionStart,
        string sectionEnd,
        string typeName,
        string route
    )
    {
        string root = ResolveRoot();
        string path = Path.Combine(root, "docs", page);
        CSharpParseOptions options = new(LanguageVersion.CSharp9);
        CompilationUnitSyntax[] snippets = ReadSnippets(path, sectionStart, sectionEnd, options);
        TypeDeclarationSyntax[] declarations = snippets
            .SelectMany(unit => unit.Members.OfType<TypeDeclarationSyntax>())
            .Where(type => type.Identifier.ValueText == typeName)
            .ToArray();
        Assert.That(declarations, Has.Length.EqualTo(1), path + ": " + typeName);
        TypeDeclarationSyntax declaration = declarations[0];
        TypeDeclarationSyntax manual = declaration
            .WithAttributeLists(default)
            .WithBaseList(
                SyntaxFactory.BaseList(
                    SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                        SyntaxFactory.SimpleBaseType(
                            SyntaxFactory.ParseTypeName("DxMessaging.Core.Messages." + route)
                        )
                    )
                )
            )
            .AddMembers(
                SyntaxFactory.ParseMemberDeclaration(
                    $"public System.Type MessageType => typeof({typeName});"
                )!
            );
        AssertCompiles(
            CreateCompilation(root, new[] { manual }, options),
            "Handwritten route oracle: " + path + ": " + typeName
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxMessageIdGenerator(), new DxAutoConstructorGenerator() },
            parseOptions: options
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(root, declarations, options),
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            path + ": " + typeName
        );
        Assert.That(diagnostics, Is.Empty, path + "\n" + string.Join("\n", diagnostics));
        AssertCompiles(generated, "Published route declaration: " + path + ": " + typeName);
        INamedTypeSymbol? message = generated.GetTypeByMetadataName(typeName);
        Assert.That(message, Is.Not.Null, path + ": " + typeName);
        Assert.That(
            message!.AllInterfaces.Select(type => type.ToDisplayString()),
            Does.Contain("DxMessaging.Core.Messages." + route),
            path + ": generated route contract"
        );
    }

    [TestCase("### 1. Untargeted", "### 2. Targeted", "Emit", "RegisterUntargeted", false)]
    [TestCase(
        "### 2. Targeted",
        "### 3. Broadcast",
        "EmitGameObjectTargeted",
        "RegisterGameObjectTargeted",
        true
    )]
    [TestCase(
        "### 3. Broadcast",
        "## The Message Journey",
        "EmitGameObjectBroadcast",
        "RegisterGameObjectBroadcast",
        true
    )]
    public void VisualGuideEmissionAndRegistrationUseTheSameRoute(
        string sectionStart,
        string sectionEnd,
        string emitName,
        string registerName,
        bool explicitIdentity
    )
    {
        string path = Path.Combine(ResolveRoot(), "docs", "getting-started", "visual-guide.md");
        CompilationUnitSyntax[] snippets = ReadSnippets(
            path,
            sectionStart,
            sectionEnd,
            new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        InvocationExpressionSyntax[] invocations = snippets
            .SelectMany(unit => unit.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .ToArray();
        InvocationExpressionSyntax[] emits = invocations
            .Where(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == emitName
            )
            .ToArray();
        Assert.That(
            emits,
            Has.Length.EqualTo(1),
            sectionStart + ": exactly one documented emission"
        );
        InvocationExpressionSyntax[] registrations = invocations
            .Where(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == registerName
            )
            .ToArray();
        Assert.That(
            registrations,
            Has.Length.EqualTo(1),
            sectionStart
                + ": receiver must subscribe through the same route and identity kind as the sender"
        );
        if (explicitIdentity)
        {
            Assert.That(
                registrations[0].ArgumentList.Arguments[0].Expression.ToString(),
                Is.EqualTo(emits[0].ArgumentList.Arguments[0].Expression.ToString()),
                sectionStart + ": the receiver and sender must name the same target/source object"
            );
        }
    }

    private static CompilationUnitSyntax[] ReadSnippets(
        string path,
        string sectionStart,
        string sectionEnd,
        CSharpParseOptions options
    )
    {
        string markdown = File.ReadAllText(path);
        int start = markdown.IndexOf(sectionStart, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), path + ": " + sectionStart);
        int end = markdown.IndexOf(
            sectionEnd,
            start + sectionStart.Length,
            StringComparison.Ordinal
        );
        Assert.That(end, Is.GreaterThan(start), path + ": " + sectionEnd);
        return Regex
            .Matches(markdown[start..end], @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .Select(match =>
                CSharpSyntaxTree
                    .ParseText(match.Groups["code"].Value, options)
                    .GetCompilationUnitRoot()
            )
            .ToArray();
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
