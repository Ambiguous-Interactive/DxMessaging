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
   scenario except the cold registration flood, which warms up 0 emits so it
   measures first-touch registration cost. `ComparisonScenarios.WarmupEmits`
   applies the same policy to the comparison bridges.
1. Sample `GC.GetAllocatedBytesForCurrentThread()` and start a stopwatch.
1. Emit in batches until ONE continuous measurement window of `N` seconds has
   elapsed (`BenchmarkProtocol.MeasurementSeconds`, currently `N = 5`).
1. Sample allocated bytes again at the end of the window.

Throughput is `total operations / measured elapsed seconds`. The GC delta is
captured over the same window, so allocation is attributed to exactly the work
that produced the throughput. There is **no median-of-runs**: the older approach
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

The cold counterpart to `BenchmarkProtocol.Measure` is
`BenchmarkProtocol.MeasureColdLatency`. It runs K trials; each trial builds fresh
state (untimed, indexed so the caller picks a distinct closed type per trial),
times EXACTLY ONE operation on it, then tears the state down (untimed). It reports
the median wall clock and median allocation across the trials (cold latency is
right-skewed, so the median is the headline). The three cold dispatch scenarios are
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

Allocation budgets are interpreted as bytes allocated during the measured
window. Dispatch scenarios should stay at zero measured bytes after warmup. Any
non-zero allocation delta on a hot-path dispatch scenario requires an
explanation, a fix, or an explicit reviewer-approved exception.

## Build and runtime configuration

The headline numbers are measured under **Mono + .NET Standard 2.1 + Release**;
a second leg adds **Standalone IL2CPP** for ahead-of-time (AOT) coverage. All
legs are driven by
[`scripts/unity/run-ci-tests.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/run-ci-tests.ps1):

- **PlayMode perf leg (headline)** runs in-editor under Mono and passes
  `-releaseCodeOptimization`, which sets
  `CompilationPipeline.codeOptimization = Release` so test assemblies compile
  without debug code paths. Mono is the backend the library ships with for most
  targets, and this leg is the fastest scope to run on every change, so it is the
  headline.
- **Standalone perf leg (AOT)** builds an **IL2CPP non-development (Release)
  player** with `ApiCompatibilityLevel.NET_Standard` (the non-deprecated profile
  that targets .NET Standard 2.1) and **disabled managed code stripping**, so the
  test assemblies and the `[Preserve]` standalone test-run callback survive into
  the player. The runner's `-StandaloneScriptingBackend` parameter defaults to
  `IL2CPP` and also accepts `Mono2x`; the published AOT leg pins IL2CPP.

The harness can exercise EditMode through the shared protocol, but
`perf-numbers.yml` does **not run or publish** EditMode (see
[Editor-vs-player rationale](#editor-vs-player-rationale)).

## Scenario taxonomy

There are two scenario families. The DxMessaging-only family measures raw
dispatch throughput across DxMessaging's own paths; the comparison family is the
apples-to-apples set every library bridge implements (or declares unsupported).

### Dispatch scenarios (DxMessaging only)

The thirteen dispatch-throughput scenarios are defined in
[`DispatchThroughputBenchmarks.cs`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs).
The first eight are warm/hot throughput; the last five are cold or warm-JIT latency
(see [Cold vs warm/hot modes](#cold-vs-warmhot-modes)):

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
| S8  | `StructNoBox`     | Struct message dispatch (zero-copy).  |

## How CI produces and publishes the numbers

The [Performance Numbers workflow](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/perf-numbers.yml)
(`.github/workflows/perf-numbers.yml`) runs on every pull request to and push on
`master`/`main`. It runs two legs with comparisons enabled
(`-IncludeComparisons`):

- **PlayMode (Mono)** -- in-editor play mode on the shipped Mono backend; needs
  no player toolchain. This is the headline scope.
- **Standalone (IL2CPP)** -- a built IL2CPP player; the AOT leg.

After the legs finish, `scripts/unity/render-perf-doc.js` reads the benchmark
rows and rewrites the AUTOGENERATED region of
`docs/architecture/performance.md`. The renderer derives the execution scope
from each row's platform string (`PlayMode`, `Standalone`, `EditMode`), emits
one dispatch-throughput table per scope present (in headline order: PlayMode,
then Standalone, then EditMode), and emits two cross-library comparison matrices
-- one for throughput and one for bytes per operation -- using the first scope
present (PlayMode when available). Each table's backend label (Mono or IL2CPP) is
derived from the platform string in that scope's rows, so the heading follows the
data. Scenario rows and library rows are joined on stable machine keys
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

The headline is **PlayMode under Mono** because Mono is the backend the library
ships with for most targets and the PlayMode leg is the fastest scope to run on
every change. **Standalone under IL2CPP** is published alongside as the AOT leg:
a built IL2CPP player exercises ahead-of-time codegen on the backend some
platforms require, so it gives a comparable data point for those targets.
EditMode is **not published**: it runs inside the editor's hosting environment
and is the least representative of shipping behavior. The renderer uses the
first scope present (PlayMode when available) for the comparison matrices.

## Baseline capture

### The committed master baseline

[`docs/architecture/perf-baseline.csv`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/docs/architecture/perf-baseline.csv)
is committed and is the baseline the CI regression gate and the PR delta comment
compare against. It ships as an honest header-only seed -- the column header with
no data rows -- so the first rollout has no fabricated numbers. Each push to the
default branch regenerates it with real PlayMode + Standalone IL2CPP rows from
that run (`extract-perf-baseline.js --replace`) and commits it alongside
`performance.md`, so the seed becomes real after the first master push. A
missing or header-only baseline makes both the gate and the delta comment skip
gracefully.

To capture or refresh the committed baseline locally, run
[`scripts/unity/capture-perf-baseline.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/capture-perf-baseline.ps1)
(`-Commit <ref>`, `-Output <csv>`, `-Replace` to overwrite). It runs the
explicit baseline-update PlayMode benchmark and writes the normalized CSV.

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

| Configuration         | Requirement                                     |
| --------------------- | ----------------------------------------------- |
| PlayMode Mono         | Required; this is the published headline scope. |
| Standalone IL2CPP x64 | Required; the published AOT leg.                |

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
- Run the PlayMode benchmarks in batchmode with the same Release configuration
  CI uses (`-releaseCodeOptimization`).
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

The permanent regression gate is a CI step, not a C# test. After the perf legs
run, the PR job calls
[`scripts/unity/render-perf-deltas.js`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/render-perf-deltas.js),
which compares this PR's PlayMode DxMessaging numbers against the committed
master baseline and prints two lines: `changed=true|false` (whether any metric
moved beyond `--tolerance`) and `regressed=true|false` (the gate signal). The
job posts the DxMessaging-only delta comment when `changed=true` OR
`regressed=true`, then fails when `regressed=true` -- so reviewers always see the
numbers even when a strict gate failure did not exceed the comment tolerance.
The script always exits 0 itself; the workflow decides whether to fail from the
`regressed=` line.

A scenario regresses when its throughput drops by more than the regression
threshold (default `0.33`, looser than the comment tolerance) OR its allocation
exceeds the baseline. Only throughput scenarios participate, so the wall-clock
rows (the cold/warm-JIT registration floods and the cold first-dispatch scenarios,
all zero throughput) never trip the gate. The comparison is DxMessaging-only: the
delta comment keeps the dispatch scenarios plus the DxMessaging comparison rows
and drops every other library's rows. A
missing or header-only baseline yields `changed=false` and `regressed=false`,
which skips both the delta comment and gate for a graceful first-rollout pass.

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

An unexpected throughput drop or a new non-zero allocation in the rendered
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
