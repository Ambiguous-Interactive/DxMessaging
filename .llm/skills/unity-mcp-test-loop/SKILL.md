---
name: unity-mcp-test-loop
description: "Running Unity EditMode and PlayMode tests locally from the Linux devcontainer against the Windows host editor over the unity-mcp MCP server: the scripts/mcp/unity-mcp.mjs entry point and its npm run unity:mcp:bridge / :probe / :configure commands, endpoint discovery and bearer-token auth, the DxMcpTestRunner.Run bridge with its JSON result and .status sidecar, Unity_RunCommand sandbox restrictions, and using durationSeconds to measure suite speed. Use when running Unity tests locally, when the MCP endpoint is unreachable or unauthorized, when the DxMcpTestRunner bridge is missing, or when capturing a local perf baseline."
metadata:
  category: "unity"
  tags: "unity, testing, mcp, devcontainer, test-runner"
---

# Unity MCP Test Loop

Local Unity verification runs against the host editor through the `unity-mcp` MCP server, driven by `Unity_RunCommand`. The devcontainer ships no Unity build; there is no docker or ephemeral-editor local runner.

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

| Command                       | Runs on      | Purpose                                                  |
| ----------------------------- | ------------ | -------------------------------------------------------- |
| `npm run unity:mcp:bridge`    | Windows host | Spawn the relay and serve it over authenticated HTTP     |
| `npm run unity:mcp:probe`     | Devcontainer | Find an endpoint a live editor is answering behind       |
| `npm run unity:mcp:configure` | Devcontainer | Discover, then write every MCP client config in the repo |

- Start the bridge on the host with `npm run unity:mcp:bridge -- --project 'D:\Path\To\HostUnityProject'`. `--project` is required for this command only and names a HOST filesystem path. The relay is discovered under `~/.unity/relay/`; override with `--relay <path>` or `UNITY_MCP_RELAY_PATH`.
- The bridge requires a bearer token and generates one into `.env.local` at the repository root if none is set. Both sides must present the same `UNITY_MCP_BEARER_TOKEN`; copy it across or pass `--token` when host and container do not share `.env.local`. Add a Windows firewall rule for the port if the container cannot reach it.
- From the container run `npm run unity:mcp:configure` then `npm run unity:mcp:probe`. Discovery pins
  MCP `2025-11-25`. Configure selects the first initialized endpoint without inspecting tools, or
  writes the configured/default endpoint if none initializes. Probe follows `tools/list` pages,
  requires `Unity_RunCommand`, and then asks `Unity_ManageEditor` for editor state, because the
  relay keeps advertising its whole registry after the editor's discovery record goes stale and a
  registry read alone reports green through a window where nothing editor-backed works (#418). A
  relay that advertises no editor tool keeps the tools-level verdict and the probe says so.
- Discovery walks hosts in order - `host.docker.internal`, `127.0.0.1`, `nameserver` entries in
  `/etc/resolv.conf`, then default-route gateways - and ports `9020` then `9003`. An explicit
  `--host` or `--port` replaces the fallbacks on that axis. `--no-discover` uses only the configured
  host and port but still performs the protocol check.
- Failure statuses classify the fix: `unreachable` (nothing accepted TCP), `transport-error` (a
  request timed out or ended before an HTTP response), `unauthorized` (token rejected), `http-error`
  (an operation returned non-success HTTP), `jsonrpc-error` (valid server error), `malformed`
  (invalid media type, result, status, version, or cursor), and `not-ready` (`Unity_RunCommand` was
  not advertised). The SDK transport streams SSE, validates schemas, and uses one lifecycle deadline;
  a session-bearing HTTP 404 restarts initialization once without resetting that deadline.
- A session-bearing probe always attempts bounded `DELETE` cleanup and releases its response. HTTP
  405 is allowed; other cleanup failures warn without changing the readiness result.
- `configure` writes `.mcp.json` (`mcpServers`), `.cursor/mcp.json` (`mcpServers`), `.vscode/mcp.json` (`servers`), and `.codex/config.toml` (`mcp_servers`) in one transaction with rollback. All four are machine-local and gitignored; only the `unity-mcp` entry is rewritten and other servers are preserved.
- Local overrides go in `.env.local` or the matching flag: `UNITY_MCP_BRIDGE_HOST`, `UNITY_MCP_BRIDGE_PORT`, `UNITY_MCP_BRIDGE_PATH`, `UNITY_MCP_BEARER_TOKEN`, `UNITY_PROJECT_PATH`. `node scripts/mcp/unity-mcp.mjs --help` lists every flag.

### Topology

The devcontainer workspace is the same directory as the embedded package inside the host Unity project, so in-container edits are immediately visible to the host editor. Compilation and test execution happen in the host editor; the container only edits files and drives the editor over MCP.

### The loop

1. **Edit** files in the container.
1. **Preflight the shared editor before any refresh or test.** Read `Unity_ManageEditor GetState`,
   then use a read-only `Unity_RunCommand` to inspect every open scene's `isDirty`, confirm the
   current stage is the main stage, and confirm the editor is not playing, compiling, or updating.
   If any scene is dirty, a prefab stage is open, or the editor is busy, do not refresh or change
   scenes. Wait or report the unsafe state; never invoke an API that can raise a save prompt.
1. **Compile** with `Unity_ValidateScript` for changed C# under `Assets/`, then execute the
   `Assets/Refresh` menu item through `Unity_ManageMenuItem`. The validator rejects embedded
   `Packages/` paths, so package edits must use the refresh plus fresh-assembly proof. Wait for
   compilation to settle before trusting tests. Do not use `AssetDatabase.Refresh()` through
   `Unity_RunCommand`; a modal prompt blocks the editor and only the developer can dismiss it.
1. **Run** `DxMcpTestRunner.Run(testMode, assemblyNames, testNames, categoryNames, resultPath)` through `Unity_RunCommand`, locating the type by scanning `AppDomain` assemblies. Arguments are semicolon-separated lists and `null` means no filter. `testMode` is `EditMode` or `PlayMode`. `testNames` accepts a full fixture type name such as `DxMessaging.Tests.Runtime.Core.TestAttributeContractTests` for a single-fixture red-green loop.
1. **Poll** the `.status` sidecar next to `resultPath` from bash. It moves `running` to `done` or `error: <message>`. The JSON result carries `{ passCount, failCount, skipCount, inconclusiveCount, durationSeconds, failures[] }`.

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

### Sandbox restrictions

- `using System.Reflection;` is REJECTED in `Unity_RunCommand` snippets. Fully qualify instead: `System.Reflection.Assembly`, `System.Reflection.BindingFlags`.
- Inside `DxMessaging.*` namespaces the bare identifier `Unity` binds to `DxMessaging.Unity`. Use a `global::`-qualified alias when that ambiguity bites.

### If the bridge is missing

`DxMcpTestRunner` lives in the host project under its `Assets/Editor/`, not in this repo, so cleaning the host project drops it. After the safe-state preflight, regenerate it through `Unity_RunCommand` with `System.IO.File.WriteAllText` of the bridge source, then execute `Assets/Refresh` through `Unity_ManageMenuItem`. It wraps `TestRunnerApi` and writes the JSON result plus the `.status` sidecar.

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
