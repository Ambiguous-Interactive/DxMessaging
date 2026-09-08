#!/usr/bin/env bash
# Regression coverage for isolated native/Z.ai/OpenRouter CLI backend launchers.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
launcher_script="${repo_root}/.devcontainer/ai-backends.sh"
test_root="$(mktemp -d)"
trap 'rm -rf "${test_root}"' EXIT

test_home="${test_root}/home"
test_bin="${test_root}/bin"
launcher_bin="${test_root}/launchers"
codex_home="${test_root}/codex-home"
mkdir -p "${test_home}" "${test_bin}" "${launcher_bin}"

fail() {
    printf 'FAIL: %s\n' "$*" >&2
    exit 1
}

grep -Eq '^[[:space:]]*bubblewrap[[:space:]]' "${repo_root}/.devcontainer/Dockerfile" \
    || fail "devcontainer does not install bubblewrap for Claude subprocess isolation"
grep -Eq '^[[:space:]]*socat[[:space:]]' "${repo_root}/.devcontainer/Dockerfile" \
    || fail "devcontainer does not install socat for Claude sandbox networking"
grep -Eq '@anthropic-ai/claude-code' "${repo_root}/.devcontainer/Dockerfile" \
    || fail "devcontainer image does not bake the Claude Code CLI"

cat >"${test_bin}/codex" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
{
    printf 'zai_key=%s\n' "${ZAI_API_KEY:+set}"
    printf 'openrouter_key=%s\n' "${OPENROUTER_API_KEY:+set}"
    printf 'arg=%s\n' "$@"
} >"${STUB_LOG:?}"
STUB

cat >"${test_bin}/claude" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
state() {
    # Distinguish set-nonempty, set-empty, and unset for $2's variable NAME.
    local -n variable="$1"
    if [ -n "${variable+x}" ]; then
        if [ -n "${variable}" ]; then
            printf 'set-nonempty'
        else
            printf 'set-empty'
        fi
    else
        printf 'unset'
    fi
}
{
    printf 'auth_token=%s\n' "${ANTHROPIC_AUTH_TOKEN:+set}"
    printf 'zai_key=%s\n' "${ZAI_API_KEY-unset}"
    printf 'zai_key_alias=%s\n' "${Z_AI_API_KEY-unset}"
    printf 'openrouter_key=%s\n' "${OPENROUTER_API_KEY-unset}"
    printf 'native_key=%s\n' "$(state ANTHROPIC_API_KEY)"
    printf 'base_url=%s\n' "${ANTHROPIC_BASE_URL-unset}"
    printf 'timeout=%s\n' "${API_TIMEOUT_MS-unset}"
    printf 'config_dir=%s\n' "${CLAUDE_CONFIG_DIR-unset}"
    printf 'bedrock=%s\n' "${CLAUDE_CODE_USE_BEDROCK-unset}"
    printf 'vertex=%s\n' "${CLAUDE_CODE_USE_VERTEX-unset}"
    printf 'foundry=%s\n' "${CLAUDE_CODE_USE_FOUNDRY-unset}"
    printf 'gateway=%s\n' "${CLAUDE_CODE_USE_GATEWAY-unset}"
    printf 'mantle=%s\n' "${CLAUDE_CODE_USE_MANTLE-unset}"
    printf 'anthropic_aws=%s\n' "${CLAUDE_CODE_USE_ANTHROPIC_AWS-unset}"
    printf 'managed_provider=%s\n' "${CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST-unset}"
    printf 'subprocess_scrub=%s\n' "${CLAUDE_CODE_SUBPROCESS_ENV_SCRUB-unset}"
    printf 'haiku_model=%s\n' "${ANTHROPIC_DEFAULT_HAIKU_MODEL-unset}"
    printf 'sonnet_model=%s\n' "${ANTHROPIC_DEFAULT_SONNET_MODEL-unset}"
    printf 'opus_model=%s\n' "${ANTHROPIC_DEFAULT_OPUS_MODEL-unset}"
    printf 'fable_model=%s\n' "${ANTHROPIC_DEFAULT_FABLE_MODEL-unset}"
    printf 'subagent_model=%s\n' "${CLAUDE_CODE_SUBAGENT_MODEL-unset}"
    printf 'gateway_discovery=%s\n' "${CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY-unset}"
    printf 'compact_window=%s\n' "${CLAUDE_CODE_AUTO_COMPACT_WINDOW-unset}"
    printf 'arg=%s\n' "$@"
} >"${STUB_LOG:?}"
STUB
cat >"${test_bin}/bwrap" <<'STUB'
#!/usr/bin/env bash
if [ "${STUB_BWRAP_FAIL:-0}" = "1" ]; then
    printf 'simulated namespace denial\n' >&2
    exit 1
