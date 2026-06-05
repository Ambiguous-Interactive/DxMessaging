#!/usr/bin/env bash
# =============================================================================
# scripts/unity/run-tests.sh
# =============================================================================
# Headless Unity Test Runner driver. Spawns an ephemeral unityci/editor
# container via the host docker socket (docker-outside-of-docker, configured
# in .devcontainer/devcontainer.json) and streams Unity's log to stdout in
# realtime.
#
# This is the canonical LOCAL entry point (devcontainer / local docker) for the
# headless Unity test flow. CI no longer calls this script: the GitHub workflows
# run Unity via the maintained game-ci actions directly (see
# .github/workflows/unity-*.yml). Locally, the run FAILS if zero tests run
# (results.xml is validated, total>0 required).
#
# Default behavior excludes Benchmarks/Allocations/Comparisons assemblies per
# .llm/context.md line 114 (perf isolation). Use --include-perf to override.
#
# Default behavior also excludes the DI integration suites (VContainer/Zenject/
# Reflex) because their backing packages are not in the test project's
# manifest.json. Use --include-integrations to opt in (requires the relevant
# packages to be added first).
#
# Bind-mount path translation (docker-outside-of-docker):
#   The docker daemon runs on the HOST, so the `-v` source must be a HOST path,
#   not a path inside this devcontainer. We resolve it (in priority order):
#     1) $DXM_HOST_REPO_ROOT  (absolute manual override)
#     2) docker inspect of the current devcontainer bind mount
#     3) $LOCAL_WORKSPACE_FOLDER  (absolute path from VS Code Dev Containers,
#        used only outside a container or after inspect fails)
#     4) $PWD  (when running outside a container, e.g., a CI runner directly)
#   If none of these resolves and we appear to be inside a container, the
#   script fails loud with remediation instructions.
# =============================================================================

set -euo pipefail

# ---------------------------------------------------------------------------
# Defaults
# ---------------------------------------------------------------------------
PLATFORM=""
UNITY_VERSION_DEFAULT="${UNITY_VERSION:-2022.3.45f1}"
UNITY_VERSION_ARG=""
TEST_FILTER=""
INCLUDE_PERF="false"
INCLUDE_INTEGRATIONS="false"
INCLUDE_COMPARISONS="false"
RESULTS_PATH=""

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ARTIFACTS_DIR="${REPO_ROOT}/.artifacts/unity"

# Colors for the summary tail (only printed when stdout is a TTY).
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

# ---------------------------------------------------------------------------
# Help / usage
# ---------------------------------------------------------------------------
usage() {
    cat <<'EOF'
Usage: scripts/unity/run-tests.sh --platform <editmode|playmode|standalone> [options]

Required:
  --platform <mode>          editmode | playmode | standalone

Options:
  --unity-version <ver>      Unity Editor version tag (default: 2022.3.45f1
                             or $UNITY_VERSION)
  --filter <regex>           Forwarded to Unity's -testFilter
  --include-perf             Include Benchmarks/Allocations asmdefs that do
                             not require external comparison packages
                             (default: excluded)
  --include-integrations     Include VContainer/Zenject/Reflex integration
                             asmdefs (default: excluded). Requires the
                             corresponding DI packages in
                             .unity-test-project/Packages/manifest.json.
  --include-comparisons      Include comparison benchmarks against MessagePipe,
                             UniRx, UniTask, and Zenject. Requires those
                             packages in .unity-test-project/Packages/manifest.json.
  --results <path>           Path to write NUnit XML
                             (default: .artifacts/unity/results.xml). Must be
                             within the repo (the docker bind-mount only
                             exposes the repo root).
  --help                     Show this help and exit 0

Environment:
  UNITY_SERIAL               Preferred. Paid Unity serial for Professional
                             activation. Requires UNITY_EMAIL + UNITY_PASSWORD.
                             When set, the editor is activated with -serial
                             -username -password and the license is RETURNED on
                             exit (the EXIT trap runs -returnlicense) so a local
                             run never leaks the seat. Treated as a secret; never
                             printed.
  UNITY_EMAIL, UNITY_PASSWORD  Unity account credentials. Required with
                             UNITY_SERIAL (and to return the seat afterwards).
  UNITY_LICENSE              Raw Unity .ulf contents (fallback). This is the same
                             shape expected by game-ci/unity-test-runner@v4.
  UNITY_LICENSE_B64          Base64-encoded Unity .ulf contents for local shell
                             profiles that cannot hold multiline secrets.
  CI                         When "true", emits GitHub Actions ::error::
                             annotations on failure; the docker run still
                             executes normally.
  LOCAL_WORKSPACE_FOLDER     HOST path to the repo root. Set automatically by
                             the VS Code Dev Containers extension when
                             docker-outside-of-docker is configured.
  DXM_HOST_REPO_ROOT         Absolute override for the HOST path. Use this when
                             VS Code did not set LOCAL_WORKSPACE_FOLDER (e.g.,
                             attached terminals, plain docker exec sessions).

Examples:
  bash scripts/unity/run-tests.sh --platform editmode
  bash scripts/unity/run-tests.sh --platform playmode --filter 'MessageBus.*'
  bash scripts/unity/run-tests.sh --platform standalone
  bash scripts/unity/run-tests.sh --platform editmode --include-perf
  bash scripts/unity/run-tests.sh --platform editmode --include-integrations

See .llm/skills/unity/headless-test-runner.md for the full skill page (Phase 4).
EOF
}

# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------
while [[ $# -gt 0 ]]; do
    case "$1" in
        --platform)
            PLATFORM="${2:-}"
            shift 2
            ;;
        --unity-version)
            UNITY_VERSION_ARG="${2:-}"
            shift 2
            ;;
        --filter)
            TEST_FILTER="${2:-}"
            shift 2
            ;;
        --include-perf)
            INCLUDE_PERF="true"
            shift 1
            ;;
        --include-integrations)
            INCLUDE_INTEGRATIONS="true"
            shift 1
            ;;
        --include-comparisons)
            INCLUDE_COMPARISONS="true"
            shift 1
            ;;
        --results)
            RESULTS_PATH="${2:-}"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            printf '%sUnknown argument: %s%s\n' "${C_RED}" "$1" "${C_NC}" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ -z "${PLATFORM}" ]]; then
    printf '%sError: --platform is required.%s\n' "${C_RED}" "${C_NC}" >&2
    usage >&2
    exit 2
fi

case "${PLATFORM}" in
    editmode|playmode|standalone) ;;
    *)
        printf '%sError: --platform must be editmode|playmode|standalone (got %s).%s\n' \
            "${C_RED}" "${PLATFORM}" "${C_NC}" >&2
        exit 2
        ;;
esac

UNITY_VERSION_RESOLVED="${UNITY_VERSION_ARG:-${UNITY_VERSION_DEFAULT}}"

if [[ -z "${RESULTS_PATH}" ]]; then
    RESULTS_PATH="${ARTIFACTS_DIR}/results.xml"
fi

