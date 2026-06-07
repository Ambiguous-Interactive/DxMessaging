---
title: "Benchmarks Run in the Highest-Fidelity Scope"
id: "benchmarks-run-in-highest-fidelity-scope"
category: "testing"
version: "1.0.0"
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
  reasoning: "Requires understanding that scope is just a test-DLL container and that the published scope must be the most player-faithful one present."

impact:
  performance:
    rating: "medium"
    details: "Publishing a less-faithful scope as the headline misrepresents shipping behavior."
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
  - "Player-fidelity scope"
  - "Standalone over PlayMode over EditMode"
  - "Scope-agnostic benchmarks"

related:
  - "benchmark-methodology-total-over-window"
  - "perf-config-mono-netstandard21-release"
  - "comparison-parity-and-package-single-source"

status: "stable"
---

# Benchmarks Run in the Highest-Fidelity Scope

> **One-line summary**: Benchmark code is scope-agnostic; EditMode, PlayMode,
> and Standalone are just test-DLL scopes. Publish the player-fidelity scopes
> (Standalone Mono over PlayMode Mono); do NOT publish EditMode as the headline.
> `scripts/unity/render-perf-doc.js` renders one labeled table per scope and
> prefers Standalone over PlayMode over EditMode.

## Overview

A benchmark scenario does not care which scope runs it. The same emit loop runs
in EditMode, in PlayMode, and in a built Standalone player; the scope is just
the container that loads the test DLL. What differs is fidelity to shipping
behavior, and that difference decides which scope becomes the headline number.

DxMessaging ranks scopes by player fidelity: a built Standalone player is
closest to shipping, in-editor PlayMode is next, and EditMode is least faithful.
The renderer prints one labeled table per scope present and draws the headline
numbers and the comparison matrices from the most player-faithful scope
available.

## Problem Statement

Two mistakes misrepresent performance:

- **Forking the benchmark per scope.** Writing a separate EditMode body and a
  separate PlayMode body lets the two drift and stops the numbers from being
  comparable. The body must be scope-agnostic.
- **Publishing EditMode as the headline.** EditMode runs under the editor's
  domain and compilation settings; its numbers do not represent a shipped
  player. Reporting EditMode as the headline overstates or understates real
  behavior.

## Solution

### One scope-agnostic body

The benchmark resolves its execution target at runtime and otherwise runs the
same code in every scope. The encoded target string is what the renderer reads
to label the table; the measurement logic is shared.

```csharp
string target = ResolveExecutionTarget();
// "Standalone ...", "Editor PlayMode ...", or "Editor EditMode ..."
BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(Warmup, EmitBatch);
WriteRow(target, scenario, measurement);
```

### Renderer prefers player fidelity

`scripts/unity/render-perf-doc.js` orders scopes by fidelity and uses the most
player-faithful scope present for the headline and the comparison matrices:

```javascript
const SCOPE_ORDER = ["Standalone", "PlayMode", "EditMode"];
```

The renderer emits one dispatch table per scope present, in that order, and the
cross-library matrices use the most player-faithful scope available. If only
EditMode ran, the renderer still labels it honestly rather than passing it off
as a player number.

## Scope Fidelity Ranking

| Scope      | Fidelity to shipping   | Published as headline?         |
| ---------- | ---------------------- | ------------------------------ |
| Standalone | Highest (built player) | Yes, when present              |
| PlayMode   | Middle (in-editor)     | Yes, when Standalone is absent |
| EditMode   | Lowest                 | No, never the headline         |

The Performance Numbers workflow (`.github/workflows/perf-numbers.yml`)
publishes the `playmode` and `standalone` legs for this reason; Standalone is
the player-fidelity scope and PlayMode is the in-editor fallback.

## Common Pitfalls

- "I will write a PlayMode-only benchmark." Keep the body scope-agnostic so the
  same scenario runs everywhere.
- "EditMode is fastest to run, so I will publish it." EditMode is the least
  faithful scope; never make it the headline.
- "I will hardcode the scope label in the row." Encode the resolved target so
  the renderer derives the scope and orders tables correctly.

## See Also

- [Benchmark Methodology: Total Over One Window](../performance/benchmark-methodology-total-over-window.md)
- [Perf Config: Mono, .NET Standard 2.1, Release](../performance/perf-config-mono-netstandard21-release.md)
- [Comparison Parity and Package Single Source](./comparison-parity-and-package-single-source.md)

## References

- Benchmark body: `Tests/Runtime/Benchmarks/DispatchThroughputBenchmarks.cs`
- Renderer: `scripts/unity/render-perf-doc.js`
- Workflow: `.github/workflows/perf-numbers.yml`