fi
exit 0
STUB
cat >"${test_bin}/socat" <<'STUB'
#!/usr/bin/env bash
exit 0
STUB
chmod 755 "${test_bin}/codex" "${test_bin}/claude" \
    "${test_bin}/bwrap" "${test_bin}/socat"

HOME="${test_home}" \
CODEX_HOME="${codex_home}" \
AI_BACKENDS_BIN_DIR="${launcher_bin}" \
    bash "${launcher_script}" install

for launcher in codex-zai claude-zai codex-openrouter claude-openrouter; do
    [ -L "${launcher_bin}/${launcher}" ] || fail "${launcher} symlink was not installed"
done

# Containers may not put ~/.local/bin on PATH. In that case install beside the
# writable Codex binary so the launchers are immediately callable.
PATH="${test_bin}:/usr/bin:/bin" \
HOME="${test_home}" \
CODEX_HOME="${codex_home}" \
    bash "${launcher_script}" install
for launcher in codex-zai claude-zai codex-openrouter claude-openrouter; do
    [ -L "${test_bin}/${launcher}" ] || fail "${launcher} was not installed beside Codex"
done

[ "$(stat -c '%a' "${codex_home}/devcontainer-zai.config.toml")" = "600" ] \
    || fail "Codex Z.ai profile permissions are not 600"
[ "$(stat -c '%a' "${codex_home}/devcontainer-openrouter.config.toml")" = "600" ] \
    || fail "Codex OpenRouter profile permissions are not 600"
jq -e '.models[0].slug == "glm-5.3"' \
    "${codex_home}/devcontainer-zai-models.json" >/dev/null \
    || fail "Codex model catalog is invalid"
grep -Eq '^env_key = "ZAI_API_KEY"$' "${codex_home}/devcontainer-zai.config.toml" \
    || fail "Codex Z.ai profile does not use the environment key"
grep -Eq '^model_reasoning_effort = "max"$' "${codex_home}/devcontainer-zai.config.toml" \
    || fail "Codex Z.ai profile does not enable Z.ai reasoning"
grep -Fq 'filters = { ZAI_API_KEY = "exclude", Z_AI_API_KEY = "exclude" }' \
    "${codex_home}/devcontainer-zai.config.toml" \
    || fail "Codex Z.ai profile exposes Z.ai keys to tool subprocesses"
grep -Fq "model_catalog_json = \"${codex_home}/devcontainer-zai-models.json\"" \
    "${codex_home}/devcontainer-zai.config.toml" \
    || fail "Codex Z.ai profile ignored the custom CODEX_HOME catalog path"
grep -Eq '^base_url = "https://openrouter.ai/api/v1"$' \
    "${codex_home}/devcontainer-openrouter.config.toml" \
    || fail "Codex OpenRouter profile does not target the OpenRouter Responses endpoint"
grep -Eq '^wire_api = "responses"$' "${codex_home}/devcontainer-openrouter.config.toml" \
    || fail "Codex OpenRouter profile does not pin the Responses wire API"
grep -Fq 'command = "sh"' "${codex_home}/devcontainer-openrouter.config.toml" \
    || fail "Codex OpenRouter profile does not use command-based auth for catalog refresh"
grep -Fq "echo \$OPENROUTER_API_KEY" "${codex_home}/devcontainer-openrouter.config.toml" \
    || fail "Codex OpenRouter auth command does not resolve the OpenRouter key"
