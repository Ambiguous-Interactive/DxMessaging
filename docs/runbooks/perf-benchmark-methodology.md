# Perf Benchmark Methodology Runbook

This runbook is the developer/ops reference for how DxMessaging's published
performance numbers are produced. The user-facing page that shows the rendered
tables is [Performance Benchmarks](../architecture/performance.md); this runbook
covers the methodology behind those tables, the CI configuration that runs them,
the scenario taxonomy, baseline capture, the automatic pull-request evidence and the
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
columns/matrices entirely rather than publishing a vacuous wall of `n/a`. For local
or manually dispatched editor runs, the renderer sources the count and byte metrics
from the first scope that measured each rather than reusing the count scope for
bytes. Bytes are
INFORMATIONAL: rendered byte deltas are goodness-signed (`N fewer bytes` /
`N more bytes`), but allocation regression classification stays on the COUNT.
There is **no median-of-runs**: the older approach
of measuring several short sub-windows and comparing their median has been
replaced by this single long window. The shared protocol is the single source of
truth for every benchmark suite (dispatch throughput, comparisons) and lives in
[`Tests/Runtime/Benchmarks/BenchmarkProtocol.cs`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Benchmarks/BenchmarkProtocol.cs).
The diagnostic in-process paired fixture is the explicit exception: it uses
counterbalanced batches to normalize DxMessaging against an unchanged MessagePipe
control while canonical published rows keep the continuous-window method.

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
zero throughput is also what AUTO-EXCLUDES these rows from the PR throughput smoke
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
  approximately equal. Because the warm-JIT flood is repeatable (the JIT is already
  paid and a fresh population is rebuilt per trial), it runs
  `DispatchThroughputBenchmarks.WarmFloodTrials` trials and reports the MINIMUM wall
  clock -- the floor when the CPU was not preempted, the most reproducible estimator
  (the same philosophy as `AllocationProbe.MeasureMin`).
- **Deregistration floods.** `DeregistrationFlood_1000Types_Cold` and
  `DeregistrationFlood_1000Types_WarmJit` are the teardown mirror of the
  registration floods. Each stages the same 1000 cached flood builders on a live
  token UNTIMED, then times `MessageRegistrationToken.UnregisterAll()` -- the
  production deregistration path, which drains one bus deregistration per staged
  handler. The cold variant pays the Mono JIT compile of that path on its first
  call; the warm-JIT variant pre-pays it on a throwaway bus (register then
  `UnregisterAll`) and times the teardown of a fresh, fully populated token, so it
  isolates the data-structure dismantle cost. Like the registration floods, the
  warm-JIT deregistration flood is repeatable and reports the MINIMUM over
  `WarmFloodTrials` trials. Both are wall-clock rows, symmetric with the
  registration floods.
- **Registration-cycle attribution.** The four `RegistrationAttribution_*_131072`
  rows time complete same-type register/remove cycles through the direct bus,
  direct handler, disabled token, and active token layers. Every row reuses one
  cached by-ref handler, selects the operation outside the timed loop, and ends
  with zero bus registrations, flat handlers, token metadata, and deliveries.
  `TokenActive` matches the DxMessaging subscribe/unsubscribe comparison shape.
  `TokenStage` is a sibling measurement, not a cumulative lower layer. The
  `TokenActive` minus `DirectHandler` delta includes registration-object creation,
  arena/handle work, teardown bookkeeping, and augmented delegate binding; do not
  label it as token storage alone. Each row warms the exact cycle on throwaway
  state, settles the heap once, and reports the minimum of seven fresh timing
  trials. Allocation instrumentation runs afterward over seven fresh states. Each
  state executes 10000 warm-up cycles before the recorder opens, then measures a
  second 10000-cycle batch and reports the minimum count with bytes from that same
  attempt. This matches the comparison harness's warmed allocation batch size and
  state without putting profiler overhead in the latency clock. A stripped IL2CPP
  player reports `n/a` for both allocation fields.
