# MCP Local Setup

This page covers machine-local MCP configuration for a Linux devcontainer with a Unity relay running
on a Windows host.

## Why this is local-only

These files hold machine-specific host, port, and token values and are gitignored:

- `.mcp.json`
- `.cursor/mcp.json`
- `.vscode/mcp.json`
- `.codex/config.toml`
- `.env.local`

Do not commit them.

## 1. Start the bridge on the Windows host

See the [Unity MCP documentation](https://docs.unity3d.com/Packages/com.unity.ai.assistant@2.9/manual/integration/unity-mcp-get-started.html)
for installing the relay.

```powershell
npm run unity:mcp:bridge -- --project 'D:\Path\To\HostUnityProject'
```

The bridge finds the relay under `~/.unity/relay/`, generates a bearer token into `.env.local` if one
is not already set, and serves streamable HTTP on port `9020`.

## 2. Configure and verify from the devcontainer

```bash
npm run unity:mcp:configure
npm run unity:mcp:probe
```

`configure` discovers a live endpoint and writes every MCP client config in one transaction. `probe`
confirms the endpoint completes an MCP `initialize` handshake.

Neither command needs a host or port supplied. Discovery tries `host.docker.internal`, `127.0.0.1`,
the `/etc/resolv.conf` nameserver (the Windows host under WSL2), and the default-route gateway,
against ports `9020` and `9003`.

## 3. Override when discovery is not enough

Set values in `.env.local` at the repository root:

```bash
UNITY_MCP_BRIDGE_HOST=192.168.1.33
UNITY_MCP_BRIDGE_PORT=9020
UNITY_MCP_BEARER_TOKEN=<64 hex characters>
```

An explicitly configured host or port is always probed first, so it takes precedence over discovery.

## Notes

- The host and the container must present the same `UNITY_MCP_BEARER_TOKEN`. When they do not share
  `.env.local`, copy the value across or pass `--token`.
- A probe that reports `unauthorized` means a bridge is running but the token does not match, which
  is a different fix from `unreachable`.
- See [scripts/mcp/README.md](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/mcp/README.md)
  for the full command reference.
