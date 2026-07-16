---
title: CI and GitHub Settings
description: Runner, environment, secret, and branch protection setup for trusted releases
---

# CI and GitHub Settings

This repository splits trust domains:

- Licensed Unity jobs run only on Ambiguous self-hosted Windows runners.
- npm publishing runs on GitHub-hosted Ubuntu with OIDC Trusted Publishing.

## Self-Hosted Unity Runners

Licensed Unity jobs target self-hosted Windows runners by labels only. No
custom runner group is required; runners may live in the organization's
default runner group.

- Labels (all required on each Unity runner):
  - `self-hosted`
  - `Windows`
  - `RAM-64GB`
- Speed marker applied only to `ELI-MACHINE`:
  - `fast`

The Unity serial allows two concurrent activations and has no server-side
reclaim. The organization lock admits at most two distinct runners, accounts
for cooldowns and quarantines, and blocks all new admission during an account
incident. GitHub native `concurrency` is repository-scoped and serializes whole
jobs, so it is not the organization-level lock. Every
Unity-credential-using job validates Unity license secret shape, then acquires
the central lock immediately before the licensed Unity section:

```yaml
- name: Validate Unity license secrets
  uses: ./.github/actions/validate-unity-license

- name: Acquire organization Unity lock
  uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@a8d43dd87a938f1b3417fd8a9310354bf38e2fd1 # v1.8.2
  with:
    lock-name: wallstop-organization-builds
    runner-id: ${{ runner.name }}
  env:
    BUILD_LOCK_APP_ID: ${{ secrets.BUILD_LOCK_APP_ID }}
    BUILD_LOCK_APP_PRIVATE_KEY: ${{ secrets.BUILD_LOCK_APP_PRIVATE_KEY }}
```

The matching release step uses the same immutable lock commit with `if:
always()`. This lets checkout, cache, Node setup, and assembly discovery split
across eligible runners while the organization lock enforces the two-seat
account boundary.

Before these workflows can run, enable private action access for this
repository if the lock repository is private and expose the organization Unity
and writer App secrets to this repository.

Do not declare native `concurrency.group: wallstop-organization-builds`;
that name is reserved for the central lock action input, not GitHub's
repository-scoped concurrency feature. IL2CPP is the `standalone` entry in
the `unity-tests` `test-mode` matrix. The direct Windows runner
(`scripts/unity/run-ci-tests.ps1`) maps that mode to `StandaloneWindows64`
and configures IL2CPP in the generated project, not a separate job.

Unity editor provisioning must be scoped before the license lock. Every
`ensure-editor.ps1 -CiManagedOnly` workflow step passes an explicit
`-ProvisioningProfile`: `EditorOnly` for editmode, playmode, benchmarks, and
release Unity checks; `StandaloneWindowsIl2Cpp` for standalone; and `Android`
only for jobs that actually need Android SDK/NDK tooling. The direct runner's
fallback uses the same mapping if `UNITY_EDITOR_PATH` is absent.

Per-runner Unity-cache safety is provided by each runner agent's exclusive
workspace - a single self-hosted agent only ever runs one job at a time, so
generated `.artifacts/unity/projects/<version>-<mode>/Library` directories
cannot collide.

Runner routing is uniform across all Unity-credential-using jobs:

```yaml
runs-on: [self-hosted, Windows, RAM-64GB]
```

Both ELI-MACHINE and DAD-MACHINE are eligible to pick up any Unity job;
the `fast` label remains on ELI-MACHINE only for future opt-in hotfix
dispatch but no job requests it today.

Lightweight matrix configuration jobs run on `ubuntu-latest` and remain
parallelizable.

### Organization Lock Interaction

The Unity workflows do not use workflow-level cancellation. Cancelling a
run while it is in the licensed section can leave Unity activation or
asset import state half-finished. The central lock release step still
runs with `if: always()` for normal failures, and the
`ambiguous-organization-build-lock` reaper clears stale holders when a
run is no longer active or its lease expires.

## Stuck-Job Recovery

