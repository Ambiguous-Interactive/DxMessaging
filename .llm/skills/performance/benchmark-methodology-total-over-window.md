---
title: "Benchmark Methodology: Total Over One Window"
id: "benchmark-methodology-total-over-window"
category: "performance"
version: "1.7.0"
created: "2026-06-07"
updated: "2026-06-27"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/Benchmarks/BenchmarkProtocol.cs"
    - path: "Tests/Runtime/Benchmarks/AllocationProbe.cs"
    - path: "Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs"
    - path: "Tests/Runtime/Comparisons/ComparisonHarness.cs"
    - path: "docs/runbooks/perf-benchmark-methodology.md"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "performance"
  - "benchmarks"
  - "methodology"
  - "throughput"
  - "measurement"
  - "gc"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding why a single long window beats median-of-runs and why allocation is counted via the GC.Alloc recorder, not GC.GetAllocatedBytesForCurrentThread."

impact:
  performance:
    rating: "high"
    details: "The measurement method decides whether reported throughput and allocation counts are trustworthy or noise."
  maintainability:
    rating: "high"
    details: "One shared protocol means every suite measures identically; changes land in one file."
  testability:
    rating: "high"
    details: "A single Measure entry point is exercised by the dispatch suite, the editor suite, and every comparison bridge."

prerequisites:
  - "Familiarity with Stopwatch and the Unity GC.Alloc profiler recorder"
  - "Awareness of the dispatch hot-path budget"

dependencies:
  packages: []
  skills:
    - "dispatch-hot-path"

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
  - "Single-window benchmarking"
  - "Total over elapsed"
  - "BenchmarkProtocol"

related:
  - "dispatch-hot-path"
  - "perf-config-il2cpp-release-netstandard21"
  - "benchmarks-run-in-highest-fidelity-scope"

status: "stable"
---

# Benchmark Methodology: Total Over One Window

> **One-line summary**: Warm up, then measure ONE continuous N-second window
> (N = `BenchmarkProtocol.MeasurementSeconds` = 5) and report total operations
> divided by measured elapsed seconds; managed allocations are COUNTED via the
> `GC.Alloc` recorder over a SEPARATE batch, and the total allocated BYTES are
> measured alongside that count from the live `"GC Allocated In Frame"` counter
> (informational; the count is the gate). Never median-of-runs, never a single
> untimed pass.

## Overview

Throughput numbers are only meaningful if every benchmark measures the same
way. DxMessaging fixes the method in one shared type,
`Tests/Runtime/Benchmarks/BenchmarkProtocol.cs`, so the dispatch suite, the
editor suite, and every cross-library comparison bridge produce numbers that
can sit in the same table.

The method is deliberately simple: run a warmup, sample the clock, emit in
batches until one continuous window of N seconds elapses, then sample the clock
again. Throughput is the total operation count divided by the actual elapsed
seconds. Allocation is measured AFTER the timed window, over one additional
untimed batch, by `AllocationProbe`, and is reported as a COUNT of managed
allocations.

## Problem Statement

Three tempting shortcuts all produce misleading numbers:

- **Median-of-runs.** Measuring several short sub-windows and reporting the
  median hides warmup spillover and rewards lucky short samples. It also makes
  allocation meaningless because no single sub-window owns the work.
- **A single untimed pass.** Emitting a fixed count once and dividing by a
  rough timer conflates JIT/pool warmup with steady state.
- **`GC.GetAllocatedBytesForCurrentThread()` for allocation.** This returns `0`
  for EVERY allocation under Unity's Boehm GC (proven on the host editor: a
  forced 1 MB array allocation read back as a `0`-byte delta). It made the old
  "allocated bytes" column vacuously `0` for every technology -- hiding real
  per-operation allocations such as `SendMessage`'s ~11-per-call reflection cost.

The fix is one warmed, continuous, timed window for throughput plus a reliable
`GC.Alloc`-recorder count for allocation.

## Solution

`BenchmarkProtocol.Measure` is the single entry point. Callers supply a warmup
action and a batch function; the batch returns the number of operations it
performed, and the protocol sums batches until the window closes.

```csharp
using DxMessaging.Tests.Runtime.Benchmarks;

BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
    warmup: () =>
    {
        for (int i = 0; i < BenchmarkProtocol.WarmupEmits; i++)
        {
            bus.UntargetedBroadcast(ref message);
        }
    },
    emitBatch: () =>
    {
        for (int i = 0; i < BenchmarkProtocol.BatchSize; i++)
        {
            bus.UntargetedBroadcast(ref message);
        }
        return BenchmarkProtocol.BatchSize;
    });

double emitsPerSecond = measurement.OperationsPerSecond;
long gcAllocations = measurement.GcAllocations; // -1 == AllocationProbe.Unmeasured
long gcAllocatedBytes = measurement.GcAllocatedBytes; // -1 == AllocationProbe.Unmeasured
```