if [[ "${RESULTS_PATH}" != /* ]] && [[ ! "${RESULTS_PATH}" =~ ^[A-Za-z]:[\\/] ]]; then
    RESULTS_PATH="${REPO_ROOT}/${RESULTS_PATH#./}"
fi

RESULTS_DIR="$(dirname "${RESULTS_PATH}")"
RESULTS_BASENAME="$(basename "${RESULTS_PATH}")"
if [[ "${RESULTS_BASENAME}" == "." ]] || [[ "${RESULTS_BASENAME}" == ".." ]]; then
    printf '%sError: --results path must be a file within the repo (got %s).%s\n' \
        "${C_RED}" "${RESULTS_PATH}" "${C_NC}" >&2
    exit 2
fi
mkdir -p "${RESULTS_DIR}"
RESULTS_DIR_REAL="$(cd "${RESULTS_DIR}" && pwd -P)"
RESULTS_PATH="${RESULTS_DIR_REAL}/${RESULTS_BASENAME}"

# ---------------------------------------------------------------------------
# Image tag selection
# ---------------------------------------------------------------------------
if [[ "${PLATFORM}" == "standalone" ]]; then
    IMAGE_TAG="${UNITY_VERSION_RESOLVED}-linux-il2cpp-3"
else
    IMAGE_TAG="${UNITY_VERSION_RESOLVED}-base-3"
fi
IMAGE_REF="unityci/editor:${IMAGE_TAG}"
UNITY_LIBRARY_CACHE_SOURCE="$(
    printf 'dxm-unity-library-%s-%s' "${IMAGE_TAG}" "${PLATFORM}" \
        | tr -c 'A-Za-z0-9_.-' '-'
)"

# ---------------------------------------------------------------------------
# Assembly include list
# ---------------------------------------------------------------------------
build_assembly_list() {
    local include_perf="$1"
    local include_integrations="$2"
    local include_comparisons="$3"
    local target="$4"
    local runtime_only="$5"
    local node_script

    # Pass the include options through to defaultIncludeAssemblies so the
    # opt-in semantics defined in scripts/unity/lib/asmdef-discovery.js are
    # the single source of truth. target keeps EditMode, PlayMode, and
    # standalone assembly compatibility aligned with CI.
    local opts="{ includePerf: ${include_perf}, includeIntegrations: ${include_integrations}, includeComparisons: ${include_comparisons}, target: '${target}', runtimeOnly: ${runtime_only} }"
    node_script="const m=require('./scripts/unity/lib/asmdef-discovery.js');"
    node_script+="console.log(m.defaultIncludeAssemblies(process.cwd(), ${opts}).join(';'));"

    (cd "${REPO_ROOT}" && node -e "${node_script}")
}

# standalone runs the IL2CPP player, so it must use runtime-only assemblies.
RUNTIME_ONLY="false"
if [[ "${PLATFORM}" == "standalone" ]]; then
    RUNTIME_ONLY="true"
fi

ASSEMBLIES="$(build_assembly_list "${INCLUDE_PERF}" "${INCLUDE_INTEGRATIONS}" "${INCLUDE_COMPARISONS}" "${PLATFORM}" "${RUNTIME_ONLY}")"
if [[ -z "${ASSEMBLIES}" ]]; then
    printf '%sError: assembly include list is empty (asmdef discovery failed).%s\n' \
        "${C_RED}" "${C_NC}" >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# Argument-level path validation (before docker/license checks so users see
# the most relevant error first regardless of system state).
# ---------------------------------------------------------------------------

# m5: --results paths outside the repo cannot be bind-mounted; reject loudly
# rather than silently rewriting (previous behavior was confusing UX).
REPO_ROOT_REAL="$(cd "${REPO_ROOT}" && pwd -P)"
if [[ "${RESULTS_PATH}" == "${REPO_ROOT_REAL}" ]] || [[ "${RESULTS_PATH}" != "${REPO_ROOT_REAL}/"* ]]; then
    printf '%sError: --results path must be within the repo (got %s).%s\n' \
        "${C_RED}" "${RESULTS_PATH}" "${C_NC}" >&2
    printf 'The bind-mounted /workspace inside the unityci/editor container only\n' >&2
    printf 'exposes the repo. Either omit --results or use a path under\n' >&2
    printf '.artifacts/ or .unity-test-project/.\n' >&2
    exit 2
fi
RESULTS_REL="${RESULTS_PATH#"${REPO_ROOT_REAL}/"}"
RESULTS_CONTAINER="/workspace/${RESULTS_REL}"

# ---------------------------------------------------------------------------
# Local mode preconditions
# ---------------------------------------------------------------------------
if ! docker info >/dev/null 2>&1; then
    printf '%sError: docker socket is not reachable.%s\n' "${C_RED}" "${C_NC}" >&2
    printf 'Remediation:\n' >&2
    printf '  1) Verify the docker-outside-of-docker devcontainer feature is\n' >&2
    printf '     enabled (.devcontainer/devcontainer.json).\n' >&2
    printf '  2) Rebuild the devcontainer (Command Palette: "Dev Containers:\n' >&2
    printf '     Rebuild Container").\n' >&2
    printf '  3) On the host, confirm the docker daemon is running:\n' >&2
    printf '     docker info\n' >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# License activation: auto-detect paid serial vs ULF vs failure.
# Serial is the PREFERRED path (UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD ->
# -serial -username -password on the editor; the seat is RETURNED on exit, see
# the trap below, so a local run never leaks it). Current Unity/GameCI behavior
# does not support email/password-only Personal headless activation in docker,
# so Personal users fall back to a .ulf in UNITY_LICENSE (raw) or
# UNITY_LICENSE_B64 (local convenience).
# ---------------------------------------------------------------------------
# Only auto-load a local .ulf when neither a serial NOR an explicit .ulf is set:
# a present serial is the preferred path and must not be shadowed by a stray
# local .ulf on disk.
if [[ -z "${UNITY_SERIAL:-}" ]] && [[ -z "${UNITY_LICENSE:-}" ]] && [[ -z "${UNITY_LICENSE_B64:-}" ]]; then
    UNITY_LICENSE_CANDIDATES=()
    if [[ -n "${UNITY_LICENSE_FILE:-}" ]]; then
        UNITY_LICENSE_CANDIDATES+=("${UNITY_LICENSE_FILE}")
    fi
    if [[ -n "${ProgramData:-}" ]]; then
        UNITY_LICENSE_CANDIDATES+=("${ProgramData}/Unity/Unity_lic.ulf")
    fi
    if [[ -n "${LOCALAPPDATA:-}" ]]; then
        UNITY_LICENSE_CANDIDATES+=("${LOCALAPPDATA}/Unity/Unity_lic.ulf")
    fi
    if [[ -n "${HOME:-}" ]]; then
        UNITY_LICENSE_CANDIDATES+=("${HOME}/.local/share/unity3d/Unity/Unity_lic.ulf")
        UNITY_LICENSE_CANDIDATES+=("${HOME}/Library/Application Support/Unity/Unity_lic.ulf")
    fi
    for license_path in "${UNITY_LICENSE_CANDIDATES[@]}"; do
        if [[ -f "${license_path}" ]]; then
            UNITY_LICENSE="$(cat "${license_path}")"
            export UNITY_LICENSE
            printf '[run-tests] loaded UNITY_LICENSE from %s\n' "${license_path}"
            break
        fi
    done
fi

LICENSE_MODE=""
if [[ -n "${UNITY_SERIAL:-}" ]] && [[ -n "${UNITY_EMAIL:-}" ]] && [[ -n "${UNITY_PASSWORD:-}" ]]; then
    LICENSE_MODE="serial"
elif [[ -n "${UNITY_LICENSE:-}" ]]; then
    LICENSE_MODE="ulf"
elif [[ -n "${UNITY_LICENSE_B64:-}" ]]; then
    LICENSE_MODE="ulf-b64"
else
    printf '%sError: No Unity license configured.%s\n' "${C_RED}" "${C_NC}" >&2
    # Serial-first ordering: serial is the preferred/primary path, the .ulf vars
    # are the fallback. Keep this list in the SAME order as the --help text and the
    # PowerShell mirror so the recommended path is always shown first.
    printf 'Set EITHER:\n' >&2
    printf '  UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD   (preferred paid serial activation)\n' >&2
    printf '  UNITY_LICENSE       (raw .ulf contents; GameCI-compatible)\n' >&2
    printf '  UNITY_LICENSE_B64   (base64 .ulf contents; local shell convenience)\n' >&2
    printf '\nUNITY_EMAIL + UNITY_PASSWORD alone is not a supported headless container license path.\n' >&2
    printf "Run 'bash scripts/unity/activate-license.sh --check' for diagnostics.\n" >&2
    exit 2
fi
printf '[run-tests] license mode: %s\n' "${LICENSE_MODE}"

mkdir -p "${ARTIFACTS_DIR}"

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
        printf 'The script can also auto-detect the path when the current container is inspectable by docker.\n' >&2
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

HOST_REPO_ROOT="$(resolve_host_repo_root)"

USER_UID_VAL="$(id -u)"
USER_GID_VAL="$(id -g)"

# ---------------------------------------------------------------------------
# Build inner Unity command. editmode, playmode, AND standalone all run through
# a single editor invocation: `Unity -runTests -testPlatform <platform>`.
# standalone maps to StandaloneLinux64, which builds AND runs the IL2CPP player
# natively in one pass (IL2CPP backend from ProjectSettings).
# ---------------------------------------------------------------------------

build_editor_cmd_inner() {
    # Single inner command shared by editmode/playmode AND standalone. The only
    # difference is the -testPlatform value: editmode/playmode are passed
    # literally; standalone maps to StandaloneLinux64 so a single editor
    # invocation builds AND runs the IL2CPP player (IL2CPP backend now comes from
    # ProjectSettings; no executeMethod, no separate build pass).
    local cmd
    local project_path_q results_q assemblies_q filter_q test_platform
    project_path_q="$(printf '%q' "/workspace/.unity-test-project")"
    results_q="$(printf '%q' "${RESULTS_CONTAINER}")"
    assemblies_q="$(printf '%q' "${ASSEMBLIES}")"
    if [[ "${PLATFORM}" == "standalone" ]]; then
        test_platform="StandaloneLinux64"
    else
        test_platform="${PLATFORM}"
    fi
    # `set -o pipefail` (part of `set -euo pipefail` below) is LOAD-BEARING for the
    # editor test pipeline `Unity ... | tee log.txt`: without it the pipeline's exit
    # status would be `tee`'s (always 0), masking a failing Unity behind a passing
    # tee. With pipefail a non-zero Unity propagates as the script's exit code.
    #
    # EVERY command in the EXIT trap body below is suffixed `|| true` ON PURPOSE: the
    # trap fires AFTER the editor pipeline has already set the script's exit code, so
    # a license-return failure (or a chown failure) must NEVER overwrite the editor's
    # non-zero exit code with its own. `|| true` forces each cleanup command to a 0
    # status so the trap cannot clobber the real test result.
    cmd=$'set -euo pipefail\n'
    cmd+=$'cleanup_ownership() {\n'
    cmd+=$'    chown -R "${USER_UID}:${USER_GID}" /workspace/.artifacts || true\n'
    cmd+=$'    if [[ -n "${DX_PERF_BASELINE:-}" ]]; then\n'
    cmd+=$'        baseline_path="${DX_PERF_BASELINE}"\n'
    cmd+=$'        [[ "${baseline_path}" = /* ]] || baseline_path="/workspace/${baseline_path}"\n'
    cmd+=$'        if [[ "${baseline_path}" == /workspace/* ]]; then\n'
    cmd+=$'            chown "${USER_UID}:${USER_GID}" "${baseline_path}" 2>/dev/null || true\n'
    cmd+=$'            baseline_dir="$(dirname "${baseline_path}")"\n'
    cmd+=$'            [[ "${baseline_dir}" == "/workspace" ]] || chown -R "${USER_UID}:${USER_GID}" "${baseline_dir}" 2>/dev/null || true\n'
    cmd+=$'        fi\n'
    cmd+=$'    fi\n'
    cmd+=$'    chown -R "${USER_UID}:${USER_GID}" /workspace/.unity-test-project/Library || true\n'
    if [[ "${LICENSE_MODE}" == "serial" ]]; then
        # Serial activation consumes a seat, so we MUST return it on EVERY exit
        # path -- clean exit, test failure, or Ctrl-C -- or a local run leaks the
        # seat. The return runs INSIDE the same EXIT trap as the chown cleanup so
        # it fires even when the editor (and the `tee` pipeline) failed. It is
        # best-effort (|| true): a failed return must never mask the real test
        # exit code. The return log goes to /tmp (NOT under /workspace/.artifacts,
        # which is bind-mounted to the repo and would persist the creds Unity may
        # echo). UNITY_EMAIL/UNITY_PASSWORD are forwarded into the container via
        # -e; we never echo them.
        cmd+=$'    /opt/unity/Editor/Unity -quit -batchmode -nographics -returnlicense -username "${UNITY_EMAIL}" -password "${UNITY_PASSWORD}" -logFile - > /tmp/unity-return-license.log 2>&1 || true\n'
    fi
    cmd+=$'}\n'
    cmd+=$'trap cleanup_ownership EXIT\n'
    cmd+=$'mkdir -p /root/.cache/unity3d\n'
    if [[ "${LICENSE_MODE}" == "ulf" ]] || [[ "${LICENSE_MODE}" == "ulf-b64" ]]; then
        cmd+=$'mkdir -p /root/.local/share/unity3d/Unity\n'
        if [[ "${LICENSE_MODE}" == "ulf-b64" ]]; then
            cmd+=$'printf "%s" "${UNITY_LICENSE_B64}" | base64 -d > /root/.local/share/unity3d/Unity/Unity_lic.ulf\n'
        else
            cmd+=$'printf "%s" "${UNITY_LICENSE}" > /root/.local/share/unity3d/Unity/Unity_lic.ulf\n'
        fi
        cmd+=$'chmod 644 /root/.local/share/unity3d/Unity/Unity_lic.ulf\n'
    fi
    cmd+="/opt/unity/Editor/Unity \\
"
    cmd+="  -batchmode -nographics \\
"
    cmd+="  -projectPath ${project_path_q} \\
"
    cmd+="  -runTests -testPlatform ${test_platform} \\
"
    cmd+="  -testResults ${results_q} \\
"
    cmd+="  -assemblyNames ${assemblies_q} \\
"
    if [[ -n "${TEST_FILTER}" ]]; then
        filter_q="$(printf '%q' "${TEST_FILTER}")"
        cmd+="  -testFilter ${filter_q} \\
"
    fi
    if [[ "${LICENSE_MODE}" == "serial" ]]; then
        cmd+="  -username \"\${UNITY_EMAIL}\" -password \"\${UNITY_PASSWORD}\" -serial \"\${UNITY_SERIAL}\" \\
"
    fi
    cmd+="  -logFile - 2>&1 | tee /workspace/.artifacts/unity/log.txt
"
    printf '%s' "${cmd}"
}

# ---------------------------------------------------------------------------
# Invoke docker
# ---------------------------------------------------------------------------
# Plan banner: one line describing exactly what is about to run, emitted before
# the docker dispatch so the chosen path is visible in local + CI logs.
printf '%s[run-tests] runner=docker platform=%s unity=%s ci=%s%s\n' \
    "${C_BLUE}" "${PLATFORM}" "${UNITY_VERSION_RESOLVED}" "${CI:-}" "${C_NC}"
printf '%sLaunching %s%s\n' "${C_BLUE}" "${IMAGE_REF}" "${C_NC}"
printf '  platform=%s assemblies=%s\n' "${PLATFORM}" "${ASSEMBLIES}"
printf '  results=%s log=%s/log.txt\n' "${RESULTS_PATH}" "${ARTIFACTS_DIR}"
printf '  perf=%s comparisons=%s integrations=%s filter=%s\n' \
    "${INCLUDE_PERF}" "${INCLUDE_COMPARISONS}" "${INCLUDE_INTEGRATIONS}" "${TEST_FILTER:-<none>}"
printf '  host_repo_root=%s\n' "${HOST_REPO_ROOT}"
printf '  library_cache=%s\n' "${UNITY_LIBRARY_CACHE_SOURCE}"

# Standard docker args reused across all platforms.
DOCKER_BASE_ARGS=(
    run --rm
    -v "${HOST_REPO_ROOT}:/workspace:rw"
    -v "${UNITY_LIBRARY_CACHE_SOURCE}:/workspace/.unity-test-project/Library"
    -e UNITY_LICENSE
    -e UNITY_LICENSE_B64
    -e UNITY_SERIAL
    -e UNITY_EMAIL
    -e UNITY_PASSWORD
    -e DX_PERF_COMMIT
    -e DX_PERF_BASELINE
    -e DX_PERF_BASELINE_MODE
    -e "USER_UID=${USER_UID_VAL}"
    -e "USER_GID=${USER_GID_VAL}"
)

EXIT_CODE=0
if [[ "${PLATFORM}" == "standalone" ]]; then
    printf '%sRunning IL2CPP standalone player (native single pass)...%s\n' \
        "${C_BLUE}" "${C_NC}"
fi
# editmode, playmode, and standalone all use the same single editor invocation.
# standalone maps -testPlatform to StandaloneLinux64, which builds AND runs the
# IL2CPP player in one pass (no separate build-log.txt, no executeMethod).
UNITY_CMD_INNER="$(build_editor_cmd_inner)"
# Delete any STALE results.xml before the run so the "honor the FILE on a non-zero
# exit" branch below can only ever honor results THIS run wrote -- never a prior
# run's leftover (a stale passing file + a newly-FAILED run would otherwise report
# a false green). Mirrors run-ci-tests.ps1's delete-before-run discipline.
rm -f "${RESULTS_PATH}"
docker "${DOCKER_BASE_ARGS[@]}" "${IMAGE_REF}" \
    bash -c "${UNITY_CMD_INNER}" \
    || EXIT_CODE=$?

# ---------------------------------------------------------------------------
# Summary tail (B2: delegate parsing to scripts/unity/lib/parse-test-results.py
# so this script and run-tests.ps1 share one parser
# implementation. The helper emits "OK total=.. passed=.. failed=.. skipped=.."
# on success and "PARSE_ERROR:<reason>" on failure with exit code 2.)
# ---------------------------------------------------------------------------
print_results_summary() {
    local results_xml="$1"
    if [[ ! -f "${results_xml}" ]]; then
        printf '%sNo results.xml at %s%s\n' "${C_YELLOW}" "${results_xml}" "${C_NC}"
        if [[ "${CI:-false}" == "true" ]]; then
            printf '::error::run-tests: no results.xml produced for %s/%s -- tests did not run\n' \
                "${PLATFORM}" "${UNITY_VERSION_RESOLVED}"
        fi
        return 2
    fi

    local parser="${REPO_ROOT}/scripts/unity/lib/parse-test-results.py"
    local summary
    if ! summary="$(python3 "${parser}" "${results_xml}")"; then
        printf '%sCould not parse results summary: %s%s\n' \
            "${C_YELLOW}" "${summary}" "${C_NC}"
        if [[ "${CI:-false}" == "true" ]]; then
            printf '::error::run-tests: could not parse results.xml for %s/%s -- %s\n' \
                "${PLATFORM}" "${UNITY_VERSION_RESOLVED}" "${summary}"
        fi
        return 2
    fi

    if [[ "${summary}" != OK* ]]; then
        printf '%sCould not parse results summary: %s%s\n' \
            "${C_YELLOW}" "${summary}" "${C_NC}"
        if [[ "${CI:-false}" == "true" ]]; then
            printf '::error::run-tests: could not parse results.xml for %s/%s -- %s\n' \
                "${PLATFORM}" "${UNITY_VERSION_RESOLVED}" "${summary}"
        fi
        return 2
    fi

    # Strip the "OK " prefix and tokenize key=value pairs.
    summary="${summary#OK }"
    local total="" passed="" failed="" skipped=""
    for kv in ${summary}; do
        case "${kv}" in
            total=*)   total="${kv#total=}" ;;
            passed=*)  passed="${kv#passed=}" ;;
            failed=*)  failed="${kv#failed=}" ;;
            skipped=*) skipped="${kv#skipped=}" ;;
        esac
    done

    if [[ "${total:-0}" == "0" ]]; then
        printf '%sERROR: 0 tests ran. Check filter / assembly list.%s\n' \
            "${C_RED}" "${C_NC}" >&2
        printf '  failed=%s passed=%s skipped=%s\n' \
            "${failed:-0}" "${passed:-0}" "${skipped:-0}" >&2
        if [[ "${CI:-false}" == "true" ]]; then
            printf '::error::run-tests: 0 tests ran (total=0) for %s/%s -- check assembly list / filter\n' \
                "${PLATFORM}" "${UNITY_VERSION_RESOLVED}"
        fi
        return 2
    fi

    if [[ "${failed:-0}" == "0" ]]; then
        printf '%sPASS%s %s passed (total=%s skipped=%s)\n' \
            "${C_GREEN}" "${C_NC}" "${passed:-0}" "${total}" "${skipped:-0}"
        if [[ "${CI:-false}" == "true" ]]; then
            printf '::notice::run-tests: %s passed (total=%s skipped=%s) for %s/%s\n' \
                "${passed:-0}" "${total}" "${skipped:-0}" "${PLATFORM}" "${UNITY_VERSION_RESOLVED}"
        fi
        return 0
    fi

    printf '%sFAIL%s %s failed of %s (passed=%s skipped=%s)\n' \
        "${C_RED}" "${C_NC}" "${failed}" "${total}" "${passed:-0}" "${skipped:-0}"
    if [[ "${CI:-false}" == "true" ]]; then
        printf '::error::run-tests: %s failed of %s for %s/%s (passed=%s skipped=%s)\n' \
            "${failed}" "${total}" "${PLATFORM}" "${UNITY_VERSION_RESOLVED}" "${passed:-0}" "${skipped:-0}"
    fi
    return 1
}

SUMMARY_EXIT=0
print_results_summary "${RESULTS_PATH}" || SUMMARY_EXIT=$?

# Scan the Unity log for 2FA / activation-failure signatures. We only emit
# the remediation block when Unity actually exited non-zero; a successful
# run can still mention these strings in package metadata or stack traces.
detect_activation_failure() {
    local log_file="${ARTIFACTS_DIR}/log.txt"
    [[ -f "${log_file}" ]] || return 1
    grep -qE "2FA|two-factor|verification code|License client failed|LICENSE SYSTEM .* (Failed|invalid)|com\\.unity\\.editor\\.headless|No valid Unity Editor license found" \
        "${log_file}" 2>/dev/null
}

if [[ "${EXIT_CODE}" -ne 0 ]]; then
    printf '%sUnity exited with code %s.%s\n' "${C_RED}" "${EXIT_CODE}" "${C_NC}"
    if detect_activation_failure; then
        printf '\n' >&2
        printf '%sERROR: Unity license activation failed; common causes:%s\n' \
            "${C_RED}" "${C_NC}" >&2
        printf '  1. UNITY_LICENSE is not raw .ulf contents, or UNITY_LICENSE_B64 is not valid base64 .ulf contents.\n' >&2
        printf '  2. UNITY_SERIAL is missing, expired, or does not match the Unity account.\n' >&2
        printf '  3. Wrong UNITY_EMAIL/UNITY_PASSWORD (check for typos, especially trailing\n' >&2
        # shellcheck disable=SC2016
        printf '     newlines from `cat`).\n' >&2
        printf '  4. Activation rate limit (Unity throttles repeated activations from one IP).\n' >&2
        printf '     Wait 1 hour and retry.\n' >&2
        printf 'See .llm/skills/unity/unity-license-bootstrap.md for details.\n' >&2
    fi
fi

# The results.xml is the SOURCE OF TRUTH. When the validator says the run passed
# (SUMMARY_EXIT 0), a non-zero Unity/docker exit is a benign post-work shutdown
# crash (Unity can fault in a background thread during teardown AFTER RunFinished
# wrote the file) -- honor the FILE and pass with a warning, never turn a passing
# run red on the shutdown exit code. Otherwise a non-zero Unity exit still fails
# (preferred over the summary's generic missing-file code so the operator sees the
# real exit / activation cause), and a clean Unity exit defers to the summary
# (which fails loudly on a missing / zero-test / unparseable results.xml).
if [[ "${SUMMARY_EXIT}" -eq 0 ]] && [[ "${EXIT_CODE}" -ne 0 ]]; then
    printf '%sWARNING run-tests: Unity exited with code %s but wrote a valid passing results.xml at %s; honoring the FILE as the source of truth (benign post-work shutdown crash).%s\n' \
        "${C_YELLOW}" "${EXIT_CODE}" "${RESULTS_PATH}" "${C_NC}"
    if [[ "${CI:-false}" == "true" ]]; then
        printf '::warning::run-tests: Unity exited with code %s but wrote a valid passing results.xml for %s/%s; honoring the FILE as the source of truth (benign post-work shutdown crash)\n' \
            "${EXIT_CODE}" "${PLATFORM}" "${UNITY_VERSION_RESOLVED}"
    fi
    exit 0
fi
if [[ "${EXIT_CODE}" -ne 0 ]]; then
    exit "${EXIT_CODE}"
fi
exit "${SUMMARY_EXIT}"
