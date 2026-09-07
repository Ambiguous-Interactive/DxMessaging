<!-- trigger: unity, mcp, local test, editmode, playmode, DxMcpTestRunner, unity-mcp-remote | Local Unity verification via the MCP server | Core -->

# Unity MCP Test Loop

> **One-line summary**: Local Unity verification runs through the `unity-mcp-remote`
> MCP server (the host editor), using Pipeline or the legacy relay. The devcontainer ships
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

## Tool selection

Discover the connected MCP catalog before choosing tool names. The host bridge defaults to
`unity mcp` with an explicit host project path. Pipeline tools differ from the legacy Assistant
relay selected by `--backend relay`; both can reach the maintained `DxMcpTestRunner`.
Host paths can be Windows, macOS, or Linux paths and never refer to the container mount.

| Operation                    | Pipeline                                                           | Legacy relay                                                                  |
| ---------------------------- | ------------------------------------------------------------------ | ----------------------------------------------------------------------------- |
| Editor state                 | `editor_status`                                                    | `Unity_ManageEditor` with `GetState`                                          |
| Open scenes                  | `list_open_scenes`                                                 | `Unity_ManageScene` with `GetActive`, plus observer evidence for other scenes |
| Stage                        | Fresh observer snapshot; fill missing state through idle bootstrap | `Unity_ManageEditor` with `GetPrefabStage`                                    |
| Invoke maintained runner     | `eval` with `code`                                                 | `Unity_RunCommand` with its advertised snippet schema                         |
| Compile after safe preflight | `menu` with `path: Assets/Refresh`                                 | `Unity_ManageMenuItem` with `Assets/Refresh`                                  |
| Console errors               | `console`                                                          | `Unity_ReadConsole`                                                           |

Read the actual schemas: legacy `result.Log` output helpers and sandbox restrictions are not a
Pipeline contract. Pipeline `eval` has invoked the maintained runner successfully for EditMode
allocation and PlayMode fixtures, with matching result and cleanup artifacts. This verifies
runner invocation, not arbitrary API compatibility or implicit asset-refresh behavior.

Legacy `Unity_RunCommand` refreshes assets before executing snippets. Pipeline `eval` refresh
behavior has not been established. For both backends, prefer passive state queries and fresh
observer files before evaluation, and poll only files while a test is running. Never change
transports to bypass a rejected operation. See the [MCP setup guide](../../../../scripts/mcp/README.md)
for endpoint, authentication, and client configuration.

## The Loop

