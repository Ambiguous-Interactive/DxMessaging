---
name: benchmark-methodology
description: "How DxMessaging measures throughput and allocation - one warmed 5-second window through BenchmarkProtocol.Measure, a separate untimed GC.Alloc probe batch, Standalone as the only published scope, and perf asmdef isolation. Use when writing or editing a benchmark, adding a comparison bridge or perf asmdef, interpreting emits/sec or n/a allocation cells, or proposing a performance change that needs an A/B verdict."
metadata:
  category: "performance"
  tags: "performance, benchmarks, methodology, throughput, measurement, gc"
---

# Benchmark Methodology: Total Over One Window

Warm up, then measure ONE continuous window and report total operations divided
by measured elapsed seconds. Allocations are COUNTED by a `GC.Alloc` recorder
over a separate untimed batch. Never median-of-runs, never a single untimed pass.

## When to use

- Writing or editing anything under `Tests/Runtime/Benchmarks`,
  `Tests/Editor/Benchmarks`, `Tests/Editor/Allocations`, or
  `Tests/Runtime/Comparisons`.
- Adding a comparison bridge against MessagePipe, UniRx, UniTask, Zenject, or
  Unity Atoms.
- Reading a throughput table, an `n/a` allocation cell, or a per-PR delta comment.
- Proposing a runtime performance change that needs an accept/reject verdict.
- Debugging a "0 tests ran" CI failure on a perf-looking asmdef.

## Rules

### The window contract

- `BenchmarkProtocol.Measure(warmup, emitBatch)` is the single entry point. All
  window logic lives there so no suite can drift to another method.
- Constants: `MeasurementSeconds = 5`, `WarmupEmits = 10_000`,
  `BatchSize = 10_000`. Warm-up runs BEFORE the first clock sample; the stopwatch
  is sampled immediately before the first measured batch; batches run until the
  window closes so the clock read stays off the per-emit path.
- Per-scenario warm-up overrides live only in
  `DispatchBenchmarkScenarios.WarmupEmits(scenario)` and
  `ComparisonScenarios.WarmupEmits(scenario)`. They return 0 for registration,
  marginal-registration, deregistration flood, and cold first-dispatch
  scenarios so those measure one-time, marginal, or first-touch cost.
- After the timed window, ONE additional untimed batch runs under
  `AllocationProbe`. It is excluded from timing on purpose: the `GC.Alloc`
  recorder's overhead would distort the throughput clock. The recorder is owned
  by a `using`-scoped `AllocationProbe.Window`, so it is always disabled on scope
  exit even when the body throws.
- Reconcile side-effect counters against `TotalEmittedOperations`
  (`= TotalOperations + AllocationProbeOperations`), never `TotalOperations`.
  Using the timed total under-counts by exactly one `BatchSize` per
  `InvocationsPerOperation` - a real regression that failed 44 comparison cases.
  Keep the fan-out assertion EXACT; fix the accounting instead of relaxing it.
  `TotalOperations` is reserved for the throughput numerator.
- Call `AllocationProbe.SettleHeapForMeasurement()` when a window needs a settled
  heap. Do not inline `GC.Collect()` / `GC.WaitForPendingFinalizers()` in tests.
- Cold, JIT-inclusive scenarios use `BenchmarkProtocol.MeasureColdLatency`: K
  trials, no warm-up and no window, each with untimed `setUpTrial(i)` and
  `tearDownTrial`, timing exactly one operation. It reports the MEDIAN wall clock
  and median allocation count because cold latency is right-skewed. Cold rows
  carry `emitsPerSecond = 0`, which auto-excludes them from the regression gate.

### Allocation honesty

- The allocation COUNT is the canonical, gated signal. `GcAllocatedBytes` -
  a before/after delta of the live
  `ProfilerRecorder(ProfilerCategory.Memory, "GC Allocated In Frame").CurrentValue` -
  is INFORMATIONAL and answers "how big", once the count says an allocation
  happened. It is collection-immune because it sums allocation-hook bytes rather
  than heap size.
- `AllocationProbe.Unmeasured` is `-1` and renders `n/a`. A measured `0` and an
  `n/a` are NOT the same thing, and a fabricated `0` is never acceptable. The
  profiler is stripped from the non-development Standalone IL2CPP Release leg, so
  both metrics read `Unmeasured` there. The published workflow omits allocation
  columns and does not run a second Mono leg solely to recover them.
- A surface where EVERY row is `Unmeasured` is dropped, not filled with `n/a`:
  the per-scope table omits the column, the comparison matrix for an unmeasured
  metric is omitted, and the delta cell drops the segment. `n/a` survives only as
  a genuine per-row gap.
