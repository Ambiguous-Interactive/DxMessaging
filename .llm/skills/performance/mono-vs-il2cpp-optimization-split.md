---
title: "Mono vs IL2CPP Optimization Split"
id: "mono-vs-il2cpp-optimization-split"
category: "performance"
version: "1.0.0"
created: "2026-06-18"
updated: "2026-06-18"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/Core/MessageBus/MessageBus.cs"
    - path: "Runtime/Core/Internal/FlatDispatch.cs"
    - path: "Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "performance"
  - "mono"
  - "il2cpp"
  - "dispatch"
  - "benchmarks"
  - "codegen"

complexity:
  level: "advanced"
  reasoning: "Requires understanding how Unity's two scripting backends differ in JIT vs AOT codegen, which optimization levers each one honors, and how to measure a backend-specific change without conflating it with editor noise."

impact:
  performance:
    rating: "high"
    details: "Picking the wrong lever for a backend wastes effort or regresses; the published headline is IL2CPP, so IL2CPP-only levers move the headline while Mono-only levers do not."
  maintainability:
    rating: "high"
    details: "Records which levers were measured and rejected so future sessions do not re-litigate AggressiveOptimization or bounds-check elision on Mono."
  testability:
    rating: "medium"
    details: "The MCP loop measures Mono; the IL2CPP headline and IL2CPP-only correctness are CI-only (perf-numbers.yml + the standalone leg)."

prerequisites:
  - "dispatch-hot-path"
  - "perf-config-il2cpp-release-netstandard21"
  - "benchmark-methodology-total-over-window"

dependencies:
  packages: []
  skills:
    - "dispatch-hot-path"
    - "perf-config-il2cpp-release-netstandard21"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.1"

aliases:
  - "Mono perf levers"
  - "IL2CPP-only optimization"
  - "AggressiveOptimization Unity"
  - "bring IL2CPP wins to Mono"

related:
  - "dispatch-hot-path"
  - "perf-config-il2cpp-release-netstandard21"
  - "aggressive-inlining"
  - "sweep-gate-must-be-cheap"

status: "stable"
---

<!-- trigger: mono perf, il2cpp only, AggressiveOptimization, bounds check elision, backend-specific optimization | Which perf lever applies to which Unity scripting backend | Core -->

# Mono vs IL2CPP Optimization Split

> **One-line summary**: DxMessaging ships on two scripting backends with
> different optimizers. IL2CPP (AOT) elides null/bounds checks per-method via
> `[Il2CppSetOption]` and pays a per-emit cost for generic-static access; Mono
> (JIT) ignores those attributes and has no tiered compilation. A perf change
> helps one backend, both, or neither - know which before spending a CI run.

## When to Use

- Considering a dispatch-hot-path change and deciding whether it can be measured
  on the local Mono MCP loop or only on the IL2CPP CI leg.
- Tempted to add `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` or an
  `Unsafe`/`Span` bounds-check-elision rewrite "for speed". Read the evidence
  table first.
- Triaging the gap between the published IL2CPP headline and the local Mono
  number (they differ by backend, not by bug).

## The two backends

| Aspect                                | Mono (editor + Mono player)             | IL2CPP (published headline)              |
| ------------------------------------- | --------------------------------------- | ---------------------------------------- |
| Codegen                               | JIT at first call                       | AOT C++ transpile, native compile        |
| Null / array-bounds checks            | JIT-emitted, not attribute-controllable | elided per-method by `[Il2CppSetOption]` |
| `[Il2CppSetOption]`                   | INERT (no effect)                       | honored (the lever)                      |
| Tiered compilation                    | none observed (single fixed tier)       | n/a (AOT)                                |
| Generic static field access           | cheap after JIT                         | carries a per-access class-init check    |
| `[Conditional("ENABLE_IL2CPP")]` body | compiled OUT (zero cost)                | compiled IN                              |

The local MCP loop is Mono. The published headline
([perf-config-il2cpp-release-netstandard21](./perf-config-il2cpp-release-netstandard21.md))
is the Standalone IL2CPP Release player, measured only in CI by
`perf-numbers.yml`. A change that is invisible to the Mono loop can still move
the headline, and vice versa.

## Levers, measured (2026-06-18 spike, editor Mono 6000.4.6f1)

Baseline single-emit throughput on the MCP loop: `UntargetedFlood_OneHandler`
~24.5M emits/s, `TargetedFlood_OneListener` ~17.5M, `BroadcastFlood_OneHandler`
~18.1M, `TargetedFlood_SixteenListeners` ~8.2M, all zero-alloc. Run-to-run noise
is ~+/-1-3% (editor warmth, GC, domain-reload state). A lever must clear that
band, repeatably, to count.

- **`[MethodImpl(MethodImplOptions.AggressiveOptimization)]` -> REJECTED (no-op
  on Mono).** A tiering probe ran identical fixed work across 12 back-to-back
  batches; per-call time was flat from batch 0 (~5.9k ns) to batch 11 (~5.9k ns)
  with only the one-time JIT of the first batch visible. Unity's Mono JIT
  compiles each method once at a fixed optimization level - there is no
  quick->optimized tier promotion for the attribute to direct, so it changes
  nothing. It is also inert on IL2CPP (AOT). Do NOT add it to the hot path.
