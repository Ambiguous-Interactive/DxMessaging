using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.Docs.Tests;

internal static class DocsSnippetCompiler
{
    private static readonly CSharpParseOptions ParseOptions = new(
        languageVersion: LanguageVersion.Latest,
        documentationMode: DocumentationMode.Parse
    );

    private static readonly CSharpParseOptions DocumentationParseOptions = new(
        languageVersion: LanguageVersion.Latest,
        documentationMode: DocumentationMode.Diagnose
    );

    private static readonly ImmutableArray<MetadataReference> CoreReferences =
        BuildCoreReferences();

    private static readonly string[] DefaultDocNamespaces =
    {
        "System",
        "System.Collections",
        "System.Collections.Generic",
        "DxMessaging.Core",
        "DxMessaging.Core.Attributes",
        "DxMessaging.Core.Messages",
        "DxMessaging.Core.Extensions",
        "DxMessaging.Unity",
        "UnityEngine",
    };

    private static readonly CSharpCompilationOptions CompilationOptions =
        new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            generalDiagnosticOption: ReportDiagnostic.Error
        );

    internal static ImmutableArray<Diagnostic> CompileSnippet(string userSource)
    {
        SyntaxTree stubs = CSharpSyntaxTree.ParseText(SharedStubs, ParseOptions);
        SyntaxTree userTree = CSharpSyntaxTree.ParseText(
            NormalizeSnippet(userSource),
            ParseOptions
        );

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "DocsSnippetCompilation",
            syntaxTrees: new[] { stubs, userTree },
            references: CoreReferences,
            options: CompilationOptions
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: ParseOptions
        );
        driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation generatedCompilation,
            out ImmutableArray<Diagnostic> generatorDiagnostics
        );

        ImmutableArray<Diagnostic> compilerDiagnostics = generatedCompilation.GetDiagnostics();
        ImmutableArray<Diagnostic> strictGeneratorDiagnostics = generatorDiagnostics
            .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
            .Select(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Warning
                    ? PromoteToError(diagnostic)
                    : diagnostic
            )
            .ToImmutableArray();
        ImmutableArray<Diagnostic> documentationDiagnostics = CSharpSyntaxTree
            .ParseText(userSource, DocumentationParseOptions)
            .GetDiagnostics()
            .Where(IsDocumentationCommentDiagnostic)
            .Select(PromoteToError)
            .ToImmutableArray();
        return compilerDiagnostics
            .AddRange(strictGeneratorDiagnostics)
            .AddRange(documentationDiagnostics);
    }

    private static Diagnostic PromoteToError(Diagnostic diagnostic)
    {
        DiagnosticDescriptor descriptor = new(
            diagnostic.Id,
            diagnostic.Descriptor.Title,
            "{0}",
            diagnostic.Descriptor.Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: diagnostic.Descriptor.Description,
            helpLinkUri: diagnostic.Descriptor.HelpLinkUri
        );
        return Diagnostic.Create(
            descriptor,
            diagnostic.Location,
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
        );
    }

    private static bool IsDocumentationCommentDiagnostic(Diagnostic diagnostic)
    {
        if (diagnostic.Location.SourceTree == null)
        {
            return false;
        }

        return diagnostic
            .Location.SourceTree.GetRoot()
            .DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.HasStructure)
            .Select(trivia => trivia.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .Any(comment => comment.FullSpan.Contains(diagnostic.Location.SourceSpan));
    }

    private static bool HasAttribute(
        SyntaxList<AttributeListSyntax> attributeLists,
        string attributeName
    )
    {
        return attributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute =>
                attribute.Name.ToString() == attributeName
                || attribute.Name.ToString() == attributeName + "Attribute"
            );
    }

    private static string NormalizeSnippet(string userSource)
    {
        string? localUsingWrapper = TryWrapSnippetWithLocalUsing(userSource);
        if (localUsingWrapper != null)
        {
            return localUsingWrapper;
        }

        SyntaxTree scriptTree = CSharpSyntaxTree.ParseText(userSource, ParseOptions);
        CompilationUnitSyntax root = InitializeExternallyAssignedFields(
            scriptTree.GetCompilationUnitRoot()
        );
        string? constructorTypeName = TryGetConstructorFragmentTypeName(userSource);
        if (constructorTypeName != null)
        {
            System.Text.StringBuilder constructorWrapper = new();
            foreach (UsingDirectiveSyntax usingDirective in root.Usings)
            {
                constructorWrapper.AppendLine(usingDirective.ToFullString().Trim());
            }
            constructorWrapper
                .Append("public partial struct ")
                .Append(constructorTypeName)
                .AppendLine()
                .AppendLine("{")
                .Append(root.WithUsings(default).ToFullString())
                .AppendLine("}");
            return constructorWrapper.ToString();
        }

        bool isCompleteCompilationUnit = root.Members.All(member =>
            member
                is BaseNamespaceDeclarationSyntax
                    or BaseTypeDeclarationSyntax
                    or DelegateDeclarationSyntax
        );
        if (isCompleteCompilationUnit)
        {
            return AddBodiesToDeclarationOnlyMembers(root).ToFullString();
        }

        System.Text.StringBuilder normalized = new();
        foreach (UsingDirectiveSyntax usingDirective in root.Usings)
        {
            normalized.AppendLine(usingDirective.ToFullString().Trim());
        }
        string wrapperBase =
            userSource.Contains("base.", StringComparison.Ordinal)
            || userSource.Contains(" override ", StringComparison.Ordinal)
            || userSource.Contains("override ", StringComparison.Ordinal)
                ? "DxMessaging.Unity.MessageAwareComponent"
                : "UnityEngine.MonoBehaviour";
        normalized.Append("public partial class Script : ").AppendLine(wrapperBase);
        normalized.AppendLine("{");
        foreach (MemberDeclarationSyntax member in root.Members)
        {
            if (
                member is GlobalStatementSyntax
                {
                    Statement: LocalFunctionStatementSyntax localFunction
                }
            )
            {
                normalized.AppendLine(
                    AddBodyToDeclarationOnlyLocalFunction(localFunction).ToFullString()
                );
            }
            else if (member is not GlobalStatementSyntax)
            {
                normalized.AppendLine(member.ToFullString());
            }
        }
        normalized.AppendLine("private void __Run()");
        normalized.AppendLine("{");
        foreach (
            GlobalStatementSyntax statement in root
                .Members.OfType<GlobalStatementSyntax>()
                .Where(statement => statement.Statement is not LocalFunctionStatementSyntax)
        )
        {
            normalized.AppendLine(statement.Statement.ToFullString());
        }
        foreach (
            VariableDeclaratorSyntax eventVariable in root
                .Members.OfType<EventFieldDeclarationSyntax>()
                .SelectMany(eventField => eventField.Declaration.Variables)
        )
        {
            normalized.Append("_ = ").Append(eventVariable.Identifier.ValueText).AppendLine(";");
        }
        normalized.AppendLine(root.EndOfFileToken.LeadingTrivia.ToFullString());
        normalized.AppendLine("}");
        normalized.AppendLine("}");
        return normalized.ToString();
    }

    private static string? TryWrapSnippetWithLocalUsing(string userSource)
    {
        string[] lines = userSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int bodyStart = 0;
        while (bodyStart < lines.Length)
        {
            string line = lines[bodyStart];
            if (string.IsNullOrWhiteSpace(line) || IsUsingDirectiveLine(line))
            {
                bodyStart++;
                continue;
            }

            break;
        }

        if (bodyStart >= lines.Length)
        {
            return null;
        }

        System.Text.StringBuilder candidate = new();
        for (int index = 0; index < bodyStart; index++)
        {
            candidate.AppendLine(lines[index]);
        }
        candidate.AppendLine("public partial class Script : UnityEngine.MonoBehaviour");
        candidate.AppendLine("{");
        candidate.AppendLine("private void __Run()");
        candidate.AppendLine("{");
        for (int index = bodyStart; index < lines.Length; index++)
        {
            candidate.AppendLine(lines[index]);
        }
        candidate.AppendLine("}");
        candidate.AppendLine("}");

        SyntaxTree candidateTree = CSharpSyntaxTree.ParseText(candidate.ToString(), ParseOptions);
        if (
            candidateTree
                .GetDiagnostics()
                .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        )
        {
            return null;
        }

        bool hasLocalUsing = candidateTree
            .GetRoot()
            .DescendantNodes()
            .Any(node =>
                node is UsingStatementSyntax
                || node is LocalDeclarationStatementSyntax localDeclaration
                    && localDeclaration.UsingKeyword.RawKind != 0
            );
        return hasLocalUsing ? candidate.ToString() : null;
    }

    private static bool IsUsingDirectiveLine(string line)
    {
        SyntaxTree lineTree = CSharpSyntaxTree.ParseText(line + "\n", ParseOptions);
        CompilationUnitSyntax lineRoot = lineTree.GetCompilationUnitRoot();
        return lineRoot.Usings.Count == 1
            && lineRoot.Members.Count == 0
            && !lineTree
                .GetDiagnostics()
                .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static CompilationUnitSyntax InitializeExternallyAssignedFields(
        CompilationUnitSyntax root
    )
    {
        VariableDeclaratorSyntax[] variables = root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .Where(field =>
                HasAttribute(field.AttributeLists, "SerializeField")
                || HasAttribute(field.AttributeLists, "Inject")
            )
            .SelectMany(field => field.Declaration.Variables)
            .Where(variable => variable.Initializer == null)
            .ToArray();
        return root.ReplaceNodes(
            variables,
            (original, _) =>
                original.WithInitializer(
                    SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)
                    )
                )
        );
    }

    private static string? TryGetConstructorFragmentTypeName(string userSource)
    {
        foreach (string rawLine in userSource.Split('\n'))
        {
            string line = rawLine.Trim();
            if (
                line.Length == 0
                || line.StartsWith("//", StringComparison.Ordinal)
                || !line.StartsWith("public ", StringComparison.Ordinal)
            )
            {
                continue;
            }

            string declaration = line.Substring("public ".Length);
            int openParenthesis = declaration.IndexOf('(', StringComparison.Ordinal);
            if (openParenthesis <= 0)
            {
                return null;
            }

            string candidate = declaration.Substring(0, openParenthesis).Trim();
            return candidate.All(character => char.IsLetterOrDigit(character) || character == '_')
                ? candidate
                : null;
        }

        return null;
    }

    private static CompilationUnitSyntax AddBodiesToDeclarationOnlyMembers(
        CompilationUnitSyntax root
    )
    {
        ConstructorDeclarationSyntax[] constructors = root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Where(constructor =>
                constructor.Body == null
                && constructor.ExpressionBody == null
                && !constructor.Modifiers.Any(SyntaxKind.ExternKeyword)
            )
            .ToArray();
        CompilationUnitSyntax normalizedRoot = root.ReplaceNodes(
            constructors,
            (original, _) =>
                original
                    .WithSemicolonToken(default)
                    .WithExpressionBody(CreateNotImplementedExpressionBody())
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        );
        MethodDeclarationSyntax[] methods = normalizedRoot
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method =>
                method.Body == null
                && method.ExpressionBody == null
                && !method.Modifiers.Any(SyntaxKind.AbstractKeyword)
                && method.Parent is TypeDeclarationSyntax type
                && type is not InterfaceDeclarationSyntax
            )
            .ToArray();
        return normalizedRoot.ReplaceNodes(
            methods,
            (original, _) =>
                original
                    .WithSemicolonToken(default)
                    .WithExpressionBody(CreateNotImplementedExpressionBody())
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        );
    }

    private static LocalFunctionStatementSyntax AddBodyToDeclarationOnlyLocalFunction(
        LocalFunctionStatementSyntax localFunction
    )
    {
        if (localFunction.Body != null || localFunction.ExpressionBody != null)
        {
            return localFunction;
        }

        return localFunction
            .WithSemicolonToken(default)
            .WithExpressionBody(CreateNotImplementedExpressionBody())
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private static ArrowExpressionClauseSyntax CreateNotImplementedExpressionBody()
    {
        return SyntaxFactory.ArrowExpressionClause(
            SyntaxFactory.ThrowExpression(
                SyntaxFactory
                    .ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("System.NotImplementedException")
                    )
                    .WithArgumentList(SyntaxFactory.ArgumentList())
            )
        );
    }

    internal static ImmutableArray<Diagnostic> CompileDocSnippet(string userSource)
    {
        System.Text.StringBuilder source = new();
        foreach (string namespaceName in DefaultDocNamespaces)
        {
            string directive = $"using {namespaceName};";
            if (!userSource.Contains(directive, StringComparison.Ordinal))
            {
                source.AppendLine(directive);
            }
        }
        source.Append(userSource);
        return CompileSnippet(source.ToString());
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

    public sealed class MessageBus : IMessageBus
    {
        public IMessageBus.TrimResult Trim(bool force = false) => default;
        public void UntargetedBroadcast<TMessage>(ref TMessage typedMessage) where TMessage : IUntargetedMessage { }
        public void TargetedBroadcast<TMessage>(ref DxMessaging.Core.InstanceId target, ref TMessage typedMessage) where TMessage : ITargetedMessage { }
        public void SourcedBroadcast<TMessage>(ref DxMessaging.Core.InstanceId source, ref TMessage typedMessage) where TMessage : IBroadcastMessage { }
    }

}

