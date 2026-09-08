#!/usr/bin/env bash
# Install and launch isolated Z.ai and OpenRouter backends without changing
# native Codex or Claude defaults.

set -euo pipefail

readonly PROFILE_ZAI="devcontainer-zai"
readonly PROFILE_OPENROUTER="devcontainer-openrouter"
readonly ZAI_RESPONSES_URL="https://api.z.ai/api/v1"
readonly ZAI_ANTHROPIC_URL="https://api.z.ai/api/anthropic"
readonly OPENROUTER_RESPONSES_URL="https://openrouter.ai/api/v1"
readonly OPENROUTER_ANTHROPIC_URL="https://openrouter.ai/api"

die() {
    printf '[ai-backends] ERROR: %s\n' "$*" >&2
    exit 1
}

resolve_action() {
    local invoked_as
    invoked_as="$(basename "$0")"
    case "${invoked_as}" in
        codex-zai|claude-zai|codex-openrouter|claude-openrouter)
            printf '%s\n' "${invoked_as}"
            ;;
        *)
            printf '%s\n' "${1:-help}"
            ;;
    esac
}

resolve_zai_key() {
    if [ -n "${ZAI_API_KEY:-}" ]; then
        printf '%s' "${ZAI_API_KEY}"
        return
    fi
    if [ -n "${Z_AI_API_KEY:-}" ]; then
        printf '%s' "${Z_AI_API_KEY}"
        return
    fi
    die "Set ZAI_API_KEY (or Z_AI_API_KEY) before launching a Z.ai backend."
}

resolve_openrouter_key() {
    if [ -n "${OPENROUTER_API_KEY:-}" ]; then
        printf '%s' "${OPENROUTER_API_KEY}"
        return
    fi
    die "Set OPENROUTER_API_KEY before launching an OpenRouter backend."
}

install_codex_zai_profile() {
    local codex_home catalog_file catalog_path_toml catalog_tmp profile_tmp
    codex_home="${CODEX_HOME:-${HOME}/.codex}"
    catalog_file="${codex_home}/${PROFILE_ZAI}-models.json"
    local profile_file="${codex_home}/${PROFILE_ZAI}.config.toml"
    catalog_path_toml="${catalog_file//\\/\\\\}"
    catalog_path_toml="${catalog_path_toml//\"/\\\"}"

    mkdir -p "${codex_home}"
    chmod 700 "${codex_home}"

    catalog_tmp="$(mktemp "${codex_home}/.${PROFILE_ZAI}-models.XXXXXX")"
    profile_tmp="$(mktemp "${codex_home}/.${PROFILE_ZAI}-profile.XXXXXX")"
    trap 'rm -f "${catalog_tmp:-}" "${profile_tmp:-}"' RETURN

    # Model metadata follows Z.ai's current Codex Responses integration contract.
    cat >"${catalog_tmp}" <<'JSON'
{
  "models": [
    {
      "slug": "glm-5.3",
      "display_name": "glm-5.3",
      "description": "Z.ai flagship coding model",
      "default_reasoning_level": "max",
      "supported_reasoning_levels": [
        {
          "effort": "low",
          "description": "Light reasoning"
        },
        {
          "effort": "high",
          "description": "Enhanced reasoning"
        },
        {
          "effort": "max",
          "description": "Deep reasoning"
        }
      ],
      "shell_type": "shell_command",
      "visibility": "list",
      "supported_in_api": true,
      "priority": 0,
      "base_instructions": "",
      "supports_reasoning_summaries": true,
      "default_reasoning_summary": "none",
      "support_verbosity": false,
      "apply_patch_tool_type": "freeform",
      "truncation_policy": {
        "mode": "bytes",
        "limit": 10000
      },
      "context_window": 1048576,
      "max_context_window": 1048576,
      "effective_context_window_percent": 95,
      "supports_parallel_tool_calls": true,
      "experimental_supported_tools": [],
      "input_modalities": [
        "text"
      ]
    }
  ]
}
JSON

    cat >"${profile_tmp}" <<TOML
model_provider = "ZAI"
model = "glm-5.3"
model_reasoning_effort = "max"
model_catalog_json = "${catalog_path_toml}"

[model_providers.ZAI]
name = "ZAI"
base_url = "${ZAI_RESPONSES_URL}"
env_key = "ZAI_API_KEY"
wire_api = "responses"
request_max_retries = 4
stream_max_retries = 5
stream_idle_timeout_ms = 300000

[shell_environment_policy]
filters = { ZAI_API_KEY = "exclude", Z_AI_API_KEY = "exclude" }
TOML

    chmod 600 "${catalog_tmp}" "${profile_tmp}"
    mv "${catalog_tmp}" "${catalog_file}"
    mv "${profile_tmp}" "${profile_file}"
    # The RETURN trap above only fires in this function's own frame (it is not
    # inherited by other functions), so the successful mv leaves nothing to clean.
    trap - RETURN
}

