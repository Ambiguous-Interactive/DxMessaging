---
title: "DxMessaging Dispatch Hot Path"
id: "dispatch-hot-path"
category: "performance"
version: "1.0.0"
created: "2026-05-05"
updated: "2026-05-05"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/Core/MessageBus/MessageBus.cs"
    - path: "Runtime/Core/MessageHandler.cs"
    - path: "Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs"
    - path: "Tests/Editor/Allocations/EmitGateClockReadIsRare.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "dispatch"
  - "hot-path"
  - "throughput"
  - "messaging"
  - "il2cpp"
  - "mono"

complexity:
  level: "advanced"
  reasoning: "Requires understanding the per-message-type dispatch state machine, dispatch snapshot lifecycle, and platform-specific JIT/AOT codegen behavior."

impact:
  performance:
    rating: "critical"
    details: "Every message emission walks this path; small per-emit overhead multiplies into measurable throughput regressions."
  maintainability:
    rating: "high"
    details: "Centralized rule set lets reviewers reject hot-path changes that violate the budget."
  testability:
    rating: "high"
    details: "T0 benchmark harness, EmitGateClockReadIsRare, and AllocationMatrix tests pin compliance."

prerequisites:
  - "memory-reclamation"
  - "aggressive-inlining"
  - "allocation-coverage-required-for-dispatch"

dependencies:
  packages: []
  skills:
    - "memory-reclamation"
    - "sweep-gate-must-be-cheap"
    - "aggressive-inlining"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
    - ".NET"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.1"

aliases:
  - "DxMessaging emission perf"
  - "dispatch loop"
  - "RunHandlers"
  - "AcquireDispatchSnapshot"

related:
  - "memory-reclamation"
  - "sweep-gate-must-be-cheap"
  - "aggressive-inlining"
  - "array-pooling"

status: "stable"
---

# DxMessaging Dispatch Hot Path

> **One-line summary**: The emission path through `MessageBus` and
> `MessageHandler` carries a strict zero-allocation, near-zero-overhead
> contract; per-emit operations are budgeted in nanoseconds, not "fine".

## Overview

Every message a caller emits walks the same critical path: enter the bus,
acquire a dispatch snapshot, walk per-priority buckets, invoke each handler.
On a 1M emits/sec workload, every nanosecond added to the per-emit prologue
costs a measurable percentage of throughput. Adding work that "feels small"
on a single call (a clock read, a virtual through an unsealed type, an extra
field write) compounds into 30-50% regressions when multiplied across the
workload.

This skill documents the prohibited operations, the established patterns,
and the test gates that enforce them.

## Hot-path file map

The dispatch hot path lives across:

- `Runtime/Core/MessageBus/MessageBus.cs` -- `UntargetedBroadcast`,
  `TargetedBroadcast`, `SourcedBroadcast`, `RunHandlers`, `RunPostProcessing`,
  `AcquireDispatchSnapshot`, `EnterDispatch`, `TrySweepIdle`.
- `Runtime/Core/MessageHandler.cs` -- `TypedHandler<T>.HandleUntargeted`,
  `HandleTargeted`, `HandleBroadcast`, the 10 `*DispatchLink<TMessage>` classes,
  `HandlerActionCache<T>` invocation paths.
- `Runtime/Core/Pooling/*.cs` -- anything called from those sites.

Any PR touching these files has its dispatch-throughput numbers regenerated
automatically by the `perf-numbers.yml` workflow (it re-runs the benchmarks on
ELI-MACHINE at the latest Unity version on every PR change and posts the refreshed
numbers as a non-blocking PR comment; after merge, CI opens or updates a
default-branch refresh PR for the committed `docs/architecture/performance.md`
table). There is no manual `### Performance numbers` PR-body requirement.

## Prohibited operations on the dispatch hot path

The following are forbidden inside `RunHandlers`, `AcquireDispatchSnapshot`,
`Handle*` methods on `TypedHandler<T>`, `*DispatchLink<TMessage>.Invoke`, and
the per-priority handler iteration in `HandlerActionCache<T>`:

1. **Unconditional clock reads** (`Stopwatch.GetTimestamp`,
   `Time.realtimeSinceStartup`, any `IDxMessagingClock.NowSeconds` call).
   `Stopwatch.GetTimestamp()` is a vDSO syscall (~15-20ns x64,
   ~60-80ns on iOS ARM Mono). The sweep gate samples the clock at most once
   per `SweepGateMask + 1` emissions; see `sweep-gate-must-be-cheap`.
