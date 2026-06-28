# Perf Benchmark Methodology Runbook

This runbook is the developer/ops reference for how DxMessaging's published
performance numbers are produced. The user-facing page that shows the rendered
tables is [Performance Benchmarks](../architecture/performance.md); this runbook
covers the methodology behind those tables, the CI configuration that runs them,
the scenario taxonomy, baseline capture, the permanent regression gate and the
local-only smoke gate, and how to add or bump a comparison library.

## Measurement methodology

The benchmark harness measures raw dispatch cost over a single continuous
window:

1. Warm up the scenario for `DispatchBenchmarkScenarios.WarmupEmits(scenario)`
   emits so JIT and pools reach steady state. That count is
   `BenchmarkProtocol.WarmupEmits` (currently 10,000, the default) for every
   scenario except the registration/deregistration floods and the cold
   first-dispatch scenarios, which warm up 0 emits so they measure one-time or
   first-touch cost. `ComparisonScenarios.WarmupEmits` applies the same policy to
   the comparison bridges.
1. Start a stopwatch.
1. Emit in batches until ONE continuous measurement window of `N` seconds has
   elapsed (`BenchmarkProtocol.MeasurementSeconds`, currently `N = 5`).
1. Count managed GC allocations over ONE additional, untimed batch via
   `AllocationProbe` (the `GC.Alloc` profiler recorder), reported as a COUNT, and
   measure the total allocated BYTES over that SAME batch as `gcAllocatedBytes`.

Throughput is `total operations / measured elapsed seconds`. Allocation is counted
in a SEPARATE batch (not the timed window) so the recorder's overhead cannot
distort the throughput clock, and is reported as a count of allocation calls -- a
sharper "0 vs N per op" signal than a byte delta. **Why a count, not bytes:**
`GC.GetAllocatedBytesForCurrentThread()` (the former source) returns `0` for every
allocation under Unity's Boehm GC -- proven on the host editor, where a forced 1 MB
array allocation read back as a `0`-byte delta -- so the old "allocated bytes"
column was vacuously `0` for every technology. The `GC.Alloc` recorder
(`AllocationProbe`) counts allocations reliably and is immune to GC timing; it
self-validates and reports the `Unmeasured` sentinel (`-1`) rather than a
fabricated `0` when no probe is available on a backend (for example a
non-development player). A surface that is unmeasured for EVERY row (the whole
Standalone leg) is omitted rather than rendered `n/a`; `n/a` survives only as an
individual cell within an otherwise-measured table or matrix.

**Allocated bytes (`gcAllocatedBytes`), informational.** Alongside the gated
allocation COUNT, the harness reports the total allocated BYTES over that SAME batch from a
before/after delta of the live Unity
`ProfilerRecorder(ProfilerCategory.Memory, "GC Allocated In Frame").CurrentValue` --
a within-frame `GC.Alloc`-hook byte accumulator. Because it SUMS allocation-hook
bytes rather than measuring a heap-size difference, it is exact and run-to-run
identical (100 x `byte[10000]` reads 1,003,200 bytes) and immune to mid-window
collections (a heavy-churn region that swung a `GC.GetTotalMemory` delta to -133 MB
read a stable 8,000,000 bytes here). The other byte sources were rejected:
`GC.GetAllocatedBytesForCurrentThread()` returns `0` under Boehm; a `GC.GetTotalMemory`
delta is dominated by warm-editor heap noise (a zero-alloc loop read 24 KB; the same
op swung 41 KB-938 KB across repeats) and is corrupted by collections; the `GC.Alloc`
recorder's per-sample `.Value` is garbage (2400 for a 1.2 MB region); and
`GC.TryStartNoGCRegion` throws `NotImplementedException` on Unity Mono. Bytes and
counts are self-validated independently; the Standalone Release leg cannot measure
either (its profiler is stripped), so the renderer omits the all-unmeasured memory
columns/matrices entirely and sources the count and byte metrics from the first scope
that measured each (the PlayMode Mono leg), rather than reusing the count scope for
bytes or publishing a vacuous wall of `n/a`. Bytes are
INFORMATIONAL: the perf-delta PR comment renders byte deltas goodness-signed (`N fewer
bytes` / `N more bytes`), but the regression gate stays on the allocation COUNT.
There is **no median-of-runs**: the older approach
of measuring several short sub-windows and comparing their median has been
replaced by this single long window. The shared protocol is the single source of
truth for every benchmark suite (dispatch throughput, comparisons) and lives in
[`Tests/Runtime/Benchmarks/BenchmarkProtocol.cs`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Benchmarks/BenchmarkProtocol.cs).

