---
name: il2cpp-build-configuration
description: "The pinned build profile behind published DxMessaging performance numbers - one Standalone IL2CPP Release player on .NET Standard 2.1 - and which optimization levers work on IL2CPP (AOT) versus Mono (JIT). Use when editing run-ci-tests.ps1 or perf-numbers.yml, when a published row reads x64 Debug or a standalone run executes zero tests, when reaching for Il2CppSetOption, AggressiveOptimization, or Unsafe/Span, or when local Mono numbers disagree with the published headline."
metadata:
  category: "performance"
  tags: "performance, benchmarks, il2cpp, release, ci, standalone"
---

# IL2CPP Build Configuration and the Backend Optimization Split

Published numbers come from ONE leg: a Standalone IL2CPP player built as a true
Release player against .NET Standard 2.1. IL2CPP (AOT) and Mono (JIT) have
different optimizers, so a perf lever helps one backend, both, or neither.

## When to use

- Editing `scripts/unity/run-ci-tests.ps1`, `.github/workflows/perf-numbers.yml`,
  or `.github/workflows/unity-benchmarks.yml`.
- A published benchmark row reads `x64 Debug`, or a standalone run reports zero
  tests.
- Adding `[Il2CppSetOption]`, `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`,
  or a `Span`/`Unsafe` rewrite for speed.
- The local Mono MCP loop disagrees with the published IL2CPP headline.
- Deciding whether a hot-path change can be measured locally at all.

## Rules

### The three Release knobs

Release is the unconditional effective mode in `run-ci-tests.ps1`; the flags stay
spelled out in workflows so YAML drift is visible in review.

1. `-ReleaseCodeOptimization` passes the editor flag `-releaseCodeOptimization`,
   setting `CompilationPipeline.codeOptimization = Release` so test assemblies
   compile without debug paths. Every Unity leg passes it, published or not.
1. `-ReleasePlayerBuild` must actively CLEAR the development flag:
   `playerOptions.options &= ~BuildOptions.Development;`. The Unity Test
   Framework's PlayerLauncher hands `ModifyOptions` a `BuildPlayerOptions` that
   ALREADY carries `BuildOptions.Development`, so merely not adding it leaves a
   development player.
1. The project configurator pins the native compiler configuration with
   `PlayerSettings.SetIl2CppCompilerConfiguration` set to
   `Il2CppCompilerConfiguration.Release` for `BuildTargetGroup.Standalone`. An
   ephemeral CI project has no committed default, and Debug C++ makes the
   standalone test player much slower even though it compiles faster.

Leg profiles: EditMode and PlayMode take `-ReleaseCodeOptimization`; the
published Standalone perf leg takes `-StandaloneScriptingBackend IL2CPP`
plus `-ReleasePlayerBuild` and `-ReleaseCodeOptimization`; Standalone tests take
`-ReleasePlayerBuild -ReleaseCodeOptimization`.

### Player configuration

- `PlayerSettings.SetScriptingBackend` must select
  `ScriptingImplementation.IL2CPP` for `BuildTargetGroup.Standalone`. The
  runner's `StandaloneScriptingBackend` parameter defaults to `IL2CPP` and also
  accepts `Mono2x`.
- `PlayerSettings.SetApiCompatibilityLevel(..., ApiCompatibilityLevel.NET_Standard)`
  is the non-deprecated profile targeting .NET Standard 2.1.
- `PlayerSettings.SetManagedStrippingLevel(..., ManagedStrippingLevel.Disabled)`
  is mandatory. Default stripping deletes the benchmark assemblies and the
  `[Preserve]` standalone test-run callback, and the player then runs nothing.

### Proving the profile

- The configurator logs a `DXM perf config:` line carrying the effective
  `backend`, `api`, `codeOpt`, and `il2cppConfig` values. A published run must
  show `backend=IL2CPP`, `api=NET_Standard`, `codeOpt=Release`, and
  `il2cppConfig=Release`.
- Each row encodes its platform string. The published leg must read
  `Standalone IL2CPP x64 Release (WindowsPlayer; ...)`. A published `x64 Debug`
  row means `Debug.isDebugBuild` was true - a configuration bug, not a code
  regression.
- Never publish a Debug or in-editor number. PlayMode and EditMode still run the
  same scenarios (weekly `unity-benchmarks.yml`) as coverage, not as numbers.

### Backend split

- `[Il2CppSetOption]` is the only null/bounds-check elision lever, and it is
  INERT on Mono. `[Conditional("ENABLE_IL2CPP")]` bodies compile out on Mono
  entirely. Generic static field access is cheap on Mono after JIT but carries a
  per-access class-init check on IL2CPP.
- `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` is REJECTED. A
  12-batch tiering probe held flat at ~5.9k ns from batch 0 to batch 11: Unity's
  Mono JIT compiles each method once at a fixed level, so there is no tier for
  the attribute to promote, and it is inert on AOT. Do not add it.
- `Span`/`Unsafe.Add` bounds-check elision on Mono is REJECTED for three
  independent reasons: the difference is below timer and delegate-dispatch noise
  (the ~10 ns per-entry delegate invoke dwarfs a ~1 ns bounds check); the flat
  dispatch entry struct holds managed references, so a pointer walk is
  GC-relocation-unsafe; and `System.Runtime.CompilerServices.Unsafe` is absent
  from IL2CPP players, which is why `Runtime/Core/Internal/DxUnsafe.cs` wraps
  `UnsafeUtility`.
- `InstanceId` hashing is already optimal (`GetHashCode()` returns the raw `int`,
  `Equals` is an `int` compare). The targeted path being ~28% slower than
  untargeted is inherent hash-routing cost, not a bottleneck to fix.
- The steady-state dispatch path is at its Mono FLOOR; remaining single-emit
  headroom is IL2CPP-only. The shipped example is hoisting
  `EnsureAot{Untargeted,Targeted,Sourced}Bridge<T>()` out of the per-emit path
  into the dispatch-plan-creation block, guarded by
  `UntypedDispatchTests.TypedDispatchSeedsBridgeForPrivateManualMessageBeforeUntypedDispatch`
  on the standalone IL2CPP leg.
- Measure honestly: run Mono benchmarks as a NEGATIVE control for a
  `[Conditional("ENABLE_IL2CPP")]` change (numbers must stay within the +/-1-3%
  editor noise band and zero-alloc must hold); pre-filter a non-conditional
  change on the Mono loop, then confirm on IL2CPP; reject anything that regresses
  Mono even when IL2CPP is neutral.
- Mono and IL2CPP will not report the same throughput. Read each scope against
  its own backend.

## References

| Document                                                                                                | Purpose                                                                                                                          |
| ------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| [perf-config-il2cpp-release-netstandard21.md](./references/perf-config-il2cpp-release-netstandard21.md) | The three Release knobs, player settings, per-leg flag table, profile proof artifacts, and CI wiring                             |
| [mono-vs-il2cpp-optimization-split.md](./references/mono-vs-il2cpp-optimization-split.md)               | Backend capability table, measured accept/reject verdicts on each optimization lever, and honest cross-backend measurement rules |
