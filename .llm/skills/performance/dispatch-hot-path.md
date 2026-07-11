---
title: "DxMessaging Dispatch Hot Path"
id: "dispatch-hot-path"
category: "performance"
version: "1.4.0"
created: "2026-05-05"
updated: "2026-07-11"

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
    details: "The DispatchThroughputBenchmarks harness, EmitGateClockReadIsRare, and AllocationMatrix tests pin compliance."

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
  - "mono-vs-il2cpp-optimization-split"
  - "runtime-performance-campaign-decisions"

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
  `TargetedBroadcast`, `SourcedBroadcast`, the steady-state
  `DispatchFlatSnapshot` / `DispatchContextFlatSnapshot` loops,
  `AcquireDispatchSnapshotFast`, `AcquireDispatchSnapshot`, `EnterDispatch`,
  `TrySweepIdle`.
- `Runtime/Core/Internal/FlatDispatch.cs` -- `FlatDispatchEntry<TMessage>` and
  the pooled flat entry arrays the snapshot dispatch loops walk.
- `Runtime/Core/MessageHandler.cs` -- `RunHandlers` / `RunHandlersWithContext`,
  the `FastHandler<TMessage>` invokers, and `HandlerActionCache<T>` invocation
  paths.
- `Runtime/Core/Pooling/*.cs` -- anything called from those sites.

The flat-dispatch redesign replaced the older per-priority `*DispatchLink`
virtual-hop chain with resolved `FlatDispatchEntry` arrays; steady-state
dispatch is now a direct delegate call per entry (see `FlatDispatch.cs`).

Any PR touching these files has its dispatch-throughput numbers regenerated
automatically by the `perf-numbers.yml` workflow. It re-runs the single
published leg at the latest Unity version on every PR change -- a Standalone
IL2CPP Release player, the headline scope (`Debug.isDebugBuild` false) -- and
posts the refreshed numbers as a non-blocking sticky PR comment. The comment's
provenance line carries privacy-safe machine specs (CPU, cores, clock, RAM, GPU,
OS) gathered by `scripts/unity/collect-machine-specs.ps1`, never a hostname or
runner name. After merge, the push run commits the refreshed
`docs/architecture/performance.md` table AND the regenerated master baseline
`docs/architecture/perf-baseline.csv` directly to the default branch via a
GitHub App token push when the App is provisioned and the measured commit is
still the branch tip (no PR); see the
[performance numbers auto-commit runbook](../../../docs/runbooks/perf-numbers-auto-commit.md)
for the App + bypass prerequisite. There is no manual PR-body number requirement.

## Prohibited operations on the dispatch hot path

The following are forbidden inside the steady-state dispatch loops
(`DispatchFlatSnapshot`, `DispatchContextFlatSnapshot`),
`AcquireDispatchSnapshot`, `RunHandlers` / `RunHandlersWithContext`, the
`FastHandler<TMessage>` invokers, and the per-priority handler iteration in
`HandlerActionCache<T>`:

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

### Steady-state dispatch walks a frozen, resolved flat array

`DispatchFlatSnapshot` / `DispatchContextFlatSnapshot` iterate a
`FlatDispatchEntry<TMessage>[]` resolved at snapshot-build time, with plain
`entries[i]` indexing over `[0, count)`:

```csharp
for (int i = 0; i < count; ++i)
{
    ref FlatDispatchEntry<TMessage> entry = ref entries[i];
    if (entry.handler.active)
    {
        entry.invoker(ref message);
        if (_resetGeneration != resetGeneration) { break; } // mid-dispatch reset
    }
}
```

Two per-entry reads are load-bearing and must NOT be hoisted: `entry.handler.active`
(handlers toggle live) and the per-iteration `_resetGeneration` re-read (a handler
may reset the bus mid-dispatch -- the documented reentrancy contract).

### Bounds AND null checks are elided on these loops via `[Il2CppSetOption]`

