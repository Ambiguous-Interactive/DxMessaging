---
title: "Benchmark Methodology: Total Over One Window"
id: "benchmark-methodology-total-over-window"
category: "performance"
version: "1.4.0"
created: "2026-06-07"
updated: "2026-06-23"

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
> `GC.Alloc` recorder over a SEPARATE batch. Never median-of-runs, never a single
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
```

The returned `BenchmarkMeasurement` carries `TotalOperations`,
`ElapsedSeconds`, `OperationsPerSecond`, and `GcAllocations`. Throughput
is `TotalOperations / ElapsedSeconds`; the renderer and the regression gate
read these fields directly. `GcAllocations` is the count of managed allocations
over one batch, or `AllocationProbe.Unmeasured` (`-1`, rendered `n/a`) when no
reliable probe is available on the backend -- never a fabricated `0`.

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
   window on purpose: the probe enables a profiler recorder whose overhead must
   not distort the throughput clock.

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
  the `GC.Alloc` recorder (`AllocationProbe`) instead.
- "A `0` and an `n/a` are the same." No -- `0` is a measured zero-allocation
  result; `n/a` (`AllocationProbe.Unmeasured`) means no probe was available.

## See Also

- [DxMessaging Dispatch Hot Path](./dispatch-hot-path.md)
- [Perf Config: IL2CPP Release, .NET Standard 2.1](./perf-config-il2cpp-release-netstandard21.md)
- [Benchmarks Run in the Highest-Fidelity Scope](../testing/benchmarks-run-in-highest-fidelity-scope.md)

## References

- Shared protocol: `Tests/Runtime/Benchmarks/BenchmarkProtocol.cs`
- Allocation probe: `Tests/Runtime/Benchmarks/AllocationProbe.cs`
- Methodology runbook: `docs/runbooks/perf-benchmark-methodology.md`
- Consumers: `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs`, `Tests/Runtime/Comparisons/ComparisonHarness.cs`
