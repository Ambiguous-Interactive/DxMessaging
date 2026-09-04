---
name: benchmark-methodology
description: "How DxMessaging measures throughput and allocation - warmed 5-second canonical windows, counterbalanced in-process comparison controls, separate untimed GC.Alloc probes, Standalone publication, and perf asmdef isolation. Use when writing or editing a benchmark, adding a comparison bridge or perf asmdef, interpreting emits/sec or paired ratios, or proposing a performance change that needs an A/B verdict."
metadata:
  category: "performance"
  tags: "performance, benchmarks, methodology, throughput, measurement, gc"
---

# Benchmark Methodology: Total Over One Window

Canonical rows warm up, then measure ONE continuous window and report total
operations divided by measured elapsed seconds. Paired stability evidence uses
counterbalanced batches inside one process and retains every raw cycle ratio.
Allocations are COUNTED by a `GC.Alloc` recorder over a separate untimed batch.
Never median-of-runs, never discard an outlier, never use a single untimed pass.

## When to use

- Writing or editing benchmark, allocation, or comparison tests.
- Adding a comparison bridge against another messaging library.
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
- Reconcile side-effect counters against `TotalEmittedOperations` (timed plus probe
  operations), never `TotalOperations`. Keep fan-out assertions exact.
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
- A surface where EVERY row is `Unmeasured` is dropped. `n/a` survives only as a
  genuine per-row gap.
- Rejected measurement methods:
  `GC.GetAllocatedBytesForCurrentThread()` returns `0` for every allocation under
  Boehm; heap deltas are noisy and collection-sensitive; per-sample `GC.Alloc`
  `.Value` is invalid; and Unity Mono does not implement no-GC regions.
- Use `AllocationProbe.MeasureWithBytes` when one body should yield both
  numbers; it returns an `AllocationSample` carrying `Allocations` and `Bytes`.
  `MeasureMin` is count-keyed:
  it never takes bytes from a different-count attempt. Use
  `MeasureMinWithDiagnostics<T>` and return an allocation-free diagnostic value
  per attempt rather than accumulating diagnostics in outer variables.

### The paired-control contract

- Keep canonical `Comparison_` rows on `Measure`. Paired diagnostics emit only
  `DXM_PAIRED_COMPARISON` evidence markers, never structured or CSV performance rows,
  so the baseline extractor cannot duplicate or replace the published matrix.
- Run each published suite in one player process. Derive the pinned host's fastest partition from
  Windows CPU-set `EfficiencyClass`; keep Normal priority; verify and retain topology and process
  settings. Fail closed on topology or setting drift. Historical deltas require an exact committed
  sidecar match on profile ID, affinity mask, and priority; otherwise omit them.
- Settle once, warm both workloads, then run four 625-ms cycles of 10000-operation ABBA/BAAB batches.
- Treat the ratio as common-mode only when host movement affects both bridges approximately
  multiplicatively. Workload-specific GC/cache effects or frequency sensitivity stay in spread.
- Geometrically combine all four cycle rate ratios and report their max/min spread. Do not take a
  median, remove an outlier, or turn a spread warning into a performance regression.
- Pair DxMessaging with the same unchanged in-process MessagePipe scenario so shared host movement
  does not masquerade as route-specific movement.
- Predeclare each paired row and the exact candidate `Runtime/` paths in the committed manifest
  before run one. Require at least one target and two sentinels; keep the manifest unchanged.
- Use `scripts/unity/reduce-paired-bracket.js` only. It validates the manifest, distinct commits,
  whole-tree and candidate-source digests, full profile, protocol, cycles, spreads, sentinels, and
  normalized effects. Require stability within 3%, every target above 3%, and no affected regression
  beyond 3%.
- Keep exact fan-out assertions over warm-up plus every paired measured operation. Allocation
  evidence remains on the canonical rows; do not enable a profiler recorder inside paired batches.
  Do not pair an allocation-heavy workload whose collections can spill into the other side's batch;
  keep it on the canonical continuous window.

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

- Asmdefs named with `Benchmarks` or `Allocations` classify as `perf`; `Comparisons`
  classifies separately because it needs external packages. Default runs exclude both.
- Opt in with `includePerf` / `--include-perf` or `includeComparisons` /
  `-IncludeComparisons`; resolve assemblies through `compute-unity-assemblies`.
- Check misclassified perf asmdef names with `node scripts/unity/lib/asmdef-discovery.js`.
- `perf-numbers.yml` isolates internal and comparison entries. External package versions
  come only from `.github/comparison-packages.json`.

### Verdict discipline

- Do not repeat a rejected candidate without new evidence or a materially different
  representation. The campaign decision reference records accepted and rejected work.
- Legacy screening needs a fresh A/B/A bracket, an interpretable paired result, and an effect
  strictly greater than 3%, as accepted by `reduce-paired-bracket.js` from the immutable manifest
  and all three retained summaries.
- Confirmation requires #500's foundations and #510's independent-build replication,
  intervals, controls, and stopping rule; a legacy screening pass alone is insufficient.
- Do not read `MessageBusConstruction_1000` or cold teardown rows as dispatch
  regressions - they are wall-time first-touch rows and move independently.
- Do not attribute a result to branch, cache, or memory stalls without resolved
  profiles and matching controls. Generated C++ and PDB-backed native ranges
  establish code shape, not sampled cost; #511 owns the attribution gate.

## References

| Document                                                                                                | Purpose                                                                                                                                                 |
| ------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [benchmark-methodology-total-over-window.md](./references/benchmark-methodology-total-over-window.md)   | `BenchmarkProtocol.Measure` window contract, the allocation probe and byte companion, `TotalEmittedOperations` reconciliation, and `MeasureColdLatency` |
| [benchmarks-run-in-highest-fidelity-scope.md](./references/benchmarks-run-in-highest-fidelity-scope.md) | Scope-agnostic benchmark bodies, the scope fidelity ranking, and renderer headline ordering                                                             |
| [unity-perf-test-isolation.md](./references/unity-perf-test-isolation.md)                               | Asmdef classification regexes, default-run exclusion, opt-in flags, and where perf actually runs in CI                                                  |
| [runtime-performance-campaign-decisions.md](./references/runtime-performance-campaign-decisions.md)     | Accepted and rejected runtime candidates, rejected measurement methods, and backend/first-touch observations                                            |
