---
title: "Perf Config: Mono, .NET Standard 2.1, Release"
id: "perf-config-mono-netstandard21-release"
category: "performance"
version: "1.0.0"
created: "2026-06-07"
updated: "2026-06-07"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/unity/run-ci-tests.ps1"
    - path: ".github/workflows/perf-numbers.yml"
    - path: "docs/runbooks/perf-benchmark-methodology.md"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "performance"
  - "benchmarks"
  - "mono"
  - "release"
  - "ci"
  - "standalone"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding the per-leg build matrix and why every Unity test/build leg now runs Release while perf standalone still pins Mono2x."

impact:
  performance:
    rating: "high"
    details: "Build profile changes the absolute numbers; an unstated profile makes published throughput unreproducible."
  maintainability:
    rating: "high"
    details: "One runner script owns the profile flags; legs differ only by documented parameters."
  testability:
    rating: "medium"
    details: "Disabled stripping keeps the test assemblies and the Preserve callback alive in the standalone player."

prerequisites:
  - "Familiarity with Unity scripting backends and ApiCompatibilityLevel"
  - "Awareness of the benchmark measurement protocol"

dependencies:
  packages: []
  skills:
    - "benchmark-methodology-total-over-window"

applies_to:
  languages:
    - "C#"
    - "PowerShell"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"
    dotnet: ">=netstandard2.1"

aliases:
  - "Release perf profile"
  - "Mono2x perf build"
  - "Standalone perf leg"

related:
  - "benchmark-methodology-total-over-window"
  - "benchmarks-run-in-highest-fidelity-scope"
  - "dispatch-hot-path"

status: "stable"
---

# Perf Config: Mono, .NET Standard 2.1, Release

> **One-line summary**: Published perf numbers are produced under Mono +
> .NET Standard 2.1 + Release. All Unity test legs pass
> `-releaseCodeOptimization`; standalone generated players are
> non-development Release players with disabled managed stripping. The
> Standalone perf leg additionally builds with `Mono2x` and
> `ApiCompatibilityLevel.NET_Standard`.

## Overview

A throughput number is only reproducible if the build profile that produced it
is fixed. DxMessaging pins Release mode for every Unity test/build and drives
CI from a single runner, `scripts/unity/run-ci-tests.ps1`. Each perf leg differs
only by documented backend parameters, so the profile cannot drift between
scopes.

The profile is Mono (not IL2CPP) with the .NET Standard 2.1 API surface and a
Release code-optimization build. Mono is the scope CI publishes because it is
the common deployment path and builds quickly enough to run on every change.

## Problem Statement

Two failure modes make perf numbers meaningless:

- **Unstated or mixed profiles.** A Debug EditMode number and a Release player
  number in the same table are not comparable. Without a pinned profile a
  regression and a build-flag change look identical.
- **Stripped test assemblies.** A default Release player strips managed code,
  which removes the benchmark assemblies and the `[Preserve]` standalone
  test-run callback, so the player runs nothing.

## Solution

`run-ci-tests.ps1` accepts the historical Release switches for compatibility,
but Release is the unconditional effective mode:

| Leg              | Profile flags                                                                     |
| ---------------- | --------------------------------------------------------------------------------- |
| EditMode tests   | `-ReleaseCodeOptimization`                                                        |
| PlayMode tests   | `-ReleaseCodeOptimization`                                                        |
| Standalone perf  | `-StandaloneScriptingBackend Mono2x -ReleasePlayerBuild -ReleaseCodeOptimization` |
| Standalone tests | `-ReleasePlayerBuild -ReleaseCodeOptimization`                                    |

`-ReleaseCodeOptimization` adds the editor flag
`-releaseCodeOptimization`, which sets
`CompilationPipeline.codeOptimization = Release` so test assemblies compile
without debug code paths. The Standalone leg additionally configures the
player:

```text
PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Standard);
PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Disabled);
```

`ApiCompatibilityLevel.NET_Standard` is the non-deprecated profile that targets
.NET Standard 2.1. `ManagedStrippingLevel.Disabled` keeps the test assemblies
and the `[Preserve]` callback in the player so the standalone benchmark run can
execute and write results.

`-ReleasePlayerBuild` is retained at the workflow call sites and the runner's
effective standalone default is non-development Release. The generated build
modifier omits `BuildOptions.Development` unless a future caller explicitly
opts back into a development player in code.

## CI Wiring

The Performance Numbers workflow (`.github/workflows/perf-numbers.yml`) runs the
`playmode` and `standalone` legs, both with comparisons enabled. The standalone
matrix entry sets `StandaloneScriptingBackend = 'Mono2x'`, `ReleasePlayerBuild`,
and `ReleaseCodeOptimization`; the playmode entry also sets the Release flags.
The backend parameter is the meaningful difference between scopes.

## Common Pitfalls

- "I will publish the Debug EditMode number; it is close enough." Debug changes
  the absolute throughput. Publish Release legs only.
- "I will leave default stripping on the player." Default stripping deletes the
  benchmark assemblies; the player runs nothing. Keep stripping Disabled for the
  perf leg.
- "I will switch the perf leg to IL2CPP for realism." The published scope is
  Mono. IL2CPP is a separate concern; do not silently change the published
  profile.
- "I will leave a Unity workflow without Release flags because the runner
  defaults Release." The runner default is the backstop; workflows still spell
  out Release flags so `validate-workflows` catches YAML drift before Unity runs.

## See Also

- [Benchmark Methodology: Total Over One Window](./benchmark-methodology-total-over-window.md)
- [Benchmarks Run in the Highest-Fidelity Scope](../testing/benchmarks-run-in-highest-fidelity-scope.md)
- [DxMessaging Dispatch Hot Path](./dispatch-hot-path.md)

## References

- Runner: `scripts/unity/run-ci-tests.ps1`
- Workflow: `.github/workflows/perf-numbers.yml`
- Methodology runbook: `docs/runbooks/perf-benchmark-methodology.md`
