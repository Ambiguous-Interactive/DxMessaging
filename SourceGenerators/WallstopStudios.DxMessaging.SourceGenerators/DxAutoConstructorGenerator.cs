namespace WallstopStudios.DxMessaging.SourceGenerators
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Text;
    using static WallstopStudios.DxMessaging.SourceGenerators.GeneratorDiagnostics;

    [Generator]
    public sealed class DxAutoConstructorGenerator : ISourceGenerator
    {
        private static readonly SymbolDisplayFormat DeclarationNameFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
        );

        private static readonly SymbolDisplayFormat IdentifierNameFormat =
            DeclarationNameFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None);

        private static readonly DiagnosticDescriptor InvalidOptionalDefaultDiagnostic = new(
            id: "DXMSG005",
            title: "Invalid optional default value",
            messageFormat: "Field '{0}' default value expression '{1}' is not a valid optional parameter default for type '{2}'",
            category: "DxMessaging",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor UnsupportedStructInitializationDiagnostic =
            new(
                id: "DXMSG011",
                title: "Cannot preserve explicit-interface struct initializers",
                messageFormat: "Type '{0}' combines initialized and uninitialized explicit-interface auto-properties with a user parameterless constructor. Add an initializer to each explicit-interface auto-property, use named backing fields, or remove DxAutoConstructor and write the constructor explicitly.",
                category: "DxMessaging",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true
            );

        private static readonly DiagnosticDescriptor PrimaryConstructorDiagnostic = new(
            id: "DXMSG012",
            title: "Cannot infer primary constructor arguments",
            messageFormat: "Type '{0}' declares a primary constructor with parameters. To choose its arguments explicitly, remove DxAutoConstructor and write the field constructor with a this(...) initializer.",
            category: "DxMessaging",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor RecordCopyConstructorDiagnostic = new(
            id: "DXMSG013",
            title: "Field constructor conflicts with record copy constructor",
            messageFormat: "Type '{0}' has a field constructor with the same signature as its record copy constructor. Remove DxAutoConstructor and use an init-only property or a construction API with a different signature.",
            category: "DxMessaging",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private const string AutoGenConstructorAttrFullName =
            "DxMessaging.Core.Attributes.DxAutoConstructorAttribute";

        private const string OptionalParameterAttrFullName =
            "DxMessaging.Core.Attributes.DxOptionalParameterAttribute";

        // Info needed during generation for a valid type
        private record struct TypeToGenerateInfo(
            INamedTypeSymbol TypeSymbol,
            TypeDeclarationSyntax DeclarationSyntax,
            List<IFieldSymbol> FieldsToInject // Public non-static fields in declaration order
        );

        /// <summary>
        /// Configures syntax collection for annotated types that need generated constructors.
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

            List<TypeToGenerateInfo> typesToGenerate = new(receiver.Candidates.Count);
            foreach (TypeDeclarationSyntax candidate in receiver.Candidates)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                TypeToGenerateInfo? target = GetSemanticTargetForGeneration(
                    candidate,
                    context.Compilation,
                    context.CancellationToken
                );
                if (target.HasValue)
                {
                    typesToGenerate.Add(target.Value);
                }
            }

            Execute(typesToGenerate, context);
        }

        private static bool IsSyntaxTargetForGeneration(SyntaxNode node) =>
            node is TypeDeclarationSyntax { AttributeLists.Count: > 0 } typeDecl
            && (
                typeDecl.IsKind(SyntaxKind.ClassDeclaration)
                || typeDecl.IsKind(SyntaxKind.StructDeclaration)
                || typeDecl.IsKind(SyntaxKind.RecordDeclaration)
                || string.Equals(
                    typeDecl.Kind().ToString(),
                    "RecordStructDeclaration",
                    StringComparison.Ordinal
                )
            );

        private static TypeToGenerateInfo? GetSemanticTargetForGeneration(
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
                is not { } typeSymbol
            )
            {
                return null;
            }

            // Check if the type has the DxAutoGenConstructor attribute
            bool hasAutoGenConstructorAttribute = false;
            foreach (AttributeData attributeData in typeSymbol.GetAttributes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    attributeData.AttributeClass?.ToDisplayString()
                    == AutoGenConstructorAttrFullName
                )
                {
                    hasAutoGenConstructorAttribute = true;
                    break;
                }
            }

            if (!hasAutoGenConstructorAttribute)
            {
                return null;
            }

            // Find public readonly non-static fields in declaration order
            List<IFieldSymbol> fieldsToInject = new();
            foreach (ISymbol member in typeSymbol.GetMembers())
            {
                if (
                    member is IFieldSymbol
                    {
                        DeclaredAccessibility: Accessibility.Public,
                        IsStatic: false,
                    } field
                )
                {
                    fieldsToInject.Add(field);
                }
            }
            SortFieldsByDeclarationOrder(fieldsToInject);

            // If there are no relevant fields, we don't need to generate a constructor
            if (fieldsToInject.Count == 0)
            {
                return null;
            }

            return new TypeToGenerateInfo(typeSymbol, typeDeclarationSyntax, fieldsToInject);
        }

        private static int GetDeclarationStart(IFieldSymbol field)
        {
            return field.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? 0;
        }

        private static void SortFieldsByDeclarationOrder(List<IFieldSymbol> fields)
        {
            for (int index = 1; index < fields.Count; index++)
            {
                IFieldSymbol field = fields[index];
                int declarationStart = GetDeclarationStart(field);
                int insertionIndex = index;
                while (
                    0 < insertionIndex
                    && declarationStart < GetDeclarationStart(fields[insertionIndex - 1])
                )
                {
                    fields[insertionIndex] = fields[insertionIndex - 1];
                    insertionIndex--;
                }
                fields[insertionIndex] = field;
            }
        }

        private static void Execute(
            List<TypeToGenerateInfo> typesToGenerate,
            GeneratorExecutionContext context
        )
        {
            if (typesToGenerate.Count == 0)
            {
                return;
            }

            // Use a HashSet to track types already processed to avoid duplicate generation for partial classes
            HashSet<INamedTypeSymbol> processedTypes = new(SymbolEqualityComparer.Default);

            foreach (TypeToGenerateInfo typeInfo in typesToGenerate)
            {
                if (!processedTypes.Add(typeInfo.TypeSymbol) || typeInfo.FieldsToInject.Count == 0)
                {
                    continue; // Already processed this type (e.g., from another partial definition)
                }

                context.CancellationToken.ThrowIfCancellationRequested();

                // If nested, ensure all containers are declared partial; otherwise report diagnostic and skip
                if (typeInfo.TypeSymbol.ContainingType is not null)
                {
                    List<INamedTypeSymbol> nonPartial = GetNonPartialContainers(
                        typeInfo.TypeSymbol
                    );
                    if (0 < nonPartial.Count)
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
                                typeInfo.DeclarationSyntax.Identifier.GetLocation(),
                                typeInfo.TypeSymbol.ToDisplayString(
                                    SymbolDisplayFormat.MinimallyQualifiedFormat
                                ),
                                containersList
                            )
                        );
                        // Location-specific suggestions on each non-partial container
                        foreach (INamedTypeSymbol container in nonPartial)
                        {
                            SyntaxReference? sr =
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
                                        typeInfo.TypeSymbol.ToDisplayString(
                                            SymbolDisplayFormat.MinimallyQualifiedFormat
                                        )
                                    )
                                );
                            }
                        }
                        continue;
                    }
                }

                if (
                    typeInfo.TypeSymbol.TypeKind == TypeKind.Class
                    && IsRecordDeclaration(typeInfo.TypeSymbol)
                    && typeInfo.FieldsToInject.Count == 1
                    && SymbolEqualityComparer.Default.Equals(
                        typeInfo.FieldsToInject[0].Type,
                        typeInfo.TypeSymbol
                    )
                )
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            RecordCopyConstructorDiagnostic,
                            typeInfo.DeclarationSyntax.Identifier.GetLocation(),
                            typeInfo.TypeSymbol.ToDisplayString(
                                SymbolDisplayFormat.MinimallyQualifiedFormat
                            )
                        )
                    );
                    continue;
                }

                ParameterListSyntax? primaryParameters = GetPrimaryConstructorParameters(
                    typeInfo.TypeSymbol,
                    context.CancellationToken
                );
                if (primaryParameters != null && 0 < primaryParameters.Parameters.Count)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            PrimaryConstructorDiagnostic,
                            typeInfo.DeclarationSyntax.Identifier.GetLocation(),
                            typeInfo.TypeSymbol.ToDisplayString(
                                SymbolDisplayFormat.MinimallyQualifiedFormat
                            )
                        )
                    );
                    continue;
                }

                // Generate the partial class/struct with the constructor
                string? generatedSource = GenerateConstructorSource(
                    context.Compilation,
                    typeInfo,
                    context,
                    hasEmptyPrimaryConstructor: primaryParameters != null
                );
                if (generatedSource == null)
                {
                    continue;
                }
                string hintName =
                    $"{typeInfo.TypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.AutoGenConstructor.g.cs"
                        .Replace("global::", "")
                        .Replace("@", "")
                        .Replace("<", "-")
                        .Replace(">", "-")
                        .Replace(",", "-"); // Hyphens cannot collide with legal C# identifier characters.

                context.AddSource(hintName, SourceText.From(generatedSource, Encoding.UTF8));
            }
        }

        /// <summary>
        /// Finds primary constructor syntax across every partial declaration of the target type.
        /// </summary>
        private static ParameterListSyntax? GetPrimaryConstructorParameters(
            INamedTypeSymbol typeSymbol,
            CancellationToken cancellationToken
        )
        {
            foreach (SyntaxReference syntaxReference in typeSymbol.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    syntaxReference.GetSyntax(cancellationToken)
                    is TypeDeclarationSyntax declaration
                )
                {
                    /* Inspect direct syntax children to recognize newer primary-constructor
                     * declarations while using Unity's Roslyn 3.8 API surface. */
                    foreach (SyntaxNode child in declaration.ChildNodes())
                    {
                        if (child is ParameterListSyntax parameters)
                        {
                            return parameters;
                        }
                    }
                }
            }
            return null;
        }

        private static string? GenerateConstructorSource(
            Compilation compilation,
            TypeToGenerateInfo typeInfo,
            GeneratorExecutionContext spc,
            bool hasEmptyPrimaryConstructor
        )
        {
            INamedTypeSymbol typeSymbol = typeInfo.TypeSymbol;
            List<IFieldSymbol> fieldsToInject = typeInfo.FieldsToInject;
            string namespaceName = typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString();
            string namespaceBlockOpen = string.IsNullOrEmpty(namespaceName)
                ? string.Empty
                : $"namespace {namespaceName}\n{{";
            string namespaceBlockClose = string.IsNullOrEmpty(namespaceName) ? string.Empty : "}";
            const string Indent = "    ";

            // Build container wrappers for nested types so the partial can merge correctly
            var containers = new Stack<INamedTypeSymbol>();
            INamedTypeSymbol current = typeSymbol.ContainingType;
            while (current is not null)
            {
                containers.Push(current);
                current = current.ContainingType;
            }

            var containersOpen = new StringBuilder();
            var containersClose = new StringBuilder();
            string currentIndent = Indent; // one level inside namespace (or top-level)

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
                    TypeKind.Class => containerIsRecord ? "record" : "class",
                    TypeKind.Struct => containerIsRecord ? "record struct" : "struct",
                    TypeKind.Interface => "interface",
                    _ => "class",
                };

                // Render generic parameters for the container
                string containerName = container.ToDisplayString(DeclarationNameFormat);

                // Do not repeat modifiers like sealed/abstract/static/readonly/ref here to avoid changing semantics across parts
                containersOpen.AppendLine(
                    $"{currentIndent}{containerAccessibility} partial {containerKind} {containerName}"
                );
                containersOpen.Append(currentIndent).AppendLine("{");
                currentIndent += Indent;
            }

            string innerIndent = currentIndent; // indent level for the target (innermost) type

            // Use simple identifier + type parameters (no containers) because we are inside container wrappers
            string typeName = typeSymbol.ToDisplayString(DeclarationNameFormat);
            bool typeIsRecord = IsRecordDeclaration(typeSymbol);
            string typeKind = typeSymbol.TypeKind switch
            {
                TypeKind.Class => typeIsRecord ? "record" : "class",
                TypeKind.Struct => typeIsRecord ? "record struct" : "struct",
                _ => throw new InvalidOperationException(
                    "Unsupported type kind for constructor generation"
                ),
            };

            string typeAccessibility = typeSymbol.DeclaredAccessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Protected => "protected",
                Accessibility.Private => "private",
                Accessibility.Internal => "internal",
                Accessibility.ProtectedOrInternal => "protected internal",
                Accessibility.ProtectedAndInternal => "private protected",
                _ => "internal",
            };

            string constructorAccessibility = "public"; // Always public as requested

            // Generate constructor parameters and body assignments
            StringBuilder constructorParams = new();
            StringBuilder constructorBody = new();

            SymbolDisplayFormat fieldTypeFormat =
                SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                    SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                        | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                );

            List<(
                string Type,
                string Name,
                bool IsOptional,
                string? DefaultExpr,
                bool AnnotationsEnabled
            )> parameterDetails =
                new List<(
                    string Type,
                    string Name,
                    bool IsOptional,
                    string? DefaultExpr,
                    bool AnnotationsEnabled
                )>();

            // For validating expressions, use the semantic model for this type's tree
            SemanticModel semanticModel = compilation.GetSemanticModel(
                typeInfo.DeclarationSyntax.SyntaxTree
            );
            int anchorPosition = typeInfo.DeclarationSyntax.SpanStart;

            (
                bool requiresUnsafeContext,
                bool useDefaultInitializer,
                bool unsupportedInitialization
            ) = hasEmptyPrimaryConstructor
                ? (false, true, false)
                : AppendStructInitialization(compilation, typeSymbol, constructorBody);
            if (unsupportedInitialization)
            {
                spc.ReportDiagnostic(
                    Diagnostic.Create(
                        UnsupportedStructInitializationDiagnostic,
                        typeInfo.DeclarationSyntax.Identifier.GetLocation(),
                        typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    )
                );
                return null;
            }
            foreach (IFieldSymbol field in fieldsToInject)
            {
                bool fieldRequiresUnsafeContext = RequiresUnsafeContext(field.Type);
                requiresUnsafeContext |= fieldRequiresUnsafeContext;
                SymbolDisplayFormat parameterTypeFormat = fieldRequiresUnsafeContext
                    ? fieldTypeFormat
                        .WithMiscellaneousOptions(
                            fieldTypeFormat.MiscellaneousOptions
                                | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                        )
                        .WithParameterOptions(
                            fieldTypeFormat.ParameterOptions
                                | SymbolDisplayParameterOptions.IncludeType
                                | SymbolDisplayParameterOptions.IncludeParamsRefOut
                        )
                        .WithMemberOptions(
                            fieldTypeFormat.MemberOptions | SymbolDisplayMemberOptions.IncludeRef
                        )
                    : fieldTypeFormat;
                string fieldType = field.Type.ToDisplayString(parameterTypeFormat);
                string fieldName = field.Name;
                string parameterName = field.ToDisplayString(IdentifierNameFormat);
                string? defaultExpr = null;
                bool isOptional = false;

                foreach (AttributeData attr in field.GetAttributes())
                {
                    if (
                        !string.Equals(
                            attr.AttributeClass?.ToDisplayString(),
                            OptionalParameterAttrFullName,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        continue;
                    }

                    isOptional = true;

                    // Named argument: Expression (verbatim C# expression)
                    if (attr.NamedArguments.Any())
                    {
                        foreach (KeyValuePair<string, TypedConstant> kv in attr.NamedArguments)
                        {
                            if (
                                string.Equals(kv.Key, "Expression", StringComparison.Ordinal)
                                && kv.Value.Kind == TypedConstantKind.Primitive
                                && kv.Value.Value is string exprStr
                                && !string.IsNullOrWhiteSpace(exprStr)
                            )
                            {
                                defaultExpr = exprStr.Trim();
                                // Validate expression compatibility with field type
                                if (
                                    !IsValidDefaultExpression(
                                        semanticModel,
                                        anchorPosition,
                                        field,
                                        defaultExpr
                                    )
                                )
                                {
                                    Location reportLoc =
                                        attr.ApplicationSyntaxReference?.GetSyntax()?.GetLocation()
                                        ?? field.Locations.FirstOrDefault()
                                        ?? Location.None;
                                    spc.ReportDiagnostic(
                                        Diagnostic.Create(
                                            InvalidOptionalDefaultDiagnostic,
                                            reportLoc,
                                            fieldName,
                                            defaultExpr,
                                            fieldType
                                        )
                                    );
                                }
                                break;
                            }
                        }
                    }

                    // If no explicit expression, check constructor argument constant
                    if (defaultExpr == null && attr.ConstructorArguments.Length == 1)
                    {
                        TypedConstant arg = attr.ConstructorArguments[0];
                        if (arg.IsNull)
                        {
                            defaultExpr = "null";
                            if (!CanUseNullDefault(field.Type))
                            {
                                Location reportLoc =
                                    attr.ApplicationSyntaxReference?.GetSyntax()?.GetLocation()
                                    ?? field.Locations.FirstOrDefault()
                                    ?? Location.None;
                                spc.ReportDiagnostic(
                                    Diagnostic.Create(
                                        InvalidOptionalDefaultDiagnostic,
                                        reportLoc,
                                        fieldName,
                                        defaultExpr,
                                        fieldType
                                    )
                                );
                            }
                        }
                        else if (arg.Kind == TypedConstantKind.Primitive)
                        {
                            object? val = arg.Value;
                            defaultExpr = FormatLiteral(val);
                            // Preserve the original constant type when rendering loses its suffix or cast.
                            string expressionForValidation = arg.Type is { } constantType
                                ? $"({constantType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})({defaultExpr})"
                                : defaultExpr;
                            if (
                                !IsValidDefaultExpression(
                                    semanticModel,
                                    anchorPosition,
                                    field,
                                    expressionForValidation
                                )
                            )
                            {
                                Location reportLoc =
                                    attr.ApplicationSyntaxReference?.GetSyntax()?.GetLocation()
                                    ?? field.Locations.FirstOrDefault()
                                    ?? Location.None;
                                spc.ReportDiagnostic(
                                    Diagnostic.Create(
                                        InvalidOptionalDefaultDiagnostic,
                                        reportLoc,
                                        fieldName,
                                        defaultExpr,
                                        fieldType
                                    )
                                );
                            }
                        }
                    }

                    break; // only one DxOptionalParameterAttribute expected
                }

                SyntaxReference? fieldDeclaration =
                    field.DeclaringSyntaxReferences.FirstOrDefault();
                SemanticModel fieldModel =
                    fieldDeclaration == null
                    || ReferenceEquals(fieldDeclaration.SyntaxTree, semanticModel.SyntaxTree)
                        ? semanticModel
                        : compilation.GetSemanticModel(fieldDeclaration.SyntaxTree);
                bool annotationsEnabled =
                    (
                        fieldModel.GetNullableContext(
                            fieldDeclaration?.Span.Start ?? anchorPosition
                        ) & NullableContext.AnnotationsEnabled
                    ) != 0;
                parameterDetails.Add(
                    (fieldType, parameterName, isOptional, defaultExpr, annotationsEnabled)
                );
                constructorBody.AppendLine(
                    $"{Indent}{Indent}    this.{parameterName} = {parameterName};"
                );
            }

            bool initialAnnotationsEnabled = parameterDetails[0].AnnotationsEnabled;
            bool currentAnnotationsEnabled = initialAnnotationsEnabled;
            string initialNullableDirective = initialAnnotationsEnabled
                ? "#nullable enable annotations"
                : "#nullable disable annotations";

            for (int i = 0; i < parameterDetails.Count; i++)
            {
                (
                    string Type,
                    string Name,
                    bool IsOptional,
                    string? DefaultExpr,
                    bool AnnotationsEnabled
                ) p = parameterDetails[i];
                if (currentAnnotationsEnabled != p.AnnotationsEnabled)
                {
                    // Retain each field's annotation context in its generated parameter.
                    constructorParams
                        .AppendLine()
                        .AppendLine(
                            p.AnnotationsEnabled
                                ? "#nullable enable annotations"
                                : "#nullable disable annotations"
                        );
                    currentAnnotationsEnabled = p.AnnotationsEnabled;
                }
                constructorParams.Append($"{p.Type} {p.Name}");
                if (p.IsOptional)
                {
                    if (!string.IsNullOrWhiteSpace(p.DefaultExpr))
                    {
                        constructorParams.Append(" = ").Append(p.DefaultExpr);
                    }
                    else
                    {
                        constructorParams.Append(" = default");
                    }
                }

                if (i < parameterDetails.Count - 1)
                {
                    constructorParams.Append(", ");
                }
            }

            if (currentAnnotationsEnabled != initialAnnotationsEnabled)
            {
                constructorParams.AppendLine().AppendLine(initialNullableDirective);
            }

            // Close containers
            for (int i = 0; i < containers.Count; i++)
            {
                currentIndent = currentIndent.Substring(
                    0,
                    Math.Max(0, currentIndent.Length - Indent.Length)
                );
                containersClose.Append(currentIndent).AppendLine("}");
            }

            string unsafeModifier = requiresUnsafeContext ? "unsafe " : string.Empty;
            string constructorInitializer = useDefaultInitializer ? " : this()" : string.Empty;
            return string.Join(
                "\r\n",
                new[]
                {
                    "// <auto-generated by DxAutoGenConstructorGenerator/>",
                    initialNullableDirective,
                    string.Empty,
                    namespaceBlockOpen,
                    $"{containersOpen}{innerIndent}{typeAccessibility} partial {typeKind} {typeName}",
                    $"{innerIndent}{{",
                    $"{Indent}    /// <summary>",
                    $"{Indent}    /// Auto-generated constructor by DxAutoGenConstructorGenerator.",
                    $"{Indent}    /// </summary>",
                    $"{innerIndent}    {constructorAccessibility} {unsafeModifier}{typeSymbol.ToDisplayString(IdentifierNameFormat)}({constructorParams}){constructorInitializer}",
                    $"{innerIndent}    {{",
                    $"{constructorBody}",
                    $"{innerIndent}    }}",
                    $"{innerIndent}}}",
                    $"{containersClose}",
                    namespaceBlockClose,
                    string.Empty,
                }
            );
        }

        private static (
            bool RequiresUnsafeContext,
            bool UseDefaultInitializer,
            bool Unsupported
        ) AppendStructInitialization(
            Compilation compilation,
            INamedTypeSymbol typeSymbol,
            StringBuilder body
        )
        {
            // C# 11 supplies auto-defaulting; retain the existing constructor on that path.
            if (
                typeSymbol.TypeKind != TypeKind.Struct
                || compilation is not CSharpCompilation csharp
                || 1100 <= (int)csharp.LanguageVersion
            )
            {
                return (false, false, false);
            }

            List<(ISymbol Member, ITypeSymbol Type, bool Initialized)> storage = new();
            bool hasOpaqueDefault = false;
            bool hasOpaqueInitializer = false;
            foreach (ISymbol member in typeSymbol.GetMembers())
            {
                if (member.IsStatic)
                {
                    continue;
                }
                ISymbol state;
                ITypeSymbol stateType;
                switch (member)
                {
                    case IFieldSymbol field:
                        /* Public fields are assigned from parameters; fixed buffers do not
                         * participate in constructor definite assignment. */
                        if (
                            field.IsFixedSizeBuffer
                            || field.DeclaredAccessibility == Accessibility.Public
                        )
                        {
                            continue;
                        }
                        state = field.AssociatedSymbol ?? field;
                        stateType = field.Type;
                        break;
                    case IEventSymbol eventSymbol
                        when eventSymbol.DeclaringSyntaxReferences.Any(reference =>
                            reference.GetSyntax() is VariableDeclaratorSyntax
                        ):
                        // Roslyn does not expose field-like event backing fields as members.
                        state = eventSymbol;
                        stateType = eventSymbol.Type;
                        break;
                    default:
                        continue;
                }

                bool initialized = state.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax()
                        is VariableDeclaratorSyntax { Initializer: not null }
                            or PropertyDeclarationSyntax { Initializer: not null }
                );
                storage.Add((state, stateType, initialized));
                if (!state.CanBeReferencedByName)
                {
                    hasOpaqueDefault |= !initialized;
                    hasOpaqueInitializer |= initialized;
                }
            }

            /* The implicit default constructor clears backing storage before running
             * initializers. An explicit parameterless constructor would run user code. */
            if (hasOpaqueDefault && hasOpaqueInitializer)
            {
                bool hasUserDefaultConstructor = typeSymbol.InstanceConstructors.Any(constructor =>
                    constructor.Parameters.Length == 0 && !constructor.IsImplicitlyDeclared
                );
                return (false, !hasUserDefaultConstructor, hasUserDefaultConstructor);
            }

            const string AssignmentIndent = "            ";
            bool requiresUnsafeContext = false;
            if (!hasOpaqueDefault)
            {
                foreach ((ISymbol member, ITypeSymbol type, bool initialized) in storage)
                {
                    if (initialized)
                    {
                        continue;
                    }
                    requiresUnsafeContext |= RequiresUnsafeContext(type);
                    body.AppendLine(
                        $"{AssignmentIndent}this.{member.ToDisplayString(IdentifierNameFormat)} = default;"
                    );
                }
                return (requiresUnsafeContext, false, false);
            }

            /* An explicit-interface auto-property cannot be assigned by name. Preserve
             * initialized named storage before clearing the remaining backing fields. */
            HashSet<string> localNames = new(StringComparer.Ordinal);
            foreach (ISymbol member in typeSymbol.GetMembers())
            {
                localNames.Add(member.Name);
            }
            for (INamedTypeSymbol? scope = typeSymbol; scope != null; scope = scope.ContainingType)
            {
                foreach (ITypeParameterSymbol parameter in scope.TypeParameters)
                {
                    localNames.Add(parameter.Name);
                }
            }
            List<(string Member, string Local)> preserved = new();
            foreach ((ISymbol member, ITypeSymbol type, bool initialized) in storage)
            {
                if (!initialized)
                {
                    continue;
                }
                requiresUnsafeContext |= RequiresUnsafeContext(type);
                string memberName = member.ToDisplayString(IdentifierNameFormat);
                string local = "__dxMessagingInitialized";
                while (!localNames.Add(local))
                {
                    local += "_";
                }
                body.AppendLine($"{AssignmentIndent}var {local} = this.{memberName};");
                preserved.Add((memberName, local));
            }
            body.AppendLine($"{AssignmentIndent}this = default;");
            foreach ((string member, string local) in preserved)
            {
                body.AppendLine($"{AssignmentIndent}this.{member} = {local};");
            }
            return (requiresUnsafeContext, false, false);
        }

        private static string FormatLiteral(object? value)
        {
            if (value == null)
            {
                return "null";
            }

            // Respect underlying type for correct literal formatting
            switch (value)
            {
                case bool b:
                    return b ? "true" : "false";
                case char ch:
                    return Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(ch).ToString();
                case string s:
                    return Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(s).ToString();
                case byte by:
                    return by.ToString(CultureInfo.InvariantCulture);
                case sbyte sb:
                    return sb.ToString(CultureInfo.InvariantCulture);
                case short sh:
                    return sh.ToString(CultureInfo.InvariantCulture);
                case ushort ush:
                    return ush.ToString(CultureInfo.InvariantCulture);
                case int i32:
                    return i32.ToString(CultureInfo.InvariantCulture);
                case uint ui32:
                    return ui32.ToString(CultureInfo.InvariantCulture) + "u";
                case long i64:
                    return i64.ToString(CultureInfo.InvariantCulture) + "L";
                case ulong ui64:
                    return ui64.ToString(CultureInfo.InvariantCulture) + "UL";
                case float f:
                    // Older Mono formatters drop the sign of negative zero.
                    if (f == 0.0f)
                    {
                        return float.IsNegativeInfinity(1.0f / f) ? "-0.0f" : "0.0f";
                    }
                    if (float.IsNaN(f))
                    {
                        return "global::System.Single.NaN";
                    }
                    if (float.IsPositiveInfinity(f))
                    {
                        return "global::System.Single.PositiveInfinity";
                    }
                    if (float.IsNegativeInfinity(f))
                    {
                        return "global::System.Single.NegativeInfinity";
                    }
                    return f.ToString("R", CultureInfo.InvariantCulture) + "f";
                case double d:
                    // Older Mono formatters drop the sign of negative zero.
                    if (d == 0.0d)
                    {
                        return double.IsNegativeInfinity(1.0d / d) ? "-0.0d" : "0.0d";
                    }
                    if (double.IsNaN(d))
                    {
                        return "global::System.Double.NaN";
                    }
                    if (double.IsPositiveInfinity(d))
                    {
                        return "global::System.Double.PositiveInfinity";
                    }
                    if (double.IsNegativeInfinity(d))
                    {
                        return "global::System.Double.NegativeInfinity";
                    }
                    return d.ToString("R", CultureInfo.InvariantCulture) + "d";
            }

            // Fallback: use ToString(), but this should not occur for attribute constants
            return value.ToString() ?? "default";
        }

        private static bool RequiresUnsafeContext(ITypeSymbol type)
        {
            while (type is IArrayTypeSymbol array)
            {
                type = array.ElementType;
            }

            return type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer;
        }

        private static bool CanUseNullDefault(ITypeSymbol type)
        {
            if (type.IsReferenceType || RequiresUnsafeContext(type))
            {
                return true;
            }

            if (
                type is INamedTypeSymbol named
                && named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            )
            {
                return true;
            }

            return false;
        }

        private static bool IsValidDefaultExpression(
            SemanticModel semanticModel,
            int anchorPosition,
            IFieldSymbol field,
            string expr
        )
        {
            string trimmed = expr.Trim();
            if (string.Equals(trimmed, "default", StringComparison.Ordinal))
            {
                // default literal is permitted for any type as parameter default (typed by parameter)
                return true;
            }

            if (string.Equals(trimmed, "null", StringComparison.Ordinal))
            {
                return CanUseNullDefault(field.Type);
            }

            try
            {
                ExpressionSyntax exprSyntax = SyntaxFactory.ParseExpression(trimmed);
                foreach (SyntaxNode node in exprSyntax.DescendantNodesAndSelf())
                {
                    if (
                        node
                            is not InvocationExpressionSyntax
                            {
                                Expression: IdentifierNameSyntax name,
                            }
                        || !string.Equals(
                            name.Identifier.ValueText,
                            "nameof",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        continue;
                    }

                    /*
                        Roslyn 5.9 throws when positional conversion binds detached nameof syntax.
                        A speculative attribute model owns the complete expression, including constants.
                    */
                    AttributeSyntax binding = SyntaxFactory.Attribute(
                        SyntaxFactory.ParseName("global::" + OptionalParameterAttrFullName),
                        SyntaxFactory.AttributeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.AttributeArgument(exprSyntax)
                            )
                        )
                    );
                    if (
                        !semanticModel.TryGetSpeculativeSemanticModel(
                            anchorPosition,
                            binding,
                            out SemanticModel? expressionModel
                        )
                        || expressionModel == null
                    )
                    {
                        return false;
                    }

                    return expressionModel
                        .ClassifyConversion(
                            binding.ArgumentList!.Arguments[0].Expression,
                            field.Type
                        )
                        .IsImplicit;
                }

                Conversion conversion = semanticModel.ClassifyConversion(
                    anchorPosition,
                    exprSyntax,
                    field.Type
                );
                return conversion.IsImplicit;
            }
            catch (ArgumentException)
            {
                // If the expression cannot be parsed, it is invalid
                return false;
            }
        }

        private static bool IsRecordDeclaration(INamedTypeSymbol symbol)
        {
            foreach (SyntaxReference syntaxReference in symbol.DeclaringSyntaxReferences)
            {
                if (syntaxReference.GetSyntax() is TypeDeclarationSyntax declaration)
                {
                    string kind = declaration.Kind().ToString();
                    if (0 <= kind.IndexOf("Record", StringComparison.Ordinal))
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
            // If we cannot find syntax references, assume not partial to be safe
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
    }
}
