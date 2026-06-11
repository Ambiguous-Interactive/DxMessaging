---
title: "Comparison Parity and Package Single Source"
id: "comparison-parity-and-package-single-source"
category: "testing"
version: "1.2.0"
created: "2026-06-07"
updated: "2026-06-08"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".github/comparison-packages.json"
    - path: ".unity-test-project/Packages/manifest.json"
    - path: ".unity-test-project/Packages/packages-lock.json"
    - path: "Tests/Runtime/Comparisons/ComparisonHarness.cs"
    - path: "Tests/Runtime/Comparisons/IMessagingTechBridge.cs"
    - path: "Tests/Runtime/Comparisons/ComparisonBridgeContract.cs"
    - path: "Tests/Runtime/Comparisons/ZeroDependencyComparisonTests.cs"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "benchmarks"
  - "comparison"
  - "single-source"
  - "parity"
  - "drift"

complexity:
  level: "advanced"
  reasoning: "Spans apples-to-apples bridge design, N/A semantics, fan-out dedup guards, and a multi-consumer package/built-in single-source contract."

impact:
  performance:
    rating: "medium"
    details: "Idiomatic best-practice bridges keep each library's number representative rather than penalized by a foreign adapter."
  maintainability:
    rating: "high"
    details: "One pinned registry file removes version and Unity built-in dependency drift across the asmdef and two manifests."
  testability:
    rating: "high"
    details: "A per-(tech,scenario) fan-out assertion plus a drift validator pin both parity and pins."

prerequisites:
  - "Familiarity with the comparison harness and its bridges"
  - "Awareness of Unity versionDefines and OpenUPM scoped registries"

dependencies:
  packages: []
  skills:
    - "benchmark-methodology-total-over-window"

applies_to:
  languages:
    - "C#"
    - "JSON"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Comparison apples-to-apples"
  - "Comparison package pinning"
  - "N/A not faked"

related:
  - "benchmark-methodology-total-over-window"
  - "benchmarks-run-in-highest-fidelity-scope"
  - "data-driven-tests"

status: "stable"
---

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
dispatch cell are measured identically. The package pins live in one JSON file
that the runner, the committed local manifest, and the drift validator all read.

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

The `StructMessageZeroCopy` scenario measures boxing-free struct dispatch, so a
bridge cannot claim it Supported while secretly raising a primitive (an `int`
through a fake event) or a boxed payload. `IMessagingTechBridge.DispatchedPayloadType(scenario)`
declares what the bridge actually dispatches, and the contract test
`StructScenarioDispatchesNonPrimitiveStructPayload`
(`ComparisonBridgeContract.AssertStructScenarioPayloadFidelity`) enforces it: a
bridge that does not support the scenario must return null, a bridge that does
must return a non-primitive, non-enum value type, and every non-DxMessaging
bridge must dispatch exactly `ComparisonStructPayload`. Unity Atoms has no
idiomatic boxing-free struct event, so it marks the struct scenario unsupported
(`N/A`) rather than dispatching an `int`.

### Per-(tech, scenario) fan-out assertion

The harness asserts one result per (tech, scenario) pair so a dedup or a
missing bridge registration fails the suite instead of silently shrinking the
table.

```csharp
foreach (IMessagingTechBridge bridge in bridges)
{
    foreach (ComparisonScenario scenario in ComparisonScenarios.All)
    {
        if (!bridge.Supports(scenario))
        {
            continue;
        }
        ComparisonResult result = harness.Run(bridge, scenario);
        Assert.IsNotNull(
            result,
            $"Missing result for ({bridge.TechName}, {scenario}).");
    }
}
```

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
    "com.cysharp.messagepipe": "1.8.1",
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
- "My library has no struct event, so I will dispatch an int for the struct
  scenario." Mark it unsupported (`N/A`); never raise a primitive for
  `StructMessageZeroCopy`. `DispatchedPayloadType` plus the
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

- [Benchmark Methodology: Total Over One Window](../performance/benchmark-methodology-total-over-window.md)
- [Benchmarks Run in the Highest-Fidelity Scope](./benchmarks-run-in-highest-fidelity-scope.md)
- [Data-Driven Tests with TestCaseSource](./data-driven-tests.md)

## References

- Single source: `.github/comparison-packages.json`
- Harness: `Tests/Runtime/Comparisons/ComparisonHarness.cs`
- Payload-fidelity contract: `Tests/Runtime/Comparisons/ComparisonBridgeContract.cs`
- Baselines: `Tests/Runtime/Comparisons/ZeroDependencyComparisonTests.cs`
