namespace WallstopStudios.DxMessaging.SourceGenerators
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Text;

    [Generator]
    public sealed class DxMessageIdGenerator : ISourceGenerator
    {
        private static readonly DiagnosticDescriptor NonPartialContainerDiagnostic = new(
            id: "DXMSG003",
            title: "Containing type must be partial for nested generation",
            messageFormat: "Type '{0}' is nested inside non-partial container(s): {1}. Suggested fix: add the 'partial' keyword to the containing type declaration(s).",
            category: "DxMessaging",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor AddPartialSuggestionDiagnostic = new(
            id: "DXMSG004",
            title: "Add 'partial' keyword to containing type",
            messageFormat: "Add 'partial' to the declaration of '{0}' to enable generation for nested type '{1}'.",
            category: "DxMessaging",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true
        );

        // Base IMessage interface (used for implementation checks if needed, and property names)
        // *** Assumes the user has defined this interface in their code ***
        private const string BaseInterfaceFullName = "DxMessaging.Core.IMessage";

        // Message Type Attribute Full Names (Ensure these match your attributes)
        private const string BroadcastAttrFullName =
            "DxMessaging.Core.Attributes.DxBroadcastMessageAttribute";
        private const string TargetedAttrFullName =
            "DxMessaging.Core.Attributes.DxTargetedMessageAttribute";
        private const string UntargetedAttrFullName =
            "DxMessaging.Core.Attributes.DxUntargetedMessageAttribute";

        // Target Interface Full Names (Ensure these match your specific message interfaces)
        private const string BroadcastInterfaceFullName =
            "DxMessaging.Core.Messages.IBroadcastMessage";
        private const string TargetedInterfaceFullName =
            "DxMessaging.Core.Messages.ITargetedMessage";
        private const string UntargetedInterfaceFullName =
            "DxMessaging.Core.Messages.IUntargetedMessage";

        // Diagnostics
        private static readonly DiagnosticDescriptor MultipleAttributesError = new(
            id: "DXMSG002",
            title: "Multiple Message Attributes",
            messageFormat: "Type '{0}' cannot have more than one Dx message attribute ([DxBroadcastMessage], [DxTargetedMessage], [DxUntargetedMessage]).",
            category: "DxMessaging",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // Information needed during the generation phase for a valid message type
        private record struct MessageToGenerateInfo(
            INamedTypeSymbol TypeSymbol,
            TypeDeclarationSyntax DeclarationSyntax,
            string TargetInterfaceFullName,
            bool RegistersUntargetedAotBridge,
            bool RegistersTargetedAotBridge,
            bool RegistersBroadcastAotBridge,
            bool HasConflictingMessageAttributes
        );

        private record struct AotRegistrarInfo(
            INamedTypeSymbol TypeSymbol,
            bool RegistersUntargetedAotBridge,
            bool RegistersTargetedAotBridge,
            bool RegistersBroadcastAotBridge,
            bool HasMessageAttribute
        );

        private record struct AotRegistrarSourceInfo(
            AotRegistrarInfo Info,
            string TypeName,
            string Suffix
        );

        private readonly struct SemanticTargetInfo
        {
            public SemanticTargetInfo(
                MessageToGenerateInfo? messageToGenerate,
                AotRegistrarInfo? aotRegistrar
            )
            {
                MessageToGenerate = messageToGenerate;
                AotRegistrar = aotRegistrar;
            }

            public MessageToGenerateInfo? MessageToGenerate { get; }
            public AotRegistrarInfo? AotRegistrar { get; }
        }

        private enum AotBridgeKind
        {
            Untargeted,
            Targeted,
            Broadcast,
        }

        /// <summary>
        /// Configures syntax collection for deterministic message identifier generation.
        /// </summary>
        /// <param name="context">Initialization context provided by Roslyn.</param>
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(static () => new TypeSyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (context.SyntaxReceiver is not TypeSyntaxReceiver receiver)
            {
                return;
            }

            List<MessageToGenerateInfo> typesToGenerate = new();
            List<AotRegistrarInfo> aotTypes = new();
            foreach (TypeDeclarationSyntax typeDeclarationSyntax in receiver.Candidates)
            {
                SemanticTargetInfo? targetInfo = GetSemanticTarget(
                    typeDeclarationSyntax,
                    context.Compilation,
                    context.CancellationToken
                );
                if (!targetInfo.HasValue)
                {
                    continue;
                }

                if (targetInfo.Value.MessageToGenerate.HasValue)
                {
                    typesToGenerate.Add(targetInfo.Value.MessageToGenerate.Value);
                }
                if (targetInfo.Value.AotRegistrar.HasValue)
                {
                    aotTypes.Add(targetInfo.Value.AotRegistrar.Value);
                }
            }

            Execute(typesToGenerate.ToImmutableArray(), aotTypes.ToImmutableArray(), context);
        }

        private static bool IsSyntaxTargetForGeneration(SyntaxNode node)
        {
            if (node is not TypeDeclarationSyntax typeDecl || !IsSupportedTypeDeclaration(typeDecl))
            {
                return false;
            }

            return typeDecl.AttributeLists.Count > 0 || HasRelevantMessageBaseType(typeDecl);
        }

        private static bool IsSupportedTypeDeclaration(TypeDeclarationSyntax typeDeclarationSyntax)
        {
            return typeDeclarationSyntax.IsKind(SyntaxKind.ClassDeclaration)
                || typeDeclarationSyntax.IsKind(SyntaxKind.StructDeclaration)
                || typeDeclarationSyntax.IsKind(SyntaxKind.RecordDeclaration)
                || string.Equals(
                    typeDeclarationSyntax.Kind().ToString(),
                    "RecordStructDeclaration",
                    StringComparison.Ordinal
                );
        }

        private static bool HasRelevantMessageBaseType(TypeDeclarationSyntax typeDeclarationSyntax)
        {
            if (typeDeclarationSyntax.BaseList is null)
            {
                return false;
            }

            foreach (BaseTypeSyntax baseType in typeDeclarationSyntax.BaseList.Types)
            {
                string baseTypeName = baseType.Type.ToString();
                if (
                    baseTypeName.IndexOf("IUntargetedMessage", StringComparison.Ordinal) >= 0
                    || baseTypeName.IndexOf("ITargetedMessage", StringComparison.Ordinal) >= 0
                    || baseTypeName.IndexOf("IBroadcastMessage", StringComparison.Ordinal) >= 0
                )
                {
                    return true;
                }
            }

            return false;
        }

        private static SemanticTargetInfo? GetSemanticTarget(
            TypeDeclarationSyntax typeDeclarationSyntax,
            Compilation compilation,
            CancellationToken cancellationToken
        )
        {
            SemanticModel semanticModel = compilation.GetSemanticModel(
                typeDeclarationSyntax.SyntaxTree
            );
            if (
                semanticModel.GetDeclaredSymbol(typeDeclarationSyntax, cancellationToken)
                is not INamedTypeSymbol typeSymbol
            )
            {
                return null;
            }

            // Ensure it's not abstract or static (if class)
            if (
                typeSymbol.IsAbstract
                || (typeSymbol.IsStatic && typeSymbol.TypeKind == TypeKind.Class)
            )
            {
                return null; // Cannot be a concrete message type
            }

            string foundTargetInterface = null;
            bool multipleAttributes = false;
            bool hasUntargetedAttribute = false;
            bool hasTargetedAttribute = false;
            bool hasBroadcastAttribute = false;

            // Check attributes to find the specific message type (Broadcast, Targeted, etc.)
            foreach (AttributeData attributeData in typeSymbol.GetAttributes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string currentAttributeFullName = attributeData.AttributeClass?.ToDisplayString();
                string targetInterfaceForThisAttribute = null;

                switch (currentAttributeFullName)
                {
                    case BroadcastAttrFullName:
                        targetInterfaceForThisAttribute = BroadcastInterfaceFullName;
                        hasBroadcastAttribute = true;
                        break;
                    case TargetedAttrFullName:
                        targetInterfaceForThisAttribute = TargetedInterfaceFullName;
                        hasTargetedAttribute = true;
                        break;
                    case UntargetedAttrFullName:
                        targetInterfaceForThisAttribute = UntargetedInterfaceFullName;
                        hasUntargetedAttribute = true;
                        break;
                }

                if (targetInterfaceForThisAttribute != null)
                {
                    if (
                        foundTargetInterface != null
                        && foundTargetInterface != targetInterfaceForThisAttribute
                    )
                    {
                        multipleAttributes = true;
                        break;
                    }
                    foundTargetInterface = targetInterfaceForThisAttribute;
                }
            }

            if (multipleAttributes)
            {
                foundTargetInterface = null;
            }

            if (foundTargetInterface == null && !multipleAttributes)
            {
                bool registersUntargeted = ImplementsInterface(
                    typeSymbol,
                    UntargetedInterfaceFullName
                );
                bool registersTargeted = ImplementsInterface(typeSymbol, TargetedInterfaceFullName);
                bool registersBroadcast = ImplementsInterface(
                    typeSymbol,
                    BroadcastInterfaceFullName
                );

                if (!registersUntargeted && !registersTargeted && !registersBroadcast)
                {
                    return null;
                }

                AotRegistrarInfo? manualRegistrar = ContainsTypeParameters(typeSymbol)
                    ? null
                    : new AotRegistrarInfo(
                        typeSymbol,
                        registersUntargeted,
                        registersTargeted,
                        registersBroadcast,
                        HasMessageAttribute: false
                    );
                return new SemanticTargetInfo(null, manualRegistrar);
            }

            bool implementsUntargeted = ImplementsInterface(
                typeSymbol,
                UntargetedInterfaceFullName
            );
            bool implementsTargeted = ImplementsInterface(typeSymbol, TargetedInterfaceFullName);
            bool implementsBroadcast = ImplementsInterface(typeSymbol, BroadcastInterfaceFullName);

            MessageToGenerateInfo messageToGenerate = new MessageToGenerateInfo(
                typeSymbol,
                typeDeclarationSyntax,
                foundTargetInterface,
                hasUntargetedAttribute || implementsUntargeted,
                hasTargetedAttribute || implementsTargeted,
                hasBroadcastAttribute || implementsBroadcast,
                multipleAttributes
            );

            AotRegistrarInfo? attributedRegistrar =
                multipleAttributes || ContainsTypeParameters(typeSymbol)
                    ? null
                    : new AotRegistrarInfo(
                        typeSymbol,
                        hasUntargetedAttribute || implementsUntargeted,
                        hasTargetedAttribute || implementsTargeted,
                        hasBroadcastAttribute || implementsBroadcast,
                        HasMessageAttribute: true
                    );

            return new SemanticTargetInfo(messageToGenerate, attributedRegistrar);
        }

        private static bool ImplementsInterface(
            INamedTypeSymbol typeSymbol,
            string interfaceFullName
        )
        {
            foreach (INamedTypeSymbol interfaceSymbol in typeSymbol.AllInterfaces)
            {
                if (
                    string.Equals(
                        interfaceSymbol.ToDisplayString(),
                        interfaceFullName,
                        StringComparison.Ordinal
                    )
                )
                {
                    return true;
                }
            }

            return false;
        }

        private static void Execute(
            ImmutableArray<MessageToGenerateInfo> typesToGenerate,
            ImmutableArray<AotRegistrarInfo> aotTypes,
            GeneratorExecutionContext context
        )
        {
            // --- Step 1: Filter out types with multiple attributes applied ---
            Dictionary<ISymbol, MessageToGenerateInfo> uniqueTypes = new Dictionary<
                ISymbol,
                MessageToGenerateInfo
            >(SymbolEqualityComparer.Default);
            HashSet<ISymbol> conflictingTypes = new HashSet<ISymbol>(
                SymbolEqualityComparer.Default
            );

            foreach (MessageToGenerateInfo typeInfo in typesToGenerate)
            {
                if (typeInfo.HasConflictingMessageAttributes)
                {
                    if (conflictingTypes.Add(typeInfo.TypeSymbol))
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                MultipleAttributesError,
                                typeInfo.DeclarationSyntax.Identifier.GetLocation(),
                                typeInfo.TypeSymbol.ToDisplayString()
                            )
                        );
                    }

                    continue;
                }

                if (conflictingTypes.Contains(typeInfo.TypeSymbol))
                {
                    continue;
                }

                if (typeInfo.TargetInterfaceFullName is null)
                {
                    continue;
                }

                if (
                    uniqueTypes.TryGetValue(
                        typeInfo.TypeSymbol,
                        out MessageToGenerateInfo existingInfo
                    )
                )
                {
                    if (
                        !string.Equals(
                            existingInfo.TargetInterfaceFullName,
                            typeInfo.TargetInterfaceFullName,
                            StringComparison.Ordinal
                        ) && conflictingTypes.Add(typeInfo.TypeSymbol)
                    )
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                MultipleAttributesError,
                                typeInfo.DeclarationSyntax.Identifier.GetLocation(),
                                typeInfo.TypeSymbol.ToDisplayString()
                            )
                        );
                        uniqueTypes.Remove(typeInfo.TypeSymbol);
                    }
                }
                else
                {
                    uniqueTypes[typeInfo.TypeSymbol] = typeInfo;
                }
            }

            List<MessageToGenerateInfo> validSingleAttrTypes = new List<MessageToGenerateInfo>();
            foreach (KeyValuePair<ISymbol, MessageToGenerateInfo> entry in uniqueTypes)
            {
                if (conflictingTypes.Contains(entry.Key))
                {
                    continue;
                }

                validSingleAttrTypes.Add(entry.Value);
            }

            // --- Step 2: Generate sources for each valid message type ---
            HashSet<ISymbol> generatedAttributedTypes = new HashSet<ISymbol>(
                SymbolEqualityComparer.Default
            );
            foreach (MessageToGenerateInfo messageInfo in validSingleAttrTypes)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                string targetInterfaceFullName = messageInfo.TargetInterfaceFullName;
                if (targetInterfaceFullName is null)
                {
                    continue;
                }

                // If nested, ensure all containers are declared partial; otherwise report diagnostic and skip
                if (messageInfo.TypeSymbol.ContainingType is not null)
                {
                    List<INamedTypeSymbol> nonPartial = GetNonPartialContainers(
                        messageInfo.TypeSymbol
                    );
                    if (nonPartial.Count > 0)
                    {
                        string containersList = string.Join(
                            ", ",
                            nonPartial.Select(static s =>
                                s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                            )
                        );
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                NonPartialContainerDiagnostic,
                                messageInfo.DeclarationSyntax.Identifier.GetLocation(),
                                messageInfo.TypeSymbol.ToDisplayString(
                                    SymbolDisplayFormat.MinimallyQualifiedFormat
                                ),
                                containersList
                            )
                        );
                        foreach (INamedTypeSymbol container in nonPartial)
                        {
                            SyntaxReference sr =
                                container.DeclaringSyntaxReferences.FirstOrDefault();
                            if (sr != null && sr.GetSyntax() is TypeDeclarationSyntax tds)
                            {
                                context.ReportDiagnostic(
                                    Diagnostic.Create(
                                        AddPartialSuggestionDiagnostic,
                                        tds.Identifier.GetLocation(),
                                        container.ToDisplayString(
                                            SymbolDisplayFormat.MinimallyQualifiedFormat
                                        ),
                                        messageInfo.TypeSymbol.ToDisplayString(
                                            SymbolDisplayFormat.MinimallyQualifiedFormat
                                        )
                                    )
                                );
                            }
                        }
                        continue;
                    }
                }

                // Generate the partial IMessage implementation source
                string implSource = GenerateImplementationSource(messageInfo);
                string implHintName =
                    $"{messageInfo.TypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.IMessage.g.cs"
                        .Replace("global::", "")
                        .Replace("<", "_")
                        .Replace(">", "_")
                        .Replace(",", "_"); // Clean hint name

                context.AddSource(implHintName, SourceText.From(implSource, Encoding.UTF8));
                generatedAttributedTypes.Add(messageInfo.TypeSymbol);
            }

            GenerateTopLevelAotRegistrar(aotTypes, generatedAttributedTypes, context);
        }

        private sealed class TypeSyntaxReceiver : ISyntaxReceiver
        {
            public List<TypeDeclarationSyntax> Candidates { get; } =
                new List<TypeDeclarationSyntax>();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (IsSyntaxTargetForGeneration(syntaxNode))
                {
                    Candidates.Add((TypeDeclarationSyntax)syntaxNode);
                }
            }
        }

        // Generates the partial class/struct implementing IMessage
        private static string GenerateImplementationSource(MessageToGenerateInfo messageInfo)
        {
            string targetInterfaceFullName = messageInfo.TargetInterfaceFullName;
            INamedTypeSymbol typeSymbol = messageInfo.TypeSymbol;
            string namespaceName = typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString();
            string namespaceBlockOpen = string.IsNullOrEmpty(namespaceName)
                ? string.Empty
                : $"namespace {namespaceName}\n{{";
            string namespaceBlockClose = string.IsNullOrEmpty(namespaceName) ? string.Empty : "}";
            const string Indent = "    ";

            // Build container wrappers so partial can merge nested types correctly
            var containers = new Stack<INamedTypeSymbol>();
            INamedTypeSymbol current = typeSymbol.ContainingType;
            while (current is not null)
            {
                containers.Push(current);
                current = current.ContainingType;
            }

            var containersOpen = new StringBuilder();
            var containersClose = new StringBuilder();
            string currentIndent = Indent;
            foreach (INamedTypeSymbol container in containers)
            {
                string containerAccessibility = container.DeclaredAccessibility switch
                {
                    Accessibility.Public => "public",
                    Accessibility.Protected => "protected",
                    Accessibility.Private => "private",
                    Accessibility.Internal => "internal",
                    Accessibility.ProtectedOrInternal => "protected internal",
                    Accessibility.ProtectedAndInternal => "private protected",
                    _ => "internal",
                };

                bool containerIsRecord = IsRecordDeclaration(container);
                string containerKind = container.TypeKind switch
                {
                    TypeKind.Class => containerIsRecord ? "record class" : "class",
                    TypeKind.Struct => containerIsRecord ? "record struct" : "struct",
                    _ => "class",
                };

                string containerTypeParams =
                    container.TypeParameters.Length > 0
                        ? "<"
                            + string.Join(", ", container.TypeParameters.Select(static p => p.Name))
                            + ">"
                        : string.Empty;

                // Avoid repeating sealed/abstract/static/readonly/ref to prevent conflicting semantics
                containersOpen.AppendLine(
                    $"{currentIndent}{containerAccessibility} partial {containerKind} {container.Name}{containerTypeParams}"
                );
                containersOpen.Append(currentIndent).AppendLine("{");
                currentIndent += Indent;
            }

            string innerIndent = currentIndent;

            // Use unqualified nested identifier for declaration (containers already opened)
            string typeGenericParams =
                typeSymbol.TypeParameters.Length > 0
                    ? "<"
                        + string.Join(", ", typeSymbol.TypeParameters.Select(static p => p.Name))
                        + ">"
                    : string.Empty;
            string typeNameWithGenerics = typeSymbol.Name + typeGenericParams;
            string fullyQualifiedName = typeSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            );

            bool typeIsRecord = IsRecordDeclaration(typeSymbol);
            string typeKind = typeSymbol.TypeKind switch
            {
                TypeKind.Class => typeIsRecord ? "record class" : "class",
                TypeKind.Struct => typeIsRecord ? "record struct" : "struct",
                _ => throw new InvalidOperationException("Unsupported type kind"),
            };

            string accessibility = typeSymbol.DeclaredAccessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Protected => "protected",
                Accessibility.Private => "private",
                Accessibility.Internal => "internal",
                Accessibility.ProtectedOrInternal => "protected internal",
                Accessibility.ProtectedAndInternal => "private protected",
                _ => "internal",
            };

            string interfaceDeclaration = $", global::{targetInterfaceFullName}";
            string aotBridgeSource = ContainsTypeParameters(typeSymbol)
                ? string.Empty
                : GenerateTypeScopedAotBridgeSource(
                    messageInfo,
                    fullyQualifiedName,
                    innerIndent + Indent
                );

            // Close containers string
            for (int i = 0; i < containers.Count; i++)
            {
                currentIndent = currentIndent.Substring(
                    0,
                    Math.Max(0, currentIndent.Length - Indent.Length)
                );
                containersClose.Append(currentIndent).AppendLine("}");
            }

            return string.Join(
                "\r\n",
                new[]
                {
                    "// <auto-generated by DxMessageIdGenerator/>",
                    "#pragma warning disable",
                    "#nullable enable annotations",
                    string.Empty,
                    namespaceBlockOpen,
                    $"{containersOpen}{innerIndent}// Partial implementation for {typeNameWithGenerics} to implement {BaseInterfaceFullName}",
                    $"{innerIndent}{accessibility} partial {typeKind} {typeNameWithGenerics} : global::{BaseInterfaceFullName} {interfaceDeclaration}",
                    $"{innerIndent}{{",
                    $"{innerIndent}    /// <inheritdoc/>",
                    $"{innerIndent}    public global::System.Type MessageType => typeof({fullyQualifiedName});",
                    $"{aotBridgeSource}",
                    $"{innerIndent}}}",
                    $"{containersClose}",
                    namespaceBlockClose,
                    string.Empty,
                }
            );
        }

        private static string GenerateTypeScopedAotBridgeSource(
            MessageToGenerateInfo messageInfo,
            string fullyQualifiedName,
            string indent
        )
        {
            var builder = new StringBuilder();
            builder.AppendLine();
            builder.Append(indent).AppendLine("#if ENABLE_IL2CPP && UNITY_2021_3_OR_NEWER");
            builder.Append(indent).AppendLine("[global::UnityEngine.Scripting.Preserve]");
            builder
                .Append(indent)
                .AppendLine(
                    "[global::UnityEngine.RuntimeInitializeOnLoadMethod(global::UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]"
                );
            builder
                .Append(indent)
                .AppendLine("private static void __DxMessagingRegisterAotBridges()");
            builder.Append(indent).AppendLine("{");
            if (messageInfo.RegistersUntargetedAotBridge)
            {
                AppendAotRegisterCall(
                    builder,
                    indent + "    ",
                    "RegisterAotUntargetedBridge",
                    AotBridgeKind.Untargeted,
                    fullyQualifiedName,
                    "__DxMessagingAotUntargetedBridge"
                );
            }
            if (messageInfo.RegistersTargetedAotBridge)
            {
                AppendAotRegisterCall(
                    builder,
                    indent + "    ",
                    "RegisterAotTargetedBridge",
                    AotBridgeKind.Targeted,
                    fullyQualifiedName,
                    "__DxMessagingAotTargetedBridge"
                );
            }
            if (messageInfo.RegistersBroadcastAotBridge)
            {
                AppendAotRegisterCall(
                    builder,
                    indent + "    ",
                    "RegisterAotSourcedBridge",
                    AotBridgeKind.Broadcast,
                    fullyQualifiedName,
                    "__DxMessagingAotSourcedBridge"
                );
            }
            builder.Append(indent).AppendLine("}");

            AppendAotReflectionHelper(builder, indent);
            if (messageInfo.RegistersUntargetedAotBridge)
            {
                AppendUntargetedBridgeMethod(
                    builder,
                    indent,
                    "__DxMessagingAotUntargetedBridge",
                    fullyQualifiedName
                );
            }
            if (messageInfo.RegistersTargetedAotBridge)
            {
                AppendTargetedBridgeMethod(
                    builder,
                    indent,
                    "__DxMessagingAotTargetedBridge",
                    fullyQualifiedName
                );
            }
            if (messageInfo.RegistersBroadcastAotBridge)
            {
                AppendBroadcastBridgeMethod(
                    builder,
                    indent,
                    "__DxMessagingAotSourcedBridge",
                    fullyQualifiedName
                );
            }

            builder.Append(indent).Append("#endif");
            return builder.ToString();
        }

        private static void GenerateTopLevelAotRegistrar(
            ImmutableArray<AotRegistrarInfo> aotTypes,
            HashSet<ISymbol> generatedAttributedTypes,
            GeneratorExecutionContext context
        )
        {
            if (aotTypes.IsDefaultOrEmpty)
            {
                return;
            }

            List<AotRegistrarInfo> registrarTypes = aotTypes
                .Where(info =>
                    !info.HasMessageAttribute
                    && !generatedAttributedTypes.Contains(info.TypeSymbol)
                    && IsAccessibleFromTopLevelRegistrar(info.TypeSymbol)
                )
                .GroupBy(info => info.TypeSymbol, SymbolEqualityComparer.Default)
                .Select(group =>
                {
                    AotRegistrarInfo merged = default;
                    bool hasValue = false;
                    foreach (AotRegistrarInfo info in group)
                    {
                        if (!hasValue)
                        {
                            merged = info;
                            hasValue = true;
                            continue;
                        }

                        merged = new AotRegistrarInfo(
                            info.TypeSymbol,
                            merged.RegistersUntargetedAotBridge
                                || info.RegistersUntargetedAotBridge,
                            merged.RegistersTargetedAotBridge || info.RegistersTargetedAotBridge,
                            merged.RegistersBroadcastAotBridge || info.RegistersBroadcastAotBridge,
                            merged.HasMessageAttribute || info.HasMessageAttribute
                        );
                    }

                    return merged;
                })
                .OrderBy(
                    info =>
                        info.TypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    StringComparer.Ordinal
                )
                .ToList();

            if (registrarTypes.Count == 0)
            {
                return;
            }

            string source = GenerateTopLevelAotRegistrarSource(registrarTypes);
            context.AddSource(
                "DxMessaging.Il2CppMessageRegistrar.g.cs",
                SourceText.From(source, Encoding.UTF8)
            );
        }

        private static string GenerateTopLevelAotRegistrarSource(List<AotRegistrarInfo> types)
        {
            const string Indent = "        ";
            List<AotRegistrarSourceInfo> sourceTypes = types
                .Select(
                    (info, index) =>
                    {
                        string typeName = info.TypeSymbol.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat
                        );
                        return new AotRegistrarSourceInfo(
                            info,
                            typeName,
                            index + "_" + SanitizeIdentifier(typeName)
                        );
                    }
                )
                .ToList();

            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated by DxMessageIdGenerator/>");
            builder.AppendLine("#pragma warning disable");
            builder.AppendLine("#nullable enable annotations");
            builder.AppendLine("#if ENABLE_IL2CPP && UNITY_2021_3_OR_NEWER");
            builder.AppendLine("namespace DxMessaging.Generated");
            builder.AppendLine("{");
            builder.AppendLine("    [global::UnityEngine.Scripting.Preserve]");
            builder.AppendLine("    internal static class DxMessagingIl2CppMessageRegistrar");
            builder.AppendLine("    {");
            builder.AppendLine(
                "        [global::UnityEngine.RuntimeInitializeOnLoadMethod(global::UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]"
            );
            builder.AppendLine("        private static void Register()");
            builder.AppendLine("        {");
            foreach (AotRegistrarSourceInfo sourceInfo in sourceTypes)
            {
                AotRegistrarInfo info = sourceInfo.Info;
                string typeName = sourceInfo.TypeName;
                string suffix = sourceInfo.Suffix;
                if (info.RegistersUntargetedAotBridge)
                {
                    AppendAotRegisterCall(
                        builder,
                        Indent + "    ",
                        "RegisterAotUntargetedBridge",
                        AotBridgeKind.Untargeted,
                        typeName,
                        "__DxMessagingAotUntargetedBridge_" + suffix
                    );
                }
                if (info.RegistersTargetedAotBridge)
                {
                    AppendAotRegisterCall(
                        builder,
                        Indent + "    ",
                        "RegisterAotTargetedBridge",
                        AotBridgeKind.Targeted,
                        typeName,
                        "__DxMessagingAotTargetedBridge_" + suffix
                    );
                }
                if (info.RegistersBroadcastAotBridge)
                {
                    AppendAotRegisterCall(
                        builder,
                        Indent + "    ",
                        "RegisterAotSourcedBridge",
                        AotBridgeKind.Broadcast,
                        typeName,
                        "__DxMessagingAotSourcedBridge_" + suffix
                    );
                }
            }
            builder.AppendLine("        }");

            AppendAotReflectionHelper(builder, Indent);
            foreach (AotRegistrarSourceInfo sourceInfo in sourceTypes)
            {
                AotRegistrarInfo info = sourceInfo.Info;
                string typeName = sourceInfo.TypeName;
                string suffix = sourceInfo.Suffix;
                if (info.RegistersUntargetedAotBridge)
                {
                    AppendUntargetedBridgeMethod(
                        builder,
                        Indent,
                        "__DxMessagingAotUntargetedBridge_" + suffix,
                        typeName
                    );
                }
                if (info.RegistersTargetedAotBridge)
                {
                    AppendTargetedBridgeMethod(
                        builder,
                        Indent,
                        "__DxMessagingAotTargetedBridge_" + suffix,
                        typeName
                    );
                }
                if (info.RegistersBroadcastAotBridge)
                {
                    AppendBroadcastBridgeMethod(
                        builder,
                        Indent,
                        "__DxMessagingAotSourcedBridge_" + suffix,
                        typeName
                    );
                }
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");
            builder.AppendLine("#endif");
            return builder.ToString();
        }

        private static void AppendAotRegisterCall(
            StringBuilder builder,
            string indent,
            string runtimeMethodName,
            AotBridgeKind bridgeKind,
            string fullyQualifiedName,
            string bridgeMethodName
        )
        {
            string delegateType = GetAotDelegateType(bridgeKind);
            builder
                .Append(indent)
                .Append("__DxMessagingRegisterAotBridge(\"")
                .Append(runtimeMethodName)
                .Append("\", typeof(")
                .Append(fullyQualifiedName)
                .Append("), (")
                .Append(delegateType)
                .Append(")")
                .Append(bridgeMethodName)
                .AppendLine(");");
        }

        private static string GetAotDelegateType(AotBridgeKind bridgeKind)
        {
            return bridgeKind switch
            {
                AotBridgeKind.Untargeted =>
                    "global::System.Action<global::DxMessaging.Core.MessageBus.IMessageBus, global::DxMessaging.Core.Messages.IUntargetedMessage>",
                AotBridgeKind.Targeted =>
                    "global::System.Action<global::DxMessaging.Core.MessageBus.IMessageBus, global::DxMessaging.Core.InstanceId, global::DxMessaging.Core.Messages.ITargetedMessage>",
                AotBridgeKind.Broadcast =>
                    "global::System.Action<global::DxMessaging.Core.MessageBus.IMessageBus, global::DxMessaging.Core.InstanceId, global::DxMessaging.Core.Messages.IBroadcastMessage>",
                _ => throw new ArgumentOutOfRangeException(nameof(bridgeKind)),
            };
        }

        private static void AppendAotReflectionHelper(StringBuilder builder, string indent)
        {
            builder.AppendLine();
            builder
                .Append(indent)
                .AppendLine(
                    "private static void __DxMessagingRegisterAotBridge(string methodName, global::System.Type messageType, global::System.Delegate bridge)"
                );
            builder.Append(indent).AppendLine("{");
            builder
                .Append(indent)
                .AppendLine(
                    "    global::System.Reflection.MethodInfo method = typeof(global::DxMessaging.Core.MessageBus.MessageBus).GetMethod(methodName, global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.NonPublic);"
                );
            builder.Append(indent).AppendLine("    if (method == null)");
            builder.Append(indent).AppendLine("    {");
            builder
                .Append(indent)
                .AppendLine(
                    "        throw new global::System.MissingMethodException(\"DxMessaging AOT bridge registration hook was not found: \" + methodName);"
                );
            builder.Append(indent).AppendLine("    }");
            builder
                .Append(indent)
                .AppendLine("    method.Invoke(null, new object[] { messageType, bridge });");
            builder.Append(indent).AppendLine("}");
        }

        private static void AppendUntargetedBridgeMethod(
            StringBuilder builder,
            string indent,
            string methodName,
            string fullyQualifiedName
        )
        {
            builder.AppendLine();
            builder
                .Append(indent)
                .AppendLine(
                    "private static void "
                        + methodName
                        + "(global::DxMessaging.Core.MessageBus.IMessageBus messageBus, global::DxMessaging.Core.Messages.IUntargetedMessage message)"
                );
            builder.Append(indent).AppendLine("{");
            builder
                .Append(indent)
                .Append("    ")
                .Append(fullyQualifiedName)
                .AppendLine(" typedMessage = (" + fullyQualifiedName + ")message;");
            builder
                .Append(indent)
                .AppendLine("    messageBus.UntargetedBroadcast(ref typedMessage);");
            builder.Append(indent).AppendLine("}");
        }

        private static void AppendTargetedBridgeMethod(
            StringBuilder builder,
            string indent,
            string methodName,
            string fullyQualifiedName
        )
        {
            builder.AppendLine();
            builder
                .Append(indent)
                .AppendLine(
                    "private static void "
                        + methodName
                        + "(global::DxMessaging.Core.MessageBus.IMessageBus messageBus, global::DxMessaging.Core.InstanceId target, global::DxMessaging.Core.Messages.ITargetedMessage message)"
                );
            builder.Append(indent).AppendLine("{");
            builder
                .Append(indent)
                .Append("    ")
                .Append(fullyQualifiedName)
                .AppendLine(" typedMessage = (" + fullyQualifiedName + ")message;");
            builder
                .Append(indent)
                .AppendLine("    messageBus.TargetedBroadcast(ref target, ref typedMessage);");
            builder.Append(indent).AppendLine("}");
        }

        private static void AppendBroadcastBridgeMethod(
            StringBuilder builder,
            string indent,
            string methodName,
            string fullyQualifiedName
        )
        {
            builder.AppendLine();
            builder
                .Append(indent)
                .AppendLine(
                    "private static void "
                        + methodName
                        + "(global::DxMessaging.Core.MessageBus.IMessageBus messageBus, global::DxMessaging.Core.InstanceId source, global::DxMessaging.Core.Messages.IBroadcastMessage message)"
                );
            builder.Append(indent).AppendLine("{");
            builder
                .Append(indent)
                .Append("    ")
                .Append(fullyQualifiedName)
                .AppendLine(" typedMessage = (" + fullyQualifiedName + ")message;");
            builder
                .Append(indent)
                .AppendLine("    messageBus.SourcedBroadcast(ref source, ref typedMessage);");
            builder.Append(indent).AppendLine("}");
        }

        private static bool IsAccessibleFromTopLevelRegistrar(INamedTypeSymbol typeSymbol)
        {
            if (ContainsTypeParameters(typeSymbol))
            {
                return false;
            }

            for (
                INamedTypeSymbol current = typeSymbol;
                current != null;
                current = current.ContainingType
            )
            {
                if (!IsAccessibleFromSameAssemblyTopLevel(current.DeclaredAccessibility))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAccessibleFromSameAssemblyTopLevel(Accessibility accessibility)
        {
            return accessibility == Accessibility.Public
                || accessibility == Accessibility.Internal
                || accessibility == Accessibility.ProtectedOrInternal;
        }

        private static bool ContainsTypeParameters(INamedTypeSymbol typeSymbol)
        {
            for (
                INamedTypeSymbol current = typeSymbol;
                current != null;
                current = current.ContainingType
            )
            {
                if (current.TypeParameters.Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string SanitizeIdentifier(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('_');
                }
            }

            return builder.ToString();
        }

        private static bool IsRecordDeclaration(INamedTypeSymbol symbol)
        {
            foreach (SyntaxReference syntaxReference in symbol.DeclaringSyntaxReferences)
            {
                if (syntaxReference.GetSyntax() is TypeDeclarationSyntax declaration)
                {
                    string kind = declaration.Kind().ToString();
                    if (kind.IndexOf("Record", StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<INamedTypeSymbol> GetNonPartialContainers(INamedTypeSymbol typeSymbol)
        {
            List<INamedTypeSymbol> result = new();
            INamedTypeSymbol current = typeSymbol.ContainingType;
            while (current is not null)
            {
                if (!IsDeclaredFullyPartial(current))
                {
                    result.Add(current);
                }
                current = current.ContainingType;
            }
            return result;
        }

        private static bool IsDeclaredFullyPartial(INamedTypeSymbol symbol)
        {
            if (symbol.DeclaringSyntaxReferences.Length == 0)
            {
                return false;
            }
            foreach (SyntaxReference syntaxRef in symbol.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax() is TypeDeclarationSyntax tds)
                {
                    bool hasPartial = tds.Modifiers.Any(static m =>
                        m.IsKind(SyntaxKind.PartialKeyword)
                    );
                    if (!hasPartial)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
            return true;
        }
    }
}
