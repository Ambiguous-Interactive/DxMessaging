using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

/// <remarks>
/// 2026-10-09: a file-wide network receiver-name exemption hid window closures
/// when another scope, lambda parameter, or anonymous member reused that name.
/// Bind the called method instead. Unresolved Close calls remain rejected.
/// </remarks>
[TestFixture]
internal sealed class EditorWindowCleanupTests
{
    private static readonly MetadataReference[] References = (
        (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("Runtime reference assemblies are unavailable.")
    )
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    private static IEnumerable<TestCaseData> ReceiverCases()
    {
        yield return Case("window", "EditorWindow window = new();", "window.Close();", true);
        yield return Case(
            "nullable",
            "DxMessagingMonitorWindow? monitor = new();",
            "monitor?.Close();",
            true
        );
        yield return Case("inferred", "var window = GetWindow();", "window.Close();", true);
        yield return Case(
            "array",
            "EditorWindow[] windows = { new() };",
            "windows[0].Close();",
            true
        );
        yield return Case("chained", "", "GetWindow().Close();", true);
        yield return Case(
            "member",
            "var owner = new { Window = GetWindow() };",
            "owner.Window.Close();",
            true
        );
        yield return Case("unknown-response", "", "Response.Close();", true);
        yield return Case("unknown-context", "", "owner.Context.Response.Close();", true);
        yield return Case(
            "mixed",
            "TcpClient Client = new(); var window = GetWindow();",
            "Client.Close(); window.Close();",
            true
        );
        yield return Case(
            "typed-shadow",
            "{ TcpClient client = new(); }",
            "{ EditorWindow client = GetWindow(); client.Close(); }",
            true
        );
        yield return Case(
            "inferred-shadow",
            "{ TcpClient client = new(); }",
            "{ var client = GetWindow(); client.Close(); }",
            true
        );
        yield return Case(
            "context-shadow",
            "{ HttpListenerContext ctx = null!; }",
            "{ WindowContext ctx = new(); ctx.Response.Close(); }",
            true
        );
        yield return Case(
            "lambda-shadow",
            "TcpClient Client = new();",
            "Action<EditorWindow> cleanup = Client => Client.Close();",
            true
        );
        yield return Case(
            "anonymous-shadow",
            "TcpClient Client = new();",
            "var owner = new { Client = GetWindow() }; owner.Client.Close();",
            true
        );
        yield return Case(
            "network",
            "TcpClient Client = new(); HttpListener Listener = new();",
            "Client.Close(); Listener.Close();",
            false
        );
        yield return Case(
            "network-member",
            "NetworkOwner owner = new();",
            "owner.Client.Close();",
            false
        );
        yield return Case(
            "network-context",
            "NetworkOwner owner = new();",
            "owner.Context.Response.Close();",
            false
        );
        yield return Case(
            "qualified-network",
            "System.Net.Sockets.TcpClient Client = new();",
            "Client.Close();",
            false
        );
        yield return Case(
            "network-alias",
            "NetworkClient Client = new();",
            "Client.Close();",
            false
        );
        yield return Case(
            "nullable-network",
            "TcpClient? Client = new();",
            "Client?.Close();",
            false
        );
        yield return Case(
            "custom-network-name",
            "Pretend.TcpClient Client = new();",
            "Client.Close();",
            true
        );
        yield return Case("whitespace", "EditorWindow window = new();", "window.Close ( );", true);
    }

    private static TestCaseData Case(
        string name,
        string declarations,
        string body,
        bool rejected
    ) => new TestCaseData(declarations, body, rejected).SetName($"CloseReceiver_{name}");

    [TestCaseSource(nameof(ReceiverCases))]
    public void CleanupRejectsWindowAndUnresolvedCalls(
        string declarations,
        string body,
        bool rejected
    )
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "using System; using System.Net; using System.Net.Sockets; using UnityEditor; "
                + "using NetworkClient = System.Net.Sockets.TcpClient; "
                + "namespace UnityEditor { class EditorWindow { public void Close() { } } } "
                + "namespace Pretend { class TcpClient { public void Close() { } } } "
                + "class DxMessagingMonitorWindow : EditorWindow { } "
                + "class WindowContext { public EditorWindow Response = new(); } "
                + "class NetworkOwner { public TcpClient Client = new(); public HttpListenerContext Context = null!; } "
                + "class Fixture { static EditorWindow GetWindow() => new(); void Run() { "
                + declarations
                + body
                + " } }"
        );
        CSharpCompilation compilation = Compile(new[] { tree });
        bool actual = RejectedCalls(compilation, tree).Any();
        Assert.That(actual, Is.EqualTo(rejected), $"declarations={declarations}; body={body}");
        if (
            !body.Contains("Response.Close();", StringComparison.Ordinal)
            || 0 < declarations.Length
        )
        {
            Assert.That(
                compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error),
                Is.Empty,
                "Known receiver controls must be valid C#; only the two explicit unresolved controls may lack context."
            );
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EditorSourcesUseTheOwnedWindowCleanup(bool newerEditorSymbols)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }
        Assert.That(directory, Is.Not.Null, "Repository root must be found.");
        string root = directory!.FullName;
        string[] symbols = newerEditorSymbols
            ? new[]
            {
                "UNITY_EDITOR",
                "UNITY_INCLUDE_TESTS",
                "DXM_621_PIPELINE_PRESENT",
                "UNITY_2021_3_OR_NEWER",
                "UNITY_2022_3_OR_NEWER",
                "UNITY_6000_0_OR_NEWER",
                "UNITY_6000_3_OR_NEWER",
                "UNITY_6000_4_OR_NEWER",
                "UNITY_6000_5_OR_NEWER",
            }
            : new[]
            {
                "UNITY_EDITOR",
                "UNITY_INCLUDE_TESTS",
                "DXM_621_PIPELINE_PRESENT",
                "UNITY_2021_3_OR_NEWER",
            };
        CSharpParseOptions options = new(preprocessorSymbols: symbols);
        SyntaxTree[] trees = Directory
            .EnumerateFiles(
                Path.Combine(root, "Tests", "Editor"),
                "*.cs",
                SearchOption.AllDirectories
            )
            .Where(path =>
                path != Path.Combine(root, "Tests", "Editor", "EditorWindowTestUtility.cs")
            )
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path))
            .ToArray();
        CSharpCompilation compilation = Compile(trees);
        string[] rejected = trees.SelectMany(tree => RejectedCalls(compilation, tree)).ToArray();
        Assert.That(
            rejected,
            Is.Empty,
            "Use EditorWindowTestUtility.CloseWindow(window); unresolved Close targets also fail closed."
        );
        Assert.That(
            trees
                .SelectMany(tree =>
                    tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                )
                .Count(IsCloseCall),
            Is.GreaterThan(0),
            "The admitted transport control must be active; an empty conditional source cannot qualify the guard."
        );
    }

    private static CSharpCompilation Compile(IEnumerable<SyntaxTree> trees) =>
        CSharpCompilation.Create(
            "EditorWindowCleanupGuard",
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

    private static bool IsCloseCall(InvocationExpressionSyntax call) =>
        call.ArgumentList.Arguments.Count == 0
        && call.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "Close",
            MemberBindingExpressionSyntax member => member.Name.Identifier.ValueText == "Close",
            _ => false,
        };

    private static IEnumerable<string> RejectedCalls(CSharpCompilation compilation, SyntaxTree tree)
    {
        SemanticModel model = compilation.GetSemanticModel(tree);
        foreach (
            InvocationExpressionSyntax call in tree.GetRoot()
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(IsCloseCall)
        )
        {
            IMethodSymbol? method = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
            string? type = method?.ContainingType.ToDisplayString();
            bool network =
                type
                    is "System.Net.Sockets.TcpClient"
                        or "System.Net.HttpListener"
                        or "System.Net.HttpListenerResponse"
                && method?.ContainingAssembly.Name
                    is "System.Net.Sockets"
                        or "System.Net.HttpListener";
            if (!network)
            {
                yield return $"{tree.FilePath}:{call.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {call}";
            }
        }
    }
}