1. **Edit** files in the container as usual.
1. **Preflight.** Use the backend's editor and scene queries from the table above,
   together with a fresh observer snapshot. Establish framework idleness,
   idle editor flags, the main stage, loaded-scene count, and every scene's path and dirty flag.
   An active-scene response alone cannot prove other scenes are clean. When the observer is
   absent or incomplete, use the [bootstrap procedure](#bootstrap-without-a-complete-passive-observer)
   below. A tool's read-only name does not prove its implementation has no import side effects.
1. **Compile**: after the safe preflight, execute `Assets/Refresh` through Pipeline `menu`
   or legacy `Unity_ManageMenuItem`. Legacy `Unity_ValidateScript` can validate changed C# under
   `Assets/` but rejects embedded `Packages/` paths. Package edits rely on refresh plus the
   fresh-assembly proof below. Wait for compilation to settle before running tests.
   Do not call `AssetDatabase.Refresh()` from an evaluation snippet.
1. **Prove the assembly is fresh before you trust a green run.** When a package
   assembly fails to compile, Unity keeps the last good DLL loaded and
   `DxMcpTestRunner` happily runs it, so an edit that does not compile reports the
   previous run's passing numbers. Console tools can come back empty in that
   state, and the host editor may have Auto Refresh disabled
   (`EditorPrefs.GetInt("kAutoRefreshMode") == 0`), which makes it permanent. Assert a
   symbol you just added actually resolves, and compare
   `System.IO.File.GetLastWriteTimeUtc(type.Assembly.Location)` against the source
   file's write time. This example uses the legacy `result.Log` helper; with Pipeline, return
   the equivalent values from `eval` using its own result schema:

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

   After the safe idle preflight, resolve the connected editor's `Application.consoleLogPath`
   and read only the current compile's `CS####` errors. Do not assume a default `Editor.log`
   belongs to that editor or print the entire log.

1. **Run**: invoke the host bridge `DxMcpTestRunner.Run(testMode, assemblyNames,
testNames, categoryNames, resultPath)` via Pipeline `eval` or legacy `Unity_RunCommand`.
   Locate the type by scanning `AppDomain` assemblies. Arguments are semicolon-separated lists; `null`
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
1. **Poll** through files only. With the maintained bridge, `.status` describes raw result
   availability and `.cleanup.status` describes passive framework completion. Require `done` in
   both files, positive passes, zero failed/inconclusive cases, and matching GUID/path companions.
   Keep expected skips visible. Read the complete tree in the result JSON for individual outcomes.

The maintained bridge survives domain reloads through `[InitializeOnLoad]` and `SessionState`.
Its source, installation, artifact contract, and preflight snapshot are documented in
[Maintain the local Unity test runner](../../../../scripts/mcp/README.md#maintain-the-local-unity-test-runner).

## Bootstrap without a complete passive observer

Missing observer fields alone do not require user confirmation or prevent local verification.
First read the available passive editor/scene queries and any existing snapshot. If available
flags are idle/clean, no non-main stage is known, and no test is known active, use a minimal
inspection through Pipeline `eval` or legacy `Unity_RunCommand` to read
`TestRunnerApi.IsRunActive`, current editor flags and stage, every open scene's path/loading/dirty state through
`SceneManager.sceneCount` and `GetSceneAt`, and the runner SessionState ownership keys listed in
the installation guide. Do not mutate scenes, launch tests, or install source in that inspection.
Use the installed framework's available API; `IsRunActive` may be nonpublic and unavailable to a
direct snippet call. Resolve API compatibility failures with a minimal inspection sequence using
supported public metadata or installed runner APIs. A compile or lookup failure does not prove
framework inactivity.

Legacy `Unity_RunCommand` refreshes assets before executing the snippet. State that limitation honestly:
the inspection bootstraps missing evidence; its result cannot prove that preceding refresh was
safe. Pipeline `eval` has no established refresh guarantee; do not infer one from the
legacy implementation or its absence from the tool description. Preserve tool approval gates.
If a tool rejects the inspection or installation, report its reason and do not switch transports or tools to bypass the rejection. Wait for actual framework
activity, compilation, imports, or play-mode transitions. Stop for dirty or unnamed scenes or a
non-main stage; never save, discard, or switch the developer's scenes to force progress.

Once the inspection shows inactive framework/editor state and saved, clean scenes, install or
update the maintained source through supported MCP editing, following the
[backup and installation flow](../../../../scripts/mcp/README.md#maintain-the-local-unity-test-runner).
Validate as supported, refresh through the backend's menu tool, and verify the loaded assembly
and fresh passive snapshots before testing. If supported inspection cannot resolve the required state, report the
specific failure. During a known or owned test, poll its files and passive snapshots only,
including after an observation timeout; never use bootstrap inspection as a polling loop.

## Framework cleanup gate

`RunFinished` makes a result available before Unity Test Framework necessarily finishes restoring
scenes. Session 266 observed `Invalid SceneManagerSetup` and a missing temporary scene after a new
run started during that cleanup interval. A raw passing result is not cleanup evidence.

The maintained `DxMcpTestRunner` retains the Execute GUID, owns a fresh result path, records
`IErrorCallbacks.OnError`, and observes framework inactivity from `EditorApplication.update`.
It writes `.cleanup.json` and `.cleanup.status` after verifying the original scene setup. Missing
results and late framework errors produce terminal errors; failed observations retain ownership.
An observation timeout never authorizes cancellation or a replacement launch.

Do not poll through code evaluation during a test run on either backend. Legacy `Unity_RunCommand`
refreshes assets before even read-only snippets; Pipeline `eval` refresh behavior is unverified.
Use filesystem polling and the bridge's passive `editor-state.json` for subsequent preflight, requiring current timestamps and complete safe-state
fields. Missing, stale, or temporarily malformed files prove no state; continue observing the same
job without refreshing assets.

For a host that still has the earlier simple bridge, retain its reviewed
`DxMcpObservedTestRunner` until migration is safe. That observer writes `.observer.status`;
require its terminal `done` alongside the raw result. An old terminal file is historical evidence,
not a fresh editor preflight. The simple bridge alone can leave `running` without a result after
framework setup fails. Prove inactivity before classifying that as terminal or recovering scenes.
Never discard an unnamed or dirty user scene.

This is local orchestration. It adds no delays, retries, or test launches to CI.
[Issue #544](https://github.com/Ambiguous-Interactive/DxMessaging/issues/544) tracks native acceptance
of maintained bridge ownership and regeneration.

## Shared Editor Safety

The MCP host is the developer's editor, not an expendable test process. A normal
`new GameObject(...)` joins the active scene and marks it dirty; destroying the object
in teardown does not clear the dirty flag, so the next refresh or scene switch can
raise a modal save prompt. Tests and probes that only need a Unity object must use:

```csharp
GameObject host = EditorUtility.CreateGameObjectWithHideFlags(
    "Probe",
    HideFlags.HideAndDontSave,
    typeof(MyComponent)
);
```

The returned object has no valid scene. Do not use this form for behavior that requires
scene lifecycle or physics registration. Put those objects in an isolated scene, close
that scene without prompting in `finally`, restore the previous active scene, and verify
the developer's original scenes remain clean. Re-run the read-only preflight after every
test batch.

## Test Assemblies

| Mode     | Assemblies                                                                                                                                                  |
| -------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| EditMode | `WallstopStudios.DxMessaging.Tests.Editor`, `...Tests.Editor.Allocations`, `...Tests.00.Editor.Benchmarks`                                                  |
| PlayMode | `...Tests.Runtime`, `...Tests.00.Runtime.Benchmarks` (category `PerfBench`), `...Tests.00.Runtime.Comparisons`, DI integrations (Reflex/VContainer/Zenject) |

The canonical include list for CI is `scripts/unity/lib/asmdef-discovery.js`
(`defaultIncludeAssemblies`); keep MCP-loop assembly choices consistent with it.

## Default Editor filters and imported samples

For an ordinary Editor run, select only `WallstopStudios.DxMessaging.Tests.Editor` and use the
EditMode exclusions from the [Unity test workflow](../../../../.github/workflows/unity-tests.yml).
Run `WallstopStudios.DxMessaging.Tests.Editor.Allocations` and benchmark assemblies as separate
scopes so their measurement windows do not inflate the ordinary suite time.

The bridge splits `categoryNames` and `testNames` at semicolons and forwards native filters
unchanged. The framework separates leading `!` exclusions from inclusions and ANDs the
exclusions. Inspect the installed `RuntimeTestRunnerFilter.AddFilters` if its behavior changes.
An assembly-name inclusion can select an `[Explicit]` test. When running the whole Editor
assembly, pass
`!DxMessaging.Tests.Editor.EditorToolingDocumentationCaptureTests.CaptureAllPublishedEditorTooling`
as `testNames` to prevent the documentation writer from regenerating tracked PNGs.
Keep other tests in that fixture selected.

Eight `SampleQualityContractTests` cases need imported runnable samples at `Assets/DxmCiSamples`.
A missing fixture is an inconclusive result, not a passing verification. The CI provisioner
`Copy-SamplesForCompilation` in `scripts/unity/run-ci-tests.ps1` copies `.cs`, `.asmdef`, and
`.unity` files with their original `.meta` files. The local fixture needs Mini Combat,
UI Buttons + Inspector, and Diagnostics Tooling Exerciser. Preserve their GUIDs, refuse an
existing destination or duplicate imported sample GUIDs/assembly names, and record ownership
and file hashes outside `Assets`. Import only while the editor is proven safe. After verification,
remove only the owned fixture, preserving any unexpected changes for review, and confirm the
editor's original scenes remain clean. Do not weaken the fixture assertions or accept the
inconclusive results to obtain a green run.

## Perf Baselines

The benchmark CSV defaults to `.artifacts/perf-baseline.csv`. Override the output via
the `DX_PERF_BASELINE` env var and stamp the commit column via `DX_PERF_COMMIT`; set
both in-process with `System.Environment.SetEnvironmentVariable` BEFORE invoking the
benchmark run, since the editor process is already up. See
[Unity Perf Test Isolation](../../benchmark-methodology/references/unity-perf-test-isolation.md).

## Sandbox Restrictions

Legacy `Unity_RunCommand` snippets run in a restricted compile sandbox. Pipeline `eval` has its
own implementation; inspect its current schema and errors instead of applying legacy rules blindly:

- `using System.Reflection;` is rejected. Qualify allowed types such as
  `System.Reflection.Assembly`; qualification does not bypass member restrictions. Some installed
  sandboxes also reject `System.Reflection.BindingFlags` members. Prefer public APIs or supported
  script editing within existing tool permissions; do not bypass a tool rejection.
- Inside `DxMessaging.*` namespaces the bare identifier `Unity` binds to
  `DxMessaging.Unity`, not `UnityEngine`-adjacent types; use a `global::`-qualified
  alias when that ambiguity bites.

## If the Bridge Is Missing

The installed bridge lives under the host's `Assets/Editor/`, outside this package. Regenerate it
from the maintained `scripts/mcp/DxMcpTestRunner.cs.txt`, following the
[installation and preflight contract](../../../../scripts/mcp/README.md#maintain-the-local-unity-test-runner).
Use the bootstrap procedure above if passive evidence is incomplete. Do not invent a replacement
bridge or describe a legacy bootstrap's implicit refresh as proven safe. Do not assume Pipeline
`eval` performs or avoids that refresh.

## CI vs Local

CI calls `scripts/unity/run-ci-tests.ps1` on self-hosted Windows runners (direct
Unity, generated host project under `.artifacts/u/<version>-<mode>/`,
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
- **A script edit forces one reload.** Executing `Assets/Refresh` after editing a
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
