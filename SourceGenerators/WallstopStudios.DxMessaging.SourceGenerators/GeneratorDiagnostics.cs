namespace WallstopStudios.DxMessaging.SourceGenerators
{
    using Microsoft.CodeAnalysis;

    internal static class GeneratorDiagnostics
    {
        internal static readonly DiagnosticDescriptor NonPartialContainerDiagnostic = new(
            id: "DXMSG003",
            title: "Containing type must be partial for nested generation",
            messageFormat: "Type '{0}' is nested inside non-partial container(s): {1}. Suggested fix: add the 'partial' keyword to the containing type declaration(s).",
            category: "DxMessaging",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true
        );

        internal static readonly DiagnosticDescriptor AddPartialSuggestionDiagnostic = new(
            id: "DXMSG004",
            title: "Add 'partial' keyword to containing type",
            messageFormat: "Add 'partial' to the declaration of '{0}' to enable generation for nested type '{1}'",
            category: "DxMessaging",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true
        );
    }
}
