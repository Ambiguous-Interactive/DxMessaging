---
name: test-code-quality
description: "Keeping tests honest: importing production code instead of re-implementing it in the test file, bidirectional SYNC notes where duplication is unavoidable (PowerShell script plus JS test, browser-only code), accurate describe-block and header categorization (missing vs wrong type vs empty), eslint-disable-next-line placement, user-facing message wording matching real output, cross-library comparison-benchmark parity with honest N/A cells and .github/comparison-packages.json as the single source of pins, and the three MessageAwareComponent inspector-overlay invariants. Use when a test defines its own copy of production logic, when adding a SYNC note or a comparison bridge, or when touching the custom editor overlay."
metadata:
  category: "testing"
  tags: "testing, documentation, linting, code-quality, javascript"
---

# Test Code Quality

A test is only worth its runtime if it exercises the real production code, describes accurately what it covers, and reports numbers nobody had to fake. These rules cover all three, plus the two DxMessaging surfaces where honesty is easiest to lose: the cross-library comparison benchmarks and the inspector overlay.

## When to use

- A test file defines validation constants, helper logic, or functions that mirror production.
- Production code is PowerShell, browser-only, or otherwise not importable from the test, and duplication is unavoidable.
- Adding or editing `describe()` blocks, file header comments, or user-facing warning text.
- Adding a linter suppression directive.
- Adding or changing a comparison bridge, a comparison package pin, or a comparison scenario.
- Touching `MessageAwareComponentFallbackEditor` or `MessageAwareComponentInspectorOverlay`.

## Rules

### Test production code, never a local copy

- Tests import and call production functions. Export whatever the tests need from the production module rather than re-declaring it. A local copy passes while production regresses, doubles maintenance, drifts, and reproduces its own bugs.
- Legitimate test-local code: thin wrappers that call production and add assertions, test-data factories and generators, and custom matchers. None of these re-implement a rule.
- Red flags in review: the test file defines validation constants, mirrors production utility functions, never imports the module it names, rivals the production file in size, or shares a bug with production.
- Verify before merging: the test imports the production module, the production file shows coverage, a small mutation to production makes the test fail, and no function body is duplicated across the pair.

### SYNC notes where duplication is forced

- SYNC notes must be bidirectional. If A references B, B references A.
- Reference function or block names, never line numbers. Confirm the target exists before adding the note. Update both sides in the same change.

### Accuracy of test documentation and messages

- `describe()` names and file header comments must match the tests they actually contain. Categorize by JavaScript semantics, not by truthiness: `undefined` and `null` are MISSING; `""`, `0`, `false`, and `{}` are WRONG TYPE (all but `{}` are falsy); `[]` is truthy and is a correct-type EMPTY value.
- `eslint-disable-next-line` suppresses exactly the next line - put it immediately above the offending line, inside the object or block if that is where the line is. Before adding any directive, confirm the linter is configured (`package.json` devDependencies, `.eslintrc*`, `eslint.config.*`); an unconfigured directive is dead code.
- User-facing warning and error text must match real output terminology. Read the code that generates the output and grep for the actual heading or column name; add a `// SYNC:` comment naming that source. Test the message content when it names a specific column or label.

### Comparison benchmark honesty

