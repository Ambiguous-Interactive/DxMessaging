using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.Docs.Tests;

/// <summary>Exercises the published quick start from trigger callback through receiver output.</summary>
[TestFixture("docs/getting-started/quick-start.md")]
[TestFixture("README.md")]
internal sealed class QuickStartDocumentationTests
{
    private static readonly HashSet<string> UnitySources = new(StringComparer.Ordinal)
    {
        "Core/Extensions/MessageExtensions.cs",
        "Core/InstanceId.cs",
        "Core/MessageBus/IMessageBusProvider.cs",
        "Core/MessageRegistrationToken.cs",
        "Unity/MessageAwareComponent.cs",
        "Unity/MessageBusProviderHandle.cs",
        "Unity/MessagingComponent.cs",
        "Unity/MessagingLifecycle.cs",
        "Unity/ScriptableMessageBusProvider.cs",
    };

    private readonly string _page;
    private (int Hit, int Miss, int Remaining, (string Message, bool HitContext)[] Logs) _result;

    public QuickStartDocumentationTests(string page)
    {
        _page = page;
    }

    [OneTimeSetUp]
    public void ExecutePublishedQuickStart()
    {
        string root = DocsSnippetCompilationTests.ResolveRepoRoot();
        string path = Path.Combine(root, _page);
        string[] examples = DocsSnippetCompilationTests
            .ExtractCodeBlocks(path, "csharp")
            .Where(code =>
                code.Contains("partial struct DamageRequested", StringComparison.Ordinal)
                || code.Contains(
                    "class DamageReceiver : MessageAwareComponent",
                    StringComparison.Ordinal
                )
                || code.Contains("class Hazard : MonoBehaviour", StringComparison.Ordinal)
            )
            .ToArray();
        Assert.That(examples, Has.Length.EqualTo(3), "Use the three complete published scripts.");

        CSharpParseOptions portable = new(LanguageVersion.CSharp9);
        CSharpParseOptions unity = portable.WithPreprocessorSymbols("UNITY_2021_3_OR_NEWER");
        string runtime = Path.Combine(root, "Runtime");
        IEnumerable<SyntaxTree> production = Directory
            .EnumerateFiles(runtime, "*.cs", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file =>
                CSharpSyntaxTree.ParseText(
                    File.ReadAllText(file),
                    UnitySources.Contains(Path.GetRelativePath(runtime, file).Replace('\\', '/'))
                        ? unity
                        : portable,
                    file
                )
            );
        IEnumerable<SyntaxTree> scripts = examples.Select(
            (example, index) =>
                CSharpSyntaxTree.ParseText(example, unity, $"{path} example {index + 1}")
        );
        string references = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        CSharpCompilation input = CSharpCompilation.Create(
            "PublishedQuickStart",
            production.Concat(scripts).Append(CSharpSyntaxTree.ParseText(Probe, unity, "Probe.cs")),
            references
                .Split(Path.PathSeparator)
                .Select(file => MetadataReference.CreateFromFile(file)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxMessageIdGenerator(), new DxAutoConstructorGenerator() },
            parseOptions: unity
        );
        driver.RunGeneratorsAndUpdateCompilation(
            input,
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(diagnostics, Is.Empty, string.Join(Environment.NewLine, diagnostics));

        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = generated.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            string.Join(Environment.NewLine, emitted.Diagnostics)
        );
        Assert.That(
            emitted.Diagnostics.Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Warning
                && diagnostic.Location.SourceTree?.FilePath.StartsWith(
                    path,
                    StringComparison.Ordinal
                ) == true
            ),
            Is.Empty,
            "The three published scripts compile without warnings."
        );
        AssemblyLoadContext context = new("PublishedQuickStart", isCollectible: true);
        try
        {
            image.Position = 0;
            Assembly assembly = context.LoadFromStream(image);
            _result = ((int, int, int, (string, bool)[]))
                assembly
                    .GetType("QuickStartProbe", throwOnError: true)!
                    .GetMethod("Run")!
                    .Invoke(null, null)!;
        }
        finally
        {
            context.Unload();
        }
    }

    [Test]
    public void DamageReachesOnlyTheColliderOwnerAndRegistrationsAreReleased()
    {
        Assert.That((_result.Hit, _result.Miss, _result.Remaining), Is.EqualTo((75, 100, 0)));
    }

    [Test]
    public void StruckReceiverPublishesOneContextualHealthResult()
    {
        Assert.That(_result.Logs, Is.EqualTo(new[] { ("Health: 75", true) }));
    }

    private const string Probe = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Reflection;
        using DxMessaging.Core;
        using DxMessaging.Core.MessageBus;
        using DxMessaging.Unity;
        using UnityEngine;

        // These doubles supply engine object identity, component lookup, and log capture.
        // The probe invokes Unity callbacks directly and uses the real DxMessaging lifecycle and bus.
        namespace UnityEngine
        {
            public class Object
            {
                private static int _nextId;
                private readonly int _id = ++_nextId;
                public string name => GetType().Name;
                public int GetInstanceID() => _id;
            }

            public class GameObject : Object
            {
                public MessagingComponent Messaging { get; }
                public GameObject() => Messaging = new MessagingComponent { gameObject = this };
            }

            public class Component : Object
            {
                public GameObject gameObject { get; set; }
                public T GetComponent<T>() where T : Component => (T)(Component)gameObject.Messaging;
            }

            public class MonoBehaviour : Component { public bool isActiveAndEnabled => true; }
            public sealed class Collider : Component { }
            public class ScriptableObject : Object { }
            public sealed class RequireComponent : Attribute { public RequireComponent(Type type) { } }
            public sealed class DisallowMultipleComponent : Attribute { }
            public sealed class SerializeField : Attribute { }
            public static class Mathf { public static int Max(int left, int right) => Math.Max(left, right); }

            public static class Debug
            {
                public static readonly List<(string Message, Object Context)> Logs = new();
                public static void Log(object message, Object context) => Logs.Add((message.ToString(), context));
                public static void LogWarning(object message, Object context) => Log(message, context);
            }
        }

        namespace UnityEngine.Serialization
        {
            public sealed class FormerlySerializedAsAttribute : Attribute
            {
                public FormerlySerializedAsAttribute(string name) { }
            }
        }

        public static class QuickStartProbe
        {
            public static (int, int, int, (string, bool)[]) Run()
            {
                MessageBus bus = new MessageBus();
                using MessageHandler.GlobalMessageBusScope scope = MessageHandler.OverrideGlobalMessageBus(bus);
                DamageReceiver hit = new DamageReceiver { gameObject = new GameObject() };
                DamageReceiver miss = new DamageReceiver { gameObject = new GameObject() };
                Hazard hazard = new Hazard { gameObject = new GameObject() };
                try
                {
                    StartReceiver(hit);
                    StartReceiver(miss);
                    Call(typeof(Hazard), hazard, "OnTriggerEnter", new Collider { gameObject = hit.gameObject });
                }
                finally
                {
                    StopReceiver(miss);
                    StopReceiver(hit);
                }

                return (hit.Health, miss.Health, bus.RegisteredTargeted,
                    Debug.Logs.Select(log => (log.Message, ReferenceEquals(log.Context, hit))).ToArray());
            }

            private static void StartReceiver(DamageReceiver receiver)
            {
                Call(typeof(MessagingComponent), receiver.gameObject.Messaging, "Awake");
                Call(typeof(MessageAwareComponent), receiver, "Awake");
                receiver.gameObject.Messaging.OnEnable();
                Call(typeof(MessageAwareComponent), receiver, "OnEnable");
            }

            private static void StopReceiver(DamageReceiver receiver)
            {
                Call(typeof(MessageAwareComponent), receiver, "OnDisable");
                receiver.gameObject.Messaging.OnDisable();
                Call(typeof(MessageAwareComponent), receiver, "OnDestroy");
                Call(typeof(MessagingComponent), receiver.gameObject.Messaging, "OnDestroy");
            }

            private static void Call(Type type, object instance, string name, params object[] args) =>
                type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, args);
        }
        """;
}
