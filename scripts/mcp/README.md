# Agent MCP setup and host Unity tooling

The devcontainer configures Codex, Claude Code, Copilot CLI, VS Code Copilot Chat,
OpenCode, Nanocoder, and Cursor. The same Node entry point publishes the host Unity
MCP server over authenticated HTTP. Unity stays on the host.

## Container startup

Rebuild the container to install the updated image. The image includes Codex,
OpenCode, and Nanocoder from npm's `latest` tags. Creation, startup, and VS Code
attachment check those tags again in the background. Existing binaries remain
available during a refresh or registry outage. Check `/tmp/dxm-agent-cli-refresh.log`
for the installed versions or a failed update. A cached Docker layer can contain an
older CLI; the lifecycle refresh handles that case.

Before the initial attach, `updateContentCommand` repairs cache ownership and writes
MCP configs without network access. The image includes the configuration dependencies,
so this works while workspace dependencies are still installing. Login-shell probing
is disabled, generated trees are excluded from file watching, and the Docker build
context contains only image inputs. `.env` files never enter the build context.

Both local `npm install` and global `npm install -g` run as `vscode`. Global installs
use `/home/vscode/.local`, which comes first on PATH. Permission repair checks nested
cache files left by earlier `sudo npm` commands, the npm prefix, and npm's manifest,
lock, and config files. It does not recursively change the host checkout. A host
checkout that is itself read-only must be repaired on the host.

## Credentials

Create `.env.local` at the repository root. Values from the process environment take
precedence over values in this file; empty forwarded variables do not hide file values.
The file is parsed as data, never sourced as shell code. For example:

```dotenv
GITHUB_PERSONAL_ACCESS_TOKEN=your_github_token
Z_AI_API_KEY=your_zai_key
UNITY_PROJECT_PATH=/absolute/host/project/path
UNITY_MCP_BRIDGE_PORT=9020
```

`GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_PERSONAL_ACCESS_TOKEN`, and `GITHUB_PAT` are supported,
in that order. Z.AI accepts `Z_AI_API_KEY` or `ZAI_API_KEY`. The configurator adds all
four Z.AI servers when a key is present and removes its Z.AI entries when the key is
removed. Available tools still depend on the account's access and quota.

After editing credentials while an agent is open, run `npm run unity:mcp:configure -- --offline`
and restart its MCP connections. Reattaching VS Code also regenerates configuration.
Generated configs are gitignored and use mode `0600`; they contain credentials.
Unrelated server definitions and settings survive. JSONC and TOML are parsed and
serialized, so comments and formatting are not retained. Malformed config files are
rejected before any client config is replaced.

