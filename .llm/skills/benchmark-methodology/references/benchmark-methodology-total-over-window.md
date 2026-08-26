# Benchmark Methodology: Total Over One Window

> **One-line summary**: Canonical rows warm up, then measure ONE continuous
> N-second window (N = `BenchmarkProtocol.MeasurementSeconds` = 5). Diagnostic
> paired rows counterbalance two workloads inside one process and retain every
> raw cycle. Managed allocations are COUNTED via the `GC.Alloc` recorder over a
> SEPARATE batch, and allocated BYTES are measured alongside that count from the
> live `"GC Allocated In Frame"` counter. Never discard a sample, never use a
> median for warm throughput, and never use a single untimed pass.

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
  per-operation allocations such as the per-call box Unity `SendMessage` pays to pass
  a value-type payload through its `object` parameter (1 allocation / ~20 bytes per
  call, verified on the host editor in PlayMode; its reflection dispatch itself is
  allocation-free once warm).

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

### Paired in-process stability evidence

`BenchmarkProtocol.MeasurePaired` is the diagnostic exception to the continuous-window shape.
`PairedComparisonHarness` settles the heap after preparing both workloads, then the protocol warms
and measures them in one player process so an unchanged second workload can expose common host
movement. Four cycles repeat
10000-operation batches in an ABBA/BAAB
super-cycle until both workloads have at least 625 ms active time. The control stays milliseconds
away, both workloads occupy every position, and each receives at least 2.5 seconds total active
time without another player launch.

The exact complement-palindromic sequence is `ABBABAAB`: across the `ABBA` and `BAAB` halves,
each workload occupies ordinal positions 1 through 4 once. This controls only movement that is
common and approximately multiplicative for both workloads. Workload-specific GC/cache effects or
different frequency sensitivity remain visible in raw-cycle or outer-run spread.

`PairedBenchmarkMeasurement` reports each workload's total operations, total active seconds, and
aggregate rate. Each cycle divides its two aggregate workload rates. `FirstToSecondRatio` is the
geometric combination of all four retained cycle ratios, while
`AggregateRateRatio` retains the simpler ratio of total rates as a diagnostic. `CycleRatios` keeps
all four raw values, and `CycleRatioSpreadPercent` is `(max / min - 1) * 100`. No cycle is removed
and no median is taken. Paired rows use `PairedComparison_`, remain outside the published matrix,
and do not run the allocation recorder inside timed batches. Allocation-heavy scenarios whose GC
can spill into the other workload's batch stay on their canonical continuous window. The canonical
rows still own allocation evidence and the published absolute throughput table.

Before the first bracket run, commit `scripts/unity/paired-bracket-manifest.json`. Classify every
paired row as a primary `target`, reachable `affected` row, or causally unreachable `sentinel`.
Declare at least one target, two sentinels, and the exact `Runtime/` paths containing the candidate
mechanism. Keep the manifest byte-for-byte unchanged through all three runs; the workflow embeds its
SHA-256 digest and a Git-derived digest of those candidate paths in every summary.

Let `q1`, `q2`, and `q3` be the first, center, and last paired headlines. The candidate factor is
`sqrt(q1 * q3) / q2` for candidate/control/candidate and `q2 / sqrt(q1 * q3)` for
control/candidate/control. Run `scripts/unity/reduce-paired-bracket.js` with the manifest and all
three retained summaries. It verifies three distinct commits, equal outer and different center
whole trees and candidate-source digests, the exact manifest digest, profile, protocol, complete
ordered row set, retained cycle ratios, raw spreads, outer spreads, every sentinel, and each target
and affected-row effect
normalized by the geometric mean of all sentinel factors. The experiment is
uninterpretable when a raw or outer spread exceeds 3% or any sentinel effect leaves +/-3%. A stable
candidate is rejected when a normalized affected row regresses by more than 3% or a normalized
target effect does not strictly exceed 3%. Do not discard or replace a run. Allocation gates remain
separate.

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
   builds but NOT in a Release IL2CPP player. The published workflow therefore
   omits allocation metrics; editor scopes remain available locally and through
   the manually dispatched benchmark workflow (see the methodology runbook).

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
registration (flood and per-kind marginal) and deregistration floods and the cold
first-dispatch scenarios, which return 0 so they measure one-time, marginal, or
first-touch cost rather than steady state. `ComparisonScenarios.WarmupEmits(scenario)`
applies the same policy to the comparison bridges. The
`BenchmarkProtocol.WarmupEmits = 10_000` constant stays the default; the
per-scenario function is the only place that count diverges.

Registration scenarios are the one documented exception to the throughput
report shape: they report wall-clock milliseconds for one-time setup cost
instead of steady-state emits per second. The three per-kind MARGINAL
registration scenarios (`{Untargeted,Targeted,Broadcast}Registration_Marginal`)
register 1000 more handlers of one already-warmed message type, using distinct
pre-built handler delegates so the measured window captures only the registration
machinery (not the handler delegate, and not a same-handler refcount bump); their
allocation count/bytes populate on a profiler-bearing editor leg and read `n/a`
on the stripped published Standalone leg.

