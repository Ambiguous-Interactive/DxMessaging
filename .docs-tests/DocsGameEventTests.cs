using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

/// <summary>Exercise mutation and subscription ownership in the actual GameEvent example.</summary>
/// <remarks>2026-11-04: Reverse live-list iteration can repeat callbacks after another listener leaves.</remarks>
[TestFixture]
internal sealed class DocsGameEventTests
{
    private AssemblyLoadContext _load = null!;
    private Type _eventType = null!;
    private Type _bridgeType = null!;
    private Type _callbackType = null!;
    private MethodInfo _register = null!;
    private MethodInfo _unregister = null!;
    private MethodInfo _raise = null!;
    private MethodInfo _enable = null!;
    private MethodInfo _disable = null!;
    private FieldInfo _asset = null!;
    private FieldInfo _listeners = null!;
    private FieldInfo _emissions = null!;

    [OneTimeSetUp]
    public void CompileActualDocumentedClasses()
    {
        string path = Path.Combine(
            DocsSnippetCompilationTests.ResolveRepoRoot(),
            "docs/guides/patterns.md"
        );
        string[] samples = DocsSnippetCompilationTests
            .ExtractCodeBlocks(path, "csharp")
            .Where(code =>
                code.Contains("class GameEvent :", StringComparison.Ordinal)
                && code.Contains("EventBridge : MonoBehaviour", StringComparison.Ordinal)
            )
            .ToArray();
        Assert.That(samples, Has.Length.EqualTo(1), "Bind the actual channel and bridge fence.");
        ClassDeclarationSyntax[] classes = CSharpSyntaxTree
            .ParseText(samples[0])
            .GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(type =>
                type.Identifier.ValueText == "GameEvent"
                || type.Identifier.ValueText.EndsWith("EventBridge", StringComparison.Ordinal)
            )
            .ToArray();
        Assert.That(classes, Has.Length.EqualTo(2), "Execute the two actual documented classes.");
        ClassDeclarationSyntax bridge = classes.Single(type =>
            type.Identifier.ValueText != "GameEvent"
        );
        Assert.That(
            bridge.Members.OfType<ConstructorDeclarationSyntax>(),
            Is.Empty,
            "Only the fixture constructor assigns the serialized field for strict standalone compilation."
        );
        classes[Array.IndexOf(classes, bridge)] = bridge.AddMembers(
            SyntaxFactory.ParseMemberDeclaration(
                "public "
                    + bridge.Identifier.ValueText
                    + "(GameEvent initialEvent) { onSceneTransitionSO = initialEvent; }"
            )!
        );
        string source =
            "using System.Collections.Generic; using UnityEngine; using UnityEngine.Events;\n"
            + string.Join("\n", classes.Select(type => type.ToFullString()))
            + """
                namespace UnityEngine
                {
                    public class ScriptableObject { }
                    public class MonoBehaviour { }
                    public sealed class SerializeField : System.Attribute { }
                    public sealed class CreateAssetMenuAttribute : System.Attribute
                    { public string menuName; }
                }
                namespace UnityEngine.Events { public delegate void UnityAction(); }
                public struct SceneTransitionRequested { }
                public static class EmissionFixture
                {
                    public static int Count;
                    public static void Emit(this ref SceneTransitionRequested message) { Count++; }
                }
                """;
        string[] references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(
            Path.PathSeparator
        );
        CSharpCompilation compilation = CSharpCompilation.Create(
            "GameEvent_" + Guid.NewGuid().ToString("N"),
            new[]
            {
                CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp9)),
            },
            references.Select(reference => MetadataReference.CreateFromFile(reference)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));
        _load = new AssemblyLoadContext("GameEventExample", isCollectible: true);
        image.Position = 0;
        Assembly assembly = _load.LoadFromStream(image);
        _eventType = assembly.GetType("GameEvent", throwOnError: true)!;
        _bridgeType = assembly.GetType(bridge.Identifier.ValueText, throwOnError: true)!;
        _callbackType = assembly.GetType("UnityEngine.Events.UnityAction", throwOnError: true)!;
        _register = _eventType.GetMethod("RegisterListener")!;
        _unregister = _eventType.GetMethod("UnregisterListener")!;
        _raise = _eventType.GetMethod("Raise")!;
        const BindingFlags members =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _enable = _bridgeType.GetMethod("OnEnable", members)!;
        _disable = _bridgeType.GetMethod("OnDisable", members)!;
        _asset = _bridgeType.GetField("onSceneTransitionSO", members)!;
        _listeners = _eventType.GetField("listeners", members)!;
        _emissions = assembly.GetType("EmissionFixture")!.GetField("Count")!;
    }

    [OneTimeTearDown]
    public void ReleaseCompiledFixture()
    {
        _load?.Unload();
    }

    private object NewEvent() => Activator.CreateInstance(_eventType)!;

    private Delegate Callback(Action action) =>
        Delegate.CreateDelegate(_callbackType, action.Target, action.Method);

    private void Register(object publisher, Delegate? callback) =>
        _register.Invoke(publisher, new object?[] { callback });

    private void Remove(object publisher, Delegate? callback) =>
        _unregister.Invoke(publisher, new object?[] { callback });

    private void Raise(object publisher) => _raise.Invoke(publisher, null);

    private int Count(object publisher) =>
        ((System.Collections.ICollection)_listeners.GetValue(publisher)!).Count;

    [TestCase("None")]
    [TestCase("Empty")]
    [TestCase("SelfRemove")]
    [TestCase("RemoveEarlier")]
    [TestCase("RemoveMiddle")]
    [TestCase("RemoveVisited")]
    [TestCase("Add")]
    [TestCase("Duplicates")]
    [TestCase("Null")]
    [TestCase("Readd")]
    [TestCase("Nested")]
    [TestCase("Throw")]
    public void ChannelUsesEntrySnapshotAndPreservesItsOtherSemantics(string mutation)
    {
        object publisher = NewEvent();
        List<string> calls = new();
        Dictionary<string, Delegate> callbacks = new();
        bool changed = false;
        InvalidOperationException failure = new("Expected callback failure.");
        callbacks["A"] = Callback(() =>
        {
            calls.Add("A");
            if (mutation == "RemoveVisited" && !changed)
            {
                changed = true;
                Remove(publisher, callbacks["C"]);
            }
        });
        callbacks["B"] = Callback(() => calls.Add("B"));
        callbacks["D"] = Callback(() => calls.Add("D"));
        callbacks["C"] = Callback(() =>
        {
            calls.Add("C");
            if (changed || mutation == "RemoveVisited")
            {
                return;
            }
            changed = true;
            switch (mutation)
            {
                case "SelfRemove":
                    Remove(publisher, callbacks["C"]);
                    break;
                case "RemoveEarlier":
                    Remove(publisher, callbacks["A"]);
                    break;
                case "RemoveMiddle":
                    Remove(publisher, callbacks["B"]);
                    break;
                case "Add":
                    Register(publisher, callbacks["D"]);
                    break;
                case "Readd":
                    Remove(publisher, callbacks["A"]);
                    Register(publisher, callbacks["A"]);
                    break;
                case "Nested":
                    Remove(publisher, callbacks["A"]);
                    Raise(publisher);
                    break;
                case "Throw":
                    throw failure;
            }
        });
        if (mutation != "Empty")
        {
            Register(publisher, callbacks["A"]);
            if (mutation == "Null")
            {
                Register(publisher, null);
            }
            Register(publisher, callbacks["B"]);
            if (mutation == "Duplicates")
            {
                Register(publisher, callbacks["A"]);
                Remove(publisher, callbacks["A"]);
            }
            else if (mutation != "Null")
            {
                Register(publisher, callbacks["C"]);
            }
        }
        Exception? observed = null;
        try
        {
            Raise(publisher);
        }
        catch (TargetInvocationException exception)
        {
            observed = exception.InnerException;
        }
        string[] first = calls.ToArray();
        calls.Clear();
        Raise(publisher);
        string[] second = calls.ToArray();
        foreach (Delegate callback in callbacks.Values)
        {
            Remove(publisher, callback);
        }
        Remove(publisher, null);
        string[] expectedFirst = mutation switch
        {
            "Empty" => Array.Empty<string>(),
            "Duplicates" => new[] { "A", "B" },
            "Null" => new[] { "B", "A" },
            "Nested" => new[] { "C", "C", "B", "B", "A" },
            "Throw" => new[] { "C" },
            _ => new[] { "C", "B", "A" },
        };
        string[] expectedSecond = mutation switch
        {
            "Empty" => Array.Empty<string>(),
            "Duplicates" => new[] { "A", "B" },
            "Null" or "SelfRemove" or "RemoveVisited" => new[] { "B", "A" },
            "RemoveEarlier" or "Nested" => new[] { "C", "B" },
            "RemoveMiddle" => new[] { "C", "A" },
            "Add" => new[] { "D", "C", "B", "A" },
            "Readd" => new[] { "A", "C", "B" },
            _ => new[] { "C", "B", "A" },
        };
        Assert.Multiple(() =>
        {
            Assert.That(
                observed,
                mutation == "Throw" ? Is.SameAs(failure) : Is.Null,
                mutation + ": propagate the original callback exception only."
            );
            Assert.That(
                first,
                Is.EqualTo(expectedFirst),
                mutation + ": exact first Raise snapshot."
            );
            Assert.That(second, Is.EqualTo(expectedSecond), mutation + ": next Raise sees edits.");
            Assert.That(Count(publisher), Is.Zero, mutation + ": all listeners remain removable.");
        });
    }

    [TestCase("Disable")]
    [TestCase("Same")]
    [TestCase("Other")]
    [TestCase("RepeatEnable")]
    public void BridgeRejectsCapturedCallbacksFromAnInvalidatedSubscription(string transition)
    {
        object original = NewEvent();
        object replacement = NewEvent();
        object bridge = Activator.CreateInstance(_bridgeType, new[] { original })!;
        _emissions.SetValue(null, 0);
        _enable.Invoke(bridge, null);
        int triggers = 0;
        bool changed = false;
        Delegate trigger = Callback(() =>
        {
            triggers++;
            if (changed)
            {
                return;
            }
            changed = true;
            if (transition != "RepeatEnable")
            {
                _disable.Invoke(bridge, null);
            }
            if (transition == "Other")
            {
                _asset.SetValue(bridge, replacement);
            }
            if (transition != "Disable")
            {
                _enable.Invoke(bridge, null);
            }
        });
        Register(original, trigger);
        Raise(original);
        int firstTriggers = triggers;
        int firstEmissions = (int)_emissions.GetValue(null)!;
        Raise(original);
        int oldPublisherEmissions = (int)_emissions.GetValue(null)! - firstEmissions;
        if (transition == "Other")
        {
            Raise(replacement);
        }
        int subsequentEmissions = (int)_emissions.GetValue(null)! - firstEmissions;
        _disable.Invoke(bridge, null);
        _disable.Invoke(bridge, null);
        Remove(original, trigger);
        int beforeCleanupProbe = (int)_emissions.GetValue(null)!;
        Raise(original);
        Raise(replacement);
        Assert.Multiple(() =>
        {
            Assert.That(firstTriggers, Is.EqualTo(1), transition + ": one current trigger.");
            Assert.That(firstEmissions, Is.Zero, transition + ": old subscription cannot forward.");
            Assert.That(
                oldPublisherEmissions,
                Is.EqualTo(transition is "Same" or "RepeatEnable" ? 1 : 0),
                transition + ": only the current publisher owns later delivery."
            );
            Assert.That(
                subsequentEmissions,
                Is.EqualTo(transition == "Disable" ? 0 : 1),
                transition + ": the new subscription remains usable."
            );
            Assert.That(Count(original), Is.Zero, transition + ": original listener cleanup.");
            Assert.That(
                Count(replacement),
                Is.Zero,
                transition + ": replacement listener cleanup."
            );
            Assert.That(
                _emissions.GetValue(null),
                Is.EqualTo(beforeCleanupProbe),
                transition + ": teardown prevents later forwarding."
            );
        });
    }
}
