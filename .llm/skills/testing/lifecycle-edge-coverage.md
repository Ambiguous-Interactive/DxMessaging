---
title: "Lifecycle Edge-Case Test Coverage"
id: "lifecycle-edge-coverage"
category: "testing"
version: "1.0.0"
created: "2026-05-02"
updated: "2026-05-02"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/Core/LifecycleEdgeCasesTests.cs"
    - path: "Tests/Runtime/Core/ReentrantEmissionExtendedTests.cs"
    - path: "Tests/Runtime/TestUtilities/LeakWatcher.cs"
    - path: "Runtime/Core/MessageBus/MessageBus.cs"
    - path: "Runtime/Core/DxMessagingStaticState.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "lifecycle"
  - "edge-cases"
  - "scenes"
  - "destroy"
  - "regression"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of Unity scene/destroy lifecycle, the bus's snapshot dispatch semantics, and the reset-generation guard."

impact:
  performance:
    rating: "none"
    details: "Pattern affects test coverage breadth, not runtime performance."
  maintainability:
    rating: "critical"
    details: "Pins the lifecycle scenarios that surfaced as production defects so they cannot regress silently."
  testability:
    rating: "critical"
    details: "Defines the canonical edge-case set every dispatch-path change must clear."

prerequisites:
  - "tests-must-be-parameterized-by-message-kind"

dependencies:
  packages: []
  skills:
    - "tests-must-be-parameterized-by-message-kind"
    - "comprehensive-test-coverage"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - "NUnit"
  versions:
    unity: ">=2021.3"

aliases:
  - "Lifecycle edge cases"
  - "Bus dispatch regression suite"
  - "Scene-aware dispatch tests"

related:
  - "tests-must-be-parameterized-by-message-kind"
  - "comprehensive-test-coverage"
  - "single-thread-contract"
  - "base-call-contract"
  - "leak-watcher-usage"

status: "stable"
---

# Lifecycle Edge-Case Test Coverage

> **One-line summary**: Every change to the bus dispatch path must be tested
> against the canonical lifecycle edge-case set; new dispatch behavior MUST
> cover scene unload mid-dispatch, DontDestroyOnLoad transitions, prefab
> pooling churn, token disable / re-enable mid-dispatch, post-Reset emit,
> OnApplicationQuit drain, and cross-kind reentrancy.

## Overview

The bus-freezing fix surfaced a class of bugs that the existing fixtures did
not exercise: a handler that disables its token mid-dispatch, a handler that
unloads its scene mid-dispatch, and emissions that arrive after
`DxMessagingStaticState.Reset` cleared the global bus. Each one was a real
production bug. The lifecycle edge-case fixtures pin the behavior so a
future dispatch-path change cannot reintroduce the regressions silently.

This skill documents the scenarios that MUST be covered, where the canonical
fixtures live, and the conventions for adding new entries.

## Required Scenario Set

Every change that touches the bus dispatch path (registration, emission,
deregistration, interceptor / post-processor pipeline) is expected to keep
this scenario list green. New dispatch behavior must add scenarios when the
mechanism it introduces is not already covered.

