# Perf Benchmark Methodology Runbook

This runbook is the developer/ops reference for how DxMessaging's published
performance numbers are produced. The user-facing page that shows the rendered
tables is [Performance Benchmarks](../architecture/performance.md); this runbook
covers the methodology behind those tables, the CI configuration that runs them,
the scenario taxonomy, baseline capture, the opt-in regression smoke gate, and
how to add or bump a comparison library.

## Measurement methodology

The benchmark harness measures raw dispatch cost over a single continuous
window:

1. Warm up the scenario (`BenchmarkProtocol.WarmupEmits`, currently 10,000
   emits) so JIT and pools reach steady state.
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
dispatch.

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

The published numbers are measured under **Mono + .NET Standard 2.1 + Release**.
All legs are driven by
[`scripts/unity/run-ci-tests.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/run-ci-tests.ps1):

- **EditMode / PlayMode legs** pass `-releaseCodeOptimization`, which sets
  `CompilationPipeline.codeOptimization = Release` so test assemblies compile
  without debug code paths.
- **Standalone perf leg** builds a **Mono2x non-development (Release) player**
  with `ApiCompatibilityLevel.NET_Standard` (the non-deprecated profile that
  targets .NET Standard 2.1) and **disabled managed code stripping**, so the
  test assemblies and the `[Preserve]` standalone test-run callback survive into
  the player.

EditMode is exercised by the shared protocol but is **not published** (see
[Editor-vs-player rationale](#editor-vs-player-rationale)).

## Scenario taxonomy

There are two scenario families. The DxMessaging-only family measures raw
dispatch throughput across DxMessaging's own paths; the comparison family is the
apples-to-apples set every library bridge implements (or declares unsupported).

### Dispatch scenarios (DxMessaging only)

The nine dispatch-throughput scenarios are defined in
[`DispatchThroughputBenchmarks.cs`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs):

| Scenario key                                  | What it measures                                         |
| --------------------------------------------- | -------------------------------------------------------- |
| `UntargetedFlood_OneHandler`                  | One untargeted handler on one message type.              |
| `UntargetedFlood_FourHandlers_OnePriority`    | Four untargeted handlers sharing priority 0.             |
| `UntargetedFlood_FourHandlers_FourPriorities` | Four untargeted handlers across priorities 0-3.          |
| `TargetedFlood_OneListener`                   | One targeted listener on one target.                     |
| `TargetedFlood_SixteenListeners`              | Sixteen targeted listeners on one target.                |
| `BroadcastFlood_OneHandler`                   | One broadcast handler.                                   |
| `InterceptorHeavy_FourInterceptors`           | Four interceptors plus one handler.                      |
| `PostProcessingHeavy_FourPostProcessors`      | Four post-processors plus one handler.                   |
| `RegistrationFlood_1000Types_FromColdBus`     | Registering 1000 distinct message types from a cold bus. |

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

- **PlayMode (Mono)** -- in-editor play mode; needs no player toolchain.
- **Standalone (Mono)** -- a built Mono2x player; the player-fidelity scope.

After the legs finish, `scripts/unity/render-perf-doc.js` reads the benchmark
rows and rewrites the AUTOGENERATED region of
`docs/architecture/performance.md`. The renderer derives the execution scope
from each row's platform string (`Standalone`, `PlayMode`, `EditMode`), emits
one dispatch-throughput table per scope present (in player-fidelity order:
Standalone, then PlayMode, then EditMode), and emits two cross-library
comparison matrices -- one for throughput and one for bytes per operation --
using the most player-faithful scope available. Scenario rows and library rows
are joined on stable machine keys (`DispatchBenchmarkScenarios.Key`,
`ComparisonScenarios.Key`, and each bridge's `TechKey`), never on display names.

On a pull request the refreshed numbers post as a non-blocking sticky comment;
the workflow never pushes to the contributor branch. After the pull request
merges, the push run re-renders and, if the numbers moved, commits the refreshed
doc directly to the default branch. The auto-commit mechanics (the GitHub App
token, branch-protection bypass, and the `paths-ignore` loop break) are in the
[perf-numbers auto-commit runbook](perf-numbers-auto-commit.md).

### Editor-vs-player rationale

The published headline is the **Standalone (player) scope** because a built
player is the most representative of shipped behavior: it runs the same
scripting backend (Mono2x), the same API profile (.NET Standard 2.1), and the
same Release code path your users get. In-editor PlayMode is published as a
secondary scope so contributors can see the development-time picture, but
EditMode is **not published**: it runs inside the editor's hosting environment
and is the least representative of shipping behavior. The renderer always
prefers the most player-faithful scope for the comparison matrices.

## Baseline capture

Capture baselines into a local CSV file and keep the file path explicit in the
commands that consume it. Do not put generated baseline CSVs in package
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

| Configuration       | Requirement                                            |
| ------------------- | ------------------------------------------------------ |
| PlayMode Mono       | Required.                                              |
| Standalone Mono x64 | Required; this is the published player-fidelity scope. |

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

## Opt-in regression smoke gate

The documented numbers are maintained entirely by CI; you do not hand-capture
them in the PR description. The optional smoke gate lets you fail a local or CI
run when a within-scope regression exceeds a threshold against a captured
baseline.

The gate reads `DX_PERF_BASELINE`, requires a row for the configured comparison
commit on the current scenario and platform identity, and fails when a
within-scope regression exceeds the configured threshold. Enable it with:

```bash
DX_PERF_GATE=1 \
DX_PERF_BASELINE=<baseline.csv> \
DX_PERF_BASELINE_COMMIT=<baseline-commit> \
pwsh scripts/unity/run-ci-tests.ps1 -TestMode editmode -ReleaseCodeOptimization
```

If the gate is enabled without `DX_PERF_BASELINE` or `DX_PERF_BASELINE_COMMIT`,
or if the configured CSV is missing, it reports an inconclusive skip instead of
failing the suite for a missing local artifact. Because the baseline and the
current run must use the same single-window methodology, do not gate against a
baseline captured under the old median-of-runs approach.

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
It pins the OpenUPM scoped registry, the exact package versions, and the
`versionDefines` symbols (for example `MESSAGEPIPE_PRESENT`, `UNIRX_PRESENT`,
`ZENJECT_PRESENT`, `UNITY_ATOMS_PRESENT`). The comparison legs build an ephemeral
manifest from this file; the committed
`.unity-test-project/Packages/manifest.json` keeps local parity.

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
   the pinned package version(s), and the `defines` symbol(s).
1. Update the matching `versionDefines` in the gated comparison asmdef(s) under
   `Tests/Runtime/Comparisons/External/` and/or
   `Tests/Runtime/Comparisons/UnityAtoms/` so the gated code compiles only when
   the package is present.
1. Update the committed `.unity-test-project/Packages/manifest.json` to keep
   local parity with the single source.
1. Run the drift validator (`npm run validate:comparison-packages`, added in a
   later slice) to confirm every consumer agrees with
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
