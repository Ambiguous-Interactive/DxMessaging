---
title: "Fast Unity Tests: Reload, Frame Tax, and Anti-Patterns"
id: "fast-unity-tests"
category: "testing"
version: "1.0.0"
created: "2026-06-15"
updated: "2026-06-16"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/Core/MessagingTestBase.cs"
    - path: "Tests/Runtime/Core/TestAttributeContractTests.cs"
    - path: "Tests/Runtime/Core/SuiteWallClockBudgetTest.cs"
    - path: "scripts/unity/run-ci-tests.ps1"
    - path: "scripts/__tests__/run-ci-tests-enter-play-mode.test.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "unity"
  - "performance"
  - "play-mode"
  - "domain-reload"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding play-mode entry, deferred destroy flushes, and the frame-vs-wall-clock cost model."

impact:
  performance:
    rating: "none"
    details: "Test-suite speed only; no product runtime cost. Removes per-test play-mode-entry reload and the O(n) teardown frame tax; the wall-clock win lands on the CI legs (cold runners, Unity 2021.3 PlayMode), not the warm local editor."
  maintainability:
    rating: "high"
    details: "Pins the fast-test contract with drift-guard tests so the anti-patterns cannot creep back."
  testability:
    rating: "high"
    details: "Coverage is unchanged; only the per-test overhead shape changes."

prerequisites:
  - "Familiarity with the Unity Test Framework ([Test] vs [UnityTest])"
  - "The Unity MCP Test Loop for local measurement"

dependencies:
  packages: []
  skills:
    - "mcp-test-loop"
    - "test-coverage-unity-anti-patterns"
    - "test-base-class-cleanup"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - "NUnit"
  versions:
    unity: ">=2021.3"

aliases:
  - "fast tests"
  - "EnterPlayModeOptions"
  - "domain reload disable"
  - "test frame tax"

related:
  - "mcp-test-loop"
  - "test-coverage-unity-anti-patterns"
  - "test-base-class-cleanup"
  - "unity-perf-test-isolation"

status: "stable"
---

<!-- trigger: slow tests, domain reload, EnterPlayModeOptions, frame tax, UnityTearDown, WaitForSeconds, timeScale | Make the Unity test legs fast without dropping coverage | Core -->

# Fast Unity Tests: Reload, Frame Tax, and Anti-Patterns

> **One-line summary**: The Unity test legs stay fast by disabling play-mode
> domain+scene reload, batching deferred destroys to one teardown frame, keeping
> `[UnityTest]` only where a frame is genuinely yielded, and banning real-time
> waits. Coverage never drops to hit a time budget.

## The cost model (read this first)

Unity test time is dominated by two things, and NEITHER is wall-clock sleep:

1. **Play-mode ENTRY.** Entering play mode reloads the scripting domain and the
   scene by default. The PlayMode runner enters once per run, so this is a fixed
   per-leg cost, largest on a cold CI runner.
1. **Per-test FRAME yields.** Each `yield return null` advances one engine frame.
   In a warm local editor a frame is near-free (sub-millisecond, no rendering),
   so the local PlayMode suite already finishes in tens of seconds. On a cold CI
   runner -- especially Unity 2021.3 PlayMode -- frames are far more expensive,
   so a per-test O(n) frame tax compounds across hundreds of tests.

