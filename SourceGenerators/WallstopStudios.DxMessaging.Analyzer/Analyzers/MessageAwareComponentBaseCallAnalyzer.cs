namespace WallstopStudios.DxMessaging.SourceGenerators.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Flags subclasses of <c>DxMessaging.Unity.MessageAwareComponent</c> that override one of the
    /// guarded lifecycle methods (<c>Awake</c>, <c>OnEnable</c>, <c>OnDisable</c>, <c>OnDestroy</c>,
    /// <c>RegisterMessageHandlers</c>) without invoking the base implementation.
    /// </summary>
    /// <remarks>
    /// Detection is good-faith: any textual <c>base.&lt;name&gt;()</c> invocation anywhere inside the body
    /// (including expression-bodied form) counts as compliant. Reachability is not analyzed.
    /// <para>
    /// Severity for <c>RegisterMessageHandlers</c> is lowered to <see cref="DiagnosticSeverity.Info"/>
    /// when the same class also overrides <c>RegisterForStringMessages</c> to return the literal
    /// <c>false</c>; that is the documented intentional opt-out for the default string-message
    /// registrations. The diagnostic id remains <c>DXMSG006</c> so users can target it from
    /// <c>.editorconfig</c>; the lowered severity is achieved by reporting the diagnostic with an
    /// explicit effective severity via the <c>Diagnostic.Create(string id, ...)</c> overload, avoiding
    /// duplicate descriptor registrations for the same id.
    /// </para>
    /// </remarks>
    // Diagnostic catalog (DxMessaging); see docs/reference/analyzers.md for full details.
    // ----------------------------------------------------------------------------------
    // DXMSG002  Error    Multiple message attributes ([DxBroadcast/Targeted/Untargeted])
    //                    on a single type. Source: DxMessageIdGenerator.
    // DXMSG003  Warning  Type that needs source generation is nested inside non-partial
    //                    container(s). Source: both DxMessageIdGenerator and
    //                    DxAutoConstructorGenerator.
    // DXMSG004  Info     Companion suggestion to DXMSG003; add 'partial' to the named
    //                    container. Source: both generators.
    // DXMSG005  Error    [DxOptionalParameter] default expression is not a legal C# constant
    //                    for the field's type. Source: DxAutoConstructorGenerator.
    // DXMSG006  Warning  MessageAwareComponent override missing base call. Source:
    //                    MessageAwareComponentBaseCallAnalyzer.
    // DXMSG007  Warning  Guarded MessageAwareComponent method shadowed with 'new' instead
    //                    of 'override'. Source: MessageAwareComponentBaseCallAnalyzer.
    // DXMSG008  Info     Type/method opted out of the base-call check via
    //                    [DxIgnoreMissingBaseCall] or the project ignore list. Source:
    //                    MessageAwareComponentBaseCallAnalyzer.
    // DXMSG009  Warning  Method on a MessageAwareComponent subclass implicitly hides one of
    //                    the guarded lifecycle methods because it lacks 'override' or 'new'.
    //                    C# emits CS0114 for the same scenario; DXMSG009 is the project-
    //                    specific equivalent. Source: MessageAwareComponentBaseCallAnalyzer.
    // DXMSG010  Warning  This override correctly calls base.{method}(), but an intermediate
    //                    ancestor's override of the same method does not; the chain is broken
    //                    at the parent, so MessageAwareComponent's lifecycle work never runs
    //                    on this component. Source: MessageAwareComponentBaseCallAnalyzer.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MessageAwareComponentBaseCallAnalyzer : DiagnosticAnalyzer
    {
        internal const string MissingBaseCallDiagnosticId = "DXMSG006";
        internal const string NewModifierHidesGuardedMethodDiagnosticId = "DXMSG007";
        internal const string OptedOutOfBaseCallCheckDiagnosticId = "DXMSG008";
        internal const string MissingModifierDiagnosticId = "DXMSG009";
        internal const string BrokenChainDiagnosticId = "DXMSG010";

        private const string Category = "DxMessaging";

        private const string MessageAwareComponentFullName =
            "DxMessaging.Unity.MessageAwareComponent";

        private const string IgnoreAttributeFullName =
            "DxMessaging.Core.Attributes.DxIgnoreMissingBaseCallAttribute";

        private const string RegisterForStringMessagesPropertyName = "RegisterForStringMessages";

        private const string RegisterMessageHandlersMethodName = "RegisterMessageHandlers";

        private const string MissingBaseCallTitle =
            "Missing base call in MessageAwareComponent override";

        // G1: generic fallback retained as a safety net. The analyzer normally selects the
        // per-method consequence text from <see cref="MissingBaseCallMessageFormatsByMethod"/>;
        // if a future contributor adds a new guarded method without populating the dictionary the
        // generic format keeps the diagnostic intact (and the
        // <c>GuardedMethodListMatchesAllVirtualLifecycleMethodsOnPublicBaseClasses</c> meta-test
        // catches the omission so the slip cannot reach a release build).
        private const string MissingBaseCallMessageFormat =
            "'{0}' overrides MessageAwareComponent.{1} but does not call base.{1}(); the messaging system may not function correctly on this component.";

        private const string HelpLinkBase =
            "https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/docs/reference/analyzers.md#";

        internal static readonly ImmutableHashSet<string> GuardedMethodNames =
            ImmutableHashSet.Create(
                "Awake",
                "OnEnable",
                "OnDisable",
                "OnDestroy",
                "OnApplicationFocus",
                "OnApplicationPause",
                RegisterMessageHandlersMethodName
            );

        /// <summary>
        /// Guarded methods whose canonical Unity signature takes a single <c>bool</c>
        /// (<c>OnApplicationFocus(bool focused)</c>, <c>OnApplicationPause(bool paused)</c>).
        /// All other guarded methods are zero-argument. Used to relax the DXMSG009
        /// signature filter for these specific names so an implicit hide of the bool-arg
        /// variant is also surfaced. <see cref="MessageAwareComponent"/> does NOT currently
        /// declare these methods; they are guarded prospectively so that adding a virtual
        /// body in a future release immediately gets DXMSG006 / DXMSG010 coverage on
        /// existing subclasses without a separate analyzer revision.
        /// </summary>
        internal static readonly ImmutableHashSet<string> GuardedMethodsWithBoolSignature =
            ImmutableHashSet.Create("OnApplicationFocus", "OnApplicationPause");

        /// <summary>
        /// Lifecycle methods that <see cref="MessageAwareComponent"/> declares as virtual but whose
        /// base body is intentionally empty. Adding them to <see cref="GuardedMethodNames"/> would
        /// produce noise without protecting any real framework work; the meta-test
        /// <c>GuardedMethodListMatchesAllVirtualLifecycleMethodsOnPublicBaseClasses</c> uses this
        /// allow-list to assert that any other virtual lifecycle method MUST be guarded.
        /// </summary>
        internal static readonly ImmutableHashSet<string> AllowListIntentionallyUnguarded =
            ImmutableHashSet.Create("OnApplicationQuit");

        /// <summary>
        /// Per-method consequence text for DXMSG006. Each entry tells the user precisely what
        /// breaks at runtime when the corresponding base call is missing, so the diagnostic
        /// (and the inspector overlay HelpBox, which mirrors this dictionary in
        /// <c>BaseCallTypeScannerCore.MissingBaseCallMessageFormatsByMethod</c>) is actionable
        /// rather than generic. ASCII-only by policy. New guarded methods MUST add an entry here
        /// or the meta-test will fail.
        /// </summary>
        internal static readonly ImmutableDictionary<
            string,
            string
        > MissingBaseCallMessageFormatsByMethod = new[]
        {
            new KeyValuePair<string, string>(
                "Awake",
                "'{0}' overrides MessageAwareComponent.Awake but does not call base.Awake(); the message registration token will never be created and handlers cannot register."
            ),
            new KeyValuePair<string, string>(
                "OnEnable",
                "'{0}' overrides MessageAwareComponent.OnEnable but does not call base.OnEnable(); handlers will not be re-enabled when this component is enabled."
            ),
            new KeyValuePair<string, string>(
                "OnDisable",
                "'{0}' overrides MessageAwareComponent.OnDisable but does not call base.OnDisable(); handlers will not be disabled when this component is disabled, causing unwanted message processing."
            ),
            new KeyValuePair<string, string>(
                "OnDestroy",
                "'{0}' overrides MessageAwareComponent.OnDestroy but does not call base.OnDestroy(); handlers will not be deregistered and the registration token will not be released, causing a memory leak."
            ),
            new KeyValuePair<string, string>(
                RegisterMessageHandlersMethodName,
                "'{0}' overrides MessageAwareComponent.RegisterMessageHandlers but does not call base.RegisterMessageHandlers(); default string-message handlers will not be registered (override RegisterForStringMessages to suppress this warning)."
            ),
            // OnApplicationFocus / OnApplicationPause are guarded prospectively. MessageAwareComponent
            // does not currently declare these methods, so the analyzer never actually fires DXMSG006
            // for them today; the entries exist so that adding a virtual body to the base class in a
            // future release immediately produces actionable per-method consequence text without an
            // analyzer revision. The meta-test forces these dictionaries to mirror GuardedMethodNames.
            new KeyValuePair<string, string>(
                "OnApplicationFocus",
                "'{0}' overrides MessageAwareComponent.OnApplicationFocus but does not call base.OnApplicationFocus(); the messaging system may not function correctly on this component when focus changes."
            ),
            new KeyValuePair<string, string>(
                "OnApplicationPause",
                "'{0}' overrides MessageAwareComponent.OnApplicationPause but does not call base.OnApplicationPause(); the messaging system may not function correctly on this component when the application pauses."
            ),
        }.ToImmutableDictionary(StringComparer.Ordinal);

        private static readonly DiagnosticDescriptor MissingBaseCallDescriptor = new(
            id: MissingBaseCallDiagnosticId,
            title: MissingBaseCallTitle,
            messageFormat: MissingBaseCallMessageFormat,
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: HelpLinkBase + "dxmsg006"
        );

        private static readonly DiagnosticDescriptor NewModifierDescriptor = new(
            id: NewModifierHidesGuardedMethodDiagnosticId,
            title: "Unity lifecycle method hidden with 'new' instead of 'override'",
            messageFormat: "'{0}' hides MessageAwareComponent.{1} with 'new'; replace with 'override' and call base.{1}() so the messaging system continues to function.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: HelpLinkBase + "dxmsg007"
        );

        private static readonly DiagnosticDescriptor OptedOutDescriptor = new(
            id: OptedOutOfBaseCallCheckDiagnosticId,
            title: "Type opted out of MessageAwareComponent base-call check",
            messageFormat: "'{0}' is excluded from the DxMessaging base-call check ({1}).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            helpLinkUri: HelpLinkBase + "dxmsg008"
        );

        private static readonly DiagnosticDescriptor MissingModifierDescriptor = new(
            id: MissingModifierDiagnosticId,
            title: "Method implicitly hides MessageAwareComponent lifecycle method",
            messageFormat: "'{0}' declares {1} without 'override' or 'new'; this implicitly hides MessageAwareComponent.{1} (CS0114) and the messaging system will not function. Add 'override' and call base.{1}(), or add 'new' if the hiding is intentional.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "A subclass of MessageAwareComponent declared a method matching one of the guarded lifecycle names (Awake, OnEnable, OnDisable, OnDestroy, RegisterMessageHandlers) without 'override' or 'new'. C# treats this as implicit hiding (CS0114); the base method never runs and the messaging system will not function.",
            helpLinkUri: HelpLinkBase + "dxmsg009"
        );

        private static readonly DiagnosticDescriptor BrokenChainDescriptor = new(
            id: BrokenChainDiagnosticId,
            title: "base.{method}() chains into an override that does not reach MessageAwareComponent",
            messageFormat: "'{0}' calls base.{1}() but the inherited override on '{2}' does not chain to MessageAwareComponent.{1}; the messaging system will not function correctly on this component.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "An override on this class correctly invokes base.X(), but the parent class's override of the same method does not itself call base; the chain is broken at the parent, so MessageAwareComponent's lifecycle work never runs. Fix the parent override to call base, OR override directly from MessageAwareComponent here, OR suppress with [DxIgnoreMissingBaseCall] if the broken chain is intentional.",
            helpLinkUri: HelpLinkBase + "dxmsg010"
        );

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(
                MissingBaseCallDescriptor,
                NewModifierDescriptor,
                OptedOutDescriptor,
                MissingModifierDescriptor,
                BrokenChainDescriptor
            );

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(
                AnalyzeMethodDeclaration,
                SyntaxKind.MethodDeclaration
            );
        }

        private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context)
        {
            // K. Defensive cast; protects against future syntax-kind reuse.
            if (context.Node is not MethodDeclarationSyntax methodDecl)
            {
                return;
            }
            string methodName = methodDecl.Identifier.ValueText;
            if (!GuardedMethodNames.Contains(methodName))
            {
                return;
            }

            if (
                context.SemanticModel.GetDeclaredSymbol(methodDecl, context.CancellationToken)
                is not IMethodSymbol methodSymbol
            )
            {
                return;
            }

            INamedTypeSymbol containingType = methodSymbol.ContainingType;
            if (containingType is null)
            {
                return;
            }

            // Only flag classes that strictly inherit from MessageAwareComponent. The base class
            // itself (and unrelated types) are never flagged.
            if (!StrictlyInheritsFromMessageAwareComponent(containingType))
            {
                return;
            }

            bool hasNewModifier = methodDecl.Modifiers.Any(static m =>
                m.IsKind(SyntaxKind.NewKeyword)
            );
            bool hasOverrideModifier = methodDecl.Modifiers.Any(static m =>
                m.IsKind(SyntaxKind.OverrideKeyword)
            );
            bool hasStaticModifier = methodDecl.Modifiers.Any(static m =>
                m.IsKind(SyntaxKind.StaticKeyword)
            );

            // DXMSG009: when neither 'override' nor 'new' is present, C# treats the method as
            // implicit hiding of the base lifecycle method (compiler emits CS0114). Fire only when
            // the signature shape actually matches a Unity lifecycle method; parameter-less, void,
            // non-static, non-generic; so unrelated overloads like `void OnEnable(int)`,
            // unrelated static helpers, and `void Awake<T>()` (which coexists with the base method
            // because of differing generic arity and does not trigger CS0114) all stay silent.
            // Special case: OnApplicationFocus / OnApplicationPause have a canonical Unity
            // signature `(bool)`; for those names we accept either zero parameters OR exactly one
            // bool parameter so an implicit hide of the bool variant also surfaces.
            bool signatureMatchesLifecycleShape =
                methodSymbol.ReturnsVoid
                && !methodSymbol.IsGenericMethod
                && (
                    methodSymbol.Parameters.Length == 0
                    || (
                        GuardedMethodsWithBoolSignature.Contains(methodName)
                        && methodSymbol.Parameters.Length == 1
                        && methodSymbol.Parameters[0].Type.SpecialType == SpecialType.System_Boolean
                    )
                );
            bool wouldFireMissingModifier =
                !hasNewModifier
                && !hasOverrideModifier
                && !hasStaticModifier
                && signatureMatchesLifecycleShape;

            // Bail when this method does not match any of our diagnostic shapes. This protects
            // unrelated methods on subclasses (e.g., a private helper named `Awake` that takes a
            // parameter, or a static factory) from producing noise; including DXMSG008 on
            // opted-out classes.
            if (!hasNewModifier && !hasOverrideModifier && !wouldFireMissingModifier)
            {
                return;
            }

            // Pre-compute would-have-fired flags so the opt-out branches can avoid emitting
            // DXMSG008 on clean overrides; pure noise per the adversarial review (B5). The
            // override / new / missing-modifier branches are mutually exclusive at the C# language
            // level (a method cannot have both `override` and `new`, and `wouldFireMissingModifier`
            // requires neither).
            bool wouldFireNewModifier = hasNewModifier;
            bool wouldFireMissingBase =
                hasOverrideModifier && !ContainsBaseInvocation(methodDecl, methodName);

            // Pre-compute the DXMSG010 (broken transitive chain) check. Only relevant when this
            // method IS an override AND base.X() IS present syntactically; otherwise DXMSG006
            // already fires on this method and DXMSG010 would be redundant noise on the same
            // location. We compute it here so the opt-out branches can lower it to DXMSG008 too.
            IMethodSymbol brokenChainAncestor = null;
            bool wouldFireBrokenChain =
                hasOverrideModifier
                && !wouldFireMissingBase
                && !ChainReachesMessageAwareComponent(
                    methodSymbol,
                    methodName,
                    out brokenChainAncestor
                );

            // Opt-out via attribute on the method or the class. We still want the user to see that
            // the suppression is active during build, so we emit DXMSG008 (Info) when bailing;
            // BUT only when there is something we would have actually reported.
            if (HasIgnoreAttribute(methodSymbol) || HasIgnoreAttribute(containingType))
            {
                if (
                    wouldFireMissingBase
                    || wouldFireNewModifier
                    || wouldFireMissingModifier
                    || wouldFireBrokenChain
                )
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            OptedOutDescriptor,
                            methodDecl.Identifier.GetLocation(),
                            containingType.ToDisplayString(),
                            "[DxIgnoreMissingBaseCall]"
                        )
                    );
                }
                return;
            }

            // Opt-out via project-wide ignore list (sidecar AdditionalFile).
            ImmutableHashSet<string> ignoreList = IgnoreListReader.Load(
                context.Options,
                context.CancellationToken
            );
            string fullyQualifiedTypeName = containingType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
                    SymbolDisplayGlobalNamespaceStyle.Omitted
                )
            );
            if (ignoreList.Contains(fullyQualifiedTypeName))
            {
                if (
                    wouldFireMissingBase
                    || wouldFireNewModifier
                    || wouldFireMissingModifier
                    || wouldFireBrokenChain
                )
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            OptedOutDescriptor,
                            methodDecl.Identifier.GetLocation(),
                            containingType.ToDisplayString(),
                            IgnoreListReader.IgnoreFileName
                        )
                    );
                }
                return;
            }

            if (wouldFireMissingModifier)
            {
                // Implicit hiding; C# would emit CS0114 alongside this. We surface a project-
                // specific diagnostic so the inspector overlay (which scopes to DXMSG006/007/009)
                // also shows the warning above the user's component.
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        MissingModifierDescriptor,
                        methodDecl.Identifier.GetLocation(),
                        containingType.ToDisplayString(),
                        methodName
                    )
                );
                return;
            }

            if (hasNewModifier)
            {
                // 'new' on a guarded name is a known footgun: the user is hiding the lifecycle
                // method instead of participating in the override chain. Stop after reporting.
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        NewModifierDescriptor,
                        methodDecl.Identifier.GetLocation(),
                        containingType.ToDisplayString(),
                        methodName
                    )
                );
                return;
            }

            // From here on we know hasOverrideModifier is true.
            // I. base.X() inside a lambda or local function still counts as compliant per the
            // good-faith policy; covered by `BaseCallInsideLocalFunctionIsAcceptedAsGoodFaith`.
            if (!wouldFireMissingBase)
            {
                // DXMSG010: base.X() IS present syntactically, but the inherited override on an
                // intermediate ancestor itself fails to chain to MessageAwareComponent. The chain
                // is broken at some ancestor and the messaging system is dead on this component
                // even though THIS override looks correct in isolation.
                if (wouldFireBrokenChain)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            BrokenChainDescriptor,
                            methodDecl.Identifier.GetLocation(),
                            containingType.ToDisplayString(),
                            methodName,
                            brokenChainAncestor.ContainingType.ToDisplayString()
                        )
                    );
                }
                return;
            }

            string typeDisplay = containingType.ToDisplayString();
            Location location = methodDecl.Identifier.GetLocation();

            // G1: select per-method consequence text. The dictionary's keys are the same set as
            // GuardedMethodNames, but we fall back to the generic format defensively so that
            // adding a guarded method without populating the dictionary still produces a usable
            // (if generic) diagnostic. The meta-test forces the dictionary to stay aligned.
            string consequenceFormat = MissingBaseCallMessageFormatsByMethod.TryGetValue(
                methodName,
                out string perMethodFormat
            )
                ? perMethodFormat
                : MissingBaseCallMessageFormat;
            string formattedMessage = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                consequenceFormat,
                typeDisplay,
                methodName
            );

            // Smart-case: lower DXMSG006 to Info when the class also overrides
            // RegisterForStringMessages and that override returns the literal `false`.
            // We keep the id stable as DXMSG006 by constructing the lowered Diagnostic via the
            // string-id overload of Diagnostic.Create, which lets us specify an effective severity
            // without registering a duplicate descriptor for the same id.
            if (
                string.Equals(methodName, RegisterMessageHandlersMethodName)
                && ClassOverridesRegisterForStringMessagesAsFalse(containingType)
            )
            {
                Diagnostic loweredDiagnostic = Diagnostic.Create(
                    id: MissingBaseCallDiagnosticId,
                    category: Category,
                    message: formattedMessage,
                    severity: DiagnosticSeverity.Info,
                    defaultSeverity: DiagnosticSeverity.Warning,
                    isEnabledByDefault: true,
                    warningLevel: 1,
                    title: MissingBaseCallTitle,
                    description: null,
                    helpLink: HelpLinkBase + "dxmsg006",
                    location: location,
                    additionalLocations: null,
                    customTags: null
                );
                context.ReportDiagnostic(loweredDiagnostic);
                return;
            }

            // Standard DXMSG006 path; emit with the per-method consequence message. We call
            // Diagnostic.Create with the descriptor only for the metadata (id, category,
            // severity, help link), and supply the formatted message explicitly via the
            // string-id overload so our per-method wording reaches the consumer verbatim.
            Diagnostic perMethodDiagnostic = Diagnostic.Create(
                id: MissingBaseCallDiagnosticId,
                category: Category,
                message: formattedMessage,
                severity: DiagnosticSeverity.Warning,
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                warningLevel: 1,
                title: MissingBaseCallTitle,
                description: null,
                helpLink: HelpLinkBase + "dxmsg006",
                location: location,
                additionalLocations: null,
                customTags: null
            );
            context.ReportDiagnostic(perMethodDiagnostic);
        }

        private static bool StrictlyInheritsFromMessageAwareComponent(INamedTypeSymbol type)
        {
            INamedTypeSymbol current = type.BaseType;
            while (current is not null)
            {
                // OriginalDefinition normalizes constructed generics back to the open type for FQN comparison.
                INamedTypeSymbol normalized = current.OriginalDefinition;
                if (
                    string.Equals(
                        normalized.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
                                SymbolDisplayGlobalNamespaceStyle.Omitted
                            )
                        ),
                        MessageAwareComponentFullName,
                        System.StringComparison.Ordinal
                    )
                )
                {
                    return true;
                }
                current = current.BaseType;
            }

            return false;
        }

        private static bool HasIgnoreAttribute(ISymbol symbol)
        {
            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                INamedTypeSymbol attrClass = attribute.AttributeClass;
                if (attrClass is null)
                {
                    continue;
                }

                if (
                    string.Equals(
                        attrClass.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
                                SymbolDisplayGlobalNamespaceStyle.Omitted
                            )
                        ),
                        IgnoreAttributeFullName,
                        System.StringComparison.Ordinal
                    )
                )
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Good-faith textual base-call detector. Returns <c>true</c> when any
        /// <c>InvocationExpressionSyntax</c> anywhere inside <paramref name="method"/>'s body
        /// targets <c>base.&lt;methodName&gt;(...)</c> -- including invocations nested inside
        /// lambdas or local functions (<c>DescendantNodes()</c> walks both).
        /// </summary>
        /// <remarks>
        /// We deliberately do NOT analyze reachability or data-flow -- a single textual
        /// <c>base.X()</c> call is treated as compliant. The known false-positive shape
        /// (helper-indirection: an override that delegates to a private method that itself
        /// calls <c>base.X()</c>) is documented and tested; users can suppress those with
        /// <c>[DxIgnoreMissingBaseCall]</c>. See the
        /// <c>BaseCallInsideLocalFunctionIsAcceptedAsGoodFaith</c> and
        /// <c>HelperIndirectionFalsePositiveStillFires</c> tests for the policy edges.
        /// </remarks>
        private static bool ContainsBaseInvocation(
            MethodDeclarationSyntax method,
            string methodName
        )
        {
            IEnumerable<InvocationExpressionSyntax> invocations = method
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>();

            foreach (InvocationExpressionSyntax invocation in invocations)
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                {
                    continue;
                }

                if (memberAccess.Expression is not BaseExpressionSyntax)
                {
                    continue;
                }

                if (
                    string.Equals(
                        memberAccess.Name.Identifier.ValueText,
                        methodName,
                        System.StringComparison.Ordinal
                    )
                )
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Walks <paramref name="methodSymbol"/>'s inheritance chain (via
        /// <see cref="IMethodSymbol.OverriddenMethod"/>). Returns <c>true</c> if every override
        /// along the way calls <c>base.{methodName}()</c> and the chain terminates at
        /// <c>MessageAwareComponent</c>. Returns <c>false</c> (with <paramref name="firstBrokenLink"/>
        /// set to the closest broken ancestor whose source we can read) if any intermediate
        /// override fails to chain.
        /// </summary>
        /// <remarks>
        /// Cross-assembly assume-clean: if any ancestor has no <c>DeclaringSyntaxReferences</c>
        /// (e.g., the parent type lives in a binary-only third-party package), we cannot inspect
        /// its body, so we trust it. Emitting DXMSG010 against a type the user can't edit would
        /// be unactionable.
        /// <para>
        /// Cycle defense: visited <c>HashSet</c> on <see cref="IMethodSymbol"/> via
        /// <see cref="SymbolEqualityComparer.Default"/>. Real C# override chains cannot cycle, but
        /// defensive code keeps the analyzer from infinite-looping if a malformed compilation
        /// surfaces a malformed symbol.
        /// </para>
        /// <para>
        /// Known limitation: this reuses <see cref="ContainsBaseInvocation"/> -- the same good-faith
        /// textual check DXMSG006 itself uses. If an ancestor's body literally contains
        /// <c>base.X()</c> after a <c>return;</c> (unreachable), the chain check will still
        /// consider it clean, mirroring DXMSG006's policy. This is documented as acceptable: both
        /// diagnostics share a single textual policy so users get consistent results.
        /// </para>
        /// </remarks>
        private static bool ChainReachesMessageAwareComponent(
            IMethodSymbol methodSymbol,
            string methodName,
            out IMethodSymbol firstBrokenLink
        )
        {
            firstBrokenLink = null;
            HashSet<IMethodSymbol> visited = new(SymbolEqualityComparer.Default);
            IMethodSymbol cursor = methodSymbol.OverriddenMethod;
            while (cursor is not null && visited.Add(cursor))
            {
                INamedTypeSymbol containing = cursor.ContainingType?.OriginalDefinition;
                if (containing is null)
                {
                    return true;
                }

                // If we've reached MessageAwareComponent itself, the chain terminates correctly.
                string containingFqn = containing.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
                        SymbolDisplayGlobalNamespaceStyle.Omitted
                    )
                );
                if (
                    string.Equals(
                        containingFqn,
                        MessageAwareComponentFullName,
                        System.StringComparison.Ordinal
                    )
                )
                {
                    return true;
                }

                // Source-only walk: bail to assume-clean if we can't inspect the parent's body.
                ImmutableArray<SyntaxReference> refs = cursor.DeclaringSyntaxReferences;
                if (refs.IsDefaultOrEmpty)
                {
                    return true; // cross-assembly / compiler-only symbol; assume clean
                }

                bool ancestorCallsBase = false;
                foreach (SyntaxReference syntaxRef in refs)
                {
                    if (syntaxRef.GetSyntax() is not MethodDeclarationSyntax ancestorDecl)
                    {
                        continue;
                    }
                    if (ContainsBaseInvocation(ancestorDecl, methodName))
                    {
                        ancestorCallsBase = true;
                        break;
                    }
                }

                if (!ancestorCallsBase)
                {
                    firstBrokenLink = cursor;
                    return false;
                }

                cursor = cursor.OverriddenMethod;
            }

            // Walked off the top without hitting MessageAwareComponent; chain doesn't terminate
            // at MessageAwareComponent. This shouldn't normally happen (the
            // StrictlyInheritsFromMessageAwareComponent gate at function entry guarantees the
            // containing type does inherit from MAC), but if it does, treat as clean to avoid
            // false positives.
            return true;
        }

        /// <summary>
        /// Walks the containing type's inheritance chain (stopping at -- and excluding --
        /// <c>MessageAwareComponent</c>) looking for the most-derived override of
        /// <c>RegisterForStringMessages</c>. The most-derived override wins; if it returns
        /// unconditionally-literal <c>false</c>, the smart-case Info lowering applies. If a
        /// more-derived override returns anything other than literal <c>false</c>, the smart-case
        /// is NOT applied even if a less-derived override returns literal <c>false</c>.
        /// </summary>
        private static bool ClassOverridesRegisterForStringMessagesAsFalse(
            INamedTypeSymbol containingType
        )
        {
            INamedTypeSymbol current = containingType;
            while (current is not null)
            {
                // Stop walking once we've reached MessageAwareComponent itself; its virtual
                // declaration is not an override and shouldn't count.
                if (
                    string.Equals(
                        current.OriginalDefinition.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
                                SymbolDisplayGlobalNamespaceStyle.Omitted
                            )
                        ),
                        MessageAwareComponentFullName,
                        System.StringComparison.Ordinal
                    )
                )
                {
                    break;
                }

                foreach (
                    ISymbol member in current.GetMembers(RegisterForStringMessagesPropertyName)
                )
                {
                    if (member is not IPropertySymbol propertySymbol)
                    {
                        continue;
                    }

                    if (!propertySymbol.IsOverride)
                    {
                        continue;
                    }

                    // Found the most-derived override (because we walk derived -> base). Decide
                    // based on it; do not continue to less-derived overrides.
                    foreach (SyntaxReference syntaxRef in propertySymbol.DeclaringSyntaxReferences)
                    {
                        SyntaxNode syntax = syntaxRef.GetSyntax();
                        if (PropertyReturnsLiteralFalse(syntax))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                current = current.BaseType;
            }

            return false;
        }

        /// <summary>
        /// Returns <c>true</c> only when the property body unconditionally yields the literal
        /// <c>false</c> constant. Anything that introduces a conditional, a non-literal expression,
        /// or even one extra return statement returns <c>false</c> -- the smart-case Info lowering
        /// must be a high-confidence call (B3 in the adversarial review).
        /// </summary>
        private static bool PropertyReturnsLiteralFalse(SyntaxNode propertySyntax)
        {
            if (propertySyntax is not PropertyDeclarationSyntax property)
            {
                return false;
            }

            // Case 1: expression-bodied property:  protected override bool X => false;
            if (property.ExpressionBody is ArrowExpressionClauseSyntax arrow)
            {
                return IsFalseLiteral(arrow.Expression);
            }

            // Cases 2 and 3: a single getter accessor.
            if (property.AccessorList is null)
            {
                return false;
            }

            AccessorDeclarationSyntax getter = null;
            foreach (AccessorDeclarationSyntax accessor in property.AccessorList.Accessors)
            {
                if (accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                {
                    getter = accessor;
                    break;
                }
            }

            if (getter is null)
            {
                return false;
            }

            // Case 2: arrow-bodied getter:  get => false;
            if (getter.ExpressionBody is ArrowExpressionClauseSyntax getterArrow)
            {
                return IsFalseLiteral(getterArrow.Expression);
            }

            // Case 3: block-bodied getter; accept ONLY a single statement that is `return false;`
            // (no conditionals, no other statements). This avoids the false positive where any
            // branch happens to return false (e.g., `if (x) return false; return true;`).
            if (getter.Body is BlockSyntax block)
            {
                if (block.Statements.Count != 1)
                {
                    return false;
                }

                if (block.Statements[0] is not ReturnStatementSyntax returnStatement)
                {
                    return false;
                }

                return IsFalseLiteral(returnStatement.Expression);
            }

            return false;
        }

        private static bool IsFalseLiteral(ExpressionSyntax expression)
        {
            return expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.FalseLiteralExpression);
        }

        // I. Sentinel comment: see HelperIndirectionFalsePositiveStillFires plus
        // BaseCallInsideLocalFunctionIsAcceptedAsGoodFaith for the documented "good faith"
        // policy; any textual `base.X()` anywhere inside the override body (including local
        // functions / lambdas) counts as compliant; helper-indirection through a separate
        // method does not.
    }
}
