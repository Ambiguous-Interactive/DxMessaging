---
name: unity-test-execution
description: "How the DxMessaging Unity test legs are hosted, filtered, and kept fast: the generated UPM host project under .artifacts/unity/projects/, NUnit [Category] filtering and -testFilter, EnterPlayModeOptions domain/scene reload disabling, batched teardown frames, [UnityTest] vs [Test], banned real-time waits, and the single-threaded bus contract. Use when an EditMode/PlayMode/standalone leg blows its wall-clock budget, when adding an asmdef or test dependency under Tests/, when picking test categories, when converting a no-yield [UnityTest], or before adding lock/Interlocked to the dispatch path."
metadata:
  category: "testing"
  tags: "testing, unity, performance, play-mode, domain-reload"
---

# Unity Test Execution

How the DxMessaging Unity suites are hosted, selected, and executed, plus the levers that keep the EditMode, PlayMode, and standalone IL2CPP legs fast. Coverage is never dropped to hit a time budget.

## When to use

- A Unity leg exceeds its wall-clock ceiling, or `SuiteWallClockBudgetTest` fails.
- Adding an `.asmdef` under `Tests/Editor/` or `Tests/Runtime/`, or adding a test-only UPM dependency.
- Diagnosing "Test framework not found", an empty test run, or a CI failure that needs a Unity project on disk.
- Choosing `[Category]` values or a `-testFilter` expression.
- Writing a `[UnityTest]`, or converting one whose body never yields.
- Considering `lock`, `Interlocked`, or `volatile` anywhere on the dispatch path.

## Rules

### Host project and assemblies

- The repo root is a UPM package, not a Unity project. `scripts/unity/run-ci-tests.ps1` generates a thin host under `.artifacts/unity/projects/<version>-<mode>/` whose `Packages/manifest.json` declares `com.wallstop-studios.dxmessaging` as a local `file:` dependency and lists it under `testables`. Never add `Assets/` content to the package root.
- `testables` exposes every asmdef under `Tests/` automatically, so a new suite needs no harness change: create the asmdef, give it a stable name such as `WallstopStudios.DxMessaging.Tests.Editor.NewSuite`, then confirm discovery with `node scripts/unity/lib/asmdef-discovery.js`.
- Name perf and DI-integration assemblies `*Benchmarks*`, `*Allocations*`, `*Comparisons*`, `*VContainer*`, `*Zenject*`, or `*Reflex*`. The classification regex in `scripts/unity/lib/asmdef-discovery.js` excludes those from the default include list.
- A new test-only UPM dependency goes into `New-ManifestJson` in `scripts/unity/run-ci-tests.ps1`; inspect the result with the runner's `-GenerateOnly` mode. Heavyweight runtime dependencies must be opt-in behind `--include-integrations`.
- Committed source of truth: `scripts/unity/run-ci-tests.ps1` plus `Runtime/`, `Editor/`, `Tests/`. Generated and gitignored: the host project's `Library/`, `Temp/`, `Logs/`, `UserSettings/`, and `.artifacts/unity/cache/**`. The `Library/` cache key pins OS, architecture, Unity version, mode, package inputs, and the runner script; do not add broad restore keys.

### Categories and selection

- Mark every test with at least one category. Repository categories that gate execution: `UnityRuntime` (scene load/unload tests that yield frames), `Allocation`, `Stress`, `PerfBench`. Generic speed taxonomy: `Fast` (under 100 ms, no I/O), `Slow` (Unity objects, under 5 s), `Integration` (external resources, under 30 s), `Flaky`.
- Filter from the command line with `-testFilter "cat==Fast"`, `-testFilter "cat!=Slow"`, or a combined expression such as `"cat==Fast && cat!=Flaky"`. The Test Runner window exposes the same filter under Category.
- A quarantined test needs `[Explicit("<reason> - issue #N")]` alongside its category. Do not label a slow test `Fast`, and do not leave `Flaky` tests in the default categories.

### Speed levers

