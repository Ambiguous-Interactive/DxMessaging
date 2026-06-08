---
title: "Benchmarks Run in the Highest-Fidelity Scope"
id: "benchmarks-run-in-highest-fidelity-scope"
category: "testing"
version: "2.0.0"
created: "2026-06-07"
updated: "2026-06-07"

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
  reasoning: "Requires understanding that scope is just a test-DLL container and that the headline scope must match the shipped runtime backend."

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
  - "Shipped-runtime scope"
  - "PlayMode Mono headline, Standalone IL2CPP alongside"
  - "Scope-agnostic benchmarks"

related:
  - "benchmark-methodology-total-over-window"
  - "perf-config-mono-netstandard21-release"
  - "comparison-parity-and-package-single-source"

status: "stable"
---

# Benchmarks Run in the Highest-Fidelity Scope

> **One-line summary**: Benchmark code is scope-agnostic; EditMode, PlayMode,
> and Standalone are just test-DLL scopes. The headline is the shipped-runtime
> scope (PlayMode Mono); Standalone IL2CPP is published alongside for AOT
> coverage; do NOT publish EditMode as the headline.
> `scripts/unity/render-perf-doc.js` renders one labeled table per scope.

## Overview

A benchmark scenario does not care which scope runs it. The same emit loop runs
in EditMode, in PlayMode, and in a built Standalone player; the scope is just
the container that loads the test DLL. What differs is the runtime backend each
scope exercises, and that difference decides which scope becomes the headline
number.

DxMessaging publishes two scopes. PlayMode runs Mono, the backend the library
ships with for most targets, so it is the headline. Standalone runs IL2CPP and
provides ahead-of-time (AOT) coverage on the backend some platforms require, so
it is published alongside. EditMode runs under the editor's own hosting
environment and is never the headline. The renderer prints one labeled table per
scope present and draws the headline numbers and the comparison matrices from
the shipped-runtime scope (PlayMode when present).

## Problem Statement

Two mistakes misrepresent performance:

- **Forking the benchmark per scope.** Writing a separate EditMode body and a
  separate PlayMode body lets the two drift and stops the numbers from being
  comparable. The body must be scope-agnostic.
- **Publishing EditMode as the headline.** EditMode runs under the editor's
  domain and compilation settings; its numbers do not represent a shipped
  runtime. Reporting EditMode as the headline overstates or understates real
  behavior.

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

### Renderer headlines the shipped-runtime scope

`scripts/unity/render-perf-doc.js` orders scopes with PlayMode first and uses
the shipped-runtime scope present for the headline and the comparison matrices:

```javascript
const SCOPE_ORDER = ["PlayMode", "Standalone", "EditMode"];
```

The renderer emits one dispatch table per scope present, in that order, and the
cross-library matrices use the first scope present (PlayMode when available). The
section heading derives its backend label (Mono or IL2CPP) from the platform
string in each scope's rows, so the heading follows the data rather than a
hard-coded assumption. If only EditMode ran, the renderer still labels it
honestly rather than passing it off as a shipped-runtime number.

## Scope Fidelity Ranking

| Scope      | Backend / fidelity to shipping | Published as headline?          |
| ---------- | ------------------------------ | ------------------------------- |
| PlayMode   | Mono (shipped runtime)         | Yes, when present               |
| Standalone | IL2CPP / AOT (built player)    | No; published alongside for AOT |
| EditMode   | Mono, editor host (lowest)     | No, never the headline          |

The Performance Numbers workflow (`.github/workflows/perf-numbers.yml`)
publishes the `playmode` and `standalone` legs for this reason: PlayMode Mono is
the shipped-runtime headline and Standalone IL2CPP is the AOT leg.

## Common Pitfalls

- "I will write a PlayMode-only benchmark." Keep the body scope-agnostic so the
  same scenario runs everywhere, including the Standalone IL2CPP leg.
- "EditMode is fastest to run, so I will publish it." EditMode runs in the
  editor host; never make it the headline.
- "Standalone is a built player, so it must be the headline." Standalone runs
  IL2CPP, not the Mono backend the library ships with for most targets; it is the
  AOT companion leg, and PlayMode Mono is the headline.
- "I will hardcode the scope label in the row." Encode the resolved target so
  the renderer derives the scope and backend and orders tables correctly.

## See Also

- [Benchmark Methodology: Total Over One Window](../performance/benchmark-methodology-total-over-window.md)
- [Perf Config: Mono, .NET Standard 2.1, Release](../performance/perf-config-mono-netstandard21-release.md)
- [Comparison Parity and Package Single Source](./comparison-parity-and-package-single-source.md)

## References

- Benchmark body: `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs`
- Renderer: `scripts/unity/render-perf-doc.js`
- Workflow: `.github/workflows/perf-numbers.yml`
