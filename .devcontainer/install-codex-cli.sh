#!/usr/bin/env bash
# shellcheck shell=bash
# =============================================================================
# install-codex-cli.sh
# -----------------------------------------------------------------------------
# Idempotently install the latest @openai/codex CLI as a user-global npm
# package. Designed to be invoked from post-start.sh on every container start.
#
# Behavior:
#   * Resolves the registry's `latest` dist-tag with a bounded timeout.
#   * Skips if the installed version already matches `latest`.
#   * Installs into NPM_CONFIG_PREFIX (= /home/vscode/.local) — no sudo needed.
#   * Retries up to 3 times with backoff on transient failures.
#   * Never fails the caller: degrades gracefully when offline or when the
#     registry is unreachable, keeping any previously-installed version.
# =============================================================================

set -euo pipefail

PKG="@openai/codex"
NPM_PREFIX="${NPM_CONFIG_PREFIX:-${HOME}/.local}"
LOG_PREFIX="[install-codex]"

log()  { echo "${LOG_PREFIX} $*"; }
warn() { echo "${LOG_PREFIX} WARN: $*" >&2; }

if ! command -v npm >/dev/null 2>&1; then
    warn "npm not found; skipping ${PKG} install."
    exit 0
fi

export PATH="${NPM_PREFIX}/bin:${PATH}"

# Avoid concurrent npm global installs when multiple lifecycle hooks overlap.
if command -v flock >/dev/null 2>&1; then
    exec 9>"${TMPDIR:-/tmp}/install-codex-cli.lock"
    if ! flock -n 9; then
        log "another install-codex process is running; skipping this run."
        exit 0
    fi
fi

# ---- read currently-installed version (cheap, offline) ----------------------
installed=""
pkg_json="${NPM_PREFIX}/lib/node_modules/@openai/codex/package.json"
if [[ -f "${pkg_json}" ]]; then
    if command -v jq >/dev/null 2>&1; then
        installed="$(jq -r '.version // empty' "${pkg_json}" 2>/dev/null || true)"
    else
        installed="$(grep -m1 '^[[:space:]]*"version"[[:space:]]*:' "${pkg_json}" \
            | sed -E 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/' \
            || true)"
    fi
fi

# ---- resolve `latest` from the registry (bounded) ---------------------------
latest="$(timeout 20 npm view "${PKG}" version 2>/dev/null | tr -d '[:space:]' || true)"

if [[ -z "${latest}" ]]; then
    if [[ -n "${installed}" ]]; then
        log "registry unreachable; keeping installed ${PKG}@${installed}."
    else
        warn "registry unreachable and ${PKG} not installed; will retry next start."
    fi
    exit 0
fi

if [[ "${installed}" == "${latest}" ]]; then
    log "${PKG}@${installed} already up-to-date."
    exit 0
fi

log "Installing ${PKG}@${latest} (previously: ${installed:-not installed})..."

for attempt in 1 2 3; do
    if timeout 180 npm install -g "${PKG}@${latest}" \
            --silent --no-fund --no-audit; then
        if command -v codex >/dev/null 2>&1; then
            log "${PKG} ready: $(timeout 10 codex --version 2>/dev/null | head -n1 || echo "${latest}")"
            exit 0
        fi
        warn "codex binary missing from PATH after install attempt ${attempt}/3."
    else
        warn "npm install failed (attempt ${attempt}/3)."
    fi
    sleep "$((attempt * 2))"
done

warn "failed to install ${PKG} after 3 attempts; continuing without it."
exit 0
