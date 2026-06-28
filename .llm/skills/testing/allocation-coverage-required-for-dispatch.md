---
title: "Allocation Coverage Required for Dispatch"
id: "allocation-coverage-required-for-dispatch"
category: "testing"
version: "1.7.0"
created: "2026-05-01"
updated: "2026-06-28"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Editor/Allocations/AllocationMatrixTests.cs"
    - path: "Tests/Editor/Allocations/RegistrationAllocationCountTests.cs"
    - path: "Tests/Editor/RegistrationStorageStructuralGuardTests.cs"
    - path: "Tests/Runtime/TestUtilities/AllocationAssertions.cs"
    - path: "Tests/Runtime/TestUtilities/MessageScenarios.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "allocation"
  - "performance"
  - "messaging"
  - "zero-gc"
  - "benchmark"
  - "unity"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of GC measurement, NUnit ValueSource, and the project's allocation harness."

impact:
  performance:
    rating: "critical"
    details: "Pins the zero-GC contract for every dispatch path."
  maintainability:
    rating: "high"
    details: "Forces new dispatch paths to declare their allocation behaviour up front."
  testability:
    rating: "critical"
    details: "Allocation regressions surface inside the test suite, not in user benchmarks."

prerequisites:
  - "comprehensive-test-coverage"
  - "tests-must-be-parameterized-by-message-kind"

dependencies:
  packages: []
  skills:
    - "comprehensive-test-coverage"
    - "tests-must-be-parameterized-by-message-kind"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - "NUnit"
  versions:
    unity: ">=2021.3"

aliases:
  - "Zero-GC dispatch contract"
  - "Allocation matrix coverage"

related:
  - "tests-must-be-parameterized-by-message-kind"
  - "comprehensive-test-coverage"
  - "test-categories"
  - "single-thread-contract"

status: "stable"
---

# Allocation Coverage Required for Dispatch

> **One-line summary**: Every new `Emit*` method, every new dispatch path, and
> every new `MessageKind` value must be represented by a row in the allocation
> matrix - otherwise the zero-GC contract is unprotected.

## Overview

DxMessaging promises zero managed allocations on the steady-state dispatch
path. A regression there is silent: messages still flow, callers still receive
them, only the GC profile gets worse - and only at scale. The defense is a
matrix of allocation tests pinned in
`Tests/Editor/Allocations/AllocationMatrixTests.cs` that asserts byte budgets
on the bare register / emit / deregister surface across every dispatch axis
(kind, interceptor presence, post-processor presence, diagnostics, priority).

If a new dispatch path lands and is not covered by the matrix, the contract
silently weakens. This skill is the rule against that.

## Problem Statement

Consider the trap:

```csharp
// New API added to MessageBusExtensions.cs
public static void EmitWithMetadata<TMessage>(
    this ref TMessage message,
    object metadata,
    IMessageBus bus = null)
    where TMessage : IUntargetedMessage
{
    // implementation that boxes 'metadata' once per call
}
```

Functional tests pass. The library still works. But the steady-state path
through `EmitWithMetadata` allocates ~24 bytes per call. Without a row in the
allocation matrix, nothing fails until a downstream user notices their GC
budget blown in production.

## Solution

Two requirements stack:

1. Every dispatch path with a stable signature must have an
   `AllocationMatrixTests` row that exercises it via the appropriate
   parameterized `MessageScenarios` source. Use `AllocationAssertions.AssertNoAllocations`
   for paths that must allocate exactly zero managed bytes per call, and
   `AllocationProbe.MeasureMin` against an explicit COUNT budget for paths where a
   small, documented ceiling is intentional (for example registration and
   deregistration). Never use a `GC.GetTotalAllocatedBytes` / `GC.GetTotalMemory`
   byte delta: under Unity's Boehm GC those under-count (the GC reclaims allocations
   inside the measurement window) and `GC.GetAllocatedBytesForCurrentThread()` returns
   a vacuous 0 for every allocation. Count managed allocation CALLS via the `GC.Alloc`
   recorder (`AllocationProbe`) instead. The benchmark pipeline now ALSO tracks the
   total allocated BYTES per measurement batch (`gcAllocatedBytes`) from the collection-immune
   `"GC Allocated In Frame"` `.CurrentValue` delta, with the SAME honesty guarantees
   as the count: `AllocationProbe.Unmeasured` (`-1`, rendered `n/a`) rather than a
   fabricated `0` where the profiler is stripped, and `0` only for a real zero-byte
   region. Bytes are informational; the allocation COUNT stays the gate (see
   [Benchmark Methodology: Total Over One Window](../performance/benchmark-methodology-total-over-window.md)).
