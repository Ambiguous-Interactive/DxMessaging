#!/usr/bin/env bash
# shellcheck shell=bash
# =============================================================================
# parse-devcontainer-mounts.sh
# =============================================================================
# Robust JSONC (JSON with comments) parsing for devcontainer.json. The Node
# counterpart lives at scripts/lib/devcontainer-jsonc.js; the two MUST agree
# on the comment-stripping algorithm. A Jest parity test exercises both
# implementations against a shared fixture set.
#
# Public surface:
#   strip_jsonc_comments <file>
#     Print the comment-stripped JSON to stdout. Comments inside JSON string
#     literals are preserved verbatim. A single leading UTF-8 BOM (EF BB BF)
#     is stripped; CR characters from CRLF line endings are normalized to LF.
#
#   parse_devcontainer_mounts <file> <containerWorkspaceFolder> [<localWorkspaceFolder>]
#     Strip JSONC comments, pipe to jq, expand the documented template
#     variables, and print one resolved mount string per line. The function
#     FAILS LOUDLY (non-zero exit + stderr message) on jq parse errors. There
#     is no silent grep fallback.
#
#   get_devcontainer_property <file> <property>
#     Strip JSONC comments and use jq to extract a top-level scalar property.
#     Output is the raw scalar value (no surrounding quotes). When the
#     property is absent or null, the function exits 0 with empty stdout.
#     The function FAILS LOUDLY (non-zero exit + stderr message) on jq parse
#     errors. Use this in any .devcontainer/*.sh script that previously used
#     a brittle `grep ... \.json"` pattern -- the grep approach silently
#     misses commented-out keys and string-literal "matches".
# =============================================================================

[[ "${_DXM_DEVCONTAINER_JSONC_LOADED:-}" == "1" ]] && return 0
_DXM_DEVCONTAINER_JSONC_LOADED=1

# Strip JSONC comments from stdin / file argument. Implementation mirrors
# stripJsoncComments() in scripts/lib/devcontainer-jsonc.js: a four-state
# machine tracking (default, in-string, in-line-comment, in-block-comment).
# Comments are replaced with spaces (not removed) so character offsets do not
# shift; newlines inside block comments are preserved so line numbers stay
# aligned for downstream jq diagnostics.
strip_jsonc_comments() {
    local file="$1"
    if [[ -z "$file" ]]; then
        echo "strip_jsonc_comments: missing file argument" >&2
        return 2
    fi
    if [[ ! -f "$file" ]]; then
        echo "strip_jsonc_comments: file not found: $file" >&2
        return 2
    fi

    # We use awk for the state machine. Bash's per-character loop would work
    # but is roughly 10x slower on devcontainer.json (~6 KB) and we want the
    # validator to stay snappy. A leading UTF-8 BOM (0xEF 0xBB 0xBF) is
    # stripped from the very first line BEFORE state-machine entry; jq
    # tolerates a BOM but the Node JSON.parse counterpart does not, and the
    # parity test enforces byte-equivalent output. CR bytes from CRLF line
    # endings are normalized to LF so output line counts match the Node side.
    awk '
        BEGIN {
            in_string = 0
            in_line = 0
            in_block = 0
            first_line = 1
        }
        {
            line = $0
            # Strip trailing CR (CRLF -> LF normalization).
            sub(/\r$/, "", line)
            if (first_line == 1) {
                # Strip a single leading UTF-8 BOM if present.
                if (length(line) >= 3 \
                    && substr(line, 1, 1) == sprintf("%c", 239) \
                    && substr(line, 2, 1) == sprintf("%c", 187) \
                    && substr(line, 3, 1) == sprintf("%c", 191)) {
                    line = substr(line, 4)
                }
                first_line = 0
            }
            len = length(line)
            out = ""
            i = 1
            while (i <= len) {
                ch = substr(line, i, 1)
                nx = (i < len) ? substr(line, i + 1, 1) : ""

                if (in_line) {
                    out = out " "
                    i++
                    continue
                }

                if (in_block) {
                    if (ch == "*" && nx == "/") {
                        out = out "  "
                        i += 2
                        in_block = 0
                        continue
                    }
                    out = out " "
                    i++
                    continue
                }

                if (in_string) {
                    out = out ch
                    if (ch == "\\" && i < len) {
                        out = out nx
                        i += 2
                        continue
                    }
                    if (ch == "\"") {
                        in_string = 0
                    }
                    i++
                    continue
                }

                if (ch == "\"") {
                    in_string = 1
                    out = out ch
                    i++
                    continue
                }

                if (ch == "/" && nx == "/") {
                    in_line = 1
                    out = out "  "
                    i += 2
                    continue
                }

                if (ch == "/" && nx == "*") {
                    in_block = 1
                    out = out "  "
                    i += 2
                    continue
                }

                out = out ch
                i++
            }
            print out
            # Line endings reset in_line but never in_block.
            if (in_line) {
                in_line = 0
            }
        }
    ' "$file"
}

