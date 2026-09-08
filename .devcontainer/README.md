# DxMessaging devcontainer maintenance

The devcontainer installs the current npm `latest` dist-tag for these coding
tools:

- `@openai/codex`
- `@anthropic-ai/claude-code`
- `opencode-ai`
- `@nanocollective/nanocoder`
- `@z_ai/mcp-server`

The image build installs the tools as user-scoped npm packages under
`/usr/local`. Container creation and every subsequent start resolve the same
`latest` tags again and update only tools whose installed versions differ. The
npm global prefix must stay writable by the current user; the refresh never
invokes `sudo`.

Run the refresh manually with:

```bash
bash .devcontainer/install-agent-clis.sh
```

## Native, Z.ai, and OpenRouter backends

The ordinary `codex` and `claude` commands keep their native OpenAI and
Anthropic backends. Container creation also installs opt-in launchers through
`bash .devcontainer/ai-backends.sh install`:

| Launcher            | Harness     | Backend              | Protocol           |
| ------------------- | ----------- | -------------------- | ------------------ |
| `codex-zai`         | Codex       | Z.ai GLM Coding Plan | OpenAI Responses   |
| `claude-zai`        | Claude Code | Z.ai GLM Coding Plan | Anthropic Messages |
| `codex-openrouter`  | Codex       | OpenRouter           | OpenAI Responses   |
| `claude-openrouter` | Claude Code | OpenRouter           | Anthropic Messages |

Switching is per process. No command rewrites the native configuration, and
exiting a launcher returns to the native commands immediately.

Provide the credentials through a private environment or secret store:

```bash
export ZAI_API_KEY="..."           # Z.ai GLM Coding Plan
export OPENROUTER_API_KEY="..."    # OpenRouter
codex-zai
claude-zai
codex-openrouter
claude-openrouter
```

The devcontainer forwards both variables from the host through `remoteEnv` in
`devcontainer.json`.

### Z.ai launchers

Codex uses a `devcontainer-zai` profile layered over your user config
(`$CODEX_HOME/devcontainer-zai.config.toml` plus a model catalog JSON), Z.ai's
Responses endpoint at `https://api.z.ai/api/v1`, and the `ZAI_API_KEY`
environment variable. Claude uses Z.ai's Anthropic endpoint at
`https://api.z.ai/api/anthropic` and a separate `~/.claude-zai` state
directory, so native credentials and sessions remain isolated. Claude's
`haiku`, `sonnet`, and `opus` aliases map to the Z.ai GLM models
(`glm-5.3-flash[1m]` and `glm-5.3[1m]`).

### OpenRouter launchers

Codex uses a `devcontainer-openrouter` profile layered over your user config
(`$CODEX_HOME/devcontainer-openrouter.config.toml`), OpenRouter's Responses
endpoint at `https://openrouter.ai/api/v1`, and command-based auth that reads
`OPENROUTER_API_KEY`. Command-based auth makes Codex refresh OpenRouter's live
model catalog, so pinned slugs get correct context-window and reasoning
metadata instead of fallback defaults. The default model is
`openai/gpt-5.6-sol`; pin any full OpenRouter slug (it must include the
provider prefix, for example `anthropic/claude-sonnet-5`).

Claude uses OpenRouter's Anthropic-compatible endpoint at
`https://openrouter.ai/api` with the key in `ANTHROPIC_AUTH_TOKEN` and
`ANTHROPIC_API_KEY` explicitly blanked, per OpenRouter's documented contract.
It keeps state in a separate `~/.claude-openrouter` directory. The `fable`,
`opus`, `sonnet`, and `haiku` aliases plus the subagent model map to
OpenRouter's tracked Anthropic slugs (`~anthropic/claude-fable-latest`,
`~anthropic/claude-opus-latest`, `~anthropic/claude-sonnet-latest`,
`~anthropic/claude-haiku-latest`), and gateway model discovery is enabled so
the `/model` picker lists gateway models.

### Credential hygiene

The launchers never write a credential to a repository file or a generated
profile. Codex profiles authenticate through environment variables, and each
profile's `shell_environment_policy` excludes the model-server credential from
harness shell and MCP subprocesses. Claude launchers remove competing
Bedrock, Vertex, Foundry, gateway, and AWS selectors before setting the
gateway endpoint, and unset the raw provider key after transferring it to
`ANTHROPIC_AUTH_TOKEN`.