- Every bridge implements `IMessagingTechBridge` using the library's own best-practice API for that scenario. Never route a library through DxMessaging-shaped glue.
- An unsupported scenario renders `N/A`. Never substitute a stand-in or copy a number. `IMessagingTechBridge.DispatchedPayloadType(scenario)` declares what is actually dispatched, and `ComparisonBridgeContract.AssertStructScenarioPayloadFidelity` (`StructScenarioDispatchesNonPrimitiveStructPayload`) fails any bridge claiming `StructMessageNoBoxing` while dispatching a primitive or boxed value.
- Do not hide an allocation a real caller pays. Unity `SendMessage(string, object)` boxes a value type on every call, so pass the value and let it box (cast to `object`, keep the backing field non-`const` so a literal `0` cannot bind to the `SendMessageOptions` overload). `ComparisonAllocationHonestyTests` pins both directions. Zenject's `SignalBus` boxes through internal `object` routing; that non-zero cost is real and is reported.
- The harness checks canonical fan-out and current-row progress so dedup or a missing registration fails loudly. `ComparisonHarness.Run` builds and disposes a fresh bridge per case and uses a fresh `MessageBus` per DxMessaging scenario. Published comparisons run in a dedicated player, so do not add per-row full collections. Dedicated contract tests verify observable teardown; do not infer that progress accounting detects every form of leaked state.
- Comparison and dispatch tables often measure different shapes. `GlobalToOne` and `GlobalToMany` have exact one-token and 16-token untargeted topology twins. `StructNoBox` shares the one-token storage shape but dispatches the canonical `ComparisonStructPayload`; do not force unlike rows equal. `ComparisonDispatchTopologyTests` observes the real twin registrations, dispatch, diagnostics, and cleanup. The methodology runbook records the remaining gaps; topology alone does not prove equal throughput.
- `.github/comparison-packages.json` is the ONLY home for the OpenUPM registry, the pinned comparison versions, and the required Unity built-in packages. Bump there, then hand-update every mirror in the same change: the asmdef `versionDefines` and same-asmdef `defineConstraints`, `.unity-test-project/Packages/manifest.json`, and `packages-lock.json`. One define per package - a shared define turns the asmdef gate into OR semantics.

### Inspector overlay invariants

1. `MessageAwareComponentFallbackEditor` registers as a PRIMARY editor (`isFallback == false`) with `editorForChildClasses: true`, and its `OnInspectorGUI` calls `MessageAwareComponentInspectorOverlay.RenderInsideOnInspectorGUI(target)` then `DrawDefaultInspector()`. `isFallback = true` loses the missing-base-call HelpBox on Unity 2021 because `finishedDefaultHeaderGUI` is unreliable there; a hand-rolled body that skips `m_Script` leaves a visible empty gap under the header. Pinned by `FallbackEditorMustRegisterAsPrimaryNonFallbackEditorForChildClasses`.
1. `BuildAndRenderOverlay` performs all gating up front and returns `false` before any `EditorGUILayout` call when `shape == 0`. Unity runs Layout and Repaint passes that must emit identical control counts; one stray layout call corrupts the window's layout cache.
1. `RenderInsideOnInspectorGUI` never gates on `Event.current.type` and never latches per-Repaint. Cross-path dedupe happens in `DrawHeader`, which skips unconditionally when the editor is a `MessageAwareComponentFallbackEditor`.

Tests here set `DxMessagingSettings.GetOrCreateSettings()._baseCallCheckEnabled = false` in setup and restore it in teardown, and declare subclasses as top-level `internal` types marked `[AddComponentMenu("")]`, because Unity cannot serialize private nested `MonoBehaviour` types across a domain reload.

## References

| Document                                                                                                      | Purpose                                                                                                                                                                   |
| ------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [comparison-parity-and-package-single-source.md](./references/comparison-parity-and-package-single-source.md) | Cross-library comparison rules: idiomatic bridges, honest `N/A`, payload and allocation fidelity, fan-out assertions, topology divergence, and the package single source. |
| [inspector-overlay-invariants.md](./references/inspector-overlay-invariants.md)                               | The three fallback-editor / overlay invariants, the rejected approaches, regression tests, and when to revisit them.                                                      |
| [test-code-quality.md](./references/test-code-quality.md)                                                     | Linter directive placement, describe-block and header accuracy, the truthiness categorization table, message consistency, and bidirectional SYNC notes.                   |
| [test-production-code-part-1.md](./references/test-production-code-part-1.md)                                 | Structuring production code for import, testing it directly, acceptable test-local helpers, and SYNC notes for PowerShell and browser-only code.                          |
| [test-production-code-part-2.md](./references/test-production-code-part-2.md)                                 | SYNC note requirements, the red-flag table for tests that do not exercise production, and the pre-merge verification checklist.                                           |
| [test-production-code.md](./references/test-production-code.md)                                               | The re-implemented-logic anti-pattern and the five ways it produces false confidence.                                                                                     |