Registration scenarios report wall-clock milliseconds instead of emits per
second, because they measure one-time setup cost rather than steady-state
dispatch. The registration flood registers 1000 distinct closed generic
value-type messages from a cold bus with no warm-up by design. Under Mono each
distinct closed generic forces a one-time JIT compile, so the flood measures JIT
cost, not the registration algorithm; under IL2CPP/AOT those generics are
precompiled, so the same flood is on the order of 100x cheaper.

### Cold vs warm/hot modes

Both registration and dispatch are covered in two modes. "Cold" is the
JIT-inclusive first execution -- the genuine first-touch hitch under Mono. "Warm"
or "hot" is steady state. The mode is encoded as a suffix on the scenario key; the
7-column baseline CSV is unchanged. Every cold/warm-JIT scenario is a wall-clock
(latency) row: it sets `emitsPerSecond=0` and puts the time in `wallClockMs`. That
zero throughput is also what AUTO-EXCLUDES these rows from the CI regression gate
(`render-perf-deltas.js` treats a baseline `emitsPerSecond<=0` as non-gating), so
they are report-only -- rendered as wall clock, never gated.

- **Cold = JIT-inclusive first-touch, stabilized via distinct types, median.** A
  single first emit of one message type is pure JIT noise: it is dominated by the
  one-time compile of that type's dispatch path. The three
  `*FirstDispatch_Cold` scenarios instead route through
  `BenchmarkProtocol.MeasureColdLatency` over 32 trials, one per distinct closed
  generic message type. Each trial spins up a FRESH bus, registers a no-op handler
  via the BY-REF (`FastHandler<T>`) overload (untimed), then times EXACTLY ONE emit
  of that type. The by-ref handler is deliberate: it makes the timed emit
  JIT-compile and exercise `RunFastHandlers` -- the SAME fast dispatch path the
  warm/hot scenarios measure -- rather than the slower by-value default path. Each
  first emit JIT-compiles that closed type's fast dispatch path, and the reported
  number is the MEDIAN of the 32 per-emit samples. The median rejects the single
  outlier the very first trial carries (the one-time compile of the shared
  dispatch infrastructure), giving a stable JIT-inclusive cold first-dispatch
  number -- symmetric with the registration flood.
- **Warm-JIT registration flood.** `RegistrationFlood_1000Types_WarmJit` is the
  JIT-pre-warmed complement to the cold flood. It registers all 1000 cached flood
  builders once on a THROWAWAY bus (disposed -- only the JIT-compiled code
  survives), then times a fresh-bus registration of the same 1000 builders. The
  cold flood times both the Mono JIT compile and the registration data-structure
  work; the warm-JIT flood isolates the data-structure cost by paying the JIT bill
  first. Under IL2CPP/AOT the generics are precompiled, so cold and warm-JIT are
  approximately equal.
- **Deregistration floods.** `DeregistrationFlood_1000Types_Cold` and
  `DeregistrationFlood_1000Types_WarmJit` are the teardown mirror of the
  registration floods. Each stages the same 1000 cached flood builders on a live
  token UNTIMED, then times `MessageRegistrationToken.UnregisterAll()` -- the
  production deregistration path, which drains one bus deregistration per staged
  handler. The cold variant pays the Mono JIT compile of that path on its first
  call; the warm-JIT variant pre-pays it on a throwaway bus (register then
  `UnregisterAll`) and times the teardown of a fresh, fully populated token, so it
  isolates the data-structure dismantle cost. Both are wall-clock rows, symmetric
  with the registration floods.

The cold counterpart to `BenchmarkProtocol.Measure` is
`BenchmarkProtocol.MeasureColdLatency`. It runs K trials; each trial builds fresh
state (untimed, indexed so the caller picks a distinct closed type per trial),
times EXACTLY ONE operation on it, then tears the state down (untimed). It reports
the median wall clock and median GC-allocation count across the trials (cold
latency is right-skewed, so the median is the headline). The three cold dispatch
scenarios are
its callers; the continuous-window protocol applies only to the warm/hot throughput
scenarios.

### Budget interpretation

Dispatch budgets are interpreted in per-emit terms. Convert throughput to
nanoseconds per emit with:

```text
ns_per_emit = 1,000,000,000 / emits_per_second
```

Compare both throughput and per-emit nanoseconds. Throughput is easier to scan,
but per-emit nanoseconds make fixed overhead visible. A 10 ns increase is
material on handlers whose work is only 10-20 ns.

Allocation budgets are interpreted as the COUNT of managed GC allocations during
the measured batch. Dispatch scenarios should stay at zero measured allocations
after warmup. Any non-zero allocation count on a hot-path dispatch scenario
requires an explanation, a fix, or an explicit reviewer-approved exception. A
count of `Unmeasured` (rendered `n/a`) is neither a pass nor a fail -- it means no
reliable probe was available on that backend, so the budget cannot be evaluated.

