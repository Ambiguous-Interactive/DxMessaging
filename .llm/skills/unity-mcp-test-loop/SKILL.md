---
name: unity-mcp-test-loop
description: "Running Unity EditMode and PlayMode tests locally from the Linux devcontainer against the host editor over the unity-mcp MCP server: the scripts/mcp/unity-mcp.mjs entry point and its npm run unity:mcp:bridge / :probe / :configure commands, endpoint discovery and bearer-token auth, the DxMcpTestRunner.Run bridge with its JSON result and .status sidecar, Pipeline and legacy relay tool selection, and using durationSeconds to measure suite speed. Use when running Unity tests locally, when the MCP endpoint is unreachable or unauthorized, when the DxMcpTestRunner bridge is missing, or when capturing a local perf baseline."
metadata:
  category: "unity"
  tags: "unity, testing, mcp, devcontainer, test-runner"
---

# Unity MCP Test Loop

Local Unity verification runs against the host editor through the `unity-mcp` MCP server, using Pipeline `eval` or legacy `Unity_RunCommand` after discovering the live catalog. The devcontainer ships no Unity build; there is no docker or ephemeral-editor local runner.

## When to use

- Iterating on `Runtime/` or `Editor/` code covered by tests under `Tests/Editor/` or `Tests/Runtime/`.
- Getting a fast local Mono/editor signal before pushing.
- Capturing a local perf baseline from the benchmark suite.
- Reproducing Unity behavior that `dotnet test` cannot exercise.
- The MCP endpoint fails to connect, rejects the token, or the `DxMcpTestRunner` type cannot be found.

Do not use it for source-generator or analyzer tests under `SourceGenerators/` (`dotnet test` directly), for documentation-only changes, or for the published IL2CPP-Release headline, which is a CI-only artifact from `scripts/unity/run-ci-tests.ps1`.

## Rules

### Tooling entry point

`scripts/mcp/unity-mcp.mjs` is the single entry point. The former shell scripts (`start-unity-mcp-bridge.ps1`, `configure-unity-mcp-endpoint.sh`, `probe-unity-mcp-endpoint.sh`, `install-claude-desktop-config.sh`) no longer exist; do not reference them.

| Command                       | Runs on      | Purpose                                                     |
| ----------------------------- | ------------ | ----------------------------------------------------------- |
| `npm run unity:mcp:bridge`    | Host         | Serve Unity CLI or the legacy relay over authenticated HTTP |
| `npm run unity:mcp:probe`     | Devcontainer | Find an endpoint a live editor is answering behind          |
| `npm run unity:mcp:configure` | Devcontainer | Discover, then write every MCP client config in the repo    |

- Start the host bridge with `npm run unity:mcp:bridge -- --project /absolute/host/project`.
  The host can run Windows, macOS, or Linux; use that host's absolute path, or set
  `UNITY_PROJECT_PATH` in `.env.local`. The default backend runs `unity mcp --project-path`.
  `--backend relay` retains Assistant compatibility and discovers the relay under `~/.unity/relay/`;
  override that executable with `--relay` or `UNITY_MCP_RELAY_PATH`.
- The bridge generates a bearer token in `.env.local` if none is set. Host and container must share
  `UNITY_MCP_BEARER_TOKEN`. Allow the bridge port through the host firewall when needed.
- Run `npm run unity:mcp:configure -- --offline` in the container, then `npm run unity:mcp:probe`.
  Offline configuration writes local files without contacting the host. Normal configure discovers
  initialized endpoints. Probe negotiates MCP `2025-11-25`, follows tool-list pages, and calls
  Pipeline `editor_status` or legacy `Unity_ManageEditor` to check the editor. A legacy registry
  advertising only `Unity_RunCommand` gets a tools-level verdict, which does not prove editor readiness.
  An initialized connection or empty catalog alone is insufficient.
- Discovery walks hosts in order - `host.docker.internal`, `127.0.0.1`, `nameserver` entries in
  `/etc/resolv.conf`, then default-route gateways - and ports `9020` then `9003`. An explicit
  `--host` or `--port` replaces the fallbacks on that axis. `--no-discover` uses only the configured
  host and port but still performs the protocol check.