1. Every `MessageKind` value must appear in
   `MessageScenarios.AllKindsIncludingWithoutContext`. Anything driven by
   `[ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKindsIncludingWithoutContext))]`
   automatically picks up the new kind once it lands there. Tests that
   intentionally cover only the context-bound surfaces should use
   `MessageScenarios.AllKinds`.

### Adding a Zero-Allocation Row

Patterned after `EmitIsZeroAlloc` in
`Tests/Editor/Allocations/AllocationMatrixTests.cs`:

```csharp
[Test]
[Category("Allocation")]
public void EmitWithMetadataIsZeroAlloc(
    [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKindsIncludingWithoutContext))]
        MessageScenario scenario
)
{
    RunWithFreshHarness(
        scenario,
        (token, bus) =>
        {
            Action emit = BuildEmitWithMetadataClosure(scenario, bus);
            RegisterHandler(scenario, token);
            AllocationAssertions.AssertNoAllocations(
                $"EmitWithMetadata-{scenario.Kind}",
                emit
            );
        }
    );
}
```

`AllocationAssertions.AssertNoAllocations` JIT-warms the action and then
asserts via `Is.Not.AllocatingGCMemory()`, so the closure must be built once
outside the assertion zone or the closure's own allocation contaminates the
measurement.

### Adding a Bounded-Allocation Row

Some dispatch paths legitimately allocate a small, fixed amount per call.
`RegisterIsZeroAllocSteadyState` and
`DiagnosticsAugmentedHandlerAllocationCostIsBounded` in
`AllocationMatrixTests.cs` budget for the closure plus dictionary entry that
registration unavoidably produces. Measure a managed-allocation CALL count with
`AllocationProbe.MeasureMin` and assert against an explicit count budget. `MeasureMin`
runs the operation several times and returns the MINIMUM, which rejects the
intermittent spikes a single window shows in a warm, long-lived editor domain (so the
test is reliable locally, not just on the cold CI legs). Measure the marginal cost of an
ADDITIONAL registration -- let the operation register without removing between attempts.
Do NOT remove the sole handler between attempts to "reset" the token: removing it tears
down and rebuilds the type's dispatch structures, whose cost depends on warm `DxPools`
state and is not stable run-to-run (it reads ~21 isolated but ~140 in a churned suite).
The accumulated handles are released when the harness disposes the token:

```csharp
[Test]
[Category("Allocation")]
public void RegisterIsZeroAllocSteadyState(
    [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKindsIncludingWithoutContext))]
        MessageScenario scenario
)
{
    RunWithFreshHarness(scenario, (token, bus) =>
    {
        for (int i = 0; i < WarmupRegistrationCycles; ++i)
        {
            MessageRegistrationHandle warm = RegisterHandler(scenario, token);
            token.RemoveRegistration(warm);
        }

        long delta = AllocationProbe.MeasureMin(
            AllocationMeasurementAttempts,
            prepare: null,
            operation: () => _ = RegisterHandler(scenario, token)
        );
        if (delta == AllocationProbe.Unmeasured)
        {
            Assert.Ignore("GC.Alloc probe is non-functional on this backend.");
        }

        // Generous budget on purpose: registration rents handler-storage collections
        // from the global DxPools, so its warm-editor floor varies run-to-run (~14-117)
        // for every kind. The tight signal is the cold CI legs (deterministic pool) and
        // the dedicated marginal-registration count test.
        Assert.That(
            delta,
            Is.LessThanOrEqualTo(PerRegistrationCountBudget),
            $"Register-{scenario.Kind} allocated {delta} managed objects; "
                + $"budget is {PerRegistrationCountBudget}."
        );
    });
}
```