namespace DxMessaging.Core.Extensions
{
    using DxMessaging.Core;

    public static class MessageExtensions
    {
        public static void Emit<TMessage>(this TMessage message) { }
        public static void Emit<TMessage>(this TMessage message, DxMessaging.Core.MessageBus.IMessageBus messageBus) { }

        public static void EmitAt<TMessage>(this TMessage message, InstanceId target)
            { }

        public static void EmitFrom<TMessage>(this TMessage message, InstanceId source)
            { }

        public static void EmitUntargeted<TMessage>(this TMessage message)
            { }

        public static void EmitTargeted<TMessage>(this TMessage message, InstanceId target)
            { }

        public static void EmitBroadcast<TMessage>(this TMessage message, InstanceId source)
            { }

        public static void EmitGameObjectTargeted<TMessage>(this TMessage message, UnityEngine.GameObject target)
            { }

        public static void EmitComponentTargeted<TMessage>(this TMessage message, UnityEngine.Component target)
            { }

        public static void EmitGameObjectBroadcast<TMessage>(this TMessage message, UnityEngine.GameObject source)
            { }

        public static void EmitComponentBroadcast<TMessage>(this TMessage message, UnityEngine.Component source)
            { }
    }
}

namespace DxMessaging.Core
{
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;

    public readonly struct InstanceId
    {
        public InstanceId(int id) { }
        public static implicit operator InstanceId(UnityEngine.GameObject gameObject) => default;
        public static implicit operator InstanceId(UnityEngine.Component component) => default;
    }

