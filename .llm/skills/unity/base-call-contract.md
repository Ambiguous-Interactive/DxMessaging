---
title: "MessageAwareComponent Base-Call Contract"
id: "base-call-contract"
category: "unity"
version: "1.0.0"
created: "2026-05-02"
updated: "2026-05-03"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/Unity/MessageAwareComponent.cs"
    - path: "SourceGenerators/WallstopStudios.DxMessaging.Analyzer/Analyzers/MessageAwareComponentBaseCallAnalyzer.cs"
    - path: "Editor/Analyzers/BaseCallTypeScannerCore.cs"
    - path: "Editor/CustomEditors/MessageAwareComponentInspectorOverlay.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "analyzer"
  - "lifecycle"
  - "diagnostics"
  - "messageawarecomponent"
  - "base-call"
  - "dxmsg006"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of Unity lifecycle methods, Roslyn analyzers, and IL inspection."

impact:
  performance:
    rating: "none"
    details: "Contract enforcement runs at compile and edit time; no runtime cost in release builds."
  maintainability:
    rating: "high"
    details: "Catches missing base calls before they ship; the meta-test forces alignment when the contract changes."
  testability:
    rating: "high"
    details: "Five enforcement layers each verify the contract from a different angle."

prerequisites:
  - "Familiarity with Unity MonoBehaviour lifecycle methods"
  - "Awareness of Roslyn analyzers"

dependencies:
  packages: []
  skills:
    - "ascii-only-docs"
    - "code-samples-must-compile"
    - "tests-must-be-parameterized-by-message-kind"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Base call contract"
  - "DXMSG006 contract"
  - "Lifecycle base-call invariant"

related:
  - "ascii-only-docs"
  - "code-samples-must-compile"
  - "tests-must-be-parameterized-by-message-kind"

status: "stable"
---

# MessageAwareComponent Base-Call Contract

> **One-line summary**: Subclasses of `MessageAwareComponent` MUST call
> `base.<method>()` from every guarded lifecycle override, or DxMessaging stops
> working on that component; five enforcement layers catch the omission.

## Overview

`MessageAwareComponent` is the base type users derive from to plug a
MonoBehaviour into the DxMessaging registration system. It owns a
`MessageRegistrationToken`, creates it during `Awake`, enables/disables it
alongside the component, and releases it on destroy. Each of those steps lives
in a `protected virtual` lifecycle method on the base class. If a subclass
overrides the method without calling `base.<method>()`, the framework work is
silently skipped; the symptom is "messages stop being received" with no
exception.

The contract:

- Five guarded methods carry framework work and MUST be chained via `base`:
  `Awake`, `OnEnable`, `OnDisable`, `OnDestroy`, `RegisterMessageHandlers`.
- Two additional methods are guarded prospectively for their canonical Unity
  one-arg-bool signature: `OnApplicationFocus(bool)` and
  `OnApplicationPause(bool)`. `MessageAwareComponent` does not currently declare
  these, so the analyzer never actually fires DXMSG006 for them today; the
  guard exists so that adding a virtual body to the base class in a future
  release immediately gets DXMSG006 / DXMSG010 coverage on existing subclasses
  without an analyzer revision.
- Scanner/reflection parity requirement: every guarded-method lookup path in
  `BaseCallTypeScannerCore` must apply the same signature rules (declared
  method resolution, base-virtual detection for hiding checks, override-chain
  traversal, and method-level `[DxIgnoreMissingBaseCall]` discovery). A bool
  fallback only in one path is a contract bug.
- One method (`OnApplicationQuit`) is virtual but intentionally empty; missing
  the base call there is harmless. It lives on the
  `AllowListIntentionallyUnguarded` allow list.
- No other Unity lifecycle method on `MessageAwareComponent` performs framework
  work today. A meta-test pins this so adding one without updating the guarded
  set, the consequence-text dictionary, the IL scanner, AND the test allow list
  fails the build.

## Guarded Methods and Consequences