install_codex_openrouter_profile() {
    local codex_home profile_tmp
    codex_home="${CODEX_HOME:-${HOME}/.codex}"
    local profile_file="${codex_home}/${PROFILE_OPENROUTER}.config.toml"

    mkdir -p "${codex_home}"
    chmod 700 "${codex_home}"

    profile_tmp="$(mktemp "${codex_home}/.${PROFILE_OPENROUTER}-profile.XXXXXX")"
    trap 'rm -f "${profile_tmp:-}"' RETURN

    # Command-based auth makes Codex refresh OpenRouter's live model catalog so
    # pinned slugs get correct context-window and reasoning metadata. The key
    # stays in memory: nothing is persisted, and shell_environment_policy keeps
    # it out of tool subprocesses (the auth command reads the process
    # environment directly and is unaffected by that policy).
    cat >"${profile_tmp}" <<TOML
model_provider = "openrouter"
model = "openai/gpt-5.6-sol"
model_reasoning_effort = "high"

[model_providers.openrouter]
name = "OpenRouter"
base_url = "${OPENROUTER_RESPONSES_URL}"
wire_api = "responses"
request_max_retries = 4
stream_max_retries = 5
stream_idle_timeout_ms = 300000

[model_providers.openrouter.auth]
command = "sh"
args = ["-c", "echo \$OPENROUTER_API_KEY"]

[shell_environment_policy]
filters = { OPENROUTER_API_KEY = "exclude" }
TOML

    chmod 600 "${profile_tmp}"
    mv "${profile_tmp}" "${profile_file}"
    # The RETURN trap above only fires in this function's own frame (it is not
    # inherited by other functions), so the successful mv leaves nothing to clean.
    trap - RETURN
}

install_launchers() {
    local bin_dir codex_command launcher script_path target
    if [ -n "${AI_BACKENDS_BIN_DIR:-}" ]; then
        bin_dir="${AI_BACKENDS_BIN_DIR}"
    elif case ":${PATH}:" in *":${HOME}/.local/bin:"*) true ;; *) false ;; esac; then
        bin_dir="${HOME}/.local/bin"
    else
        codex_command="$(command -v codex || true)"
        if [ -n "${codex_command}" ] && [ -w "$(dirname "${codex_command}")" ]; then
            bin_dir="$(dirname "${codex_command}")"
        else
            bin_dir="${HOME}/.local/bin"
        fi
    fi
    script_path="$(realpath "${BASH_SOURCE[0]}")"
    mkdir -p "${bin_dir}"

    # Validate every destination first so a refusal cannot leave a partially
    # applied install behind.
    for launcher in codex-zai claude-zai codex-openrouter claude-openrouter; do
        target="${bin_dir}/${launcher}"
        if [ -e "${target}" ] && [ ! -L "${target}" ]; then
            die "Refusing to replace non-symlink launcher: ${target}"
        fi
        if [ -L "${target}" ] && [ -e "${target}" ]; then
            # Own earlier launchers may dangle after a repository move; a symlink
            # that still resolves to some other live file is not ours to replace.
            if [ "$(realpath "${target}")" != "${script_path}" ]; then
                die "Refusing to replace launcher symlink pointing elsewhere: ${target}"
            fi
        fi
    done

    for launcher in codex-zai claude-zai codex-openrouter claude-openrouter; do
        ln -sfn "${script_path}" "${bin_dir}/${launcher}"
    done
}