grep -Fq 'filters = { OPENROUTER_API_KEY = "exclude" }' \
    "${codex_home}/devcontainer-openrouter.config.toml" \
    || fail "Codex OpenRouter profile exposes the OpenRouter key to tool subprocesses"
grep -Eq '^model = "openai/gpt-5.6-sol"$' "${codex_home}/devcontainer-openrouter.config.toml" \
    || fail "Codex OpenRouter profile does not pin a full OpenRouter model slug"
if grep -Eq 'experimental_bearer_token|test-secret' \
    "${codex_home}/devcontainer-zai.config.toml" \
    "${codex_home}/devcontainer-openrouter.config.toml"; then
    fail "Codex profile persisted a bearer token"
fi

codex_log="${test_root}/codex.log"
PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
HOME="${test_home}" \
CODEX_HOME="${codex_home}" \
STUB_LOG="${codex_log}" \
ZAI_API_KEY="test-secret" \
CODEX_ZAI_MODEL="glm-test" \
    "${launcher_bin}/codex-zai" exec "hello world"
grep -Eq '^zai_key=set$' "${codex_log}" || fail "Codex did not receive the Z.ai key"
grep -Eq '^arg=--profile$' "${codex_log}" || fail "Codex profile was not selected"
grep -Eq '^arg=devcontainer-zai$' "${codex_log}" || fail "Codex profile name was not forwarded"
grep -Eq '^arg=glm-test$' "${codex_log}" || fail "Codex model override was not forwarded"
grep -Eq '^arg=model_reasoning_effort="max"$' "${codex_log}" \
    || fail "Codex reasoning effort was not forwarded"
grep -Eq '^arg=exec$' "${codex_log}" || fail "Codex subcommand was not forwarded"
if grep -Eq 'test-secret' "${codex_log}"; then
    fail "Codex key leaked into arguments"
fi

codex_openrouter_log="${test_root}/codex-openrouter.log"
PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
HOME="${test_home}" \
CODEX_HOME="${codex_home}" \
STUB_LOG="${codex_openrouter_log}" \
OPENROUTER_API_KEY="test-or-secret" \
CODEX_OPENROUTER_MODEL="anthropic/claude-sonnet-5" \
CODEX_OPENROUTER_REASONING_EFFORT="medium" \
    "${launcher_bin}/codex-openrouter" exec "hello openrouter"
grep -Eq '^openrouter_key=set$' "${codex_openrouter_log}" \
    || fail "Codex did not receive the OpenRouter key"
grep -Eq '^arg=devcontainer-openrouter$' "${codex_openrouter_log}" \
    || fail "Codex OpenRouter profile name was not forwarded"
grep -Eq '^arg=anthropic/claude-sonnet-5$' "${codex_openrouter_log}" \
    || fail "Codex OpenRouter model override was not forwarded"
grep -Eq '^arg=model_reasoning_effort="medium"$' "${codex_openrouter_log}" \
    || fail "Codex OpenRouter reasoning effort was not forwarded"
if grep -Eq 'test-or-secret' "${codex_openrouter_log}"; then
    fail "OpenRouter key leaked into arguments"
fi

invalid_effort_output="${test_root}/invalid-effort.log"
if PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
    HOME="${test_home}" \
    CODEX_HOME="${codex_home}" \
    STUB_LOG="${test_root}/unused.log" \
    OPENROUTER_API_KEY="test-or-secret" \
    CODEX_OPENROUTER_REASONING_EFFORT="ultra" \
    "${launcher_bin}/codex-openrouter" --version >"${invalid_effort_output}" 2>&1; then
    fail "Codex OpenRouter launcher accepted an invalid reasoning effort"
fi
grep -Eq 'CODEX_OPENROUTER_REASONING_EFFORT must be low, medium, high, or xhigh' \
    "${invalid_effort_output}" \
    || fail "Invalid OpenRouter reasoning effort did not provide actionable guidance"