A known GitHub Actions dispatcher bug ([Community Discussion #186811](https://github.com/orgs/community/discussions/186811))
causes self-hosted runners to report Online/Idle while `runner_id` stays
at 0 for 7+ minutes, leaving a queued job indefinitely stuck even when an
idle runner's labels are a superset of the job's requested labels.

Two workflows together provide recovery: an auto-watchdog that audits
the queue on a cron schedule, and a manual one-click recovery workflow
for a single run id.

### Auto-watchdog: `.github/workflows/stuck-job-watchdog.yml`

Runs every 5 minutes on `ubuntu-latest`, lists queued workflow runs
older than 5 minutes (`MIN_QUEUE_AGE_SECONDS=300`), fetches the org
runner inventory (falling back to repo runners on 403), and identifies
the subset that are genuinely dispatcher-stuck. A run is considered
stuck only when ALL of the following hold: the run is `status: queued`,
no job in the run is `in_progress` (a run with an in-progress job is by
definition holding/using a runner, not dispatcher-stuck), at least one
job is queued, at least one idle runner's labels satisfy a queued job's
label requirements, the run's workflow file is not in the exclusion
list, and the run is not the watchdog's own run.

Worst-case time-to-recover under the tightened thresholds is roughly
10 minutes (5-min cron interval + 300s queue-age threshold + cancel /
redispatch latency).

For each genuinely-stuck run the watchdog `gh run cancel`s the run
(the documented recovery for a queued-only run; `gh run rerun --failed`
cannot rerun a run that never reached `failed` status - see cli/cli
issue #9221). For runs triggered by `push`, `schedule`, or
`workflow_dispatch` on a workflow that declares `workflow_dispatch:`
the watchdog then re-dispatches the workflow on the same `ref` via the
REST API. For `pull_request`-triggered runs, the watchdog only cancels
and writes a clear `GITHUB_STEP_SUMMARY` instruction asking the
operator to click "Re-run all jobs" in the GitHub UI - there is no
safe API path to re-trigger a `pull_request` run without pushing a
commit, so the watchdog does not attempt it.

`release.yml` is excluded by default (`EXCLUDED_WORKFLOW_FILES=
("release.yml")`) so a spurious cancel cannot double-publish or break
attestation. Additional exclusions may be set via the
`WATCHDOG_EXCLUDED_WORKFLOWS` repository variable (whitespace-separated
list of workflow filenames).

Cancel attempts are capped at 2 per run-id per 24 hours via a small
state file on the `watchdog-state` orphan branch.

GitHub `schedule:` cron triggers only fire from the repository default
branch. Until `stuck-job-watchdog.yml` is on `master`, the cron is
INACTIVE and only manual `workflow_dispatch` from the Actions tab
works. After merge to `master` the cron resumes automatically and runs
every 5 minutes.

### Manual one-click recovery: `.github/workflows/unstick-run.yml`

`workflow_dispatch`-only workflow that targets a single explicit run id
rather than auto-scanning. Use this when:

1. The watchdog is not yet on the default branch (see the cron caveat
   above) and a job is stuck right now.
1. You want immediate recovery and do not want to wait for the next
   cron tick or the queue-age threshold.

Inputs:

- `run_id` (required, string of digits): the GitHub Actions run id to
  recover. Find it in the URL of the stuck run.
- `force_redispatch` (optional, boolean, default `false`): when true,
  attempt REST `actions/workflows/{id}/dispatches` after cancel. Only
  valid for `push` / `schedule` / `workflow_dispatch` events on a
  branch where the workflow file declares `workflow_dispatch:`.
- `bypass_exclusion` (optional, boolean, default `false`): operate on a
  run whose workflow file is in the exclusion list (e.g. `release.yml`).
  Use deliberately.

Behavior mirrors the watchdog's per-run logic: validates the run id is
a positive integer, confirms the run exists and is `queued` and older
than `MIN_AGE_SECONDS=30` (guards against accidental cancellation of
fresh runs), honors the same exclusion list unless `bypass_exclusion`
is true, then `gh run cancel`s and optionally REST-redispatches. It
does NOT touch the watchdog state branch and does NOT count against the
watchdog's per-run cancel cap.

To invoke: repo Actions tab -> "Unstick Run" workflow -> "Run workflow"
dropdown -> select branch -> enter the run id.

## Unity Workflows

Active Unity workflows:

- `.github/workflows/unity-tests.yml`
- `.github/workflows/unity-benchmarks.yml`
- `.github/workflows/release.yml` (`unity-checks` job)

Unity test matrix:

- `2021.3.45f1`
- `2022.3.45f1`
- `6000.3.16f1`
- `editmode`
- `playmode`
- `standalone` (native `StandaloneWindows64` IL2CPP player via
  `scripts/unity/run-ci-tests.ps1`, runtime-only assemblies)

Release checks default to `2022.3.45f1`. Benchmarks run on schedule
or manual dispatch only.

## Licensed Job Guardrails

Licensed Unity jobs admit same-repository pull requests, protected branch
pushes, and controlled dispatches. They reject fork pull requests and pushes to
unprotected branches. This gives trusted PRs Unity validation without exposing
organization secrets to fork code.

The workflows must not use `pull_request_target` to check out untrusted fork
code.

## Required Unity Secrets

Set secret names without documenting values:

- `UNITY_SERIAL`
- `UNITY_EMAIL`
- `UNITY_PASSWORD`
- `BUILD_LOCK_APP_ID`
- `BUILD_LOCK_APP_PRIVATE_KEY`

CI activates Unity with a classic serial: `UNITY_SERIAL` plus the account
`UNITY_EMAIL` and `UNITY_PASSWORD` are the single CI activation path. A serial
has no server-side reclaim and only a small activation-seat pool, so the license
is returned on every exit path (defensive return-at-start, a PowerShell
`try`/`finally` return, an `if: always()` workflow return step, and the next
run's return-at-start on the persistent runner). The floating licensing server is
RETIRED: `UNITY_LICENSING_SERVER` is removed from all workflows and the
`validate-unity-license` action fails the run if it is still set. The classic
serial is the single CI activation path; there is no local license. LOCAL Unity
verification runs on the host editor through the MCP loop
(`unity-mcp-remote`), and that editor supplies its own license -- no `.ulf`, no
`UNITY_LICENSE` / `UNITY_LICENSE_B64`, and no local serial are needed. Never echo
or log the serial or password; license logs go to `RUNNER_TEMP`, never to
uploaded artifacts.

The return adapter reports exact cleanup evidence to the organization lock.
Exit zero or `Serial number unavailable` alone is not cleanup proof. Exact
numeric `20111` activation evidence reports the account-blocked reason
`unity-account-limit-20111`; `20113`, `400006`, termination, timeout, and
missing positive return evidence remain runner-local uncertainty. Diagnostics
contain only stable reason codes and SHA-256 evidence digests, never log text or
credential values.

Do not record secret existence, rotation status, the serial, or account
credential state in tracked files or the local ignored runbook. Keep that
security status in GitHub organization settings or the approved organization
password manager.

## Organization Secrets and GitHub Environments

Store the five Unity/build-lock secrets as organization secrets available to
the intended organization-owned repositories. Licensed jobs do not declare a
`unity-license` environment, so eligible PR checks start without a deployment
approval. Keep Unity and App credentials scoped to the exact validation,
licensed work, cleanup, acquire, and release steps.

Other deployment workflows may still use purpose-specific environments such as
`github-pages`. Configure reviewers, wait timers, and deployment branch rules
only for those deployment environments; they must not gate Unity PR validation.

## Branch and Tag Protection

Protect the default branch and release tags:

1. Require pull requests for `master` and `main` if both are active.
1. Require status checks. A required check must report on every pull request
   shape, or auto-merge hangs waiting for an absent check. Use only checks that
   the [Required Status Checks runbook](../runbooks/required-checks.md) lists as
   applied or remediated, and verify new required checks on real PRs before
   adding them to the ruleset.
1. Require signed tags or limit tag creation to release maintainers if the
   organization supports it.
1. Protect `v*` tags from deletion or force updates.
1. Confirm release maintainers can create `vX.Y.Z` tags through the intended
   process.

## Cache Contract

Unity Library caches must include:

- runner OS and architecture
- Unity version
- Unity test mode
- package/test input hashes
- `scripts/unity/run-ci-tests.ps1`

Do not add broad `restore-keys` for Unity Library caches.

## Verification

Run:

```bash
npm test
```

Also confirm in review that no workflow reintroduces the reserved
native `concurrency.group: wallstop-organization-builds` use
(workflow-level or job-level, in multi-line mapping, inline mapping, or
scalar-shorthand form).

Workflow-shape contract checklist:

1. Confirm each of the Unity-credential-using jobs (`unity-tests`,
   `benchmarks`, `unity-checks`) acquires
   `wallstop-organization-builds` through
   `Ambiguous-Interactive/ambiguous-organization-build-lock` before
   `scripts/unity/run-ci-tests.ps1`.
1. Confirm each of those jobs runs `./.github/actions/validate-unity-license`
   before the direct Unity runner.
1. Confirm each `ensure-editor.ps1 -CiManagedOnly` provisioning step passes an
   explicit `-ProvisioningProfile` matching the Unity test mode.
1. Confirm each of those jobs releases the organization lock after the direct
   Unity runner with `if: always()`.
1. Confirm each of those jobs declares the uniform static label set
   `runs-on: [self-hosted, Windows, RAM-64GB]` so either Windows machine
   can pick up any Unity job.
1. Confirm `wallstop-organization-builds` appears only as a central lock
   action input, not as a native GitHub `concurrency.group`.
1. Confirm `.github/workflows/stuck-job-watchdog.yml` exists and is
   enabled (queue auto-recovery for the GitHub Actions dispatcher bug).
   Once merged to the default branch, the 5-minute cron fires
   automatically.
1. Confirm `.github/workflows/unstick-run.yml` exists for manual
   one-click recovery of a single run id (operator dispatches it from
   the Actions tab with the stuck run's id). The workflow is
   `workflow_dispatch`-only -- there is no cron, push, or PR trigger.
   (See "Stuck-Job Recovery" subsection above for the operator runbook.)

Trigger safe workflows after transfer:

1. `workflow_dispatch` for Unity Tests with one Unity version and one mode.
1. `workflow_dispatch` for Unity IL2CPP.
1. `workflow_dispatch` for Unity Benchmarks if runner capacity allows it.
1. A same-repository pull request to confirm licensed checks land on a
   Windows runner and serialize correctly (only one matrix entry running
   at a time, no eviction messages).
1. A fork pull request dry run to confirm licensed checks skip.
