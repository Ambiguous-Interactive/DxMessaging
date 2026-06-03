---
title: "Inspector Overlay Invariants for MessageAwareComponent"
id: "inspector-overlay-invariants"
category: "testing"
version: "1.0.0"
created: "2026-04-30"
updated: "2026-04-30"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Editor/CustomEditors/MessageAwareComponentFallbackEditor.cs"
    - path: "Editor/CustomEditors/MessageAwareComponentInspectorOverlay.cs"
    - path: "Tests/Editor/MessageAwareComponentFallbackEditorTests.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "editor"
  - "inspector"
  - "custom-editor"
  - "unity"
  - "regression"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of Unity's IMGUI Layout/Repaint cycle and CustomEditor selection rules."

impact:
  performance:
    rating: "low"
    details: "Invariants are about correctness in the editor; no runtime cost."
  maintainability:
    rating: "high"
    details: "Three concrete invariants prevent silent regressions in the inspector overlay."
  testability:
    rating: "high"
    details: "Each invariant has an explicit guard test or documented source comment."

prerequisites:
  - "Familiarity with Unity's CustomEditor attribute and IMGUI."
  - "Familiarity with Editor.finishedDefaultHeaderGUI."

dependencies:
  packages: []
  skills: []

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Fallback editor invariants"
  - "Inspector overlay rules"

related:
  - "test-base-class-cleanup"
  - "test-failure-investigation"

status: "stable"
---

# Inspector Overlay Invariants for MessageAwareComponent

> **One-line summary**: Three invariants keep `MessageAwareComponentFallbackEditor` and `MessageAwareComponentInspectorOverlay` from corrupting Unity's inspector layout cache or producing visible regressions like an empty vertical gap below the component header.

## Overview

The DxMessaging inspector overlay is split across two cooperating pieces:

- `MessageAwareComponentFallbackEditor` -- a primary (non-fallback) `[CustomEditor]` registered for `MessageAwareComponent` and every subclass via `editorForChildClasses: true`. A user-defined `[CustomEditor]` for a specific subclass still wins precedence; this editor handles the rest.
- `MessageAwareComponentInspectorOverlay` -- a static class that hooks `Editor.finishedDefaultHeaderGUI` (the header path) and is also called from inside the fallback editor's `OnInspectorGUI` (the body path).

Three invariants must hold simultaneously. Breaking any one of them causes a visible bug or layout corruption.

## Invariant 1: register as PRIMARY (`isFallback = false`) and draw the default inspector body

`MessageAwareComponentFallbackEditor` must be a primary (non-fallback) `[CustomEditor]` whose `OnInspectorGUI` body matches Unity's `GenericInspector` exactly:

```csharp
[CustomEditor(typeof(MessageAwareComponent), true)]
[CanEditMultipleObjects]
public sealed class MessageAwareComponentFallbackEditor : Editor
{
    public override void OnInspectorGUI()
    {
        MessageAwareComponentInspectorOverlay.RenderInsideOnInspectorGUI(target);
        DrawDefaultInspector();
    }
}
```

**Why primary, not fallback**: With `isFallback = true`, Unity prefers `GenericInspector` whenever it can -- and on Unity 2021, `Editor.finishedDefaultHeaderGUI` does not reliably fire for `MonoBehaviour` subclasses that have no registered `[CustomEditor]`. The combination causes the missing-base-call HelpBox to vanish entirely on Unity 2021 (and on any Unity version where the header hook is not reliable for the inspected type). Registering as a primary editor guarantees the warning surfaces on every supported Unity version because we render the HelpBox directly from `OnInspectorGUI`.

**Why `DrawDefaultInspector()` and not a manual `SerializedObject` walk that skips `m_Script`**: Unity's `GenericInspector` (and `DrawDefaultInspector`) draws a disabled "Script" row beneath the component header for every `MonoBehaviour`. Skipping `m_Script` in a custom body -- under the (incorrect) assumption that Unity already draws the script reference in the title bar -- leaves the row blank and produces a visible empty vertical gap below the header for subclasses with no `[SerializeField]` fields. Calling `DrawDefaultInspector()` instead makes the body byte-for-byte identical to `GenericInspector`, eliminating the gap.

**Why `editorForChildClasses: true`**: The warning HelpBox must surface for every `MessageAwareComponent` subclass, not only the abstract base. Unity's selection still prefers a more-specific user-defined `[CustomEditor(typeof(MySubclass))]` over our `editorForChildClasses` registration, so user editors continue to win precedence; the header-hook overlay surfaces the warning above the user's editor in that case.

**Earlier rejected approaches and why they failed**:

- `isFallback = true` (via reflection on `m_IsFallback`): the field is named `isFallback` (no `m_` prefix) and is public, so reflection on the `m_IsFallback` name returned null and emitted a runtime warning on every domain reload. Even after fixing the field-name bug and assigning the public field directly, `isFallback = true` regressed the warning surface as described above.
- Static-constructor reflection mutation on a single attribute instance: `Type.GetCustomAttributes` constructs a _fresh_ attribute instance on every call (the .NET runtime caches the metadata, not the instances). The static ctor mutated one instance, and Unity's `CustomEditorAttributes.Rebuild()` later asked for its own copy and saw an attribute with the default value because nothing had run the mutation on that copy.
- Subclass `CustomEditor` attribute that sets `isFallback` in its own constructor: structurally valid (`Type.GetCustomAttributes<CustomEditor>(false)` returns subclass instances too), but inherits the same regression as the direct-assignment approach above -- `isFallback = true` is the wrong design for this editor.

