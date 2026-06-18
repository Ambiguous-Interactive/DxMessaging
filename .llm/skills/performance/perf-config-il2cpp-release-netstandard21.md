---
title: "Perf Config: IL2CPP Release, .NET Standard 2.1"
id: "perf-config-il2cpp-release-netstandard21"
category: "performance"
version: "3.0.0"
created: "2026-06-07"
updated: "2026-06-12"

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
  - "il2cpp"
  - "release"
  - "ci"
  - "standalone"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding the three Release knobs and why the Unity Test Framework's default development player silently produces Debug numbers."

impact:
  performance:
    rating: "high"
    details: "Build profile changes the absolute numbers; an unstated or accidentally-Debug profile makes published throughput unreproducible."
  maintainability:
    rating: "high"
    details: "One runner script owns the profile flags; the published leg differs from test legs only by documented parameters."
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
  - "Standalone IL2CPP Release headline"
  - "IL2CPP perf leg"

related:
  - "benchmark-methodology-total-over-window"
  - "benchmarks-run-in-highest-fidelity-scope"
  - "dispatch-hot-path"

status: "stable"
---

# Perf Config: IL2CPP Release, .NET Standard 2.1

> **One-line summary**: The published numbers come from ONE leg: a Standalone
> IL2CPP player built as a true Release player (`BuildOptions.Development`
> stripped, Release IL2CPP C++ configuration) against .NET Standard 2.1 with
> Release code optimization. PlayMode and EditMode remain local/CI test
> scopes; their numbers are not published.

## Overview

A throughput number is only reproducible if the build profile that produced it
is fixed. DxMessaging pins Release mode for every Unity test/build and drives
CI from a single runner, `scripts/unity/run-ci-tests.ps1`.

One leg is published. The headline is a Standalone player built under IL2CPP,
the ahead-of-time (AOT) backend shipped players actually run, with the
.NET Standard 2.1 API surface and every Release knob engaged. The in-editor
PlayMode Mono leg is retired from publishing; PlayMode and EditMode still run
the same scenarios for local iteration and for the weekly per-version
benchmark-test coverage in `unity-benchmarks.yml`, but those runs are
coverage, not published numbers.

## Problem Statement

Three failure modes make perf numbers meaningless:

- **Unstated or mixed profiles.** A Debug EditMode number and a Release player
  number in the same table are not comparable. Without a pinned profile a
  regression and a build-flag change look identical.
- **A development player posing as Release.** Unity Test Framework's
  PlayerLauncher injects `BuildOptions.Development` into the build options by
  default. The resulting player reports `Debug.isDebugBuild == true` (published
  runs showed "x64 Debug" platform strings until the strip landed) and carries
  development-build overhead; the C++ configuration is pinned separately so it
  can never ride on a development default.
- **Stripped test assemblies.** A default Release player strips managed code,
  which removes the benchmark assemblies and the `[Preserve]` standalone
  test-run callback, so the player runs nothing.

## Solution: the three Release knobs

`run-ci-tests.ps1` accepts the historical Release switches for compatibility,
but Release is the unconditional effective mode. The published leg engages all
three knobs:

1. **`-ReleaseCodeOptimization` (editor compilation).** Adds the editor flag
   `-releaseCodeOptimization`, which sets
   `CompilationPipeline.codeOptimization = Release` so test assemblies compile
   without debug code paths. Every Unity CI leg passes this, including the
   non-published EditMode/PlayMode legs.
1. **`-ReleasePlayerBuild` (strip `BuildOptions.Development`).** The generated
   test-player build modifier must actively CLEAR the flag, because the Unity
   Test Framework's PlayerLauncher hands `ModifyOptions` a
   `BuildPlayerOptions` that already carries `BuildOptions.Development`;
   merely not adding the flag leaves a development player:

   ```text
   playerOptions.options &= ~BuildOptions.Development;
   ```