1. **Disable enter-play-mode reload.** `EnterPlayModeOptions` must be `DisableDomainReload | DisableSceneReload` (serialized value `3`). `Initialize-EphemeralProject` in `scripts/unity/run-ci-tests.ps1` emits a partial `ProjectSettings/EditorSettings.asset` carrying both fields and no `serializedVersion` pin, so it survives the 2021.3 / 2022.3 / 6000.x matrix. This is safe because DxMessaging resets statics through five `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` hooks plus `DxMessagingStaticState.Reset()` per test. If a leak surfaces, add the missing reset; do NOT re-enable reload.
1. **One teardown frame, not one per object.** Queue every `Object.Destroy` first, then `yield return null` exactly once, as `MessagingTestBase.UnityCleanup` does. A per-object yield makes teardown O(n). In the normal lifecycle the synchronous `[TearDown] Cleanup()` has already emptied `_spawned`, so the residual per-test frame is the `UnitySetup` drain, which is load-bearing and must stay.
1. **`[UnityTest]` only when the body yields.** A no-yield `[UnityTest]` still pays per-method enumerator scheduling. Convert it to `[Test]`; it stays in the PlayMode assembly, so `MessageAwareComponent.OnEnable` and the `[UnitySetUp]`/`[UnityTearDown]` brackets still run. The migration is complete and the `pendingMigration` allowlist is empty.
1. **No real-time waits.** `Thread.Sleep`, `Task.Delay`, `WaitForSeconds`, `WaitForSecondsRealtime`, and `Time.timeScale` are banned everywhere under `Tests/`. Poll a frame budget or a synchronous condition instead.
1. **Keep the standalone leg on Release C++.** Debug C++ saves native compile time but makes the IL2CPP player far slower overall. Never touch `Il2CppCodeGeneration` on the correctness leg; that codegen fidelity is why the leg exists.

### Drift guards

- `TestAttributeContractTests.TestSourcesAvoidRealTimeWaitAntiPatterns` source-scans `Tests/` for banned wait tokens.
- `TestAttributeContractTests.NoYieldUnityTestsMustBePlainTest` source-scans for `[UnityTest]` methods that never yield. A source scan is required: a `yield break`-only method is still a compiler iterator, so reflection cannot detect it.
- `scripts/__tests__/run-ci-tests-enter-play-mode.test.js` asserts the runner emits the reload-disable into each ephemeral project, not into the gitignored `.unity-test-project`.
- `SuiteWallClockBudgetTest` fails the default suite past a per-version hard ceiling (300 s on 2021.3, 180 s on 2022.3 and 6000.x) and warns past a 60 s soft budget. Write new budget assertions RED first.

### Single-thread contract

- Bus operations - registration, emission, deregistration, interceptor and post-processor changes - are single-threaded by contract. The dispatch hot path carries no thread-safety primitives on purpose.
- Do not add `lock`, `Interlocked`, or `volatile` to dispatch code, and do not wrap registration in locks "to be safe". Callers needing cross-thread emission marshal onto the main thread instead.
- `Tests/Runtime/Core/SingleThreadContractTests.cs` pins the current behavior: `BusOperationFromNonMainThreadDoesNotCrash` (no exception escapes, handler runs at least once) and `RepeatedSerialEmitProducesDeterministicCounts` (50 serial emits produce exactly 50 invocations). Do not edit these to make a speculative concurrency change pass; the failure is the signal.
- Changing the contract requires maintainer agreement first, a deliberate sentinel update, a `### Changed` CHANGELOG entry, README and docs updates, and a benchmarked choice of lock strategy.

## References

| Document                                                                  | Purpose                                                                                                        |
| ------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| [fast-unity-tests.md](./references/fast-unity-tests.md)                   | The five speed levers, the reload/frame cost model, the four drift guards, and the MCP measurement protocol.   |
| [single-thread-contract.md](./references/single-thread-contract.md)       | The single-threaded bus contract, its sentinel tests, and the procedure for changing it.                       |
| [test-categories-execution.md](./references/test-categories-execution.md) | Running categories from the Test Runner window, the CLI `-testFilter`, CI jobs, and a TestRunnerApi menu item. |
| [test-categories-part-1.md](./references/test-categories-part-1.md)       | Category hygiene do/don't list and `[Explicit]` quarantine conventions.                                        |
| [test-categories.md](./references/test-categories.md)                     | The `[Category]` taxonomy, fixture and per-test application, time budgets, and per-context execution strategy. |
| [upm-test-harness.md](./references/upm-test-harness.md)                   | The generated UPM host project, `testables`, adding asmdefs and test dependencies, commit-vs-gitignore split.  |