The shipped loops carry BOTH `[Il2CppSetOption(Option.NullChecks, false)]` and
`[Il2CppSetOption(Option.ArrayBoundsChecks, false)]`. This intentionally
supersedes the older "keep NullChecks on" guidance: `BuildFlatDispatch` fills
`entries[0..count)` with non-null handler+invoker pairs and never publishes
`count > entries.Length`, and the array is frozen for the emission (single-
threaded bus; mutations surface on the NEXT emission's rebuild), so the elided
checks are safe by construction. Rig builds keep a `DXMESSAGING_INTERNAL_CHECKS`
shape assert. These attributes are IL2CPP-only; Mono keeps the JIT-emitted
checks. Do NOT try to port the elision to Mono via `Unsafe`/`MemoryMarshal`: the
entry struct holds managed references (GC-relocation-unsafe to pointer-walk) and
`System.Runtime.CompilerServices.Unsafe` is absent from IL2CPP players
(`Runtime/Core/Internal/DxUnsafe.cs` wraps `UnsafeUtility` for this reason). See
[Mono vs IL2CPP Optimization Split](./mono-vs-il2cpp-optimization-split.md).

### IL2CPP AOT bridge is rooted on first emit, not per emit

`EnsureAot{Untargeted,Targeted,Sourced}Bridge<T>()` is
`[Conditional("ENABLE_IL2CPP")]` and roots the untyped-dispatch bridge for `T`.
It runs in the dispatch-plan-creation block (the first typed emit per bus), NOT
on every emit, so the IL2CPP steady-state path does not pay a per-emit
generic-static-init check + non-inlined call. Keep it there: the bridge is a
process-global one-way latch that only must be rooted before the first UNTYPED
dispatch of `T`, which every `Register*<T>` path and the first typed emit both
guarantee. On Mono the calls are compiled out (a provable no-op). Guarded by the
`UntypedDispatchTests.TypedDispatchSeedsBridgeForPrivateManualMessageBeforeUntypedDispatch`
fixture, which runs on the standalone IL2CPP leg.

### Sealed everywhere on the dispatch chain

Audit `MessageBus`, `MessageHandler.TypedHandler<T>`, the sealed flat-dispatch
holders (`FlatDispatch<TMessage>` / `ContextFlatDispatch<TMessage>`), and
`HandlerActionCache<T>`. Mono lacks guarded devirtualization; sealing is
load-bearing.

## Per-emit budget

The budget is interpreted in per-emit nanoseconds (convert throughput with
`1e9 / emits_per_second`). The live numbers live in the rendered tables in
`docs/architecture/performance.md` and the committed master baseline
`docs/architecture/perf-baseline.csv`; rely on the workflow output and the PR
delta comment for before/after numbers rather than a hand-captured table.

### Regression analysis: the dispatch number moved, the code did not

An earlier headline of roughly 15-19M emits/sec dropped to roughly 11M. That
move was mostly a scope change, not a core code regression: the old number was
measured under IL2CPP/AOT, the lower one came from the in-editor Mono (JIT)
scope while that scope was briefly the headline, and a machine change happened
alongside. The headline is again the Standalone IL2CPP Release player, which
restores the AOT data point. The memory-reclamation per-emit additions (the
idle-sweep gate, `TrySweepIdle`, the `Touch()` field write) are cheaply gated
-- the clock is sampled at most once per `SweepGateMask + 1` emissions -- so
they do not account for the difference. When a number moves, confirm the scope,
backend, build configuration, and machine before treating it as a regression.

## Per-scenario warm-up

`DispatchBenchmarkScenarios.WarmupEmits(scenario)` returns the per-scenario
warm-up count: `BenchmarkProtocol.WarmupEmits` (10,000, the default) for every
scenario except the cold registration flood, which returns 0 so it measures
first-touch registration cost. `ComparisonScenarios.WarmupEmits(scenario)`
mirrors that policy for the comparison bridges. The
`BenchmarkProtocol.WarmupEmits = 10_000` constant remains the default.

## Enforcement

- `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs` -- the harness.
- `.github/workflows/perf-numbers.yml` plus
  `scripts/unity/render-perf-deltas.js` -- the PERMANENT regression gate. The
  workflow runs the single published Standalone IL2CPP Release leg at the
  latest Unity version on every pull_request change, posts the regenerated
  numbers as a non-blocking sticky PR comment, then runs
  `render-perf-deltas.js --scope Standalone` against the committed master
  baseline. The script emits `changed=true|false` and `regressed=true|false`;
  the PR job posts a DxMessaging-only delta comment when either signal is true,
  then fails after the comment when a gated Standalone scenario dropped
  throughput beyond the threshold (default 0.33) or increased allocation. A missing or header-only baseline prints both signals false,
  skipping the comment/gate gracefully on first rollout. After the PR merges, the
  push run commits `performance.md` AND `perf-baseline.csv` to the default branch
  via GitHub App, skipping with a warning if App credentials are missing or
  the branch advanced past the measured commit.
- `Tests/Editor/Benchmarks/PerfRegressionSmokeTests.cs` -- a LOCAL tool only,
  `[Explicit, Category("PerfGate")]`, opt-in via `DX_PERF_GATE=1`. It calls
  `DispatchThroughputBenchmarks.RunScenario` (a single continuous
  `BenchmarkProtocol` window, no median) and fails when a within-platform
  regression vs. a captured baseline CSV exceeds 1.5x. Its commit matching was
  relaxed: when `DX_PERF_BASELINE_COMMIT` is unset it matches on scenario +
  platform only, and a no-row match now skips gracefully rather than failing, so
  a contributor on a different Unity version or OS is not blocked.

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

- [Mono vs IL2CPP Optimization Split](./mono-vs-il2cpp-optimization-split.md)
- [Sweep Gate Must Be Cheap](./sweep-gate-must-be-cheap.md)
- [DxMessaging Memory Reclamation](./memory-reclamation.md)
- [Aggressive Inlining](./aggressive-inlining.md)
- [Allocation Coverage Required for Dispatch](../testing/allocation-coverage-required-for-dispatch.md)
- [Runtime Performance Campaign Decisions](./runtime-performance-campaign-decisions.md)
