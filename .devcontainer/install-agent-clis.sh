#!/usr/bin/env bash
# shellcheck shell=bash

# Refresh the user-scoped agent CLIs. The image provides a build-time copy, so an
# offline launch keeps working while an online launch refreshes each CLI
# from npm without requiring sudo.

set -euo pipefail

readonly NPM_PREFIX="${NPM_CONFIG_PREFIX:-${HOME}/.local}"
readonly LOG_PREFIX="[agent-clis]"
readonly OPENCODE_PACKAGE="@opencode/cli"
readonly OPENCODE_VERSION_PATTERN='^2\.[0-9]+\.[0-9]+$'
readonly PACKAGES=(
    "@openai/codex"
    "@anthropic-ai/claude-code"
    "${OPENCODE_PACKAGE}"
    "@nanocollective/nanocoder"
)
readonly COMMANDS=(
    "codex"
    "claude"
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
export NPM_CONFIG_PREFIX="${NPM_PREFIX}"

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
        claude) output="$(timeout 10 claude --version 2>/dev/null || true)" ;;
        opencode) output="$(timeout 10 "${NPM_PREFIX}/bin/opencode" --version 2>/dev/null)" || return 1 ;;
        nanocoder) output="$(timeout 10 nanocoder --version 2>/dev/null || true)" ;;
        *) return 1 ;;
    esac
    grep -Eo '[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?' <<< "${output}" | head -n 1
}

latest_version() {
    local package_name="$1"
    if [[ "${package_name}" == "${OPENCODE_PACKAGE}" ]]; then
        # npm view returns a scalar for one match and a semver-ordered array for multiple matches.
        timeout 20 npm view "${package_name}@2" version --json 2>/dev/null |
            node -e '
                const versions = JSON.parse(require("node:fs").readFileSync(0, "utf8"));
                const latest = Array.isArray(versions) ? versions.at(-1) : versions;
                if (typeof latest === "string") process.stdout.write(latest);
            ' 2>/dev/null
    else
        timeout 20 npm view "${package_name}@latest" version 2>/dev/null | tr -d '[:space:]'
    fi
}

failures=0
for index in "${!PACKAGES[@]}"; do
    package_name="${PACKAGES[$index]}"
    command_name="${COMMANDS[$index]}"
    installed="$(command_version "${command_name}" || true)"
    latest="$(latest_version "${package_name}")" || latest=""

    if [[ -z "${latest}" ]]; then
        if [[ "${package_name}" == "${OPENCODE_PACKAGE}" && ! "${installed}" =~ ${OPENCODE_VERSION_PATTERN} ]]; then
            warn "registry unavailable and ${package_name} is not installed. Expected a working OpenCode v2 in ${NPM_PREFIX}/bin."
            ((failures += 1))
        elif command -v "${command_name}" >/dev/null 2>&1; then
            log "registry unavailable; keeping ${package_name}@${installed:-unknown}."
        else
            warn "registry unavailable and ${package_name} is not installed."
            ((failures += 1))
        fi
        continue
    fi

    if [[ ! "${latest}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]] ||
        [[ "${package_name}" == "${OPENCODE_PACKAGE}" && ! "${latest}" =~ ${OPENCODE_VERSION_PATTERN} ]]; then
        warn "Registry returned an invalid version for ${package_name}."
        ((failures += 1))
        continue
    fi

    if [[ "${installed}" == "${latest}" ]]; then
        log "${package_name}@${installed} is current."
        continue
    fi

    # Both npm packages claim the opencode link. Remove only a recognized user v1 install.
    if [[ "${package_name}" == "${OPENCODE_PACKAGE}" &&
        "$(readlink "${NPM_PREFIX}/bin/opencode" 2>/dev/null)" == "../lib/node_modules/opencode-ai/bin/opencode.exe" ]]; then
        if ! timeout 180 npm uninstall -g opencode-ai; then
            warn "Could not remove the legacy OpenCode v1 npm package from ${NPM_PREFIX}."
            ((failures += 1))
            continue
        fi
    fi

    log "installing ${package_name}@${latest} (current: ${installed:-missing})..."
    installed_ok=false
    install_options=(--silent --no-fund --no-audit)
    case "${package_name}" in
        "${OPENCODE_PACKAGE}"|@anthropic-ai/claude-code) install_options+=("--allow-scripts=${package_name}") ;;
    esac
    for attempt in 1 2 3; do
        if timeout 180 npm install -g "${package_name}@${latest}" "${install_options[@]}"; then
            if [[ "$(command_version "${command_name}" || true)" == "${latest}" ]]; then
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
    warn "${failures} agent CLI refresh(es) failed; see messages above."
    exit 1
fi