    public sealed class MessageHandler
    {
        // SYNC: Runtime/Core/MessageHandler.cs owns these parameter modifiers. Constraints are
        // deliberately omitted because attributed snippets are not augmented by DxMessageIdGenerator.
        public delegate void FastHandler<TMessage>(in TMessage message);

        public delegate void FastHandlerWithContext<TMessage>(
            in InstanceId context,
            in TMessage message
        );

        public MessageHandler(InstanceId id) { }
        public MessageHandler(InstanceId id, IMessageBus bus) { }
        public static IMessageBus MessageBus => new MessageBus.MessageBus();
        public bool active { get; set; }
    }

    public sealed class MessageRegistrationToken : System.IDisposable
    {
        public static MessageRegistrationToken Create(
            MessageHandler handler,
            IMessageBus bus = null
        ) => new();

        public void Enable() { }

        public void Dispose() { }

        public object RegisterGlobalAcceptAll(
            System.Action<IUntargetedMessage> acceptAllUntargeted,
            System.Action<InstanceId, ITargetedMessage> acceptAllTargeted,
            System.Action<InstanceId, IBroadcastMessage> acceptAllBroadcast
        ) => new object();

        public object RegisterGlobalAcceptAll(
            MessageHandler.FastHandler<IUntargetedMessage> acceptAllUntargeted,
            MessageHandler.FastHandlerWithContext<ITargetedMessage> acceptAllTargeted,
            MessageHandler.FastHandlerWithContext<IBroadcastMessage> acceptAllBroadcast
        ) => new object();