- Rejected measurement methods, with dates, so nobody retries them:
  `GC.GetAllocatedBytesForCurrentThread()` returns `0` for every allocation under
  Unity's Boehm GC; a `GC.GetTotalMemory` delta is dominated by warm-editor noise
  and corrupted by mid-window collections; the per-sample `.Value` of the
  `GC.Alloc` recorder is garbage; `GC.TryStartNoGCRegion` throws
  `NotImplementedException` on Unity Mono.
- Use `AllocationProbe.MeasureWithBytes` when one body should yield both
  numbers; it returns an `AllocationSample` carrying `Allocations` and `Bytes`.
  `MeasureMin` is count-keyed:
  it never takes bytes from a different-count attempt. Use
  `MeasureMinWithDiagnostics<T>` and return an allocation-free diagnostic value
  per attempt rather than accumulating diagnostics in outer variables.

### Scope

- Benchmark bodies are SCOPE-AGNOSTIC. Resolve the execution target at runtime
  and encode it in the row; never fork an EditMode body from a PlayMode body, and
  never hardcode the scope label.
- Fidelity order is `["Standalone", "PlayMode", "EditMode"]`
  (`scripts/unity/render-perf-doc.js`). Standalone IL2CPP Release is the headline
  and the ONLY published scope; PlayMode is the local and CI iteration scope;
  EditMode is never representative. The renderer emits one labeled table per
  scope present and derives the backend label from the platform string.

### Perf isolation

- Asmdefs under `Tests/` whose `name` contains `Benchmarks` or `Allocations` are
  classified `perf`; `Comparisons` is a separate `comparison` class, because
  those suites need external packages. Both are excluded from the default run by
  `scripts/unity/lib/asmdef-discovery.js`.
- Opt in with `{ includePerf: true }` / `--include-perf` or
  `{ includeComparisons: true }` / `-IncludeComparisons`. Workflows resolve their
  assembly list through `.github/actions/compute-unity-assemblies`, which calls
  `defaultIncludeAssemblies` - never a hand-edited `customParameters` list.
- A perf asmdef in the `core` bucket almost always means its `name` field is
  missing the magic substring. Verify with
  `node scripts/unity/lib/asmdef-discovery.js`.
- `unity-tests.yml` excludes perf; `unity-benchmarks.yml` includes it;
  `perf-numbers.yml` passes `include-comparisons: "true"` on every PR and push.
  External comparison package versions come only from
  `.github/comparison-packages.json`.

### Verdict discipline

- Do not repeat a rejected candidate without new evidence or a materially
  different representation. Rejected so far: the 0-4 flat-dispatch `switch`
  (regressed dispatch 8-11%), `[ThreadStatic]` snapshot-holder stacks (failed the
  no-retained-memory-increase gate), the 256+ open-addressed `InstanceId` map
  (2.83% median gain, below the 3% claim threshold, plus correctness failures),
  inline bus context maps at physical capacity 2/4/8 (failed spill storage), and
  recombined typed plus interceptor teardown (+14% registration bytes).
- Accepted: the physical-two `HandlerActionCache` entry map (fresh Mono
  construction 2.129M to 3.541M caches/sec, +66.3%) and reading the non-global
  dispatch count from the already-cast flat holder.
- Claims need fresh A/B/A bracketed control and candidate runs and must clear the
  3% threshold; run-to-run editor noise is +/-1-3%.
- Do not read `MessageBusConstruction_1000` or cold teardown rows as dispatch
  regressions - they are wall-time first-touch rows and move independently.
- Do not attribute a result to branch, cache, or memory stalls: no CPU-sampling
  or Top-Down capture exists on the measured runners, and the published artifacts
  retain results and player logs, not generated IL2CPP C++.

## References

| Document                                                                                                | Purpose                                                                                                                                                 |
| ------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [benchmark-methodology-total-over-window.md](./references/benchmark-methodology-total-over-window.md)   | `BenchmarkProtocol.Measure` window contract, the allocation probe and byte companion, `TotalEmittedOperations` reconciliation, and `MeasureColdLatency` |
| [benchmarks-run-in-highest-fidelity-scope.md](./references/benchmarks-run-in-highest-fidelity-scope.md) | Scope-agnostic benchmark bodies, the scope fidelity ranking, and renderer headline ordering                                                             |
| [unity-perf-test-isolation.md](./references/unity-perf-test-isolation.md)                               | Asmdef classification regexes, default-run exclusion, opt-in flags, and where perf actually runs in CI                                                  |
| [runtime-performance-campaign-decisions.md](./references/runtime-performance-campaign-decisions.md)     | Accepted and rejected runtime candidates, rejected measurement methods, and backend/first-touch observations                                            |
