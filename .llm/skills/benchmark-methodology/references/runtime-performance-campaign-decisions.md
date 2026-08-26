# Runtime Performance Campaign Decisions

<!-- cspell:ignore gshared -->

> **One-line summary**: Keep the measured physical-two handler-entry map,
> holder-local flat-dispatch count read, same-trial deregistration diagnostic,
> and controlled one-process comparison profile. Reject performance attribution
> when causally untouched sentinels move with the target.

## Decision rule

Do not repeat an implemented candidate below without new evidence or a
materially different representation. Implemented candidates were compiled at
the Unity/C# 9 floor, checked against committed contracts, and reverted when
they failed a campaign gate. Observations that were not implemented are labeled
separately.

## Accepted candidate

- Keep the physical-two `HandlerActionCache` entry map. Fresh Mono construction
  improved from 2.129M to 3.541M caches/sec (+66.3%) by removing two eager
  collection allocations. The four-handler decision row used fresh A/B/A
  bracketing; adjacent fresh controls put 1/2/4/16-handler dispatch between
  -0.57% and +0.57%. Repeated churn controls found no representative regression
  over 3%.
- Physical capacities four and eight were rejected. Four regressed four-entry
  dispatch by 3.44% and churn by up to 16.1%. Eight increased fresh-construction
  allocated bytes from 248,000 to 422,396 per 1,000 caches (+70.3%).
- Keep reading the non-global dispatch count from the already-cast flat holder.
  The first final-head screen read the copied `DispatchSnapshot.entryCount` and
  found `Comparison_DxMessaging_KeyedToOne` down 4.02% at 4/5 IL2CPP observations.
  A two-line, semantics-equivalent ablation restored the five-run keyed result to
  +1.56% IL2CPP and +1.88% Mono versus control, both below the 3% claim threshold;
  versus the prior candidate's IL2CPP median, it improved 3.65%. All other
  representative DxMessaging dispatch rows cleared the regression gate. Keep
  `DispatchSnapshot.entryCount` as lifecycle/topology telemetry, but do not route
  the hot loop through the extra owner.

## Accepted measurement method

- Keep the controlled one-process comparison profile from PR #467. The
  Standalone IL2CPP Release player uses the highest Windows CPU-set
  `EfficiencyClass`, affinity `0xFFFF`, Normal priority, four retained
  `ABBABAAB` cycles, and at least 625 ms of active time per workload per cycle.
  All seven paired raw-cycle spreads passed the fixed 3% limit on the accepted
  host. This proves within-process stability only. A candidate/control/candidate
  verdict must also commit the complete target/affected/sentinel manifest before
  the first run and keep it unchanged through the bracket. The workflow embeds
  its digest in every summary. Only `reduce-paired-bracket.js` produces the
  verdict from all three summaries and every declared row. See the developer
  runbook for the schema and gates.
- Keep the joint deregistration palindrome. Its predecessor minimized four
  independent seven-trial windows, so the reported H/B/B/H arms could come from
  four different host phases. The corrected diagnostic prepares four fresh
  populations per trial, measures them back-to-back, balances four forward and
  four reverse preparation opportunities, and selects one complete trial. Its
  marker retains every trial's direction and total so the floor is independently
  auditable. The loaded Mono assembly passed 48
  contract cases, including whole-sample selection and an enforced H/B/B/H
  execution-order contract. Four
  fresh-population invocations in one already-loaded Mono editor qualified only
  two samples under the predeclared 3% gates; the two rejected samples measured
  3.70% handler drift with 5.13% handler-excess spread and 3.03% bus drift. A
  separate warmed raw distribution qualified all seven trials. The method stops
  mixing arms from different host phases, but that intermittent invocation result
  does not authorize the exact-`MessageBus` candidate. The final balanced
  eight-trial marker independently rejected itself at 3.79% handler-excess spread.
  Require repeated
  interpretable Standalone IL2CPP Release brackets before making that runtime
  edit. Four simultaneous populations deliberately increase diagnostic-only peak
  memory: the existing 131072 cardinality keeps the shortest direct-bus arm near
  10 ms, and preparing all arms first avoids allocation-heavy setup between their
  timestamps.

## Rejected runtime candidates

- Do not move the warmed untargeted cached-route check ahead of
  `fastHandlers.handlers.Count` in `UntargetedBroadcast<TMessage>`. Native
  IL2CPP output proved that the dictionary-count call moved behind the cache-miss
  branch, and both candidate arms were byte-identical and stable. The immutable
  candidate/control/candidate reducer still rejected the result as
  uninterpretable. Unreachable `Filtered`, `PostProcess`, and
  `FilteredPostProcess` sentinels moved +24.863%, +19.225%, and +14.210% against
  the center control. Those shifts exceed the fixed 3% bound. The diagnostic
  normalized effects of +26.820% for `GlobalToOne` and -3.480% for
  `StructNoBox` are not acceptance evidence. The candidate was reverted.
  Revisit only with a materially different representation and a fresh immutable
  bracket.