Set each budget from the MEASURED floor (via `MeasureMin`) plus a margin -- NOT converted
from a byte budget -- and document it with a comment explaining the floor so reviewers can
audit relaxations. Where a path rents from the shared global `DxPools` (registration
does), its warm-editor floor varies run-to-run with the pool's warmth and `MeasureMin`
cannot subtract that real allocation; budget generously for the warm editor and rely on
the cold CI legs (deterministic pool) plus a dedicated focused count test for the tight
signal, rather than a tight bound that flakes locally.

### Differential Count Guards (subtracting the shared churn)

When the thing you want to pin is the cost DIFFERENCE between two near-identical paths
(for example "the `Action<T>` registration must not allocate more closures than the
`FastHandler<T>` registration"), measure the DIFFERENCE rather than an absolute budget.
Run both paths into independent buses each attempt and take the MINIMUM of
`(pathA - pathB)` over attempts. The shared bus-side / `DxPools` churn that forces the
absolute budgets to be generous cancels in the subtraction, and the minimum filters the
warm-editor spikes (a spike inflates one window, never the floor), so the residual is the
structural delta you care about -- letting you assert a TIGHT tolerance (well below a
one-allocation-per-op regression) that is still robust in the warm editor. Use a static
method group for both handlers so neither window allocates the user delegate itself.
`RegistrationAllocationCountTests.ActionRegistrationAllocatesNoMoreClosuresThanFastHandler`
(handlers) and `...ActionPostProcessorAllocatesNoMoreClosuresThanFastHandler`
(post-processors) are the worked examples (tolerance = half the registration batch).
Prove it red-green by temporarily reverting the optimized path and confirming the
differential blows past the tolerance (the post-processor revert measured +32 over a
tolerance of 8).

When the optimized path stores the RAW user handler as the dedup key and dispatches a
separate diagnostics-augmented closure (the `Action` -> single-`FastHandler` collapse does
this), pair the allocation guard with a CORRECTNESS guard that proves the augmented closure
-- not the raw handler -- is the live dispatch target, or the saving could silently drop
diagnostics. `PostProcessorDiagnosticsTests` does this: it enables token diagnostics,
registers an `Action` post-processor, emits, and asserts the token's per-registration call
count recorded the invocation (only the augmented closure touches `_callCounts`; a zero
count would mean the raw handler was dispatched). Closes the gap that the global-accept-all
slot -- which DOES dispatch `entry.handler` -- warns about.

### Structural Guards (deterministic, backend-independent pins)

When an allocation win is a STRUCTURAL fact -- a field stores a value type instead of a
delegate, a parameter is passed by value instead of through a closure factory, a map stores
the staging function directly instead of a per-item wrapper -- pin it with a deterministic
reflection assertion on the type signature, NOT only a count budget. A structural guard
cannot flake (it never measures allocations) and still fails on the backend where the
`GC.Alloc` probe is unavailable, so it is the most reliable lock for a closure-collapse win.
Worked examples in `RegistrationStorageStructuralGuardTests` (the per-PR EditMode
correctness leg -- so they protect every PR; they were relocated there from the weekly
`Allocation` suite precisely because, being deterministic reflection assertions, they never
flake and benefit from per-PR coverage): `InternalRegisterPassesMetadataByValueNotFactory`
(asserts the metadata parameter is the `MessageRegistrationMetadata` struct, never a
`Func<...>` factory) and `RegistrationsStoreStagingFunctionNotWrapperAction` (asserts the
token's `_registrations` value type is `Func<MessageRegistrationHandle, Action>` -- the
staging function stored directly -- not a per-registration `Action` wrapper, which would
re-introduce one delegate plus its display class per registration). Prove it red-green by
reverting the optimization and confirming the type-signature assertion flips. A structural
guard is necessary, not sufficient: pair it with the behavioral count/differential guards in
the `Allocation` suite (`RegistrationAllocationCountTests`) -- a revert that kept the field
type but re-wrapped elsewhere would slip past the structural test alone. Keep the
deterministic structural pins in the per-PR leg and the probe-based count budgets in the
weekly `Allocation` leg, so every PR gets the flake-free guard and the noisy count lives
where its warm-editor denoising belongs.

When a closure-collapse win is uniform across many near-identical paths (every registration
kind), the warm-editor accumulating-token count test is too noisy to read a small per-path
delta -- min-over-attempts on a single growing token swings with mid-window dictionary/array
resizes. Measure the delta instead as a COLD TOTAL: a fresh bus+token per attempt, register
N handlers of one kind, take the minimum over attempts. Cold totals grow the same way every
attempt, so the floor is deterministic and the before/after delta is trustworthy (the
staging-function collapse measured untargeted 14.69 -> 12.69 allocs/registration, a clean
-2.00). Reserve this for a one-off science measurement; ship the deterministic structural
guard as the regression lock.

When the win is collapsing an eager collection inside a PRIVATE holder whose multi-element
BEHAVIOR also changes (e.g. the per-handle de-registration holder: an eager `List<Action>`
became an inline head plus a lazy overflow list, saving the list object and its backing
array per registration), the type-signature pin is not enough -- the reworked
invoke/remove/rollback logic must match the old collection's semantics exactly. Pin both
with a focused reflection unit test over the private type (resolve it via
`GetNestedType(..., BindingFlags.NonPublic)`, drive `Add`/`InvokeFrom`/`Count` directly):
assert the storage shape (the overflow collection field is null after a single add) AND
re-derive the behavior against the old form (insertion order, partial-failure-retryable,
the rollback `startIndex` baseline) across an exhaustive count x failure x start-index
matrix. Put it in the per-PR EditMode correctness assembly (NOT the `Allocation`-category
suite) so it runs every PR and stays immune to warm-editor allocation-count flakiness --
the example is `PendingDeregistrationStorageTests` (cold-total A/B untargeted 13.29 -> 11.29,
a clean -2.00).

### When an absolute COUNT budget is structurally doomed (prefer a STATE assertion)

An absolute `GC.Alloc` COUNT budget only works when the operation's true floor sits
comfortably above the editor's ambient-noise floor. A warm, long-lived editor domain
attributes background allocations to whatever window is open, and `MeasureMin` cannot push a
measurement below that ambient floor -- it only rejects upward spikes. Measured on the host
editor, token `Create` (a deterministic 7-allocation operation) read a MINIMUM of ~19 over 64
windows (median ~51, p90 ~73): any budget tight enough to catch the diagnostics-revert (+4)
was already below the achievable warm-editor floor, so the absolute-count guard
false-failed run-to-run. When the floor is small and the noise is comparable, an absolute
count budget cannot be both non-flaky AND meaningful -- replace it.

For a LAZY-ALLOCATION win (a field materialized on first use via `??=` rather than eagerly in
the constructor), the strongest replacement is a deterministic STATE assertion, not a count
at all: construct the object in the no-diagnostics state and assert the lazy backing field is
still `null` (proof the constructor allocated nothing for it). It uses no allocation probe,
never flakes, and runs in the per-PR correctness leg. The example is
`RegistrationDiagnosticsLazyAllocationTests.TokenCreateDoesNotEagerlyAllocateDiagnosticsCollections`
(asserts `_callCountsBacking`/`_emissionBufferBacking` are `null` after `Create`); a revert to
an eager `= new()` field makes them non-null and trips it (proven red-green).

For a BEHAVIORAL operation that already exposes a deterministic RESULT or COUNTER which moves
iff the guarded behavior breaks, assert THAT result directly instead of an allocation count.
Two warm-flaky count budgets were replaced this way (no probe, so they never flake):

- **Repeated forced trim** -- assert via `IMessageBus.TrimResult` that the first force-trim
  reclaims (`TypeSlotsEvicted + TargetSlotsEvicted > 0`) and every subsequent force-trim is an
  idempotent no-op (evicts 0 type/target slots, stable `LiveTypeSlotsRemaining`). `force: true`
  makes the bus's idle check return `true` unconditionally, so one pass clears everything
  eligible and the rest observe a clean bus; unbounded per-call trim work would evict again or
  drift the live count. Example: `AllocationMatrixTests.RepeatedForcedTrimIsIdempotentAfterReclaim`.
- **Dirty-target reuse** -- assert via the `DxPools` Hits/Misses counters that marking rents the
  warmed pooled collection (Hits climb) and NEVER allocates a fresh one (Misses stay flat across
  disjoint mark/return cycles). The Misses-equality is exact and strictly STRONGER than the count
  budget it replaced -- it catches a per-target rent-and-allocate regression even at
  targetCount=1, where a count delta could not separate one extra allocation from the warm-editor
  floor. Example: `AllocationMatrixTests.DirtyTargetTrackingIsAllocationFreeAfterWarmup`.

The rule generalizes: STATE (a lazy field is `null`) or RESULT/COUNTER (a `TrimResult` eviction
count, a pool Hits/Misses delta) beats an absolute `GC.Alloc` budget whenever the operation
exposes a deterministic signal that breaks exactly when the optimization regresses.

Measure-first correction worth remembering: do NOT assume registration allocation noise comes
from `DxPools` rental. A per-pool probe showed the steady refcount registration path never
rents from the typed-handler pools (hits = misses = 0), so a deterministic pool pre-warm would
not have reduced the swing -- the noise was pure background-editor `GC.Alloc` pollution.
Attribute the noise source with data before "fixing" the wrong layer.

## Enforcement

`Tests/Runtime/Core/TestAttributeContractTests.cs` contains
`EveryEmitPathHasAllocationCoverage`. The test enumerates every
`MessageKind` value via reflection and asserts that
`MessageScenarios.AllKindsIncludingWithoutContext` yields a scenario for each.
Adding a new kind without updating the full-surface source - and therefore the
tests that consume it - fails the build.

The contract pin is intentionally narrow (kind enumeration). It cannot prove
that every individual `Emit*` method is covered, but it does guarantee the
matrix's parameterization stays in sync with the kind enum, which is the most
common drift point.

## Best Practices

### Do

- Add an allocation matrix row in the same PR that introduces a new
  dispatch path.
- Tag every allocation test with `[Category("Allocation")]` so the
  default-suite speed budget skips them.
- Use `MessageScenarios.AllKindsIncludingWithoutContext` for full dispatch-surface
  rows, or a narrower source when the test intentionally covers only a subset.
- Build emit closures outside the assertion zone.

### Don't

- Don't measure inside `[SetUp]` / `[TearDown]`; the harness state is not
  guaranteed stable.
- Don't add a kind to `MessageKind` without adding it to
  `MessageScenarios.AllKindsIncludingWithoutContext`; the contract test will fail.
- Don't relax a budget without explaining the new ceiling in the test's
  XML doc comment.

## See Also

- [Tests Must Be Parameterized by Message Kind](tests-must-be-parameterized-by-message-kind.md)
- [Test Coverage Requirements](comprehensive-test-coverage.md)
- [Test Categories for Selective Execution](test-categories.md)
- [Single Thread Contract](single-thread-contract.md)

## References

- NUnit `ValueSource` documentation: https://docs.nunit.org/articles/nunit/writing-tests/attributes/valuesource.html

## Changelog

| Version | Date       | Changes                                                                                                               |
| ------- | ---------- | --------------------------------------------------------------------------------------------------------------------- |
| 1.7.0   | 2026-06-28 | Add RESULT/COUNTER state assertions (Trim idempotency, DxPools Hits/Misses); relocate structural guards to per-PR leg |
| 1.6.0   | 2026-06-28 | Add STATE-assertion-over-doomed-count-budget subsection + DxPools-noise correction                                    |
| 1.5.0   | 2026-06-28 | Add private-holder storage-shape + behavioral reflection-guard pattern                                                |
| 1.4.0   | 2026-06-28 | Add Structural Guards subsection (type-signature pins + cold-total A/B)                                               |
| 1.3.0   | 2026-06-27 | Add `gcAllocatedBytes` byte-tracking honesty note alongside the count                                                 |
| 1.2.0   | 2026-06-26 | Add post-processor differential guard + augmented-closure correctness                                                 |
| 1.1.0   | 2026-06-26 | Add Differential Count Guards subsection                                                                              |
| 1.0.0   | 2026-05-01 | Initial version                                                                                                       |
