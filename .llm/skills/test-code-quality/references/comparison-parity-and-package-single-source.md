# Comparison Parity and Package Single Source

> **One-line summary**: Every library is exercised through its idiomatic
> best-practice API per scenario, unsupported scenarios render `N/A` (never
> faked), a per-(tech,scenario) fan-out assertion guards against silent dedup,
> and the comparison registry, PINNED versions, and required Unity built-ins live ONLY in
> `.github/comparison-packages.json`; keep every mirror in sync by hand.

## Overview

Cross-library comparison benchmarks are only fair if each library is measured
the way its own authors would write it. DxMessaging holds two rules to keep the
table honest: parity in how bridges exercise each library, and a single source
for the comparison package registry, pins, and required Unity built-ins so the
asmdef and both manifests cannot drift apart.

The harness in `Tests/Runtime/Comparisons/ComparisonHarness.cs` runs each
bridge through the shared benchmark protocol, so a comparison cell and a
dispatch cell use the same MEASUREMENT (warm-up, window, GC delta) -- but they
deliberately measure different SHAPES, so their numbers are expected to differ
(see [Comparison vs dispatch topology](#comparison-vs-dispatch-topology)). The
package pins live in one JSON file that the runner, the committed local manifest,
and the drift validator all read.

## Problem Statement

Comparison tables rot in predictable ways:

- **Foreign adapters.** Wrapping every library in DxMessaging-shaped glue
  measures the glue, not the library. Each bridge must use the library's own
  best-practice API for the scenario.
- **Faked cells.** Filling an unsupported scenario with a stand-in or a copied
  number invents a capability the library does not have.
- **Silent dedup.** If two (tech, scenario) results collapse to one row, the
  table quietly drops coverage and no test notices.
- **Version / built-in drift.** A version pinned in the asmdef
  `versionDefines`, the ephemeral CI manifest, and the committed local manifest
  will diverge unless one file owns the value. The same is true for Unity
  built-in packages such as `com.unity.ugui` and `com.unity.modules.animation`
  that external comparison packages need to compile.

## Solution

### Idiomatic bridges and honest `N/A`

Every bridge implements `IMessagingTechBridge` and exercises only the scenarios
the library idiomatically supports. A scenario a library does not support is
reported as `N/A`, not filled with a substitute. The renderer prints `N/A` in
the matrix cell; it is a capability gap, never a failure and never faked.

### Payload fidelity for the struct scenario

The `StructMessageNoBoxing` scenario measures boxing-free struct dispatch, so a
bridge cannot claim it Supported while secretly raising a primitive (an `int`
through a fake event) or a boxed payload. `IMessagingTechBridge.DispatchedPayloadType(scenario)`
declares what the bridge actually dispatches, and the contract test
`StructScenarioDispatchesNonPrimitiveStructPayload`
(`ComparisonBridgeContract.AssertStructScenarioPayloadFidelity`) enforces it: a
bridge that does not support the scenario must return null, and every supporting
bridge must dispatch exactly the same non-primitive, non-enum
`ComparisonStructPayload`. The canonical payload implements DxMessaging's
`IUntargetedMessage<ComparisonStructPayload>` contract, so DxMessaging does not need
a cheaper substitute. Unity Atoms supports the scenario through a custom
`AtomEvent<ComparisonStructPayload>`; using its generated concrete-event pattern is
idiomatic and does not box the payload.

### Allocation honesty: measure idiomatic per-call cost, never hide it

The cross-library GC columns (the allocation-COUNT matrix and its companion
allocated-BYTES matrix) exist to surface each technology's REAL per-dispatch
allocation cost, so a bridge must dispatch its payload the way an idiomatic caller
would and must not cache an allocation that real usage pays on every call. The trap
is a reflection/`object`-based API: Unity `SendMessage(string, object)` has no
generic overload, so a value-type payload BOXES on every call. Caching one pre-boxed
`object` (`static readonly object Payload = 0;`) makes that bridge read 0
allocations / 0 bytes -- a number no real `SendMessage(value)` caller can achieve --
which misrepresents the technology. Pass the value so it boxes per call: cast to
`object` explicitly, and keep the backing field NON-`const` so a literal `0` does
not bind to the `SendMessage(string, SendMessageOptions)` overload and silently drop
the argument.

Verified on the host editor (Unity 6000.4, PlayMode): a pre-boxed payload reads
0/0; a per-call box reads exactly 1 allocation / ~20 bytes per dispatch; and
`SendMessage`'s reflection dispatch is otherwise allocation-free once warm. (EditMode
adds ~6 allocations/call of editor-only `SendMessage` instrumentation that the
shipped player never pays, which is why the PlayMode Mono leg is the honest
allocation scope.)

`ComparisonAllocationHonestyTests` is the red-green guard. It pins BOTH directions
with the real `AllocationProbe` over a batch (the MINIMUM across attempts rejects
warm-editor spikes, which only ADD): a forced-boxing bridge's dispatch floor is at
least one allocation per call (`SendMessage`), while a bridge that advertises
boxing-free struct dispatch floors UNDER one per call. The rule is "do not HIDE a
cost", not "must be zero": Zenject's `SignalBus` boxes through its internal `object`
routing and is measured honestly -- its non-zero count and bytes are its real cost,
not an artifact.

### Per-(tech, scenario) progress and completeness assertions

The harness checks each supported row while it runs. It compares the bridge's
`InvocationsPerOperation` with the canonical scenario fan-out (16 for
`GlobalToMany`, 4 for `PriorityOrdered`, and 1 otherwise), then verifies that the
row made progress and delivered the expected invocations. Fast bridge contracts
also pin the canonical fan-out and supported capability shape.

Those current-row checks cannot prove that the runner executed the complete
matrix. The performance workflow extracts the published comparison IDs and runs
`scripts/unity/require-comparison-rows.ps1`, which requires an exact match with
the 46-row capability manifest in `scripts/unity/perf-scenarios.js`. Missing,
extra, duplicate, and zero-row outputs all fail before reporting switches to
trusted base code.

### Comparison vs dispatch topology

The comparison matrix and the DxMessaging-only dispatch table answer different
questions, so a comparison cell and its dispatch look-alike usually register a
DIFFERENT topology and their DxMessaging numbers diverge -- often a lot.
`GlobalToOne` and `GlobalToMany` have exact dispatch topology twins
(`UntargetedFlood_OneHandler` and `UntargetedFlood_SixteenHandlers_OnePriority`).
Both harnesses create exactly one token per subscriber and disable bus and token diagnostics.
`StructNoBox` has the same one-token,
one-untargeted-handler storage shape but uses the canonical
`ComparisonStructPayload`, while the dispatch row uses `SimpleUntargetedMessage`.
The rest differ on purpose: `PriorityOrdered` uses one token with four priorities
where the dispatch twin uses four separate tokens; `KeyedToOne` registers 16 targets and
dispatches to one (selectivity), unlike the single-target dispatch cell. Do NOT "fix"
a divergence by forcing the shapes equal -- that would destroy what each scenario measures. The
relationship is a single source of truth pinned by
`ComparisonDispatchTopologyTests`, which checks the DxMessaging
fan-out, referenced dispatch keys, scenario roster, and the true twins' actual token
registrations, payloads, priorities, contexts, diagnostics, callback totals, and cleanup.
Topology equivalence does not establish timing equivalence across separate players or builds.
Keep the mapping synchronized with the
[methodology runbook table](../../../../docs/runbooks/perf-benchmark-methodology.md#comparison-vs-dispatch-deliberately-different-topologies).

### Fresh state and process isolation

The published workflow builds internal benchmarks and comparisons into separate
Standalone IL2CPP players. A high-cardinality internal diagnostic therefore cannot
leave heap state behind for the matrix. `ComparisonHarness.Run` builds a fresh bridge
per (tech, scenario) and disposes it with `using`. It does not force collections between
rows; the dedicated player provides the clean process boundary without adding GC work to
each case. MessagePipe resolves through its row-local service provider
instead of assigning `GlobalMessagePipe`; Unity object bridges destroy row-local
objects synchronously. The fan-out assertion catches current-row deduplication and
fan-out mismatches; teardown contracts cover observable object and event cleanup.

Comparison cases are scenario-major within each roster assembly, so related cells run
closer together inside that roster. Assembly boundaries still separate parts of the
full matrix. Use repeated runs with rotated technology order before claiming a close
cross-library ranking; one pass does not prove temporal parity.

### Zero-dependency baselines always compile

Plain C# event, `UnityEvent`, a ScriptableObject event channel, and Unity
`SendMessage` carry no external packages, so they compile unconditionally. The
table keeps reference points even when OpenUPM is unavailable; external bridges
are guarded behind their package defines and drop out cleanly when absent.

## The Package Single Source

`.github/comparison-packages.json` is the only place the OpenUPM scoped
registry, the PINNED comparison-benchmark versions, and the required Unity
built-in packages live. Bump a version or module THERE and nowhere else:

```json
{
  "packages": {
    "com.cysharp.messagepipe": "1.8.2",
    "com.neuecc.unirx": "7.1.0",
    "com.svermeulen.extenject": "9.2.0-stcf3"
  },
  "unityBuiltInPackages": {
    "com.unity.ugui": "1.0.0",
    "com.unity.modules.animation": "1.0.0"
  }
}
```

Three consumers read this file and must agree:

- `scripts/unity/run-ci-tests.ps1` injects the registry, external pins, and
  Unity built-ins into the ephemeral comparison manifest (comparison legs only,
  via `-IncludeComparisons`).
- The committed `.unity-test-project/Packages/manifest.json` and
  `.unity-test-project/Packages/packages-lock.json` mirror the pins and
  built-ins for local parity.
- The gated comparison asmdef expresses each package as a `versionDefines`
  entry so the bridge compiles only when its package is present. Each package
  define must be unique: sharing one define across multiple packages turns the
  asmdef gate into OR semantics, so an assembly that references both packages
  can compile when only one is installed. When one asmdef references multiple
  package assemblies, put every package-specific define in that same asmdef's
  `defineConstraints` so Unity must satisfy the full AND gate before compiling
  the assembly.

The single-source file is the authority. When editing it, manually update and
review every mirror in the same change: the asmdef `versionDefines`,
same-asmdef `defineConstraints`, the committed
`.unity-test-project/Packages/manifest.json`, and the committed
`packages-lock.json`. Keep package define mappings unique (one define per
package) and never constrain a define the asmdef does not produce locally.

## Common Pitfalls

- "I will fake the unsupported cell so the row is full." Render `N/A`; do not
  invent a capability.
- "My library has no built-in concrete struct event, so I will dispatch an int for
  the struct scenario." Check whether its documented generic/custom-event extension
  pattern supports the canonical payload first. Otherwise mark it unsupported
  (`N/A`); never raise a primitive for `StructMessageNoBoxing`.
  `DispatchedPayloadType` plus the
  `StructScenarioDispatchesNonPrimitiveStructPayload` contract test fail any
  bridge that claims the scenario while dispatching a primitive or boxed payload.
- "I will bump the pin in the manifest only." Bump
  `.github/comparison-packages.json` and update every mirror in the same change.
- "Two companion packages can share one `_PRESENT` symbol." Use one define per
  package and require every package-specific define in the consuming asmdef.
- "I will path-filter a workflow on only the source JSON." Include the
  manifest and package-lock mirrors in the trigger paths too.
- "I will route every library through a DxMessaging-style wrapper." Use each
  library's own best-practice API per scenario.
- "I will skip the fan-out assertion; the rows look complete." The assertion is
  what catches a silently dropped (tech, scenario) pair.

## See Also

- [Benchmark Methodology: Total Over One Window](../../benchmark-methodology/references/benchmark-methodology-total-over-window.md)
- [Benchmarks Run in the Highest-Fidelity Scope](../../benchmark-methodology/references/benchmarks-run-in-highest-fidelity-scope.md)
- [Data-Driven Tests with TestCaseSource](../../data-driven-tests/references/data-driven-tests.md)

## References

- Single source: `.github/comparison-packages.json`
- Harness: `Tests/Runtime/Comparisons/ComparisonHarness.cs`
- Payload-fidelity contract: `Tests/Runtime/Comparisons/ComparisonBridgeContract.cs`
- Baselines: `Tests/Runtime/Comparisons/ZeroDependencyComparisonTests.cs`
