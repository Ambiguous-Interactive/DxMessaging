---
name: dispatch-hot-path
description: "The zero-allocation, nanosecond-budgeted rules for the DxMessaging emission path through MessageBus, FlatDispatch, and MessageHandler, including the prohibited per-emit operations and the sweep-gate cadence. Use when editing MessageBus.cs, FlatDispatch.cs, MessageHandler.cs, or Runtime/Core/Pooling, when adding anything to the per-emit prologue, when dispatch throughput moves in perf-numbers.yml, or when deciding where to put AggressiveInlining."
metadata:
  category: "performance"
  tags: "dispatch, hot-path, throughput, messaging, il2cpp, mono"
---

# DxMessaging Dispatch Hot Path

Every emit walks the same path: enter the bus, acquire a dispatch snapshot, walk
the resolved flat entry array, invoke each handler. At roughly 1M emits/sec, a
nanosecond added to the per-emit prologue is a measurable share of throughput.

## When to use

- Editing `Runtime/Core/MessageBus/MessageBus.cs`,
  `Runtime/Core/Internal/FlatDispatch.cs`, `Runtime/Core/MessageHandler.cs`, or
  anything under `Runtime/Core/Pooling/`.
- Adding a field write, counter, clock read, diagnostic, or virtual call that
  runs once per emit.
- Reviewing a `perf-numbers.yml` delta comment that shows a throughput drop.
- Touching the idle-eviction sweep gate or `TrySweepIdle`.
- Deciding whether `[MethodImpl(MethodImplOptions.AggressiveInlining)]` belongs
  on a method.

## Rules

### Prohibited inside the steady-state dispatch loops

These apply to `DispatchFlatSnapshot`, `DispatchContextFlatSnapshot`,
`AcquireDispatchSnapshot`, `RunHandlers`/`RunHandlersWithContext`, the
`FastHandler<TMessage>` invokers, and `HandlerActionCache<T>` iteration:

1. Unconditional clock reads (`Stopwatch.GetTimestamp`,
   `Time.realtimeSinceStartup`, `IDxMessagingClock.NowSeconds`).
   `Stopwatch.GetTimestamp()` is a vDSO syscall: ~15-20 ns on x64, ~60-80 ns on
   iOS ARM Mono.
1. Allocations. No `new` on reference types; transient buffers come from
   `DxPools` or pooled snapshot arrays. `AllocationMatrixTests` catches this.
1. Syscalls and P/Invokes, including `Environment.*` property reads.
1. Virtual or interface dispatch through unsealed types. Unity Mono has no
   guarded devirtualization, so every class on the dispatch chain must be
   `sealed` or the method non-virtual. Audit `MessageBus`,
   `MessageHandler.TypedHandler<T>`, `FlatDispatch<TMessage>`,
   `ContextFlatDispatch<TMessage>`, and `HandlerActionCache<T>`.
1. Boxing. Keep emission state as `ref TMessage` and observer calls as `in TMessage`;
   a struct message must never touch an `object` field.
1. `ArrayPool<T>.Shared.Rent`/`Return`. Its `Interlocked` operations are
   expensive on IL2CPP. Use private bus-owned pools or `DxPools`.

### Required patterns

- Steady-state dispatch walks a frozen `FlatDispatchEntry<TMessage>[]` resolved
  at snapshot-build time with plain `entries[i]` indexing over `[0, count)`. Two
  per-entry reads are load-bearing and must NOT be hoisted out of the loop:
  `entry.handler.active` (handlers toggle live) and the per-iteration
  `_resetGeneration` re-read (a handler may reset the bus mid-dispatch, which is
  the documented reentrancy contract).
- The shipped loops carry BOTH `[Il2CppSetOption(Option.NullChecks, false)]` and
  `[Il2CppSetOption(Option.ArrayBoundsChecks, false)]`. This supersedes older
  "keep NullChecks on" guidance: `BuildFlatDispatch` fills `entries[0..count)`
  with non-null handler plus invoker pairs, never publishes a `count` larger
  than `entries.Length`, and the array is frozen for the emission. Rig builds
  keep a `DXMESSAGING_INTERNAL_CHECKS` shape assert.
- Do not port that elision to Mono with `Unsafe`/`MemoryMarshal`. The entry
  struct holds managed references (unsafe to pointer-walk across GC relocation)
  and `System.Runtime.CompilerServices.Unsafe` is absent from IL2CPP players -
  the reason `Runtime/Core/Internal/DxUnsafe.cs` wraps `UnsafeUtility`.