claude_log="${test_root}/claude.log"
PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
HOME="${test_home}" \
STUB_LOG="${claude_log}" \
Z_AI_API_KEY="test-secret" \
ANTHROPIC_API_KEY="native-secret" \
CLAUDE_CODE_USE_BEDROCK=1 \
CLAUDE_CODE_USE_VERTEX=1 \
CLAUDE_CODE_USE_FOUNDRY=1 \
CLAUDE_CODE_USE_GATEWAY=1 \
CLAUDE_CODE_USE_MANTLE=1 \
CLAUDE_CODE_USE_ANTHROPIC_AWS=1 \
CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST=ambient \
ANTHROPIC_MODEL=native-model \
ANTHROPIC_DEFAULT_HAIKU_MODEL=native-haiku \
ANTHROPIC_DEFAULT_SONNET_MODEL=native-sonnet \
ANTHROPIC_DEFAULT_OPUS_MODEL=native-opus \
AI_BACKENDS_CONTAINER_MODE=yes \
STUB_BWRAP_FAIL=1 \
ZAI_API_TIMEOUT_MS="123456" \
    "${launcher_bin}/claude-zai" --print "hello world"
grep -Eq '^auth_token=set$' "${claude_log}" || fail "Claude did not receive the Z.ai token"
grep -Eq '^zai_key=unset$' "${claude_log}" || fail "Claude retained the canonical Z.ai key"
grep -Eq '^zai_key_alias=unset$' "${claude_log}" || fail "Claude retained the Z.ai key alias"
grep -Eq '^native_key=unset$' "${claude_log}" || fail "Claude native key was not isolated"
grep -Eq '^base_url=https://api.z.ai/api/anthropic$' "${claude_log}" \
    || fail "Claude Z.ai endpoint is incorrect"
grep -Eq '^timeout=123456$' "${claude_log}" || fail "Claude timeout override was not forwarded"
grep -Eq "^config_dir=${test_home}/.claude-zai$" "${claude_log}" \
    || fail "Claude Z.ai state was not isolated"
grep -Eq '^bedrock=unset$' "${claude_log}" || fail "Claude Bedrock routing was not isolated"
grep -Eq '^vertex=unset$' "${claude_log}" || fail "Claude Vertex routing was not isolated"
grep -Eq '^foundry=unset$' "${claude_log}" || fail "Claude Foundry routing was not isolated"
grep -Eq '^gateway=unset$' "${claude_log}" || fail "Claude gateway routing was not isolated"
grep -Eq '^mantle=unset$' "${claude_log}" || fail "Claude Mantle routing was not isolated"
grep -Eq '^anthropic_aws=unset$' "${claude_log}" \
    || fail "Claude Platform on AWS routing was not isolated"
grep -Eq '^managed_provider=1$' "${claude_log}" \
    || fail "Claude project settings can override the managed Z.ai provider"
grep -Eq '^subprocess_scrub=0$' "${claude_log}" \
    || fail "Claude enables namespace-dependent subprocess isolation inside a devcontainer"
grep -Eq '^haiku_model=glm-5.3-flash\[1m\]$' "${claude_log}" \
    || fail "Claude Haiku alias was not mapped to Z.ai"
grep -Eq '^sonnet_model=glm-5.3\[1m\]$' "${claude_log}" \
    || fail "Claude Sonnet alias was not mapped to Z.ai"
grep -Eq '^opus_model=glm-5.3\[1m\]$' "${claude_log}" \
    || fail "Claude Opus alias was not mapped to Z.ai"
grep -Eq '^arg=--print$' "${claude_log}" || fail "Claude arguments were not forwarded"
grep -Eq '^arg=--settings$' "${claude_log}" \
    || fail "Claude nested-container settings were not forwarded"
grep -Fq 'arg={"sandbox":{"enabled":false,"enableWeakerNestedSandbox":true}}' "${claude_log}" \
    || fail "Claude inner sandbox was not disabled inside the devcontainer"
if grep -Eq 'test-secret|native-secret' "${claude_log}"; then
    fail "Claude credential leaked into arguments"
fi