The returned `BenchmarkMeasurement` carries `TotalOperations`,
`ElapsedSeconds`, `OperationsPerSecond`, `GcAllocations`, `GcAllocatedBytes`,
`AllocationProbeOperations`, and the derived
`TotalEmittedOperations`. Throughput is `TotalOperations / ElapsedSeconds`; the
renderer and the regression gate read these fields directly. `GcAllocations` is
the count of managed allocations over one batch, or `AllocationProbe.Unmeasured`
(`-1`, rendered `n/a`) when no reliable probe is available on the backend --
never a fabricated `0`. `GcAllocatedBytes` is the total allocated BYTES over the
SAME batch (see the dedicated section below), likewise `AllocationProbe.Unmeasured`
(`-1`, rendered `n/a`) when the byte counter is unavailable.
`AllocationProbeOperations` is the operation count of the
untimed allocation-probe batch (see the invariant below).

## The Window Contract

The protocol pins the shape with three constants and one loop:

```csharp
public const int MeasurementSeconds = 5;
public const int WarmupEmits = 10_000;
public const int BatchSize = 10_000;
```

1. `warmup` runs once so JIT and pools reach steady state.
1. The stopwatch is sampled immediately before the first measured batch.
1. Batches run until `endTimestamp - startTimestamp` reaches the window in
   stopwatch ticks; the batch granularity keeps the clock read off the
   per-emit path.
1. AFTER the timed window, one more (untimed) batch runs under
   `AllocationProbe` to count managed allocations. It is kept OUT of the timed
   window on purpose: the probe enables a `GC.Alloc` profiler recorder whose
   overhead must not distort the throughput clock. That recorder is owned by a
   `using`-scoped `AllocationProbe.Window` (`BeginWindow` / `Sample`), so it is
   ALWAYS disabled on scope exit -- even when the measured body throws. There is
   no raw enable/disable pair to leak a permanently-enabled recorder (whose
   profiler overhead would distort every later measurement in the domain). The
   recorder needs the profiler, so it is functional in the editor / development
   builds but NOT in a Release IL2CPP player; the published allocation numbers
   therefore come from the in-editor Mono leg (see the methodology runbook).

Allocation windows that need a settled heap call
`AllocationProbe.SettleHeapForMeasurement()`. Do not inline
`GC.Collect()` / `GC.WaitForPendingFinalizers()` in tests: the helper performs
the complete collect, wait-for-finalizers, collect sequence so objects made
unreachable by finalizers are reclaimed before the measured window or before
the next test starts.

When a repeated minimum allocation measurement needs side-effect diagnostics,
use `AllocationProbe.MeasureMinWithDiagnostics<TDiagnostics>` and return a
small allocation-free diagnostic value from each attempt. Do not accumulate
diagnostic state in outer variables across attempts and then report it next to
the minimum count; that can pair the winning count with another attempt's state
or let aggregate side effects hide that the winning attempt did not perform the
required work.

Warm-up is per scenario. `DispatchBenchmarkScenarios.WarmupEmits(scenario)`
returns `WarmupEmits` (10,000) for every dispatch scenario except the
registration and deregistration floods and the cold first-dispatch scenarios,
which return 0 so they measure one-time or first-touch cost rather than steady
state. `ComparisonScenarios.WarmupEmits(scenario)`
applies the same policy to the comparison bridges. The
`BenchmarkProtocol.WarmupEmits = 10_000` constant stays the default; the
per-scenario function is the only place that count diverges.

Registration scenarios are the one documented exception to the throughput
report shape: they report wall-clock milliseconds for one-time setup cost
instead of steady-state emits per second.

## The Byte Companion: gcAllocatedBytes

Alongside the allocation CALL count, the same probe window measures the total
allocated BYTES per operation as `gcAllocatedBytes`. The count is and remains the
canonical, gated signal; bytes are INFORMATIONAL and answer the follow-up "how
big was each allocation" once the count says one happened.

**Mechanism.** Bytes come from a before/after delta of the live Unity
`ProfilerRecorder(ProfilerCategory.Memory, "GC Allocated In Frame").CurrentValue`
-- a within-frame `GC.Alloc` hook byte accumulator. The probe reads the counter
when the window opens and again when it closes; the delta is the bytes the body
allocated. Proven on the host editor (Unity 6000.4): exact and run-to-run
identical (100 × `byte[10000]` measured 1,003,200 bytes every run), ~0 for a
genuine zero-allocation region, and -- crucially -- **collection-immune**. A
heavy-churn region that made a `GC.GetTotalMemory` delta swing to −133 MB read a
rock-stable 8,000,000 bytes here, because the counter SUMS allocation-hook bytes
rather than measuring a heap-size difference, so a mid-window collection cannot
corrupt it.