When `base.<method>()` is missed, the per-method consequence determines what
breaks at runtime:

| Method                    | Framework work performed by base                                           | Consequence if skipped                                                          |
| ------------------------- | -------------------------------------------------------------------------- | ------------------------------------------------------------------------------- |
| `Awake`                   | Creates the `MessageRegistrationToken`; calls `RegisterMessageHandlers`.   | Token is never created; handlers cannot register.                               |
| `OnEnable`                | Re-enables the token (subject to `MessageRegistrationTiedToEnableStatus`). | Handlers are not re-enabled when the component is enabled.                      |
| `OnDisable`               | Disables the token (subject to `MessageRegistrationTiedToEnableStatus`).   | Handlers are not disabled; the component keeps processing while ostensibly off. |
| `OnDestroy`               | Releases the messaging component, disables the token, clears refs.         | Handlers are not deregistered; token leaks; memory leak.                        |
| `RegisterMessageHandlers` | Registers default `StringMessage` / `GlobalStringMessage` handlers.        | Default string handlers do not register.                                        |

To suppress the `RegisterMessageHandlers` warning intentionally, override
`RegisterForStringMessages` to return literal `false` (the analyzer detects
this and lowers the diagnostic to Info; see Smart Case below).

The diagnostic message text for DXMSG006 is per-method and lives in two
places that MUST stay in sync:

- `MessageAwareComponentBaseCallAnalyzer.MissingBaseCallMessageFormatsByMethod`
  (the Roslyn analyzer; emits the diagnostic message at compile time).
- `BaseCallTypeScannerCore.MissingBaseCallMessageFormatsByMethod` and
  `GetMissingBaseConsequenceLine(...)` (the IL scanner; the inspector overlay
  HelpBox renders the same per-method text at edit time).

A meta-test asserts every entry in `GuardedMethodNames` has a matching
consequence row.

## Enforcement Layers

The contract is enforced at five layers, each catching the omission from a
different angle:

1. **Roslyn analyzer (compile time)** -
   `MessageAwareComponentBaseCallAnalyzer` emits DXMSG006 (override missing
   base call), DXMSG007 (`new`-modifier hide), DXMSG008 (opt-out marker),
   DXMSG009 (implicit hide; CS0114 equivalent), and DXMSG010 (transitive
   broken chain). These show up as build warnings.
1. **IL scanner (edit time)** - `BaseCallTypeScanner` walks loaded
   subclasses via Unity's `TypeCache` and probes IL for missing
   `call`/`callvirt` instructions to the base method. Deterministic across
   Unity 2021 cache hits where the analyzer console pipe drops warnings.
1. **Inspector overlay** -
   `MessageAwareComponentInspectorOverlay` reads the IL scanner's snapshot
   and shows a HelpBox above the offending component, listing the missing
   method and the per-method consequence sentence.
1. **Runtime self-check breadcrumb** - In `Editor` and `Debug` builds,
   `MessageAwareComponent.OnEnable` checks for a null registration token
   and emits a one-time `Debug.LogError` per instance pointing at
   `docs/reference/analyzers.md`. Catches the case where a user has
   disabled the analyzer or opened the project on a Unity version that
   does not load the analyzer DLL.
1. **Meta-test invariant (CI)** -
   `MessageAwareComponentBaseCallAnalyzerTests.GuardedMethodListMatchesAllVirtualLifecycleMethodsOnPublicBaseClasses`
   parses the actual `Runtime/Unity/MessageAwareComponent.cs` source and
   fails the build if any virtual lifecycle method with a non-empty body
   is missing from `GuardedMethodNames` or
   `AllowListIntentionallyUnguarded`.

## Adding a New Guarded Method

When the framework grows a new lifecycle method that performs work, every
layer must update together. The meta-test fails until all four are aligned:

