using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace WallstopStudios.DxMessaging.Docs.Tests;

internal static class DocsSnippetCompiler
{
    private static readonly CSharpParseOptions ParseOptions = new(
        languageVersion: LanguageVersion.Latest,
        documentationMode: DocumentationMode.Diagnose
    );

    private static readonly ImmutableArray<MetadataReference> CoreReferences =
        BuildCoreReferences();

    internal static ImmutableArray<Diagnostic> CompileSnippet(string userSource)
    {
        SyntaxTree stubs = CSharpSyntaxTree.ParseText(SharedStubs, ParseOptions);
        SyntaxTree userTree = CSharpSyntaxTree.ParseText(
            userSource,
            ParseOptions.WithKind(SourceCodeKind.Script)
        );

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "DocsSnippetCompilation",
            syntaxTrees: new[] { stubs, userTree },
            references: CoreReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return compilation.GetDiagnostics();
    }

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

    public static class MessageExtensions
    {
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

        public static void EmitComponentTargeted<TMessage>(this ref TMessage message, UnityEngine.Component target)
            where TMessage : struct { }

        public static void EmitGameObjectBroadcast<TMessage>(this ref TMessage message, UnityEngine.GameObject source)
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
