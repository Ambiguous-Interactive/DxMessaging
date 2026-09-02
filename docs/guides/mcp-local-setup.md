# MCP Local Setup

This page covers machine-local MCP configuration for a Linux devcontainer with a Unity relay running
on a Windows host.

## Why this is local-only

These files hold machine-specific host, port, and token values and are gitignored:

- `.mcp.json`
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

## 1. Start the bridge on the Windows host

See the [Unity MCP documentation](https://docs.unity3d.com/Packages/com.unity.ai.assistant@2.9/manual/integration/unity-mcp-get-started.html)
for installing the relay.

```powershell
npm run unity:mcp:bridge -- --project 'D:\Path\To\HostUnityProject'
```

The bridge finds the relay under `~/.unity/relay/`, generates a bearer token into `.env.local` if one
is not already set, and serves streamable HTTP on port `9020`. `GET /healthz` answers `200 ok`
without a token, so an orchestrator can use it as a liveness probe.

## 2. Configure and verify from the devcontainer

```bash
npm run unity:mcp:configure
npm run unity:mcp:probe
```

`configure` selects the first endpoint that completes MCP initialization, then writes every MCP
client config in one transaction. If no candidate completes initialization, it writes the explicitly
configured or default endpoint. `probe` follows `tools/list` pagination and requires a candidate to
advertise `Unity_RunCommand`. Both commands pin MCP `2025-11-25`. A successful probe does not execute
the tool or validate Unity's later heartbeat. A `not-ready` result means the required tool was not
advertised; wait for the editor to finish refreshing, then run the probe again.

Neither command needs a host or port supplied. Discovery tries `host.docker.internal`, `127.0.0.1`,
the `/etc/resolv.conf` nameserver (the Windows host under WSL2), and the default-route gateway,
against ports `9020` and `9003`.

`configure` writes the native schema for each client:

| Client                      | Machine-local file    |
| --------------------------- | --------------------- |
| Claude Code and Copilot CLI | `.mcp.json`           |
| Cursor                      | `.cursor/mcp.json`    |
| VS Code and Copilot Chat    | `.vscode/mcp.json`    |
| Codex                       | `.codex/config.toml`  |
| OpenCode                    | `opencode.jsonc`      |
| Nanocoder                   | `.nanocoder/mcp.json` |

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

Keep the repository's Node bridge as the default for this topology. Unity CLI is currently beta,
its live Editor/MCP pipeline requires Unity 6.0 or newer, and its client configurator does not cover
OpenCode or Nanocoder. This package supports Unity 2021.3, while the current relay also handles the
Windows-host-to-Linux-container boundary and explicit per-project port and token routing.

Unity CLI can still be installed and run on the Windows host for Unity 6 projects or project
management. A future pilot should run it host-side, retain one authenticated endpoint per editor,
and prove all six generated client configurations before replacing this bridge. See Unity's
[Unity CLI skill documentation](https://github.com/Unity-Technologies/skills/blob/main/skills/unity-cli/SKILL.md)
for the current requirements and supported clients.

## Troubleshooting

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
