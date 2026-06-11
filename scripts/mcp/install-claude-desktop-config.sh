#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
local_env_file="${repo_root}/.env.local"

if [[ -f "$local_env_file" ]]; then
  # shellcheck disable=SC1090
  set -a
  source "$local_env_file"
  set +a
fi

default_host="${UNITY_MCP_DEFAULT_HOST:-192.168.1.33}"
default_port="${UNITY_MCP_DEFAULT_PORT:-9003}"
default_path="${UNITY_MCP_DEFAULT_PATH:-/mcp}"

host="${UNITY_MCP_BRIDGE_HOST:-${UNITY_MCP_HOST:-$default_host}}"
port="${UNITY_MCP_BRIDGE_PORT:-${UNITY_MCP_PORT:-$default_port}}"
endpoint_path="${UNITY_MCP_BRIDGE_PATH:-${UNITY_MCP_PATH:-$default_path}}"
target="${1:-$HOME/.config/Claude/claude_desktop_config.json}"

if ! command -v node >/dev/null 2>&1; then
  echo "node is required but was not found on PATH." >&2
  exit 1
fi

if [[ ! "$port" =~ ^[0-9]+$ ]] || (( port < 1 || port > 65535 )); then
  echo "UNITY MCP port must be an integer between 1 and 65535. Got: $port" >&2
  exit 1
fi

if [[ "$endpoint_path" != /* ]]; then
  endpoint_path="/$endpoint_path"
fi

mkdir -p "$(dirname "$target")"

if [[ -f "$target" ]]; then
  backup_file="${target}.bak.$(date +%Y%m%d%H%M%S)"
  cp "$target" "$backup_file"
  echo "Backed up existing Claude Desktop config to: $backup_file"
fi

UNITY_MCP_HOST="$host" \
UNITY_MCP_PORT="$port" \
UNITY_MCP_PATH="$endpoint_path" \
CLAUDE_DESKTOP_CONFIG_PATH="$target" \
node <<'EOF'
const fs = require('node:fs');

const targetPath = process.env.CLAUDE_DESKTOP_CONFIG_PATH;
const host = process.env.UNITY_MCP_HOST;
const port = process.env.UNITY_MCP_PORT;
const endpointPath = process.env.UNITY_MCP_PATH;

let config = {};
if (fs.existsSync(targetPath)) {
  const raw = fs.readFileSync(targetPath, 'utf8').trim();
  if (raw.length > 0) {
    try {
      config = JSON.parse(raw);
    } catch (error) {
      console.error(`Invalid JSON in existing config: ${targetPath}`);
      console.error(error.message);
      process.exit(1);
    }
  }
}

if (!config || typeof config !== 'object' || Array.isArray(config)) {
  config = {};
}

if (!config.mcpServers || typeof config.mcpServers !== 'object' || Array.isArray(config.mcpServers)) {
  config.mcpServers = {};
}

config.mcpServers['unity-mcp-remote'] = {
  type: 'http',
  url: `http://${host}:${port}${endpointPath}`,
};

fs.writeFileSync(targetPath, `${JSON.stringify(config, null, 2)}\n`, 'utf8');
EOF

echo "Wrote Claude Desktop MCP config to: $target"
echo "Configured bridge URL: http://${host}:${port}${endpoint_path}"