| Scenario                                            | Pinned by                                      | Notes                                                                |
| --------------------------------------------------- | ---------------------------------------------- | -------------------------------------------------------------------- |
| `SceneUnloadMidDispatchDrainsInFlightEmission`      | `LifecycleEdgeCasesTests`                      | Handler triggers `SceneManager.UnloadSceneAsync` from inside body.   |
| `SceneTransitionWithDontDestroyOnLoad`              | `LifecycleEdgeCasesTests`                      | DDOL host survives an additive scene unload and keeps receiving.     |
| `RegisterDuringSceneLoadCallback`                   | `LifecycleEdgeCasesTests`                      | `SceneManager.sceneLoaded` callback registers a handler.             |
| `PrefabPoolingEnableDisableCycles`                  | `LifecycleEdgeCasesTests` (with `LeakWatcher`) | 100-cycle SetActive churn; bus must not leak registrations.          |
| `TokenDisableMidDispatch`                           | `LifecycleEdgeCasesTests`                      | Snapshot semantics: B still runs after A disables the token.         |
| `TokenReEnableMidDispatch`                          | `LifecycleEdgeCasesTests`                      | Re-enable mid-dispatch does not retroactively join current emission. |
| `EmitOnEmptyBusIsSilentNoOp`                        | `LifecycleEdgeCasesTests`                      | Emit with zero handlers must not throw or perturb counters.          |
| `EmitImmediatelyAfterResetIsSilentNoOp`             | `LifecycleEdgeCasesTests`                      | Reset-generation guard: pre-reset handlers must NOT fire.            |
| `OnApplicationQuitDrainsCleanly`                    | `LifecycleEdgeCasesTests` (with `LeakWatcher`) | Quit must not throw and must not leak registrations.                 |
| `HostDestroyMidDispatchDoesNotCrash`                | `LifecycleEdgeCasesTests`                      | `Object.Destroy(host)` from a handler must not crash dispatch.       |
| `CrossKindReentrancyChainCompletes`                 | `ReentrantEmissionExtendedTests`               | All 6 (outer, inner) cross-kind permutations.                        |
| `DeepRecursion10Levels`                             | `ReentrantEmissionExtendedTests`               | Bounded self-recursion plus `IMessageBus.EmissionId` invariant.      |
| `ReentrantUnsubscribeThenResubscribeSelf`           | `ReentrantEmissionExtendedTests`               | Snapshot semantics for self-modifying handlers.                      |
| `NestedHandlerThrowsDuringReentrantEmit`            | `ReentrantEmissionExtendedTests`               | Inner throw aborts outer trailing handlers consistently.             |
| `ReentrantInterceptorVeto`                          | `ReentrantEmissionExtendedTests`               | Inner vetoed re-emit; outer trailing handler still runs.             |
| `InterceptorMutationDuringReemitObservesFreshState` | `ReentrantEmissionExtendedTests`               | Interceptor sees fresh state on re-emit, no carry-over.              |

## Where the Canonical Fixtures Live

Two fixtures, both parameterized by `MessageScenario`:

- `Tests/Runtime/Core/LifecycleEdgeCasesTests.cs` -- destruction, scene
  loading, token disable / re-enable, post-Reset, OnApplicationQuit,
  empty-bus emit. Scene-loading tests carry `[Category("UnityRuntime")]`.
- `Tests/Runtime/Core/ReentrantEmissionExtendedTests.cs` -- cross-kind
  reentrancy, deep recursion, self-resubscribe, nested-throw, interceptor
  veto, interceptor mutation. Default category (no gating).

The supporting `LeakWatcher` utility lives at
`Tests/Runtime/TestUtilities/LeakWatcher.cs`. See
`leak-watcher-usage.md` for its public-counter contract and usage rules.

## Adding a New Edge Case

When a new dispatch-path change introduces a lifecycle interaction that is
not already covered, extend the appropriate fixture:

1. Drive the test from
   `[ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]`
   so every entry covers all three kinds. The
   `tests-must-be-parameterized-by-message-kind` skill is enforced by
   `TestAttributeContractTests.TripletEmitTestsUseScenarioParameterization`;
   per-kind triplets fail CI.
1. Track every spawned `GameObject` via `_spawned.Add(host)`. The
   `MessagingTestBase` cleanup loop relies on this list; the rule is pinned
   by `TestAttributeContractTests.FixturesUsingMessagingTestBaseUseSpawnedCleanupPattern`.
1. Gate scene-load / scene-unload tests behind `[Category("UnityRuntime")]`.
   These tests yield frames for async ops to settle and add wall-clock to
   the run; the suite-wide budget in `SuiteWallClockBudgetTest.cs` skips
   the default-suite assertion when a UnityRuntime test is observed.
1. When the test creates and tears down registrations in a small region,
   wrap the region in `using (LeakWatcher.Watch(...))`. The watcher reads
   every public counter on `IMessageBus`; new counter kinds added to the
   bus must extend the watcher (see `leak-watcher-usage.md`).