| Server             | Source                                                                                  | Credential                                                    |
| ------------------ | --------------------------------------------------------------------------------------- | ------------------------------------------------------------- |
| `github`           | [GitHub hosted MCP](https://github.com/github/github-mcp-server)                        | GitHub token, or interactive OAuth in clients that support it |
| `web-search-prime` | [Z.AI Web Search](https://docs.z.ai/devpack/mcp/search-mcp-server)                      | Z.AI key                                                      |
| `web-reader`       | [Z.AI Web Reader](https://docs.z.ai/devpack/mcp/reader-mcp-server)                      | Z.AI key                                                      |
| `zread`            | [Z.AI Zread](https://docs.z.ai/devpack/mcp/zread-mcp-server)                            | Z.AI key                                                      |
| `zai-mcp-server`   | [Z.AI Vision](https://docs.z.ai/devpack/mcp/vision-mcp-server)                          | Z.AI key                                                      |
| `git`              | [MCP Git server](https://github.com/modelcontextprotocol/servers/tree/main/src/git)     | Local repository access                                       |
| `fetch`            | [MCP Fetch server](https://github.com/modelcontextprotocol/servers/tree/main/src/fetch) | None                                                          |
| `unity-mcp`        | Host bridge in this repository                                                          | Shared bridge token                                           |

Git, Fetch, and Vision executables are installed in the image. MCP startup does not
need to download them. Client trust prompts and account sign-in remain client-managed;
the container does not auto-approve tool calls.

Codex connects to the three remote Z.AI servers through the image-installed
`mcp-remote` adapter. Z.AI returns an empty HTTP 200 without Content-Type for
initialized notifications; Codex's HTTP client rejects that response. The adapter
handles it and supplies the header from an environment variable, keeping the key
out of process arguments. Other clients use the direct HTTP endpoints. After a
configuration repair, reconnect MCP servers or restart the client to reload them.

## Generated client configs

| Client                   | File                       | Schema key    |
| ------------------------ | -------------------------- | ------------- |
| Claude Code              | `.mcp.json`                | `mcpServers`  |
| Copilot CLI              | `.copilot/mcp-config.json` | `mcpServers`  |
| Cursor                   | `.cursor/mcp.json`         | `mcpServers`  |
| VS Code and Copilot Chat | `.vscode/mcp.json`         | `servers`     |
| Codex CLI and extension  | `.codex/config.toml`       | `mcp_servers` |
| OpenCode                 | `opencode.jsonc`           | `mcp`         |
| Nanocoder                | `.nanocoder/mcp.json`      | `mcpServers`  |

The seven files are written as one transaction, with rollback if a write fails.
The devcontainer sets `COPILOT_HOME` to its dedicated `.copilot` directory, including
explicit tool selections. This also supports CLI versions whose `mcp list` command
reads only the user configuration. Nanocoder's `NANOCODER_MCPSERVERS_FILE` selects
its dedicated transport schema.
OpenCode gets local command arrays and remote entries in its own schema.
Codex uses project configuration once the repository is trusted. See the
[Codex MCP documentation](https://developers.openai.com/codex/mcp),
[Copilot CLI MCP documentation](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers),
[OpenCode MCP documentation](https://opencode.ai/docs/mcp-servers/), and
[Nanocoder MCP documentation](https://github.com/Nano-Collective/nanocoder/blob/main/docs/configuration/mcp-configuration.md).

## Unity CLI migration

Unity now provides `unity mcp` and the `com.unity.pipeline` package. The CLI selects
a running editor by `--project-path`, including when several editors are open.
It reads that editor's discovery record and authentication token on the host.
The container does not need access to the host's private discovery files.
See [Unity's CLI guide](https://docs.unity.com/en-us/unity-cli/use-unity-cli) and
[Unity's editor selection and MCP reference](https://github.com/Unity-Technologies/skills/blob/main/skills/unity-cli/references/integration-advanced.md).

The transport was checked on 2026-09-07 with the published Unity CLI
`1.0.0-beta.8`: initialization through this HTTP bridge negotiated MCP `2025-11-25`.
The registry listed Pipeline `0.6.0-exp.1`, requiring Unity `6000.0` or newer.
The CLI and package still carry beta and experimental version labels. An empty
CLI tool list correctly fails the readiness probe. A live host editor cutover
must also pass the checks below before Assistant is removed.

### Prepare the host

Install the [official Unity CLI](https://docs.unity.com/en-us/unity-cli/use-unity-cli)
on the host, using a release that provides `unity mcp`. With the intended project
open and its work saved, run these commands in a host terminal:

```bash
unity --version
unity pipeline install --project-path /absolute/host/project/path
unity list --project-path /absolute/host/project/path --format json
unity command editor_status --project-path /absolute/host/project/path --format json
```

Use the corresponding Windows path when the host is Windows. Install host Node.js
and run `npm install` in the host checkout if its bridge dependencies are absent.
The container's Linux `node_modules` volume is separate from host dependencies.

Keep Assistant installed until Pipeline answers for the intended editor. Then start
the bridge, which defaults to the CLI backend:

```bash
npm run unity:mcp:bridge -- --project /absolute/host/project/path
```

Alternatively set `UNITY_PROJECT_PATH` in `.env.local` and omit `--project`.
`UNITY_CLI_PATH` or `--cli` can select a CLI executable outside host PATH. The bridge
starts each session as `unity mcp --project-path <selected-project>` with that project
as its working directory. It never chooses an arbitrary running editor.

Verify from the container:

```bash
npm run unity:mcp:probe
```

The Pipeline probe calls the read-only `editor_status` tool. Check the host project
identity, then exercise the test runner through Pipeline's `eval` command before
removing `com.unity.ai.assistant` from that host project's Package Manager. This
repository does not declare Assistant as a package dependency. Removing it from the
host has not been automated: transport verification alone does not prove editor
command and test-runner compatibility.

For rollback, run the bridge with `--backend relay` or set `UNITY_MCP_BACKEND=relay`.
The relay backend discovers Assistant under `~/.unity/relay/`; `--relay` or
`UNITY_MCP_RELAY_PATH` overrides its path.

An initialized connection does not prove the Editor provides tools. If the selected
project has no running Pipeline server, the CLI can return an empty tool registry.
The bridge reports this as an error. Run `npm run unity:mcp:probe` from the container
and `unity list --project-path <host-project> --format json` on the host. Confirm
Pipeline has loaded in that Editor before using the CLI backend. An installed
package on disk alone is insufficient; the Assistant relay remains available with
`--backend relay --relay <relay-executable>` while completing the migration.

If Pipeline is installed but absent from `unity status`, check compilation before
changing the project path. Assistant 2.9.0-pre.2 can override Pipeline 0.6.0-exp.1's
`System.Reflection.Metadata.dll` with an explicitly referenced copy. Pipeline's
interpreter then fails with CS0234/CS0246 errors and its server never loads. In a
project containing both packages, add `EXCLUDE_REFLECTION_METADATA` to the active
target's Scripting Define Symbols. Assistant's importer supports this exclusion,
so Pipeline supplies the shared assembly. Recompile and verify `unity list` and
the container readiness probe before selecting `UNITY_MCP_BACKEND=cli`.
Legacy probes use `Unity_ManageEditor` and
`Unity_RunCommand`. Pipeline exposes its own command names; agents must discover
the live catalog instead of assuming the legacy names still exist.

### Host lifetime, multiple projects, and connectivity

Keep the bridge running under the host user's service manager (Task Scheduler on
Windows, launchd on macOS, or systemd on Linux) if it must survive terminal closure
or host restarts. Its command is host `node` plus the absolute path to
`scripts/mcp/unity-mcp.mjs bridge`, with the chosen project in `.env.local`.
Container attachment configures clients; it cannot start a process on another OS.

Run one bridge per selected project on a distinct port. Give each checkout its own
`UNITY_PROJECT_PATH` and `UNITY_MCP_BRIDGE_PORT`. The same `unity-mcp` name in each
checkout then reaches that checkout's editor. Avoid sharing a port between projects.

The bridge generates `UNITY_MCP_BEARER_TOKEN` in `.env.local` if absent. Host and
container must share that file or use matching token values. The bridge listens on
`0.0.0.0:9020` by default; allow the container network through the host firewall.
The devcontainer maps `host.docker.internal` to the host gateway on Linux too.
Use `UNITY_MCP_BRIDGE_HOST`, `--host`, `--port`, and `--path` to override routing.

`configure --offline` writes only local files. Normal `configure` probes candidate
endpoints and refuses to replace credentials if a running bridge rejects the token.
Explicit host and port settings restrict discovery; defaults try ports `9020` and
`9003` with Docker, loopback, DNS, and gateway candidates. `--no-discover` probes only
the configured endpoint. `probe` checks editor readiness as well as initialization.

The bridge caps sessions at eight and authenticates every MCP request. Idle sessions
expire after 60 seconds, active requests after 300 seconds. `--max-sessions`,
`--session-timeout`, and `--request-timeout` adjust those limits. Bodies over 1 MiB
receive `413`, stalled bodies receive `408`, bad tokens receive `401`, and excess
sessions receive `503`. `GET /healthz` is an unauthenticated liveness check.

## Maintain the local Unity test runner

The HTTP bridge above transports requests through either host backend. The maintained Editor test runner is
[DxMcpTestRunner.cs.txt](./DxMcpTestRunner.cs.txt). Copy that file verbatim to the host project's
`Assets/Editor/DxMcpTestRunner.cs`; the `.txt` source stays outside package compilation and CI
test execution. The same source is compiled and exercised by `.docs-tests/UnityMcpBridgeTests.cs`.

Discover the live tools before installing or replacing the runner. Pipeline provides
`editor_status`, `list_open_scenes`, `eval`, and `menu`; the legacy relay provides `Unity_*`
tools. Prefer passive editor/scene queries and a fresh observer snapshot for framework
inactivity, idle editor flags, the main stage, and every loaded scene's saved, clean state.
If the observer is incomplete, available flags are idle/clean, and no test is known active,
use the [bootstrap procedure](../../.llm/skills/unity-mcp-test-loop/references/mcp-test-loop.md#bootstrap-without-a-complete-passive-observer)
to inspect framework activity, every open scene, and ownership keys. Legacy `Unity_RunCommand`
refreshes before snippets, so its bootstrap cannot prove that preceding refresh safe.
Pipeline `eval` refresh behavior has not been established. Missing fields alone do not require
user confirmation. Poll active tests through files on either backend.

After inspection confirms inactivity and saved, clean scenes, preserve any existing runner source
and metadata outside `Assets`, review local changes, and copy the maintained source through
supported MCP editing. Keep existing `.meta` identity when replacing an owned script. Validate
with the available backend tools, refresh through Pipeline `menu` or legacy `Unity_ManageMenuItem`
with `Assets/Refresh`, then
verify the loaded assembly, fresh passive snapshots, and unchanged scenes. Respect tool approval
gates; do not bypass a rejected operation through another transport. Remove an old
`DxMcpObservedTestRunner` only after its ownership scope is empty and its source is backed up.

Inspect the existing `DxMcpTestRunner.ResultPath`, `DxMcpTestRunner.OwnedResultPath`, and
`DxMcpObservedTestRunner.ResultPath` SessionState keys before replacement. Let an active run
finish through its installed runner. If a legacy run is terminal and cannot clear its ownership,
preserve its artifacts and record passive framework inactivity plus saved, clean scene state.
Retire only the identified legacy key through the reviewed host recovery flow after checking
that evidence; never erase a key merely to force a new run. The maintained callbacks require
matching result and owner paths, so they cannot overwrite evidence left by an older runner or
inconsistent ownership metadata.

The runner writes a passive snapshot once per second to
`Packages/com.wallstop-studios.dxmessaging/.artifacts/unity-mcp/editor-state.json`. Require a fresh
`observedUtc`, no `observationError`, inactive framework/editor flags, `mainStage: true`, an empty
`resultPath`, `ownedResultPath`, and `legacyObserverResultPath`, and saved, clean scenes before
the next refresh or run. Compare the host clock when
checking freshness. A missing, stale, or temporarily malformed snapshot proves no state. During
a known or owned run, keep polling its files without invoking an asset-refreshing tool; never
launch another test because observation timed out. Outside an active run, use the bootstrap
procedure above if the observer is absent or incomplete.

Invoke `DxMcpTestRunner.Run(mode, assemblies, tests, categories, resultPath)` through Pipeline
`eval` or legacy `Unity_RunCommand`. It returns the exact GUID from
`TestRunnerApi.Execute`. Filters use semicolon-separated strings. Use a fresh result path under
`Packages/com.wallstop-studios.dxmessaging/.artifacts/unity-mcp/`; existing result or companion
files are refused. The runner owns that path until passive framework cleanup ends.

| Artifact                                        | Meaning                                                                             |
| ----------------------------------------------- | ----------------------------------------------------------------------------------- |
| `resultPath.run.json`                           | Exact Execute GUID and absolute result path; joins every companion to one job       |
| `resultPath` and `.status`                      | Raw RunFinished result, including every result-tree node, output, failure and count |
| `resultPath.errors.log`                         | Framework and result-writer errors, including errors after RunFinished              |
| `resultPath.cleanup.json` and `.cleanup.status` | Passive terminal outcome, GUID, retained errors and restored-scene evidence         |

A raw `.status` of `done` means results are available. Accept success only when `.cleanup.status`
is also `done`, GUID/path companions agree, and the result has positive passes with zero failures
and inconclusive cases. Keep skips visible. Unity can mark a suite `Skipped` when it contains
both passing and ignored tests; failed or inconclusive suites still prevent acceptance.
Cleanup checks that the original scene paths, loaded
states and active scene were restored. A framework failure without RunFinished becomes an `error:`
outcome after framework inactivity is proven. An `observation-error:` keeps ownership pending;
continue observing the same job. Storage failures retain errors in SessionState and later copy
them into the cleanup record when storage recovers.

Validate an installation with consecutive focused runs and a deliberately failed framework setup.
Retain each GUID, complete result and terminal cleanup record, and verify the original scene setup
after each attempt. No waits, retries or extra Unity launches are added to CI by this local runner.

## Local overrides

Set any of these in `.env.local` at the repository root, or pass the matching flag:

```bash
UNITY_MCP_BRIDGE_HOST=192.168.1.33
UNITY_MCP_BRIDGE_PORT=9020
UNITY_MCP_BRIDGE_PATH=/mcp
UNITY_MCP_BEARER_TOKEN=<64 hex characters>
UNITY_PROJECT_PATH=D:\Path\To\HostUnityProject
UNITY_MCP_MAX_SESSIONS=8
```

Quoted values follow shell rules: `"D:\Path\To\Proj\"` keeps its trailing backslash, `\"` and `\\`
are unescaped inside double quotes, and single-quoted values are literal. `.env.local` is shared with
other tooling, so a line this parser cannot read is warned about and skipped instead of aborting the
command.

Run `node scripts/mcp/unity-mcp.mjs --help` for the full flag list.
