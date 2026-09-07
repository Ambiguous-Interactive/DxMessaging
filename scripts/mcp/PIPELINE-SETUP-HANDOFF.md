# Unity Pipeline MCP setup and troubleshooting handoff

Use this guide to reproduce the working host-Unity plus Linux-devcontainer setup
in another package repository, such as `unity-helpers`. It records the failures
found on 2026-09-07 UTC and the fixes verified in DxMessaging. Replace example paths,
ports, and credentials with values for the destination machine.

## What was verified

| Component        | Verified configuration or result                                                                |
| ---------------- | ----------------------------------------------------------------------------------------------- |
| Host             | macOS, Unity Editor 6000.4.6f1                                                                  |
| Unity CLI        | 1.0.0-beta.8                                                                                    |
| Pipeline package | `com.unity.pipeline` 0.6.0-exp.1                                                                |
| Codex            | 0.153.4; 149 Pipeline tools discovered                                                          |
| Z.AI             | Search: 1 tool; Reader: 1; Zread: 3; Vision: 8                                                  |
| Nanocoder        | Its native MCP client verified Unity and all Z.AI connections before the final Pipeline cutover |
| OpenCode         | All eight configured servers connected before the final Pipeline cutover                        |
| Claude Code      | Generated HTTP configuration checked; CLI unavailable for a direct client test                  |
| Repository tests | 1,242 script tests passed before the final host-only Pipeline repair                            |

Tool counts are observations for these versions, not permanent requirements.
The final Pipeline endpoint passed the container's live Editor probe and Codex's
own tool discovery. No claim of Unity test-suite execution through Pipeline is
made by these connection checks.

## Architecture and path rules

```text
Host Unity project root: /Users/developer/Code/Packages
  Assets/
  ProjectSettings/
  Packages/
    manifest.json                 <-- install Pipeline here
    com.wallstop-studios.unity-helpers/
      .env.local                  <-- host settings and shared bridge token
      scripts/mcp/unity-mcp.mjs    <-- host HTTP bridge and client configurator

Linux devcontainer
  /workspaces/com.wallstop-studios.unity-helpers/
    generated MCP client configs
           |
           | authenticated HTTP to host.docker.internal:<bridge-port>/mcp
           v
Host bridge: node scripts/mcp/unity-mcp.mjs bridge
           |
           | stdio: unity mcp --project-path <host-project-root>
           v
Pipeline server inside the selected Unity Editor
```

`UNITY_PROJECT_PATH` names the HOST project containing `Assets`, `ProjectSettings`,
and `Packages/manifest.json`. It does not name the embedded package or the
container mount. The bridge runs on the host; agents and client configuration run
inside the container. Keep the actual project path spelling consistent with
`unity status`. Capitalization was not the cause of this incident.

## Findings and fixes

### Unity connected with zero tools

The Unity CLI completed MCP initialization even when the selected project had no
running Pipeline server. Clients displayed a connected server with an empty tool
list. Pipeline was installed and registered, but its assemblies had never loaded
because project compilation failed.

Assistant 2.9.0-pre.2 supplied a non-overridable, explicitly referenced
`System.Reflection.Metadata.dll`. This displaced Pipeline's automatically
referenced copy. `Unity.Pipeline.IlInterpreter` then failed with CS0234/CS0246 for
`System.Reflection.Metadata`, `MetadataReader`, `PEReader`, and related types.

The fix was to add `EXCLUDE_REFLECTION_METADATA` to the root project's active
target Scripting Define Symbols. Assistant's DLL importer has the matching
exclusion constraint; Pipeline's copy then supplied the missing reference.
Compilation succeeded and Pipeline appeared in `unity status`.

The bridge now returns an actionable error for an empty initial tool registry.
Neither a connected indicator nor `/healthz` proves Editor readiness.

### Z.AI worked in some clients but failed in Codex

The three remote Z.AI endpoints returned HTTP 200 with no Content-Type and no
body for `notifications/initialized`. Codex's HTTP transport rejected this with:

