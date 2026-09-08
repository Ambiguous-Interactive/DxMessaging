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

foreign_output="${test_root}/foreign-launcher.log"
foreign_dir="${test_root}/foreign"
foreign_codex_home="${test_root}/foreign-codex-home"
mkdir -p "${foreign_dir}" "${foreign_codex_home}"
chmod 755 "${foreign_codex_home}"
printf '#!/usr/bin/env sh\nexit 0\n' >"${foreign_dir}/decoy"
chmod 755 "${foreign_dir}/decoy"
ln -s "${foreign_dir}/decoy" "${foreign_dir}/claude-zai"
for profile in devcontainer-zai-models.json devcontainer-zai.config.toml devcontainer-openrouter.config.toml; do
    printf 'preserve-%s\n' "${profile}" >"${foreign_codex_home}/${profile}"
done
if AI_BACKENDS_BIN_DIR="${foreign_dir}" \
    HOME="${test_home}" \
    CODEX_HOME="${foreign_codex_home}" \
    bash "${launcher_script}" install >"${foreign_output}" 2>&1; then
    fail "install replaced a launcher symlink pointing at an unrelated program"
fi
grep -Eq 'Refusing to replace launcher symlink pointing elsewhere' "${foreign_output}" \
    || fail "Foreign-symlink refusal did not provide actionable guidance"
[ "$(readlink "${foreign_dir}/claude-zai")" = "${foreign_dir}/decoy" ] \
    || fail "Foreign-symlink refusal modified the existing symlink"
for profile in devcontainer-zai-models.json devcontainer-zai.config.toml devcontainer-openrouter.config.toml; do
    [ "$(cat "${foreign_codex_home}/${profile}")" = "preserve-${profile}" ] \
        || fail "Foreign-symlink refusal modified ${profile}"
done
[ "$(stat -c '%a' "${foreign_codex_home}")" = "755" ] \
    || fail "Foreign-symlink refusal modified CODEX_HOME permissions"

dangling_output="${test_root}/dangling-launcher.log"
dangling_dir="${test_root}/dangling"
dangling_codex_home="${test_root}/dangling-codex-home"
mkdir -p "${dangling_dir}" "${dangling_codex_home}"
ln -s "${dangling_dir}/missing-decoy" "${dangling_dir}/claude-zai"
for profile in devcontainer-zai-models.json devcontainer-zai.config.toml devcontainer-openrouter.config.toml; do
    printf 'preserve-%s\n' "${profile}" >"${dangling_codex_home}/${profile}"
done
if AI_BACKENDS_BIN_DIR="${dangling_dir}" \
    HOME="${test_home}" \
    CODEX_HOME="${dangling_codex_home}" \
    bash "${launcher_script}" install >"${dangling_output}" 2>&1; then
    fail "install replaced a dangling launcher symlink"
fi
grep -Eq 'Refusing to replace dangling launcher symlink' "${dangling_output}" \
    || fail "Dangling-symlink refusal did not provide actionable guidance"
[ "$(readlink "${dangling_dir}/claude-zai")" = "${dangling_dir}/missing-decoy" ] \
    || fail "Dangling-symlink refusal modified the existing symlink"
for profile in devcontainer-zai-models.json devcontainer-zai.config.toml devcontainer-openrouter.config.toml; do
    [ "$(cat "${dangling_codex_home}/${profile}")" = "preserve-${profile}" ] \
        || fail "Dangling-symlink refusal modified ${profile}"
done

plain_output="${test_root}/plain-launcher.log"
plain_dir="${test_root}/plain"
mkdir -p "${plain_dir}"
printf '#!/usr/bin/env sh\nexit 0\n' >"${plain_dir}/codex-zai"
chmod 755 "${plain_dir}/codex-zai"
if AI_BACKENDS_BIN_DIR="${plain_dir}" \
    HOME="${test_home}" \
    bash "${launcher_script}" install >"${plain_output}" 2>&1; then
    fail "install replaced a non-symlink launcher file"
fi
grep -Eq 'Refusing to replace non-symlink launcher' "${plain_output}" \
    || fail "Non-symlink refusal did not provide actionable guidance"
if [ -f "${plain_dir}/codex-zai" ] && [ ! -L "${plain_dir}/codex-zai" ]; then
    : # The refused launcher file must survive untouched.
else
    fail "Non-symlink refusal modified the existing launcher file"
fi

profile_collision_output="${test_root}/profile-collision.log"
profile_collision_dir="${test_root}/profile-collision-launchers"
profile_collision_codex_home="${test_root}/profile-collision-codex-home"
mkdir -p "${profile_collision_dir}" \
    "${profile_collision_codex_home}/devcontainer-openrouter.config.toml"
