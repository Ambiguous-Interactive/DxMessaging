# MCP Local Setup

This page covers machine-local MCP configuration on the host or in a Linux devcontainer.
The Unity Editor and its bridge run on a Windows, macOS, or Linux host.

## Why this is local-only

These files hold machine-specific host, port, and token values and are gitignored:

- `.mcp.json`
- `.copilot/mcp-config.json`
- `.cursor/mcp.json`
- `.vscode/mcp.json`
- `.codex/config.toml`
- `opencode.jsonc`
- `.nanocoder/mcp.json`
- `.env.local`

Do not commit them.

The devcontainer runs configuration during creation and again in the background on every start.
Codex, Claude Code, Copilot CLI, Copilot Chat, VS Code, OpenCode, Nanocoder, and Cursor therefore
share the same Unity endpoint without manual client setup. The configurator also adds GitHub's hosted
MCP server. It preserves unrelated servers and sets every generated config to mode `0600` because
the files can contain bearer tokens.

## 1. Start the bridge on the host

Install the Unity CLI and load Pipeline in the intended editor, following the
[host preparation instructions](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/mcp/README.md#prepare-the-host).

```powershell
npm run unity:mcp:bridge -- --project 'D:\Path\To\HostUnityProject'
```

Use an absolute host path on macOS/Linux, or set `UNITY_PROJECT_PATH` in `.env.local` and omit
`--project`. `npm run unity:mcp:run` is an alias for `unity:mcp:bridge`.
The bridge starts `unity mcp --project-path` for each session, generates a bearer token into
`.env.local` if one is not already set, and serves streamable HTTP on port `9020` by default.
`--backend relay` selects the legacy Assistant relay under `~/.unity/relay/`.
`GET /healthz` answers `200 ok`
without a token, so an orchestrator can use it as a liveness probe.

The command reports `Unity MCP ready` only after a live editor status tool answers.
It then stays in the foreground, waiting for clients; this is expected. Keep that
host terminal open and run configure and probe in a separate devcontainer terminal.
Ctrl-C stops the bridge. An HTTP listener banner alone proves no editor readiness.
Starting the bridge inside the devcontainer is rejected because the editor and its
CLI discovery records belong to the host.

## 2. Configure and verify from the host or devcontainer

```bash
npm run unity:mcp:configure
npm run unity:mcp:probe
```

`configure` selects the first endpoint that completes MCP initialization, then writes every MCP
client config in one transaction. If every candidate is unreachable, it writes the configured or
default endpoint for initial setup. If a listener answers TCP but fails initialization, configure
exits without writing configs or credentials. `--offline` explicitly writes without probing.
Configure does not start a bridge. `probe` follows `tools/list` pagination and calls Pipeline
`editor_status` or legacy `Unity_ManageEditor` to verify editor readiness. A registry with only
`Unity_RunCommand` receives a tools-level verdict. Both commands pin MCP `2025-11-25`.

Neither command needs a host or port supplied. Host commands default to `127.0.0.1`; containers
default to `host.docker.internal`. Discovery tries the selected host first, then the other host,
the `/etc/resolv.conf` nameserver (the Windows host under WSL2), and the default-route gateway,
against ports `9020` and `9003`.

`configure` writes the native schema for each client:

| Client                   | Machine-local file         |
| ------------------------ | -------------------------- |
| Claude Code              | `.mcp.json`                |
| Copilot CLI              | `.copilot/mcp-config.json` |
| Cursor                   | `.cursor/mcp.json`         |
| VS Code and Copilot Chat | `.vscode/mcp.json`         |
| Codex                    | `.codex/config.toml`       |
| OpenCode                 | `opencode.jsonc`           |
| Nanocoder                | `.nanocoder/mcp.json`      |

The devcontainer sets `NANOCODER_MCPSERVERS_FILE` to the Nanocoder file. This avoids making
Nanocoder consume Claude's different HTTP schema.

## GitHub MCP authentication

The generated `github` entry points to GitHub's hosted MCP server. The devcontainer forwards the
first available value from `GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_PERSONAL_ACCESS_TOKEN`, or
`GITHUB_PAT`. The configurator writes that value as the bearer token for clients that need explicit
headers. Clients with GitHub OAuth support can authenticate interactively when no token is present.

Keep tokens outside tracked files. A token used with GitHub MCP should have only the permissions
needed for the intended tools. See the
[GitHub MCP server documentation](https://github.com/github/github-mcp-server) for authentication
and toolset options.

## 3. Override when discovery is not enough

Set values in `.env.local` at the repository root:

```bash
UNITY_MCP_BRIDGE_HOST=192.168.1.33
UNITY_MCP_BRIDGE_PORT=9020
UNITY_MCP_BEARER_TOKEN=<64 hex characters>
```

If host and container share the generated config files, run configure in the
container so local tool paths match that environment. Add
`UNITY_MCP_CLIENT_HOST=host.docker.internal` to their shared `.env.local` to keep
the Unity client address stable when configure is run from the host. The matching
`--client-host` flag changes the address written to clients without changing the
address used for discovery. Reconnect existing MCP clients after configuration.

An explicitly configured host or port replaces the fallback list on that axis instead of being tried
ahead of it, so discovery never reaches past a deliberate setting. `--host X` probes only `X`, and
`--host X --port Y` probes exactly one endpoint.

Windows paths need no special quoting, and a quoted one may end in a backslash
(`UNITY_PROJECT_PATH="D:\Program Files\Proj\"`). A line these commands cannot parse is warned about
and skipped, so unrelated entries in a shared `.env.local` cannot break them.

## Multiple editors and devcontainers

Give each host project and devcontainer pair its own port, token, and `.env.local`. For example:

```text
Project A: port 9020, token A, host path D:\Work\ProjectA
Project B: port 9021, token B, host path D:\Work\ProjectB
```

Start one bridge from each host checkout:

```powershell
# Project A checkout
npm run unity:mcp:bridge -- --project 'D:\Work\ProjectA' --port 9020

# Project B checkout
npm run unity:mcp:bridge -- --project 'D:\Work\ProjectB' --port 9021
```

Set the matching port and token in each checkout's `.env.local`. Each devcontainer then configures
only its own explicit endpoint. Do not share one `.env.local` across projects. The bridge rejects a
port already in use, which catches accidental cross-project reuse before a client is configured.

## Unity CLI compatibility

The Node bridge defaults to the Unity CLI backend and retains `--backend relay` for Assistant.
It pins each session to the selected host project and supplies authenticated HTTP to all seven
configured clients. See the [migration guide](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/mcp/README.md#unity-cli-migration)
for backend preparation and verification.

## Troubleshooting

### Initialization times out while a port is listening

Check the port owner with `lsof -nP -iTCP:<port> -sTCP:LISTEN` on macOS/Linux or
`Get-NetTCPConnection` on Windows. A stale VS Code port forward can accept TCP without
answering HTTP. Remove the forward from VS Code's Ports view or choose a free
`UNITY_MCP_BRIDGE_PORT` in `.env.local`, restart the host bridge, and run configure and probe.
Do not forward the host bridge port from the container. On macOS, wildcard and loopback
listeners can coexist; bridge startup now rejects that collision before reporting success.

### Repeating audio lock assertion

Close the Unity Editor if its Console continually prints
`Access version should be odd when acquiring lock`. Unity tracks that assertion as
[UUM-146734](https://issuetracker.unity.com/issues/23329/crash-on-assertimplementation-when-audio-dual-thread-lock-version-is-even-on-acquire)
in `audio::DualThreadManager::ControlUpdate` and reports that the loop can exhaust editor memory.
The native assertion is distinct from the MCP probe's endpoint and bearer-token diagnoses. MCP
activity may still trigger the Unity defect.

Reopen Unity on the latest available patch and follow the Unity issue for fixed-version status. Run
`npm run unity:mcp:probe` separately after the editor restarts; its `unreachable`, `unauthorized`,
`transport-error`, `http-error`, `jsonrpc-error`, `malformed`, or `not-ready` result separates TCP,
MCP transport, server-reported, protocol, tool-advertisement, and editor-readiness failures. The
probe finishes by asking `Unity_ManageEditor` for editor state: the bridge stays healthy while the
editor's discovery record goes stale, and a `not-ready` naming that tool means the transport is fine
and the editor is not answering.

## Notes

- The host and the container must present the same `UNITY_MCP_BEARER_TOKEN`. When they do not share
  `.env.local`, copy the value across or pass `--token`.
- A probe that reports `unauthorized` means a bridge is running but the token does not match, which
  is a different fix from `unreachable`. `configure` refuses to write anything in that case, because
  a fresh generated token would be guaranteed wrong; copy the host's token or pass `--token` first.
- `--timeout` is one deadline for the MCP lifecycle, including every `tools/list` page. Session
  cleanup uses a separate bounded request. HTTP 405 is allowed; other cleanup failures are warnings.
  A session-bearing HTTP 404 restarts initialization once without resetting the lifecycle deadline.
- `configure` owns the whole `unity-mcp` entry in each file, including the entire
  `[mcp_servers.unity-mcp]` table in `.codex/config.toml`. Keys added inside that table are dropped
  on the next run.
- See [scripts/mcp/README.md](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/mcp/README.md)
  for the full command reference.
