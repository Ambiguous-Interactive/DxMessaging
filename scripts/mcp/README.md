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

### Bridge request handling

Every MCP session owns one relay child process, so concurrency is capped: `--max-sessions` (default
`8`) rejects a further `initialize` with HTTP `503` and JSON-RPC `-32000` instead of spawning an
unbounded number of relays. A session that never completes its handshake is reaped after the idle
`--session-timeout` rather than after the much longer `--request-timeout`.

Client mistakes are reported as client errors, so a well-behaved client stops retrying:

| Condition                                  | Response                                        |
| ------------------------------------------ | ----------------------------------------------- |
| Missing or wrong bearer token              | `401` with `WWW-Authenticate: Bearer`           |
| Body over 1 MiB                            | `413` with `-32600`, then the connection closes |
| Body stalled (15s, or `--session-timeout`) | `408` with `-32001`, then the connection closes |
| Body that is not JSON                      | `400` with `-32700 Parse error`                 |
| Unknown path or method                     | `404`                                           |
| Session cap reached                        | `503` with `-32000`                             |

Both of the closing cases send a real response first; neither resets the connection under the
client, which is what turns a diagnosable `413` into an opaque `ECONNRESET`.

`GET /healthz` returns `200 ok` and is deliberately **not** authenticated: it reveals nothing, and a
liveness probe an orchestrator has to hold the token for is not usable as a liveness probe.

## Configure and verify from the devcontainer

```bash
npm run unity:mcp:configure
npm run unity:mcp:probe
```

Both commands probe before acting. Discovery walks every combination of candidate host and port
until one completes an MCP `initialize` handshake:

- **Hosts** - the explicitly configured host if there is one; otherwise `host.docker.internal`,
  `127.0.0.1`, the `nameserver` entries in `/etc/resolv.conf` (the Windows host under WSL2), and the
  default-route gateways in `/proc/net/route`.
- **Ports** - the explicitly configured port if there is one; otherwise `9020`, then `9003`.

An explicit `--host` or `--port` replaces the fallback list on that axis rather than being prepended
to it, so discovery can never override a deliberate setting: `--host X` probes only `X` (against the
fallback ports), and `--host X --port Y` yields exactly one candidate. Pass `--no-discover` to skip
probing entirely and use the configured values as-is.

Failed attempts are reported with a classification, because the fixes differ:

| Status         | Meaning                                                        |
| -------------- | -------------------------------------------------------------- |
| `unreachable`  | Nothing accepted a TCP connection                              |
| `unauthorized` | A bridge is running but rejected the bearer token              |
| `http-error`   | Something answered that is not an MCP streamable-HTTP endpoint |
| `malformed`    | The endpoint answered but not with a valid `initialize` result |

`unauthorized` is special-cased by `configure`: a bridge IS running at that endpoint and only the
token is wrong, so `configure` writes nothing, generates no token, and fails naming the endpoint it
found. Copy `UNITY_MCP_BEARER_TOKEN` from the host's `.env.local` or pass `--token`, then re-run.

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

### What `configure` owns

The `unity-mcp` entry is regenerated wholesale, not merged key by key:

- In the three JSON files, `unity-mcp` inside `mcpServers` / `servers` is replaced. Sibling servers
  and every unrelated top-level key survive.
- In `.codex/config.toml`, the whole `[mcp_servers.unity-mcp]` table is replaced. **Keys you add
  inside that table are dropped on the next run**, so a hand-raised `startup_timeout_sec` reverts.
  Put per-machine Codex overrides in a different table, or re-apply them after `configure`.
- The JSON files are read as JSONC, so the `//` comments VS Code's own "MCP: Add Server" command
  writes into `.vscode/mcp.json` no longer make `configure` fail. Comments are **not** preserved:
  the file is rewritten as plain JSON.
- If `configure` cannot tell which lines of `.codex/config.toml` it owns (a `[mcp_servers.unity-mcp]`
  line appearing inside a multi-line string, or a genuinely duplicated table), it refuses rather than
  splicing, and names the file and the fix.

Writes are transactional: every file is staged, then committed, and a failure part way through rolls
every committed file back to its previous content and permissions. Rollback is itself failure safe,
which matters on Windows where `rename` returns `EPERM` while an editor holds a config file open: a
failed restore is collected and attached to the original error rather than replacing it.

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