Marginal registration is fast enough that one 1000-registration pass completes in
less than a millisecond on IL2CPP. It is also allocation-heavy, so retaining enough
populations to form one long window forces collections into the clock. This latency
therefore follows the warm-flood exception: after one heap settle, seven fresh trials
report their minimum repeatable floor. Every trial is warmed to 16 simultaneous live
registrations and then returned to zero live registrations before the clock. This
crosses the inline handler spill boundary and grows the token arena without timing
setup. On a profiler-bearing Mono run, allocation measurement uses eight fresh, identically
warmed attempts after latency timing and keeps the minimum exact count plus bytes from
that same attempt. A stripped IL2CPP player skips these allocation-only attempts and
reports `Unmeasured`; its seven timing populations still execute and validate. This
keeps Mono's `GC.Alloc` profiler hook out of wall time and rejects additive warm-editor
noise. Do not reduce timing to one short pass, combine live populations into a
GC-heavy long window, or put the allocation recorder around the latency clock. Do not
force a full warm-editor collection before every timing trial; a collection that lands
in one timed trial is already rejected as a slow outlier by the floor estimator.

## The Byte Companion: gcAllocatedBytes

Alongside the allocation CALL count, the same probe window measures the total
allocated BYTES over the same measurement batch as `gcAllocatedBytes`. The count is and remains the
canonical, gated signal; bytes are INFORMATIONAL and answer the follow-up "how
big was each allocation" once the count says one happened.

**Mechanism.** Bytes come from a before/after delta of the live Unity
`ProfilerRecorder(ProfilerCategory.Memory, "GC Allocated In Frame").CurrentValue`
-- a within-frame `GC.Alloc` hook byte accumulator. The probe reads the counter
when the window opens and again when it closes; the delta is the bytes the body
allocated. Proven on the host editor (Unity 6000.4): exact and run-to-run
identical (100 x `byte[10000]` measured 1,003,200 bytes every run), ~0 for a
genuine zero-allocation region, and -- crucially -- **collection-immune**. A
heavy-churn region that made a `GC.GetTotalMemory` delta swing to -133 MB read a
stable 8,000,000 bytes here, because the counter SUMS allocation-hook bytes
rather than measuring a heap-size difference, so a mid-window collection cannot
corrupt it.

**Why not the obvious alternatives** (dated rationale -- 2026-06, so nobody
re-tries them):

- `GC.GetAllocatedBytesForCurrentThread()` returns `0` for every allocation under
  Unity's Boehm GC.
- A `GC.GetTotalMemory` delta is dominated by warm-editor heap noise for
  sub-megabyte regions (a zero-alloc loop read back 24 KB; the same op swung
  41 KB-938 KB across repeats) and is corrupted by any mid-window collection.
- The per-sample `.Value` of the `GC.Alloc` ProfilerRecorder is garbage (it read
  2400 for a 1.2 MB region); only the `"GC Allocated In Frame"`
  `.CurrentValue` delta is trustworthy.
- `GC.TryStartNoGCRegion` throws `NotImplementedException` on Unity Mono.
- Unity's own Performance Testing package reads the alloc-CALL `.Count`, not
  bytes -- which is why the count stays the canonical signal and the gate metric.

**Honesty / availability.** The byte counter and count recorder are both
profiler-dependent but self-validate independently. They are functional in the
editor and in development players; on the published NON-development Standalone
IL2CPP Release leg the profiler is stripped, so both metrics read
`AllocationProbe.Unmeasured` (`-1`) for every row -- never a fabricated `0`.
Renderers must choose the first scope that measured each metric instead of
reusing the count scope for bytes, AND must drop a structurally-unmeasured surface
rather than fill it with `n/a`: the per-scope dispatch table omits a metric column
when every row is `Unmeasured` (so the Standalone table is throughput-only), the
comparison matrix for a metric no present scope measured is omitted entirely, and
the per-PR delta cell drops the metric segment when both sides are `Unmeasured`.
`n/a` then survives only as a genuine per-row/per-library cell (measured for the
scope in general, missing for that one entry). The real allocation net is the EDITOR
allocation suite plus the weekly editor benchmarks, not the per-PR Standalone
delta gate. A `gcAllocatedBytes` of `-1` means `AllocationProbe.Unmeasured`,
identical in meaning to the count's sentinel; a `0` is a measured zero-byte
result and must never be conflated with it.

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
`ColdLatencyMeasurement.MedianGcAllocatedBytes`; cold count and byte medians are
reduced independently because a byte sample can be `Unmeasured` for one
frame-boundary trial while count samples remain valid. The repeated-minimum path
exposes `MinimumMeasurement<T>.GcAllocatedBytes` next to its `GcAllocations`.
`MeasureMin` is count-keyed: if the count probe is unavailable it returns
`Unmeasured` for both fields rather than selecting a byte-only attempt, and when
multiple attempts tie the minimum count it prefers a tied attempt with measured
bytes over one whose bytes are `Unmeasured` without ever taking bytes from a
different-count attempt.

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

Because `Measure` owns canonical window logic and `MeasurePaired` owns paired
control logic, a suite cannot silently invent a third method. The dispatch
benchmarks, editor benchmarks, and comparison bridges call the appropriate
shared function. The allocation count stays honest because `AllocationProbe`
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

- [DxMessaging Dispatch Hot Path](../../dispatch-hot-path/references/dispatch-hot-path.md)
- [Perf Config: IL2CPP Release, .NET Standard 2.1](../../il2cpp-build-configuration/references/perf-config-il2cpp-release-netstandard21.md)
- [Benchmarks Run in the Highest-Fidelity Scope](./benchmarks-run-in-highest-fidelity-scope.md)

## References

- Shared protocol: `Tests/Runtime/Benchmarks/BenchmarkProtocol.cs`
- Allocation probe: `Tests/Runtime/Benchmarks/AllocationProbe.cs`
- Methodology runbook: `docs/runbooks/perf-benchmark-methodology.md`
- Consumers: `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs`, `Tests/Runtime/Comparisons/ComparisonHarness.cs`
