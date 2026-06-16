---
title: "Unity MCP Test Loop"
id: "mcp-test-loop"
category: "unity"
version: "1.1.0"
created: "2026-06-14"
updated: "2026-06-15"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".llm/context.md"
    - path: ".llm/skills/unity/upm-test-harness.md"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "testing"
  - "mcp"
  - "devcontainer"
  - "test-runner"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding the host-vs-container split, the DxMcpTestRunner bridge, and the Unity_RunCommand sandbox restrictions."

impact:
  performance:
    rating: "none"
    details: "Tooling only; no runtime cost"
  maintainability:
    rating: "high"
    details: "Single local Unity verification path; no docker / ephemeral-editor machinery to maintain"
  testability:
    rating: "high"
    details: "EditMode and PlayMode suites run against the live host editor with a status-polled artifact"

prerequisites:
  - "A host Unity project that embeds this repo as a UPM package, with the unity-mcp-remote MCP server connected"
  - "The DxMcpTestRunner bridge present in the host project (regenerate if missing)"

dependencies:
  packages: []
  skills:
    - "upm-test-harness"
    - "unity-perf-test-isolation"
    - "unity-ci-matrix"

applies_to:
  languages:
    - "C#"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "MCP test loop"
  - "unity-mcp-remote"
  - "DxMcpTestRunner"

related:
  - "upm-test-harness"
  - "unity-perf-test-isolation"
  - "unity-ci-matrix"
  - "devcontainer-cache-contract"

status: "stable"
---

<!-- trigger: unity, mcp, local test, editmode, playmode, DxMcpTestRunner, unity-mcp-remote | Local Unity verification via the MCP server | Core -->

# Unity MCP Test Loop

> **One-line summary**: Local Unity verification runs through the `unity-mcp-remote`
> MCP server (the host editor), driven by `Unity_RunCommand`. The devcontainer ships
> no local Unity build; there is no docker / ephemeral-editor runner anymore.

## When to Use

- Iterating on Runtime/Editor code that has Unity tests under `Tests/Editor` or `Tests/Runtime`.
- Getting a fast local Mono/editor signal before pushing (the shipping IL2CPP-Release headline still comes from CI).
- Capturing a local perf baseline from the benchmark suite.
- Reproducing a Unity behavior the .NET-only `dotnet test` surface cannot exercise.

## When NOT to Use

- Source-generator / analyzer tests under `SourceGenerators/`. Use `dotnet test` directly; no Unity needed.
- Pure documentation or markdown changes; no Unity surface to exercise.
- The published IL2CPP-Release headline. That is a CI-only artifact (`scripts/unity/run-ci-tests.ps1` on self-hosted Windows); the local MCP loop is editor/Mono and does not reproduce the standalone IL2CPP player byte-for-byte.

## Topology

The devcontainer workspace (`/workspaces/com.wallstop-studios.dxmessaging`) IS the
same directory as the embedded package inside the host Unity project. Edits made
in-container are instantly visible to the host editor. Compilation and tests run in
the host editor; the container only edits files and drives the editor over MCP.

## The Loop

1. **Edit** files in the container as usual.
1. **Compile**: trigger `AssetDatabase.Refresh()` via `Unity_RunCommand`. Wait for the
   recompile to settle before running tests.
1. **Run**: invoke the host bridge `DxMcpTestRunner.Run(testMode, assemblyNames,
testNames, categoryNames, resultPath)` via `Unity_RunCommand`. Locate the type by
   scanning `AppDomain` assemblies. Arguments are semicolon-separated lists; `null`
   means "no filter".
   - `testMode`: `EditMode` or `PlayMode`.
   - Write results under `.artifacts/unity-mcp/` (gitignored).
1. **Poll**: read the `.status` sidecar next to `resultPath` from bash in the
   container. It moves `running` -> `done` (or `error: <message>`). The JSON result
   carries `{ passCount, failCount, skipCount, inconclusiveCount, durationSeconds,
failures[] }`.

The bridge survives domain reloads via `[InitializeOnLoad]` + `SessionState`, so a
recompile mid-run does not lose the result.

## Test Assemblies

