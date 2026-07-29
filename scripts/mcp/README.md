# Unity MCP in a Linux devcontainer with a Windows host

Unity and its relay binary run on the Windows host; agents run inside the Linux devcontainer. The
relay speaks stdio, which a container cannot reach, so the host publishes it over authenticated
streamable HTTP and container clients point at that endpoint.

`scripts/mcp/unity-mcp.mjs` is the single entry point for all three steps.

| Command                       | Runs on | Purpose                                                   |
| ----------------------------- | ------- | --------------------------------------------------------- |
| `npm run unity:mcp:bridge`    | Host    | Spawn the relay and serve it over HTTP                    |
| `npm run unity:mcp:probe`     | Agent   | Discover a live endpoint and complete an MCP handshake    |
| `npm run unity:mcp:configure` | Agent   | Discover, then write every MCP client config in this repo |

## Start the bridge on the Windows host

```powershell
npm run unity:mcp:bridge -- --project 'D:\Path\To\HostUnityProject'
```

The relay executable is discovered under `~/.unity/relay/`. Override it with `--relay <path>` or
`UNITY_MCP_RELAY_PATH` when it lives elsewhere. `--project` is required for this command only: the
relay opens that Unity project, and the path names a host filesystem location.

The bridge requires a bearer token. If none is configured it generates one and appends it to
`.env.local` at the repository root. Both sides must present the same token, so when the host and
the container do not share `.env.local`, copy `UNITY_MCP_BEARER_TOKEN` across or pass `--token`.

Add a Windows firewall rule for the chosen port if the container cannot reach it.

## Configure and verify from the devcontainer

```bash
npm run unity:mcp:configure
npm run unity:mcp:probe
```

Both commands probe before acting. Discovery walks every combination of candidate host and port
until one completes an MCP `initialize` handshake:

- **Hosts** - any explicitly configured host first, then `host.docker.internal`, `127.0.0.1`, the
  `nameserver` entries in `/etc/resolv.conf` (the Windows host under WSL2), and the default-route
  gateways in `/proc/net/route`.
- **Ports** - any explicitly configured port first, then `9020`, then `9003`.

An explicit `--host` or `--port` is always tried first, so discovery can never override a deliberate
setting. Pass `--no-discover` to skip probing entirely and use the configured values as-is.

Failed attempts are reported with a classification, because the fixes differ:

| Status         | Meaning                                                        |
| -------------- | -------------------------------------------------------------- |
| `unreachable`  | Nothing accepted a TCP connection                              |
| `unauthorized` | A bridge is running but rejected the bearer token              |
| `http-error`   | Something answered that is not an MCP streamable-HTTP endpoint |
| `malformed`    | The endpoint answered but not with a valid `initialize` result |

## Generated client configs

`configure` writes all four in one transaction. Every file is staged before any is committed and a
mid-write failure rolls back, so no agent is ever left pointing at a stale endpoint:

| Client            | File                 | Schema key    |
| ----------------- | -------------------- | ------------- |
| Claude Code       | `.mcp.json`          | `mcpServers`  |
| Cursor            | `.cursor/mcp.json`   | `mcpServers`  |
| VS Code / Copilot | `.vscode/mcp.json`   | `servers`     |
| Codex             | `.codex/config.toml` | `mcp_servers` |

All four are machine-local and gitignored. Existing entries for other servers are preserved; only
the `unity-mcp` entry is rewritten.

## Local overrides

Set any of these in `.env.local` at the repository root, or pass the matching flag:

```bash
UNITY_MCP_BRIDGE_HOST=192.168.1.33
UNITY_MCP_BRIDGE_PORT=9020
UNITY_MCP_BRIDGE_PATH=/mcp
UNITY_MCP_BEARER_TOKEN=<64 hex characters>
UNITY_PROJECT_PATH=D:\Path\To\HostUnityProject
```

Run `node scripts/mcp/unity-mcp.mjs --help` for the full flag list.