install_backend_support() {
    install_codex_zai_profile
    install_codex_openrouter_profile
    install_launchers
    printf '[ai-backends] Installed codex-zai, claude-zai, codex-openrouter, and claude-openrouter launchers.\n'
}

launch_codex_zai() {
    local catalog_file codex_home model reasoning_effort zai_key
    command -v codex >/dev/null 2>&1 || die "codex is not installed."
    zai_key="$(resolve_zai_key)"
    export ZAI_API_KEY="${zai_key}"

    codex_home="${CODEX_HOME:-${HOME}/.codex}"
    catalog_file="${codex_home}/${PROFILE_ZAI}-models.json"
    if [ ! -f "${codex_home}/${PROFILE_ZAI}.config.toml" ] || [ ! -f "${catalog_file}" ]; then
        install_codex_zai_profile
    fi

    model="${CODEX_ZAI_MODEL:-glm-5.3}"
    reasoning_effort="${CODEX_ZAI_REASONING_EFFORT:-max}"
    case "${reasoning_effort}" in
        low|high|max) ;;
        *) die "CODEX_ZAI_REASONING_EFFORT must be low, high, or max." ;;
    esac
    exec codex \
        --profile "${PROFILE_ZAI}" \
        --model "${model}" \
        --config 'model_provider="ZAI"' \
        --config "model_reasoning_effort=\"${reasoning_effort}\"" \
        --config "model_catalog_json=\"${catalog_file}\"" \
        "$@"
}

