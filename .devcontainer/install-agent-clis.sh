#!/usr/bin/env bash
# shellcheck shell=bash

# Refresh the user-scoped agent CLIs. The image provides a build-time copy, so an
# offline launch keeps working while an online launch moves each CLI to npm's
# current latest tag without requiring sudo.

set -euo pipefail

readonly NPM_PREFIX="${NPM_CONFIG_PREFIX:-${HOME}/.local}"
readonly LOG_PREFIX="[agent-clis]"
readonly PACKAGES=(
    "@openai/codex"
    "opencode-ai"
    "@nanocollective/nanocoder"
)
readonly COMMANDS=(
    "codex"
    "opencode"
    "nanocoder"
)

log() {
    echo "${LOG_PREFIX} $*"
}

warn() {
    echo "${LOG_PREFIX} WARN: $*" >&2
}

if ! command -v npm >/dev/null 2>&1; then
    warn "npm is unavailable; keeping the image-provided agent CLIs."
    exit 0
fi

mkdir -p "${NPM_PREFIX}/bin" "${NPM_PREFIX}/lib"
export PATH="${NPM_PREFIX}/bin:${PATH}"

# Several VS Code lifecycle hooks can overlap during a rebuild. One updater is
# enough; the image-provided commands remain available to the other callers.
if command -v flock >/dev/null 2>&1; then
    exec 9>"${TMPDIR:-/tmp}/dxm-install-agent-clis.lock"
    if ! flock -n 9; then
        log "another agent CLI refresh is already running."
        exit 0
    fi
fi

command_version() {
    local command_name="$1"
    local output=""
    case "${command_name}" in
        codex) output="$(timeout 10 codex --version 2>/dev/null || true)" ;;
        opencode) output="$(timeout 10 opencode --version 2>/dev/null || true)" ;;
        nanocoder) output="$(timeout 10 nanocoder --version 2>/dev/null || true)" ;;
        *) return 1 ;;
    esac
    grep -Eo '[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?' <<< "${output}" | head -n 1
}

failures=0
for index in "${!PACKAGES[@]}"; do
    package_name="${PACKAGES[$index]}"
    command_name="${COMMANDS[$index]}"
    installed="$(command_version "${command_name}" || true)"
    latest="$(timeout 20 npm view "${package_name}" version 2>/dev/null | tr -d '[:space:]' || true)"

    if [[ -z "${latest}" ]]; then
        if command -v "${command_name}" >/dev/null 2>&1; then
            log "registry unavailable; keeping ${package_name}@${installed:-unknown}."
        else
            warn "registry unavailable and ${package_name} is not installed."
            ((failures += 1))
        fi
        continue
    fi

    if [[ "${installed}" == "${latest}" ]]; then
        log "${package_name}@${installed} is current."
        continue
    fi

    log "installing ${package_name}@${latest} (current: ${installed:-missing})..."
    installed_ok=false
    for attempt in 1 2 3; do
        if timeout 180 npm install -g "${package_name}@${latest}" --silent --no-fund --no-audit; then
            if command -v "${command_name}" >/dev/null 2>&1; then
                installed_ok=true
                break
            fi
        fi
        warn "${package_name} install attempt ${attempt}/3 failed."
        sleep "$((attempt * 2))"
    done

    if [[ "${installed_ok}" == "true" ]]; then
        log "${package_name}@$(command_version "${command_name}" || echo "${latest}") is ready."
    else
        ((failures += 1))
    fi
done

if [[ "${failures}" -gt 0 ]]; then
    warn "${failures} agent CLI installation(s) remain unavailable."
    exit 1
fi
