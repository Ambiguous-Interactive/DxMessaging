---
title: "Allocation Coverage Required for Dispatch"
id: "allocation-coverage-required-for-dispatch"
category: "testing"
version: "1.2.0"
created: "2026-05-01"
updated: "2026-06-26"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Editor/Allocations/AllocationMatrixTests.cs"
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
   recorder (`AllocationProbe`) instead.
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

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-01 | Initial version |
