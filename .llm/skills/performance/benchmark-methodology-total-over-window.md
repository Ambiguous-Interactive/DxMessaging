---
title: "Benchmark Methodology: Total Over One Window"
id: "benchmark-methodology-total-over-window"
category: "performance"
version: "1.0.0"
created: "2026-06-07"
updated: "2026-06-07"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/Benchmarks/BenchmarkProtocol.cs"
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
  reasoning: "Requires understanding why a single long window beats median-of-runs and how GC bytes attribute to the same window."

impact:
  performance:
    rating: "high"
    details: "The measurement method decides whether reported throughput and bytes/op are trustworthy or noise."
  maintainability:
    rating: "high"
    details: "One shared protocol means every suite measures identically; changes land in one file."
  testability:
    rating: "high"
    details: "A single Measure entry point is exercised by the dispatch suite, the editor suite, and every comparison bridge."

prerequisites:
  - "Familiarity with Stopwatch and GC.GetAllocatedBytesForCurrentThread"
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
  - "perf-config-mono-netstandard21-release"
  - "benchmarks-run-in-highest-fidelity-scope"

status: "stable"
---

# Benchmark Methodology: Total Over One Window

> **One-line summary**: Warm up, then measure ONE continuous N-second window
> (N = `BenchmarkProtocol.MeasurementSeconds` = 5) and report total operations
> divided by measured elapsed seconds; GC bytes are captured over the same
> window. Never median-of-runs, never a single untimed pass.

## Overview

Throughput numbers are only meaningful if every benchmark measures the same
way. DxMessaging fixes the method in one shared type,
`Tests/Runtime/Benchmarks/BenchmarkProtocol.cs`, so the dispatch suite, the
editor suite, and every cross-library comparison bridge produce numbers that
can sit in the same table.

The method is deliberately simple: run a warmup, sample the clock and the GC
allocation counter, emit in batches until one continuous window of N seconds
elapses, then sample the clock and GC counter again. Throughput is the total
operation count divided by the actual elapsed seconds. The allocation delta is
the difference of the two GC samples, so bytes/op is attributed to exactly the
work that produced the throughput.

## Problem Statement

Two tempting shortcuts both produce misleading numbers:

- **Median-of-runs.** Measuring several short sub-windows and reporting the
  median hides warmup spillover and rewards lucky short samples. It also makes
  bytes/op meaningless because no single sub-window owns the allocation.
- **A single untimed pass.** Emitting a fixed count once and dividing by a
  rough timer conflates JIT/pool warmup with steady state.

The fix is one warmed, continuous, timed window. A long window (5 seconds)
amortizes scheduler jitter and gives a stable operations-per-second figure
without resampling games.

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
long bytesOverWindow = measurement.AllocatedBytesDelta;
```

The returned `BenchmarkMeasurement` carries `TotalOperations`,
`ElapsedSeconds`, `OperationsPerSecond`, and `AllocatedBytesDelta`. Throughput
is `TotalOperations / ElapsedSeconds`; the renderer and the regression gate
read these fields directly.

## The Window Contract

The protocol pins the shape with three constants and one loop:

```csharp
public const int MeasurementSeconds = 5;
public const int WarmupEmits = 10_000;
public const int BatchSize = 10_000;
```

1. `warmup` runs once so JIT and pools reach steady state.
1. The GC counter and the stopwatch are sampled immediately before the first
   measured batch.
1. Batches run until `endTimestamp - startTimestamp` reaches the window in
   stopwatch ticks; the batch granularity keeps the clock read off the
   per-emit path.
1. The GC counter is sampled immediately after the last batch, so the delta
   covers the same window as the elapsed time.

Registration scenarios are the one documented exception: they report
wall-clock milliseconds for one-time setup cost instead of steady-state
emits per second.

## Why It Holds

Because `Measure` is the only place the window logic lives, a suite cannot
silently drift to a different method. The dispatch benchmarks, the editor
benchmarks, and each comparison bridge call the same function, so a cell in
the throughput table and a cell in the comparison matrix are directly
comparable. Bytes/op stays honest because allocation is read across the exact
measured interval, not estimated.

## Common Pitfalls

- "I will average five one-second runs." That reintroduces median-of-runs.
  Use one five-second window.
- "I will time the warmup too." Warmup must run before the first clock sample;
  including it depresses throughput.
- "I will read the clock per emit for finer granularity." The per-emit clock
  read is itself a cost on the hot path. Batch and sample at batch boundaries.
- "I will measure GC bytes separately afterward." Sample inside the same
  window so the delta matches the work.

## See Also

- [DxMessaging Dispatch Hot Path](./dispatch-hot-path.md)
- [Perf Config: Mono, .NET Standard 2.1, Release](./perf-config-mono-netstandard21-release.md)
- [Benchmarks Run in the Highest-Fidelity Scope](../testing/benchmarks-run-in-highest-fidelity-scope.md)

## References

- Shared protocol: `Tests/Runtime/Benchmarks/BenchmarkProtocol.cs`
- Methodology runbook: `docs/runbooks/perf-benchmark-methodology.md`
- Consumers: `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs`, `Tests/Runtime/Comparisons/ComparisonHarness.cs`
