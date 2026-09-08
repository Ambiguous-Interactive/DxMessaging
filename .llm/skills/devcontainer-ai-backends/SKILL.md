---
name: devcontainer-ai-backends
description: "The four isolated backend launchers in .devcontainer/ai-backends.sh: codex-zai, claude-zai, codex-openrouter, and claude-openrouter. Use when adding or editing a gateway launcher, its Codex profile, its Claude config-dir, or test-ai-backends.sh, wiring ZAI_API_KEY or OPENROUTER_API_KEY into devcontainer.json, debugging an isolated backend endpoint or model mapping, or deciding where a Z.ai or OpenRouter harness integration belongs."
metadata:
  category: "devcontainer"
  tags: "bash, devcontainer, codex, claude-code, zai, openrouter, launchers, credentials"
---

# Devcontainer AI Backends

## When to use

- Adding or editing `.devcontainer/ai-backends.sh`, `.devcontainer/test-ai-backends.sh`,
  or one of the four launcher integrations.
- Wiring a model-server credential through `.devcontainer/devcontainer.json`.
- A launcher reports a missing key, a wrong endpoint, or leaked credentials.

## Rules

- Native `codex` and `claude` commands stay untouched. A launcher is a symlink
  next to the harness binary, resolved by the invoked name, never a rewrite of
  the harness's own config.
- Credentials come from the environment (`ZAI_API_KEY` with the `Z_AI_API_KEY`
  alias, `OPENROUTER_API_KEY`). Never persist a key to a repository file, a
  generated profile, or launcher argv.
- Codex integrations live in `$CODEX_HOME/<profile>.config.toml` layered with
  `codex --profile`. Z.ai keeps its static model catalog JSON and
  `env_key` auth; OpenRouter uses command-based auth so Codex refreshes its
  live model catalog, and `wire_api` stays `"responses"` for both.
- Claude integrations isolate state in `~/.claude-zai` and
  `~/.claude-openrouter` through `CLAUDE_CONFIG_DIR`, send the key through
  `ANTHROPIC_AUTH_TOKEN`, unset every competing Bedrock/Vertex/Foundry/
  gateway/AWS selector, and set `CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST=1`.
  Z.ai unsets `ANTHROPIC_API_KEY`; OpenRouter must leave it explicitly blank
  per OpenRouter's documented contract.
- Each Codex profile excludes its credential from tool subprocesses through
  `shell_environment_policy`. Codex provider auth commands read the process
  environment directly and are unaffected by that policy; verified against
  Codex 0.153.4 with a local capture server.
- Both Claude launchers share `resolve_claude_sandbox` and
  `launch_claude_gateway`. Inside a devcontainer they disable the nested
  sandbox (Docker seccomp rejects the bubblewrap namespaces) and rely on the
  outer container boundary. Outside, they run a real bubblewrap preflight
  before launching when the scrubber stays on.
- Behavior changes land with a failing test first in
  `.devcontainer/test-ai-backends.sh` (stub `codex`/`claude` binaries, temp
  `HOME`/`CODEX_HOME`, no network). The suite runs in the devcontainer CI
  smoke test on x64 and ARM64.

## Detail

- Launcher contract, endpoints, defaults, and the full override table:
  [.devcontainer/README.md](../../../.devcontainer/README.md)
