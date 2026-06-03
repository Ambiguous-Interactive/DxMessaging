#!/usr/bin/env bash
# =============================================================================
# scripts/unity/activate-license.sh
# =============================================================================
# Unity license diagnostic and ULF encoding helper.
#
# Modes:
#   --check (default)        Diagnose the current environment. Reports which
#                            supported license path is active (raw ULF,
#                            base64 ULF, paid serial, or unconfigured).
#
#   --apply <path-to.ulf>    Read a .ulf file the operator
#                            obtained from license.unity3d.com or the Unity
#                            Hub on a dev machine, validate the contents,
#                            and print the local `UNITY_LICENSE_B64` export.
#                            GitHub/GameCI secrets should use the raw .ulf
#                            contents, not the base64 value.
#
#   --help                   Show this help.
#
# UNITY_EMAIL + UNITY_PASSWORD alone is not a supported headless container
# activation path. Personal/GameCI runs require a .ulf in UNITY_LICENSE; paid
# serial activation requires UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD.
#
# Bind-mount path translation (docker-outside-of-docker):
#   The docker daemon runs on the HOST, so the `-v` source must be a HOST
#   path, not a path inside this devcontainer. Resolved (in priority order):
#     1) $DXM_HOST_REPO_ROOT  (absolute manual override)
#     2) docker inspect of the current devcontainer bind mount
#     3) $LOCAL_WORKSPACE_FOLDER  (absolute path from VS Code Dev Containers,
#        used only outside a container or after inspect fails)
#     4) $REPO_ROOT  (when running outside a container)
#   If none resolves and we appear to be inside a container, the script
#   fails loud with remediation instructions.
# =============================================================================

set -euo pipefail

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# ---------------------------------------------------------------------------
# Colors (TTY only)
# ---------------------------------------------------------------------------
if [[ -t 1 ]]; then
    C_RED=$'\033[0;31m'
    C_GREEN=$'\033[0;32m'
    C_YELLOW=$'\033[1;33m'
    C_BLUE=$'\033[0;34m'
    C_NC=$'\033[0m'
else
    C_RED=""
    C_GREEN=""
    C_YELLOW=""
    C_BLUE=""
    C_NC=""
fi

usage() {
    cat <<'EOF'
Usage: scripts/unity/activate-license.sh [mode]

Modes:
  --check                   (default) Diagnostic mode. Detects, in order:
                            UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD
                            (preferred paid serial activation), then
                            UNITY_LICENSE or UNITY_LICENSE_B64 (.ulf fallback).
                            For the .ulf paths it sanity-checks the file shape;
                            for the serial path it confirms PRESENCE of the vars
                            ONLY and does not verify the serial is valid (that is
                            checked at live activation). Exit 0 on success, 2 on
                            configuration failure.

  --apply <path-to.ulf>     Validate and base64-encode a .ulf obtained from
                            license.unity3d.com or the Unity Hub. Prints
                            `export UNITY_LICENSE_B64='<b64>'` for local use.

  --help                    Show this help.

UNITY_EMAIL + UNITY_PASSWORD alone is not enough for headless Unity in docker.
Use UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD for paid serial activation, a
raw .ulf in UNITY_LICENSE, or a local base64 .ulf in UNITY_LICENSE_B64. See
.llm/skills/unity/unity-license-bootstrap.md for the full flow.

Environment:
  UNITY_SERIAL              Preferred. Paid Unity serial. Requires UNITY_EMAIL
                            and UNITY_PASSWORD.
  UNITY_EMAIL, UNITY_PASSWORD  Unity account email and password. Required for
                            UNITY_SERIAL (and to return the seat afterwards).
  UNITY_LICENSE             Raw Unity .ulf contents (fallback). Same shape
                            GameCI expects.
  UNITY_LICENSE_B64         Base64-encoded Unity .ulf for local shell profiles.
  UNITY_VERSION             Override the Unity Editor image tag (default:
                            2022.3.45f1). Used by --check.
  LOCAL_WORKSPACE_FOLDER    HOST path to the repo root. Auto-set by VS Code
                            Dev Containers. Used by --check as the docker
                            bind-mount source.
  DXM_HOST_REPO_ROOT        Manual override for the HOST path.

Examples:
  bash scripts/unity/activate-license.sh --check
  bash scripts/unity/activate-license.sh --apply ~/Downloads/Unity_v2022.x.ulf
EOF
}