- **Token dispatch attribution.** Compare `UntargetedFlood_OneDirectHandler` with
  `UntargetedFlood_OneHandler`. Both use the same by-ref fast slot, one active
  handler, and exact fan-out. The first registers the user delegate directly
  through `MessageHandler`; the second uses the public enabled-token path and its
  `AugmentedScalarFast` callback. The token's diagnostics flag remains mutable, so
  the augmented callback's per-dispatch branch is part of the public contract.
- **Deregistration attribution.** The four `DeregistrationAttribution_*_131072`
  rows report cumulative layers over one same-type, high-cardinality population:
  direct bus removal; handler-cache plus bus removal; token `RemoveRegistration`
  plus the lower layers; and token `Disable` queue teardown plus the lower layers.
  Within the same run, subtract direct bus from direct handler to estimate
  handler-cache cost, then direct handler from token removal to estimate
  per-handle token bookkeeping. Treat `Disable` as a sibling end-to-end path:
  unlike `RemoveRegistration`, it retains staged token state, so subtracting the
  two mixes queue work with omitted arena unlinking. Each row collects once before
  its seven-trial loop, prepares every fresh state outside the stopwatch, validates
  exact zero-registration and zero-delivery
  state after each sample, and reports the minimum elapsed time. Full teardown
  also clears the ordered handler storage before the clock stops, so the sample
  does not defer compaction to the next dispatch. Allocation columns are `n/a`:
  multi-second destructive windows collect ambient editor allocations, and a
  faster implementation would look like an allocation win merely by shortening
  exposure. Prove allocation changes with structural or short-window differential
  guards instead. The attribution entry and the published dispatch entry share
  `DispatchThroughputBenchmarks`: method-level NUnit `Order` runs every dispatch
  scenario before the 131072-cycle and retained-population attribution setup. These
  diagnostic wall-clock rows are not hard regression gates. Do not compare them
  with the 1000-type flood rows because their registration topology differs.
- **Deregistration attribution palindrome.** The non-published
  `DirectHandlerAndBusDeregistrationPalindromeDiagnostic` measures a
  handler-then-bus pair followed by a bus-then-handler pair after the published
  attribution rows. Each trial prepares all four fresh populations before timing,
  measures the H/B/B/H arms back-to-back, and selects the lowest complete
  palindrome duration across eight trials. Preparation direction alternates with
  four forward and four reverse opportunities, so the same endpoint is not always
  the most recently built state. The diagnostic
  never combines an arm from one trial with an arm from another. Keeping four
  populations live increases diagnostic peak memory, but it is required to avoid
  allocation-heavy setup between timed arms. The 131072 cardinality stays aligned
  with the published attribution rows and keeps the direct-bus arm near 10 ms on
  the measured Mono host. This diagnostic runs after the published rows and does
  not add player/runtime storage. It reports
  `DXM_DEREGISTRATION_ATTRIBUTION_PALINDROME` with both additive handler-minus-bus
  excesses and their arithmetic center. Interpret the sample only when both
  excesses are positive, handler and bus same-path drift are each at most 3%, and
  the two additive excesses differ by at most 3% of their center. Joint trial
  selection retains all four arms from one observation and records
  `jointTrialSelection=true` plus `sameTrialArms=true` in the marker. Its compact
  `trialSequence` records each trial index, preparation direction, and complete
  duration so an artifact consumer can verify alternation and the selected floor.
  `interpretable=true` is still a noise-rejection prerequisite, not candidate
  acceptance. The marker always records
  `diagnosticOnly=true`, `acceptanceEvidence=false`, and
  `candidateCompared=false`; require a separate repeated control/candidate
  bracket before claiming a 3% improvement.