# Extract a top-level scalar property from a JSONC file. The output is the
# raw scalar value (no surrounding quotes). When the property is absent or
# null the function exits 0 with empty stdout. jq parse errors exit 2.
#
# Non-scalar policy (round-3 MINOR-C, locked in by the parity test at
# scripts/__tests__/devcontainer-jsonc.test.js): when the requested
# property resolves to an array or object, this function FAILS LOUDLY
# (exit 2 + descriptive stderr message). The Node counterpart
# `getDevcontainerProperty` in scripts/lib/devcontainer-jsonc.js throws a
# TypeError with the same semantics. Composite values must be read via
# `strip_jsonc_comments <file> | jq <expression>` (or
# `parseDevcontainerMounts` on the Node side); the legacy behavior --
# returning jq's `tostring` of the composite -- silently disagreed with
# the Node `String(value)` shape ("[object Object]" / "1,2,3").
#
# Use this helper in .devcontainer/*.sh scripts instead of grep against the
# raw JSON -- grep cannot distinguish between commented-out keys, string
# literal "matches", or duplicate keys.
get_devcontainer_property() {
    local file="$1"
    local property="$2"

    if [[ -z "$file" ]]; then
        echo "get_devcontainer_property: missing file argument" >&2
        return 2
    fi
    if [[ -z "$property" ]]; then
        echo "get_devcontainer_property: missing property argument" >&2
        return 2
    fi
    if [[ ! -f "$file" ]]; then
        echo "get_devcontainer_property: file not found: $file" >&2
        return 2
    fi
    if ! command -v jq >/dev/null 2>&1; then
        echo "get_devcontainer_property: jq is required but not installed" >&2
        return 1
    fi

    local stripped
    if ! stripped="$(strip_jsonc_comments "$file")"; then
        echo "get_devcontainer_property: failed to strip JSONC comments from ${file}" >&2
        return 2
    fi

    # First detect whether the value is a non-scalar (array/object) so we can
    # FAIL LOUDLY with a clear message instead of letting jq's `tostring` emit
    # a JSON-shaped string that the Node counterpart would never produce. The
    # value-type probe and the scalar extraction are two separate jq calls so
    # the diagnostic message can name the offending type concretely.
    local type_stderr
    type_stderr="$(mktemp)"
    local value_type
    if ! value_type="$(printf '%s' "$stripped" \
            | jq -r --arg key "$property" '
                if has($key) then (.[$key] | type) else "absent" end
            ' 2>"$type_stderr")"; then
        echo "get_devcontainer_property: jq failed to parse ${file}" >&2
        if [[ -s "$type_stderr" ]]; then
            local type_excerpt
            type_excerpt="$(head -c 400 "$type_stderr")"
            echo "get_devcontainer_property: jq stderr (truncated): ${type_excerpt}" >&2
        fi
        rm -f "$type_stderr"
        return 2
    fi
    rm -f "$type_stderr"

    case "$value_type" in
        array|object)
            echo "get_devcontainer_property: property '${property}' resolves to a non-scalar ${value_type}; use 'strip_jsonc_comments <file> | jq <expression>' (or parseDevcontainerMounts on the Node side) for composite values." >&2
            return 2
            ;;
    esac

    local jq_stderr
    jq_stderr="$(mktemp)"
    local value
    if ! value="$(printf '%s' "$stripped" \
            | jq -r --arg key "$property" '
                if has($key) then
                    (.[$key])
                    | if . == null then "" else (if type == "string" then . else tostring end) end
                else
                    ""
                end
            ' 2>"$jq_stderr")"; then
        echo "get_devcontainer_property: jq failed to parse ${file}" >&2
        if [[ -s "$jq_stderr" ]]; then
            local jq_excerpt
            jq_excerpt="$(head -c 400 "$jq_stderr")"
            echo "get_devcontainer_property: jq stderr (truncated): ${jq_excerpt}" >&2
        fi
        rm -f "$jq_stderr"
        return 2
    fi
    rm -f "$jq_stderr"

    printf '%s' "$value"
}