claude_openrouter_log="${test_root}/claude-openrouter.log"
PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
HOME="${test_home}" \
STUB_LOG="${claude_openrouter_log}" \
OPENROUTER_API_KEY="test-or-secret" \
ANTHROPIC_API_KEY="native-secret" \
CLAUDE_CODE_USE_BEDROCK=1 \
CLAUDE_CODE_USE_VERTEX=1 \
ANTHROPIC_MODEL=native-model \
ANTHROPIC_DEFAULT_SONNET_MODEL=native-sonnet \
AI_BACKENDS_CONTAINER_MODE=yes \
STUB_BWRAP_FAIL=1 \
    "${launcher_bin}/claude-openrouter" --print "hello openrouter"
grep -Eq '^auth_token=set$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter did not receive the bearer token"
grep -Eq '^openrouter_key=unset$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter retained the canonical OpenRouter key"
grep -Eq '^native_key=set-empty$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter did not explicitly blank ANTHROPIC_API_KEY"
grep -Eq '^base_url=https://openrouter.ai/api$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter endpoint is incorrect"
grep -Eq '^bedrock=unset$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter Bedrock routing was not isolated"
grep -Eq '^vertex=unset$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter Vertex routing was not isolated"
grep -Eq '^managed_provider=1$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter provider is not host-managed"
grep -Eq "^config_dir=${test_home}/.claude-openrouter$" "${claude_openrouter_log}" \
    || fail "Claude OpenRouter state was not isolated"
grep -Eq '^fable_model=~anthropic/claude-fable-latest$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter Fable alias was not mapped"
grep -Eq '^opus_model=~anthropic/claude-opus-latest$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter Opus alias was not mapped"
grep -Eq '^sonnet_model=~anthropic/claude-sonnet-latest$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter Sonnet alias was not mapped"
grep -Eq '^haiku_model=~anthropic/claude-haiku-latest$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter Haiku alias was not mapped"
grep -Eq '^subagent_model=~anthropic/claude-opus-latest$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter subagent model was not mapped"
grep -Eq '^gateway_discovery=1$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter gateway model discovery was not enabled"
grep -Eq '^arg=--print$' "${claude_openrouter_log}" \
    || fail "Claude OpenRouter arguments were not forwarded"
grep -Fq 'arg={"sandbox":{"enabled":false,"enableWeakerNestedSandbox":true}}' \
    "${claude_openrouter_log}" \
    || fail "Claude OpenRouter inner sandbox was not disabled inside the devcontainer"
if grep -Eq 'test-or-secret|native-secret' "${claude_openrouter_log}"; then
    fail "Claude OpenRouter credential leaked into arguments"
fi

claude_openrouter_override_log="${test_root}/claude-openrouter-override.log"
PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
HOME="${test_home}" \
STUB_LOG="${claude_openrouter_override_log}" \
OPENROUTER_API_KEY="test-or-secret" \
CLAUDE_OPENROUTER_SONNET_MODEL="anthropic/claude-sonnet-5" \
CLAUDE_OPENROUTER_GATEWAY_DISCOVERY=0 \
AI_BACKENDS_CONTAINER_MODE=no \
    "${launcher_bin}/claude-openrouter" --print "overrides"
grep -Eq '^sonnet_model=anthropic/claude-sonnet-5$' "${claude_openrouter_override_log}" \
    || fail "Claude OpenRouter Sonnet override was not honored"
grep -Eq '^gateway_discovery=unset$' "${claude_openrouter_override_log}" \
    || fail "Claude OpenRouter gateway discovery override was not honored"
grep -Eq '^subprocess_scrub=1$' "${claude_openrouter_override_log}" \
    || fail "Claude OpenRouter subprocess isolation was not retained outside a container"

invalid_discovery_output="${test_root}/invalid-discovery.log"
if PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
    HOME="${test_home}" \
    STUB_LOG="${test_root}/unused.log" \
    OPENROUTER_API_KEY="test-or-secret" \
    CLAUDE_OPENROUTER_GATEWAY_DISCOVERY=maybe \
    "${launcher_bin}/claude-openrouter" --print "invalid" >"${invalid_discovery_output}" 2>&1; then
    fail "Claude OpenRouter launcher accepted an invalid gateway discovery mode"
