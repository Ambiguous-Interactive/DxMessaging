using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators.Analyzers;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
internal sealed class MessagingLifecycleAnalyzerTests
{
    private static readonly string[] ExpectedMissing = { "DXMSG015" };

    private const string Stubs = """
namespace UnityEngine { public class MonoBehaviour { } }
namespace DxMessaging.Core
{
    public sealed class MessageRegistrationToken
    {
        public void Enable() { }
        public void Disable() { }
        public void Dispose() { }
    }
}
namespace DxMessaging.Unity
{
    public sealed class MessagingLifecycle : System.IDisposable
    {
        public DxMessaging.Core.MessageRegistrationToken Token => null!;
        public bool IsDisposed => false;
        public void Enable() { }
        public void Disable() { }
        public void Dispose() { }
    }
    public sealed class MessagingComponent
    {
        public MessagingLifecycle CreateLifecycleBound(UnityEngine.MonoBehaviour owner) => new MessagingLifecycle();
    }
}
""";

    private const string Listener = """
public class Listener : UnityEngine.MonoBehaviour
{
    protected DxMessaging.Unity.MessagingLifecycle messages;
    private readonly DxMessaging.Unity.MessagingComponent component = new DxMessaging.Unity.MessagingComponent();
    protected virtual void Awake() { messages = component.CreateLifecycleBound(this); }
    protected virtual void OnEnable() { messages.Enable(); }
    protected virtual void OnDisable() { messages.Disable(); }
    protected virtual void OnDestroy() { messages.Dispose(); }
}
""";