launch_codex_openrouter() {
    local codex_home model reasoning_effort openrouter_key
    command -v codex >/dev/null 2>&1 || die "codex is not installed."
    openrouter_key="$(resolve_openrouter_key)"
    export OPENROUTER_API_KEY="${openrouter_key}"

    codex_home="${CODEX_HOME:-${HOME}/.codex}"
    if [ ! -f "${codex_home}/${PROFILE_OPENROUTER}.config.toml" ]; then
        install_codex_openrouter_profile
    fi

    model="${CODEX_OPENROUTER_MODEL:-openai/gpt-5.6-sol}"
    case "${model}" in
        */*) ;;
        *) die "CODEX_OPENROUTER_MODEL must be a full OpenRouter slug with its provider prefix, for example openai/gpt-5.6-sol." ;;
    esac
    reasoning_effort="${CODEX_OPENROUTER_REASONING_EFFORT:-high}"
    case "${reasoning_effort}" in
        low|medium|high|xhigh) ;;
        *) die "CODEX_OPENROUTER_REASONING_EFFORT must be low, medium, high, or xhigh." ;;
    esac
    exec codex \
        --profile "${PROFILE_OPENROUTER}" \
        --model "${model}" \
        --config 'model_provider="openrouter"' \
        --config "model_reasoning_effort=\"${reasoning_effort}\"" \
        "$@"
}

# Shared sandbox resolution for the Claude gateways. Sets CONTAINER_MODE and
# SUBPROCESS_SCRUB globals and runs the bubblewrap preflight when the strong
# subprocess scrubber was requested outside an existing container boundary.
resolve_claude_sandbox() {
    container_mode="${AI_BACKENDS_CONTAINER_MODE:-auto}"
    case "${container_mode}" in
        auto)
            if [ -f /.dockerenv ] || [ -f /run/.containerenv ]; then
                container_mode=yes
            else
                container_mode=no
            fi
            ;;
        yes|no) ;;
        *) die "AI_BACKENDS_CONTAINER_MODE must be auto, yes, or no." ;;
    esac

    subprocess_scrub="${CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB:-auto}"
    case "${subprocess_scrub}" in
        auto)
            if [ "${container_mode}" = "yes" ]; then
                # The devcontainer is already the isolation boundary. Claude's
                # additional Linux scrub sandbox still requires CLONE_NEWUSER
                # and mount operations that Docker's default profiles deny.
                subprocess_scrub=0
            else
                subprocess_scrub=1
            fi
            ;;
        0|1) ;;
        *) die "CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB must be auto, 0, or 1." ;;
    esac

    if [ "$(uname -s)" = "Linux" ] && [ "${subprocess_scrub}" = "1" ]; then
        command -v bwrap >/dev/null 2>&1 \
            || die "bubblewrap is required for Claude subprocess isolation; install bubblewrap and socat."
        command -v socat >/dev/null 2>&1 \
            || die "socat is required for Claude sandbox networking; install bubblewrap and socat."
        bwrap --unshare-user --ro-bind / / --bind /proc /proc --dev /dev true \
            || die "Claude subprocess isolation cannot create its nested sandbox. Set CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB=0 to rely on the outer devcontainer boundary."
    fi
}

# Shared launcher for both Claude gateways. Arguments:
#   $1 base_url, $2 config_dir, $3 timeout_ms, $4 api_key_mode ("unset" or
#   "blank"), $5 auth token, then the user arguments to forward to claude, then
#   a literal "--", then NAME=VALUE environment assignments exported before
#   exec. The wrapper appends exactly one "--", so splitting at the LAST "--"
#   forwards a user-supplied "--" to claude instead of exporting user arguments
#   as environment variables.
launch_claude_gateway() {
    local base_url="$1"
    local config_dir="$2"
    local timeout_ms="$3"
    local api_key_mode="$4"
    local auth_token="$5"
    shift 5
    local -a claude_args=()
    local -a user_args=()
    local -a env_pairs=()
    local -a all_args=("$@")
    local arg index separator=-1 total=${#all_args[@]}

    # The wrapper appends exactly one "--", so the LAST "--" separates user
    # arguments from the launcher's NAME=VALUE assignments. Earlier "--" values
    # belong to the user and are forwarded verbatim.
    for ((index = 0; index < total; ++index)); do
        if [ "${all_args[index]}" = "--" ]; then
            separator=${index}
        fi
    done
    for ((index = 0; index < total; ++index)); do
        if [ "${index}" -eq "${separator}" ]; then
            continue
        elif [ "${separator}" != "-1" ] && [ "${index}" -gt "${separator}" ]; then
            env_pairs+=("${all_args[index]}")
        else
            user_args+=("${all_args[index]}")
        fi
    done
    for arg in "${env_pairs[@]}"; do
        case "${arg}" in
            [A-Za-z_][A-Za-z0-9_]*=*) ;;
            *) die "Internal launcher error: expected NAME=VALUE after '--', got '${arg}'." ;;
        esac
    done
    mkdir -p "${config_dir}"
    chmod 700 "${config_dir}"

    resolve_claude_sandbox

    if [ "${container_mode}" = "yes" ]; then
        # The outer devcontainer is the primary isolation boundary. Anthropic's
        # weaker mode still needs blocked namespaces, so keep the inner sandbox
        # off unless the caller explicitly opts into the scrubber and preflight.
        claude_args+=(--settings '{"sandbox":{"enabled":false,"enableWeakerNestedSandbox":true}}')
    fi

    unset ANTHROPIC_API_KEY \
        ANTHROPIC_MODEL \
        ANTHROPIC_DEFAULT_MODEL \
        ANTHROPIC_DEFAULT_HAIKU_MODEL \
        ANTHROPIC_DEFAULT_SONNET_MODEL \
        ANTHROPIC_DEFAULT_OPUS_MODEL \
        ANTHROPIC_DEFAULT_FABLE_MODEL \
        ANTHROPIC_SMALL_FAST_MODEL \
        CLAUDE_CODE_SUBAGENT_MODEL \
        CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY \
        CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST \
        CLAUDE_CODE_USE_ANTHROPIC_AWS \
        CLAUDE_CODE_USE_BEDROCK \
        CLAUDE_CODE_USE_VERTEX \
        CLAUDE_CODE_USE_FOUNDRY \
        CLAUDE_CODE_USE_MANTLE \
        CLAUDE_CODE_USE_GATEWAY
    export ANTHROPIC_AUTH_TOKEN="${auth_token}"
    if [ "${api_key_mode}" = "blank" ]; then
        # OpenRouter's documented contract: the direct-Anthropic credential
        # must be explicitly empty so Claude Code never falls back to it.
        export ANTHROPIC_API_KEY=""
    fi
    export ANTHROPIC_BASE_URL="${base_url}"
    export API_TIMEOUT_MS="${timeout_ms}"
    export CLAUDE_CODE_AUTO_COMPACT_WINDOW=1000000
    export CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1
    export CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST=1
    export CLAUDE_CODE_SUBPROCESS_ENV_SCRUB="${subprocess_scrub}"
    export CLAUDE_CONFIG_DIR="${config_dir}"
    for arg in "${env_pairs[@]}"; do
        export "${arg?}"
    done
    exec claude "${claude_args[@]}" "${user_args[@]}"
}

launch_claude_zai() {
    local config_dir timeout_ms zai_key
    command -v claude >/dev/null 2>&1 || die "claude is not installed."
    zai_key="$(resolve_zai_key)"
    config_dir="${CLAUDE_ZAI_CONFIG_DIR:-${HOME}/.claude-zai}"
    timeout_ms="${ZAI_API_TIMEOUT_MS:-300000}"
    case "${timeout_ms}" in
        ''|*[!0-9]*) die "ZAI_API_TIMEOUT_MS must be a positive integer." ;;
        0) die "ZAI_API_TIMEOUT_MS must be greater than zero." ;;
    esac

    unset ZAI_API_KEY Z_AI_API_KEY
    launch_claude_gateway \
        "${ZAI_ANTHROPIC_URL}" \
        "${config_dir}" \
        "${timeout_ms}" \
        unset \
        "${zai_key}" \
        "$@" \
        -- \
        "ANTHROPIC_DEFAULT_HAIKU_MODEL=${CLAUDE_ZAI_HAIKU_MODEL:-glm-5.3-flash[1m]}" \
        "ANTHROPIC_DEFAULT_SONNET_MODEL=${CLAUDE_ZAI_SONNET_MODEL:-glm-5.3[1m]}" \
        "ANTHROPIC_DEFAULT_OPUS_MODEL=${CLAUDE_ZAI_OPUS_MODEL:-glm-5.3[1m]}"
}

launch_claude_openrouter() {
    local config_dir timeout_ms openrouter_key
    command -v claude >/dev/null 2>&1 || die "claude is not installed."
    openrouter_key="$(resolve_openrouter_key)"
    config_dir="${CLAUDE_OPENROUTER_CONFIG_DIR:-${HOME}/.claude-openrouter}"
    timeout_ms="${CLAUDE_OPENROUTER_TIMEOUT_MS:-300000}"
    case "${timeout_ms}" in
        ''|*[!0-9]*) die "CLAUDE_OPENROUTER_TIMEOUT_MS must be a positive integer." ;;
        0) die "CLAUDE_OPENROUTER_TIMEOUT_MS must be greater than zero." ;;
    esac
    local gateway_discovery="${CLAUDE_OPENROUTER_GATEWAY_DISCOVERY:-1}"
    case "${gateway_discovery}" in
        0|1) ;;
        *) die "CLAUDE_OPENROUTER_GATEWAY_DISCOVERY must be 0 or 1." ;;
    esac
    local -a gateway_pair=()
    if [ "${gateway_discovery}" = "1" ]; then
        gateway_pair=("CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1")
    fi

    unset OPENROUTER_API_KEY
    launch_claude_gateway \
        "${OPENROUTER_ANTHROPIC_URL}" \
        "${config_dir}" \
        "${timeout_ms}" \
        blank \
        "${openrouter_key}" \
        "$@" \
        -- \
        "ANTHROPIC_DEFAULT_FABLE_MODEL=${CLAUDE_OPENROUTER_FABLE_MODEL:-~anthropic/claude-fable-latest}" \
        "ANTHROPIC_DEFAULT_OPUS_MODEL=${CLAUDE_OPENROUTER_OPUS_MODEL:-~anthropic/claude-opus-latest}" \
        "ANTHROPIC_DEFAULT_SONNET_MODEL=${CLAUDE_OPENROUTER_SONNET_MODEL:-~anthropic/claude-sonnet-latest}" \
        "ANTHROPIC_DEFAULT_HAIKU_MODEL=${CLAUDE_OPENROUTER_HAIKU_MODEL:-~anthropic/claude-haiku-latest}" \
        "CLAUDE_CODE_SUBAGENT_MODEL=${CLAUDE_OPENROUTER_SUBAGENT_MODEL:-~anthropic/claude-opus-latest}" \
        "${gateway_pair[@]}"
}

print_help() {
    cat <<'HELP'
Usage:
  bash .devcontainer/ai-backends.sh install
  codex-zai [codex arguments...]
  claude-zai [claude arguments...]
  codex-openrouter [codex arguments...]
  claude-openrouter [claude arguments...]

The ordinary `codex` and `claude` commands retain their native backends.
Set ZAI_API_KEY (or Z_AI_API_KEY) and OPENROUTER_API_KEY only in your private
shell/secret store.

Z.ai defaults: CODEX_ZAI_MODEL (glm-5.3), CODEX_ZAI_REASONING_EFFORT
(low, high, or max), CLAUDE_ZAI_HAIKU_MODEL (glm-5.3-flash[1m]),
CLAUDE_ZAI_SONNET_MODEL and CLAUDE_ZAI_OPUS_MODEL (glm-5.3[1m]).
OpenRouter defaults: CODEX_OPENROUTER_MODEL (openai/gpt-5.6-sol),
CODEX_OPENROUTER_REASONING_EFFORT (low, medium, high, or xhigh),
CLAUDE_OPENROUTER_FABLE_MODEL (~anthropic/claude-fable-latest),
CLAUDE_OPENROUTER_OPUS_MODEL and CLAUDE_OPENROUTER_SUBAGENT_MODEL
(~anthropic/claude-opus-latest), CLAUDE_OPENROUTER_SONNET_MODEL
(~anthropic/claude-sonnet-latest), CLAUDE_OPENROUTER_HAIKU_MODEL
(~anthropic/claude-haiku-latest), CLAUDE_OPENROUTER_GATEWAY_DISCOVERY (0 or 1).

Shared settings: CLAUDE_ZAI_CONFIG_DIR, CLAUDE_OPENROUTER_CONFIG_DIR,
AI_BACKENDS_CONTAINER_MODE (auto, yes, or no), and
CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB (auto, 0, or 1). Per-launcher timeouts:
ZAI_API_TIMEOUT_MS for claude-zai and CLAUDE_OPENROUTER_TIMEOUT_MS for
claude-openrouter (default: five minutes).
HELP
}

action="$(resolve_action "${1:-}")"
case "$(basename "$0")" in
    codex-zai|claude-zai|codex-openrouter|claude-openrouter) ;;
    *)
        if [ "$#" -gt 0 ]; then
            shift
        fi
        ;;
esac

case "${action}" in
    install) install_backend_support ;;
    codex-zai) launch_codex_zai "$@" ;;
    claude-zai) launch_claude_zai "$@" ;;
    codex-openrouter) launch_codex_openrouter "$@" ;;
    claude-openrouter) launch_claude_openrouter "$@" ;;
    help|-h|--help) print_help ;;
    *) die "Unknown action: ${action}" ;;
esac
