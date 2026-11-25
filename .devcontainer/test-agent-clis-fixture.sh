#!/usr/bin/env bash
# shellcheck shell=bash
# Shell commands invoked by the hermetic devcontainer agent CLI tests.

case "${0##*/}" in
npm)
    printf '%s\n' "$*" >>"${NPM_CALL_LOG}"
    case "$1" in
        view)
            if [[ -n "${NPM_VIEW_FAILS}" ]]; then exit 1; fi
            if [[ "$2" == "@opencode/cli@2" && "$4" == "--json" ]]; then
                printf '%s\n' "${NPM_OPENCODE_VERSIONS}"
                exit
            fi
            printf '%s\n' "${NPM_LATEST}"
            ;;
        uninstall)
            [[ "$*" == "uninstall -g opencode-ai" ]] || exit 1
            rm "${NPM_CONFIG_PREFIX}/bin/opencode"
            ;;
        install)
            if [[ -n "${NPM_INSTALL_FAILS}" ]]; then exit 1; fi
            if [[ -n "${NPM_INSTALL_NOOP}" ]]; then exit 0; fi
            spec="$3"
            case "${spec%@*}" in
                @openai/codex) shim="codex" ;;
                @anthropic-ai/claude-code) [[ " $* " == *" --allow-scripts=@anthropic-ai/claude-code "* ]] || exit 1; shim="claude" ;;
                @opencode/cli)
                    [[ " $* " == *" --allow-scripts=@opencode/cli "* ]] || exit 1
                    shim="opencode"
                    [[ ! -L "${NPM_CONFIG_PREFIX}/bin/opencode" ]] || exit 1
                    ;;
                @nanocollective/nanocoder) shim="nanocoder" ;;
                *) exit 1 ;;
            esac
            target="${NPM_CONFIG_PREFIX}/bin/${shim}"
            printf '#!/usr/bin/env bash\necho %s\n' "${spec##*@}" >"${target}"
            chmod +x "${target}"
            ;;
        *) exit 2 ;;
    esac
    exit $?
    ;;
esac

case "$1" in
post-create)
    shift
    # shellcheck source=.devcontainer/post-create.sh
    source "$1"
    for stub in fix_volume_permissions ensure_path_line dotnet configure_agent_mcps git pre-commit ensure_pinned_sdk validate_dotnet validate_workspace print_summary; do
        eval "$stub() { :; }"
    done
    ensure_pinned_sdk() { printf '%s\n' sdk >>"$TMPDIR/sdk-order"; }
    dotnet() {
        if [[ "$*" == "tool restore" ]]; then
            printf '%s\n' restore >>"$TMPDIR/sdk-order"
        fi
    }
    install_agent_clis() { printf '%s' "$BASH_SUBSHELL" >"$TMPDIR/refresh-scope"; }
    npm() {
        if [[ "$*" == "config get prefix" ]]; then
            echo "$HOME/.local"
        elif [[ "$1" == install ]]; then
            printf '%s' "$BASH_SUBSHELL" >"$TMPDIR/npm-scope"
        fi
    }
    main
    wait
    cmp "$TMPDIR/refresh-scope" "$TMPDIR/npm-scope" &&
    [[ "$(cat "$TMPDIR/sdk-order")" == $'sdk\nrestore' ]]
    ;;
cache-permissions)
    shift
    # shellcheck source=.devcontainer/cache-contract.sh
    source "$1"
    if [[ "$REPAIR" == directory ]]; then
        cache_contract_repair_directory "$WORKSPACE_FOLDER" "$(id -u)" "$(id -g)"
        exit
    fi
    cache_contract_repair_directory() { :; }
    sudo() {
        [[ "$2" == chown && "$3" == -h ]] || return 99
        case "$REPAIR" in
            real) command sudo "$@" ;;
            noop) return 0 ;;
            *) return 1 ;;
        esac
    }
    cache_contract_repair_permissions
    ;;
pinned-sdk)
    shift
    set -euo pipefail
    # shellcheck source=.devcontainer/lib/pinned-sdk.sh
    source "$1"
    flock() { return 0; }
    dotnet() { if [[ -f "$TMPDIR/ready" ]]; then echo 10.0.404; else echo 10.0.401; fi; }
    curl() { [[ "$MODE" != download-fails ]]; }
    sudo() {
        [[ "$MODE" != install-fails ]] || return 1
        if [[ "$MODE" != install-noop ]]; then touch "$TMPDIR/ready"; fi
    }
    if [[ "$MODE" == ready ]]; then touch "$TMPDIR/ready"; fi
    ensure_pinned_sdk "$TMPDIR"
    ;;
*) exit 2 ;;
esac