    /// <summary>Checks that each missing callback produces one diagnostic naming the omitted operation.</summary>
    [TestCase("Awake", "messages = component.CreateLifecycleBound(this);")]
    [TestCase("OnEnable", "messages.Enable();")]
    [TestCase("OnDisable", "messages.Disable();")]
    [TestCase("OnDestroy", "messages.Dispose();")]
    public void MissingForwardingReportsItsLifecycleMethod(string method, string body)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Listener.Replace(body, string.Empty, StringComparison.Ordinal)
        );
        Assert.That(
            diagnostics.Select(d => d.Id),
            Is.EqualTo(ExpectedMissing),
            $"[{method}] Missing forwarding must report one diagnostic."
        );
        Assert.That(
            diagnostics[0].GetMessage(CultureInfo.InvariantCulture),
            Does.Contain(method),
            $"[{method}] Name the missing Unity lifecycle callback."
        );
    }

    /// <summary>Checks wrong operations and raw-token calls without accepting another lifecycle.</summary>
    [TestCase("OnEnable", "messages.Enable();", "messages.Disable();")]
    [TestCase("OnDisable", "messages.Disable();", "messages.Enable();")]
    [TestCase("OnDestroy", "messages.Dispose();", "messages.Disable();")]
    [TestCase("OnEnable", "messages.Enable();", "messages.Token.Enable();")]
    [TestCase("OnDestroy", "messages.Dispose();", "messages.Token.Dispose();")]
    [TestCase("OnEnable", "messages.Enable();", "component.CreateLifecycleBound(this).Enable();")]
    public void WrongForwardingCannotSatisfyTheOwnedField(
        string method,
        string oldBody,
        string newBody
    )
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            Listener.Replace(oldBody, newBody, StringComparison.Ordinal)
        );
        Assert.That(
            diagnostics.Select(d => d.Id),
            Is.EqualTo(ExpectedMissing),
            $"[{method}] Only the matching binding and operation may satisfy the callback."
        );
    }

    /// <summary>Checks both direct and null-conditional forwarding on the owned field.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public void CorrectDirectAndConditionalCallsAreClean(bool conditional)
    {
        string source = conditional
            ? Listener.Replace("messages.", "messages?.", StringComparison.Ordinal)
            : Listener;
        Assert.That(
            Analyze(source),
            Is.Empty,
            $"Conditional={conditional}: correct forwarding must remain clean."
        );
    }

    /// <summary>Checks callback forwarding through private source methods.</summary>
    [Test]
    public void PrivateForwardingMethodsAreFollowed()
    {
        string source = Listener
            .Replace("messages.Enable();", "EnableMessages();", StringComparison.Ordinal)
            .Replace(
                "messages = component.CreateLifecycleBound(this);",
                "InitializeMessages();",
                StringComparison.Ordinal
            )
            .Replace(
                "    protected virtual void Awake()",
                "    private void EnableMessages() { messages.Enable(); }\n    private void InitializeMessages() { messages = component.CreateLifecycleBound(this); }\n    protected virtual void Awake()",
                StringComparison.Ordinal
            );
        Assert.That(
            Analyze(source),
            Is.Empty,
            "Forwarding through a source method must preserve the same owned field."
        );
    }

    /// <summary>Checks that derived callbacks preserve the inherited forwarding chain.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public void InheritedForwardingRequiresTheBaseCall(bool broken)
    {
        string source =
            Listener
            + "\npublic sealed class Child : Listener { protected override void OnEnable() { "
            + (broken ? "" : "base.OnEnable();")
            + " } }";
        ImmutableArray<Diagnostic> diagnostics = Analyze(source);
        Assert.That(
            diagnostics.Length,
            Is.EqualTo(broken ? 1 : 0),
            "An inherited binding needs a complete forwarding chain."
        );
        if (broken)
        {
            Assert.That(
                diagnostics[0].Id,
                Is.EqualTo("DXMSG015"),
                "The helper-specific diagnostic must identify the broken chain."
            );
        }
    }

    /// <summary>Keeps ordinary managed helper owners outside Unity callback diagnostics.</summary>
    [Test]
    public void NonComponentOwnersAreOutsideUnityLifecycleAnalysis()
    {
        string source = Listener
            .Replace(" : UnityEngine.MonoBehaviour", "", StringComparison.Ordinal)
            .Replace(
                "component.CreateLifecycleBound(this)",
                "component.CreateLifecycleBound(null)",
                StringComparison.Ordinal
            );
        Assert.That(
            Analyze(source),
            Is.Empty,
            "Plain owners may manage bindings through their own lifecycle."
        );
    }

    /// <summary>Matches the production helper symbol rather than its short type name.</summary>
    [Test]
    public void AnUnrelatedSameNamedHelperDoesNotTriggerDiagnostics()
    {
        string source =
            "public class MessagingLifecycle { } public class Listener : UnityEngine.MonoBehaviour { public MessagingLifecycle messages; }";
        Assert.That(
            Analyze(source),
            Is.Empty,
            "Resolve the binding type symbol rather than matching its simple name."
        );
    }

    /// <summary>Rejects calls that belong to another receiver or an uninvoked function.</summary>
    [TestCase("messages.Enable();", "other.messages.Enable();")]
    [TestCase(
        "messages = component.CreateLifecycleBound(this);",
        "messages = component.CreateLifecycleBound(other);"
    )]
    [TestCase(
        "messages.Enable();",
        "System.Action unused = () => messages.Enable(); System.GC.KeepAlive(unused);"
    )]
    [TestCase(
        "messages.Enable();",
        "void Unused() { messages.Enable(); } System.Action unused = Unused; System.GC.KeepAlive(unused);"
    )]
    public void OtherReceiversAndUninvokedBodiesCannotSatisfyForwarding(
        string oldBody,
        string replacement
    )
    {
        string source = Listener
            .Replace(oldBody, replacement, StringComparison.Ordinal)
            .Replace(
                "    protected DxMessaging",
                "    public Listener other;\n    protected DxMessaging",
                StringComparison.Ordinal
            );
        Assert.That(
            Analyze(source).Select(d => d.Id),
            Is.EqualTo(ExpectedMissing),
            "Only a callback acting on this listener's owned lifecycle may satisfy forwarding."
        );
    }

    /// <summary>Compiles an isolated consumer and executes every registered production diagnostic analyzer.</summary>
    private static ImmutableArray<Diagnostic> Analyze(string source)
    {
        CSharpParseOptions parseOptions = new(LanguageVersion.CSharp9);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "LifecycleConsumer",
            new[]
            {
                CSharpSyntaxTree.ParseText(Stubs, parseOptions),
                CSharpSyntaxTree.ParseText(source, parseOptions),
            },
            GeneratorTestUtilities.CompilationReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        Assert.That(
            compilation
                .GetDiagnostics()
                .Where(d =>
                    d.Severity == DiagnosticSeverity.Error
                    || d.Severity == DiagnosticSeverity.Warning
                ),
            Is.Empty,
            "Analyzer inputs must compile without unrelated diagnostics."
        );
        ImmutableArray<DiagnosticAnalyzer> analyzers = typeof(MessageAwareComponentBaseCallAnalyzer)
            .Assembly.GetTypes()
            .Where(t =>
                !t.IsAbstract
                && typeof(DiagnosticAnalyzer).IsAssignableFrom(t)
                && t.GetCustomAttribute<DiagnosticAnalyzerAttribute>() != null
            )
            .Select(t => (DiagnosticAnalyzer)System.Activator.CreateInstance(t)!)
            .ToImmutableArray();
        ImmutableArray<Diagnostic> diagnostics = compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
        Assert.That(
            diagnostics.Where(d => d.Id == "AD0001"),
            Is.Empty,
            "No analyzer may fail while inspecting the fixture."
        );
        return diagnostics;
    }
}