1. **Release IL2CPP C++ configuration (configurator).** The project
   configurator pins the native compiler configuration explicitly: an ephemeral
   CI project has no committed default for the setting, and the pin removes the
   variable regardless of how the build flags would otherwise influence it.
   Measured CI runs showed Debug C++ reduces native compile time but makes the
   standalone test player much slower, so both the correctness leg and the
   published perf leg stay Release:

   ```text
   PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone, Il2CppCompilerConfiguration.Release);
   ```

| Leg                         | Profile flags                                                                     |
| --------------------------- | --------------------------------------------------------------------------------- |
| EditMode tests              | `-ReleaseCodeOptimization`                                                        |
| PlayMode tests/benchmarks   | `-ReleaseCodeOptimization`                                                        |
| Standalone perf (published) | `-StandaloneScriptingBackend IL2CPP -ReleasePlayerBuild -ReleaseCodeOptimization` |
| Standalone tests            | `-ReleasePlayerBuild -ReleaseCodeOptimization`                                    |

## .NET Standard 2.1 and stripping

The Standalone leg additionally configures the player from the parameterized
backend (`ScriptingImplementation.IL2CPP` for the published leg):

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
backend; the published leg pins IL2CPP.

## Proving the profile

Two artifacts prove a published run used the right profile:

- The configurator logs the effective Unity settings into the CI log:

  ```text
  DXM perf config: backend=..., api=..., codeOpt=..., il2cppConfig=...
  ```

  For the published leg that line must show `backend=IL2CPP`,
  `api=NET_Standard`, `codeOpt=Release`, and `il2cppConfig=Release`.

- Each benchmark row encodes the effective platform string. The published leg
  must read `Standalone IL2CPP x64 Release (WindowsPlayer; ...)`. A published
  row reading `x64 Debug` means `Debug.isDebugBuild` was true in the player:
  a configuration bug (the Development flag survived the build), not a code
  regression.

## CI Wiring

The Performance Numbers workflow (`.github/workflows/perf-numbers.yml`) runs a
single-entry matrix: the `standalone` leg with comparisons enabled and
`StandaloneScriptingBackend = 'IL2CPP'` on top of `ReleasePlayerBuild` and
`ReleaseCodeOptimization`. The regression gate (`render-perf-deltas.js`)
compares `--scope Standalone` rows against the committed master baseline. The
weekly `unity-benchmarks.yml` runs the EditMode and PlayMode benchmark tests
across Unity versions with `-ReleaseCodeOptimization` for coverage only.

## Common Pitfalls

- "I will publish the Debug EditMode number; it is close enough." Debug changes
  the absolute throughput. Publish the Release player leg only.
- "I will leave default stripping on the player." Default stripping deletes the
  benchmark assemblies; the player runs nothing. Keep stripping Disabled for the
  perf leg.
- "Not setting `BuildOptions.Development` is enough." It is not; the Unity Test
  Framework injects the flag, so the build modifier must clear it with
  `&= ~BuildOptions.Development`.
- "PlayMode and Standalone should report the same throughput." They run
  different backends (Mono JIT vs IL2CPP AOT) with different codegen, so the
  numbers differ by design. Read each scope against its own backend.
- "I will leave a Unity workflow without Release flags because the runner
  defaults Release." The runner default is the backstop; workflows still spell
  out Release flags explicitly so YAML drift is visible in review.

## See Also

- [Benchmark Methodology: Total Over One Window](./benchmark-methodology-total-over-window.md)
- [Benchmarks Run in the Highest-Fidelity Scope](../testing/benchmarks-run-in-highest-fidelity-scope.md)
- [DxMessaging Dispatch Hot Path](./dispatch-hot-path.md)

## References

- Runner: `scripts/unity/run-ci-tests.ps1`
- Workflow: `.github/workflows/perf-numbers.yml`
- Methodology runbook: `docs/runbooks/perf-benchmark-methodology.md`
