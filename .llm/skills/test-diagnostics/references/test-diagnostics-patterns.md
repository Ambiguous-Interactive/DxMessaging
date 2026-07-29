# Test Diagnostics Patterns

> **One-line summary**: Toggleable diagnostics and edge-case markers for test debugging.

## Overview

This skill documents toggleable diagnostics and edge-case markers.

## Solution

Apply the patterns below to collect targeted diagnostics safely.

### Toggleable Diagnostics Pattern

```csharp
namespace WallstopStudios.UnityHelpers.Diagnostics
{
    using UnityEngine;

    /// <summary>
    /// Zero-overhead diagnostics with name filtering.
    /// </summary>
    internal static class EditorDiagnostics
    {
        /// <summary>
        /// Enable/disable all diagnostics.
        /// </summary>
        internal static bool Enabled { get; set; } = false;

        /// <summary>
        /// Only log for components matching this filter (null = all).
        /// </summary>
        internal static string NameFilter { get; set; }

        private const string LogPrefix = "[Diagnostics] ";

        private static bool ShouldLog(string name)
        {
            if (!Enabled) return false;

            if (!string.IsNullOrEmpty(NameFilter))
            {
                if (name == null) return false;
                if (name.IndexOf(NameFilter, System.StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return true;
        }

        internal static void Log(string name, string message)
        {
            if (!ShouldLog(name)) return;
            Debug.Log($"{LogPrefix}[{name}] {message}");
        }

        internal static void LogFormat(string name, string format, params object[] args)
        {
            if (!ShouldLog(name)) return;
            Debug.LogFormat($"{LogPrefix}[{name}] {format}", args);
        }

        internal static void LogStateChange(string name, string from, string to)
        {
            if (!ShouldLog(name)) return;
            Debug.Log($"{LogPrefix}[{name}] State: {from} -> {to}");
        }

        internal static void LogMethodEntry(string name, string method, string parameters = null)
        {
            if (!ShouldLog(name)) return;
            string msg = string.IsNullOrEmpty(parameters)
                ? $"-> {method}()"
                : $"-> {method}({parameters})";
            Debug.Log($"{LogPrefix}[{name}] {msg}");
        }

        internal static void LogMethodExit(string name, string method, object result = null)
        {
            if (!ShouldLog(name)) return;
            string msg = result == null
                ? $"<- {method}()"
                : $"<- {method}() = {result}";
            Debug.Log($"{LogPrefix}[{name}] {msg}");
        }
    }
}
```

### Intentional Edge Case Markers

```csharp
/// <summary>
/// Marker comments for test linter to ignore intentional patterns.
/// </summary>
public static class TestMarkers
{
    // Comment pattern: // UNH-SUPPRESS: <reason>
    //
    // Used when tests intentionally do things that would trigger warnings:
    // - DestroyImmediate in tests
    // - Null access testing
    // - Exception testing
}

// Usage in tests:
[Test]
public void GetGameObjectHandlesDestroyedComponent()
{
    GameObject go = CreateGameObject("Test");
    SpriteRenderer sr = go.AddComponent<SpriteRenderer>();

    // UNH-SUPPRESS: Test verifies behavior after intentional destruction
    Object.DestroyImmediate(sr);

    // Should handle destroyed component gracefully
    GameObject result = sr.GetGameObject();
    Assert.IsTrue(result == null);
}

[Test]
public void HandleNullGracefully()
{
    // UNH-SUPPRESS: Intentional null testing
    string nullString = null;

    Assert.DoesNotThrow(() => processor.Process(nullString));
}
```
