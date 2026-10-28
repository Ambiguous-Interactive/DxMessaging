---
name: allocation-and-leak-coverage
description: "Protecting the DxMessaging zero-GC dispatch contract and detecting registration leaks in tests: AllocationMatrixTests rows, AllocationProbe.MeasureMin GC.Alloc call counting (never GC.GetTotalAllocatedBytes), differential and structural guards, when a STATE or RESULT assertion beats a count budget, and LeakWatcher usage over the six IMessageBus registration counters. Use when adding an Emit* method or dispatch path, adding a MessageKind, chasing an allocation regression or a flaky allocation budget, or asserting that a register/deregister cycle leaks nothing."
metadata:
  category: "testing"
  tags: "testing, allocation, performance, messaging, zero-gc, benchmark, unity"
---

# Allocation and Leak Coverage

DxMessaging promises zero managed allocations on the steady-state dispatch path. That promise is only real if every dispatch path has a row in the allocation matrix and every register/teardown region is watched for leaked registrations.

## When to use

- Adding an `Emit*` method, a dispatch path, or a `MessageKind` value.
- Writing or relaxing an allocation budget, or a budget flakes in the warm editor.
- Optimizing a closure, delegate, or collection out of the registration path.
- Bracketing a test region that creates and tears down registrations.
- Adding a new public registration counter to `IMessageBus`.

## Rules

### Every dispatch path needs a matrix row

- Rows live in `Tests/Editor/Allocations/AllocationMatrixTests.cs`, tagged `[Category("Allocation")]` so the default-suite speed budget skips them, and are driven by `[ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKindsIncludingWithoutContext))]` for full-surface coverage (use `MessageScenarios.AllKinds` only when the test deliberately covers the context-bound subset).
- Add the row in the same PR that introduces the path. Add every new `MessageKind` to `MessageScenarios.AllKindsIncludingWithoutContext`; `TestAttributeContractTests.EveryEmitPathHasAllocationCoverage` reflects over the enum and fails when the source drifts.
- Build emit closures OUTSIDE the assertion zone, or the closure's own allocation contaminates the measurement. Never measure inside `[SetUp]` or `[TearDown]`.

### Measure allocation CALLS, not bytes

- Zero-allocation paths use `AllocationAssertions.AssertNoAllocations`, which JIT-warms then asserts `Is.Not.AllocatingGCMemory()`.
- Bounded paths use `AllocationProbe.MeasureMin(attempts, prepare, operation)` against an explicit COUNT budget. `MeasureMin` returns the minimum across attempts, rejecting the upward spikes a warm editor domain produces. Handle `AllocationProbe.Unmeasured` with `Assert.Ignore`.
- Never use a `GC.GetTotalAllocatedBytes` or `GC.GetTotalMemory` delta: under Unity's Boehm GC they under-count, and `GC.GetAllocatedBytesForCurrentThread()` returns a vacuous 0. The benchmark pipeline also tracks `gcAllocatedBytes` from the `"GC Allocated In Frame"` `.CurrentValue` delta, but bytes are informational; the allocation COUNT is the gate.
- For a marginal-registration measurement, let registrations accumulate across attempts. Do NOT remove the sole handler between attempts to "reset" the token - that tears down and rebuilds the type's dispatch structures, whose cost depends on warm `DxPools` state (roughly 21 isolated versus 140 in a churned suite).
- Set each budget from the measured floor plus a margin, never converted from a byte figure, and document the floor in a comment so reviewers can audit a relaxation.

### Pick the guard shape that matches the win