- Failure statuses classify the fix: `unreachable` (nothing accepted TCP), `transport-error` (a
  request timed out or ended before an HTTP response), `unauthorized` (token rejected), `http-error`
  (an operation returned non-success HTTP), `jsonrpc-error` (valid server error), `malformed`
  (invalid media type, result, status, version, or cursor), and `not-ready` (required Unity tools or live editor state are missing). The SDK transport streams SSE, validates schemas, and uses one lifecycle deadline;
  a session-bearing HTTP 404 restarts initialization once without resetting that deadline.
- A session-bearing probe always attempts bounded `DELETE` cleanup and releases its response. HTTP
  405 is allowed; other cleanup failures warn without changing the readiness result.
- Configure writes seven private, gitignored client files transactionally, including Copilot CLI.
  It manages Unity, GitHub, local Git/Fetch, and optional Z.AI entries while preserving unrelated
  settings. See the [MCP setup guide](../../../scripts/mcp/README.md#generated-client-configs).
- The devcontainer runs offline configuration before initial attachment and on subsequent starts
  and attachments. Pair different host projects with distinct bridge ports and matching tokens.
- Local overrides go in `.env.local` or the matching flag: `UNITY_MCP_BRIDGE_HOST`, `UNITY_MCP_BRIDGE_PORT`, `UNITY_MCP_BRIDGE_PATH`, `UNITY_MCP_BEARER_TOKEN`, `UNITY_PROJECT_PATH`. `node scripts/mcp/unity-mcp.mjs --help` lists every flag.

### Topology

The devcontainer workspace is the same directory as the embedded package inside the host Unity project, so in-container edits are immediately visible to the host editor. Compilation and test execution happen in the host editor; the container only edits files and drives the editor over MCP.

### The loop

1. **Edit** files in the container.
1. **Discover tools.** Pipeline exposes `editor_status`, `list_open_scenes`, `eval`, and `menu`;
   the legacy relay exposes `Unity_*` tools. Read the connected schemas and use the
   [backend mapping](./references/mcp-test-loop.md#tool-selection) rather than guessing names.
1. **Preflight.** Prefer passive editor and scene queries plus a fresh observer snapshot for
   framework activity, editor flags, stage, and every scene's dirty flag. If the observer is
   incomplete, available flags are idle/clean, and no test is known active, follow the
   [bootstrap procedure](./references/mcp-test-loop.md#bootstrap-without-a-complete-passive-observer).
   Legacy `Unity_RunCommand` refreshes before snippets; Pipeline `eval` refresh behavior has not
   been established. Neither is a polling tool during an active run. Missing fields alone do not
   require confirmation. Preserve tool approval gates; wait for editor work and stop for dirty scenes.
1. **Compile** by executing `Assets/Refresh` through Pipeline `menu` or legacy
   `Unity_ManageMenuItem` after the safe preflight. Legacy `Unity_ValidateScript` accepts changed
   C# under `Assets/`, not embedded `Packages/`. Prove the changed assembly is loaded before
   trusting tests. Do not invoke `AssetDatabase.Refresh()` inside a code-evaluation snippet.
1. **Run** `DxMcpTestRunner.Run(testMode, assemblyNames, testNames, categoryNames, resultPath)`
   through Pipeline `eval` or legacy `Unity_RunCommand`, locating the type in `AppDomain`
   assemblies. Use the tool's own code/result schema. Arguments are semicolon-separated lists;
   `null` means no filter. Mode is `EditMode` or `PlayMode`; `testNames` accepts a full fixture name.
1. **Poll** the `.status` and `.cleanup.status` sidecars next to `resultPath` from bash.
   The first records raw result capture; only the second establishes passive cleanup.
   Retain the returned Execute GUID and its `.run.json` identity record. The JSON result
   includes counts, duration, and the full `nodes[]` tree with output and failures.
   Require both sidecars to report `done`, positive passes, no failed/inconclusive nodes,
   and matching GUID/path in the cleanup record before accepting a run.
1. **Wait for framework cleanup before another run or scene mutation.** `RunFinished` can write
   `done` before Unity restores its temporary scenes. Check the installed Test Framework's active
   job state and run-scoped framework errors through a passive host observer; poll files only
   during tests. Legacy `Unity_RunCommand` refreshes assets before executing the snippet.
   See the [framework cleanup gate](./references/mcp-test-loop.md#framework-cleanup-gate).
   A stale `running` sidecar alone is not a live job. Inspect framework state and errors before
   treating an observation timeout as completion or starting another run.

`resultPath` resolves relative to the HOST Unity project root, not the embedded package. To land somewhere the container can read, prefix it: `Packages/com.wallstop-studios.dxmessaging/.artifacts/unity-mcp/<name>.json`. A bare `.artifacts/unity-mcp/<name>.json` writes to the host project root, invisible to the container.

The bridge survives domain reloads via `[InitializeOnLoad]` plus `SessionState`, so a recompile mid-run does not lose the result.

The host editor belongs to the developer. Tests and probes that only need a temporary
`GameObject` must create it with
`EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave, ...)`; constructing
one normally dirties the active scene even when teardown destroys it. Tests that genuinely need
scene residency must use an isolated scene, close it without prompting, and restore the prior
active scene. Re-read scene dirtiness after the run.

### Assemblies

EditMode: `WallstopStudios.DxMessaging.Tests.Editor`, `...Tests.Editor.Allocations`, `...Tests.00.Editor.Benchmarks`. PlayMode: `...Tests.Runtime`, `...Tests.00.Runtime.Benchmarks` (category `PerfBench`), `...Tests.00.Runtime.Comparisons`, and the Reflex / VContainer / Zenject integrations. Keep choices consistent with `defaultIncludeAssemblies` in `scripts/unity/lib/asmdef-discovery.js`.

### Default Editor scope

For ordinary Editor verification, select `WallstopStudios.DxMessaging.Tests.Editor` and the
EditMode category exclusions in `.github/workflows/unity-tests.yml`. Run allocation and benchmark
assemblies separately. The bridge forwards semicolon-separated native filters, including `!`
exclusions. An assembly selection can run `[Explicit]` tests; explicitly exclude the documentation
PNG writer when testing the whole Editor assembly. See
[default Editor filters and imported samples](./references/mcp-test-loop.md#default-editor-filters-and-imported-samples).

### Sandbox restrictions

Pipeline `eval` and legacy `Unity_RunCommand` have separate implementations and schemas.
Do not assume legacy restrictions or output helpers apply to Pipeline. Respect either tool's gates.

- `using System.Reflection;` is rejected in `Unity_RunCommand`. Qualify allowed reflection types;
  qualification does not bypass member restrictions. Prefer public APIs when `BindingFlags` is rejected.
- Inside `DxMessaging.*` namespaces the bare identifier `Unity` binds to `DxMessaging.Unity`. Use a `global::`-qualified alias when that ambiguity bites.

### If the bridge is missing

The maintained source is `scripts/mcp/DxMcpTestRunner.cs.txt`. Its installed copy lives at
the host's `Assets/Editor/DxMcpTestRunner.cs`, so cleaning the host can remove it.
After the safe-state preflight, follow the
[maintained runner installation flow](../../../scripts/mcp/README.md#maintain-the-local-unity-test-runner).
Copy that source verbatim through supported MCP editing, back up existing source and metadata
outside `Assets`, preserve unreviewed host changes, and refresh through the connected backend's menu tool.
Verify the loaded assembly and fresh passive snapshots. Use the bootstrap procedure when the
missing observer leaves preflight fields unavailable; do not generate a replacement runner.

### Measuring suite speed

Baseline a mode, record `durationSeconds` and the pass/fail/skip counts, change ONE lever, re-run the SAME call, and diff. Keep a change only if pass counts hold and no flake appears across repeated runs. Two caveats: the host editor is warm, so frames are near-free and a structural win can show a near-zero local delta while paying off on the cold CI legs (per-mode under 3 minutes is a CI metric, so trust relative deltas locally); and an `Assets/Refresh` after a `.cs` edit forces one domain reload, so run twice back-to-back to exercise the true persistent-domain path - a latent reload dependency fails only on the second run.

### Perf baselines

The benchmark CSV defaults to `.artifacts/perf-baseline.csv`. Override with the `DX_PERF_BASELINE` env var and stamp the commit column with `DX_PERF_COMMIT`, setting both in-process via `System.Environment.SetEnvironmentVariable` BEFORE invoking the benchmark run, since the editor process is already up.

### CI versus local

CI calls `scripts/unity/run-ci-tests.ps1` on self-hosted Windows runners with a generated host project and classic-serial licensing. The MCP loop is local only, never runs in CI, and needs no Unity license secrets - the host editor supplies its own.

## References

| Document                                          | Purpose                                                                                                                                                                                   |
| ------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [mcp-test-loop.md](./references/mcp-test-loop.md) | The `DxMcpTestRunner.Run` bridge contract, result and `.status` sidecar polling, test assemblies per mode, sandbox restrictions, bridge regeneration, and the speed-measurement protocol. |