```text
Unexpected content type: missing-content-type
when send initialized notification
```

The endpoints and API key were valid. SDK-based checks and OpenCode worked.
For Codex only, the configurator now uses the installed `mcp-remote` 0.8.3 adapter
for Search, Reader, and Zread. The adapter accepts the notification response and
speaks stdio to Codex. Other clients retain direct HTTP. Vision is a separate
local stdio server and needs `Z_AI_API_KEY` and `Z_AI_MODE=ZAI`.

### Repeated macOS Keychain prompts

The bridge starts a lowercase `unity` CLI process for each MCP session. On this
machine its executable was `~/.unity/bin/unity`, separate from the Unity Editor
application. Authorizing the Editor does not authorize that CLI.

In Keychain Access, select `login`, find the exact credential named in the popup,
double-click it, open **Access Control**, and add the actual CLI executable to
the allowed applications. Use Cmd+Shift+G in the file picker to reach the hidden
`.unity/bin` directory. Save and authenticate locally. The user completed this
step; we did not establish a separate cause for macOS failing to remember the
original Always Allow selection. Do not grant every application access to the
credential as a substitute.

## 1. Bring the tooling to the similar repository

Send this document together with the repaired `unity-mcp.mjs` from this checkout.
Commit or copy the repaired version before handing off; an older upstream copy
may not contain the Z.AI adapter configuration or empty-registry error handling.

Copy the repaired [MCP entry point](unity-mcp.mjs) to the destination repository
at exactly `scripts/mcp/unity-mcp.mjs`. It derives the repository root from that
location. Preserve its dependency loader, which can also resolve packages from
`/opt/dxm-mcp` before workspace dependencies are installed.

Merge these npm scripts into the destination's existing `package.json`:

```json
{
  "scripts": {
    "unity:mcp:bridge": "node scripts/mcp/unity-mcp.mjs bridge",
    "unity:mcp:configure": "node scripts/mcp/unity-mcp.mjs configure",
    "unity:mcp:probe": "node scripts/mcp/unity-mcp.mjs probe"
  }
}
```

Install the script dependencies and retain the updated lockfile:

```bash
npm install --save-dev --save-exact @modelcontextprotocol/sdk@1.30.0 smol-toml@1.8.0 jsonc-parser@3.3.1
```

Run dependency installation on both the host and in the container. A Linux
`node_modules` volume is separate from host dependencies. If the destination
publishes a Unity package, apply its own package-exclusion and `.meta` rules to
development tooling; do not copy this repository's publishing configuration blindly.

The host test runner `DxMcpTestRunner.cs.txt` is optional for MCP connectivity.
Port it separately if agents must run the repository's Unity tests.

## 2. Install the host CLI and enable Pipeline

