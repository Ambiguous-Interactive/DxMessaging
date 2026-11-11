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
    private static readonly int[] NoOwnedCallbacks = { 0, 0 };
    private static readonly CSharpParseOptions ProviderOptions = new(
        LanguageVersion.CSharp9,
        preprocessorSymbols: new[] { "UNITY_2021_3_OR_NEWER" }
    );

    private static readonly Lazy<byte[]> PortableRuntimeImage = new(() =>
        RuntimeHashCompilationTests.CompilePortableRuntimeImage(false)
    );
    private static readonly Lazy<MetadataReference> PortableRuntime = new(() =>
        MetadataReference.CreateFromImage(PortableRuntimeImage.Value)
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

    /// <summary>Executes the published owner against callbacks during first and later registration.</summary>
    /// <remarks>2026-11-11: registration and group enrollment must finish before a reentrant owner disposal can release their cleanup.</remarks>
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    public void SubscriptionOwnershipGuideDefersDisposalUntilEnrollmentCompletes(
        int disposeAt,
        bool cleanupFailure
    )
    {
        string probe = """
            namespace SubscriptionOwnershipExample
            {
                using System;
                using DxMessaging.Core;
                using DxMessaging.Core.MessageBus;
                using DxMessaging.Core.Messages;
                using DxMessaging.Tests.Runtime;

                public static class ReentryProbe
                {
                    public static int[] Run(int disposeAt, bool cleanupFailure)
                    {
                        MessageBus inner = new MessageBus();
                        using LeakWatcher leaks = new LeakWatcher(inner);
                        EnrollmentBus bus = new EnrollmentBus(inner) { DisposeAt = disposeAt };
                        MessageHandler handler = new MessageHandler(new InstanceId(982), bus) { active = true };
                        using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
                        token.DiagnosticMode = false;
                        token.Enable();
                        using ParentSubscriptions owner = new ParentSubscriptions(token);
                        bus.OnAcceptance = () =>
                        {
                            bus.CleanupFailures = cleanupFailure ? 1 : 0;
                            owner.Dispose();
                        };
                        Exception failure = null;
                        InstanceId parent = new InstanceId(983);
                        try
                        {
                            owner.BindParent(parent);
                        }
                        catch (Exception exception)
                        {
                            failure = exception;
                        }
                        finally
                        {
                            bus.CleanupFailures = 0;
                        }
                        owner.Dispose();
                        if (cleanupFailure ? !ReferenceEquals(failure, bus.Failure) : failure != null)
                        {
                            throw new InvalidOperationException("Owner disposal must defer through enrollment and preserve the original retryable cleanup failure.", failure);
                        }
                        ParentChanged changed = new ParentChanged(9);
                        ParentHidden hidden = default;
                        bus.SourcedBroadcast(ref parent, ref changed);
                        bus.SourcedBroadcast(ref parent, ref hidden);
                        token.Disable();
                        token.Enable();
                        bus.SourcedBroadcast(ref parent, ref changed);
                        bus.SourcedBroadcast(ref parent, ref hidden);
                        return new[] { owner.Revision, owner.HiddenCount };
                    }

                    private sealed class EnrollmentBus : DelegatingMessageBus
                    {
                        internal readonly InvalidOperationException Failure = new InvalidOperationException("Rejected guide cleanup before forwarding.");
                        internal int DisposeAt;
                        internal int CleanupFailures;
                        internal Action OnAcceptance;
                        private int _registrations;

                        internal EnrollmentBus(IMessageBus inner) : base(inner) { }

                        public override MessageBusRegistration RegisterSourcedBroadcast<T>(InstanceId source, MessageHandler handler, int priority = 0)
                        {
                            if (++_registrations == DisposeAt)
                            {
                                OnAcceptance();
                            }
                            return base.RegisterSourcedBroadcast<T>(source, handler, priority);
                        }

                        public override void Deregister<T>(in MessageBusRegistration registration)
                        {
                            if (0 < CleanupFailures)
                            {
                                --CleanupFailures;
                                throw Failure;
                            }
                            base.Deregister<T>(in registration);
                        }
                    }
                }
            }
            """;
        AssertOwnershipGuideProbe(
            probe,
            new object[] { disposeAt, cleanupFailure },
            $"registration={disposeAt}, cleanupFailure={cleanupFailure}"
        );
    }

    /// <summary>Executes persistent owner-disposal callbacks while cleanup succeeds or needs a retry.</summary>
    /// <remarks>2026-11-11: Nested owner disposal must let the outer group finish before inspecting its retained Count.</remarks>
    [TestCase(false)]
    [TestCase(true)]
    public void SubscriptionOwnershipGuideAllowsReentrantDisposalDuringCleanup(bool cleanupFailure)
    {
        string probe = """
            namespace SubscriptionOwnershipExample
            {
                using System;
                using DxMessaging.Core;
                using DxMessaging.Core.MessageBus;
                using DxMessaging.Tests.Runtime;

                public static class ReentryProbe
                {
                    public static int[] Run(bool cleanupFailure)
                    {
                        MessageBus inner = new MessageBus();
                        using LeakWatcher leaks = new LeakWatcher(inner);
                        CleanupBus bus = new CleanupBus(inner);
                        MessageHandler handler = new MessageHandler(new InstanceId(984), bus) { active = true };
                        using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
                        token.DiagnosticMode = false;
                        token.Enable();
                        using ParentSubscriptions owner = new ParentSubscriptions(token);
                        InstanceId parent = new InstanceId(985);
                        owner.BindParent(parent);
                        bus.OnCleanup = owner.Dispose;
                        bus.CleanupFailures = cleanupFailure ? 1 : 0;
                        Exception first = null;
                        Exception retry = null;
                        try
                        {
                            try { owner.Dispose(); } catch (Exception exception) { first = exception; }
                            bus.CleanupFailures = 0;
                            try { owner.Dispose(); } catch (Exception exception) { retry = exception; }
                            if (cleanupFailure ? !ReferenceEquals(first, bus.Failure) : first != null)
                            {
                                throw new InvalidOperationException("Nested disposal changed the outer cleanup outcome; callbacks=" + bus.Callbacks + ", first=" + first?.GetType().Name + ", retry=" + retry?.GetType().Name, first);
                            }
                            if (retry != null || bus.Callbacks < 2)
                            {
                                throw new InvalidOperationException("The persistent callback must run for both registrations and retry successfully.", retry);
                            }
                            ParentChanged changed = new ParentChanged(9);
                            ParentHidden hidden = default;
                            bus.SourcedBroadcast(ref parent, ref changed);
                            bus.SourcedBroadcast(ref parent, ref hidden);
                            token.Disable();
                            token.Enable();
                            bus.SourcedBroadcast(ref parent, ref changed);
                            bus.SourcedBroadcast(ref parent, ref hidden);
                            return new[] { owner.Revision, owner.HiddenCount };
                        }
                        finally
                        {
                            bus.OnCleanup = null;
                            bus.CleanupFailures = 0;
                            owner.Dispose();
                        }
                    }

                    private sealed class CleanupBus : DelegatingMessageBus
                    {
                        internal readonly InvalidOperationException Failure = new InvalidOperationException("Rejected cleanup after nested owner disposal.");
                        internal Action OnCleanup;
                        internal int CleanupFailures;
                        internal int Callbacks;
                        internal CleanupBus(IMessageBus inner) : base(inner) { }
                        public override void Deregister<T>(in MessageBusRegistration registration)
                        {
                            if (OnCleanup != null) { ++Callbacks; OnCleanup(); }
                            if (0 < CleanupFailures) { --CleanupFailures; throw Failure; }
                            base.Deregister<T>(in registration);
                        }
                    }
                }
            }
            """;
        AssertOwnershipGuideProbe(
            probe,
            new object[] { cleanupFailure },
            $"persistent disposal callback, cleanupFailure={cleanupFailure}"
        );
    }

    /// <summary>Runs an unchanged published guide fence against the real runtime and cleanup helpers.</summary>
    private static void AssertOwnershipGuideProbe(string probe, object[] arguments, string label)
    {
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null, "Locate the actual ownership guide and bus helpers.");
        string path = Path.Combine(root!.FullName, "docs", "guides", "subscription-ownership.md");
        Match[] examples = Regex
            .Matches(File.ReadAllText(path), @"```csharp\s*\r?\n(?<code>[\s\S]*?)```")
            .ToArray();
        Assert.That(examples, Has.Length.EqualTo(1));

        string references = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        string helperRoot = Path.Combine(root.FullName, "Tests", "Runtime", "TestUtilities");
        CSharpParseOptions options = new(LanguageVersion.CSharp9);
        SyntaxTree[] trees =
        {
            CSharpSyntaxTree.ParseText(examples[0].Groups["code"].Value, options, path),
            CSharpSyntaxTree.ParseText(
                File.ReadAllText(Path.Combine(helperRoot, "DelegatingMessageBus.cs")),
                options,
                "ActualDelegatingMessageBus.cs"
            ),
            CSharpSyntaxTree.ParseText(
                File.ReadAllText(Path.Combine(helperRoot, "LeakWatcher.cs")),
                options,
                "ActualLeakWatcher.cs"
            ),
            CSharpSyntaxTree.ParseText(
                File.ReadAllText(Path.Combine(helperRoot, "EntryStorageInspection.cs")),
                options,
                "ActualEntryStorageInspection.cs"
            ),
            CSharpSyntaxTree.ParseText(
                File.ReadAllText(Path.Combine(helperRoot, "MessageBusInspection.cs")),
                options,
                "ActualMessageBusInspection.cs"
            ),
            CSharpSyntaxTree.ParseText(probe, options, "GuideReentryProbe.cs"),
        };
        CSharpCompilation compilation = CSharpCompilation.Create(
            "WallstopStudios.DxMessaging.Tests.Runtime",
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
            "Compile the actual guide and actual bus helpers:\n"
                + string.Join("\n", emitted.Diagnostics)
        );
        System.Runtime.Loader.AssemblyLoadContext context = new(
            "GuideOwnership_" + Guid.NewGuid().ToString("N"),
            isCollectible: true
        );
        try
        {
            using MemoryStream runtime = new(PortableRuntimeImage.Value);
            _ = context.LoadFromStream(runtime);
            image.Position = 0;
            System.Reflection.Assembly assembly = context.LoadFromStream(image);
            object? result = assembly
                .GetType("SubscriptionOwnershipExample.ReentryProbe", throwOnError: true)!
                .GetMethod("Run")!
                .Invoke(null, arguments);
            Assert.That(
                result,
                Is.EqualTo(NoOwnedCallbacks),
                label + ": disposed subscriptions must never deliver or replay."
            );
        }
        finally
        {
            context.Unload();
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

    /// <summary>Compiles documented message calls using only the imports shown with each sample.</summary>
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

    /// <summary>Compiles the documented fluent callback signature with its printed imports.</summary>
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
