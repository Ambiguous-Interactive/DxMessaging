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

    /// <summary>Compiles the full ownership guide against real APIs and rejects broken group, move, and state calls.</summary>
    [TestCase("published", true)]
    [TestCase("group", false)]
    [TestCase("source", false)]
    [TestCase("state", false)]
    public void SubscriptionOwnershipGuideUsesActualRuntimeContracts(
        string shape,
        bool expectedSuccess
    )
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Locate the subscription ownership guide.");
        string path = Path.Combine(root!.FullName, "docs", "guides", "subscription-ownership.md");
        Match[] examples = Regex
            .Matches(File.ReadAllText(path), @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .ToArray();
        Assert.That(examples, Has.Length.EqualTo(1), path + ": one complete ownership example");
        string source = examples[0].Groups["code"].Value;
        string marker = string.Empty;
        if (shape == "group")
        {
            source = source.Replace(
                ".CreateGroup()",
                ".CreateMissingGroup()",
                StringComparison.Ordinal
            );
            marker = "CreateMissingGroup";
        }
        else if (shape == "source")
        {
            source = source.Replace(
                ".RetargetSource(",
                ".RetargetMissingSource(",
                StringComparison.Ordinal
            );
            marker = "RetargetMissingSource";
        }
        else if (shape == "state")
        {
            source = source.Replace(
                "RegisterBroadcast<ParentChanged, ParentSubscriptions>",
                "RegisterBroadcast<ParentChanged, ParentSubscriptions, int>",
                StringComparison.Ordinal
            );
            marker = "RegisterBroadcast";
        }
        else
        {
            Assert.That(shape, Is.EqualTo("published"), path + ": known control shape");
        }
        string references = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.CSharp9),
            path
        );
        CSharpCompilation compilation = CSharpCompilation.Create(
            "SubscriptionOwnershipGuide",
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
            Is.EqualTo(expectedSuccess),
            $"{path}; shape={shape}:\n" + string.Join("\n", emitted.Diagnostics)
        );
        if (!expectedSuccess)
        {
            Assert.That(
                emitted.Diagnostics.Any(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error
                    && diagnostic
                        .GetMessage(System.Globalization.CultureInfo.InvariantCulture)
                        .Contains(marker, StringComparison.Ordinal)
                ),
                Is.True,
                $"{path}; shape={shape}: actual API binding must reject {marker}"
            );
        }
    }

    /// <summary>Check the inherited bus configuration call against the production declaration.</summary>
    /// <remarks>2026-11-03: ZenjectAwareComponent called Configure instead of its inherited API.</remarks>
    [Test]
    public void ZenjectAwareExampleUsesTheActualInheritedConfigurationMethod()
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Locate the integration guide and production component.");
        string page = Path.Combine(root!.FullName, "docs", "integrations", "zenject.md");
        CompilationUnitSyntax example = Regex
            .Matches(File.ReadAllText(page), @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .Select(match =>
                CSharpSyntaxTree.ParseText(match.Groups["code"].Value).GetCompilationUnitRoot()
            )
            .Single(unit =>
                unit.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Any(type => type.Identifier.ValueText == "ZenjectAwareComponent")
            );
        InvocationExpressionSyntax call = example
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation =>
                invocation.ArgumentList.Arguments.Any(argument =>
                    argument.Expression.ToString() == "MessageBusRebindMode.RebindActive"
                )
            );
        IdentifierNameSyntax? calledMethod = call.Expression as IdentifierNameSyntax;
        Assert.That(calledMethod, Is.Not.Null, page + ": inherited method call");
        SyntaxNode production = CSharpSyntaxTree
            .ParseText(
                File.ReadAllText(
                    Path.Combine(root.FullName, "Runtime", "Unity", "MessageAwareComponent.cs")
                ),
                ProviderOptions
            )
            .GetRoot();
        MethodDeclarationSyntax declaration = production
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method =>
                method.Modifiers.Any(SyntaxKind.PublicKeyword)
                && method.ParameterList.Parameters.Count == 2
                && method.ParameterList.Parameters[0].Type?.ToString() == "IMessageBus"
                && method.ParameterList.Parameters[1].Type?.ToString() == "MessageBusRebindMode"
            );
        Assert.That(
            calledMethod!.Identifier.ValueText,
            Is.EqualTo(declaration.Identifier.ValueText),
            page + ": use the actual inherited IMessageBus configuration API"
        );
    }

    /// <summary>Resolve bridge message attributes through the imports printed with the example.</summary>
    /// <remarks>2026-11-03: The complete Zenject bridge omitted the message attribute namespace.</remarks>
    [TestCase("DxUntargetedMessage", false)]
    [TestCase("DxUntargetedMessage", true)]
    [TestCase("DxAutoConstructor", false)]
    [TestCase("DxAutoConstructor", true)]
    public void ZenjectBridgeAttributesResolveFromDocumentedImports(
        string attributeName,
        bool qualifyName
    )
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Locate the Zenject integration guide.");
        string path = Path.Combine(root!.FullName, "docs", "integrations", "zenject.md");
        string[] examples = Regex
            .Matches(File.ReadAllText(path), @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .Select(match => match.Groups["code"].Value)
            .Where(code =>
                code.Contains("public sealed class DxToSignalBridge", StringComparison.Ordinal)
            )
            .ToArray();
        Assert.That(examples, Has.Length.EqualTo(1), path + ": complete bridge example");
        CompilationUnitSyntax published = CSharpSyntaxTree
            .ParseText(examples[0], ProviderOptions)
            .GetCompilationUnitRoot();
        AttributeSyntax printed = published
            .DescendantNodes()
            .OfType<AttributeSyntax>()
            .Single(attribute => attribute.Name.ToString() == attributeName);
        string expectedType = "DxMessaging.Core.Attributes." + attributeName + "Attribute";
        string imports = string.Join(
            "\n",
            published
                .Usings.Where(import =>
                    import.Name?.ToString().StartsWith("DxMessaging.", StringComparison.Ordinal)
                    == true
                )
                .Select(import => import.ToFullString())
        );
        string name = qualifyName ? "global::" + expectedType : printed.Name.ToString();
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            imports + "\n[" + name + "] public readonly partial struct DocumentedMessage { }",
            ProviderOptions,
            "DocumentedBridgeMessage.cs"
        );
        string references = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "BridgeAttributeImports",
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
            $"{attributeName}; qualified control={qualifyName}: \n"
                + string.Join("\n", emitted.Diagnostics)
        );
        AttributeSyntax compiled = tree.GetRoot()
            .DescendantNodes()
            .OfType<AttributeSyntax>()
            .Single();
        IMethodSymbol? constructor =
            compilation.GetSemanticModel(tree).GetSymbolInfo(compiled).Symbol as IMethodSymbol;
        Assert.That(constructor?.ContainingType.ToDisplayString(), Is.EqualTo(expectedType), path);
    }

    [TestCase(0, false)]
    [TestCase(0, true)]
    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(2, false)]
    [TestCase(2, true)]
    public void VContainerExamplesRegisterBuilderWithoutConsumerSymbol(
        int exampleIndex,
        bool explicitConsumerSymbol
    )
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Locate the VContainer integration guide.");
        string path = Path.Combine(root!.FullName, "docs", "integrations", "vcontainer.md");
        string[] examples = Regex
            .Matches(File.ReadAllText(path), @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .Select(match => match.Groups["code"].Value)
            .Where(code =>
                code.Contains("RegisterMessageRegistrationBuilder", StringComparison.Ordinal)
            )
            .ToArray();
        Assert.That(
            examples,
            Has.Length.EqualTo(3),
            path + ": complete builder-registration surface"
        );
        CSharpParseOptions options = ProviderOptions;
        if (explicitConsumerSymbol)
        {
            options = options.WithPreprocessorSymbols(
                "UNITY_2021_3_OR_NEWER",
                "VCONTAINER_PRESENT"
            );
        }
        SyntaxNode tree = CSharpSyntaxTree.ParseText(examples[exampleIndex], options).GetRoot();
        InvocationExpressionSyntax[] calls = tree.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(call =>
                call.Expression is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == "RegisterMessageRegistrationBuilder"
            )
            .ToArray();
        Assert.That(
            calls,
            Has.Length.EqualTo(1),
            $"{path} example {exampleIndex}; consumer symbol={explicitConsumerSymbol}: installed framework must retain builder registration"
        );
    }

    [TestCase("VContainerInitializer", false)]
    [TestCase("VContainerInitializer", true)]
    [TestCase("ScoreReporter", false)]
    [TestCase("ScoreReporter", true)]
    [TestCase("DiagnosticLogger", false)]
    [TestCase("DiagnosticLogger", true)]
    [TestCase("PlayerController", false)]
    [TestCase("PlayerController", true)]
    public void MessagingExamplesResolveFromDocumentedImports(string exampleName, bool qualifyName)
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Locate the repository containing the provider guide.");
        bool vcontainer = exampleName == "VContainerInitializer";
        string className = vcontainer ? "GameInitializer" : exampleName;
        bool busFirst = vcontainer || exampleName == "ScoreReporter";
        string page =
            vcontainer ? "vcontainer.md"
            : busFirst ? "emit-shorthands.md"
            : "message-bus-providers.md";
        string path = Path.Combine(
            root!.FullName,
            "docs",
            vcontainer ? "integrations" : "advanced",
            page
        );
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
                    .Any(type => type.Identifier.ValueText == className)
            )
            .ToArray();
        Assert.That(examples, Has.Length.EqualTo(1), path + ": " + exampleName);
        CompilationUnitSyntax published = examples[0];
        bool fieldProbe = busFirst || exampleName == "DiagnosticLogger";
        string expectedType = exampleName switch
        {
            "VContainerInitializer" => "DxMessaging.Core.MessageBus.IMessageBus",
            "ScoreReporter" => "DxMessaging.Core.MessageBus.IMessageBus",
            "DiagnosticLogger" => "DxMessaging.Unity.InitialGlobalMessageBusProvider",
            "PlayerController" => "DxMessaging.Core.MessageBus.MessageBusRebindMode",
            _ => throw new ArgumentOutOfRangeException(nameof(exampleName)),
        };
        string expression;
        if (fieldProbe)
        {
            string fieldName =
                vcontainer ? "_messageBus"
                : busFirst ? "messageBus"
                : "initialProvider";
            FieldDeclarationSyntax field = published
                .DescendantNodes()
                .OfType<FieldDeclarationSyntax>()
                .Single(candidate =>
                    candidate.Declaration.Variables.Any(variable =>
                        variable.Identifier.ValueText == fieldName
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
        string member = fieldProbe
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
        string references = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
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
        if (fieldProbe)
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
        string references = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
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
