namespace WallstopStudios.DxMessaging.SourceGenerators.Analyzers
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>Checks Unity callbacks for owned messaging lifecycle fields.</summary>
    /// <remarks>
    /// Follows source forwarding methods and base calls. It does not prove reachability,
    /// arbitrary aliases, interface dispatch, or lifecycle work in referenced binaries.
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MessagingLifecycleAnalyzer : DiagnosticAnalyzer
    {
        internal const string DiagnosticId = "DXMSG015";
        private static readonly string[] LifecycleNames =
        {
            "Awake",
            "OnEnable",
            "OnDisable",
            "OnDestroy",
        };
        private static readonly string[] ForwardNames =
        {
            "CreateLifecycleBound",
            "Enable",
            "Disable",
            "Dispose",
        };
        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            "Missing messaging lifecycle forwarding",
            "'{0}' must forward {1} to its '{2}' messaging lifecycle ({3})",
            "DxMessaging",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/docs/reference/analyzers.md#dxmsg015-missing-messaging-lifecycle-forwarding",
            customTags: "CompilationEnd"
        );

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                INamedTypeSymbol? binding = start.Compilation.GetTypeByMetadataName(
                    "DxMessaging.Unity.MessagingLifecycle"
                );
                INamedTypeSymbol? component = start.Compilation.GetTypeByMetadataName(
                    "DxMessaging.Unity.MessagingComponent"
                );
                INamedTypeSymbol? behaviour = start.Compilation.GetTypeByMetadataName(
                    "UnityEngine.MonoBehaviour"
                );
                if (binding == null || component == null || behaviour == null)
                {
                    return;
                }
                ConcurrentDictionary<SyntaxTree, SemanticModel> models = new();
                ConcurrentDictionary<INamedTypeSymbol, byte> types = new(
                    SymbolEqualityComparer.Default
                );
                start.RegisterSemanticModelAction(model =>
                {
                    _ = models.TryAdd(model.SemanticModel.SyntaxTree, model.SemanticModel);
                });
                start.RegisterSymbolAction(
                    symbol =>
                    {
                        if (symbol.Symbol is INamedTypeSymbol type)
                        {
                            _ = types.TryAdd(type, 0);
                        }
                    },
                    SymbolKind.NamedType
                );
                start.RegisterCompilationEndAction(end =>
                {
                    foreach (KeyValuePair<INamedTypeSymbol, byte> entry in types)
                    {
                        Analyze(end, entry.Key, binding, component, behaviour, models);
                    }
                });
            });
        }

        /// <summary>Checks each owned field against the most specific Unity callbacks and inherited forwarding chain.</summary>
        private static void Analyze(
            CompilationAnalysisContext context,
            INamedTypeSymbol type,
            INamedTypeSymbol binding,
            INamedTypeSymbol component,
            INamedTypeSymbol behaviour,
            ConcurrentDictionary<SyntaxTree, SemanticModel> models
        )
        {
            if (type.TypeKind != TypeKind.Class || !DerivesFrom(type, behaviour))
            {
                return;
            }
            for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                foreach (ISymbol member in current.GetMembers())
                {
                    if (
                        member is not IFieldSymbol field
                        || field.IsStatic
                        || field.IsImplicitlyDeclared
                        || !SymbolEqualityComparer.Default.Equals(field.Type, binding)
                    )
                    {
                        continue;
                    }
                    for (int index = 0; index < LifecycleNames.Length; ++index)
                    {
                        IMethodSymbol? method = FindLifecycle(type, LifecycleNames[index]);
                        HashSet<ISymbol> visited = new(SymbolEqualityComparer.Default);
                        if (
                            method != null
                            && Forwards(
                                context,
                                method,
                                field,
                                binding,
                                component,
                                index,
                                visited,
                                models
                            )
                        )
                        {
                            continue;
                        }
                        Location? location =
                            method?.Locations.Length != 0
                            && method?.ContainingType.Equals(type, SymbolEqualityComparer.Default)
                                == true
                                ? method.Locations[0]
                            : type.Locations.Length == 0 ? null
                            : type.Locations[0];
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                Rule,
                                location,
                                type.Name,
                                LifecycleNames[index],
                                field.Name,
                                ForwardNames[index]
                            )
                        );
                    }
                }
            }
        }

        /// <summary>Finds the Unity MonoBehaviour base without matching unrelated same-named types.</summary>
        private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol parent)
        {
            for (
                INamedTypeSymbol? current = type.BaseType;
                current != null;
                current = current.BaseType
            )
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, parent))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Selects the callback Unity resolves before falling back to inherited source methods.</summary>
        private static IMethodSymbol? FindLifecycle(INamedTypeSymbol type, string name)
        {
            for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
            {
                foreach (ISymbol member in current.GetMembers(name))
                {
                    if (
                        member is IMethodSymbol method
                        && !method.IsStatic
                        && method.Parameters.Length == 0
                        && method.ReturnsVoid
                    )
                    {
                        return method;
                    }
                }
            }
            return null;
        }

        /// <summary>Follows source callback execution while preserving the captured field and operation identity.</summary>
        private static bool Forwards(
            CompilationAnalysisContext context,
            IMethodSymbol method,
            IFieldSymbol field,
            INamedTypeSymbol binding,
            INamedTypeSymbol component,
            int index,
            HashSet<ISymbol> visited,
            ConcurrentDictionary<SyntaxTree, SemanticModel> models
        )
        {
            method = method.OriginalDefinition;
            if (!visited.Add(method))
            {
                return false;
            }
            foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                SyntaxNode syntax = reference.GetSyntax(context.CancellationToken);
                SyntaxNode? body = syntax switch
                {
                    MethodDeclarationSyntax declaration => (SyntaxNode?)declaration.Body
                        ?? declaration.ExpressionBody?.Expression,
                    LocalFunctionStatementSyntax declaration => (SyntaxNode?)declaration.Body
                        ?? declaration.ExpressionBody?.Expression,
                    _ => null,
                };
                if (body == null)
                {
                    continue;
                }
                if (!models.TryGetValue(syntax.SyntaxTree, out SemanticModel model))
                {
                    continue;
                }
                foreach (
                    SyntaxNode node in body.DescendantNodesAndSelf(
                        descendIntoChildren: static child =>
                            child is not AnonymousFunctionExpressionSyntax
                            && child is not LocalFunctionStatementSyntax
                    )
                )
                {
                    if (
                        index == 0
                        && node is AssignmentExpressionSyntax assignment
                        && IsThisField(model, assignment.Left, field, context)
                    )
                    {
                        ExpressionSyntax value = assignment.Right;
                        while (value is ParenthesizedExpressionSyntax parentheses)
                        {
                            value = parentheses.Expression;
                        }
                        if (
                            value is InvocationExpressionSyntax creation
                            && model.GetSymbolInfo(creation, context.CancellationToken).Symbol
                                is IMethodSymbol factory
                            && factory.Name == ForwardNames[0]
                            && creation.ArgumentList.Arguments.Count == 1
                            && IsThis(creation.ArgumentList.Arguments[0].Expression)
                            && SymbolEqualityComparer.Default.Equals(
                                factory.ContainingType,
                                component
                            )
                        )
                        {
                            return true;
                        }
                    }
                    if (
                        node is not InvocationExpressionSyntax invocation
                        || model.GetSymbolInfo(invocation, context.CancellationToken).Symbol
                            is not IMethodSymbol target
                    )
                    {
                        continue;
                    }
                    if (
                        index != 0
                        && target.Name == ForwardNames[index]
                        && SymbolEqualityComparer.Default.Equals(target.ContainingType, binding)
                        && IsReceiver(model, invocation, field, context)
                    )
                    {
                        return true;
                    }
                    if (
                        IsThisCall(invocation)
                        && Forwards(
                            context,
                            target,
                            field,
                            binding,
                            component,
                            index,
                            visited,
                            models
                        )
                    )
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Checks direct and conditional calls against this listener's captured field.</summary>
        private static bool IsReceiver(
            SemanticModel model,
            InvocationExpressionSyntax invocation,
            IFieldSymbol field,
            CompilationAnalysisContext context
        )
        {
            ExpressionSyntax? receiver =
                invocation.Expression is MemberAccessExpressionSyntax member ? member.Expression
                : invocation.Parent is ConditionalAccessExpressionSyntax conditional
                    ? conditional.Expression
                : null;
            return receiver != null && IsThisField(model, receiver, field, context);
        }

        /// <summary>Rejects equal field symbols read through another listener instance.</summary>
        private static bool IsThisField(
            SemanticModel model,
            ExpressionSyntax expression,
            IFieldSymbol field,
            CompilationAnalysisContext context
        )
        {
            expression = Unwrap(expression);
            if (
                expression is MemberAccessExpressionSyntax member
                && !IsThis(member.Expression)
                && Unwrap(member.Expression) is not BaseExpressionSyntax
            )
            {
                return false;
            }
            return SameField(
                model.GetSymbolInfo(expression, context.CancellationToken).Symbol,
                field
            );
        }

        /// <summary>Follows explicit calls only when they execute on this listener instance.</summary>
        private static bool IsThisCall(InvocationExpressionSyntax invocation)
        {
            ExpressionSyntax expression = invocation.Expression;
            return expression is IdentifierNameSyntax
                || expression is GenericNameSyntax
                || expression is MemberAccessExpressionSyntax member
                    && (
                        IsThis(member.Expression)
                        || Unwrap(member.Expression) is BaseExpressionSyntax
                    );
        }

        /// <summary>Recognizes the current Unity owner without accepting another variable.</summary>
        private static bool IsThis(ExpressionSyntax expression)
        {
            return Unwrap(expression) is ThisExpressionSyntax;
        }

        /// <summary>Removes parentheses without treating casts or aliases as known receivers.</summary>
        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parentheses)
            {
                expression = parentheses.Expression;
            }
            return expression;
        }

        /// <summary>Compares original field definitions across constructed containing types.</summary>
        private static bool SameField(ISymbol? symbol, IFieldSymbol field)
        {
            return symbol is IFieldSymbol receiver
                && SymbolEqualityComparer.Default.Equals(
                    receiver.OriginalDefinition,
                    field.OriginalDefinition
                );
        }
    }
}
