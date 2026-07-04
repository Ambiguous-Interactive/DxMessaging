---
title: "Unity Editor Design System"
id: "editor-design-system"
category: "unity"
version: "1.0.0"
created: "2026-07-02"
updated: "2026-07-03"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Editor/Theme"
    - path: "Editor/Icons"
    - path: "docs/stylesheets/extra.css"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "editor"
  - "ui-toolkit"
  - "design-system"

complexity:
  level: "basic"
  reasoning: "Most work is applying shared UI Toolkit styles while preserving existing editor-tool data contracts."

impact:
  performance:
    rating: "low"
    details: "Theme loading is editor-only and should be idempotent per VisualElement root."
  maintainability:
    rating: "high"
    details: "Keeps package editor styling in one tracked location."
  testability:
    rating: "high"
    details: "Theme loading, class contracts, and behavior parity are covered by focused EditMode tests."

prerequisites:
  - "UI Toolkit editor code built in CreateGUI or CreateInspectorGUI"
  - "Unity package root is Packages/com.wallstop-studios.dxmessaging"

dependencies:
  packages: []
  skills:
    - "mcp-test-loop"

applies_to:
  languages:
    - "C#"
    - "USS"
  frameworks:
    - "Unity UI Toolkit"
  versions:
    unity: ">=2021.3"

aliases:
  - "editor theme"
  - "DxMessagingEditorTheme"
  - "design system migration"

related:
  - "mcp-test-loop"
  - "base-call-contract"

status: "stable"
---

<!-- trigger: editor theme, design system, UI Toolkit styling, DxMessagingEditorTheme | Package-owned editor design system rules | Core -->

# Unity Editor Design System

## Source of Truth

- Production editor theme assets live in `Editor/Theme` and `Editor/Icons`.
- `DxMessagingEditorTheme` is the package-owned loader for token USS, component USS, skin classes, and editor icons.
- Documentation theme styling lives in `docs/stylesheets/extra.css`.
- Local `design-system*` folders are ignored source inputs only. They are not package or docs canonical output, and tracked dumps must be removed rather than shipped.
- Do not introduce a second UPM package for the design system. The package root is `Packages/com.wallstop-studios.dxmessaging`.
- `Samples~/Diagnostics Tooling Exerciser` is the canonical importable scene for
  exercising Message Monitor, Flow Graph, Inspector diagnostics, and Project
  Settings together. Keep its scene, README, package manifest entry, and
  `DiagnosticsToolingSampleContractTests` aligned when editor diagnostics change.

## Applying the Theme

- Call `DxMessagingEditorTheme.Apply(root)` for retained inspector fragments and settings subtrees.
- Call `DxMessagingEditorTheme.ApplyWindow(root)` for package-owned editor window roots.
- Roots should retain their tool-specific class names, then add shared classes such as `dx-window`, `dx-toolbar`, `dx-card`, `dx-btn-ghost`, `dx-tool-btn`, and taxonomy badge classes.
- StyleSheet loading must be idempotent. Repeated `Apply` calls must not duplicate `DxTokens.uss` or `DxMessagingTheme.uss`.
- Keep `DxMessagingEditorPalette` as the C# mirror for IMGUI, border colors, and dynamic route-kind tinting.
- Semantic UI Toolkit accents must use thin complete borders through `DxMessagingEditorTheme.ApplyCompleteBorder`. Do not use left-only borders or left-edge rails for cards, rows, sections, warnings, filter summaries, or lane groups.

## Editor Tool Rules

- Preserve existing data contracts and behavior. Restyle the connected Inspector, Project Settings, Message Monitor, and Flow Graph implementations; do not replace them with prototype windows or demo data.
- Flow Graph must remain hand-built UI Toolkit for Unity 2021.3 compatibility. Do not adopt `UnityEditor.Experimental.GraphView` or Graph Toolkit for this package.
- Use UI Toolkit editor entry points: `CreateGUI` for windows and `CreateInspectorGUI` for custom inspectors.
- Preserve default inspector parity with `InspectorElement.FillDefaultInspector`.
- Keep IMGUI fallback paths functional where they already exist.
- Project Settings > Wallstop Studios > DxMessaging > Inspector Checks must expose
  the base-call master toggle, console bridge toggle, and Ignored Base-Call Types
  list together. Keep the provider, screenshot manifest, analyzer docs, and
  `DxMessagingSettingsProviderTests` aligned when any of those controls change.