**Regression test**:

- `Tests/Editor/MessageAwareComponentFallbackEditorTests.cs::FallbackEditorMustRegisterAsPrimaryNonFallbackEditorForChildClasses` -- asserts `customEditor.isFallback == false` and `editorForChildClasses == true`. If a future contributor sets `isFallback = true` (regressing to a broken state), the test fails immediately with the regression context in its message.

## Invariant 2: `BuildAndRenderOverlay` emits ZERO `EditorGUILayout` calls when `shape == 0`

`MessageAwareComponentInspectorOverlay.BuildAndRenderOverlay` performs all gating up-front. When `shape == 0` (nothing to render), it must `return false` before any `EditorGUILayout.*` call.

**Why**: When called from inside `OnInspectorGUI`, Unity invokes the editor twice per frame (`EventType.Layout` then `EventType.Repaint`). Both passes must emit identical control counts; a single stray `EditorGUILayout.LabelField` on only one pass corrupts Unity's layout cache and prevents adjacent components from rendering.

The current source enforces this: see the comment in `Editor/CustomEditors/MessageAwareComponentInspectorOverlay.cs` that begins with `"Render nothing" branch`.

**Regression coverage**: `Tests/Editor/MessageAwareComponentFallbackEditorTests.cs::FallbackEditorBodyDoesNotEmitVisibleHelpBoxWhenOverlayDisabled` (no-throw assertion when overlay is gated off).

## Invariant 3: `RenderInsideOnInspectorGUI` is event-type-agnostic

`MessageAwareComponentInspectorOverlay.RenderInsideOnInspectorGUI` (called from `OnInspectorGUI`) must NOT gate on `Event.current.type` and must NOT latch per-Repaint. Cross-path dedupe with the header hook is handled inside `DrawHeader` by an unconditional skip when `editor is MessageAwareComponentFallbackEditor`.

**Why**: Inside an editor body, Unity runs both Layout and Repaint passes. Latching or event-type gating would produce different control counts on each pass and corrupt the inspector layout cache for the entire window.

The current source enforces this: see the XML doc comment on `MessageAwareComponentInspectorOverlay` ("Layout/Repaint control-count invariant") and the dedicated comment block on `RenderInsideOnInspectorGUI`.

## Practical Notes

- Tests that exercise the fallback editor should set `DxMessagingSettings.GetOrCreateSettings()._baseCallCheckEnabled = false` in setup (and restore in teardown). This forces `BuildAndRenderOverlay` to early-return via the gating phase, so previous-session report data cannot make `shape != 0`.
- Subclasses used in tests must be top-level `internal` types (not nested private), because Unity cannot serialize private nested `MonoBehaviour` types through domain reload. Mark them `[AddComponentMenu("")]` to keep them out of the picker.
- `CustomEditor.isFallback` is a public field -- read it directly (no reflection). `CustomEditor.m_EditorForChildClasses` is `internal` with no public getter, so reflection is required to read that one.

## When to Revisit

The invariants above depend on Unity-internal behavior. Revisit if any of these change:

- Unity renames or removes the public `CustomEditor.isFallback` field. The regression test reads it directly, so a rename would surface as a compile error in the test (immediate, build-time signal).
- Unity renames `CustomEditor.m_EditorForChildClasses`. The reflection-based read in the test will return null and the test will fail loudly with the guidance message embedded in the assertion ("Unity may have renamed the field; update this test").
- Unity changes its `MonoBehaviour` inspector body so that the disabled "Script" row is no longer drawn by `DrawDefaultInspector()`. In that case the empty-row would no longer occupy the slot, and an empty subclass might briefly show a gap until we re-render an equivalent placeholder. Update `OnInspectorGUI` to match the new default behavior.
- Unity changes the `Editor.finishedDefaultHeaderGUI` semantics on Unity 2021 so the hook reliably fires for unregistered MonoBehaviours. At that point `isFallback = true` becomes a viable alternative architecture and would let `GenericInspector` handle our subclasses while the header hook still surfaces the warning. Re-evaluate the trade-off at that time.
- `MessageAwareComponentInspectorOverlay.BuildAndRenderOverlay` is restructured so that gating decisions intermix with `EditorGUILayout` calls. Re-establish the up-front gating phase before any layout call.

## See Also

- [MessageAwareComponent Base-Call Contract](../unity/base-call-contract.md) -- the analyzer + IL-scanner contract whose output the inspector overlay renders.
- `Editor/CustomEditors/MessageAwareComponentFallbackEditor.cs` -- fallback editor source and XML doc.
- `Editor/CustomEditors/MessageAwareComponentInspectorOverlay.cs` -- overlay source with full Layout/Repaint invariant comments.
- `Tests/Editor/MessageAwareComponentFallbackEditorTests.cs` -- regression tests.