Install the official Unity CLI using the [Unity CLI guide](https://docs.unity.com/hub/unity-cli).
Use a CLI release that supports `unity mcp`. Open the intended project in Unity.
From a HOST terminal:

```bash
unity --version
unity pipeline install --project-path /absolute/host/project/root
unity status --format json
unity list --project-path /absolute/host/project/root --format json
unity command editor_status --project-path /absolute/host/project/root --format json
```

Skip the installation command if Pipeline is already installed. The version
observed here is recorded above; verify the actual version on the new machine.

If compilation fails with the metadata errors described above and Assistant is
also installed, use **Edit > Project Settings > Player > Other Settings > Script
Compilation > Scripting Define Symbols** for the active target. Add:

```text
EXCLUDE_REFLECTION_METADATA
```

Preserve all existing symbols. Ensure the change is saved in the root project's
`ProjectSettings/ProjectSettings.asset`. Repeat for other target configurations
only if they encounter the same dependency conflict. Do not add a collection of
unrelated `EXCLUDE_*` symbols: each must match an actual importer constraint and
a replacement assembly available in that project.

Wait for compilation and domain reload to finish. The selected project must
appear in `unity status`, `unity list` must return a nonempty catalog, and
`editor_status` must report the intended project. Avoid editing `Library/PackageCache`
as the durable fix; Unity can replace those files.

If the Console was cleared, compiler evidence can still be present in
`Library/Bee/tundra.log.json`: inspect `noderesult` records with nonzero `exitcode`
and their `stdout`. An installed package directory is not proof of loaded code.

## 3. Prepare the devcontainer

Install the agent clients you intend to use. Also install the local MCP programs
in the IMAGE so client startup does not depend on downloading them:

```dockerfile
RUN npm install --global --prefix /usr/local @z_ai/mcp-server@latest mcp-remote@0.8.3 --no-fund --no-audit
RUN UV_TOOL_DIR=/opt/dxm-mcp/uv-tools UV_TOOL_BIN_DIR=/usr/local/bin uv tool install mcp-server-git \
    && UV_TOOL_DIR=/opt/dxm-mcp/uv-tools UV_TOOL_BIN_DIR=/usr/local/bin uv tool install mcp-server-fetch
```

These fragments assume Node/npm, Python, and `uv` are already installed. For the
offline configurator fallback, install the three npm dependencies from step 1
under `/opt/dxm-mcp` too. See the [source Dockerfile](../../.devcontainer/Dockerfile)
for the complete image integration. Rebuild after changing the image.

Keep `/home/vscode/.local/bin` and `/usr/local/bin` on the client PATH. Use the
ordinary container user for subsequent npm installs, with npm prefix
`/home/vscode/.local`; do not mix root-owned user installs with unprivileged updates.

Merge this into the destination's `devcontainer.json`:

```json
{
  "remoteEnv": {
    "PATH": "/home/vscode/.local/bin:${containerEnv:PATH}",
    "COPILOT_HOME": "${containerWorkspaceFolder}/.copilot",
    "NANOCODER_MCPSERVERS_FILE": "${containerWorkspaceFolder}/.nanocoder/mcp.json"
  }
}
```

If agents also run through `docker exec` or outside VS Code terminals, supply
these variables there too; VS Code's `remoteEnv` is not a global container
environment. Nanocoder otherwise reads `.mcp.json`, whose Claude-style `type`
field does not supply Nanocoder's required `transport` field.

Docker Desktop provides `host.docker.internal`. On Linux Docker hosts, configure
the host-gateway mapping, commonly `--add-host=host.docker.internal:host-gateway`.
Allow the bridge port from the container network through the host firewall.

## 4. Configure machine-local values

Create `.env.local` at the PACKAGE repository root, not the Unity project root:

```dotenv
UNITY_PROJECT_PATH=/absolute/host/project/root
UNITY_MCP_BACKEND=cli
UNITY_MCP_BRIDGE_HOST=host.docker.internal
UNITY_MCP_BRIDGE_PORT=9020
Z_AI_API_KEY=replace-with-your-zai-key
GITHUB_PERSONAL_ACCESS_TOKEN=replace-with-your-github-token
```

Use an absolute host path rather than `~`; this file is parsed as data, not
sourced as a shell script. Omit services you do not use. Environment variables
take precedence over file values, so check for stale exported credentials or
backend overrides when a file edit seems ineffective.

The host bridge generates `UNITY_MCP_BEARER_TOKEN` if absent. Share that value
with the container, normally through the bind-mounted `.env.local`. Use fresh
credentials on the destination machine; do not copy generated secret files from
this machine. Z.AI account access and quotas must support the requested services.

Ignore `.env.local` and the generated client config files in version control and
exclude them from the Docker build context. The configurator writes client files
with mode `0600`. Also protect `.env.local` with restrictive local permissions.

## 5. Start one host bridge, then configure clients

Run from the HOST package repository and leave this terminal open:

```bash
npm run unity:mcp:bridge
```

From the CONTAINER package repository:

```bash
npm run unity:mcp:configure -- --offline
npm run unity:mcp:probe
```

Configure writes all clients; probe verifies the host connection and makes a
read-only `editor_status` call. Offline configuration alone does not verify
reachability. Reconnect MCP servers or restart existing clients after changes.

For automatic setup, run offline configuration after the repository is mounted
and before agents start. Merge this into existing lifecycle hooks rather than
overwriting them. The [source startup script](../../.devcontainer/post-start.sh)
serializes overlapping configuration runs with `flock`. It does not start the
HOST bridge. A service manager can keep the host bridge running independently.

If two package repositories use the same Unity project, they may share one
bridge endpoint and token deliberately. Different Unity projects require
different bridge ports and explicit host project paths. Never start a second
bridge on an occupied port.

On macOS, identify the listener with:

```bash
lsof -nP -iTCP:9020 -sTCP:LISTEN
```

If taking over a known bridge, stop that exact process before starting yours.
Do not copy a PID from another machine or terminate the Unity Editor. The port
in the original incident was 9017; it is not a required port for new setups.

## 6. Check client-specific configuration

| Client               | Generated file             | Key details                                                                            |
| -------------------- | -------------------------- | -------------------------------------------------------------------------------------- |
| Codex                | `.codex/config.toml`       | `mcp_servers`; Unity HTTP; remote Z.AI via `mcp-remote` stdio; project must be trusted |
| Claude Code          | `.mcp.json`                | `mcpServers`; `type: http` for remote servers                                          |
| OpenCode             | `opencode.jsonc`           | `mcp`; `type: remote` or `local`; local command is an array                            |
| Nanocoder            | `.nanocoder/mcp.json`      | `mcpServers`; `transport: http` or `stdio`; set `NANOCODER_MCPSERVERS_FILE`            |
| Copilot CLI          | `.copilot/mcp-config.json` | `mcpServers`; tool list `[*]`; set `COPILOT_HOME`                                      |
| VS Code Copilot Chat | `.vscode/mcp.json`         | `servers`                                                                              |
| Cursor               | `.cursor/mcp.json`         | `mcpServers`                                                                           |

The three remote Z.AI endpoints are:

```text
https://api.z.ai/api/mcp/web_search_prime/mcp
https://api.z.ai/api/mcp/web_reader/mcp
https://api.z.ai/api/mcp/zread/mcp
```

For reference, the generated Codex Search entry has this shape:

```toml
[mcp_servers.web-search-prime]
command = "mcp-remote"
args = ["https://api.z.ai/api/mcp/web_search_prime/mcp", "--transport", "http-only", "--header", "Authorization:${ZAI_AUTH_HEADER}", "--silent"]
startup_timeout_sec = 30
tool_timeout_sec = 300
enabled = true

[mcp_servers.web-search-prime.env]
ZAI_AUTH_HEADER = "Bearer replace-with-your-zai-key"
```

The header placeholder in `args` stays literal. `mcp-remote` expands it from the
environment so the credential is not exposed in process arguments. Let the
configurator generate the actual entries instead of manually synchronizing clients.
See the [Z.AI Reader documentation](https://docs.z.ai/devpack/mcp/reader-mcp-server)
for the direct HTTP configuration and
[Codex MCP documentation](https://developers.openai.com/codex/mcp) for client settings.

## Acceptance checks for the receiving developer or agent

1. Verify host CLI discovery names the intended root project.
1. Verify project compilation succeeds and Pipeline provides a nonempty tool catalog.
1. Verify the host bridge uses `cli`, with the intended root and port.
1. Run `unity:mcp:probe` inside the container and require `editor_status` to answer.
1. Inspect tools in each actual client. A configured or connected label is insufficient.
1. Check all configured Z.AI services. Distinguish transport errors from key, account,
   quota, or local executable failures; never print keys while diagnosing them.
1. Restart or reconnect a client and repeat discovery to prove it loads the saved config.
1. Verify the root project saves the reflection exclusion if that fix was required.
1. Record versions and results. Do not assert the original tool counts for newer packages.

A handoff is complete when the receiving machine's clients discover Pipeline tools
and the selected Editor answers a read-only request. Running project tests, editing
scenes, and removing Assistant require their own validation after connectivity works.
