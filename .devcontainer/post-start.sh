#!/usr/bin/env bash
# shellcheck shell=bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=.devcontainer/cache-contract.sh
source "${SCRIPT_DIR}/cache-contract.sh"
cache_contract_repair_permissions
export PATH="${HOME}/.local/bin:${PATH}"
export NPM_CONFIG_PREFIX="${HOME}/.local"

# Configuration is local and works with the image's dependencies before npm
# install. Finish it before clients start, without probing a sleeping host.
mcp_script="${SCRIPT_DIR}/../scripts/mcp/unity-mcp.mjs"
mcp_lock="${TMPDIR:-/tmp}/dxm-mcp-configure.lock"
if command -v flock >/dev/null 2>&1; then
    flock -w 30 "${mcp_lock}" node "${mcp_script}" configure --offline
else
    node "${mcp_script}" configure --offline
fi
[[ "${1:-}" == "--prepare" ]] && exit 0

# Every launch checks the latest tags. Image copies remain usable while offline.
installer="${SCRIPT_DIR}/install-agent-clis.sh"
nohup bash "${installer}" </dev/null >"${TMPDIR:-/tmp}/dxm-agent-cli-refresh.log" 2>&1 &