- `EnsureAot{Untargeted,Targeted,Sourced}Bridge<T>()` is
  `[Conditional("ENABLE_IL2CPP")]` and lives in the dispatch-plan-creation block
  (first typed emit per bus), NOT in the per-emit path. Keep it there; the bridge
  is a process-global one-way latch. Guarded by
  `UntypedDispatchTests.TypedDispatchSeedsBridgeForPrivateManualMessageBeforeUntypedDispatch`
  on the standalone IL2CPP leg.

### Sweep gate

- The gate is consulted on EVERY emission, so it must sample-not-call: read the
  clock at most once per `SweepGateMask + 1` emissions via
  `(unchecked(_emissionId + 1) & SweepGateMask) == 0`. `SweepGateMask` is `0x0F`
  (once per 16 emits), internal and not public API.
- Keep the comparison against `_evictionTickIntervalSeconds` so the configured
  idle-then-evict semantics still mean what the public setting says.
- The `_clock.NowSeconds` read is an interface call Mono does not reliably
  devirtualize. The gate's value is the 1-in-16 rate, not a free getter. Do not
  raise the mask past `0x0F` without measurement: at `0x3F` a 1 emit/sec workload
  skews ~64 s against the 5 s default cadence.
- Reuse the existing per-emit `_emissionId`; do not add a second counter (that
  broke `CounterBasedTouchTests`).
- Headless hosts, dedicated servers, and non-Unity consumers have no PlayerLoop
  hook (`#if UNITY_2021_3_OR_NEWER`) and must call `bus.Trim()` themselves.
- Tests inject a probe clock through `MessageBus.CreateForInternalUse(probeClock, ...)`.
  `DxMessagingRuntimeSettingsProvider.Override` cannot inject it; the clock is
  constructor-injected.

### AggressiveInlining

- Apply `[MethodImpl(MethodImplOptions.AggressiveInlining)]` to trivial property
  getters, small arithmetic helpers, hash and equality members on value types,
  type checks, and thin forwarding methods. Measured effect: 10M dictionary
  lookups went from 142 ms to 98 ms (31%).
- Do not apply it to virtual methods, methods with `try`/`catch`, recursive
  methods, large bodies (code bloat), or cold paths. The JIT ignores or refuses
  it in those cases. Default JIT inlining already covers methods under 32 IL
  bytes; the attribute is a stronger hint, never a guarantee.

## Enforcement

`.github/workflows/perf-numbers.yml` re-runs the published Standalone IL2CPP
Release leg on every PR change, posts a sticky comment, and runs
`scripts/unity/render-perf-deltas.js --scope Standalone` against
`docs/architecture/perf-baseline.csv`; it fails when a gated scenario drops
throughput past the threshold (default 0.33) or increases allocation.
`Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs` is the harness;
`Tests/Editor/Allocations/EmitGateClockReadIsRare.cs` pins the gate cadence at or
below `ceil(emitCount / 16) + 1`; `Tests/Editor/Contract/EvictionSweepContractTests.cs`
pins wall-clock eviction semantics. `Tests/Editor/Benchmarks/PerfRegressionSmokeTests.cs`
is a local-only `[Explicit, Category("PerfGate")]` tool behind `DX_PERF_GATE=1`.

When a number moves, confirm scope, backend, build configuration, and machine
before calling it a regression: the 15-19M to 11M emits/sec drop was a scope
change from IL2CPP AOT to in-editor Mono, not a code regression.

## References

| Document                                                                                          | Purpose                                                                                          |
| ------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| [dispatch-hot-path.md](./references/dispatch-hot-path.md)                                         | Hot-path file map, prohibited operations, required patterns, per-emit budget, and CI enforcement |
| [sweep-gate-must-be-cheap.md](./references/sweep-gate-must-be-cheap.md)                           | Mask-gate pattern, clock-read budget, headless-host guidance, and test injection                 |
| [aggressive-inlining.md](./references/aggressive-inlining.md)                                     | What the attribute does and the call-overhead problem it addresses                               |
| [aggressive-inlining-part-1.md](./references/aggressive-inlining-part-1.md)                       | Applied examples on bit sets, discriminated unions, and value-type equality                      |
| [aggressive-inlining-part-2.md](./references/aggressive-inlining-part-2.md)                       | Good and poor inlining candidates plus the readonly-struct pairing                               |
| [aggressive-inlining-performance-notes.md](./references/aggressive-inlining-performance-notes.md) | Dictionary-lookup benchmark and JIT inlining thresholds                                          |
