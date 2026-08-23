---
name: unity-editor-conventions
description: "Three Unity-side contracts for DxMessaging: the MessageAwareComponent base-call contract (Awake, OnEnable, OnDisable, OnDestroy, RegisterMessageHandlers must chain base.<method>(), enforced by DXMSG006-DXMSG010 plus an IL scanner, inspector overlay, runtime breadcrumb, and meta-test); the package-owned editor design system built on DxMessagingEditorTheme, UI Toolkit, and EditorWindowTestUtility; and the devcontainer named-volume cache contract in .devcontainer/cache-contract.sh. Use when subclassing MessageAwareComponent, adding a guarded lifecycle method, styling or testing an editor window or inspector, or adding/diagnosing a devcontainer cache mount."
metadata:
  category: "unity"
  tags: "unity, analyzer, lifecycle, diagnostics, messageawarecomponent, base-call, dxmsg006"
---

# Unity Editor Conventions

Three contracts that keep the Unity-facing side of the package honest: the lifecycle base-call
contract on `MessageAwareComponent`, the package-owned editor design system, and the
devcontainer cache mount contract.

## When to use

- Writing or reviewing a subclass of `MessageAwareComponent`, or adding a lifecycle method to
  the base class itself.
- Triaging DXMSG006 through DXMSG010, or "messages stop being received" with no exception.
- Styling, restyling, or testing a package editor window, inspector, or Project Settings page.
- Adding, removing, or debugging a devcontainer named-volume cache mount.

## Rules

### MessageAwareComponent base-call contract

- Five guarded methods carry framework work and MUST call `base.<method>()`: `Awake`,
  `OnEnable`, `OnDisable`, `OnDestroy`, `RegisterMessageHandlers`. Skipping the base call
  silently disables the framework work: no token created, handlers not re-enabled or disabled,
  token leaked on destroy, default string handlers unregistered.
- `OnApplicationFocus(bool)` and `OnApplicationPause(bool)` are guarded prospectively for their
  canonical one-arg-bool signature; the base class does not declare them today, and hide-based
  diagnostics (DXMSG007/DXMSG009) fire only when an ancestor actually declares a virtual member
  with a matching signature, so declaring either hook on a subclass stays silent.
- `OnApplicationQuit` is virtual but intentionally empty and lives on
  `AllowListIntentionallyUnguarded`.
- Five enforcement layers: the Roslyn analyzer
  `MessageAwareComponentBaseCallAnalyzer` (DXMSG006 missing base call, DXMSG007 `new`-modifier
  hide, DXMSG008 opt-out marker, DXMSG009 implicit hide, DXMSG010 transitive broken chain);
  the edit-time IL scanner `BaseCallTypeScanner` over Unity's `TypeCache`;
  `MessageAwareComponentInspectorOverlay`; a one-time `Debug.LogError` breadcrumb in Editor and
  Debug builds when the registration token is null; and a meta-test that parses
  `Runtime/Unity/MessageAwareComponent.cs`.
- Per-method diagnostic text lives in TWO places that must stay in sync:
  `MessageAwareComponentBaseCallAnalyzer.MissingBaseCallMessageFormatsByMethod` and
  `BaseCallTypeScannerCore.MissingBaseCallMessageFormatsByMethod` /
  `GetMissingBaseConsequenceLine(...)`.
- Adding a guarded method requires four aligned edits (analyzer `GuardedMethodNames`, analyzer
  consequence row, scanner `GuardedMethodNames` plus its consequence dictionary, and tests
  covering DXMSG006/DXMSG007/DXMSG009 plus the consequence text). The meta-test fails until all
  four land. An intentionally empty method goes on `AllowListIntentionallyUnguarded` with a
  `///` rationale.
- Every guarded-method lookup path in `BaseCallTypeScannerCore` must apply the same signature
  rules (declared method resolution, base-virtual detection, override-chain traversal,
  method-level `[DxIgnoreMissingBaseCall]` discovery). A bool fallback in only one path is a
  contract bug.
- Opt-outs: `[DxIgnoreMissingBaseCall]` at class or method scope, the ignore list on
  `Assets/Editor/DxMessagingSettings.asset` (mirrored to
  `Assets/Editor/DxMessaging.BaseCallIgnore.txt`), or an `.editorconfig` severity override.
  The first two emit DXMSG008 (Info) so the suppression stays visible.
- Smart case: a subclass that overrides `RegisterForStringMessages` to return the LITERAL
  `false` lowers DXMSG006 to Info on `RegisterMessageHandlers` only. Ternaries, `is false`
  patterns, and switch expressions do not qualify.

### Editor design system

- Production theme assets live in `Editor/Theme` and `Editor/Icons`;
  `DxMessagingEditorTheme` is the only loader for token USS, component USS, skin classes, and
  icons. Docs styling lives in `docs/stylesheets/extra.css`. Local `design-system*` folders are
  ignored inputs; tracked dumps must be deleted, and a repository test asserts none remain.
