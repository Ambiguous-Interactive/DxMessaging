# Perf-Numbers Auto-Commit Runbook

This runbook explains the one repository-settings prerequisite that lets the
Performance Numbers workflow (`.github/workflows/perf-numbers.yml`) commit the
refreshed `docs/architecture/performance.md` dispatch-throughput table directly
to the default branch after a pull request merges. Keep execution notes local. Do
not paste secrets or screenshots of organization settings into this file.

## How the auto-commit works

On a pull request the workflow re-runs the dispatch benchmarks and posts the
numbers as a non-blocking sticky comment; it never pushes to the contributor
branch. After the pull request merges, the `push` event runs the benchmarks
again and the `commit-perf-doc` job re-renders the table. If the numbers moved,
it commits the refreshed doc **directly to the default branch** using the
built-in `GITHUB_TOKEN` (the same pattern as
`.github/workflows/update-llms-txt.yml`).

Commits pushed with `GITHUB_TOKEN` do **not** trigger new workflow runs, so the
auto-commit cannot recurse into another benchmark run. That is why this workflow
has no loop guard and no bot pull request.

## Prerequisite: allow `github-actions[bot]` to push to the default branch

The default branch is protected, so by default a direct push is rejected with
`GH006: Protected branch update failed` / `Changes must be made through a pull
request`. The bot must be allowed to bypass that rule.

### If the default branch uses a repository ruleset (recommended)

1. Open **Settings -> Rules -> Rulesets**.
1. Open the ruleset that targets `master` (and/or `main`).
1. In the **Bypass list**, select **Add bypass**, then in the actor picker choose
   **GitHub Actions** (the integration/app entry). This is the actor that
   `GITHUB_TOKEN` pushes present as, so selecting it lets the workflow's direct
   push bypass the pull-request requirement. Do **not** rely on the **Repository
   admin** role bypass for this: the `github-actions[bot]` / `GITHUB_TOKEN` actor
   is not matched by the Repository admin role, so that selection alone will still
   produce `GH006`.
1. Save the ruleset.

### If the default branch uses classic branch protection

1. Open **Settings -> Branches**.
1. Edit the branch protection rule for `master` (and/or `main`).
1. Enable **Allow specified actors to bypass required pull requests** and add
   `github-actions[bot]`.
1. Save the rule.

Note: if the ruleset or branch-protection rule also requires status checks,
confirm that bypass actors are allowed to push without those checks. Bypass
actors skip required-check enforcement on a direct push; if your configuration
does not let them skip, the push will still be rejected.

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
   example a change under `Runtime/Core/MessageBus/`). Any push to the default
   branch triggers the workflow (there is no `paths:` filter), but the doc only
   changes when the measured numbers move beyond the render tolerance.
1. Watch the `push` run of the Performance Numbers workflow on the default
   branch.
1. If the numbers moved, confirm that a commit with subject
   `docs(perf): auto-update dispatch throughput numbers` by
   `github-actions[bot]` lands on the default branch.
1. Confirm that commit does **not** start a follow-up Performance Numbers run
   (this proves `GITHUB_TOKEN` pushes do not recurse).
1. Confirm the run did **not** fail with `GH006` / protected-branch -- a failure
   there means the bypass above is missing.

If the numbers did not move, the `commit-perf-doc` job renders, finds no diff,
and pushes nothing. That is the expected no-op outcome.
