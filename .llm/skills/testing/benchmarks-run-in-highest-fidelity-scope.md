---
title: "Benchmarks Run in the Highest-Fidelity Scope"
id: "benchmarks-run-in-highest-fidelity-scope"
category: "testing"
version: "3.0.0"
created: "2026-06-07"
updated: "2026-06-12"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs"
    - path: "scripts/unity/render-perf-doc.js"
    - path: ".github/workflows/perf-numbers.yml"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "testing"
  - "benchmarks"
  - "scope"
  - "standalone"
  - "playmode"
  - "fidelity"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding that scope is just a test-DLL container and that the published scope must be the highest shipping-fidelity build: a Release IL2CPP player."

impact:
  performance:
    rating: "medium"
    details: "Publishing the wrong scope as the headline misrepresents shipping behavior."
  maintainability:
    rating: "high"
    details: "Scope-agnostic benchmark code runs in any scope without per-scope forks."
  testability:
    rating: "high"
    details: "One benchmark body is reused across EditMode, PlayMode, and Standalone scopes."

prerequisites:
  - "Familiarity with Unity EditMode, PlayMode, and Standalone test scopes"
  - "Awareness of the benchmark measurement protocol"

dependencies:
  packages: []
  skills:
    - "benchmark-methodology-total-over-window"

applies_to:
  languages:
    - "C#"
    - "JavaScript"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Shipping-fidelity scope"
  - "Standalone IL2CPP Release headline"
  - "Scope-agnostic benchmarks"

related:
  - "benchmark-methodology-total-over-window"
  - "perf-config-il2cpp-release-netstandard21"
  - "comparison-parity-and-package-single-source"

status: "stable"
---

# Benchmarks Run in the Highest-Fidelity Scope

> **One-line summary**: Benchmark code is scope-agnostic; EditMode, PlayMode,
> and Standalone are just test-DLL scopes. The headline and only published
> scope is Standalone IL2CPP in a true Release player, the highest shipping
> fidelity. PlayMode and EditMode are iteration scopes and are NOT published;
> EditMode must never be treated as representative.
> `scripts/unity/render-perf-doc.js` renders one labeled table per scope.

## Overview

A benchmark scenario does not care which scope runs it. The same emit loop runs
in EditMode, in PlayMode, and in a built Standalone player; the scope is just
the container that loads the test DLL. What differs is how closely each scope
matches a shipped game build, and that difference decides which scope is
published.

DxMessaging publishes one scope. Standalone runs the benchmark inside a real
IL2CPP Release player (`BuildOptions.Development` stripped, Release IL2CPP C++
configuration), which is what shipped titles actually execute, so it is the
headline and the only published scope. PlayMode runs in-editor under Mono and
is the fast scope for local and CI iteration; EditMode runs under the editor's
own hosting environment and is the least representative. Neither in-editor
scope is published. The renderer prints one labeled table per scope present
and draws the headline numbers and the comparison matrices from the first
scope present in headline order (Standalone when available).

## Problem Statement

Two mistakes misrepresent performance:

- **Forking the benchmark per scope.** Writing a separate EditMode body and a
  separate PlayMode body lets the two drift and stops the numbers from being
  comparable. The body must be scope-agnostic.
- **Publishing an in-editor scope as the headline.** PlayMode and EditMode run
  inside the editor's domain and compilation settings; their numbers do not
  represent a shipped Release player. Reporting either as the headline
  overstates or understates real behavior.

## Solution

### One scope-agnostic body

The benchmark resolves its execution target at runtime and otherwise runs the
same code in every scope. The encoded target string is what the renderer reads
to label the table; the measurement logic is shared.

```csharp
string target = ResolveExecutionTarget();
// "Standalone IL2CPP ...", "Editor PlayMode Mono ...", or "Editor EditMode ..."
BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(Warmup, EmitBatch);
WriteRow(target, scenario, measurement);
```

### Renderer headlines the shipping-fidelity scope

`scripts/unity/render-perf-doc.js` orders scopes with Standalone first and
uses the first scope present in headline order for the headline and the
comparison matrices:

```javascript
const SCOPE_ORDER = ["Standalone", "PlayMode", "EditMode"];
```

The renderer emits one dispatch table per scope present, in that order, and
the cross-library matrices use the first scope present (Standalone when
available). The section heading derives its backend label (Mono or IL2CPP)
from the platform string in each scope's rows, so the heading follows the data
rather than a hard-coded assumption. If only an in-editor scope ran, the
renderer still labels it honestly rather than passing it off as a Release
player number.

## Scope Fidelity Ranking

| Scope      | Build / fidelity to shipping    | Published?                       |
| ---------- | ------------------------------- | -------------------------------- |
| Standalone | IL2CPP Release player (highest) | Yes; headline and only published |
| PlayMode   | Mono, in-editor (iteration)     | No; local/CI iteration scope     |
| EditMode   | Mono, editor host (lowest)      | No; never representative         |

The Performance Numbers workflow (`.github/workflows/perf-numbers.yml`)
publishes only the `standalone` leg for this reason: a Release IL2CPP player
is the highest shipping fidelity. The weekly `unity-benchmarks.yml` still runs
the EditMode and PlayMode benchmark tests across Unity versions, as coverage
rather than published numbers.

## Common Pitfalls

- "I will write a PlayMode-only benchmark." Keep the body scope-agnostic so the
  same scenario runs everywhere, including the published Standalone IL2CPP leg.
- "Publishing an in-editor scope as the headline." PlayMode and EditMode are
  iteration scopes; the published headline is the Standalone IL2CPP Release
  player.
- "Benchmarking a development-build player." The Unity Test Framework injects
  `BuildOptions.Development` by default; `Debug.isDebugBuild` must be false in
  published runs, and a published `x64 Debug` platform string is a
  configuration bug.
- "EditMode is fastest to run, so it is good enough to report." EditMode runs
  in the editor host; never treat it as representative.
- "I will hardcode the scope label in the row." Encode the resolved target so
  the renderer derives the scope and backend and orders tables correctly.

## See Also

- [Benchmark Methodology: Total Over One Window](../performance/benchmark-methodology-total-over-window.md)
- [Perf Config: IL2CPP Release, .NET Standard 2.1](../performance/perf-config-il2cpp-release-netstandard21.md)
- [Comparison Parity and Package Single Source](./comparison-parity-and-package-single-source.md)

## References

- Benchmark body: `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs`
- Renderer: `scripts/unity/render-perf-doc.js`
- Workflow: `.github/workflows/perf-numbers.yml`
