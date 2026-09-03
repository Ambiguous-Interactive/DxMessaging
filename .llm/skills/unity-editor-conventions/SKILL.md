---
name: unity-editor-conventions
description: "Unity-side contracts for DxMessaging: MessageAwareComponent base calls and diagnostics, the package-owned editor design system, safe consumer source migration commands, and the devcontainer cache contract. Use when subclassing MessageAwareComponent, adding a guarded lifecycle method, styling or testing an editor window or inspector, writing a source upgrade command under Assets, or changing a devcontainer cache mount."
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
- Adding an Editor migration command that rewrites consumer source under `Assets`.
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

### Consumer source migration tools

- Limit automatic rewrites to consumer-owned C# files under `Assets`. Never rewrite package,
  cache, generated, or project-settings content unless the migration explicitly owns it. Exclude
  conventional generated paths and suffixes and inspect source headers for generation markers.
- Mask comments, strings, and character literals before recognizing C# syntax. Do not run a broad
  text replacement over source files. Restrict each rewrite to named APIs and syntax shapes whose
  semantics the tool can prove.
- Analyze every file before writing, show the file and replacement counts, and require confirmation.
  Preserve BOM/encoding and line endings. Apply the batch transactionally and restore all changed
  files if any write fails. Fingerprint both the previewed input and the tool-written output so a
  concurrent user edit is never overwritten during replacement or rollback.
- Skip and report ambiguous constructs such as overloads, callback declarations outside the
  current file, qualified callbacks, or direct same-file callbacks shared by mutable and readonly
  APIs. Tell users to review project-wide callback uses because text analysis cannot prove that a
  same-file declaration is not referenced from another file.
- Add focused tests for positive forms and for adjacent syntax that must remain unchanged. When
  callback mutability differs by API, pin readonly handlers separately from mutable interceptors
  and emission calls. Include idempotency and line-ending coverage.
- If CI promotes Roslynator diagnostics to errors, scan migrated concrete `in` callbacks for
  RCS1242. Use `readonly struct` only for already-immutable messages, never mutable interceptors.

### Devcontainer cache contract

- `.devcontainer/cache-contract.sh` is the single source of truth for six index-aligned named
  volumes: NuGet, .NET tools, PowerShell, pip, npm, and workspace `node_modules`.
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

| Document                                                                      | Purpose                                                                                                                   |
| ----------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| [base-call-contract.md](./references/base-call-contract.md)                   | Guarded methods and their runtime consequences, the five enforcement layers, opt-outs, and how to add a guarded method    |
| [devcontainer-cache-contract.md](./references/devcontainer-cache-contract.md) | The six named-volume mounts, volume ownership rule, workspace-root derivation, validator blocks, and add/remove procedure |
| [editor-design-system.md](./references/editor-design-system.md)               | Theme loader and shared class rules, editor tool constraints, and editor-window test and screenshot-capture rules         |