# Parse devcontainer.json mounts and emit resolved mount strings.
# Args:
#   $1: path to devcontainer.json
#   $2: containerWorkspaceFolder (resolved path; substituted for the template)
#   $3: localWorkspaceFolder (optional; substituted for ${localWorkspaceFolder})
# Output: one mount entry per line on stdout.
# Exit codes:
#   0 on success
#   1 when jq is unavailable (the caller must surface this loudly)
#   2 when input file is missing or jq could not parse the stripped text
parse_devcontainer_mounts() {
    local file="$1"
    local container_folder="$2"
    local local_folder="${3:-}"

    if [[ -z "$file" ]]; then
        echo "parse_devcontainer_mounts: missing file argument" >&2
        return 2
    fi
    if [[ ! -f "$file" ]]; then
        echo "parse_devcontainer_mounts: file not found: $file" >&2
        return 2
    fi
    if ! command -v jq >/dev/null 2>&1; then
        echo "parse_devcontainer_mounts: jq is required but not installed" >&2
        echo "parse_devcontainer_mounts: install jq (https://jqlang.github.io/jq/) and re-run" >&2
        return 1
    fi

    local stripped
    if ! stripped="$(strip_jsonc_comments "$file")"; then
        echo "parse_devcontainer_mounts: failed to strip JSONC comments from ${file}" >&2
        return 2
    fi

    local jq_stderr
    local mounts_json
    jq_stderr="$(mktemp)"
    if ! mounts_json="$(printf '%s' "$stripped" \
            | jq -r '.mounts[]? // empty | if type == "string" then . else (
                ["source=", (.source // ""), ",target=", (.target // ""), ",type=", (.type // "")] | join("")
            ) end' 2>"$jq_stderr")"; then
        echo "parse_devcontainer_mounts: jq failed to parse ${file}" >&2
        if [[ -s "$jq_stderr" ]]; then
            local jq_excerpt
            jq_excerpt="$(head -c 400 "$jq_stderr")"
            echo "parse_devcontainer_mounts: jq stderr (truncated): ${jq_excerpt}" >&2
        fi
        echo "parse_devcontainer_mounts: hint -- run jq manually to triage: jq '.mounts' <(bash .devcontainer/lib/parse-devcontainer-mounts.sh strip ${file})" >&2
        rm -f "$jq_stderr"
        return 2
    fi
    rm -f "$jq_stderr"

    if [[ -z "$mounts_json" ]]; then
        return 0
    fi

    # Template substitution. We do this AFTER jq so a template literal inside
    # a JSON string cannot be mistaken for a comment or a key.
    while IFS= read -r line; do
        if [[ -n "$container_folder" ]]; then
            line="${line//\$\{containerWorkspaceFolder\}/${container_folder}}"
        fi
        if [[ -n "$local_folder" ]]; then
            line="${line//\$\{localWorkspaceFolder\}/${local_folder}}"
        fi
        printf '%s\n' "$line"
    done <<< "$mounts_json"
}

# CLI dispatcher so the script is usable both as a sourced library and as a
# standalone tool (handy for ad-hoc debugging and for the Jest parity test
# which shells out to the bash implementation).
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    cmd="${1:-}"
    case "$cmd" in
        strip)
            shift
            strip_jsonc_comments "$@"
            ;;
        mounts)
            shift
            parse_devcontainer_mounts "$@"
            ;;
        property)
            shift
            get_devcontainer_property "$@"
            ;;
        *)
            echo "Usage: $0 {strip|mounts|property} <devcontainer.json> [<containerWorkspaceFolder>|<property>] [<localWorkspaceFolder>]" >&2
            exit 2
            ;;
    esac
fi
