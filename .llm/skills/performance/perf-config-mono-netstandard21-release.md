---
title: "Perf Config: Mono, .NET Standard 2.1, Release"
id: "perf-config-mono-netstandard21-release"
category: "performance"
version: "2.0.0"
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
  - "il2cpp"
  - "release"
  - "ci"
  - "standalone"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding the per-leg build matrix and why the headline leg runs Mono while a second leg runs Standalone IL2CPP for AOT coverage."

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
  - "PlayMode Mono headline"
  - "Standalone IL2CPP perf leg"

related:
  - "benchmark-methodology-total-over-window"
  - "benchmarks-run-in-highest-fidelity-scope"
  - "dispatch-hot-path"

status: "stable"
---

# Perf Config: Mono, .NET Standard 2.1, Release

> **One-line summary**: The headline numbers are produced in PlayMode under the
> Mono + .NET Standard 2.1 + Release profile, the backend the library ships with.
> Standalone IL2CPP is published alongside as the AOT leg. All Unity test legs
> pass `-releaseCodeOptimization`; standalone generated players are
> non-development Release players with disabled managed stripping.

## Overview

A throughput number is only reproducible if the build profile that produced it
is fixed. DxMessaging pins Release mode for every Unity test/build and drives
CI from a single runner, `scripts/unity/run-ci-tests.ps1`. Each perf leg differs
only by documented backend parameters, so the profile cannot drift between
scopes.

Two legs are published. The headline leg is PlayMode under Mono with the
.NET Standard 2.1 API surface and a Release code-optimization build, because the
library ships mostly on Mono and the PlayMode Mono leg is the fastest scope to
run on every change. The second leg is a Standalone player built under IL2CPP,
which gives ahead-of-time (AOT) coverage of the same scenarios on the backend
shipped titles often use for platforms that require it.

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
| PlayMode perf    | `-ReleaseCodeOptimization`                                                        |
| Standalone perf  | `-StandaloneScriptingBackend IL2CPP -ReleasePlayerBuild -ReleaseCodeOptimization` |
| Standalone tests | `-ReleasePlayerBuild -ReleaseCodeOptimization`                                    |

`-ReleaseCodeOptimization` adds the editor flag
`-releaseCodeOptimization`, which sets
`CompilationPipeline.codeOptimization = Release` so test assemblies compile
without debug code paths. The Standalone leg additionally configures the
player from the parameterized backend (`ScriptingImplementation.IL2CPP` for the
published AOT leg):

```text
PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP);
PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Standard);
PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Disabled);
```

`ApiCompatibilityLevel.NET_Standard` is the non-deprecated profile that targets
.NET Standard 2.1. `ManagedStrippingLevel.Disabled` keeps the test assemblies
and the `[Preserve]` callback in the player so the standalone benchmark run can
execute and write results. The runner's `StandaloneScriptingBackend` parameter
defaults to `IL2CPP` and accepts `Mono2x`, so the same script can build either
backend; the published AOT leg pins IL2CPP.

`-ReleasePlayerBuild` is retained at the workflow call sites and the runner's
effective standalone default is non-development Release. The generated build
modifier omits `BuildOptions.Development` unless a future caller explicitly
opts back into a development player in code.

## CI Wiring

The Performance Numbers workflow (`.github/workflows/perf-numbers.yml`) runs the
`playmode` and `standalone` legs, both with comparisons enabled. The playmode
entry sets the Release flags; the standalone matrix entry adds
`StandaloneScriptingBackend = 'IL2CPP'` on top of `ReleasePlayerBuild` and
`ReleaseCodeOptimization`. The backend parameter is the meaningful difference
between scopes: PlayMode runs the shipped Mono backend (the headline), and
Standalone runs IL2CPP for AOT coverage.

## Common Pitfalls

- "I will publish the Debug EditMode number; it is close enough." Debug changes
  the absolute throughput. Publish Release legs only.
- "I will leave default stripping on the player." Default stripping deletes the
  benchmark assemblies; the player runs nothing. Keep stripping Disabled for the
  perf leg.
- "PlayMode and Standalone should report the same throughput." They run
  different backends (Mono vs IL2CPP) with different codegen, so the numbers
  differ by design. Read each leg against its own backend, not against the other.
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