# ---------------------------------------------------------------------------
# Resolve the host repo path for the docker bind mount (DooD).
# ---------------------------------------------------------------------------
is_absolute_path() {
    [[ "$1" == /* ]] || [[ "$1" =~ ^[A-Za-z]:[\\/] ]]
}

is_container_runtime() {
    [[ -f /.dockerenv ]] && return 0
    [[ -f /proc/1/cgroup ]] && grep -qE '(docker|containerd|kubepods)' /proc/1/cgroup 2>/dev/null
}

detect_host_repo_root_from_container() {
    local container_id mount_source
    container_id="$(hostname)"
    mount_source="$(docker inspect "${container_id}" \
        --format "{{range .Mounts}}{{if eq .Destination \"${REPO_ROOT}\"}}{{.Source}}{{end}}{{end}}" \
        2>/dev/null || true)"
    if [[ -n "${mount_source}" ]]; then
        printf '%s' "${mount_source}"
        return 0
    fi
    return 1
}

resolve_host_repo_root() {
    if [[ -n "${DXM_HOST_REPO_ROOT:-}" ]]; then
        if is_absolute_path "${DXM_HOST_REPO_ROOT}"; then
            printf '%s' "${DXM_HOST_REPO_ROOT}"
            return 0
        fi
        printf '%sIgnoring relative DXM_HOST_REPO_ROOT=%s; docker bind mounts require an absolute host path.%s\n' \
            "${C_YELLOW}" "${DXM_HOST_REPO_ROOT}" "${C_NC}" >&2
    fi
    if is_container_runtime; then
        if detect_host_repo_root_from_container; then
            return 0
        fi
        if [[ -n "${LOCAL_WORKSPACE_FOLDER:-}" ]] && [[ "${LOCAL_WORKSPACE_FOLDER}" == /* ]]; then
            printf '%s' "${LOCAL_WORKSPACE_FOLDER}"
            return 0
        fi
        if [[ -n "${LOCAL_WORKSPACE_FOLDER:-}" ]]; then
            printf '%sIgnoring LOCAL_WORKSPACE_FOLDER=%s inside devcontainer; docker inspect did not resolve a POSIX host path.%s\n' \
                "${C_YELLOW}" "${LOCAL_WORKSPACE_FOLDER}" "${C_NC}" >&2
        fi
        printf '%sERROR: Cannot determine host path for the workspace.%s\n' \
            "${C_RED}" "${C_NC}" >&2
        printf 'When running inside a devcontainer with docker-outside-of-docker, set:\n' >&2
        printf '  DXM_HOST_REPO_ROOT=/absolute/path/on/host\n' >&2
        return 1
    fi
    if [[ -n "${LOCAL_WORKSPACE_FOLDER:-}" ]]; then
        if is_absolute_path "${LOCAL_WORKSPACE_FOLDER}"; then
            printf '%s' "${LOCAL_WORKSPACE_FOLDER}"
            return 0
        fi
        printf '%sIgnoring relative LOCAL_WORKSPACE_FOLDER=%s; docker bind mounts require an absolute host path.%s\n' \
            "${C_YELLOW}" "${LOCAL_WORKSPACE_FOLDER}" "${C_NC}" >&2
    fi
    printf '%s' "${REPO_ROOT}"
}

# ---------------------------------------------------------------------------
# --check mode: diagnose the current configuration.
# ---------------------------------------------------------------------------
do_check() {
    local mode=""
    # Serial-first: paid serial activation (UNITY_SERIAL + UNITY_EMAIL +
    # UNITY_PASSWORD) is the preferred path; a .ulf in UNITY_LICENSE /
    # UNITY_LICENSE_B64 is the fallback. The credentials are treated as secrets
    # and never printed.
    if [[ -n "${UNITY_SERIAL:-}" ]] && [[ -n "${UNITY_EMAIL:-}" ]] && [[ -n "${UNITY_PASSWORD:-}" ]]; then
        mode="serial"
    elif [[ -n "${UNITY_LICENSE:-}" ]]; then
        mode="ulf"
    elif [[ -n "${UNITY_LICENSE_B64:-}" ]]; then
        mode="ulf-b64"
    else
        printf '%sNo Unity license configured.%s\n' "${C_RED}" "${C_NC}" >&2
        printf 'Set EITHER:\n' >&2
        printf '  UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD   (paid serial activation)\n' >&2
        printf '  UNITY_LICENSE       (raw .ulf contents; GameCI-compatible)\n' >&2
        printf '  UNITY_LICENSE_B64   (base64 .ulf contents; local shell convenience)\n' >&2
        printf '\nUNITY_EMAIL + UNITY_PASSWORD alone is not a supported headless container license path.\n' >&2
        return 2
    fi

    printf '%sLicense mode detected: %s%s\n' "${C_BLUE}" "${mode}" "${C_NC}"

    if [[ "${mode}" == "ulf" ]]; then
        if ! printf '%s' "${UNITY_LICENSE}" | grep -q -E '<root>|UnityLicense'; then
            printf '%sUNITY_LICENSE is set but does not look like raw Unity .ulf contents.%s\n' \
                "${C_YELLOW}" "${C_NC}" >&2
            return 2
        fi
        printf '%sUNITY_LICENSE contains plausible raw .ulf contents.%s\n' \
            "${C_GREEN}" "${C_NC}"
        return 0
    fi

    if [[ "${mode}" == "ulf-b64" ]]; then
        local decoded
        if ! decoded="$(printf '%s' "${UNITY_LICENSE_B64}" | base64 -d 2>/dev/null)"; then
            printf '%sUNITY_LICENSE_B64 is set but not valid base64.%s\n' \
                "${C_RED}" "${C_NC}" >&2
            return 2
        fi
        if ! printf '%s' "${decoded}" | grep -q -E '<root>|UnityLicense'; then
            printf '%sUNITY_LICENSE_B64 decodes but does not look like a Unity .ulf.%s\n' \
                "${C_YELLOW}" "${C_NC}" >&2
            return 2
        fi
        printf '%sUNITY_LICENSE_B64 present and decodes to a plausible .ulf.%s\n' \
            "${C_GREEN}" "${C_NC}"
        return 0
    fi

    printf '%sUNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD are present.%s\n' \
        "${C_GREEN}" "${C_NC}"
    # Unlike the ULF paths above (which sanity-check the .ulf shape), --check
    # confirms PRESENCE of the serial vars ONLY -- it does not validate the serial's
    # format or that it is accepted by Unity. The serial is verified at live
    # activation (a real `-serial` editor launch), which is the only place Unity
    # actually checks it. We deliberately do not echo the values.
    printf '%sNote: --check confirms presence only; the serial is verified at live activation.%s\n' \
        "${C_YELLOW}" "${C_NC}"
    printf 'Run scripts/unity/run-tests.sh to perform the live Unity activation.\n'
    return 0
}

# ---------------------------------------------------------------------------
# --apply mode: encode a .ulf for the UNITY_LICENSE_B64 local env var.
# ---------------------------------------------------------------------------
do_apply() {
    local ulf_path="$1"

    if [[ -z "${ulf_path}" ]]; then
        printf '%sError: --apply requires a path to the .ulf file.%s\n' "${C_RED}" "${C_NC}" >&2
        usage >&2
        exit 2
    fi

    if [[ ! -f "${ulf_path}" ]]; then
        printf '%sError: file not found: %s%s\n' "${C_RED}" "${ulf_path}" "${C_NC}" >&2
        exit 1
    fi

    if ! grep -q -E '<root>|UnityLicense' "${ulf_path}" 2>/dev/null; then
        printf '%sWarn: %s does not look like a Unity license file.%s\n' \
            "${C_YELLOW}" "${ulf_path}" "${C_NC}" >&2
    fi

    local encoded
    encoded="$(base64 -w 0 "${ulf_path}" 2>/dev/null || base64 "${ulf_path}" | tr -d '\n')"

    if [[ -z "${encoded}" ]]; then
        printf '%sError: base64 encoding produced no output.%s\n' "${C_RED}" "${C_NC}" >&2
        exit 1
    fi

    printf '%sUNITY_LICENSE_B64 export line for local shells:%s\n' "${C_GREEN}" "${C_NC}"
    printf "export UNITY_LICENSE_B64='%s'\n" "${encoded}"
    printf '\n'
    printf '%sFor GitHub Actions/GameCI secrets:%s paste the raw .ulf file contents into UNITY_LICENSE.\n' \
        "${C_BLUE}" "${C_NC}"
    printf 'Do not paste the base64 UNITY_LICENSE_B64 value into UNITY_LICENSE.\n'
}

# ---------------------------------------------------------------------------
# Dispatch
# ---------------------------------------------------------------------------
if [[ $# -eq 0 ]]; then
    do_check
    exit $?
fi

case "$1" in
    --check)
        do_check
        exit $?
        ;;
    --apply)
        do_apply "${2:-}"
        ;;
    --help|-h)
        usage
        ;;
    *)
        printf '%sUnknown mode: %s%s\n' "${C_RED}" "$1" "${C_NC}" >&2
        usage >&2
        exit 2
        ;;
esac