- **Differential count guard** - when pinning the cost DIFFERENCE between two near-identical paths. Run both into independent buses each attempt and take the minimum of `(pathA - pathB)`; the shared `DxPools` churn cancels, so a tight tolerance stays stable in a warm editor. Use static method groups so neither window allocates the user delegate. Worked examples: `RegistrationAllocationCountTests.ActionRegistrationAllocatesNoMoreClosuresThanFastHandler` and `...ActionPostProcessorAllocatesNoMoreClosuresThanFastHandler`. Prove red-green by reverting the optimization.
- **Structural guard** - when the win is a type-level fact (a struct stored instead of a delegate, a staging function stored instead of a wrapper). Assert the type signature by reflection; it cannot flake and works where the `GC.Alloc` probe is unavailable. Examples in `RegistrationStorageStructuralGuardTests`: `InternalRegisterPassesMetadataByValueNotFactory` and `RegistrationsStoreStagingFunctionNotWrapperAction`. Keep these in the per-PR EditMode leg and the probe-based counts in the weekly `Allocation` leg. A structural guard is necessary, not sufficient - pair it with a behavioral count or differential guard.
- **Private-holder guard** - when collapsing an eager collection inside a private type also changes multi-element behavior. Resolve the type via `GetNestedType(..., BindingFlags.NonPublic)`, assert the storage shape (overflow field null after one add) AND re-derive the old semantics (insertion order, partial-failure retry, rollback `startIndex`) across a count x failure x start-index matrix. Example: `PendingDeregistrationStorageTests`.
- **STATE or RESULT assertion** - when the operation's true floor sits inside the editor's ambient noise, an absolute count budget cannot be both non-flaky and meaningful. Token `Create` is a deterministic 7-allocation operation that read a minimum of ~19 over 64 windows, so a budget tight enough to catch a +4 regression false-failed. Replace it: for a lazy-allocation win assert the backing field is still `null` (`RegistrationDiagnosticsLazyAllocationTests.TokenCreateDoesNotEagerlyAllocateDiagnosticsCollections`); for a behavioral win assert the deterministic result (`AllocationMatrixTests.RepeatedForcedTrimIsIdempotentAfterReclaim` via `IMessageBus.TrimResult`, `AllocationMatrixTests.DirtyTargetTrackingIsAllocationFreeAfterWarmup` via `DxPools` Hits/Misses).
- When an optimized path stores the RAW user handler as the dedup key and dispatches a separate diagnostics-augmented closure, pair the allocation guard with a correctness guard proving the augmented closure is the live dispatch target. `PostProcessorDiagnosticsTests` does this via the token's per-registration call count.
- Attribute noise with data before fixing a layer: a per-pool probe showed the steady refcount registration path never rents from the typed-handler pools (hits = misses = 0), so the swing was background editor pollution, not `DxPools`.

### LeakWatcher is the only leak-detection mechanism

- `Tests/Runtime/TestUtilities/LeakWatcher.cs` captures six public `IMessageBus` counter pairs and requires every pair to return to baseline. It also uses the built-in bus's cold internal live-registration count, including shared-priority callbacks and duplicate reference counts. The forwarding `DelegatingMessageBus` test double exposes its accounting source; independent-storage doubles can opt out. Do not re-implement counter math inline.
- Arbitrary custom buses expose only their public counter granularity. A count check cannot prove ownership when one registration replaces another without changing any observed count. Pair it with exact callback/ownership assertions; keep retained storage and object references separate.
- Default form is `using (LeakWatcher.Watch(label: scenario.DisplayName)) { ... }`. To inspect without failing, construct with `throwOnLeak: false` and read `LeakedRegistrations` before disposal.
- Pass `handler: handler` to the constructor or `WatchWithSlots(bus, ...)` when testing typed-handler reclamation. This adds a cold internal query for retained context keys and scalar/context priority caches on that bus. `LeakedHandlerContexts` and `LeakedHandlerPriorityCaches` remain separate from registration and bus-slot deltas; disposal freezes the counts. Default watchers do not query handler storage.
- `Snapshot` and live `LeakedRegistrations` reads traverse the built-in bus's registration stores or the custom bus's public counters; optional handler queries also traverse typed slots and contexts. Capture at region boundaries, not in each iteration or dispatch. Disposal freezes the recorded delta. Keep the watcher out of Allocation and Performance fixtures. GameObject and NativeArray leaks are out of scope.
- Adding a seventh registration kind means updating, in lock-step: `IMessageBus`, `Tests/Runtime/Core/Snapshots/public-surface.txt`, `LeakWatcher` snapshots, `Snapshot` fallback, individual-pair invariant and message format, plus the built-in live-registration query, a row in `LeakWatcherSelfTests`, and the Public-Counter Contract section of the reference. The public type-set snapshot checks types; the new registration regression must prove that counter and reference accounting include the new kind.

## References

| Document                                                                                                  | Purpose                                                                                                                                                                                                  |
| --------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [allocation-coverage-required-for-dispatch.md](./references/allocation-coverage-required-for-dispatch.md) | The allocation matrix contract: zero and bounded rows, `AllocationProbe` measurement rules, differential/structural/private-holder guards, and when a STATE or RESULT assertion replaces a count budget. |
| [leak-watcher-usage.md](./references/leak-watcher-usage.md)                                               | `LeakWatcher` live-registration and public-counter contract, accounting limits, cold-query cost, self-tests, and the procedure for adding a counter.                                                     |
