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
again and the `commit-perf-doc` job re-renders the table. If the numbers moved,
it attempts to commit the refreshed doc **directly to the default branch**.

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
   **Read and write**. Leave everything else at **No access** (**Metadata:
   Read-only** is added automatically).
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

## Context: the "create and approve pull requests" toggle

A previous version of this workflow opened a bot pull request via
`peter-evans/create-pull-request`. That step failed with `GitHub Actions is not
permitted to create or approve pull requests` because **Settings -> Actions ->
General -> Workflow permissions -> "Allow GitHub Actions to create and approve
pull requests"** was OFF.

With the direct-push approach that toggle is **irrelevant** -- the workflow no
longer creates pull requests. It is documented here only so the original failure
mode is understood; you do **not** need to enable it for this approach.

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
1. Confirm the run did **not** fail with `GH006` / protected-branch -- a failure
   there means the App is not yet allowed to bypass the protection (Step 3); if
   the branch requires status checks, see the classic-branch-protection caveat in
   Step 3 (you likely need a ruleset bypass or an admin PAT).

If the numbers did not move, the `commit-perf-doc` job renders, finds no diff,
and pushes nothing. That is the expected no-op outcome. If the
`AUTO_COMMIT_APP_*` secrets are not set, the job is skipped with a warning. If
another merge advances the default branch before the doc push, the job also
warns and exits successfully because its benchmark artifacts no longer describe
the branch tip.
