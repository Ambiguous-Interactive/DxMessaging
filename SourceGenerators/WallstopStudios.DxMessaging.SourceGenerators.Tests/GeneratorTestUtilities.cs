using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using WallstopStudios.DxMessaging.SourceGenerators;
using WallstopStudios.DxMessaging.SourceGenerators.Analyzers;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

internal static class GeneratorTestUtilities
{
    private static readonly CSharpParseOptions ParseOptions = new(
        languageVersion: LanguageVersion.Latest,
        documentationMode: DocumentationMode.Diagnose
    );

    private static readonly ImmutableArray<MetadataReference> CoreReferences =
        BuildCoreReferences();

    internal static GeneratorDriverRunResult RunDxAutoConstructor(string userSource)
    {
        SyntaxTree attributeTree = CSharpSyntaxTree.ParseText(SharedStubs, ParseOptions);
        SyntaxTree userTree = CSharpSyntaxTree.ParseText(userSource, ParseOptions);

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorTests",
            syntaxTrees: new[] { attributeTree, userTree },
            references: CoreReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        DxAutoConstructorGenerator generator = new();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        return driver.GetRunResult();
    }

    internal static GeneratorDriverRunResult RunDxMessageId(string userSource)
    {
        SyntaxTree stubs = CSharpSyntaxTree.ParseText(SharedStubs, ParseOptions);
        SyntaxTree userTree = CSharpSyntaxTree.ParseText(userSource, ParseOptions);

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorTests",
            syntaxTrees: new[] { stubs, userTree },
            references: CoreReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        DxMessageIdGenerator generator = new();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        return driver.GetRunResult();
    }

    internal static ImmutableArray<Diagnostic> CompileDxMessageIdGeneratedOutput(
        string userSource,
        params string[] preprocessorSymbols
    )
    {
        CSharpParseOptions parseOptions = ParseOptions.WithPreprocessorSymbols(
            preprocessorSymbols ?? Array.Empty<string>()
        );
        GeneratorDriverRunResult result = RunDxMessageId(userSource);

        List<SyntaxTree> syntaxTrees = new()
        {
            CSharpSyntaxTree.ParseText(SharedStubs, parseOptions, path: "DxMessagingStubs.cs"),
            CSharpSyntaxTree.ParseText(userSource, parseOptions, path: "UserSource.cs"),
        };
        syntaxTrees.AddRange(
            result
                .Results.SelectMany(static run => run.GeneratedSources)
                .Select(generated =>
                    CSharpSyntaxTree.ParseText(
                        generated.SourceText.ToString(),
                        parseOptions,
                        path: generated.HintName
                    )
                )
        );

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "GeneratedOutputCompilation",
            syntaxTrees: syntaxTrees,
            references: CoreReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return compilation.GetDiagnostics();
    }

    internal static ImmutableArray<Diagnostic> CompileSnippet(string userSource)
    {
        SyntaxTree stubs = CSharpSyntaxTree.ParseText(SharedStubs, ParseOptions);
        SyntaxTree userTree = CSharpSyntaxTree.ParseText(
            userSource,
            ParseOptions.WithKind(SourceCodeKind.Script)
        );

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "SnippetCompilation",
            syntaxTrees: new[] { stubs, userTree },
            references: CoreReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return compilation.GetDiagnostics();
    }

    /// <summary>
    /// Compiles <paramref name="userSource"/> as a snippet with the standard
    /// DxMessaging documentation usings prepended. Used by the doc-sample
    /// compilation tests where snippets are expected to use these namespaces
    /// implicitly.
    /// </summary>
    internal static ImmutableArray<Diagnostic> CompileDocSnippet(string userSource)
    {
        const string usings =
            @"using System;
using System.Collections.Generic;
using DxMessaging.Core;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Messages;
using DxMessaging.Unity;
using UnityEngine;
";
        return CompileSnippet(usings + userSource);
    }

    internal static ImmutableArray<Diagnostic> ParseSnippet(string userSource)
    {
        SyntaxTree userTree = CSharpSyntaxTree.ParseText(userSource, ParseOptions);
        return userTree.GetDiagnostics().ToImmutableArray();
    }

    internal static ImmutableArray<Diagnostic> RunBaseCallAnalyzer(
        string userSource,
        params (string path, string contents)[] additionalFiles
    )
    {
        return RunBaseCallAnalyzer(userSource, compilationOptions: null, additionalFiles);
    }

    /// <summary>
    /// Variant accepting a custom <see cref="CSharpCompilationOptions"/> so tests can pass
    /// <c>WithSpecificDiagnosticOptions(...)</c> to verify .editorconfig severity overrides
    /// (item H in the adversarial review).
    /// </summary>
    internal static ImmutableArray<Diagnostic> RunBaseCallAnalyzer(
        string userSource,
        CSharpCompilationOptions? compilationOptions,
        params (string path, string contents)[] additionalFiles
    )
    {
        SyntaxTree stubs = CSharpSyntaxTree.ParseText(SharedStubs, ParseOptions);
        SyntaxTree userTree = CSharpSyntaxTree.ParseText(userSource, ParseOptions);

        CSharpCompilationOptions effectiveOptions =
            compilationOptions ?? new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerTests",
            syntaxTrees: new[] { stubs, userTree },
            references: CoreReferences,
            options: effectiveOptions
        );