- Do not retain `AggressiveInlining` on `InterceptorCache<T>.EnsureFlat` from
  PR #468. The native artifacts proved the intended call disappeared, and every
  raw-cycle and outer-candidate spread passed 3%, but the center control was not
  representative across builds. `GlobalToOne` and `StructNoBox` cannot call
  `EnsureFlat`; they nevertheless reported +7.285% and +7.341%, matching the
  `Filtered` target's +7.334%. Both sentinel effects exceed the fixed 3% bound,
  so the bracket is uninterpretable. All five unreachable paired rows produce a
  diagnostic normalized `Filtered` effect of +4.754%, but no target effect from
  this failed bracket is acceptance evidence. The attribute and its user-facing
  performance claim were reverted.
- Caller-side duplication of the settled
  `AcquireDispatchSnapshotFast<TMessage>` branch improved `PostProcess` by only
  1.19% in a fresh Standalone candidate/control/candidate bracket. Keep the
  shared helper until a materially different representation removes more work.
- Inlining all of `RunUntargetedInterceptors<T>` removed the intended native
  call and improved the four-interceptor internal row by 19.15%, but the old
  cross-process comparison bracket moved unrelated rows by 4% to 27%. The
  candidate was reverted. Do not retry it until the causal-sentinel protocol
  first produces an interpretable bracket for the smaller `EnsureFlat` change.
- Do not bundle `RunTargetedPostPhases<TMessage>`'s immutable arguments into a
  readonly struct passed by `in`. Fresh Standalone IL2CPP Release A/B/B/A means
  regressed targeted stable, rewritten-empty-final, and
  rewritten-populated-final throughput by 32.21%, 28.98%, and 16.22%. The
  broadcast sibling-control means moved only +0.43%, -1.68%, and +2.12%.
  Candidate player size measurements and hashed code/metadata payloads were
  identical;
  separate editor rewritten-route allocation contracts remained green;
  shippable size grew 3,308 bytes (+0.0039%). Generated C++ constructed
  `TargetedPostPhaseState`, passed its address to the `_gshared_inline` helper,
  and retained field-accessor calls, while the control helper received the loose
  snapshots and scalars directly. Keep the loose parameters. Revisit only with
  a materially different representation or an IL2CPP backend change that can be
  proven to scalarize the state.
- Removing the steady-state `handlers.lastTouchTicks` store from
  `AcquireDispatchSnapshotFast` was semantically safe but had no material Mono
  throughput effect. Freshly compiled A/B/A `Comparison_DxMessaging_GlobalToOne`
  samples were 24.786M / 24.844M / 25.208M emits/sec: the candidate was 1.4%
  below the second control and 0.6% below the control mean, far under the 3%
  claim threshold. The first stale
  control falsely suggested a 5.7x gain because only the candidate edit forced a
  fresh Release assembly; always change and recompile both sides of a local
  Unity A/B. Keep the touch unless new IL2CPP evidence shows a material backend
  difference.
- A 0-4 flat-dispatch `switch` preserved live-active and reset-generation reads
  but regressed representative dispatch by roughly 8-11% versus the compact loop.
- `[ThreadStatic]` snapshot-holder stacks changed a process-wide 64-holder ceiling
  into 64 holders plus a stack per participating thread. They failed the
  no-retained-memory-increase gate before a timing claim.
- The 256+ scalar open-addressed `InstanceId` map improved its five-run 256-key
  hit median by only 2.83%, below the 3% threshold. It also added a wrapper
  allocation/retained object to every dominant small map, lacked comparable
  retained-byte telemetry, could allocate or throw during remove-time cleanup,
  grew from deleted rather than live load, incompletely cleared managed-reference
  keys, and had non-transactional migration/version behavior.
- Stop the nested open-addressing experiments at that failed parent candidate.
  The 4,096-key candidate row and byte-per-slot-control versus bit-packed-control
  variants were not reached. Metadata packing cannot repair the independent
  wrapper-allocation, managed-key-clearing, transactional-migration, or
  remove-time correctness failures, so timing a second encoding would not alter
  the retention decision. Revisit those variants only after a materially
  different parent design passes the correctness, allocation, and storage gates.
- Physical 2/4/8 inline bus context maps all failed spill storage. Capacity two's
  one-key construction used 128 allocated bytes and 2 physical slots, but four
  keys used 680 bytes/9 slots versus Dictionary's 600/7. Capacities four and eight
  produced 21 and 25 physical slots at sixteen keys versus Dictionary's 17.
- The first ordered-priority prototype used a separate map class and added an
  owner allocation on spill. The corrected mutable-struct designs were embedded
  in both owners and audited for copy-safe mutation. Physical 2/4/8 then retained
  Dictionary/List spill storage in addition to 32/56/104 bytes of inline owner
  state. Exact backing-capacity equality was observed for capacity two; larger
  requested spill capacities can be no smaller and may round higher. Capacity two
  passed the full 57-case cardinality contract sweep, so correctness was not the
  rejection reason.