| Mode     | Assemblies                                                                                                                                                  |
| -------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| EditMode | `WallstopStudios.DxMessaging.Tests.Editor`, `...Tests.Editor.Allocations`, `...Tests.00.Editor.Benchmarks`                                                  |
| PlayMode | `...Tests.Runtime`, `...Tests.00.Runtime.Benchmarks` (category `PerfBench`), `...Tests.00.Runtime.Comparisons`, DI integrations (Reflex/VContainer/Zenject) |

The canonical include list for CI is `scripts/unity/lib/asmdef-discovery.js`
(`defaultIncludeAssemblies`); keep MCP-loop assembly choices consistent with it.

## Perf Baselines

The benchmark CSV defaults to `.artifacts/perf-baseline.csv`. Override the output via
the `DX_PERF_BASELINE` env var and stamp the commit column via `DX_PERF_COMMIT`; set
both in-process with `System.Environment.SetEnvironmentVariable` BEFORE invoking the
benchmark run, since the editor process is already up. See
[Unity Perf Test Isolation](./unity-perf-test-isolation.md).

## Sandbox Restrictions

`Unity_RunCommand` snippets run in a restricted compile sandbox:

- `using System.Reflection;` is REJECTED. Fully qualify instead
  (`System.Reflection.Assembly`, `System.Reflection.BindingFlags`, ...).
- Inside `DxMessaging.*` namespaces the bare identifier `Unity` binds to
  `DxMessaging.Unity`, not `UnityEngine`-adjacent types; use a `global::`-qualified
  alias when that ambiguity bites.

## If the Bridge Is Missing

The `DxMcpTestRunner` bridge lives in the host project (under its `Assets/Editor/`),
NOT in this package repo, so a clean of the host project drops it. Regenerate it via
`Unity_RunCommand` (`System.IO.File.WriteAllText` of the bridge source, then
`AssetDatabase.Refresh()`). It wraps `TestRunnerApi` and writes the JSON result plus
the `.status` sidecar.

## CI vs Local

CI calls `scripts/unity/run-ci-tests.ps1` on self-hosted Windows runners (direct
Unity, generated host project under `.artifacts/unity/projects/<version>-<mode>/`,
classic-serial license with a guaranteed return). The MCP loop is the LOCAL path
only; it does not run in CI and does not need any Unity license secrets. See
[UPM Test Harness](./upm-test-harness.md) and [Unity CI Matrix](./unity-ci-matrix.md).

## Measuring Test-Suite Speed

`DxMcpTestRunner.Run` writes `durationSeconds` into the result JSON, so the loop
doubles as a stopwatch for test-suite-performance work:

1. Baseline a mode (`Run("PlayMode", "<assembly>", null, null, <path>)`), record
   `durationSeconds` + `{pass,fail,skip}`.
1. Change ONE lever, re-run the SAME `Run(...)` call, diff the duration and the
   counts. Keep a change only if pass counts hold and no flake appears across
   repeated runs.

Two caveats keep the numbers honest:

- **Warm-editor frames are near-free.** The host editor is already warm, so the
  local PlayMode suite finishes in tens of seconds and a structural fix (batched
  teardown, disabled reload) can show a near-zero LOCAL delta while still paying
  off on the cold CI legs. Per-mode `< 3 min` is a CI metric; locally, trust
  relative deltas, not the absolute number.
- **A script edit forces one reload.** `AssetDatabase.Refresh()` after editing a
  `.cs` triggers a domain reload even when enter-play-mode reload is disabled, so
  the FIRST play-mode entry after an edit is a fresh domain. Run twice
  back-to-back to exercise the true persistent-domain (reload-off) path -- a
  test with a latent reload dependency fails only on the SECOND, persistent run.

See [Fast Unity Tests](../testing/fast-unity-tests.md) for the levers themselves.

## See Also

- [Fast Unity Tests](../testing/fast-unity-tests.md)
- [UPM Test Harness](./upm-test-harness.md)
- [Unity Perf Test Isolation](./unity-perf-test-isolation.md)
- [Unity CI Matrix](./unity-ci-matrix.md)
- [Devcontainer Cache Contract](./devcontainer-cache-contract.md)

## References

- Unity TestRunnerApi: https://docs.unity3d.com/Packages/com.unity.test-framework@latest
- Source: `.llm/context.md` (Running Unity Tests)