## Build and runtime configuration

The published THROUGHPUT numbers are measured under **Standalone IL2CPP + .NET
Standard 2.1 + Release** in a true Release player; the published GC-ALLOCATION
counts come from an in-editor **PlayMode (Mono)** leg, because allocations are
only measurable where the profiler is available and a Release player strips it
(see [Editor-vs-player rationale](#editor-vs-player-rationale)). All legs are
driven by
[`scripts/unity/run-ci-tests.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/run-ci-tests.ps1):

- **Standalone perf leg (the throughput headline)** builds an **IL2CPP
  non-development (Release) player**. The generated build modifier actively
  clears `BuildOptions.Development` -- the Unity Test Framework's PlayerLauncher
  injects it by default, and a development player reports
  `Debug.isDebugBuild == true` -- and the project configurator pins the IL2CPP
  C++ configuration to Release:
  `PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone, Il2CppCompilerConfiguration.Release)`.
  The player uses `ApiCompatibilityLevel.NET_Standard` (the non-deprecated
  profile that targets .NET Standard 2.1) and **disabled managed code
  stripping**, so the test assemblies and the `[Preserve]` standalone test-run
  callback survive into the player. The runner's `-StandaloneScriptingBackend`
  parameter defaults to `IL2CPP` and also accepts `Mono2x`; the published leg
  pins IL2CPP. The configurator's
  `DXM perf config: backend=..., api=..., codeOpt=..., il2cppConfig=...` log
  line and each row's platform string
  (`Standalone IL2CPP x64 Release (WindowsPlayer; ...)`) prove the profile per
  run; a published `x64 Debug` row is a configuration bug. A Release player
  strips the `GC.Alloc` profiler recorder, so the Standalone leg cannot measure
  allocations or bytes at all; rather than fill those columns with `n/a`, the
  renderer omits the all-unmeasured memory columns from the Standalone table and
  the all-unmeasured memory matrices entirely (the real numbers come from the
  PlayMode Mono leg).
- **PlayMode allocation leg (published for allocations only)** runs in-editor
  under Mono and passes `-releaseCodeOptimization`, which sets
  `CompilationPipeline.codeOptimization = Release` so test assemblies compile
  without debug code paths. The differentiator from the Standalone leg is the
  **profiler**, not bridge coverage: a built Standalone player has
  `Application.isPlaying == true` just like PlayMode, so BOTH legs run every
  comparison bridge (including the ones that require `Application.isPlaying`) and
  produce a complete cross-library matrix. What the editor's Mono backend adds is
  a functional `GC.Alloc` recorder, which a Release player strips -- so only this
  leg can MEASURE the allocation COUNTS and bytes, and the Standalone table simply
  omits those memory columns. Allocation counts are build-config-independent -- the
  same code paths box and allocate under Mono and IL2CPP -- so these counts
  represent the shipped IL2CPP player; the renderer documents this provenance by
  labeling the allocation tables with the PlayMode (Mono) scope. Only ALLOCATIONS
  are sourced from this leg; the headline throughput stays on the Standalone leg.
- **EditMode leg (not published)** also runs in-editor under Mono with
  `-releaseCodeOptimization`. It remains a fast scope for local iteration, and
  the weekly `unity-benchmarks.yml` runs the editmode + playmode benchmark tests
  per Unity version as coverage; EditMode numbers are not published.

The harness can exercise every scope through the shared protocol;
`perf-numbers.yml` runs the **Standalone IL2CPP leg (throughput)** and the
**in-editor PlayMode Mono leg (allocations)**, and publishes both (see
[Editor-vs-player rationale](#editor-vs-player-rationale)). The two legs serialize
through the single organization build lock on the single `fast` runner, so adding
the PlayMode leg increases the workflow's wall clock by the PlayMode leg's
duration, which is shorter than the Standalone leg's because it builds no IL2CPP
player (well under 2x total). The PlayMode leg is also **non-blocking**: it is
marked `continue-on-error`, so a flaky PlayMode failure does NOT fail the
benchmark job for the downstream comment/commit jobs -- the Standalone throughput
headline and the regenerated baseline still publish, and that run simply omits the
allocation/byte tables (no PlayMode leg measured them) until the PlayMode leg is
healthy again (see [Editor-vs-player rationale](#editor-vs-player-rationale)).

## Scenario taxonomy

There are two scenario families. The DxMessaging-only family measures raw
dispatch throughput across DxMessaging's own paths; the comparison family is the
apples-to-apples set every library bridge implements (or declares unsupported).

### Dispatch scenarios (DxMessaging only)

The eighteen dispatch-throughput scenarios are defined in
[`DispatchThroughputBenchmarks.cs`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs).
The first eight are warm/hot throughput; the remaining ten are cold, warm-JIT, or
marginal-registration latency (zero-throughput, wall-clock rows; see
[Cold vs warm/hot modes](#cold-vs-warmhot-modes)). The three marginal-registration
rows report the GC-allocation cost of an additional same-type registration -- the
surface the registration allocation work reduced -- and are measurable only where the
profiler is present (the in-editor PlayMode/Mono leg; the published Standalone IL2CPP
leg strips it, so their allocation columns read `n/a` there):

| Scenario key                                  | What it measures                                                       |
| --------------------------------------------- | ---------------------------------------------------------------------- |
| `UntargetedFlood_OneHandler`                  | One untargeted handler on one message type.                            |
| `UntargetedFlood_FourHandlers_OnePriority`    | Four untargeted handlers sharing priority 0.                           |
| `UntargetedFlood_FourHandlers_FourPriorities` | Four untargeted handlers across priorities 0-3.                        |
| `TargetedFlood_OneListener`                   | One targeted listener on one target.                                   |
| `TargetedFlood_SixteenListeners`              | Sixteen targeted listeners on one target.                              |
| `BroadcastFlood_OneHandler`                   | One broadcast handler.                                                 |
| `InterceptorHeavy_FourInterceptors`           | Four interceptors plus one handler.                                    |
| `PostProcessingHeavy_FourPostProcessors`      | Four post-processors plus one handler.                                 |
| `RegistrationFlood_1000Types_FromColdBus`     | Registering 1000 distinct message types from a cold bus (cold flood).  |
| `RegistrationFlood_1000Types_WarmJit`         | Registering the same 1000 types after a JIT pre-warm (warm-JIT flood). |
| `UntargetedRegistration_Marginal`             | Marginal cost of 1000 more untargeted handlers on one warm type.       |
| `TargetedRegistration_Marginal`               | Marginal cost of 1000 more targeted handlers on one warm type/target.  |
| `BroadcastRegistration_Marginal`              | Marginal cost of 1000 more broadcast handlers on one warm type/source. |
| `DeregistrationFlood_1000Types_Cold`          | Tearing down 1000 live registrations, JIT-inclusive (cold flood).      |
| `DeregistrationFlood_1000Types_WarmJit`       | Tearing down the same 1000 registrations after a JIT pre-warm.         |
| `UntargetedFirstDispatch_Cold`                | First untargeted dispatch per type, JIT-inclusive, median of 32 types. |
| `TargetedFirstDispatch_Cold`                  | First targeted dispatch per type, JIT-inclusive, median of 32 types.   |
| `BroadcastFirstDispatch_Cold`                 | First broadcast dispatch per type, JIT-inclusive, median of 32 types.  |

### Comparison scenarios (cross-library)

The eight apples-to-apples comparison scenarios are defined in
[`ComparisonScenario.cs`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Comparisons/ComparisonScenario.cs).
Each library implements only the scenarios it idiomatically supports; an
unsupported scenario renders `N/A` in the matrix and is **never faked**:

| #   | Scenario key      | What it measures                      |
| --- | ----------------- | ------------------------------------- |
| S1  | `GlobalToOne`     | Global dispatch to one subscriber.    |
| S2  | `GlobalToMany`    | Global dispatch to 16 subscribers.    |
| S3  | `KeyedToOne`      | Keyed/targeted dispatch to 1 of many. |
| S4  | `PriorityOrdered` | Priority-ordered dispatch.            |
| S5  | `Filtered`        | Filtered/intercepted dispatch.        |
| S6  | `PostProcess`     | Post-processing dispatch.             |
| S7  | `SubUnsub`        | Subscribe/unsubscribe churn.          |
| S8  | `StructNoBox`     | Struct message dispatch (no boxing).  |

### Comparison vs dispatch: deliberately different topologies

The comparison matrix and the dispatch-throughput table are **two different
measurements**, not two views of the same number. Each comparison scenario is the
shape every library can implement idiomatically; each dispatch scenario is tuned to
stress one DxMessaging path. Where a comparison cell and a dispatch cell look like
"the same" scenario, they usually register a **different topology**, so their
DxMessaging numbers are expected to differ -- often substantially. Read the two
tables as answering different questions; do not expect a comparison cell to match
its dispatch look-alike unless the row below says they are a true twin.

The map below is pinned by
[`ComparisonDispatchTopologyTests`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Comparisons/ComparisonDispatchTopologyTests.cs);
that suite fails the build if the DxMessaging fan-out, the dispatch scenario keys, or
the scenario roster drift from this table, so this documentation and the code cannot
silently desync.

| Comparison cell   | DxMessaging shape                    | Nearest dispatch cell                         | True twin? | Why they differ                                                                                                                                       |
| ----------------- | ------------------------------------ | --------------------------------------------- | ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GlobalToOne`     | 1 token, 1 untargeted handler        | `UntargetedFlood_OneHandler`                  | **Yes**    | Identical shape; the DxMessaging numbers must agree within noise.                                                                                     |
| `StructNoBox`     | 1 token, 1 untargeted handler        | `UntargetedFlood_OneHandler`                  | **Yes**    | Same shape as `GlobalToOne`; this row exists to expose other libraries' boxing in the GC-allocations column.                                          |
| `GlobalToMany`    | 16 tokens, 16 untargeted handlers    | (none)                                        | No         | No dispatch scenario fans untargeted dispatch out to 16 handlers (the dispatch family caps untargeted fan-out at four).                               |
| `KeyedToOne`      | 16 targets registered, dispatch to 1 | `TargetedFlood_OneListener`                   | No         | Measures lookup selectivity (16 registered, 1 fires); the dispatch cell registers a single target.                                                    |
| `PriorityOrdered` | 1 token, 4 priorities                | `UntargetedFlood_FourHandlers_FourPriorities` | No         | Comparison uses one MessageHandler with four handler-store entries; the dispatch cell uses four separate tokens. Same fan-out (4), different storage. |
| `Filtered`        | 1 interceptor + 1 handler            | `InterceptorHeavy_FourInterceptors`           | No         | Comparison runs one interceptor; the dispatch cell runs four.                                                                                         |
| `PostProcess`     | 1 post-processor + 1 handler         | `PostProcessingHeavy_FourPostProcessors`      | No         | Comparison runs one post-processor; the dispatch cell runs four.                                                                                      |
| `SubUnsub`        | register/unregister churn cycle      | (none)                                        | No         | The dispatch family has no subscribe/unsubscribe-throughput scenario.                                                                                 |

**Fresh-state guarantee.** Every bridge is constructed fresh per (tech, scenario)
case by `ComparisonHarness.Run` (factory + `using`), so no subscriber, GameObject,
ScriptableObject, or DI container survives into the next case. The DxMessaging path
uses a fresh `new MessageBus()` per scenario, mirroring the dispatch benchmarks'
isolated bus. The harness's `ProgressMarker` assertion is a hard tripwire that fails
loudly if any case ever fans out more (or fewer) invocations than declared, which is
exactly what a leaked cross-case subscriber would do. So a comparison/dispatch
divergence is always topology, never shared global state.

## How CI produces and publishes the numbers

The [Performance Numbers workflow](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/perf-numbers.yml)
(`.github/workflows/perf-numbers.yml`) runs on every pull request to and push on
`master`/`main`. It runs two published legs with comparisons enabled
(`-IncludeComparisons`):

- **Standalone (IL2CPP Release player)** -- a built IL2CPP player with
  `BuildOptions.Development` stripped and the Release IL2CPP C++ configuration;
  the throughput headline scope.
- **In-editor PlayMode (Mono)** -- an editor-only run (the matrix provisions
  `EditorOnly` rather than the IL2CPP build support, and builds no player) whose
  functional `GC.Alloc` recorder supplies the real allocation counts the Release
  player cannot measure. This leg is `continue-on-error` (NON-BLOCKING): only the
  Standalone leg gates publishing, so a flaky PlayMode failure still lets the
  Standalone headline + baseline publish; that run simply omits the allocation/byte
  tables because no leg measured them (see [Editor-vs-player rationale](#editor-vs-player-rationale)).

After the legs finish, `scripts/unity/render-perf-doc.js` reads the benchmark
rows from BOTH legs and rewrites the AUTOGENERATED region of
`docs/architecture/performance.md`. The renderer derives the execution scope
from each row's platform string (`Standalone`, `PlayMode`, `EditMode`), emits
one dispatch-throughput table per scope present (in headline order: Standalone,
then PlayMode, then EditMode), and emits the cross-library comparison matrices.
Each per-scope dispatch table omits any profiler metric column it could not measure
(the Standalone Release table is throughput-only), so a column never degenerates to
all-`n/a`. The throughput comparison matrix uses the first scope present in headline
order (Standalone in published runs); the GC-allocation COUNT matrix and the
GC-allocated-BYTES matrix each use the first scope that actually MEASURED their
metric (the PlayMode Mono leg in published runs), and each is labeled with its own
scope -- a metric no present scope measured has its whole matrix omitted rather than
rendered all-`n/a`. These selectors are intentionally independent because
a backend can expose the byte counter while the allocation-count recorder is
unavailable, or vice versa. Each table's backend
label (Mono or IL2CPP) is derived from the platform string in that scope's rows,
so the heading follows the data. Scenario rows and library rows are joined on
stable machine keys
(`DispatchBenchmarkScenarios.Key`, `ComparisonScenarios.Key`, and each bridge's
`TechKey`), never on display names.

The doc and PR comment carry a privacy-safe provenance line describing the
runner HARDWARE -- CPU, physical/logical cores, clock, RAM size/speed/type, GPU,
and OS -- collected by
[`scripts/unity/collect-machine-specs.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/collect-machine-specs.ps1)
and embedded by `render-perf-doc.js --machine-specs`. The script deliberately
emits NO hostname or runner name; when the probe yields nothing the renderer
falls back to a neutral description.

On a pull request the refreshed numbers post as a non-blocking sticky comment
(marker `<!-- dxm-perf-autonumbers -->`); the workflow never pushes to the
contributor branch. A second sticky comment (marker `<!-- dxm-perf-deltas -->`)
posts the DxMessaging-only deltas against the committed master baseline when a
metric moves beyond tolerance or the regression gate will fail -- see
[Permanent regression gate](#permanent-regression-gate). After the pull request
merges, the push run re-renders and, if the doc OR the baseline moved, commits
both `docs/architecture/performance.md` and the regenerated
`docs/architecture/perf-baseline.csv` directly to the default branch. The
auto-commit mechanics (the GitHub App token, branch-protection bypass, and the
`paths-ignore` loop break covering both files) are in the
[perf-numbers auto-commit runbook](perf-numbers-auto-commit.md).

### Editor-vs-player rationale

The throughput headline is **Standalone under IL2CPP in a true Release player**
because it is the highest shipping fidelity: shipped titles run ahead-of-time
(AOT) compiled Release players, not the editor. A development build or a Debug
IL2CPP C++ configuration changes the measured numbers, so the published leg
strips `BuildOptions.Development` and pins the Release C++ configuration;
`Debug.isDebugBuild` must be false in every published run.

Allocations, however, are **only measurable where the profiler is available**. A
Release player strips the `GC.Alloc` recorder, so the Standalone leg cannot measure
allocations or bytes at all, and the renderer omits those memory columns entirely
(rather than filling them with `n/a`). To publish real
allocation numbers the workflow re-adds an in-editor **PlayMode (Mono)** leg,
where the recorder is functional. The differentiator from the Standalone leg is
the profiler, NOT bridge coverage: a built Standalone player has
`Application.isPlaying == true` too, so BOTH legs run every comparison bridge
(including the `Application.isPlaying`-only ones) and produce a complete matrix --
only the editor's Mono backend can additionally MEASURE the allocations. (PlayMode
rather than EditMode supplies them because EditMode runs with
`Application.isPlaying == false` and so cannot host the `isPlaying`-only bridges.)
Allocation counts are build-config-independent (the same code paths box and
allocate under Mono and IL2CPP), so the Mono counts represent the shipped IL2CPP
player; this is the documented provenance for sourcing throughput from one leg and
allocations from another. The renderer therefore sources the throughput comparison
matrix from the first scope present (Standalone in published runs) and BOTH the
GC-allocation COUNT matrix and the GC-allocated-BYTES matrix from the first scope
that measured each metric (PlayMode in published runs).

The PlayMode allocation leg is **non-blocking** (`continue-on-error` in
`perf-numbers.yml`, see [How CI produces and publishes the
numbers](#how-ci-produces-and-publishes-the-numbers)). The Standalone throughput
leg is the headline and the baseline source, so it gates publishing; a flaky
PlayMode leg must not. When the PlayMode leg fails, the matrix job still reports
success for the downstream comment/commit jobs, the Standalone headline and the
regenerated baseline publish as usual, and that run simply omits the
allocation/byte tables (no leg measured them) until the PlayMode leg is healthy
again.

EditMode runs inside the editor's hosting environment, is the least
representative of shipping behavior, and is not published; it -- and PlayMode --
remain useful for local iteration and for the weekly per-version benchmark-test
coverage in `unity-benchmarks.yml`.

## Baseline capture

### The committed master baseline

[`docs/architecture/perf-baseline.csv`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/docs/architecture/perf-baseline.csv)
is committed and is the baseline the CI regression gate and the PR delta comment
compare against. It ships as an honest header-only seed -- the column header with
no data rows -- so the first rollout has no fabricated numbers. Each push to the
default branch regenerates it with real Standalone IL2CPP rows from that run
(`extract-perf-baseline.js --replace --scope Standalone`) and commits it
alongside `performance.md`, so the seed becomes real after the first master push.
Even though the perf run now also produces in-editor PlayMode (Mono) rows, the
baseline regeneration passes `--scope Standalone` so the committed baseline stays
**Standalone-only** -- the regression gate is Standalone-scoped (it compares
`--scope Standalone` rows against the baseline), so mixing the PlayMode rows in
would make the gate compare against the wrong scope. A missing or header-only
baseline makes both the gate and the delta comment skip gracefully. The committed
baseline is therefore CI-owned and Standalone-scoped.

To capture a baseline locally, run the explicit `DispatchThroughputBenchmarks`
baseline-update test in PlayMode through the MCP loop against the host editor (run
the benchmark assembly via `DxMcpTestRunner.Run`; see the Unity MCP Test Loop
skill). The benchmark CSV defaults to `.artifacts/perf-baseline.csv` (override
with the `DX_PERF_BASELINE` env var), and `DX_PERF_COMMIT` stamps the commit
column. Because baseline rows match on scenario + platform, a locally captured
PlayMode CSV serves local within-scope comparison (for example the local smoke
gate), not the published Standalone baseline.

### Ad-hoc baselines for regression work

Capture ad-hoc baselines into a local CSV file and keep the file path explicit
in the commands that consume it. Do not put generated baseline CSVs in package
documentation or rely on a dated filename. For CI or release comparison, publish
the CSV as a workflow artifact or attach it to the pull request that records the
measurement.

Recommended commit cells:

| Commit reference              | Purpose                                 |
| ----------------------------- | --------------------------------------- |
| Chosen comparison commit      | Accepted baseline for regression gates. |
| Previous optimization landing | Runtime after the last relevant change. |
| `HEAD`                        | Current branch result.                  |

Required configuration cells:

| Configuration                 | Requirement                                                   |
| ----------------------------- | ------------------------------------------------------------- |
| Standalone IL2CPP x64 Release | Required; the headline and only published scope.              |
| PlayMode Mono                 | Optional; local iteration scope, never published or compared. |

For each commit and configuration:

- Keep the benchmark harness available; older runtime commits may not contain
  the benchmark files.
- Measure the older runtime with a harness-preserving flow. Use a throwaway
  branch that cherry-picks the current harness onto the measured runtime commit,
  or keep the harness branch checked out and swap only the runtime files being
  measured.
- Set `DX_PERF_COMMIT=<measured-runtime-commit>` for every benchmark run so CSV
  rows identify the runtime commit under test. `DX_PERF_COMMIT` overrides CI's
  `GITHUB_SHA` when both are present.
- Run the benchmarks in batchmode with the same Release configuration CI uses:
  the Standalone IL2CPP leg
  (`-StandaloneScriptingBackend IL2CPP -ReleasePlayerBuild -ReleaseCodeOptimization`)
  for rows comparable to the published scope, or the PlayMode leg
  (`-ReleaseCodeOptimization`) for faster local iteration.
- Extract the benchmark rows from the Unity output and append them to the local
  baseline CSV.
- Record the exact commit, platform, scope, Unity version, and scripting
  backend.

Do not mix methodology changes with baseline updates. If the harness changes,
capture a new baseline and make the old/new methodology boundary explicit in the
PR description. In particular, a baseline captured under the old median-of-runs
methodology is not comparable to one captured under the current single-window
methodology -- recapture rather than compare across the boundary.

## Permanent regression gate

The permanent regression gate is a CI step, not a C# test. After the perf leg
runs, the PR job calls
[`scripts/unity/render-perf-deltas.js`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/render-perf-deltas.js)
with `--scope Standalone`, which compares this PR's Standalone IL2CPP
DxMessaging numbers against the committed master baseline and prints two lines: `changed=true|false` (whether any metric
moved beyond `--tolerance`) and `regressed=true|false` (the gate signal). The
job posts the DxMessaging-only delta comment when `changed=true` OR
`regressed=true`, then fails when `regressed=true` -- so reviewers always see the
numbers even when a strict gate failure did not exceed the comment tolerance.
The script always exits 0 itself; the workflow decides whether to fail from the
`regressed=` line.

A scenario regresses when its throughput drops by more than the regression
threshold (default `0.33`, looser than the comment tolerance) OR its allocation
exceeds the baseline. Only canonical dispatch scenarios participate in the hard
gate, so the wall-clock rows (the cold/warm-JIT registration and deregistration
floods and the cold first-dispatch scenarios, all zero throughput) never trip the
gate. The delta
comment is still broader diagnostic output: it keeps the dispatch scenarios plus
the DxMessaging comparison rows and drops every other library's rows. Comparison
rows are report-only because a single cross-library comparison sample is too
noisy for required CI. A missing or header-only baseline yields `changed=false`
and `regressed=false`, which skips both the delta comment and gate for a graceful
first-rollout pass.

## Local-only C# smoke gate

`Tests/Editor/Benchmarks/PerfRegressionSmokeTests.cs` is now a LOCAL tool only;
it is `[Explicit, Category("PerfGate")]` and not part of the permanent gate. Use
it to fail a local run when a within-platform regression exceeds 1.5x against a
captured baseline. Enable it with:

```bash
DX_PERF_GATE=1 \
DX_PERF_BASELINE=<baseline.csv> \
DX_PERF_BASELINE_COMMIT=<baseline-commit> \
pwsh scripts/unity/run-ci-tests.ps1 -TestMode editmode -ReleaseCodeOptimization -ReleasePlayerBuild
```

The commit matching was relaxed for this local use. When
`DX_PERF_BASELINE_COMMIT` is unset the gate matches the baseline row on
`scenario` + `platform` only (a committed master baseline reflects one historical
commit while a local run is at HEAD, so commit-exact matching would make the gate
impossible); when it IS set, the original commit-exact match is preserved. If
`DX_PERF_GATE=1` is set without `DX_PERF_BASELINE`, if the CSV is missing, or if
NO baseline row matches the current scenario + platform (for example on a
different Unity version or OS than the captured baseline), the gate now skips
gracefully rather than failing. Because the baseline and the current run must use
the same single-window methodology, do not gate against a baseline captured under
the old median-of-runs approach.

### Hot-path review rule

When you change one of the hot paths, review the refreshed numbers the workflow
posts as a comment on your PR:

- `Runtime/Core/MessageBus/MessageBus.cs`
- `Runtime/Core/MessageHandler.cs`
- `Runtime/Core/Pooling/**`

An unexpected throughput drop or a new non-zero allocation count in the rendered
tables is a regression to investigate before merging. The numbers track the
actual measured throughput of the branch under review rather than committed
state or a manually pasted table.

## Comparison packages: add or bump a library

The single source of truth for the comparison packages is
[`.github/comparison-packages.json`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/comparison-packages.json).
It pins the OpenUPM scoped registry, the exact package versions, the required
Unity built-in packages (for example `com.unity.ugui` and
`com.unity.modules.animation`), and the `versionDefines` symbols (for example
`MESSAGEPIPE_PRESENT`, `UNIRX_PRESENT`, `ZENJECT_PRESENT`,
`UNITY_ATOMS_CORE_PRESENT`, and `UNITY_ATOMS_BASE_ATOMS_PRESENT`). The
comparison legs build an ephemeral manifest from this file; the committed
`.unity-test-project/Packages/manifest.json` and
`.unity-test-project/Packages/packages-lock.json` keep local parity.

The external and Unity Atoms comparison suites are package-gated and live under:

- [`Tests/Runtime/Comparisons/External/`](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Tests/Runtime/Comparisons/External)
  -- MessagePipe, UniRx, Zenject SignalBus.
- [`Tests/Runtime/Comparisons/UnityAtoms/`](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Tests/Runtime/Comparisons/UnityAtoms)
  -- Unity Atoms.

The zero-dependency baselines (plain C# event, UnityEvent, ScriptableObject
channel, Unity `SendMessage`) live directly in
[`Tests/Runtime/Comparisons/`](https://github.com/Ambiguous-Interactive/DxMessaging/tree/master/Tests/Runtime/Comparisons)
and need no package.

To add or bump a comparison library:

1. Edit `.github/comparison-packages.json`: add or change the registry scope,
   the pinned package version(s), any required `unityBuiltInPackages`, and the
   `defines` symbol(s).
1. Update the matching `versionDefines` in the gated comparison asmdef(s) under
   `Tests/Runtime/Comparisons/External/` and/or
   `Tests/Runtime/Comparisons/UnityAtoms/` so the gated code compiles only when
   the package is present.
1. Update the committed `.unity-test-project/Packages/manifest.json` and
   `.unity-test-project/Packages/packages-lock.json` to keep local parity with
   the single source.
1. Re-check by hand that every consumer (asmdef `versionDefines` /
   `defineConstraints`, manifest, package lock) agrees with
   `.github/comparison-packages.json`.

## History note

The numbers on this page were previously produced by a now-removed editor-side
PlayMode benchmark suite that wrote per-OS (Windows/macOS/Linux) tables by hand
and used a median-of-short-windows methodology. Those hand tables and the
old comparison tables (which lived in a now-deleted editor comparison test
directory) have been
**superseded by the CI-generated per-scope tables** rendered into
`docs/architecture/performance.md`. The only enduring guidance from that era is
the tradeoff intuition that still holds: interceptors and post-processors add
real overhead (with several interceptors or post-processors registered,
throughput drops materially versus the no-interceptor baseline), and reflexive
(dynamic) messaging is slower than direct handler registration because of
reflection overhead. Treat any pre-migration number as non-comparable to the
current single-window results.
