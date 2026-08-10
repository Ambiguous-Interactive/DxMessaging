using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

internal sealed class ReflexCompatibilityCompilationTests
{
    private const string InjectedContainerField = "private Container _container;";
    private const string InitializedContainerField = "private Container _container = null!;";

    [TestCase(false, TestName = "ProductionReflexAdapterCompilesAgainstPre14Api")]
    [TestCase(true, TestName = "ProductionReflexAdapterCompilesAgainst14OrNewerApi")]
    public void ProductionReflexAdapterCompilesAgainstVersionSpecificApi(bool reflex14OrNewer)
    {
        string adapterPath = LocateAdapterSource();
        string adapterSource = File.ReadAllText(adapterPath);
        Assert.That(
            adapterSource,
            Does.Contain(InjectedContainerField),
            "The contract test must initialize the exact Reflex-injected field from production."
        );
        adapterSource = adapterSource.Replace(
            InjectedContainerField,
            InitializedContainerField,
            StringComparison.Ordinal
        );

        List<string> symbols = new() { "UNITY_2021_3_OR_NEWER", "REFLEX_PRESENT" };
        if (reflex14OrNewer)
        {
            symbols.Add("REFLEX_14_OR_NEWER");
        }

        CSharpParseOptions parseOptions = new(
            languageVersion: LanguageVersion.CSharp9,
            documentationMode: DocumentationMode.Parse,
            preprocessorSymbols: symbols
        );
        SyntaxTree adapterTree = CSharpSyntaxTree.ParseText(
            adapterSource,
            parseOptions,
            path: adapterPath
        );
        SyntaxTree contractTree = CSharpSyntaxTree.ParseText(
            ReflexApiContracts,
            parseOptions,
            path: "ReflexApiContracts.cs"
        );
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: reflex14OrNewer
                ? "Reflex14CompatibilityContract"
                : "ReflexPre14CompatibilityContract",
            syntaxTrees: new[] { contractTree, adapterTree },
            references: GeneratorTestUtilities.CompilationReferences,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                warningLevel: 9999,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );

        ImmutableArray<Diagnostic> diagnostics = compilation.GetDiagnostics();
        Assert.That(
            diagnostics,
            Is.Empty,
            $"The production adapter must compile warning-free against the selected Reflex API generation. Diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}"
        );
    }

    private static string LocateAdapterSource()
    {
        string? current =
            Path.GetDirectoryName(typeof(ReflexCompatibilityCompilationTests).Assembly.Location)
            ?? Directory.GetCurrentDirectory();
        for (int hop = 0; hop < 10 && current is not null; hop++)
        {
            string candidate = Path.Combine(
                current,
                "Runtime",
                "Unity",
                "Integrations",
                "Reflex",
                "ReflexRegistrationInstaller.cs"
            );
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "Runtime",
            "Unity",
            "Integrations",
            "Reflex",
            "ReflexRegistrationInstaller.cs"
        );
    }

    private const string ReflexApiContracts = """
namespace DxMessaging.Core.Pooling
{
    public interface IDxMessagingClock { }
}

namespace DxMessaging.Core.MessageBus
{
    using DxMessaging.Core.Pooling;

    public interface IMessageBus { }

    public interface IMessageBusProvider
    {
        IMessageBus Resolve();
    }

    public sealed class MessageBus : IMessageBus
    {
        public static MessageBus CreateForInternalUse(IDxMessagingClock clock)
        {
            return new MessageBus();
        }
    }

    public sealed class MessageRegistrationBuildOptions { }

    public sealed class MessageRegistrationLease { }

    public interface IMessageRegistrationBuilder
    {
        MessageRegistrationLease Build(MessageRegistrationBuildOptions options);
    }

    public sealed class MessageRegistrationBuilder
    {
        public MessageRegistrationBuilder(IMessageBusProvider provider) { }

        public MessageRegistrationLease Build(MessageRegistrationBuildOptions options)
        {
            return new MessageRegistrationLease();
        }
    }
}

namespace Reflex.Attributes
{
    using System;

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class InjectAttribute : Attribute { }
}

namespace Reflex.Core
{
    using System;
#if REFLEX_14_OR_NEWER
    using Reflex.Enums;
#endif

    public interface IInstaller
    {
        void InstallBindings(ContainerBuilder containerBuilder);
    }

    public sealed class Container
    {
        public T Resolve<T>()
        {
            return default(T);
        }
    }

    public sealed class ContainerBuilder
    {
#if REFLEX_14_OR_NEWER
        public ContainerBuilder RegisterType(
            Type concrete,
            Type[] contracts,
            Lifetime lifetime,
            Resolution resolution
        )
        {
            return this;
        }

        public ContainerBuilder RegisterFactory<T>(
            Func<Container, T> factory,
            Type[] contracts,
            Lifetime lifetime,
            Resolution resolution
        )
        {
            return this;
        }
#else
        public ContainerBuilder AddSingleton(Type concrete, params Type[] contracts)
        {
            return this;
        }

        public ContainerBuilder AddSingleton<T>(
            Func<Container, T> factory,
            params Type[] contracts
        )
        {
            return this;
        }
#endif
    }
}

#if REFLEX_14_OR_NEWER
namespace Reflex.Enums
{
    public enum Lifetime
    {
        Singleton,
    }

    public enum Resolution
    {
        Lazy,
    }
}
#endif
""";
}