printf 'preserve-models\n' >"${profile_collision_codex_home}/devcontainer-zai-models.json"
printf 'preserve-zai\n' >"${profile_collision_codex_home}/devcontainer-zai.config.toml"
if AI_BACKENDS_BIN_DIR="${profile_collision_dir}" \
    HOME="${test_home}" \
    CODEX_HOME="${profile_collision_codex_home}" \
    bash "${launcher_script}" install >"${profile_collision_output}" 2>&1; then
    fail "install accepted a directory at a Codex profile path"
fi
grep -Eq 'Refusing unsupported Codex profile path' "${profile_collision_output}" \
    || fail "Profile-path refusal did not provide actionable guidance"
[ "$(cat "${profile_collision_codex_home}/devcontainer-zai-models.json")" = "preserve-models" ] \
    || fail "Profile-path refusal modified the Z.ai model catalog"
[ "$(cat "${profile_collision_codex_home}/devcontainer-zai.config.toml")" = "preserve-zai" ] \
    || fail "Profile-path refusal modified the Z.ai profile"
for launcher in codex-zai claude-zai codex-openrouter claude-openrouter; do
    [ ! -e "${profile_collision_dir}/${launcher}" ] \
        || fail "Profile-path refusal created ${launcher}"
done

write_collision_output="${test_root}/write-collision.log"
write_collision_dir="${test_root}/write-collision-launchers"
write_collision_codex_home="${test_root}/write-collision-codex-home"
write_collision_bin="${test_root}/write-collision-bin"
mkdir -p "${write_collision_dir}" "${write_collision_codex_home}" "${write_collision_bin}"
chmod 755 "${write_collision_codex_home}"
for profile in devcontainer-zai-models.json devcontainer-zai.config.toml devcontainer-openrouter.config.toml; do
    printf 'preserve-%s\n' "${profile}" >"${write_collision_codex_home}/${profile}"
done
cat >"${write_collision_bin}/ln" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
"${REAL_LN:?}" "$@"
if [ ! -e "${INJECT_LAUNCHER_DIR:?}/claude-zai" ]; then
    printf '#!/usr/bin/env sh\nexit 0\n' >"${INJECT_LAUNCHER_DIR}/claude-zai"
    chmod 755 "${INJECT_LAUNCHER_DIR}/claude-zai"
fi
STUB
chmod 755 "${write_collision_bin}/ln"
if PATH="${write_collision_bin}:/usr/bin:/bin" \
    REAL_LN="$(command -v ln)" \
    INJECT_LAUNCHER_DIR="${write_collision_dir}" \
    AI_BACKENDS_BIN_DIR="${write_collision_dir}" \
    HOME="${test_home}" \
    CODEX_HOME="${write_collision_codex_home}" \
    bash "${launcher_script}" install >"${write_collision_output}" 2>&1; then
    fail "install accepted a launcher path created after preflight"
fi
grep -Eq 'Launcher installation was rolled back' "${write_collision_output}" \
    || fail "Write-time launcher collision did not report rollback"
if [ ! -f "${write_collision_dir}/claude-zai" ] \
    || [ -L "${write_collision_dir}/claude-zai" ]; then
    fail "Write-time collision modified the injected launcher"
fi
for launcher in codex-zai codex-openrouter claude-openrouter; do
    [ ! -e "${write_collision_dir}/${launcher}" ] \
        || fail "Write-time collision left ${launcher} partially installed"
done
for profile in devcontainer-zai-models.json devcontainer-zai.config.toml devcontainer-openrouter.config.toml; do
    [ "$(cat "${write_collision_codex_home}/${profile}")" = "preserve-${profile}" ] \
        || fail "Write-time collision modified ${profile}"
done
[ "$(stat -c '%a' "${write_collision_codex_home}")" = "755" ] \
    || fail "Write-time collision modified CODEX_HOME permissions"

occupied_bin="${test_root}/occupied-launcher-bin"
absent_codex_home="${test_root}/must-remain-absent-codex-home"
printf 'preserve-occupied-bin\n' >"${occupied_bin}"
if AI_BACKENDS_BIN_DIR="${occupied_bin}" \
    HOME="${test_home}" \
    CODEX_HOME="${absent_codex_home}" \
    bash "${launcher_script}" install >"${test_root}/occupied-bin.log" 2>&1; then
    fail "install accepted a file as its launcher directory"
fi
grep -Eq 'Refusing unsupported launcher directory' "${test_root}/occupied-bin.log" \
    || fail "Occupied launcher directory refusal did not provide actionable guidance"
[ "$(cat "${occupied_bin}")" = "preserve-occupied-bin" ] \
    || fail "Occupied launcher directory refusal modified the file"
[ ! -e "${absent_codex_home}" ] \
    || fail "Occupied launcher directory refusal created CODEX_HOME"