        public object RegisterUntargeted<TMessage>(
            System.Action<TMessage> untargetedHandler,
            int priority = 0
        ) => new object();

        public object RegisterUntargeted<TMessage>(
            MessageHandler.FastHandler<TMessage> untargetedHandler,
            int priority = 0
        ) => new object();

        public object RegisterGameObjectTargeted<TMessage>(
            UnityEngine.GameObject target,
            System.Action<TMessage> targetedHandler,
            int priority = 0
        ) => new object();

        public object RegisterGameObjectTargeted<TMessage>(
            UnityEngine.GameObject target,
            MessageHandler.FastHandler<TMessage> targetedHandler,
            int priority = 0
        ) => new object();

        public object RegisterGameObjectBroadcast<TMessage>(
            UnityEngine.GameObject source,
            System.Action<TMessage> broadcastHandler,
            int priority = 0
        ) => new object();

        public object RegisterGameObjectBroadcast<TMessage>(
            UnityEngine.GameObject source,
            MessageHandler.FastHandler<TMessage> broadcastHandler,
            int priority = 0
        ) => new object();
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
    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string name) { }
    }
    public class Component : Object { public GameObject gameObject => default; }
    public class MonoBehaviour : Component { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    public static class Debug
    {
        public static void Log(object message) { }
    }
}

namespace UnityEngine.Scripting
{
    using System;

    [AttributeUsage(AttributeTargets.All)]
    public sealed class PreserveAttribute : Attribute { }
}

namespace UnityEngine.Events
{
    public delegate void UnityAction();
}

namespace DxMessaging.Unity
{
    using UnityEngine;

    public abstract class MessageAwareComponent : MonoBehaviour
    {
        public virtual DxMessaging.Core.MessageRegistrationToken Token => default;

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
