#!/usr/bin/env bash
# shellcheck shell=bash
# Resolve the exact repository SDK before restore, build, or agent attachment.
ensure_pinned_sdk() (
    local workspace="${1:-${WORKSPACE_DIR:-${CACHE_WORKSPACE_ROOT:-}}}"
    local global_json="${workspace}/global.json"
    local expected_sdk actual_sdk installer
    if [[ ! -f "${global_json}" ]]; then
        printf 'Missing SDK pin: %s\n' "${global_json}" >&2
        return 1
    fi
    expected_sdk="$(jq -er '.sdk.version | select(type == "string" and length > 0)' "${global_json}")" || return 1
    actual_sdk="$(cd "${workspace}" && dotnet --version 2>/dev/null)" || actual_sdk=''
    if [[ "${actual_sdk}" == "${expected_sdk}" ]]; then
        return 0
    fi
    # Create/start/attach can overlap. A subshell releases this descriptor on every exit.
    exec 9>"${TMPDIR:-/tmp}/dxm-dotnet-sdk.lock"
    flock -w 300 9 || return 1
    actual_sdk="$(cd "${workspace}" && dotnet --version 2>/dev/null)" || actual_sdk=''
    if [[ "${actual_sdk}" == "${expected_sdk}" ]]; then return 0; fi
    printf 'Installing repository SDK %s before continuing.\n' "${expected_sdk}"
    installer="$(mktemp "${TMPDIR:-/tmp}/dxm-dotnet-install.XXXXXX")" || return 1
    if ! curl --fail --silent --show-error --location https://dot.net/v1/dotnet-install.sh -o "${installer}"; then
        rm -f "${installer}"
        printf 'SDK download failed; restore network access and restart the container.\n' >&2
        return 1
    fi
    # The PATH host lives here; installing to ~/.dotnet would leave it unresolved.
    if ! sudo bash "${installer}" --jsonfile "${global_json}" --install-dir /usr/share/dotnet; then
        rm -f "${installer}"
        printf 'SDK installation failed; see the SDK setup in CONTRIBUTING.md.\n' >&2
        return 1
    fi
    rm -f "${installer}"
    actual_sdk="$(cd "${workspace}" && dotnet --version 2>/dev/null)" || actual_sdk=''
    if [[ "${actual_sdk}" != "${expected_sdk}" ]]; then
        printf 'SDK installation did not resolve %s (actual: %s).\n' "${expected_sdk}" "${actual_sdk:-unavailable}" >&2
        return 1
    fi
)