- **Noise control on the wall-clock floods.** A single one-shot sample of a ~1 ms
  operation on a shared CI runner swings run-to-run by tens of percent (scheduler
  preemption, or a GC landing inside the timed window). Two mitigations: (1) the
  WARM/repeatable floods report the minimum over `WarmFloodTrials` trials (above);
  (2) EVERY flood (cold and warm) calls `QuiesceGarbageCollector()` -- a full
  `GC.Collect` + `WaitForPendingFinalizers` + `GC.Collect` -- STRICTLY BEFORE the
  measurement stopwatch starts, so a pending collection cannot land inside the timed
  region and inflate the sample. The COLD floods stay single-shot (their whole point
  is the one-time first-touch JIT cost, which cannot be re-measured cold), but are
  GC-quiesced. These floods are report-only (never gated -- see below), so the only
  effect is more trustworthy published numbers.

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

The published numbers are measured under **Standalone IL2CPP + .NET Standard
2.1 + Release** in a true Release player. The published workflow omits allocation
metrics because the Release player strips the required profiler recorder (see
[Editor-vs-player rationale](#editor-vs-player-rationale)). The leg is driven by
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
  the all-unmeasured memory matrices entirely.
- **EditMode leg (not published)** also runs in-editor under Mono with
  `-releaseCodeOptimization`. It remains a fast scope for local iteration, and
  manually dispatched `unity-benchmarks.yml` runs the editmode + playmode
  benchmark tests per Unity version as coverage; EditMode numbers are not
  published.

The harness can exercise every scope through the shared protocol.
`perf-numbers.yml` runs and publishes only the **Standalone IL2CPP leg** (see
[Editor-vs-player rationale](#editor-vs-player-rationale)). Local MCP runs and
manually dispatched `unity-benchmarks.yml` runs retain the editor scopes for fast
iteration and allocation investigation.

## Scenario taxonomy

There are two scenario families. The DxMessaging-only family measures raw
dispatch throughput across DxMessaging's own paths; the comparison family is the
apples-to-apples set every library bridge implements (or declares unsupported).

### Dispatch scenarios (DxMessaging only)

The scenario registry contains thirty-five DxMessaging rows: fifteen continuous-window
dispatch-throughput rows and twenty wall-clock rows. Twenty-seven rows are defined in
[`DispatchThroughputBenchmarks.cs`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs).
The eight diagnostic registration/deregistration attribution rows are defined in
[`RegistrationLifecycleBenchmarks.cs`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Benchmarks/RegistrationLifecycleBenchmarks.cs).
The twenty cold, warm-JIT, construction, marginal-registration, deregistration, and
attribution rows report zero throughput and wall-clock latency; see
[Cold vs warm/hot modes](#cold-vs-warmhot-modes). The three marginal-registration
rows report the GC-allocation cost of an additional same-type registration -- the
surface where the registration allocation work was reduced -- and are measurable only where the
profiler is present (an in-editor PlayMode/Mono run; the published Standalone IL2CPP
leg strips it, so its allocation columns are omitted):

Each marginal latency row settles the heap once, then reports the minimum of seven
independently prepared 1000-registration trials. A long retained-population window is
unsuitable because registration allocation forces collections into that clock.
On profiler-bearing Mono, allocation is measured over eight separate fresh populations
after timing, keeping the minimum count and its same-attempt bytes, so the profiler hook
is not part of latency and ambient editor spikes do not become the allocation headline.
The stripped IL2CPP leg skips those allocation-only populations and reports `n/a`; its
seven timing populations still execute. Repeated floor trials replace the former
sub-millisecond single shot without changing the reported per-bus cardinality.

| Scenario key                                                      | What it measures                                                       |
| ----------------------------------------------------------------- | ---------------------------------------------------------------------- |
| `EmptyBus_Dispatch`                                               | Dispatch with no registered handler.                                   |
| `UntargetedFlood_OneHandler`                                      | One untargeted handler on one message type.                            |
| `UntargetedFlood_OneDirectHandler`                                | One direct handler without the token-owned augmented callback.         |
| `UntargetedFlood_TwoHandlers_OnePriority`                         | Two untargeted handlers sharing priority 0.                            |
| `UntargetedFlood_ThreeHandlers_OnePriority`                       | Three untargeted handlers sharing priority 0.                          |
| `UntargetedFlood_FourHandlers_OnePriority`                        | Four untargeted handlers sharing priority 0.                           |
| `UntargetedFlood_FourHandlers_FourPriorities`                     | Four untargeted handlers across priorities 0-3.                        |
| `UntargetedFlood_SixteenHandlers_OnePriority`                     | Sixteen untargeted handlers sharing priority 0.                        |
| `UntargetedFlood_OneInactiveHandler`                              | One registered but inactive untargeted handler.                        |
| `UntargetedFirstDispatch_Cold`                                    | First untargeted dispatch per type, JIT-inclusive, median of 32 types. |
| `TargetedFlood_NoMatchingTarget`                                  | Targeted dispatch with no handler for the emitted target.              |
| `TargetedFlood_OneListener`                                       | One targeted listener on one target.                                   |
| `TargetedFlood_SixteenListeners`                                  | Sixteen targeted listeners on one target.                              |
| `TargetedFirstDispatch_Cold`                                      | First targeted dispatch per type, JIT-inclusive, median of 32 types.   |
| `BroadcastFlood_OneHandler`                                       | One broadcast handler.                                                 |
| `BroadcastFirstDispatch_Cold`                                     | First broadcast dispatch per type, JIT-inclusive, median of 32 types.  |
| `InterceptorHeavy_FourInterceptors`                               | Four interceptors plus one handler.                                    |
| `PostProcessingHeavy_FourPostProcessors`                          | Four post-processors plus one handler.                                 |
| `MessageBusConstruction_1000`                                     | Constructing 1000 isolated message buses.                              |
| `MessageRegistrationTokenConstruction_1000_PrebuiltHandlerAndBus` | Constructing 1000 tokens with prebuilt handlers and buses.             |
| `RegistrationFlood_1000Types_FromColdBus`                         | Registering 1000 distinct message types from a cold bus (cold flood).  |
| `RegistrationFlood_1000Types_WarmJit`                             | Registering the same 1000 types after a JIT pre-warm (warm-JIT flood). |
| `UntargetedRegistration_Marginal`                                 | Marginal cost of 1000 more untargeted handlers on one warm type.       |
| `TargetedRegistration_Marginal`                                   | Marginal cost of 1000 more targeted handlers on one warm type/target.  |
| `BroadcastRegistration_Marginal`                                  | Marginal cost of 1000 more broadcast handlers on one warm type/source. |
| `DeregistrationFlood_1000Types_Cold`                              | Tearing down 1000 live registrations, JIT-inclusive (cold flood).      |
| `DeregistrationFlood_1000Types_WarmJit`                           | Tearing down the same 1000 registrations after a JIT pre-warm.         |
| `RegistrationAttribution_DirectBus_131072`                        | Direct bus register/remove cycles for one reused handler.              |
| `RegistrationAttribution_DirectHandler_131072`                    | Handler plus bus register/remove cycles.                               |
| `RegistrationAttribution_TokenStage_131072`                       | Disabled-token stage/remove cycles.                                    |
| `RegistrationAttribution_TokenActive_131072`                      | Enabled-token register/remove cycles including lower layers.           |
| `DeregistrationAttribution_DirectBus_131072`                      | Direct built-in bus teardown for 131072 same-type registrations.       |
| `DeregistrationAttribution_DirectHandler_131072`                  | Handler-cache teardown including the direct bus layer.                 |
| `DeregistrationAttribution_TokenRemove_131072`                    | Per-handle token removal including handler and bus layers.             |
| `DeregistrationAttribution_TokenDisable_131072`                   | Token queue teardown including handler and bus layers.                 |

### Comparison scenarios (cross-library)

The nine apples-to-apples comparison scenarios are defined in
[`ComparisonScenario.cs`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/Tests/Runtime/Comparisons/ComparisonScenario.cs).
Each library implements only the scenarios it idiomatically supports; an
unsupported scenario renders `N/A` in the matrix and is **never faked**:

| #   | Scenario key          | What it measures                         |
| --- | --------------------- | ---------------------------------------- |
| S1  | `GlobalToOne`         | Global dispatch to one subscriber.       |
| S2  | `GlobalToMany`        | Global dispatch to 16 subscribers.       |
| S3  | `KeyedToOne`          | Keyed/targeted dispatch to 1 of many.    |
| S4  | `PriorityOrdered`     | Priority-ordered dispatch.               |
| S5  | `Filtered`            | Filtered/intercepted dispatch.           |
| S6  | `PostProcess`         | Post-processing dispatch.                |
| S7  | `FilteredPostProcess` | Intercepted and post-processed dispatch. |
| S8  | `SubUnsub`            | Subscribe/unsubscribe churn.             |
| S9  | `StructNoBox`         | Struct message dispatch (no boxing).     |

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

| Comparison cell       | DxMessaging shape                            | Nearest dispatch cell                         | True twin? | Why they differ                                                                                                                                       |
| --------------------- | -------------------------------------------- | --------------------------------------------- | ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GlobalToOne`         | 1 token, 1 untargeted handler                | `UntargetedFlood_OneHandler`                  | **Yes**    | Identical shape; the DxMessaging numbers must agree within noise.                                                                                     |
| `StructNoBox`         | 1 token, 1 untargeted handler                | `UntargetedFlood_OneHandler`                  | No         | Same storage shape, but the comparison uses the canonical `ComparisonStructPayload` while the dispatch row uses `SimpleUntargetedMessage`.            |
| `GlobalToMany`        | 16 tokens, 16 untargeted handlers            | (none)                                        | No         | No dispatch scenario fans untargeted dispatch out to 16 handlers (the dispatch family caps untargeted fan-out at four).                               |
| `KeyedToOne`          | 16 targets registered, dispatch to 1         | `TargetedFlood_OneListener`                   | No         | Measures lookup selectivity (16 registered, 1 fires); the dispatch cell registers a single target.                                                    |
| `PriorityOrdered`     | 1 token, 4 priorities                        | `UntargetedFlood_FourHandlers_FourPriorities` | No         | Comparison uses one MessageHandler with four handler-store entries; the dispatch cell uses four separate tokens. Same fan-out (4), different storage. |
| `Filtered`            | 1 interceptor + 1 handler                    | `InterceptorHeavy_FourInterceptors`           | No         | Comparison runs one interceptor; the dispatch cell runs four.                                                                                         |
| `PostProcess`         | 1 post-processor + 1 handler                 | `PostProcessingHeavy_FourPostProcessors`      | No         | Comparison runs one post-processor; the dispatch cell runs four.                                                                                      |
| `FilteredPostProcess` | 1 interceptor + 1 post-processor + 1 handler | (none)                                        | No         | No dispatch scenario combines hook kinds. Compare this cell inside the matrix against `GlobalToOne`, `Filtered`, and `PostProcess` from the same run. |
| `SubUnsub`            | register/unregister churn cycle              | (none)                                        | No         | The dispatch family has no subscribe/unsubscribe throughput scenario.                                                                                 |

**Fresh-state guarantee.** CI builds the comparison matrix into a dedicated player;
the internal benchmark player, including the 131072-cycle and teardown rows,
cannot leave heap state behind for it. Every matrix row then constructs and disposes
a fresh bridge. The harness does not force collections between rows; the dedicated player
provides the required clean process boundary without adding GC work to each case. The DxMessaging path uses a fresh
`new MessageBus()` per scenario,
MessagePipe resolves from a row-local provider instead of its static global provider,
and Unity object bridges destroy their row-local objects synchronously. The harness
also requires each bridge's declared fan-out to equal the scenario's canonical fan-out
before its `ProgressMarker` assertion reconciles the full measurement. This catches
current-row deduplication and fan-out mismatches. Teardown contracts separately verify
synchronous cleanup where the pinned API exposes observable Unity objects or assets.

### In-process paired stability evidence

The published comparison job builds and launches one Standalone IL2CPP Release
player. A dedicated fixture prepares DxMessaging and MessagePipe together for
each scenario that both libraries support. MessagePipe is the unchanged control
for host-wide movement when a DxMessaging candidate is compared with its control.

After preparing both bridges, the harness settles the heap once outside all timed
work, then warms both bridges. `BenchmarkProtocol.MeasurePaired` runs four cycles.
Each cycle repeats batch-level `ABBA/BAAB` super-cycles until both libraries have
at least 625 ms of measured active time. A batch contains 10000 operations. Both
libraries appear four times in each eight-batch super-cycle, and the unchanged
control stays milliseconds from the workload it normalizes. Each library receives
at least 2.5 seconds of measured active time per scenario without another player
launch; a slower library naturally receives more time while the faster side reaches
the same minimum.

The exact sequence is `ABBABAAB`: across its `ABBA` and `BAAB` halves, each
library occupies ordinal positions 1 through 4 once. The estimator removes only
movement that is common and approximately multiplicative for both bridges.
Workload-specific GC/cache effects or different frequency sensitivity are not
common-mode; they must remain visible in the raw-cycle or outer-run spread.

Each cycle ratio divides the two aggregate rates from that cycle. The headline is
the geometric combination of all four retained cycle ratios. The evidence also
keeps the simpler ratio of total rates as a diagnostic and reports:

```text
spread_percent = (maximum_cycle_ratio / minimum_cycle_ratio - 1) * 100
```

No cycle is discarded and no median is taken. A spread above the predeclared 3%
materiality band emits a warning and remains in the NUnit output. It does not
fail correctness or turn an unstable measurement into a regression. Each side
still has an exact fan-out assertion over warm-up plus every measured operation.
The paired rows use the `PairedComparison_` prefix, so they remain diagnostic and
cannot replace the canonical `Comparison_` rows in the published matrix.
`SubUnsub` stays on the canonical continuous window: its allocation and collection
work can spill into the other workload's next batch, which breaks paired isolation.
The paired fixture lives in the lexically last comparison assembly. The CI gate
reads the chronological player log and requires its first paired marker to follow
the last canonical marker, so sustained paired load cannot silently heat published rows.

Cases run scenario-major within each roster assembly. This keeps same-scenario cells
closer together inside the zero-dependency, external-package, and Unity Atoms rosters,
but assembly boundaries still separate the complete matrix. Use the paired
DxMessaging/MessagePipe ratios for candidate/control/candidate attribution when
absolute rates move together.

Predeclare the reduction before launching candidate/control/candidate (`C1/R/C2`).
Let each symbol be that run's paired DxMessaging/MessagePipe headline ratio:

```text
candidate_effect = sqrt(C1 * C2) / R - 1
outer_spread_percent = (maximum(C1, C2) / minimum(C1, C2) - 1) * 100
```

Retain all three summary files and every raw cycle ratio. Report a candidate
verdict only when `outer_spread_percent <= 3` and every run's raw cycle spread is
also at most 3%. Otherwise report the experiment as uninterpretable and do not
discard or replace a run. A performance candidate must additionally produce a
strictly greater than 3% effect in its intended direction and satisfy its
scenario-specific regression and allocation gates. For control/candidate/control,
invert the reduction: `candidate_effect = R / sqrt(C1 * C2) - 1`, where `R` is
the center candidate ratio and the outer values are controls.

Capability cells follow the pinned libraries' public APIs. MessagePipe uses predicate
subscription and pre/post filters, UniRx uses `Where`, Zenject uses signal identifiers,
and Unity Atoms uses a custom `AtomEvent<ComparisonStructPayload>` with replay disabled.
Unity `SendMessage` is an addressed GameObject API, so its keyless S1 and S2 cells are
`N/A`; its keyed cell measures the addressed operation it actually provides.

## How CI produces and publishes the numbers

The [Performance Numbers workflow](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/perf-numbers.yml)
(`.github/workflows/perf-numbers.yml`) runs on eligible same-repository pull
requests and on pushes to `master`. Fork and Dependabot pull requests skip the
licensed jobs; generated performance-doc pull requests do not trigger this
workflow because both generated paths are ignored. It runs two sequential published
matrix entries: one **Standalone IL2CPP Release player** for internal benchmarks and
one fresh **Standalone IL2CPP Release player** containing only real cross-library
comparison rows. The comparison job first runs bridge contracts in a disposable Mono
editor process; the IL2CPP player starts afterward with a separate managed heap. Both
published players have `BuildOptions.Development` stripped and use the Release IL2CPP
C++ configuration. Contract fixtures that open synthetic five-second windows are
excluded from both published categories.

After both published entries finish, `scripts/unity/render-perf-doc.js` reads the benchmark
rows and rewrites the AUTOGENERATED region of
`docs/architecture/performance.md`. The renderer derives the execution scope
from each row's platform string (`Standalone`, `PlayMode`, `EditMode`), emits
one dispatch-throughput table per scope present (in headline order: Standalone,
then PlayMode, then EditMode), and emits the cross-library comparison matrices.
Each per-scope dispatch table omits any profiler metric column it could not measure
(the Standalone Release table is throughput-only), so a column never degenerates to
all-`n/a`. The throughput comparison matrix uses the first scope present in headline
order (Standalone in published runs); a metric no present scope measured has its
whole matrix omitted rather than rendered all-`n/a`. These selectors are
intentionally independent because a backend can expose the byte counter while the
allocation-count recorder is unavailable, or vice versa. Each table's backend
label (Mono or IL2CPP) is derived from the platform string in that scope's rows,
so the heading follows the data. Scenario rows and library rows are joined on
stable machine keys (`DispatchBenchmarkScenarios.Key`, `ComparisonScenarios.Key`,
and each bridge's `TechKey`), never on display names.

The generated doc carries a privacy-safe provenance line describing the runner
HARDWARE -- CPU, physical/logical cores, clock, RAM size/speed/type, GPU, and OS
-- collected by
[`scripts/unity/collect-machine-specs.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/collect-machine-specs.ps1)
and embedded by `render-perf-doc.js --machine-specs`. The script deliberately
emits NO hostname or runner name; when the probe yields nothing the renderer
falls back to a neutral description.

For an eligible pull request, the reporting job checks out the trusted base
commit's renderer and baseline, verifies artifacts stamped with the exact head
SHA, and creates or updates one PR comment linked to that commit and workflow
attempt. The comment includes current Standalone TargetMap rows and a historical
Standalone delta only when the pull request did not change benchmark or harness
files. Delta percentages use outcome direction: `+` is better and `-` is worse,
for both higher-is-better throughput and lower-is-better wall clock. It re-checks the
live PR head before reporting so a superseded run cannot overwrite newer
evidence. A failed current-head run replaces the sticky comment's older success
with the failed status and run link. After the pull request merges, the push run
re-renders and, if the doc OR the baseline moved, commits both
`docs/architecture/performance.md` and the regenerated
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

Allocations are **only measurable where the profiler is available**. A
Release player strips the `GC.Alloc` recorder, so the Standalone leg cannot measure
allocations or bytes at all, and the renderer omits those memory columns entirely
(rather than filling them with `n/a`). `perf-numbers.yml` does not add a Mono leg
solely to recover those metrics. Use local MCP runs or manually dispatched
`unity-benchmarks.yml` editor scopes when allocation evidence is needed, and keep
the exact-zero `AllocationMatrixTests` contract green for dispatch changes.

EditMode runs inside the editor's hosting environment, is the least
representative of shipping behavior, and is not published; it -- and PlayMode --
remain useful for local iteration and for manually dispatched per-version
benchmark-test coverage in `unity-benchmarks.yml`.

## Baseline capture

### The committed master baseline

[`docs/architecture/perf-baseline.csv`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/docs/architecture/perf-baseline.csv)
is committed and is the baseline the diagnostic PR regression smoke and delta table
compare against. It ships as an honest header-only seed -- the column header with
no data rows -- so the first rollout has no fabricated numbers. Each push to the
default branch regenerates it with real Standalone IL2CPP rows from that run
(`extract-perf-baseline.js --replace --scope Standalone`) and commits it
alongside `performance.md`, so the seed becomes real after the first master push.
The baseline regeneration passes `--scope Standalone` so the committed baseline
stays **Standalone-only**. A missing or header-only baseline omits the
historical delta while current evidence still reports. The committed
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

| Commit reference              | Purpose                                     |
| ----------------------------- | ------------------------------------------- |
| Chosen comparison commit      | Historical reference for diagnostic deltas. |
| Previous optimization landing | Runtime after the last relevant change.     |
| `HEAD`                        | Current branch result.                      |

Required configuration cells:

| Configuration                 | Requirement                                                        |
| ----------------------------- | ------------------------------------------------------------------ |
| Standalone IL2CPP x64 Release | Required; the published headline and historical-comparison scope.  |
| PlayMode Mono                 | Optional; useful for local iteration and allocation investigation. |

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

## Pull-request performance evidence

After the perf leg runs, the PR job publishes current-head evidence before any
optimization is accepted. It calls
[`scripts/unity/render-perf-deltas.js`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/render-perf-deltas.js)
with `--scope Standalone`, which compares this PR's Standalone IL2CPP
DxMessaging numbers against the committed master baseline and prints two lines: `changed=true|false` (whether any metric
moved beyond `--tolerance`) and `regressed=true|false` (the catastrophic-smoke
signal). The job posts the DxMessaging-only delta before failing when
`regressed=true`, so reviewers always see the numbers.
The script always exits 0 itself; the workflow decides whether to fail from the
`regressed=` line.

A scenario trips that smoke signal when its throughput drops by more than the regression
threshold (default `0.33`, looser than the comment tolerance) OR its allocation
exceeds the baseline. Only canonical dispatch scenarios participate in the hard
smoke, so the wall-clock rows (the cold/warm-JIT registration and deregistration
floods and the cold first-dispatch scenarios, all zero throughput) never trip the
gate. The delta
comment is still broader diagnostic output: it keeps the dispatch scenarios plus
the DxMessaging comparison rows and drops every other library's rows. Comparison
rows are report-only because a single cross-library comparison sample is too
noisy for required CI. The workflow first requires the complete expected
Standalone scenario set and the exact measured commit stamp. A missing or
header-only baseline yields no historical comparison, while current Standalone
TargetMap evidence still reports.

The committed historical baseline is Standalone-only, where allocation values
are unmeasured, so the automatic historical smoke gates throughput only.
`AllocationMatrixTests` remains a separate exact-zero contract for focused local
and manually dispatched validation; the required PR jobs do not run its
`Allocation` category.

This historical comparison is not a causal A/B/A experiment and the workflow is
not a required branch-protection check. Treat green as evidence that the pinned
benchmarks completed, not as proof that an optimization is acceptable. Use
fresh, same-runner controls for a close result; do not compare across a benchmark
or harness change.

## Local-only C# smoke gate

`Tests/Editor/Benchmarks/PerfRegressionSmokeTests.cs` is now a LOCAL tool only;
it is `[Explicit, Category("PerfGate")]` and not part of automatic PR evidence. Use
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