fi
grep -Eq 'CLAUDE_OPENROUTER_GATEWAY_DISCOVERY must be 0 or 1' \
    "${invalid_discovery_output}" \
    || fail "Invalid gateway discovery mode did not provide actionable guidance"

# Outside an existing container, retain Claude's stronger subprocess isolation.
PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
HOME="${test_home}" \
STUB_LOG="${claude_log}" \
ZAI_API_KEY="test-secret" \
AI_BACKENDS_CONTAINER_MODE=no \
    "${launcher_bin}/claude-zai" --print "host mode"
grep -Eq '^subprocess_scrub=1$' "${claude_log}" \
    || fail "Claude subprocess isolation was not retained outside a container"

invalid_scrub_output="${test_root}/invalid-scrub.log"
if PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
    HOME="${test_home}" \
    STUB_LOG="${claude_log}" \
    ZAI_API_KEY="test-secret" \
    AI_BACKENDS_CONTAINER_MODE=yes \
    CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB=maybe \
    "${launcher_bin}/claude-zai" --print "invalid" >"${invalid_scrub_output}" 2>&1; then
    fail "Claude Z.ai launcher accepted an invalid subprocess isolation mode"
fi
grep -Eq 'CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB must be auto, 0, or 1' \
    "${invalid_scrub_output}" \
    || fail "Invalid subprocess isolation mode did not provide actionable guidance"

nested_sandbox_output="${test_root}/nested-sandbox.log"
nested_sandbox_claude_log="${test_root}/nested-sandbox-claude.log"
if PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
    HOME="${test_home}" \
    STUB_LOG="${nested_sandbox_claude_log}" \
    STUB_BWRAP_FAIL=1 \
    ZAI_API_KEY="test-secret" \
    AI_BACKENDS_CONTAINER_MODE=yes \
    CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB=1 \
    "${launcher_bin}/claude-zai" --print "nested" >"${nested_sandbox_output}" 2>&1; then
    fail "Claude launcher ignored a broken explicitly requested nested sandbox"
fi
grep -Eq 'simulated namespace denial' "${nested_sandbox_output}" \
    || fail "Nested sandbox probe discarded bubblewrap diagnostics"
grep -Eq 'cannot create its nested sandbox' "${nested_sandbox_output}" \
    || fail "Nested sandbox failure did not provide actionable guidance"
[ ! -e "${nested_sandbox_claude_log}" ] \
    || fail "Claude was launched after the nested sandbox preflight failed"

missing_key_output="${test_root}/missing-key.log"
if PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
    HOME="${test_home}" \
    CODEX_HOME="${codex_home}" \
    STUB_LOG="${test_root}/unused.log" \
    env -u ZAI_API_KEY -u Z_AI_API_KEY \
    "${launcher_bin}/codex-zai" --version >"${missing_key_output}" 2>&1; then
    fail "Codex Z.ai launcher accepted a missing key"
fi
grep -Eq 'Set ZAI_API_KEY' "${missing_key_output}" \
    || fail "Missing-key failure did not provide setup guidance"

missing_openrouter_key_output="${test_root}/missing-openrouter-key.log"
if PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
    HOME="${test_home}" \
    CODEX_HOME="${codex_home}" \
    STUB_LOG="${test_root}/unused.log" \
    env -u OPENROUTER_API_KEY \
    "${launcher_bin}/claude-openrouter" --version >"${missing_openrouter_key_output}" 2>&1; then
    fail "Claude OpenRouter launcher accepted a missing key"
fi
grep -Eq 'Set OPENROUTER_API_KEY' "${missing_openrouter_key_output}" \
    || fail "Missing OpenRouter key failure did not provide setup guidance"

if grep -Eq 'exec env' "${launcher_script}"; then
    fail "Claude launcher exposes assignments to an intermediate env process argv"
fi

printf 'PASS: isolated AI backend launchers\n'