1. Add the method to
   `MessageAwareComponentBaseCallAnalyzer.GuardedMethodNames` (the Roslyn
   analyzer's guarded set).
1. Add a per-method consequence row to
   `MessageAwareComponentBaseCallAnalyzer.MissingBaseCallMessageFormatsByMethod`
   describing what breaks at runtime when the base call is missed.
1. Add the method to `BaseCallTypeScannerCore.GuardedMethodNames` AND its
   `MissingBaseCallMessageFormatsByMethod`. The IL scanner runs at edit
   time; both dictionaries must mirror the analyzer's content.
1. Add tests: a DXMSG006 case (override with body, no `base.X()`), a
   DXMSG007 case (`new`-modifier), a DXMSG009 case (implicit hide), and an
   assertion that the per-method consequence text appears in the
   diagnostic message.

If the new method is intentionally empty (does no framework work), add it
to `AllowListIntentionallyUnguarded` instead. Document the rationale in a
`///` comment on the method body.

## Opt-Out Mechanisms

Three escape hatches exist for users who genuinely want to suppress the
diagnostic on a specific class or method:

- `[DxIgnoreMissingBaseCall]` attribute - apply at class scope to suppress
  for every guarded method on the type, or at method scope to suppress for
  one method only. The analyzer emits DXMSG008 (Info) so the suppression is
  visible in the build log.
- Project ignore list - managed on `Assets/Editor/DxMessagingSettings.asset`
  and mirrored to the generated sidecar
  `Assets/Editor/DxMessaging.BaseCallIgnore.txt` for the analyzer. Same
  DXMSG008 behavior as the attribute.
- `.editorconfig` severity override - e.g.
  `dotnet_diagnostic.DXMSG006.severity = none` to silence the analyzer
  globally. Still leaves DXMSG009/010 etc. firing under their own ids.

The IL scanner respects the attribute and the ignore list; the inspector
overlay reads the ignore list directly to render its "Stop ignoring"
HelpBox.

## Smart Case: `RegisterForStringMessages => false`

A subclass that overrides `RegisterForStringMessages` to return literal
`false` is opting out of the default string-message registrations. In that
case, missing the base call on `RegisterMessageHandlers` is a documented
intentional pattern. The analyzer detects this syntactically and lowers
DXMSG006 from Warning to Info on `RegisterMessageHandlers` only. Other
overrides on the same class still emit DXMSG006 at full Warning severity.
The lowering is literal-only; ternaries, `is false` patterns, and switch
expressions that "happen to return false" do not trigger smart case.

## Why Five Layers and Not One

Each layer covers a gap the others have:

- The analyzer can be disabled per project (`<RoslynAnalyzers>` MSBuild
  property, `.editorconfig` severity override, or just opting out of the
  package).
- The IL scanner runs every domain reload but never blocks the build; a
  user who ignores the inspector HelpBox can still ship.
- The runtime breadcrumb fires only after the failure has already occurred
  on a live component; useful for catching "the analyzer didn't run" but
  late.
- The meta-test catches the inverse case: the contract drifts inside the
  package itself when a contributor adds a new lifecycle method.

The cost of each layer is low (no runtime overhead in release builds; one
NUnit test; a per-domain-reload reflection scan), so we keep all five.

## See Also

- [ASCII-Only Documentation Policy](../documentation/ascii-only-docs.md)
- [Code Samples Must Compile](../documentation/code-samples-must-compile.md)
- [Tests Must Be Parameterized by Message Kind](../testing/tests-must-be-parameterized-by-message-kind.md)
- [Lifecycle Edge-Case Test Coverage](../testing/lifecycle-edge-coverage.md)
- [LeakWatcher: Detecting Registration Leaks in Tests](../testing/leak-watcher-usage.md)
- [Inspector Overlay Invariants](../testing/inspector-overlay-invariants.md)

## References

- DxMessaging analyzer reference (DXMSG006-DXMSG010): `docs/reference/analyzers.md`
- Unity MonoBehaviour event reference: https://docs.unity3d.com/ScriptReference/MonoBehaviour.html

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-02 | Initial version |
