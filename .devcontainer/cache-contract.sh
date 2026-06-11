#!/usr/bin/env bash
# shellcheck shell=bash

# Shared devcontainer cache mount contract.
# Keep these arrays aligned by index: source[i] mounts to target[i].
#
# Entries:
#   1. dxm-nuget-cache          -> NuGet package cache for .NET restore
#   2. dxm-dotnet-tools         -> Global dotnet tools (csharpier, etc.)
#   3. dxm-powershell-modules   -> PowerShell module cache
#   4. dxm-python-cache         -> pip wheel/download cache
#   5. dxm-node-modules         -> Linux devcontainer node_modules tree
#
# Unity Library caches are owned by scripts/unity/run-tests.sh and
# scripts/unity/run-tests.ps1 because they must be keyed by Unity image tag and
# test mode. Do not add a static .unity-test-project/Library mount here.

# Re-source guard: this file is sourced by post-create.sh, post-start.sh,
# and validate-caching.sh. Multiple sources in the same shell would
# otherwise re-declare the readonly arrays and abort under `set -e`.
[[ "${_DXM_CACHE_CONTRACT_LOADED:-}" == "1" ]] && return 0
_DXM_CACHE_CONTRACT_LOADED=1

# Workspace root resolution. Prefer the explicit WORKSPACE_FOLDER (set by
# devcontainer.json remoteEnv == ${containerWorkspaceFolder}). When it is unset
# -- e.g. during postCreateCommand, which runs BEFORE remoteEnv is applied --
# derive it from THIS script's own location: cache-contract.sh lives in
# <workspaceRoot>/.devcontainer/, so the parent of its directory is the
# workspace root and equals ${containerWorkspaceFolder} by construction. This is
# robust to repo path/name migration; never hardcode an absolute fallback (a
# stale literal silently diverges from the real mount target).
CACHE_WORKSPACE_ROOT="${WORKSPACE_FOLDER:-}"
if [[ -z "${CACHE_WORKSPACE_ROOT}" ]]; then
    # ${BASH_SOURCE[0]:?...} fails LOUDLY rather than deriving a wrong root from
    # the caller's CWD if this file is ever sourced without a resolvable path
    # (every real consumer sources by absolute path, so this never trips). Never
    # silently default to a permissive/incorrect path.
    #
    # Normalize backslashes to forward slashes BEFORE deriving the directory.
    # GNU `dirname` splits only on `/`, so a Windows-native BASH_SOURCE path
    # (`D:\repo\.devcontainer\cache-contract.sh`, as produced when a Windows
    # bash flavor is handed a native path) would yield `.` and then resolve the
    # WRONG root relative to the shell CWD. `cd` accepts the forward-slash form
    # on every bash flavor (Git-Bash/MSYS/Cygwin/WSL), so normalizing first is
    # robust everywhere; on Linux/macOS the substitution is a no-op.
    _dxm_cache_contract_source="${BASH_SOURCE[0]:?cache-contract.sh must be sourced by path so the workspace root can be derived}"
    _dxm_cache_contract_source="${_dxm_cache_contract_source//\\//}"
    CACHE_WORKSPACE_ROOT="$(cd -- "$(dirname -- "${_dxm_cache_contract_source}")/.." && pwd)"
    unset _dxm_cache_contract_source
fi
readonly CACHE_WORKSPACE_ROOT

readonly CACHE_MOUNT_SOURCES=(
    "dxm-nuget-cache"
    "dxm-dotnet-tools"
    "dxm-powershell-modules"
    "dxm-python-cache"
    "dxm-node-modules"
)

readonly CACHE_MOUNT_TARGETS=(
    "/home/vscode/.nuget"
    "/home/vscode/.dotnet/tools"
    "/home/vscode/.local/share/powershell"
    "/home/vscode/.cache/pip"
    "${CACHE_WORKSPACE_ROOT}/node_modules"
)

cache_contract_validate_shape() {
    if [[ "${#CACHE_MOUNT_SOURCES[@]}" -eq 0 ]] \
        || [[ "${#CACHE_MOUNT_TARGETS[@]}" -eq 0 ]] \
        || [[ "${#CACHE_MOUNT_SOURCES[@]}" -ne "${#CACHE_MOUNT_TARGETS[@]}" ]]; then
        return 1
    fi

    return 0
}

# Emit a one-line diagnostic describing how CACHE_WORKSPACE_ROOT was resolved.
# Consumers (validate-caching.sh) call this explicitly; sourcing stays silent.
cache_contract_describe_workspace_root() {
    if [[ -n "${WORKSPACE_FOLDER:-}" ]]; then
        echo "CACHE_WORKSPACE_ROOT=${CACHE_WORKSPACE_ROOT} (from WORKSPACE_FOLDER env)"
    else
        echo "CACHE_WORKSPACE_ROOT=${CACHE_WORKSPACE_ROOT} (derived from script location; WORKSPACE_FOLDER unset)"
    fi
}

cache_contract_get_owner_uid() {
    local target="$1"
    local owner_uid

    if owner_uid="$(stat -c %u "$target" 2>/dev/null)" && [[ "$owner_uid" =~ ^[0-9]+$ ]]; then
        echo "$owner_uid"
        return 0
    fi

    if owner_uid="$(stat -f %u "$target" 2>/dev/null)" && [[ "$owner_uid" =~ ^[0-9]+$ ]]; then
        echo "$owner_uid"
        return 0
    fi

    return 1
}

cache_contract_is_container_runtime() {
    if [[ -f "/.dockerenv" ]]; then
        return 0
    fi

    if [[ "${DEVCONTAINER:-}" == "true" ]] || [[ "${REMOTE_CONTAINERS:-}" == "true" ]]; then
        return 0
    fi

    if grep -qaE '(docker|containerd|kubepods)' /proc/1/cgroup 2>/dev/null; then
        return 0
    fi

    return 1
}