- Call `DxMessagingEditorTheme.Apply(root)` for inspector fragments and settings subtrees,
  `ApplyWindow(root)` for package editor window roots. Roots keep their tool-specific class
  names and add shared classes (`dx-window`, `dx-toolbar`, `dx-card`, `dx-btn-ghost`,
  `dx-tool-btn`, taxonomy badges). StyleSheet loading must be idempotent: repeat `Apply` calls
  must not duplicate `DxTokens.uss` or `DxMessagingTheme.uss`.
- Semantic accents use `DxMessagingEditorTheme.ApplyCompleteBorder` (thin complete borders).
  Left-only borders and left-edge rails are forbidden on cards, rows, sections, warnings,
  filter summaries, and lane groups. `DxMessagingEditorPalette` stays the C# mirror for IMGUI
  and dynamic route-kind tinting.
- Restyle the connected Inspector, Project Settings, Message Monitor, and Flow Graph; do not
  replace them with prototypes or demo data. Flow Graph stays hand-built UI Toolkit for Unity
  2021.3 compatibility: no `UnityEditor.Experimental.GraphView`, no Graph Toolkit. Use
  `CreateGUI` for windows and `CreateInspectorGUI` for inspectors, preserve
  `InspectorElement.FillDefaultInspector` parity, and keep existing IMGUI fallbacks working.
- Tests that need an attached UI Toolkit panel must use `EditorWindowTestUtility.CreateWindow()`
  / `ShowWindow()` / `CloseWindow()`, never a bare `EditorWindow` (Unity persists those into
  layout files and reopens them as "Failed to Load" tabs). Teardown calls
  `CloseTrackedWindows(...)`; `CloseLeakedEditorWindows()` is an explicit recovery sweep only,
  because the global `Resources.FindObjectsOfTypeAll` sweep can emit
  `Resolve of invalid GC handle` on Unity 6000.
- Prefer focused EditMode assertions over screenshots. Border tests assert all four sides share
  the same 1 px semantic border. Do not use
  `UnityEditorInternal.InternalEditorUtility.ReadScreenPixel` for documentation screenshots, and
  do not switch editor skins automatically during capture; start from a Personal/light editor,
  record `EditorGUIUtility.isProSkin` before capture, and inspect every artifact.
- `Samples~/Diagnostics Tooling Exerciser` is the canonical importable scene. Keep its scene,
  README, manifest entry, and `DiagnosticsToolingSampleContractTests` aligned when editor
  diagnostics change, and keep `DxMessagingSettingsProviderTests` aligned with the Inspector
  Checks controls.

### Devcontainer cache contract

- `.devcontainer/cache-contract.sh` is the single source of truth for five named volumes,
  aligned by array index: `dxm-nuget-cache` to `/home/vscode/.nuget`, `dxm-dotnet-tools` to
  `/home/vscode/.dotnet/tools`, `dxm-powershell-modules` to
  `/home/vscode/.local/share/powershell`, `dxm-python-cache` to `/home/vscode/.cache/pip`, and
  `dxm-node-modules` to `${CACHE_WORKSPACE_ROOT}/node_modules`.
- Docker stamps the target's owner UID/GID onto an empty volume on first attach, so the
  Dockerfile pre-creates each target as `vscode:vscode` and `post-start.sh` re-runs `chown`
  on every start.
- `CACHE_WORKSPACE_ROOT` resolves from `WORKSPACE_FOLDER`, else from the parent of the script's
  own directory. Never reintroduce a hardcoded absolute fallback.
- Adding a mount is a three-place edit in order: both arrays in `cache-contract.sh` at the same
  index (with a `dxm-` prefix), the matching `source=...,target=...,type=volume` entry in
  `devcontainer.json`, and an `install -d -o vscode -g vscode` line in the Dockerfile. Removing
  goes in the inverse order. Verify with `bash .devcontainer/validate-caching.sh`; a pre-commit
  hook re-runs it on `.devcontainer/` changes, and `devcontainer-test.yml` runs it in the image.
- The contract covers named volumes only. Bind mounts go straight into `devcontainer.json`, and
  Unity `Library/` is deliberately absent: local Unity verification runs on the host editor.

## References

| Document                                                                      | Purpose                                                                                                                    |
| ----------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| [base-call-contract.md](./references/base-call-contract.md)                   | Guarded methods and their runtime consequences, the five enforcement layers, opt-outs, and how to add a guarded method     |
| [devcontainer-cache-contract.md](./references/devcontainer-cache-contract.md) | The five named-volume mounts, volume ownership rule, workspace-root derivation, validator blocks, and add/remove procedure |
| [editor-design-system.md](./references/editor-design-system.md)               | Theme loader and shared class rules, editor tool constraints, and editor-window test and screenshot-capture rules          |
