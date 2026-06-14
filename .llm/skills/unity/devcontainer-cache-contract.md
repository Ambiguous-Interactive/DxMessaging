---
title: "Devcontainer Cache Contract"
id: "devcontainer-cache-contract"
category: "unity"
version: "1.0.0"
created: "2026-05-05"
updated: "2026-05-05"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".devcontainer/cache-contract.sh"
    - path: ".devcontainer/devcontainer.json"
    - path: ".devcontainer/post-create.sh"
    - path: ".devcontainer/post-start.sh"
    - path: ".devcontainer/validate-caching.sh"
    - path: ".devcontainer/Dockerfile"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "devcontainer"
  - "cache"
  - "volumes"
  - "ownership"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding Docker named-volume ownership semantics and the multi-source contract pattern."

impact:
  performance:
    rating: "high"
    details: "Warm caches save minutes per devcontainer rebuild and Unity run"
  maintainability:
    rating: "high"
    details: "One contract file is the single source of truth across four configuration surfaces"
  testability:
    rating: "high"
    details: "validate-caching.sh enforces the contract on every devcontainer-test.yml run"

prerequisites:
  - "Familiarity with Docker named volumes and bind mounts"
  - "Awareness of devcontainer lifecycle (postCreate, postStart)"

dependencies:
  packages: []
  skills:
    - "mcp-test-loop"

applies_to:
  languages:
    - "Bash"
    - "JSON"
  frameworks:
    - "Docker"
  versions: {}

aliases:
  - "Cache contract"
  - "Volume ownership"

related:
  - "mcp-test-loop"
  - "upm-test-harness"
  - "cicd-devcontainer-workflows"

status: "stable"
---

<!-- trigger: devcontainer, docker, cache, volume, ownership, contract | Devcontainer named-volume cache contract and ownership fix | Core -->

# Devcontainer Cache Contract

> **One-line summary**: `.devcontainer/cache-contract.sh` is the single source of truth for the five non-Unity devcontainer named-volume mounts (nuget, dotnet-tools, powershell, pip, node_modules); the same file is sourced by `post-create.sh`, `post-start.sh`, and `validate-caching.sh` so the four surfaces (Dockerfile, devcontainer.json, lifecycle scripts, validator) cannot drift.

## When to Use

- Adding a new persistent cache (npm modules, Cargo, pnpm, etc.).
- Removing or renaming a cache after deprecating a tool.
- Diagnosing "permission denied" errors writing into `~/.nuget` or `~/.dotnet/tools` after a container rebuild.
- Verifying CI sees the same mount shape as the local devcontainer.

## When NOT to Use

- Adding bind mounts (host paths). The contract covers named volumes only; bind mounts go directly in `devcontainer.json`.
- Per-container ephemeral state. `Temp/`, `Logs/`, build outputs belong inside the workspace, not in a shared volume.

## Why This Exists

Docker named volumes have a subtle ownership rule: when a volume is attached to a target directory for the first time, Docker copies the target's owner UID/GID onto the empty volume. Subsequent attaches keep that initial UID/GID regardless of the running container's user. If the first container that mounts the volume runs as root (which most build steps do), every later attach as `vscode` (uid 1000) sees an unwritable directory.

The fix has two parts:

1. The Dockerfile pre-creates each target with `vscode:vscode` ownership before any volume can attach.
1. `post-start.sh` re-runs `chown` on every container start, so an ownership drift (rare but possible after host upgrades) self-heals on the next attach.

`cache-contract.sh` is the table the devcontainer prongs read from. If a target is missing from the contract or misaligned by index, the validator fails loud rather than the developer hitting "permission denied" three minutes into a build.

Unity `Library/` is intentionally not in this devcontainer contract. The
container ships no local Unity build; local Unity verification runs on the host
editor through the MCP loop (see [Unity MCP Test Loop](./mcp-test-loop.md)), and
the host editor owns its own `Library/` cache outside any container volume.

## The Contract

Five entries, sources and targets aligned by array index:

| Index | Source (volume name)     | Target (in-container path)             | Purpose                                  |
| ----- | ------------------------ | -------------------------------------- | ---------------------------------------- |
| 0     | `dxm-nuget-cache`        | `/home/vscode/.nuget`                  | NuGet package cache for `dotnet restore` |
| 1     | `dxm-dotnet-tools`       | `/home/vscode/.dotnet/tools`           | Global dotnet tools (csharpier, etc.)    |
| 2     | `dxm-powershell-modules` | `/home/vscode/.local/share/powershell` | PowerShell module cache                  |
| 3     | `dxm-python-cache`       | `/home/vscode/.cache/pip`              | pip wheel/download cache                 |
| 4     | `dxm-node-modules`       | `${CACHE_WORKSPACE_ROOT}/node_modules` | Linux devcontainer `node_modules` tree   |

Source (verbatim) lives in `.devcontainer/cache-contract.sh`:

```bash
readonly CACHE_MOUNT_SOURCES=(
    "dxm-nuget-cache"
    "dxm-dotnet-tools"
    "dxm-powershell-modules"
    "dxm-python-cache"
    "dxm-node-modules"
)

readonly CACHE_MOUNT_TARGETS=(
    "/home/vscode/.nuget"
    "/home/vscode/.dotnet/tools"
    "/home/vscode/.local/share/powershell"
    "/home/vscode/.cache/pip"
    "${CACHE_WORKSPACE_ROOT}/node_modules"
)
```

The arrays are `readonly`. The file uses a re-source guard so the validator and lifecycle scripts can both `source` it inside the same shell without aborting under `set -e`.

## Workspace Root Derivation

The `dxm-node-modules` target is built from `${CACHE_WORKSPACE_ROOT}/node_modules`, so the script must resolve the workspace root correctly. It resolves in this precedence:

1. `WORKSPACE_FOLDER` when set. `devcontainer.json` sets it through `remoteEnv` to `${containerWorkspaceFolder}`.
1. Otherwise, the parent of the script's own directory. `cache-contract.sh` lives in `<workspaceRoot>/.devcontainer/`, so the parent of its directory is the workspace root and equals `${containerWorkspaceFolder}` by construction.

The derivation matters because `postCreateCommand` runs BEFORE `remoteEnv` is applied, so `WORKSPACE_FOLDER` is unset there. Deriving from the script location keeps the target correct in that window and survives a repo path or name change. Do NOT reintroduce a hardcoded absolute fallback: a stale literal silently diverges from the real mount target.

```bash
CACHE_WORKSPACE_ROOT="${WORKSPACE_FOLDER:-}"
if [[ -z "${CACHE_WORKSPACE_ROOT}" ]]; then
    CACHE_WORKSPACE_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]:?cache-contract.sh must be sourced by path so the workspace root can be derived}")/.." && pwd)"
fi
readonly CACHE_WORKSPACE_ROOT
```

The `${BASH_SOURCE[0]:?...}` form fails loud rather than deriving a wrong root from the caller's working directory if the file is ever sourced without a resolvable path. Every real consumer sources it by absolute path, so this never trips in practice.

`cache_contract_describe_workspace_root` emits a one-line diagnostic stating which branch resolved the root (`from WORKSPACE_FOLDER env` versus `derived from script location; WORKSPACE_FOLDER unset`). Sourcing the file stays silent; `validate-caching.sh` calls the helper explicitly so the resolution is visible in validator output.

Anti-drift invariants to keep honest when editing the contract: no hardcoded absolute workspace literal; the derived fallback (with `WORKSPACE_FOLDER` unset) resolves to the repo root; an explicit `WORKSPACE_FOLDER` wins; and the `node_modules` contract target lines up with the `${containerWorkspaceFolder}/node_modules` mount in `devcontainer.json`.

## How the Validator Works

`bash .devcontainer/validate-caching.sh` runs five blocks of checks:

1. **Contract shape**: arrays exist, are non-empty, and have equal length.
1. **Static wiring**: Dockerfile has the BuildKit `# syntax=` directive and apt cache mounts; `post-create.sh` and `post-start.sh` both `source cache-contract.sh`.
1. **devcontainer.json mounts**: every contract entry appears in `mounts`; `remoteUser` is `vscode`.
1. **Workflow configuration**: `devcontainer-test.yml` and `devcontainer-prebuild.yml` push and pull the prebuilt image.
1. **Runtime mount state** (only inside a container): each target is a real mount point owned by `vscode:vscode`, and a write probe succeeds.

Outside a container the runtime block is skipped with a single warning. Inside the devcontainer, every assertion is a hard failure. The `devcontainer-test.yml` workflow runs `validate-caching.sh` inside the built image; it is the sole automated enforcement of this contract.

## Adding a New Cache Mount

A new mount must be added in three places, in this order:

1. Append to BOTH arrays in `.devcontainer/cache-contract.sh`. Same index in both. Pick a name with the `dxm-` prefix.
1. Append the matching `source=...,target=...,type=volume` entry to `mounts` in `.devcontainer/devcontainer.json`.
1. Pre-create the target in `.devcontainer/Dockerfile` with `vscode:vscode` ownership:

   ```dockerfile
   RUN install -d -o vscode -g vscode /home/vscode/.cache/<new-tool>
   ```

After the change, run:

```bash
bash .devcontainer/validate-caching.sh
```

A pre-commit hook re-runs the validator when files under `.devcontainer/` change, so the three-place edit is enforced before the commit lands.

## Removing a Cache Mount

Remove in the inverse order: devcontainer.json first (so a fresh build does not request a mount that no longer has a target), then `cache-contract.sh`, then optionally the Dockerfile pre-create line. Do NOT remove the volume itself with `docker volume rm` until you are sure no team member's container is still running against it.

## See Also

- [Unity MCP Test Loop](./mcp-test-loop.md)
- [UPM Test Harness](./upm-test-harness.md)
- [Unity Version Single Source of Truth](../github-actions/unity-version-single-source.md)
- [CI/CD Devcontainer Workflows](../github-actions/cicd-devcontainer-workflows.md)

## References

- Docker volumes: https://docs.docker.com/storage/volumes/
- Devcontainer JSON reference: https://containers.dev/implementors/json_reference/
- Source: `.devcontainer/cache-contract.sh`
