---
title: "CI/CD Devcontainer Workflows"
id: "cicd-devcontainer-workflows"
category: "github-actions"
version: "1.0.0"
created: "2026-05-05"
updated: "2026-05-05"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".github/workflows/devcontainer-prebuild.yml"
    - path: ".github/workflows/devcontainer-test.yml"
    - path: ".devcontainer/Dockerfile"
    - path: ".devcontainer/devcontainer.json"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "github-actions"
  - "ci-cd"
  - "devcontainer"
  - "ghcr"
  - "docker"
  - "prebuild"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding the devcontainers/ci action's event-filter default and GHCR push semantics."

impact:
  performance:
    rating: "high"
    details: "Pre-built images cut new-contributor onboarding from 15 min to under 2 min"
  maintainability:
    rating: "high"
    details: "Single skill captures the silent-failure gotchas so they do not bite the next contributor"
  testability:
    rating: "low"
    details: "Verified by an explicit push and pull-back step in devcontainer-prebuild.yml"

prerequisites:
  - "Familiarity with GitHub Actions workflow YAML"
  - "Awareness of GHCR authentication and image naming"

dependencies:
  packages: []
  skills:
    - "devcontainer-cache-contract"

applies_to:
  languages:
    - "YAML"
  frameworks:
    - "GitHub Actions"
  versions: {}

aliases:
  - "Devcontainer CI"
  - "GHCR prebuild"
  - "eventFilterForPush"

related:
  - "devcontainer-cache-contract"
  - "mcp-test-loop"
  - "unity-ci-matrix"

status: "stable"
---

<!-- trigger: cicd, ci, cd, workflow, devcontainer, ghcr, docker, prebuild | Devcontainer CI/CD workflow patterns and pitfalls | Core -->

# CI/CD Devcontainer Workflows

> **One-line summary**: For same-job GHCR verification, build with `devcontainers/ci@v0.3` and `push: never`, then run `docker push` explicitly before `docker pull`; for workflows that let `devcontainers/ci` publish from its post action, set `eventFilterForPush: ""` explicitly.

## When to Use

- Creating or modifying devcontainer CI/CD workflows.
- Debugging image push failures to GHCR.
- Setting up GHCR caching for devcontainer builds.
- Investigating CI failures related to Docker image operations.

## When NOT to Use

- Unity-specific CI (build, test, deploy Unity projects). See [unity-ci-matrix](../unity/unity-ci-matrix.md).
- General Docker operations unrelated to devcontainers.
- Local devcontainer development (no CI involved).

## Critical: `devcontainers/ci@v0.3` Post-Action Push Gotcha

`devcontainers/ci@v0.3` pushes images from its post action. Normal workflow steps that follow the action run before that post action.

### The Problem

This is fragile:

```yaml
- uses: devcontainers/ci@v0.3
  with:
    imageName: ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer
    cacheFrom: ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer
    push: always

- name: Verify image
  run: docker pull "ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer:latest"
```

The `docker pull` step runs before the action-managed push. If the image is not already present in GHCR, the verification step fails with `manifest unknown`. Because `devcontainers/ci` declares `post-if: success()`, that failure also prevents the post action from publishing the image.

### The Fix

For workflows that verify GHCR in the same job, disable the action-managed push and push explicitly:

```yaml
- uses: devcontainers/ci@v0.3
  with:
    imageName: ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer
    cacheFrom: ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer
    push: never

- name: Push devcontainer image to GHCR
  run: |
    set -euo pipefail
    IMAGE="ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer:latest"
    docker image inspect "${IMAGE}" --format 'id={{.Id}} created={{.Created}} size={{.Size}}'
    docker push "${IMAGE}"
    docker manifest inspect "${IMAGE}" >/dev/null

- name: Verify image pushed to GHCR
  run: |
    set -euo pipefail
    IMAGE="ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer:latest"
    docker pull "${IMAGE}"
```

`devcontainer-prebuild.yml` must use this explicit push pattern because its pull-back step is intended to verify the registry state before the job completes.

## Critical: `devcontainers/ci@v0.3` Event Filter Gotcha

The `devcontainers/ci` action has a subtle and dangerous default that silently skips image pushes.

### The Problem

`eventFilterForPush` defaults to `"push"`, which acts as a universal gate on ALL push decisions, including `push: always`. This means:

| Trigger             | `github.event_name` | Matches default filter? | Push happens? |
| ------------------- | ------------------- | ----------------------- | ------------- |
| `push` to branch    | `push`              | Yes                     | Yes           |
| `schedule` (cron)   | `schedule`          | No                      | No            |
| `workflow_dispatch` | `workflow_dispatch` | No                      | No            |

