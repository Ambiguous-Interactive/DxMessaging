namespace WallstopStudios.DxMessaging.SourceGenerators
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Text;

    [Generator]
    public sealed class DxAutoConstructorGenerator : ISourceGenerator
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

        private static readonly DiagnosticDescriptor InvalidOptionalDefaultDiagnostic = new(
            id: "DXMSG005",
            title: "Invalid optional default value",
            messageFormat: "Field '{0}' default value expression '{1}' is not a valid optional parameter default for type '{2}'.",
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
            ImmutableArray<IFieldSymbol> FieldsToInject // Public readonly non-static fields
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

            ImmutableArray<TypeToGenerateInfo> typesToGenerate = receiver
                .Candidates.Select(typeDeclarationSyntax =>
                    GetSemanticTargetForGeneration(
                        typeDeclarationSyntax,
                        context.Compilation,
                        context.CancellationToken
                    )
                )
                .Where(static target => target.HasValue)
                .Select(static target => target!.Value)
                .ToImmutableArray();

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
            ImmutableArray<IFieldSymbol> fieldsToInject = typeSymbol
                .GetMembers()
                .OfType<IFieldSymbol>()
                .Where(f => f.DeclaredAccessibility == Accessibility.Public && !f.IsStatic)
                .OrderBy(f => f.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? 0) // Order by declaration in source
                .ToImmutableArray();

            // If there are no relevant fields, we don't need to generate a constructor
            if (fieldsToInject.Length == 0)
            {
                return null;
            }

            return new TypeToGenerateInfo(typeSymbol, typeDeclarationSyntax, fieldsToInject);
        }

        private static void Execute(
            ImmutableArray<TypeToGenerateInfo> typesToGenerate,
            GeneratorExecutionContext context
        )
        {
            if (typesToGenerate.IsDefaultOrEmpty)
            {
                return;
            }

            // Use a HashSet to track types already processed to avoid duplicate generation for partial classes
            HashSet<INamedTypeSymbol> processedTypes = new(SymbolEqualityComparer.Default);

            foreach (TypeToGenerateInfo typeInfo in typesToGenerate)
            {
                if (!processedTypes.Add(typeInfo.TypeSymbol) || typeInfo.FieldsToInject.Length == 0)
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

                // Generate the partial class/struct with the constructor
                string generatedSource = GenerateConstructorSource(
                    context.Compilation,
                    typeInfo,
                    context
                );
                string hintName =
                    $"{typeInfo.TypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.AutoGenConstructor.g.cs"
                        .Replace("global::", "")
                        .Replace("<", "_")
                        .Replace(">", "_")
                        .Replace(",", "_"); // Clean hint name for file system

                context.AddSource(hintName, SourceText.From(generatedSource, Encoding.UTF8));
            }
        }

        private static string GenerateConstructorSource(
            Compilation compilation,
            TypeToGenerateInfo typeInfo,
            GeneratorExecutionContext spc
        )
        {
            INamedTypeSymbol typeSymbol = typeInfo.TypeSymbol;
            ImmutableArray<IFieldSymbol> fieldsToInject = typeInfo.FieldsToInject;
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
                    TypeKind.Class => containerIsRecord ? "record class" : "class",
                    TypeKind.Struct => containerIsRecord ? "record struct" : "struct",
                    _ => "class",
                };

                // Render generic parameters for the container
                string containerTypeParams =
                    container.TypeParameters.Length > 0
                        ? "<"
                            + string.Join(", ", container.TypeParameters.Select(static p => p.Name))
                            + ">"
                        : string.Empty;

                // Do not repeat modifiers like sealed/abstract/static/readonly/ref here to avoid changing semantics across parts
                containersOpen.AppendLine(
                    $"{currentIndent}{containerAccessibility} partial {containerKind} {container.Name}{containerTypeParams}"
                );
                containersOpen.Append(currentIndent).AppendLine("{");
                currentIndent += Indent;
            }

            string innerIndent = currentIndent; // indent level for the target (innermost) type

            // Use simple identifier + type parameters (no containers) because we are inside container wrappers
            string typeGenericParams =
                typeSymbol.TypeParameters.Length > 0
                    ? "<"
                        + string.Join(", ", typeSymbol.TypeParameters.Select(static p => p.Name))
                        + ">"
                    : string.Empty;
            string typeName = typeSymbol.Name + typeGenericParams;
            bool typeIsRecord = IsRecordDeclaration(typeSymbol);
            string typeKind = typeSymbol.TypeKind switch
            {
                TypeKind.Class => typeIsRecord ? "record class" : "class",
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
                );

            List<(string Type, string Name, bool IsOptional, string DefaultExpr)> parameterDetails =
                new List<(string Type, string Name, bool IsOptional, string DefaultExpr)>();

            // For validating expressions, use the semantic model for this type's tree
            SemanticModel semanticModel = compilation.GetSemanticModel(
                typeInfo.DeclarationSyntax.SyntaxTree
            );
            int anchorPosition = typeInfo.DeclarationSyntax.SpanStart;

            foreach (IFieldSymbol field in fieldsToInject)
            {
                string fieldType = field.Type.ToDisplayString(fieldTypeFormat);
                string fieldName = field.Name;
                string defaultExpr = null;
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
                                        compilation,
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
                            defaultExpr = "null"; // only valid for reference or nullable types; compiler will enforce
                            if (!IsReferenceOrNullable(field.Type))
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
                            object val = arg.Value;
                            defaultExpr = FormatLiteral(val, arg.Type);
                            // Validate primitive conversion to field type
                            ITypeSymbol sourceType = arg.Type;
                            if (sourceType != null)
                            {
                                Conversion conv = compilation.ClassifyConversion(
                                    sourceType,
                                    field.Type
                                );
                                if (!conv.IsImplicit)
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
                    }

                    break; // only one DxOptionalParameterAttribute expected
                }

                parameterDetails.Add((fieldType, fieldName, isOptional, defaultExpr));
                constructorBody.AppendLine($"{Indent}{Indent}    this.{fieldName} = {fieldName};");
            }

            for (int i = 0; i < parameterDetails.Count; i++)
            {
                (string Type, string Name, bool IsOptional, string DefaultExpr) p =
                    parameterDetails[i];
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

            // Close containers
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
                    "// <auto-generated by DxAutoGenConstructorGenerator/>",
                    "#pragma warning disable",
                    "#nullable enable annotations",
                    string.Empty,
                    namespaceBlockOpen,
                    $"{containersOpen}{innerIndent}{typeAccessibility} partial {typeKind} {typeName}",
                    $"{innerIndent}{{",
                    $"{Indent}    /// <summary>",
                    $"{Indent}    /// Auto-generated constructor by DxAutoGenConstructorGenerator.",
                    $"{Indent}    /// </summary>",
                    $"{innerIndent}    {constructorAccessibility} {typeSymbol.Name}({constructorParams})",
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

        private static string FormatLiteral(object value, ITypeSymbol type)
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
                    return f.ToString("R", CultureInfo.InvariantCulture) + "f";
                case double d:
                    return d.ToString("R", CultureInfo.InvariantCulture);
            }

            // Fallback: use ToString(), but this should not occur for attribute constants
            return value.ToString() ?? "default";
        }

        private static bool IsReferenceOrNullable(ITypeSymbol type)
        {
            if (type.IsReferenceType)
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
            Compilation compilation,
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
                return IsReferenceOrNullable(field.Type);
            }

            try
            {
                var exprSyntax = SyntaxFactory.ParseExpression(trimmed);
                var typeInfo = semanticModel.GetSpeculativeTypeInfo(
                    anchorPosition,
                    exprSyntax,
                    SpeculativeBindingOption.BindAsExpression
                );

                ITypeSymbol sourceType = typeInfo.Type;
                if (sourceType == null)
                {
                    // Could not bind; let the compiler decide but report as invalid here
                    return false;
                }

                Conversion conv = compilation.ClassifyConversion(sourceType, field.Type);
                return conv.IsImplicit;
            }
            catch
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
