---
title: "Runtime Performance Campaign Decisions"
id: "runtime-performance-campaign-decisions"
category: "performance"
version: "1.2.0"
created: "2026-07-11"
updated: "2026-07-11"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Runtime/Core/MessageBus/MessageBus.cs"
    - path: "Runtime/Core/MessageHandler.cs"
    - path: "Runtime/Core/Internal/TypedSlots.cs"
    - path: "Runtime/Core/MessageRegistrationToken.cs"
    - path: "Tests/Runtime/Benchmarks/HandlerCardinalityBenchmarks.cs"
    - path: "Tests/Runtime/Benchmarks/RegistrationLifecycleBenchmarks.cs"
    - path: "Tests/Runtime/Benchmarks/TargetMapBenchmarks.cs"
    - path: "Tests/Runtime/Benchmarks/SnapshotRebuildBenchmarks.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "performance"
  - "benchmarks"
  - "rejected-experiments"
  - "mono"
  - "il2cpp"

complexity:
  level: "advanced"
  reasoning: "Interpreting these decisions requires cardinality, allocation, retained-storage, Mono, and IL2CPP evidence together."

impact:
  performance:
    rating: "critical"
    details: "Prevents repeating candidates that already failed timing, allocation, retained-storage, or lifecycle gates."
  maintainability:
    rating: "high"
    details: "Keeps campaign conclusions and their exact failure modes in one durable location."
  testability:
    rating: "high"
    details: "Links each conclusion to committed benchmark and contract fixtures."

prerequisites:
  - "dispatch-hot-path"
  - "benchmark-methodology-total-over-window"
  - "mono-vs-il2cpp-optimization-split"

dependencies:
  packages: []
  skills:
    - "dispatch-hot-path"
    - "mono-vs-il2cpp-optimization-split"

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
  - "runtime perf rejected experiments"
  - "small-container campaign results"
  - "dispatch optimization decisions"

related:
  - "dispatch-hot-path"
  - "benchmark-methodology-total-over-window"
  - "memory-reclamation"
  - "mono-vs-il2cpp-optimization-split"

status: "stable"
---

# Runtime Performance Campaign Decisions

> **One-line summary**: Keep the measured physical-two handler-entry map;
> the other small-container, dispatch-switch, and private-pool candidates
> failed explicit timing or storage gates and were reverted.

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

## Rejected runtime candidates

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

- [DxMessaging Dispatch Hot Path](./dispatch-hot-path.md)
- [Benchmark Methodology](./benchmark-methodology-total-over-window.md)
- [Mono vs IL2CPP Optimization Split](./mono-vs-il2cpp-optimization-split.md)