### Optional per-process overrides

| Variable                              | Default                                   | Applies to            |
| ------------------------------------- | ----------------------------------------- | --------------------- |
| `CODEX_ZAI_MODEL`                     | `glm-5.3`                                 | `codex-zai`           |
| `CODEX_ZAI_REASONING_EFFORT`          | `max` (`low`, `high`, `max`)              | `codex-zai`           |
| `CODEX_OPENROUTER_MODEL`              | `openai/gpt-5.6-sol`                      | `codex-openrouter`    |
| `CODEX_OPENROUTER_REASONING_EFFORT`   | `high` (`low`, `medium`, `high`, `xhigh`) | `codex-openrouter`    |
| `CLAUDE_ZAI_HAIKU_MODEL`              | `glm-5.3-flash[1m]`                       | `claude-zai`          |
| `CLAUDE_ZAI_SONNET_MODEL`             | `glm-5.3[1m]`                             | `claude-zai`          |
| `CLAUDE_ZAI_OPUS_MODEL`               | `glm-5.3[1m]`                             | `claude-zai`          |
| `CLAUDE_OPENROUTER_FABLE_MODEL`       | `~anthropic/claude-fable-latest`          | `claude-openrouter`   |
| `CLAUDE_OPENROUTER_OPUS_MODEL`        | `~anthropic/claude-opus-latest`           | `claude-openrouter`   |
| `CLAUDE_OPENROUTER_SONNET_MODEL`      | `~anthropic/claude-sonnet-latest`         | `claude-openrouter`   |
| `CLAUDE_OPENROUTER_HAIKU_MODEL`       | `~anthropic/claude-haiku-latest`          | `claude-openrouter`   |
| `CLAUDE_OPENROUTER_SUBAGENT_MODEL`    | `~anthropic/claude-opus-latest`           | `claude-openrouter`   |
| `CLAUDE_OPENROUTER_GATEWAY_DISCOVERY` | `1` (`0` or `1`)                          | `claude-openrouter`   |
| `ZAI_API_TIMEOUT_MS`                  | `300000`                                  | `claude-zai`          |
| `CLAUDE_OPENROUTER_TIMEOUT_MS`        | `300000`                                  | `claude-openrouter`   |
| `CLAUDE_ZAI_CONFIG_DIR`               | `~/.claude-zai`                           | `claude-zai`          |
| `CLAUDE_OPENROUTER_CONFIG_DIR`        | `~/.claude-openrouter`                    | `claude-openrouter`   |
| `AI_BACKENDS_CONTAINER_MODE`          | `auto` (`auto`, `yes`, `no`)              | both Claude launchers |
| `CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB` | `auto` (`auto`, `0`, `1`)                 | both Claude launchers |

Reinstall the launchers manually after moving the checkout:

```bash
bash .devcontainer/ai-backends.sh install
```

### Claude subprocess isolation

Claude's experimental Linux subprocess scrubber uses `bubblewrap` and `socat`;
both are baked into the devcontainer. Current Claude Code still asks
bubblewrap to create a user and mount namespace even with
`sandbox.enableWeakerNestedSandbox=true`, which Docker's default seccomp and
AppArmor profiles reject. In an existing devcontainer, both Claude launchers
therefore default `CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB` to `0` and rely on the
outer container as the process boundary. Controlled Bash and stdio MCP probes
verified that Claude still removes the model-server credential from those
children.

Outside a container, the launchers retain the stronger scrubber by default.
Set `CLAUDE_GATEWAY_SUBPROCESS_ENV_SCRUB=1` to request it explicitly; the
launcher runs a real bubblewrap preflight and exits with actionable guidance
before starting a model session when the runtime cannot support it. Do not add
`SYS_ADMIN`, run a privileged container, or globally weaken host AppArmor
merely to make this optional second sandbox work.

## Regression tests

`.devcontainer/test-ai-backends.sh` drives the launcher through stub `codex`
and `claude` binaries and asserts profile generation, permissions, credential
scrubbing, endpoint selection, and argument forwarding. It runs in the
devcontainer CI smoke test on both x64 and ARM64:

```bash
bash .devcontainer/test-ai-backends.sh
```