- **`Span` / `Unsafe.Add` bounds-check elision on Mono -> REJECTED (sub-noise +
  unsafe + unavailable).** Three reasons: (1) an isolated `array[i<count]` vs
  `ReadOnlySpan[i<span.Length]` microbench could not resolve a difference above
  timer + delegate-dispatch noise, and on the real loop the per-entry delegate
  invoke (~10ns) dwarfs a ~1ns bounds check; (2) the flat dispatch entry struct
  holds managed references, so a raw `byte*`/pointer walk across handler
  callbacks is GC-relocation-unsafe; (3) `System.Runtime.CompilerServices.Unsafe`
  is absent from IL2CPP player builds (this is why `Runtime/Core/Internal/
DxUnsafe.cs` wraps `UnsafeUtility`). The shipped loop uses
  `[Il2CppSetOption(ArrayBoundsChecks, false)]` + plain indexing instead - an
  IL2CPP-only elision. Mono keeps the bounds check; that is acceptable.
- **`InstanceId` hash / equality -> already optimal.** `GetHashCode()` returns
  the raw `int` id and `Equals` is an `int` compare, so the targeted/broadcast
  `Dictionary<InstanceId, ...>` routing lookup is already minimal. The
  ~28%-slower targeted path vs untargeted is the inherent hash-routing cost, not
  a fixable bottleneck.
- **Emit prologue (`TrySweepIdle`, plan lookup, `EnterDispatch`, `AdvanceTick`)
  -> already minimal.** The plan lookup is a generic-static-indexed list, not a
  dictionary; `TrySweepIdle` is a single field read + branch when idle eviction
  is disabled. No safe slack remains.

Conclusion of the spike: the steady-state dispatch path is at its **Mono
floor**. The remaining single-emit headroom is **IL2CPP-only**.

## The one IL2CPP-only lever that shipped: AOT-bridge first-touch rooting

The per-emit `EnsureAot{Untargeted,Targeted,Sourced}Bridge<T>()` calls at the
top of `UntargetedBroadcast`/`TargetedBroadcast`/`SourcedBroadcast` are
`[Conditional("ENABLE_IL2CPP")]`. On IL2CPP each call is a non-inlined static
call plus a generic-static-class-init check on `AotBridgeState<T>` - paid on
EVERY emit. They were hoisted into the dispatch-plan-creation block
(`if (!TryGetValue) { GetOrAdd; EnsureAot...; }`), so the bridge is rooted on the
FIRST typed emit per bus and skipped thereafter.

Why this is safe:

- The bridge state is a process-global one-way latch; it only must be rooted
  before the first UNTYPED dispatch of `T`. Every `Register*<T>` path roots it
  independently, and untyped dispatch is reachable only for a type already
  registered or typed-emitted first (otherwise it throws - unchanged contract).
- On Mono the calls are compiled out, so the hoist is byte-identical Mono IL -
  a provable no-op there (the benchmark delta after the change is pure editor
  noise, mixed in direction, zero-alloc preserved).
- The invariant is CI-guarded by
  `UntypedDispatchTests.TypedDispatchSeedsBridgeForPrivateManualMessageBeforeUntypedDispatch`,
  which does a typed emit of a never-registered type then asserts the following
  untyped dispatch resolves the bridge - exactly the rooting the relocated call
  performs. It runs on the standalone IL2CPP leg.

This is the template for "things only possible under IL2CPP": the win is
unmeasurable on the Mono loop, so it is validated by `perf-numbers.yml` on a
dedicated PR, and its correctness by the standalone leg.

## Measuring a backend-specific change honestly

- **For a `[Conditional("ENABLE_IL2CPP")]` change, run the Mono benchmarks as a
  NEGATIVE control.** The numbers must stay within noise and zero-alloc must
  hold; the change is compiled out, so a real Mono delta would mean a mistake.
  The actual win shows only on the IL2CPP CI leg.
- **For a non-conditional change, pre-filter on the Mono loop**, then confirm on
  IL2CPP. A change that regresses Mono is rejected even if IL2CPP is neutral.
- **Never publish a Mono editor number as the headline.** The headline scope is
  the Standalone IL2CPP Release player; the MCP loop is local signal only.

## Common Pitfalls

- "AggressiveOptimization is free speed." It is a no-op on Unity Mono (no
  tiering) and inert on IL2CPP. Measured, rejected.
- "I'll elide the Mono bounds check like IL2CPP does." Not safely, and it is
  sub-noise on the delegate-dominated loop. The two backends elide differently
  by design.
- "The Mono number dropped after my IL2CPP-only change, so I regressed." If the
  change is `[Conditional("ENABLE_IL2CPP")]`, the Mono IL is identical; you are
  reading editor noise. Re-run; check the direction is mixed and alloc is 0.
- "Mono and IL2CPP should report the same throughput." Different backends,
  different codegen. Read each scope against its own backend.

## See Also

- [DxMessaging Dispatch Hot Path](./dispatch-hot-path.md)
- [Perf Config: IL2CPP Release, .NET Standard 2.1](./perf-config-il2cpp-release-netstandard21.md)
- [Aggressive Inlining](./aggressive-inlining.md)
- [Sweep Gate Must Be Cheap](./sweep-gate-must-be-cheap.md)

## References

- Hot path: `Runtime/Core/MessageBus/MessageBus.cs`,
  `Runtime/Core/Internal/FlatDispatch.cs`
- Bridge guard: `Tests/Runtime/Core/UntypedDispatchTests.cs`
- Benchmark harness: `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs`