1. **Allocations.** No `new`-ing reference types. All transient buffers come
   from `DxPools` or pooled snapshot arrays. The `AllocationMatrixTests`
   suite catches violations.
1. **Syscalls / P/Invokes.** No file or socket operations. No reading
   `Environment.*` properties (most are P/Invokes).
1. **Virtual / interface dispatch through unsealed types.** Unity Mono lacks
   guarded devirtualization; sealed types let the JIT inline. Every class on
   the dispatch chain must be `sealed` or the method must be non-virtual.
1. **Boxing.** Never let a struct message hit an `object` field. Keep the
   `ref TMessage where TMessage : IMessage` shape end-to-end.
1. **`ArrayPool<T>.Shared.Rent` / `Return`.** The shared pool uses
   `Interlocked` operations that are very expensive on IL2CPP. Use private
   bus-owned pools or `DxPools` instead.

## Required patterns

### Per-iteration array access via `MemoryMarshal.GetReference`

Replace `entries[h]` indexing with the bounds-check-elision pattern:

```csharp
ref DispatchEntry first = ref MemoryMarshal.GetReference(entries.AsSpan(0, entryCount));
for (int h = 0; h < entryCount; h++)
{
    ref readonly DispatchEntry e = ref Unsafe.Add(ref first, h);
    InvokeUntargetedEntry(ref message, priority, in e);
}
```

`MemoryMarshal.GetReference` exists in Unity 2021.3 via the bundled
`System.Memory` package. **Do NOT** use `MemoryMarshal.GetArrayDataReference`
(added in .NET 5; not in Unity 2021.3).

### Per-iteration `DispatchEntry` is passed by `in`, never by value

`DispatchEntry` is a multi-reference struct (24+ bytes). Copying per
iteration costs cycles; passing by `in` does not.

### `[Il2CppSetOption(Option.ArrayBoundsChecks, false)]`

Only on verified-safe inner loop bodies, gated by `#if ENABLE_IL2CPP`.
**Keep `Option.NullChecks` enabled** -- silent SIGSEGV on a null delegate is
unacceptable. Validate inputs at the public API boundary.

### Sealed everywhere on the dispatch chain

Audit `MessageBus`, `MessageHandler.TypedHandler<T>`, every
`*DispatchLink<TMessage>` class, and `HandlerActionCache<T>`. Mono lacks
guarded devirtualization; sealing is load-bearing.

## Per-emit budget

<!-- to be measured by Week 1b T0.3 baseline runs and updated in Week 5 -->

The current empirical budget on Mono Editor is captured in
`progress/perf-baseline-2026-05-05.csv`. Any PR touching the hot-path file list
above should rely on the Performance Numbers workflow output for before/after
dispatch-throughput numbers.

## Enforcement

- `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs` -- the harness.
- `Tests/Editor/Benchmarks/PerfRegressionSmokeTests.cs` -- `[Explicit,
Category("PerfGate")]`; opt-in via `DX_PERF_GATE=1`. Median-of-5; fails
  when within-platform regression vs. baseline CSV exceeds 1.5x.
- `.github/workflows/perf-numbers.yml` -- per-PR workflow that re-runs the
  editmode + playmode dispatch benchmarks on ELI-MACHINE (the `fast` runner) at
  the latest Unity version on every pull_request change and posts the regenerated
  dispatch-throughput numbers as a non-blocking PR comment; the committed
  `docs/architecture/performance.md` is refreshed by a default-branch bot PR
  after the PR merges. The numbers are owned by CI, not by PR-body text.

## Common pitfalls

- "It's just a single field write." Per-emit field writes on the hot path
  compound. The `Touch()` field write inside `AcquireDispatchSnapshot` was
  ~1-2ns by itself but participated in the GC landing's combined regression.
  Measure first.
- "I'll add a virtual call here, the JIT will devirtualize." Mono will not.
  IL2CPP has limited devirtualization. Seal the type or pay the cost.
- "I'll use `ArrayPool<T>.Shared`." See above. Use private pools or
  `DxPools`.
- "I'll add a clock read just for diagnostics." Diagnostics that read the
  clock per emit count toward the budget. Sample-not-call (see
  `sweep-gate-must-be-cheap`) or capture once at scope entry.

## See also

- [Sweep Gate Must Be Cheap](./sweep-gate-must-be-cheap.md)
- [DxMessaging Memory Reclamation](./memory-reclamation.md)
- [Aggressive Inlining](./aggressive-inlining.md)
- [Allocation Coverage Required for Dispatch](../testing/allocation-coverage-required-for-dispatch.md)