        // B6. Refuse to return when the underlying compilation has errors; otherwise tests can
        // silently bind nothing and pass. We exclude analyzer diagnostics here (we only want raw
        // compile errors) by calling Compilation.GetDiagnostics rather than the analyzer pipeline.
        ImmutableArray<Diagnostic> compileDiags = compilation.GetDiagnostics();
        ImmutableArray<Diagnostic> errors = compileDiags
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        if (!errors.IsEmpty)
        {
            throw new InvalidOperationException(
                "Test source did not compile cleanly:\n"
                    + string.Join("\n", errors.Select(d => d.ToString()))
            );
        }

        ImmutableArray<AdditionalText> texts = (additionalFiles ?? Array.Empty<(string, string)>())
            .Select(t => (AdditionalText)new InMemoryAdditionalText(t.path, t.contents))
            .ToImmutableArray();

        AnalyzerOptions analyzerOptions = new(texts);
        CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new MessageAwareComponentBaseCallAnalyzer()),
            analyzerOptions
        );

        Task<ImmutableArray<Diagnostic>> task =
            compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Lenient variant of <see cref="RunBaseCallAnalyzer(string, CSharpCompilationOptions?, ValueTuple{string, string}[])"/>
    /// that does NOT throw on compile errors. Used by <see cref="DocsSnippetCompilationTests"/>
    /// where many doc fragments are not standalone-compilable (e.g. they reference types from the
    /// runtime that the test compilation does not link). The base-call analyzer keys exclusively
    /// off override syntax + the <c>MessageAwareComponent</c> base-symbol lookup, so it still
    /// produces meaningful results when other parts of the snippet fail to bind.
    /// </summary>
    internal static ImmutableArray<Diagnostic> RunBaseCallAnalyzerLenient(string userSource)
    {
        SyntaxTree stubs = CSharpSyntaxTree.ParseText(SharedStubs, ParseOptions);
        SyntaxTree userTree = CSharpSyntaxTree.ParseText(userSource, ParseOptions);

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "DocsSnippetAnalyzer",
            syntaxTrees: new[] { stubs, userTree },
            references: CoreReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new MessageAwareComponentBaseCallAnalyzer())
        );

        Task<ImmutableArray<Diagnostic>> task =
            compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// S1. Builds an <see cref="AnalyzerOptions"/> wrapping the given in-memory additional files.
    /// Exposed to tests that need to exercise <see cref="WallstopStudios.DxMessaging.SourceGenerators.Analyzers.IgnoreListReader"/>
    /// directly (e.g. verifying the cache contract across repeat calls with different cancellation tokens).
    /// </summary>
    internal static AnalyzerOptions BuildAnalyzerOptions(
        params (string path, string contents)[] additionalFiles
    )
    {
        ImmutableArray<AdditionalText> texts = (additionalFiles ?? Array.Empty<(string, string)>())
            .Select(t => (AdditionalText)new InMemoryAdditionalText(t.path, t.contents))
            .ToImmutableArray();
        return new AnalyzerOptions(texts);
    }

    private static ImmutableArray<MetadataReference> BuildCoreReferences()
    {
        List<MetadataReference> references = new();

        void AddAssembly(Assembly assembly)
        {
            string location = assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                references.Add(MetadataReference.CreateFromFile(location));
            }
        }

        AddAssembly(typeof(object).Assembly);
        AddAssembly(typeof(Attribute).Assembly);
        AddAssembly(typeof(Enumerable).Assembly);
        AddAssembly(typeof(List<>).Assembly);

        return references.ToImmutableArray();
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _sourceText;

        public InMemoryAdditionalText(string path, string contents)
        {
            Path = path;
            _sourceText = SourceText.From(contents ?? string.Empty);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            return _sourceText;
        }
    }

    private const string SharedStubs = """
namespace DxMessaging.Core.Attributes
{
    using System;

    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
    public sealed class DxAutoConstructorAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class DxOptionalParameterAttribute : Attribute
    {
        public DxOptionalParameterAttribute() { }

        public DxOptionalParameterAttribute(object _) { }

        public string Expression { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class DxTargetedMessageAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class DxUntargetedMessageAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class DxBroadcastMessageAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class DxIgnoreMissingBaseCallAttribute : Attribute { }
}

namespace DxMessaging.Core
{
    using System;

    public interface IMessage
    {
        Type MessageType => GetType();
    }
}

namespace DxMessaging.Core.Messages
{
    using System;
    using DxMessaging.Core;

    public interface IUntargetedMessage : IMessage { }
    public interface ITargetedMessage : IMessage { }
    public interface IBroadcastMessage : IMessage { }

    public interface IUntargetedMessage<T> : IUntargetedMessage
        where T : IUntargetedMessage
    {
        Type IMessage.MessageType => typeof(T);
    }

    public interface ITargetedMessage<T> : ITargetedMessage
        where T : ITargetedMessage
    {
        Type IMessage.MessageType => typeof(T);
    }

    public interface IBroadcastMessage<T> : IBroadcastMessage
        where T : IBroadcastMessage
    {
        Type IMessage.MessageType => typeof(T);
    }
}

namespace DxMessaging.Core.MessageBus
{
    using DxMessaging.Core.Messages;

    public interface IMessageBus
    {
        TrimResult Trim(bool force = false);

        void UntargetedBroadcast<TMessage>(ref TMessage typedMessage)
            where TMessage : IUntargetedMessage;

        void TargetedBroadcast<TMessage>(
            ref DxMessaging.Core.InstanceId target,
            ref TMessage typedMessage
        )
            where TMessage : ITargetedMessage;

        void SourcedBroadcast<TMessage>(
            ref DxMessaging.Core.InstanceId source,
            ref TMessage typedMessage
        )
            where TMessage : IBroadcastMessage;

        public readonly struct TrimResult
        {
            public TrimResult(
                int typeSlotsEvicted,
                int targetSlotsEvicted,
                int pooledCollectionsEvicted,
                int liveTypeSlotsRemaining
            )
            {
                TypeSlotsEvicted = typeSlotsEvicted;
                TargetSlotsEvicted = targetSlotsEvicted;
                PooledCollectionsEvicted = pooledCollectionsEvicted;
                LiveTypeSlotsRemaining = liveTypeSlotsRemaining;
            }

            public int TypeSlotsEvicted { get; }
            public int TargetSlotsEvicted { get; }
            public int PooledCollectionsEvicted { get; }
            public int LiveTypeSlotsRemaining { get; }
        }
    }

    public sealed class MessageBus { }
}

namespace DxMessaging.Core.Extensions
{
    using DxMessaging.Core;
    using DxMessaging.Core.Messages;

    /// <summary>
    /// Minimal stubs of the Emit shorthands. The "this ref TMessage" signature
    /// is the load-bearing detail: with these stubs in place, the pattern
    /// "new X().Emit()" will not compile (CS1612 / CS1510 depending on
    /// context) -- which is the bug class the doc-snippet compilation tests
    /// exist to catch.
    /// </summary>
    public static class MessageExtensions
    {
        // The "this ref TMessage" signature is the load-bearing detail. The
        // "where struct" constraint is also kept (to mirror the real API),
        // but interface constraints are intentionally dropped: doc snippets
        // routinely omit explicit "I*Message" markers and rely on the
        // [Dx*Message] attributes to auto-generate them. The auto-generators
        // do not run inside this test compilation, so requiring the interface
        // would surface as spurious CS0315 / CS0453 failures rather than the
        // real "struct emit on a temporary" bug we want to catch (CS1612 /
        // CS1510).
        public static void Emit<TMessage>(this ref TMessage message)
            where TMessage : struct { }

        public static void EmitAt<TMessage>(this ref TMessage message, InstanceId target)
            where TMessage : struct { }

        public static void EmitFrom<TMessage>(this ref TMessage message, InstanceId source)
            where TMessage : struct { }

        public static void EmitUntargeted<TMessage>(this ref TMessage message)
            where TMessage : struct { }

        public static void EmitTargeted<TMessage>(this ref TMessage message, InstanceId target)
            where TMessage : struct { }

        public static void EmitBroadcast<TMessage>(this ref TMessage message, InstanceId source)
            where TMessage : struct { }

        public static void EmitGameObjectTargeted<TMessage>(this ref TMessage message, UnityEngine.GameObject target)
            where TMessage : struct { }

        public static void EmitComponentBroadcast<TMessage>(this ref TMessage message, UnityEngine.Component source)
            where TMessage : struct { }
    }
}

namespace DxMessaging.Core
{
    public readonly struct InstanceId
    {
        public InstanceId(int id) { }
    }
}

namespace UnityEngine
{
    using System;

    public enum RuntimeInitializeLoadType
    {
        AfterAssembliesLoaded,
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }

    public struct Color
    {
        public static readonly Color green = default;
    }

    public class Object { }
    public class GameObject : Object { }
    public class Component : Object { public GameObject gameObject => default; }
    public class MonoBehaviour : Component { }
}

namespace UnityEngine.Scripting
{
    using System;

    [AttributeUsage(AttributeTargets.All)]
    public sealed class PreserveAttribute : Attribute { }
}

namespace DxMessaging.Unity
{
    using UnityEngine;

    public abstract class MessageAwareComponent : MonoBehaviour
    {
        protected virtual bool RegisterForStringMessages => true;

        protected virtual void Awake() { }
        protected virtual void OnEnable() { }
        protected virtual void OnDisable() { }
        protected virtual void OnDestroy() { }
        protected virtual void RegisterMessageHandlers() { }
    }
}
""";
}