### The Fix

Always set `eventFilterForPush: ""` to disable the event gate:

```yaml
- uses: devcontainers/ci@v0.3
  with:
    imageName: ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer
    cacheFrom: ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer
    push: always
    # Required: devcontainers/ci defaults eventFilterForPush to "push",
    # which silently skips push on schedule/workflow_dispatch triggers
    eventFilterForPush: ""
```

The DxMessaging workflows that need this:

- `.github/workflows/devcontainer-prebuild.yml` does **not** use this now because it uses `push: never` plus an explicit `docker push`.
- `.github/workflows/devcontainer-test.yml` (uses `push: filter` plus `refFilterForPush: refs/heads/master`; the empty `eventFilterForPush` makes the ref filter the single source of truth).

### Why This Is Dangerous

- The push failure is silent. No error, no warning in logs.
- The build succeeds, the image exists locally, but never reaches the registry.
- Downstream steps that `docker pull` the image fail with a misleading "not found" error.
- The bug only manifests on `schedule` / `workflow_dispatch` triggers, making it hard to catch on regular PR runs.

## Required Workflow Patterns

### GHCR Lowercase Repository Name

GHCR requires lowercase image names. Always convert:

```yaml
- name: Set lowercase repository name
  id: repo
  run: |
    set -euo pipefail
    repo_lower=$(echo "${{ github.repository }}" | tr '[:upper:]' '[:lower:]')
    echo "repository_lowercase=${repo_lower}" >> "${GITHUB_OUTPUT}"
```

Then use `${{ steps.repo.outputs.repository_lowercase }}` in every `imageName` and `cacheFrom`.

### Debug Context Step

Include a debug step early in the workflow:

```yaml
- name: Debug workflow context
  run: |
    echo "Event name: ${{ github.event_name }}"
    echo "Ref: ${{ github.ref }}"
    echo "Actor: ${{ github.actor }}"
```

The repo's `Ambiguous-Interactive/DxMessaging` slug forces a lowercase conversion every time, so the debug step also surfaces a wrong-actor or wrong-ref problem before it consumes 15 minutes of runner time.

### Diagnostic Verification

When verifying a pushed image, include diagnostics:

```yaml
- name: Verify image
  run: |
    set -euo pipefail
    IMAGE="ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer:latest"
    echo "=== Local Docker images ==="
    docker images | grep devcontainer || echo "No local images found"
    echo "=== Pulling from GHCR ==="
    if docker pull "${IMAGE}"; then
        echo "Image successfully pulled from GHCR"
    else
        echo "ERROR: Failed to pull from GHCR. Push may have failed." >&2
        exit 1
    fi
```

`devcontainer-prebuild.yml` includes an explicit push before this step. The pull-back check verifies the registry state after the known push, rather than relying on the action's post step.

## Common Pitfalls

| Pitfall                                                  | Impact                                                     | Prevention                                                          |
| -------------------------------------------------------- | ---------------------------------------------------------- | ------------------------------------------------------------------- |
| Verifying GHCR before `devcontainers/ci` post action     | Pull-back fails with `manifest unknown`; post push skipped | Use `push: never`, then explicit `docker push` before `docker pull` |
| Missing `eventFilterForPush: ""`                         | Silent push failure on schedule / dispatch                 | Set it explicitly on action-managed publishing steps                |
| Using `${{ github.repository }}` directly in image names | Case mismatch breaks GHCR                                  | Always lowercase convert via the `set lowercase` step               |
| `cacheFrom` on first build                               | Cache miss warnings (non-fatal)                            | Expected on bootstrap; no action needed                             |
| Double `devcontainers/ci` steps building same image      | Wasted CI time                                             | Use `cacheFrom` for the second step                                 |
| No debug context step                                    | Hard to diagnose trigger-related issues                    | Always add the debug step                                           |
| Referencing gitignored files in workflow `paths:`        | CI-only failure: file exists locally but not on runner     | Run the workflow path linter before merging                         |
| `permissions: packages: write` missing                   | Push fails with 403                                        | Add it at the job level for prebuild workflows                      |

## Related Skills

- [Devcontainer Cache Contract](../unity/devcontainer-cache-contract.md)
- [Unity MCP Test Loop](../unity/mcp-test-loop.md)
- [Unity CI Matrix](../unity/unity-ci-matrix.md)

## References

- `devcontainers/ci` action: https://github.com/devcontainers/ci
- GHCR docs: https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry
- Source: `.github/workflows/devcontainer-prebuild.yml`, `.github/workflows/devcontainer-test.yml`