**Why not the obvious alternatives** (dated rationale -- 2026-06, so nobody
re-tries them):

- `GC.GetAllocatedBytesForCurrentThread()` returns `0` for every allocation under
  Unity's Boehm GC.
- A `GC.GetTotalMemory` delta is dominated by warm-editor heap noise for
  sub-megabyte regions (a zero-alloc loop read back 24 KB; the same op swung
  41 KB–938 KB across repeats) and is corrupted by any mid-window collection.
- The per-sample `.Value` of the `GC.Alloc` ProfilerRecorder is garbage (it read
  2400 for a 1.2 MB region); only the `"GC Allocated In Frame"`
  `.CurrentValue` delta is trustworthy.
- `GC.TryStartNoGCRegion` throws `NotImplementedException` on Unity Mono.
- Unity's own Performance Testing package reads the alloc-CALL `.Count`, not
  bytes -- which is why the count stays the canonical signal and the gate metric.

**Honesty / availability.** The byte counter is profiler-dependent, exactly like
the count probe. It is functional in the editor and in development players; on the
published NON-development Standalone IL2CPP Release leg the profiler is stripped,
so BOTH metrics read `AllocationProbe.Unmeasured` (`-1`, rendered `n/a`) -- never
a fabricated `0`. The real allocation net is the EDITOR allocation suite plus the
weekly editor benchmarks, not the per-PR Standalone delta gate. A `gcAllocatedBytes`
of `-1` means `AllocationProbe.Unmeasured`, identical in meaning to the count's
sentinel; a `0` is a measured zero-byte result and must never be conflated with it.

**Goodness.** Fewer bytes is better. The perf-delta PR comment renders byte deltas
goodness-signed (`N fewer bytes` / `N more bytes`). Bytes are informational only --
the regression gate stays on the allocation COUNT.

### Measuring both at once: MeasureWithBytes / AllocationSample

When you want both numbers from one body, use `AllocationProbe.MeasureWithBytes`,
which returns an `AllocationProbe.AllocationSample { long Allocations; long Bytes; }`
(each field independently `Unmeasured` when its probe is non-functional):

```csharp
AllocationProbe.AllocationSample sample = AllocationProbe.MeasureWithBytes(() =>
{
    for (int i = 0; i < BenchmarkProtocol.BatchSize; i++)
    {
        bus.UntargetedBroadcast(ref message);
    }
});

long allocations = sample.Allocations; // -1 == AllocationProbe.Unmeasured
long bytes = sample.Bytes;             // -1 == AllocationProbe.Unmeasured
```

Inside a `using`-scoped window, `AllocationProbe.Window.SampleBytes()` returns the
byte delta alone and `AllocationProbe.Window.SampleBoth()` returns the
`AllocationSample`; `AllocationProbe.BytesFunctional` reports whether the
`"GC Allocated In Frame"` counter is confirmed usable on this backend (cached, the
byte analogue of `IsFunctional`). The cold counterpart carries the byte median as
`ColdLatencyMeasurement.MedianGcAllocatedBytes`, and the repeated-minimum path
exposes `MinimumMeasurement<T>.GcAllocatedBytes` next to its `GcAllocations`.

## Invariant: Reconcile Side-Effect Counters Against TotalEmittedOperations

`Measure` drives `emitBatch` MORE times than the timed window: once per timed
iteration AND one extra UNTIMED batch under `AllocationProbe` (step 4 above).
Both kinds of batch produce real side effects -- handler invocations, churn
cycles, `ProgressMarker` increments. So the measurement reports two distinct
totals:

- `TotalOperations` -- the TIMED window only. It is the numerator of
  `OperationsPerSecond`; throughput must never include the untimed probe batch.
- `TotalEmittedOperations` (= `TotalOperations + AllocationProbeOperations`) --
  every operation the protocol actually drove this run, timed window plus the
  post-window probe batch.

Any assertion that reconciles an OBSERVED side-effect counter against an EXPECTED
count MUST use `TotalEmittedOperations`, never `TotalOperations`. The comparison
harness's exact fan-out check is the canonical example:

```csharp
long expected =
    bridge.InvocationsPerOperation(scenario)
    * (warmupEmits + measurement.TotalEmittedOperations);
Assert.AreEqual(expected, bridge.ProgressMarker, /* enriched diagnostic */);
```

