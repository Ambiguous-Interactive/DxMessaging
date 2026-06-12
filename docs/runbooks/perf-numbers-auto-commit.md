# Perf-Numbers Auto-Commit Runbook

This runbook explains the one-time setup that lets the Performance Numbers
workflow (`.github/workflows/perf-numbers.yml`) commit the refreshed
`docs/architecture/performance.md` dispatch-throughput table directly to the
default branch after a pull request merges. Keep execution notes local. Do not
paste secrets or screenshots of organization settings into this file.

## How the auto-commit works

On a pull request the workflow re-runs the dispatch benchmarks and posts the
numbers as a non-blocking sticky comment; it never pushes to the contributor
branch. After the pull request merges, the `push` event runs the benchmarks
again and the `commit-perf-doc` job re-renders the table (a manual
`workflow_dispatch` from the default branch does the same and is the supported
recovery path after a failed publish). If the numbers moved, the job lands the
refreshed doc + baseline in two tiers:

1. **Direct push** to the default branch with the App token (the fast path;
   requires the bypass in Step 3).
1. **Fallback auto-merge pull request** when the direct push is rejected with
   `GH006`: the same commit is pushed to a `ci/perf-auto-update-*` branch, a
   pull request is opened with the App token, and squash auto-merge is
   requested. The `pull_request` trigger path-ignores the two rendered files,
   so the fallback PR cannot re-run the benchmark; only the cheap required
   checks run. Superseded fallback PRs from older runs are closed automatically
   before a new one opens. The numbers are therefore never lost: worst case
   they wait in a visible PR for one human merge click.

The push is authenticated by a **GitHub App installation token**, not the
built-in `GITHUB_TOKEN`. The built-in token cannot push to a protected branch:
`github-actions[bot]` is a system account, so it is not selectable as a
branch-protection bypass actor and GitHub blocks the push by design. A dedicated
GitHub App that **is** allowed to bypass the protection does the push instead.

App-token pushes **do** re-trigger workflows (only the built-in `GITHUB_TOKEN`
suppresses that). Recursion is therefore broken at the trigger: the `push`
trigger has `paths-ignore: [docs/architecture/performance.md]`, so the doc-only
auto-commit cannot re-run the benchmark. There is no loop guard and no bot pull
request.

If the App credentials are absent, the `commit-perf-doc` job is skipped with a
warning (the PR comment still posts), so the workflow is never red just because
the App has not been provisioned yet. If the default branch advances while the
long benchmark run is in progress, the job warns and skips that stale artifact
instead of pushing older numbers over a newer merge; the newer push run owns the
fresh table update.

## Prerequisite: provision the auto-commit GitHub App

### Step 1 -- create the App

1. Open **Settings -> Developer settings -> GitHub Apps -> New GitHub App** (an
   organization-owned App under `Ambiguous-Interactive` is recommended so it can
   be reused; a repo-owner personal App also works).
1. Give it a name such as `dxmessaging-auto-commit`. Set any homepage URL.
1. Under **Webhook**, uncheck **Active** (this App needs no events).
1. Under **Permissions -> Repository permissions**, set **Contents** to
   **Read and write** and **Pull requests** to **Read and write** (the second
   powers the fallback pull request tier). Leave everything else at **No
   access** (**Metadata: Read-only** is added automatically).
1. Create the App, then on its page choose **Generate a private key** and
   download the `.pem` file.
1. Note the numeric **App ID** shown near the top of the App's settings page.

### Step 2 -- install the App on this repository

1. On the App's page choose **Install App** and install it on
   `Ambiguous-Interactive/DxMessaging` (only this repository is required).

### Step 3 -- let the App bypass the default-branch protection

The default branch is protected, so a direct push is rejected with `GH006:
Protected branch update failed` / `Changes must be made through a pull request`
unless the pushing actor can bypass the rule. This repo uses a **classic branch
protection rule** on `master`/`main`, so configure it there:

1. Open **Settings -> Branches**.
1. Edit the branch protection rule for `master` (and/or `main`).
1. Under **Require a pull request before merging**, enable **Allow specified
   actors to bypass required pull requests** and add the App you created (App
   actors appear here only once the org owns them and the App is installed).
1. If **Restrict who can push to matching branches** is enabled, also add the App
   to that allowed-pushers list.
1. Save the rule.

**Important caveat for classic branch protection.** The bypass above only waives
the _pull-request_ requirement. Classic branch protection has **no** way to let a
non-admin actor (including a GitHub App) bypass **required status checks** -- that
option simply does not exist for classic rules. So:

- If the rule does **not** check **Require status checks to pass before merging**,
  the App bypass is sufficient and the direct push succeeds.