- Do not recombine typed and interceptor teardown into one enlarged registration
  layout. The co-located draft grew the 1,000-registration allocated-byte rows by
  roughly 14%; splitting the typed teardown state restored parity while retaining
  the common-case allocation win.

## Rejected measurement methods

- Do not accept a cross-build target effect merely because every raw cycle and
  both outer candidate arms are stable. PR #468's one anomalous control moved
  two impossible-to-affect routes by the same roughly 7.3% as the target. The
  old gate rejected only non-target regressions, so correlated positive movement
  passed. Require bounded causal sentinels and a target-specific normalized
  effect for every future bracket.
- Do not treat PR #437's cross-build comparison against the historical PR #434
  baseline as authoritative route-cache attribution. Unrelated rows moved from
  -11.22% to +19.41%, and no fresh Standalone three-run bracket existed. Keep
  the tested route-cache mechanism, but describe it factually and do not claim
  a measured throughput improvement until the immutable-manifest reducer accepts
  a fresh bracket.
- The original marginal-registration rows timed one sub-millisecond
  1000-registration pass while the Mono allocation recorder was active. Five
  candidate launches compared against that single historical master row are not a
  valid five-run A/B verdict. A 16-population continuous-window prototype was also
  rejected: its roughly 10 MB of live registration allocation forced collections
  into the clock and raised Mono samples from roughly 1 ms to 2.6-4.1 ms. The
  retained harness settles once, uses seven fresh floor trials, measures the Mono
  allocation floor separately over eight fresh populations (stripped IL2CPP skips
  that allocation-only pass and reports unmeasured), and requires fresh
  control/candidate runs before accepting or rejecting a runtime change.
- Do not measure whole-fixture construction when the hypothesis targets one
  storage owner. Global pool state produced non-monotonic handler samples of
  190/1,276/349 allocations at 1/2/3 entries. A separate fresh end-to-end
  registration measurement also remained pool-contaminated at 102/714/631;
  neither method isolates one storage owner.
- The first integrated target-map draft similarly reported 273 then 199
  allocations at 1/4 keys. The retained benchmark calls the exact production
  fresh-map creator, prebuilds keys and values outside the window, and observes
  one map's allocations, bytes, and topology from the same selected attempt.

## Backend and first-touch observations

- The final exact-head campaign used five verified 408-test/70-row artifacts per
  backend and side. Marginal registration latency improved 20.4-27.6% in the
  published IL2CPP Release player and 9.7-12.5% under Mono. Every Mono marginal
  row removed exactly 1,055 allocation calls and 486,800 allocated bytes. The
  16-byte registration handle was unchanged. The final player measured
  1,364,362,758 bytes versus 1,360,906,232 bytes for control: +3,456,526 bytes
  (+0.254%), below the 1% gate.
- Do not interpret `MessageBusConstruction_1000` or cold 1,000-type teardown as
  dispatch-throughput regressions. They are short wall-time/first-touch rows, not
  fixed-window throughput rows, and shifted by more than 5% between two candidate
  heads whose only code difference was two reads inside a dispatch method that
  neither row calls. Preserve and report the raw values, but require a freshly
  bracketed, causally relevant experiment before using those rows to reject an
  unrelated dispatch-only change.

- Inspect the loaded Mono assembly rather than a possibly stale generated-project
  DLL. The campaign's loaded `DispatchFlatSnapshot<T>` compiled to 113 IL bytes,
  six locals, and no exception regions. Its instructions form one indexed entry
  loop with a live `MessageHandler.active` read, direct
  `FastHandler<T>.Invoke(ref T)`, post-call reset-generation comparison, and
  `HasAnyDispatchEntries` fallback. The context sibling was 114 bytes with the
  same six-local/no-exception shape. This supports retaining the compact loop;
  it does not justify further generic specialization or source generation.
- Leave `RegistrationMethodAxes`' one-time `Enum.GetValues` initialization alone
  until a first-touch benchmark attributes material cost to it. It is outside
  steady-state dispatch, and existing coverage already pins exhaustiveness.
- The published perf artifacts retain results and player logs, not generated
  IL2CPP C++. Three retained player artifacts and the active editor project's
  Bee cache contained zero generated C++ candidates. Do not claim source-level
  C++ inspection from these artifacts; arrange deliberate generated-source
  capture before a future code-generation experiment.
- No CPU-sampling or Intel Top-Down capture was available: the retained artifacts
  contain no CPU profile, the workflows collect none, and VTune/perf tooling was
  absent from the audit environment. Outcome-based timing, allocation, storage,
  and player-size gates remain valid, but do not attribute a result to branch,
  code-size, cache, or memory stalls without a matched profile. Provision capture
  on the measured runner before a future experiment that needs such attribution.

## See also

- [DxMessaging Dispatch Hot Path](../../dispatch-hot-path/references/dispatch-hot-path.md)
- [Benchmark Methodology](./benchmark-methodology-total-over-window.md)
- [Mono vs IL2CPP Optimization Split](../../il2cpp-build-configuration/references/mono-vs-il2cpp-optimization-split.md)