Counting only the timed window under-counts by exactly one `BatchSize` per
`InvocationsPerOperation`, so the check fails for every case -- this was a real
regression (44 comparison cases failed with
`observed - expected == InvocationsPerOperation * BatchSize`) introduced when the
allocation probe began running its own untimed `emitBatch`. The fix is to count
the probe batch, NOT to relax the assertion: it is an EXACT correctness check
that catches a library dropping, duplicating, or deduping a message, and a
one-`BatchSize` tolerance would hide up to 10,000 lost or doubled deliveries. Fix
the accounting; keep the equality exact. `DispatchThroughputBenchmarks` applies
the same reconciliation to its handler-invocation count.

## Cold Counterpart: MeasureColdLatency

The window protocol above applies only to warm/hot throughput. Cold (JIT-inclusive
first-touch) scenarios use `BenchmarkProtocol.MeasureColdLatency`, the cold
counterpart to `Measure`. Where `Measure` warms up and then sums batches over one
continuous window, `MeasureColdLatency` runs K trials with NO warm-up and NO
window. Each trial i builds FRESH state via `setUpTrial(i)` (UNTIMED; the index
lets the caller pick a DISTINCT closed generic type per trial), times EXACTLY ONE
`timedOperation` on that state (counting its allocations over the SAME region, since
a cold op cannot be re-run cold), then disposes it via `tearDownTrial` (UNTIMED). It
reports the MEDIAN wall clock and median allocation COUNT across the K trials, not
the mean -- cold latency is right-skewed, so one GC or scheduler blip must not move
the headline.

```csharp
ColdLatencyMeasurement cold = BenchmarkProtocol.MeasureColdLatency(
    trials: 32,
    setUpTrial: index => /* fresh state for trial index (UNTIMED) */ CreateState(index),
    timedOperation: state => state.EmitOnce(),
    tearDownTrial: state => state.Dispose());
double medianMs = cold.MedianWallClockMs;
long medianAllocations = cold.MedianGcAllocations;
long medianBytes = cold.MedianGcAllocatedBytes; // -1 == AllocationProbe.Unmeasured
```

Cold/latency results carry `emitsPerSecond=0` (the time lives in `wallClockMs`),
which is what auto-excludes them from the regression gate. The three cold dispatch
scenarios are the callers: each trial registers a BY-REF (`FastHandler<T>`) no-op
handler on a fresh bus, then times one emit of a distinct closed generic type, so
it JIT-compiles and measures the SAME fast dispatch path (`RunFastHandlers`) the
warm/hot scenarios use; the median over the distinct types stabilizes the JIT
noise. See the methodology runbook.

## Why It Holds

Because `Measure` is the only place the window logic lives, a suite cannot
silently drift to a different method. The dispatch benchmarks, the editor
benchmarks, and each comparison bridge call the same function, so a cell in
the throughput table and a cell in the comparison matrix are directly
comparable. The allocation count stays honest because `AllocationProbe`
self-validates the recorder and reports `Unmeasured` rather than a fabricated
`0` when the backend cannot measure -- the report can never again claim a
zero it did not observe.

## Common Pitfalls

- "I will average five one-second runs." That reintroduces median-of-runs.
  Use one five-second window.
- "I will time the warmup too." Warmup must run before the first clock sample;
  including it depresses throughput.
- "I will read the clock per emit for finer granularity." The per-emit clock
  read is itself a cost on the hot path. Batch and sample at batch boundaries.
- "I will measure allocated BYTES with `GC.GetAllocatedBytesForCurrentThread()`."
  That returns `0` for everything under Unity's Boehm GC. Count allocations with
  the `GC.Alloc` recorder (`AllocationProbe`) and, for the informational byte
  figure, the `"GC Allocated In Frame"` `.CurrentValue` delta -- never a
  `GC.GetTotalMemory` or per-thread byte delta (see the byte-companion section).
- "A `0` and an `n/a` are the same." No -- `0` is a measured zero-allocation
  result; `n/a` (`AllocationProbe.Unmeasured`) means no probe was available.
- "`TotalOperations` is the number of emits that happened." No -- the untimed
  allocation-probe batch also emits. Reconcile fan-out / side-effect counters
  against `TotalEmittedOperations`; reserve `TotalOperations` for throughput.

## See Also

- [DxMessaging Dispatch Hot Path](./dispatch-hot-path.md)
- [Perf Config: IL2CPP Release, .NET Standard 2.1](./perf-config-il2cpp-release-netstandard21.md)
- [Benchmarks Run in the Highest-Fidelity Scope](../testing/benchmarks-run-in-highest-fidelity-scope.md)

## References

- Shared protocol: `Tests/Runtime/Benchmarks/BenchmarkProtocol.cs`
- Allocation probe: `Tests/Runtime/Benchmarks/AllocationProbe.cs`
- Methodology runbook: `docs/runbooks/perf-benchmark-methodology.md`
- Consumers: `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs`, `Tests/Runtime/Comparisons/ComparisonHarness.cs`