- If the rule **does** require status checks, the App push is still rejected with
  `GH006` (the pushed commit has not passed the required checks). In that case you
  must either (a) **migrate this branch's protection to a repository ruleset** --
  a ruleset's bypass list waives the _entire_ ruleset, status checks included --
  and add the App to that ruleset's **Bypass list** (Settings -> Rules ->
  Rulesets -> the ruleset -> Bypass list -> Add bypass -> the App); or (b) push
  with a Personal Access Token belonging to a repository **admin**, since admins
  bypass classic protections when **Do not allow bypassing the above settings**
  is unchecked. Option (a) is recommended.

Note: this repo also has a **Copilot review for default branch** _ruleset_. If
that ruleset (not just the classic rule) is what blocks the push, add the App to
its **Bypass list** as well -- both systems are enforced independently, so the
pushing actor must satisfy every rule that targets the branch.

### Step 4 -- store the credentials as Actions secrets

Add these as repository (or organization) **Actions** secrets:

1. `AUTO_COMMIT_APP_ID` -- the numeric App ID from Step 1.
1. `AUTO_COMMIT_APP_PRIVATE_KEY` -- the full contents of the `.pem` private key,
   including the `-----BEGIN...` and `-----END...` lines.

The workflow reads both via `actions/create-github-app-token`. The same App and
secrets back every auto-commit workflow that pushes to the default branch -- both
`perf-numbers.yml` and `update-llms-txt.yml` use them, so this one provisioning
unblocks both at once.

## Fallback pull request prerequisites

The tier-2 fallback needs two repository conditions to fully self-heal:

1. **Auto-merge enabled**: **Settings -> General -> Pull Requests -> Allow
   auto-merge**. If it is off, the fallback PR still opens (numbers preserved)
   but waits for a manual merge; the job emits a warning with the PR URL.
1. **Required checks must run on doc-only PRs.** Auto-merge completes only once
   every required status check reports. The fallback PR touches only
   `docs/architecture/performance.md` and `docs/architecture/perf-baseline.csv`;
   if a required check's workflow path-filters those files out entirely, GitHub
   waits forever for an "Expected" check and the PR needs a human merge (or an
   admin can mark required checks as path-aware). The licensed Unity matrix
   (`unity-tests.yml`) handles this with a `ci-owned-docs-only` short-circuit:
   on a PR whose entire diff is those two files it SKIPS the matrix jobs, and
   skipped jobs report success to branch protection, so the fallback PR stays
   cheap and auto-mergeable. Audit the remaining required-check list against
   doc-only changes once when provisioning.

## Context: the "create and approve pull requests" toggle

A previous version of this workflow opened a bot pull request via
`peter-evans/create-pull-request` using the built-in `GITHUB_TOKEN`. That step
failed with `GitHub Actions is not permitted to create or approve pull requests`
because **Settings -> Actions -> General -> Workflow permissions -> "Allow
GitHub Actions to create and approve pull requests"** was OFF.

That toggle restricts only the built-in `GITHUB_TOKEN`. The tier-2 fallback
creates its pull request with the **GitHub App installation token**, which the
toggle does not govern, so the fallback works regardless of that setting -- the
App just needs the **Pull requests: Read and write** permission from Step 1.

## How to verify it worked

1. Merge a small pull request that meaningfully changes dispatch throughput (for
   example a change under `Runtime/Core/MessageBus/`). The merge commit touches
   non-ignored paths, so it triggers the workflow; the doc only changes when the
   measured numbers move beyond the render tolerance.
1. Watch the `push` run of the Performance Numbers workflow on the default
   branch.
1. If the numbers moved, confirm that a commit with subject
   `docs(perf): auto-update dispatch throughput numbers` lands on the default
   branch.
1. Confirm that doc-only commit does **not** start a follow-up Performance
   Numbers run (this proves the `paths-ignore` loop break works).
1. Confirm the run did **not** warn about a fallback pull request -- a
   `GH006`-triggered fallback means the App is not yet allowed to bypass the
   protection (Step 3); if the branch requires status checks, see the
   classic-branch-protection caveat in Step 3 (you likely need a ruleset bypass
   or an admin PAT). The numbers still land via the fallback PR; fixing the
   bypass just restores the zero-click fast path.
1. If a run failed before any of this landed, re-run it from the default branch
   via **workflow_dispatch** (Actions -> Performance Numbers -> Run workflow);
   the commit job treats a default-branch dispatch exactly like a merge push.

If the numbers did not move, the `commit-perf-doc` job renders, finds no diff,
and pushes nothing. That is the expected no-op outcome. If the
`AUTO_COMMIT_APP_*` secrets are not set, the job is skipped with a warning. If
another merge advances the default branch before the doc push, the job also
warns and exits successfully because its benchmark artifacts no longer describe
the branch tip.