mktemp_failure_bin="${test_root}/mktemp-failure-bin"
mktemp_failure_launcher_parent="${test_root}/mktemp-failure-launcher-parent"
mktemp_failure_codex_parent="${test_root}/mktemp-failure-codex-parent"
mktemp_failure_launchers="${mktemp_failure_launcher_parent}/deep/bin"
mktemp_failure_codex_home="${mktemp_failure_codex_parent}/deep/codex"
mkdir -p "${mktemp_failure_bin}"
cat >"${mktemp_failure_bin}/mktemp" <<'STUB'
#!/usr/bin/env bash
exit 73
STUB
chmod 755 "${mktemp_failure_bin}/mktemp"
if PATH="${mktemp_failure_bin}:/usr/bin:/bin" \
    AI_BACKENDS_BIN_DIR="${mktemp_failure_launchers}" \
    HOME="${test_home}" \
    CODEX_HOME="${mktemp_failure_codex_home}" \
    bash "${launcher_script}" install >"${test_root}/mktemp-failure.log" 2>&1; then
    fail "install accepted a failed staging-directory creation"
fi
grep -Eq 'Unable to create the Codex profile staging directory' "${test_root}/mktemp-failure.log" \
    || fail "Staging-directory failure did not provide actionable guidance"
[ ! -e "${mktemp_failure_launchers}" ] \
    || fail "Staging-directory failure left the launcher directory behind"
[ ! -e "${mktemp_failure_codex_home}" ] \
    || fail "Staging-directory failure left CODEX_HOME behind"
[ ! -e "${mktemp_failure_launcher_parent}" ] \
    || fail "Staging-directory failure left a launcher parent behind"
[ ! -e "${mktemp_failure_codex_parent}" ] \
    || fail "Staging-directory failure left a CODEX_HOME parent behind"

chmod_failure_bin="${test_root}/chmod-failure-bin"
chmod_failure_launchers="${test_root}/chmod-failure-launchers"
chmod_failure_codex_home="${test_root}/chmod-failure-codex-home"
mkdir -p "${chmod_failure_bin}" "${chmod_failure_codex_home}"
chmod 755 "${chmod_failure_codex_home}"
for profile in devcontainer-zai-models.json devcontainer-zai.config.toml devcontainer-openrouter.config.toml; do
    printf 'preserve-%s\n' "${profile}" >"${chmod_failure_codex_home}/${profile}"
done
cat >"${chmod_failure_bin}/chmod" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
last_argument="${!#}"
if [ "${last_argument}" = "${FAIL_CHMOD_PATH:?}" ]; then
    exit 74
fi
exec "${REAL_CHMOD:?}" "$@"
STUB
chmod 755 "${chmod_failure_bin}/chmod"
if PATH="${chmod_failure_bin}:/usr/bin:/bin" \
    REAL_CHMOD="$(command -v chmod)" \
    FAIL_CHMOD_PATH="${chmod_failure_codex_home}" \
    AI_BACKENDS_BIN_DIR="${chmod_failure_launchers}" \
    HOME="${test_home}" \
    CODEX_HOME="${chmod_failure_codex_home}" \
    bash "${launcher_script}" install >"${test_root}/chmod-failure.log" 2>&1; then
    fail "install accepted a failed final CODEX_HOME chmod"
fi
grep -Eq 'Final profile installation failed and all changes were rolled back' \
    "${test_root}/chmod-failure.log" \
    || fail "Final chmod failure did not report rollback"
[ ! -e "${chmod_failure_launchers}" ] \
    || fail "Final chmod failure left the launcher directory behind"
for profile in devcontainer-zai-models.json devcontainer-zai.config.toml devcontainer-openrouter.config.toml; do
    [ "$(cat "${chmod_failure_codex_home}/${profile}")" = "preserve-${profile}" ] \
        || fail "Final chmod failure modified ${profile}"
done
[ "$(stat -c '%a' "${chmod_failure_codex_home}")" = "755" ] \
    || fail "Final chmod failure modified CODEX_HOME permissions"
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

# A user-supplied "--" must reach claude instead of becoming environment state,
# while the launcher's own appended NAME=VALUE pairs still take effect.
dash_dash_log="${test_root}/claude-dash-dash.log"
PATH="${test_bin}:${launcher_bin}:/usr/bin:/bin" \
HOME="${test_home}" \
STUB_LOG="${dash_dash_log}" \
ZAI_API_KEY="test-secret" \
AI_BACKENDS_CONTAINER_MODE=yes \
    "${launcher_bin}/claude-zai" -- --print "dash dash"
tr '\n' ' ' <"${dash_dash_log}" |
    grep -Fq 'arg=--settings arg={"sandbox":{"enabled":false,"enableWeakerNestedSandbox":true}} arg=-- arg=--print arg=dash dash' \
    || fail "A user-supplied '--' was not forwarded verbatim to claude"
grep -Eq '^haiku_model=glm-5.3-flash\[1m\]$' "${dash_dash_log}" \
    || fail "Launcher NAME=VALUE pairs were lost when the user supplied '--'"

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
