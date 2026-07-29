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
1. **Prove the assembly is fresh before you trust a green run.** When a package
   assembly fails to compile, Unity keeps the last good DLL loaded and
   `DxMcpTestRunner` happily runs it, so an edit that does not compile reports the
   previous run's passing numbers. `Unity_ReadConsole` can come back empty in that
   state, and the host editor may have Auto Refresh disabled
   (`EditorPrefs.GetInt("kAutoRefreshMode") == 0`), which makes it permanent. Assert a
   symbol you just added actually resolves, and compare
   `System.IO.File.GetLastWriteTimeUtc(type.Assembly.Location)` against the source
   file's write time:

   ```csharp
   System.Type fixture = null;
   foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
   {
       fixture = assembly.GetType("DxMessaging.Tests.Editor.MyNewTests");
       if (fixture != null) { break; }
   }
   result.Log("fresh={0} asmUtc={1}",
       fixture != null && fixture.GetMethod("MyNewTestMethod") != null,
       System.IO.File.GetLastWriteTimeUtc(fixture.Assembly.Location).ToString("O"));
   ```

   A stale timestamp means the compile failed. Read the CI job log or Unity's
   `Editor.log` for the `CS####` rather than re-running the suite.

1. **Run**: invoke the host bridge `DxMcpTestRunner.Run(testMode, assemblyNames,
testNames, categoryNames, resultPath)` via `Unity_RunCommand`. Locate the type by
   scanning `AppDomain` assemblies. Arguments are semicolon-separated lists; `null`
   means "no filter".
   - `testMode`: `EditMode` or `PlayMode`.
   - `resultPath` resolves relative to the HOST Unity project root (the editor's
     working directory), NOT the embedded package. To land in the container-visible,
     gitignored `.artifacts/unity-mcp/`, prefix it with the package path:
     `Packages/com.wallstop-studios.dxmessaging/.artifacts/unity-mcp/<name>.json`. A
     bare `.artifacts/unity-mcp/<name>.json` writes to the host project root instead,
     where the container cannot see it.
   - `testNames` accepts a fixture's full type name (for example
     `DxMessaging.Tests.Runtime.Core.TestAttributeContractTests`) to run just that
     fixture -- handy for a fast red-green loop on a single contract test.
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
[Unity Perf Test Isolation](../../benchmark-methodology/references/unity-perf-test-isolation.md).

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
[UPM Test Harness](../../unity-test-execution/references/upm-test-harness.md) and [Unity CI Matrix](../../unity-editor-ci/references/unity-ci-matrix.md).

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

See [Fast Unity Tests](../../unity-test-execution/references/fast-unity-tests.md) for the levers themselves.

## See Also

- [Fast Unity Tests](../../unity-test-execution/references/fast-unity-tests.md)
- [UPM Test Harness](../../unity-test-execution/references/upm-test-harness.md)
- [Unity Perf Test Isolation](../../benchmark-methodology/references/unity-perf-test-isolation.md)
- [Unity CI Matrix](../../unity-editor-ci/references/unity-ci-matrix.md)
- [Devcontainer Cache Contract](../../unity-editor-conventions/references/devcontainer-cache-contract.md)

## References

- Unity TestRunnerApi: https://docs.unity3d.com/Packages/com.unity.test-framework@latest
- Source: `.llm/context.md` (Running Unity Tests)