## Testing

- Prefer focused EditMode assertions over screenshots.
- Theme tests should verify `AssetDatabase.LoadAssetAtPath` for USS and icons, `Apply` class/style idempotency, and palette token parity.
- Tool tests should assert shared classes on roots, toolbars, rows, buttons, chips, cards, and warnings while retaining existing behavior assertions for filtering, export, selection, settings binding, and deferred mutations.
- Tests that verify taxonomy or route coloring should assert all four border sides share the same 1 px semantic border, not only a left-side color.
- Repository tests should assert no tracked `design-system*` dump files remain.
- Tests that need an attached UI Toolkit panel must use `EditorWindowTestUtility.CreateWindow()` / `ShowWindow()` / `CloseWindow()`. Do not show a generic `EditorWindow`; Unity can persist those into layout files and reopen them later as "Failed to Load" tabs after reloads or interrupted tests.
- Normal test teardown should call `EditorWindowTestUtility.CloseTrackedWindows(...)`, which closes fixture-tracked windows plus any package test-host windows registered by `CreateWindow()` without scanning every Unity object.
- A leak can survive as an orphan `DockArea` / `ContainerWindow` whose `actualView` is a Unity-null generic `UnityEditor.EditorWindow`; `GetWindows` and `FindObjectsOfTypeAll<EditorWindow>()` may miss it. Use `EditorWindowTestUtility.CloseLeakedEditorWindows()` only as an explicit recovery sweep after a suspected leak. On Unity 6000, running the global `Resources.FindObjectsOfTypeAll` sweep from every normal teardown can emit `Resolve of invalid GC handle` asserts from stale editor-package objects.
- When running visual smoke through Unity MCP, inspect open windows before and after and close `DxMessagingMessageMonitorWindow`, `DxMessagingFlowGraphWindow`, or `DxMessagingTestHostWindow` in cleanup.
- If failed-load windows are reported, check both live state and persisted layout state: `Unity_ManageEditor.GetWindows`, `Resources.FindObjectsOfTypeAll<EditorWindow>()`, and current/global layout files under `UserSettings/Layouts` and Unity editor preferences.
- MCP camera/scene captures are trustworthy for scene layout. Host editor-window
  screenshot automation must prove its capture source before it can replace
  tracked documentation PNGs.
- Do not use `UnityEditorInternal.InternalEditorUtility.ReadScreenPixel` for
  documentation screenshots on the current devcontainer/MCP topology. Probes on
  2026-07-03 captured the visible VS Code desktop instead of Unity even when
  MCP-reported Unity window coordinates were used.
- A 2026-07-03 synchronous Win32/GDI `PrintWindow` probe captured a separate
  Unity utility window with correct content once `GetDIBits` rows were flipped
  before PNG encoding. A later synchronous `PrintWindow` capture of the actual
  Project Settings window also captured the correct Unity content, but that
  target required the original row order and the host editor remained in
  Pro/dark skin. Treat row orientation as target-specific and visually inspect
  every artifact.
- Do not use live editor skin switching as part of automated docs capture in the
  current MCP topology. `InternalEditorUtility.SwitchSkinAndRepaintAllViews()`
  changed the host to Personal/light asynchronously, but a follow-up light-theme
  Project Settings `PrintWindow` capture timed out without writing an artifact
  and left Unity MCP unresponsive. Start from an editor that is already in
  Personal/light theme, record `EditorGUIUtility.isProSkin` and `UserSkin`
  before capture, and abort rather than switching skins automatically.
- The delayed callback variant stalled, and the path has not yet been proven for
  Inspector or menu cascade targets.
- Before overwriting tracked documentation PNGs, capture the actual target
  window in Personal/light theme, visually inspect the artifact for correct
  content/orientation/cropping, confirm Unity console errors are clean, confirm
  the post-capture editor skin state matches the pre-capture skin state, and
  confirm the post-capture editor window list contains no temporary capture
  windows.
