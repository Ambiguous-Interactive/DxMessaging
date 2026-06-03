---
title: "Single Thread Contract"
id: "single-thread-contract"
category: "testing"
version: "1.0.0"
created: "2026-05-01"
updated: "2026-05-01"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/Core/SingleThreadContractTests.cs"
    - path: "Runtime/Core/MessageBus/MessageBus.cs"
    - path: "Runtime/Core/MessageHandler.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "concurrency"
  - "threading"
  - "messaging"
  - "contract"
  - "unity"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of the documented threading contract and the cost of changing it."

impact:
  performance:
    rating: "high"
    details: "Adding locks or interlocked operations on the dispatch path costs measurable throughput."
  maintainability:
    rating: "high"
    details: "Pinning the contract prevents speculative concurrency code from accumulating."
  testability:
    rating: "high"
    details: "Behaviour under cross-thread misuse is documented and tested rather than left implicit."

prerequisites:
  - "comprehensive-test-coverage"

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
  - "Threading contract"
  - "DxMessaging is single-threaded"

related:
  - "tests-must-be-parameterized-by-message-kind"
  - "allocation-coverage-required-for-dispatch"
  - "comprehensive-test-coverage"

status: "stable"
---

# Single Thread Contract

> **One-line summary**: DxMessaging buses are single-threaded. Do not add
> `lock`, `Interlocked`, or any other concurrency primitive to the dispatch
> path without a deliberate contract change reviewed with the maintainer.

## Overview

DxMessaging is built on the assumption that all bus operations - registration,
emission, deregistration, interceptor / post-processor manipulation - happen
on a single thread (typically Unity's main thread). The dispatch hot path
contains no thread-safety primitives because adding them would impose a
throughput cost on every single emission for a guarantee almost no caller
needs.

The contract is documented and pinned by `SingleThreadContractTests.cs`. The
sentinel does NOT assert correctness under concurrency; it asserts that the
current behaviour - "no exception escapes when used cross-thread, but
correctness is on the caller" - does not change silently.

## Problem Statement

Speculative concurrency code is one of the most expensive forms of cargo cult.
Consider a well-meaning PR that adds:

```csharp
// BAD: speculative locking on the dispatch path.
public void UntargetedBroadcast<TMessage>(ref TMessage message)
    where TMessage : IUntargetedMessage
{
    lock (_dispatchLock)
    {
        // ... existing dispatch ...
    }
}
```

Every emission now pays a `Monitor.Enter` / `Monitor.Exit` pair. The library's
zero-GC story still holds, but throughput on the hot path drops measurably -
for a guarantee that no real consumer is asking for, on a code path where the
maintainers have explicitly chosen single-threaded semantics.

The contract test makes this kind of change deliberate.

## Solution

Treat the threading contract as a load-bearing invariant.

### What the Contract Says

- Bus operations are not guaranteed thread-safe.
- The dispatch path has no thread checks; calling from a non-main thread will
  not throw, but correctness (ordering, atomicity, visibility) is on the
  caller.
- The current sentinel pins behaviour: "no exception escapes during cross-
  thread emission, and the handler runs at least once."

### What the Sentinel Tests

`Tests/Runtime/Core/SingleThreadContractTests.cs`:

- `BusOperationFromNonMainThreadDoesNotCrash` - emits from a background
  thread, joins the worker, and asserts no exception was captured AND the
  handler ran at least once. If a future change starts throwing on cross-
  thread misuse (a deliberate contract tightening), this test fails and
  forces the maintainer to update the contract documentation.
- `RepeatedSerialEmitProducesDeterministicCounts` - 50 serial emissions on
  the main thread must produce exactly 50 invocations. This is a
  determinism smoke check, not a concurrency test; it pins that the
  single-thread path remains drift-free.

### Changing the Contract

If a future requirement genuinely needs multi-threaded support:

1. Discuss with the maintainer FIRST. Adding locks costs measurable
   throughput. The benefit must be concrete.
1. Update `SingleThreadContractTests.cs` deliberately. The sentinel's
   purpose is to fail when the contract changes.
1. Update CHANGELOG.md with a `### Changed` entry under user-impact
   guidance, and update the README + docs.
1. Decide on the lock strategy explicitly: per-bus lock, per-kind lock,
   reader-writer, lock-free dictionary, etc. Each has different
   performance characteristics; pick one and benchmark.

## Best Practices

### Do

- Treat single-threaded as the default. New features should not assume the
  bus will be touched concurrently.
- Document any thread-safety guarantees in XML doc comments on public
  surface.
- If a caller genuinely needs cross-thread emission, recommend they marshal
  the call onto the main thread (e.g. via Unity `MainThreadDispatcher`) and
  keep DxMessaging single-threaded.

### Don't

- Don't add `lock` / `Interlocked` / `volatile` casually to dispatch code.
- Don't "just to be safe" wrap registrations in locks. The harness is not
  designed for it.
- Don't change `SingleThreadContractTests.cs` to make a speculative
  concurrency PR pass. The test failing IS the signal.

## Enforcement

The sentinel tests in `SingleThreadContractTests.cs` fail when:

- An exception escapes a background-thread emission (contract change toward
  thread-safety enforcement).
- Serial emission counts drift (state corruption).

There is no static analyzer pin; the cultural pin is this skill plus code
review.

## See Also

- [Tests Must Be Parameterized by Message Kind](tests-must-be-parameterized-by-message-kind.md)
- [Allocation Coverage Required for Dispatch](allocation-coverage-required-for-dispatch.md)
- [Test Coverage Requirements](comprehensive-test-coverage.md)

## References

- Unity main-thread invariants: https://docs.unity3d.com/Manual/ExecutionOrder.html

## Changelog

| Version | Date       | Changes         |
| ------- | ---------- | --------------- |
| 1.0.0   | 2026-05-01 | Initial version |