`Thread.Sleep` / `WaitForSeconds` / `Time.timeScale` do NOT appear in this suite,
so the common "lower `timeScale`" tip does not apply -- there is no real time to
compress. Keep it that way (see [Drift-guards](#drift-guards)).

> **Per-mode `< 3 min` is a CI metric.** The warm local MCP editor is already far
> under it; measure relative deltas locally, but read the absolute budget off the
> CI EditMode/PlayMode legs.

## Lever 1: Disable enter-play-mode reload

Set `EnterPlayModeOptions` to `DisableDomainReload | DisableSceneReload` (the
serialized value `3`) so play-mode entry skips both reloads:

- CI (the committed source of truth): `Initialize-EphemeralProject` in
  `scripts/unity/run-ci-tests.ps1` emits a partial
  `ProjectSettings/EditorSettings.asset` with both fields. The partial form carries
  no `serializedVersion` pin (Unity reads the present fields and defaults the rest),
  so there is no pinned version to mismatch across the 2021.3 / 2022.3 / 6000.x
  matrix; the CI matrix legs validate it (the local MCP loop only exercises 6000.x).
- Local project: `.unity-test-project/ProjectSettings/EditorSettings.asset` carries
  the same two fields, but `.unity-test-project/ProjectSettings/*` is gitignored, so
  that copy is a per-developer convenience, not committed. The runner emit above is
  what the drift-guard pins.

**Why it is safe here.** Disabling domain reload keeps statics from the edit-mode
domain alive into play mode. DxMessaging resets its statics on play-mode entry
anyway, via five `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` hooks
(those run on every play-mode entry regardless of reload), and every test resets
through `DxMessagingStaticState.Reset()`. Verify red-green (full PlayMode at pass
parity with reload OFF) before trusting it; if a leak ever surfaces, add the
missing reset -- do NOT re-enable reload.

**Caveat.** An `AssetDatabase.Refresh()` after a script edit still forces one
reload even with this set. So locally it speeds repeated play-mode entries, not
the post-edit recompile; in batchmode CI (scripts precompiled, one entry) it is
one saved reload per PlayMode leg. The editmode and standalone legs never enter
in-editor play mode, so this setting is inert for them.

## Lever 2: One teardown frame, not one per object

In play mode `Object.Destroy` is deferred to the end-of-frame flush, and every
destroy queued in the same frame flushes together. So destroy ALL tracked objects
first, then yield ONCE:

```csharp
// GOOD: O(1) teardown -- queue every destroy, then a single drain frame.
bool destroyedAny = false;
foreach (GameObject spawned in _spawned)
{
    if (spawned == null) { continue; }
    DestroyTrackedObject(spawned);
    destroyedAny = true;
}
_spawned.Clear();
if (destroyedAny && Application.isPlaying)
{
    yield return null; // flushes EVERY queued OnDisable/OnDestroy at once
}

// BAD: O(n) teardown -- a 128-component fixture pays ~128 frames here.
foreach (GameObject spawned in _spawned)
{
    if (spawned == null) { continue; }
    DestroyTrackedObject(spawned);
    if (Application.isPlaying) { yield return null; } // one frame per object
}
```

The single drain still flushes inside this teardown, so a queued `OnDisable` never
bleeds into the next test's emptied bus (the race `UnitySetup` guards). This is the
`MessagingTestBase.UnityCleanup` pattern.

## Lever 3: `[UnityTest]` only when you yield

A `[UnityTest]` body with no `yield return` is a synchronous test wearing a
coroutine costume -- it still pays the coroutine and base-fixture frame overhead.
Use `[Test]` unless the body genuinely needs a frame. The one structural reason a
DxMessaging test stays `[UnityTest]` is that a `MessageAwareComponent` acquires its
`MessageRegistrationToken` in `OnEnable`, which needs the play-mode lifecycle; when
the token can be obtained synchronously, prefer `[Test]` (it can also run in the
EditMode leg). Migrate file-by-file, preserving every assertion; the companion
no-yield-`[UnityTest]` drift-guard ships WITH that migration (it is not built yet --
it would otherwise be red against the many existing no-yield bodies).

## Lever 4: No real-time waits

Banned anywhere in `Tests/`: `Thread.Sleep`, `Task.Delay`, `WaitForSeconds`,
`WaitForSecondsRealtime`, `Time.timeScale`. They trade deterministic frame-based
waiting for wall-clock flake and slowness. Poll a frame budget or a synchronous
condition instead.

## Drift-guards

Three guards pin the contract so it cannot silently regress:

- `TestAttributeContractTests.TestSourcesAvoidRealTimeWaitAntiPatterns` (C#,
  runtime asmdef) scans the `Tests/` source tree and fails on any banned
  real-time-wait token.
- `scripts/__tests__/run-ci-tests-enter-play-mode.test.js` (Node) asserts
  `run-ci-tests.ps1` emits the reload-disable into each CI ephemeral project. The
  guard targets the runner emit, not `.unity-test-project`, because that local copy
  is gitignored (absent in a fresh CI checkout) while the emit is the committed,
  tracked enforcement.
- `SuiteWallClockBudgetTest` (C#, runtime asmdef) is the pre-existing speed
  backstop: it fails the default correctness suite when its wall clock exceeds a
  per-version hard ceiling (300 s on 2021.3, 180 s on 2022.3 / 6000.x) and warns
  past a 60 s soft budget -- a slowdown is unmissable no matter which lever drifts.

Write the assertion RED-first where offenders exist, then keep it green.

## Measurement protocol (MCP loop)

1. Baseline each mode with `DxMcpTestRunner.Run(testMode, assemblies, null, null,
resultPath)`; record `durationSeconds` + `{pass,fail,skip}`.
1. Change ONE lever, re-run the SAME command, diff. Keep a change only if pass
   counts hold and no flake appears over repeated runs.
1. Remember the warm-editor caveat: a near-zero local delta does not mean the
   change is worthless -- confirm structural wins (reload, O(n) teardown) on the
   CI legs, where frames and reloads are expensive.

## See Also

- [Unity MCP Test Loop](../unity/mcp-test-loop.md)
- [Unity Test Considerations and Anti-Patterns](test-coverage-unity-anti-patterns.md)
- [Test Base Class Cleanup](test-base-class-cleanup.md)
- [Unity Perf Test Isolation](../unity/unity-perf-test-isolation.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-06-16 | Initial version |