1. Pick assertion shapes that name the kind under test. The fixtures use
   `[{0}] ...` format strings keyed on `scenario.Kind` so a per-kind
   regression is easy to triage. Example from
   `LifecycleEdgeCasesTests.TokenDisableMidDispatch`:

   ```csharp
   Assert.AreEqual(
       1,
       bCount,
       "[{0}] Snapshot semantics: B must still run on the in-flight emission "
           + "even after Disable. aCount={1}, bCount={2}.",
       scenario.Kind,
       aCount,
       bCount
   );
   ```

## Common Gotchas

These bit real pull requests during the bus-freezing fix work; document them
as the failure modes to expect.

- **Scene-load tests must build transient scenes dynamically.** Do not
  assume Build Settings contains a stub scene; the package can be consumed
  by a project with no scenes registered. Use
  `SceneManager.CreateScene(name)` and unload with
  `SceneManager.UnloadSceneAsync(scene)` (yield a frame after `CreateScene`
  before moving objects into it).
- **`LogAssert.Expect` must be placed BEFORE the triggering action.** Unity
  matches expected logs against the log queue accumulated during the test,
  not retroactively; an `Expect` after the throwing emit fails to match.
- **Iteration counts: stay under 100 in the default suite.**
  `PrefabPoolingEnableDisableCycles` runs 100 SetActive cycles -- that is
  the default ceiling. Heavier counts (1000+) belong behind
  `[Category("Stress")]` or `[Category("Allocation")]` so the wall-clock
  budget in `SuiteWallClockBudgetTest.cs` does not breach.
- **`LeakWatcher` is the canonical leak-detection mechanism.** Do not
  re-implement counter snapshotting inline. If a leak is missed by the
  watcher, the cause is a missing counter on `IMessageBus`; the fix is
  extending the bus's public surface and the watcher in lock-step (see
  `PublicSurfaceContractTests.PublicTypeSetInDxMessagingCoreNamespaceMatchesSnapshot`).
- **`DxMessagingStaticState.Reset` is the only legitimate way to clear the
  global bus mid-test.** The post-Reset guard in
  `EmitImmediatelyAfterResetIsSilentNoOp` pins that handlers registered
  before a Reset cannot fire on emissions issued after it. Direct
  manipulation of bus internals from a test bypasses the guard and will
  drift if the reset-generation counter is renamed.

## Why These Scenarios and Not Others

The set above is not exhaustive; it is the floor. Each entry was added in
response to a real production-side issue:

- Scene unload mid-dispatch: a user reported a crash when a UI handler
  destroyed its parent scene from inside a click callback.
- Prefab pooling churn: a pooled enemy that flickered SetActive false / true
  every frame leaked one registration per cycle on a previous version of
  the bus.
- Token disable / re-enable mid-dispatch: snapshot semantics were unclear
  in user reports; the tests pin the documented contract.
- Post-Reset emit: an in-editor scene-reload sequence was issuing emissions
  against a freshly-reset bus and silently corrupting registration state.
- OnApplicationQuit drain: the previous version threw on shutdown when a
  handler tried to deregister during its own quit callback.
- Cross-kind reentrancy: an interceptor on the targeted bus emitting
  untargeted from inside its callback deadlocked one revision of the
  dispatcher.

When a future regression uncovers a lifecycle interaction not on the list,
add it to the fixture, add the row to the table above, and reference the
diagnosing PR in the test's XML doc.

## See Also

- [LeakWatcher: Detecting Registration Leaks in Tests](./leak-watcher-usage.md)
- [Tests Must Be Parameterized by Message Kind](./tests-must-be-parameterized-by-message-kind.md)
- [Test Coverage Requirements](./comprehensive-test-coverage.md)
- [Single Thread Contract](./single-thread-contract.md)
- [MessageAwareComponent Base-Call Contract](../unity/base-call-contract.md)

## References

- Unity scene management API: https://docs.unity3d.com/ScriptReference/SceneManagement.SceneManager.html
- NUnit `ValueSource` documentation: https://docs.nunit.org/articles/nunit/writing-tests/attributes/valuesource.html
- Unity Test Framework: https://docs.unity3d.com/Packages/com.unity.test-framework@latest

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-02 | Initial version |
