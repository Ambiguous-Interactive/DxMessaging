#!/usr/bin/env bash
# shellcheck shell=bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ ! -f "${SCRIPT_DIR}/cache-contract.sh" ]]; then
    echo "[post-start] FATAL: cache-contract.sh not found at ${SCRIPT_DIR}/cache-contract.sh"
    exit 1
fi

# shellcheck source=.devcontainer/cache-contract.sh
source "${SCRIPT_DIR}/cache-contract.sh" || {
    echo "[post-start] FATAL: failed to source cache-contract.sh"
    exit 1
}

if ! cache_contract_validate_shape; then
    echo "[post-start] Cache mount contract is invalid (sources/targets length mismatch)."
    exit 1
fi

if cache_contract_is_container_runtime; then
    current_uid="$(id -u)"
    current_gid="$(id -g)"

    for i in "${!CACHE_MOUNT_TARGETS[@]}"; do
        source_name="${CACHE_MOUNT_SOURCES[$i]}"
        target_dir="${CACHE_MOUNT_TARGETS[$i]}"

        mkdir -p "${target_dir}" 2>/dev/null || true

        owner_uid="$(cache_contract_get_owner_uid "${target_dir}" 2>/dev/null || echo "unknown")"
        if [[ "${owner_uid}" != "${current_uid}" ]]; then
            echo "[post-start] Fixing ownership for ${target_dir} (source=${source_name}, owner=${owner_uid}, expected=${current_uid})"
            sudo chown -R "${current_uid}:${current_gid}" "${target_dir}" 2>/dev/null || true
            owner_uid="$(cache_contract_get_owner_uid "${target_dir}" 2>/dev/null || echo "unknown")"
            if [[ "${owner_uid}" != "${current_uid}" ]]; then
                echo "[post-start] ERROR: ${target_dir} ownership remains ${owner_uid} (expected ${current_uid}); sudo chown appears to have failed silently" >&2
            fi
        fi

        write_probe="${target_dir}/.dxm-write-probe-$$"
        if touch "${write_probe}" 2>/dev/null; then
            rm -f "${write_probe}"
        else
            echo "[post-start] ERROR: ${target_dir} is not writable by uid ${current_uid}" >&2
            exit 1
        fi
    done
else
    echo "[post-start] Non-container runtime detected; skipping cache ownership checks."
fi

# Network checks must not delay VS Code attach. The image already contains all
# three CLIs; these background jobs refresh npm's latest tags and regenerate
# machine-local MCP configs for the current port/token pairing.
installer="${SCRIPT_DIR}/install-agent-clis.sh"
if [[ -f "${installer}" ]]; then
    nohup bash "${installer}" </dev/null >"${TMPDIR:-/tmp}/dxm-agent-cli-refresh.log" 2>&1 &
else
    echo "[post-start] WARN: install-agent-clis.sh missing; keeping image-provided agent CLIs"
fi

mcp_script="${SCRIPT_DIR}/../scripts/mcp/unity-mcp.mjs"
if [[ -f "${mcp_script}" ]] && [[ -d "${SCRIPT_DIR}/../node_modules/@modelcontextprotocol" ]]; then
    # Share one lock with post-create: the two can overlap, and both mint a bearer token when
    # .env.local has none, which would leave the generated client configs disagreeing.
    mcp_lock="${TMPDIR:-/tmp}/dxm-mcp-configure.lock"
    if command -v flock >/dev/null 2>&1; then
        nohup flock -w 180 "${mcp_lock}" \
            node "${mcp_script}" configure --no-discover --timeout 750 </dev/null \
            >"${TMPDIR:-/tmp}/dxm-mcp-configure.log" 2>&1 &
    else
        nohup node "${mcp_script}" configure --no-discover --timeout 750 </dev/null \
            >"${TMPDIR:-/tmp}/dxm-mcp-configure.log" 2>&1 &
    fi
else
    echo "[post-start] WARN: Unity MCP configurator dependencies are not ready; post-create will configure them"
fi
