---
title: "Allocation Coverage Required for Dispatch"
id: "allocation-coverage-required-for-dispatch"
category: "testing"
version: "1.0.0"
created: "2026-05-01"
updated: "2026-05-01"

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
   for paths that must allocate exactly zero managed bytes per call, and a
   hand-rolled `GC.GetTotalAllocatedBytes(precise: true)` delta with an
   explicit `Is.LessThanOrEqualTo(byteBudget)` for paths where a small,
   documented ceiling is intentional (for example registration and
   deregistration).
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
registration unavoidably produces. For those, measure a delta with
`GC.GetTotalAllocatedBytes(precise: true)` after warming the path to steady
state, and assert against an explicit byte budget:

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

        long before = GC.GetTotalAllocatedBytes(precise: true);
        MessageRegistrationHandle measured = RegisterHandler(scenario, token);
        long after = GC.GetTotalAllocatedBytes(precise: true);
        long delta = after - before;
        token.RemoveRegistration(measured);

        Assert.That(
            delta,
            Is.LessThanOrEqualTo(PerRegistrationByteBudget),
            $"Register-{scenario.Kind} allocated {delta} bytes; "
                + $"budget is {PerRegistrationByteBudget} bytes."
        );
    });
}
```

Declare `PerRegistrationByteBudget` as a `private const long` at the top of
the fixture and document it with an XML comment explaining what the bytes
pay for, so reviewers can audit relaxations.

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
