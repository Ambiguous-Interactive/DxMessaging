---
title: "Memory Reclaim Coverage"
id: "memory-reclaim-coverage"
category: "testing"
version: "1.0.0"
created: "2026-05-04"
updated: "2026-05-04"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/MemoryReclaim/MemoryReclamationTests.cs"
    - path: "Tests/Runtime/TestUtilities/LeakWatcher.cs"
    - path: "Tests/Editor/Allocations/AllocationMatrixTests.cs"
    - path: "Tests/Editor/Contract/MessageBusInvariantTests.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "memory"
  - "reclamation"
  - "allocation"
  - "leaks"

complexity:
  level: "intermediate"
  reasoning: "Tests are mechanical once the memory holder is identified, but they must cover slot counts, stale deregistration, and allocation budgets."

impact:
  performance:
    rating: "high"
    details: "Prevents regressions that would reintroduce unbounded retained slots or dispatch allocations."
  maintainability:
    rating: "high"
    details: "Gives future cache additions a required test checklist."
  testability:
    rating: "critical"
    details: "Defines the coverage expected for every message-type and InstanceId memory holder."

prerequisites:
  - "leak-watcher-usage"
  - "allocation-coverage-required-for-dispatch"
  - "tests-must-be-parameterized-by-message-kind"

dependencies:
  packages: []
  skills:
    - "memory-reclamation"
    - "leak-watcher-usage"
    - "allocation-coverage-required-for-dispatch"
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
  - "MemoryReclaim tests"
  - "slot leak tests"
  - "trim coverage"

related:
  - "memory-reclamation"
  - "leak-watcher-usage"
  - "allocation-coverage-required-for-dispatch"
  - "tests-must-be-parameterized-by-message-kind"

status: "stable"
---

# Memory Reclaim Coverage

> **One-line summary**: Every DxMessaging memory holder keyed by message type or
> `InstanceId` must have explicit trim, idle-sweep, slot-count, and allocation
> coverage.

## Overview

Memory reclamation is a runtime guarantee, not an implementation detail. A new
dictionary, list, stack, or cache keyed by message type or context can create a
session-length retention bug unless tests prove it empties and trims.

Use the `MemoryReclaim` category for direct reclamation tests. Use
`LeakWatcher.WatchWithSlots` when a test expects slot counts to return to the
baseline. Use `AllocationMatrixTests` when the reclaim path could affect
zero-allocation dispatch or bounded trim budgets.

## Solution

Start each change by naming the holder that can retain memory, then write the
smallest test that proves it becomes empty, is swept, and leaves future
registrations safe after stale teardown callbacks run.

## Coverage Rule

Every memory holder gets a reclamation test. The minimum proof is:

1. Create the holder through public registration or emit APIs.
1. Deregister or otherwise make the holder empty.
1. Assert `OccupiedTypeSlots` or `OccupiedTargetSlots` increased before trim.
1. Run `Trim(force: true)` or age the slot and trigger an idle sweep.
1. Assert slot counts return to the pre-test baseline.
1. Assert a stale deregistration closure is a no-op after sweep when the holder
   has deregistration handles.

## LeakWatcher Slots

`LeakWatcher.Watch()` checks registration counters only. Use
`LeakWatcher.WatchWithSlots()` for tests where trim is part of the expected
cleanup.

```csharp
using (LeakWatcher watcher = LeakWatcher.WatchWithSlots(label: scenario.DisplayName))
{
    IMessageBus bus = MessageHandler.MessageBus;
    int baseline = bus.OccupiedTypeSlots;
    MessageRegistrationHandle handle = RegisterSomething(scenario);

    handle.Deregister();
    Assert.GreaterOrEqual(bus.OccupiedTypeSlots, baseline + 1);

    _ = bus.Trim(force: true);
}
```

Slot deltas are compared to the watcher's starting snapshot. Tests do not need
the whole bus to be empty; they need the watched region to return to its own
baseline.

## MemoryReclaim Category

Place direct reclamation fixtures under `Tests/Runtime/MemoryReclaim` and mark
the fixture or tests with `[Category("MemoryReclaim")]`. This category is
opt-in because it can create many message types or `InstanceId` values and can
run longer than the default suite budget.

The current runtime budget tests treat `MemoryReclaim` like `Stress`,
`Performance`, and `Allocation`: when it is selected, the default-suite
wall-clock assertion is skipped. If a Unity Test Runner workflow adds category
matrices, include an explicit `MemoryReclaim` leg.

## Allocation Budget Pattern

`AllocationMatrixTests` owns dispatch allocation guarantees. Reclamation work
that changes trim or post-trim emit behavior needs allocation coverage:

- Forced trim should remain bounded by `TrimAllocBudget`.
- Emitting after a partial trim should remain zero-allocation.
- Allocation tests that exercise multiple message kinds must use
  `MessageScenarios.AllKinds`.

## Adding a Holder

When adding a dictionary, list, stack, pool, or cache keyed by message type or
`InstanceId`:

1. Identify whether it contributes to type-slot or target-slot occupancy.
1. Add it to `OccupiedTypeSlots` or `OccupiedTargetSlots` if users need to see
   the footprint.
1. Add a forced-trim test in `MemoryReclamationTests`.
1. Add an idle-sweep test if the holder is eligible for idle reclamation.
1. Add stale-deregistration coverage when handles can outlive the slot.
1. Add allocation coverage when the holder participates in emit or trim paths.
1. Add `MessageBusInvariantTests` coverage when the holder is a
   `MessageCache<>` field.

## See Also

- [DxMessaging Memory Reclamation](../performance/memory-reclamation.md)
- [LeakWatcher: Detecting Registration Leaks in Tests](./leak-watcher-usage.md)
- [Allocation Coverage Required for Dispatch](./allocation-coverage-required-for-dispatch.md)
- [Tests Must Be Parameterized by Message Kind](./tests-must-be-parameterized-by-message-kind.md)

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-04 | Initial version |
