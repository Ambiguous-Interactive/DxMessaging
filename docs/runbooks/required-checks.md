# Required Status Checks Runbook

This runbook is the reference for making CI a required gate for the default
branch, so that auto-merge blocks a pull request until the chosen checks pass.
It records which checks are safe to require today, which workflows must be
changed before they can be required, how to apply the ruleset, and how to keep
the required set from silently breaking.

The brief branch-protection checklist in
[CI and GitHub Settings](../ops/ci-and-github-settings.md#branch-and-tag-protection)
points here for the detail.

## The one rule that governs everything

A required status check must report a conclusion on **every** pull request,
whatever it touches. Branch protection waits for each required check by name. If
a required check never reports on some pull request shape -- because its
workflow filtered itself out of that pull request -- auto-merge waits forever
and the pull request can never merge.

A **skipped** job still reports: GitHub posts the job as `skipped`, and branch
protection treats a skipped required check as passing. So the requirement is not
"the check must run", it is "the check must be **present** (run or skip), never
absent". A workflow that path-filters its whole trigger is absent on
non-matching pull requests and cannot be required as written.

## Current state

Auto-merge is enabled. The Unity Tests gate is **applied** -- repository ruleset
`Required CI - Unity Tests (default branch)` (active, `~DEFAULT_BRANCH`) requires
the 9 `Unity <ver> <mode>` legs plus `Resolve Unity test matrix` and
`Self-hosted runner access preflight`, with the `bot-auto-commit` App in
`bypass_actors` (mode `always`) so the perf-doc auto-commit still pushes to
`master`. The other gates path-filter themselves out, so they were remediated to
report on every PR shape (see [Remediation](#remediation-make-a-gate-always-report));
that remediation must merge and be verified on real PRs **before** its checks are
added to the ruleset (see [Augmenting the gate](#augmenting-the-gate-after-remediation-merges)).

## Safe to require today: Unity Tests

[`unity-tests.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/unity-tests.yml)
is the only correctness gate whose checks report on every pull request shape. Its
`pull_request` trigger has no `paths:` filter, so the workflow always starts. A
`matrix-config` job lists the changed files and, only when every changed file is
one of the two CI-owned perf-doc artifacts
(`docs/architecture/performance.md`, `docs/architecture/perf-baseline.csv`),
sets a `ci-owned-docs-only` output that skips the licensed matrix legs. Skipped
legs still report success, so even that fallback pull request stays mergeable.

The required check names are the expanded matrix legs, one per Unity version
(from [`.github/unity-versions.json`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/unity-versions.json))
times each test mode (`editmode`, `playmode`, `standalone`). As of 2026-06-14
the `all` set is `2021.3.45f1`, `2022.3.45f1`, `6000.3.16f1`, so the names are:

```text
Unity 2021.3.45f1 editmode
Unity 2021.3.45f1 playmode
Unity 2021.3.45f1 standalone
Unity 2022.3.45f1 editmode
Unity 2022.3.45f1 playmode
Unity 2022.3.45f1 standalone
Unity 6000.3.16f1 editmode
Unity 6000.3.16f1 playmode
Unity 6000.3.16f1 standalone
Resolve Unity test matrix
Self-hosted runner access preflight
```

`Resolve Unity test matrix` and `Self-hosted runner access preflight` always
run (or skip to success on forks), so they are safe anchors. The nine matrix
names are **data-driven** -- see [Fragile check names](#fragile-check-names).

## Must change before they can be required

Every other correctness/style gate path-filters its whole `pull_request`
trigger, so its check is absent on pull requests that touch none of its paths,
and requiring it as written would hang auto-merge. Each needs an always-report
job first (see [Remediation](#remediation-make-a-gate-always-report)).

| Workflow                | Check name today                           | Absent on                   |
| ----------------------- | ------------------------------------------ | --------------------------- |
| `csharpier-check.yml`   | Check C# formatting                        | doc-only PRs                |
| `dotnet-tests.yml`      | `test` (bare job id)                       | doc-only PRs (no job name)  |
| `json-format-check.yml` | Check JSON/.asmdef formatting              | PRs with no JSON/asmdef     |
| `markdownlint.yml`      | Lint repository Markdown                   | code-only PRs               |
| `spellcheck.yml`        | Check spelling                             | PRs with no scanned types   |
| `validate-banner.yml`   | Validate banner SVG                        | PRs off the banner paths    |
| `validate-llms-txt.yml` | Check llms.txt is up-to-date               | PRs off the llms paths      |
| `yaml-format-lint.yml`  | Prettier and yamllint                      | non-YAML PRs                |
| `actionlint.yml`        | Lint GitHub Actions workflows              | non-workflow PRs            |
| `script-tests.yml`      | Script tests (ubuntu/macos/windows-latest) | PRs off its paths           |
| `validate-docs.yml`     | Validate Documentation Build               | code-only PRs               |
| `lint-doc-links.yml`    | `lint` (bare job id)                       | code-only PRs (no job name) |

`devcontainer-test.yml` (`Build + smoke-test devcontainer image`) is the same
shape; require it only if devcontainer changes must gate merges.

## Must NOT be required

These never gate a pull request:

- Perf sticky-comment job in `perf-numbers.yml` (non-blocking by design); the
  perf commit and release legs run on push and `workflow_dispatch`, never on a
  pull request.
- Auto-fix workflows `csharpier-autofix.yml` and `prettier-autofix.yml`. Their
  visible `Format and propose changes` check is path-filtered (`**/*.cs`;
  `**/*.md` etc.), so it is absent on non-matching pull requests -- the same
  hang failure mode. The dedicated `Check C# formatting`, `Lint repository
Markdown`, and `Prettier and yamllint` gates are the correct required checks.
  (The `_fork` jobs additionally run under `pull_request_target` for Dependabot,
  which is a separate reason not to treat these as the gate.)
- `deploy-docs.yml` (deploy is push-only; its PR build duplicates
  `validate-docs.yml`), and the schedule/dispatch/release workflows, none of
  which have a `pull_request` trigger (`unity-benchmarks.yml`, `release*.yml`,
  `runner-bootstrap.yml`, `update-llms-txt.yml`, `sync-wiki.yml`,
  `markdown-link-validity.yml`, `stuck-job-watchdog.yml`, `unstick-run.yml`,
  `devcontainer-prebuild.yml`, `unity-gameci-experiment.yml`).

## Remediation: make a gate always-report

Copy the `unity-tests.yml` pattern. Keep the workflow trigger unfiltered, and
move the path decision into a job-level `if:` so the check is always present and
skips to success when it has nothing to do. A first job lists the changed files
(the way `unity-tests.yml` does, via `gh api .../files`) and sets an output that
the gate job gates on:

```yaml
on:
  pull_request:
    branches: [master, main]
  # no top-level paths: filter -- the workflow always starts

jobs:
  changes:
    runs-on: ubuntu-latest
    permissions:
      pull-requests: read # for the gh api .../files listing (as unity-tests.yml grants)
    outputs:
      relevant: ${{ steps.filter.outputs.relevant }}
    steps:
      - id: filter
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          files="$(gh api "repos/${{ github.repository }}/pulls/${{ github.event.pull_request.number }}/files" \
            --paginate --jq '.[].filename')"
          if printf '%s\n' "$files" | grep -qE '\.cs$'; then
            echo "relevant=true" >> "$GITHUB_OUTPUT"
          else
            echo "relevant=false" >> "$GITHUB_OUTPUT"
          fi
  gate:
    name: Check C# formatting
    needs: changes
    if: needs.changes.outputs.relevant == 'true'
    runs-on: ubuntu-latest
    steps:
      - run: ./check.sh
```

When `changes.relevant` is `false`, `gate` is skipped, reports success, and the
required check `Check C# formatting` is still present. The job `name:` is the
required-check string, so it must be stable and not depend on the job id.

## Applying the ruleset

Prefer a repository ruleset over classic branch protection: rulesets support a
bypass list (needed so the perf-doc auto-commit App can push to the default
branch) and are API-manageable. Applying it needs repository admin; the commands
below are a template to run with an admin token, not something CI performs.

```bash
# List existing rulesets and current required checks.
gh api repos/Ambiguous-Interactive/DxMessaging/rulesets

# Create/replace the default-branch ruleset (verify the payload against the
# current GitHub rulesets API before running; required_status_checks contexts
# are the exact check names from this runbook).
gh api repos/Ambiguous-Interactive/DxMessaging/rulesets -X POST --input - <<'JSON'
{
  "name": "default-branch",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [
    { "type": "pull_request" },
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": false,
        "required_status_checks": [
          { "context": "Unity 2021.3.45f1 editmode" },
          { "context": "Unity 2021.3.45f1 playmode" },
          { "context": "Unity 2021.3.45f1 standalone" },
          { "context": "Unity 2022.3.45f1 editmode" },
          { "context": "Unity 2022.3.45f1 playmode" },
          { "context": "Unity 2022.3.45f1 standalone" },
          { "context": "Unity 6000.3.16f1 editmode" },
          { "context": "Unity 6000.3.16f1 playmode" },
          { "context": "Unity 6000.3.16f1 standalone" }
        ]
      }
    }
  ],
  "bypass_actors": []
}
JSON
```

Add the auto-commit App to `bypass_actors` (by its integration/app id, mode
`always`) so the perf-doc auto-commit keeps reaching the default branch. Add the
remediated check names to `required_status_checks` as each workflow is fixed.

## Augmenting the gate (after remediation merges)

The 12 path-filtered gates were remediated to the always-report pattern (each gained
a `changes` job that lists the PR's files via `gh api` -- failing safe to "run" if
that call errors -- and gates the real job on it). **Do not add their contexts to the
ruleset until that remediation is merged to `master` and verified on real PRs** (open
one doc-only and one code-only PR; confirm each remediated check reports run-or-skip
on both). Adding a context before its workflow reports it on every PR shape hangs
auto-merge.

Once merged and verified, replace the ruleset (id from `gh api repos/OWNER/REPO/rulesets`)
with the full set -- the 11 Unity contexts plus these remediated ones:

```text
Lint repository Markdown
Check C# formatting
dotnet tests
Check JSON/.asmdef formatting
Check spelling
Validate banner SVG
Check llms.txt is up-to-date
Prettier and yamllint
Lint GitHub Actions workflows
Script tests (ubuntu-latest)
Script tests (macos-latest)
Script tests (windows-latest)
Validate Documentation Build
Lint docs links
```

```bash
# PUT the existing ruleset with the augmented contexts (keep target/conditions/
# bypass_actors; extend required_status_checks). Two jobs were renamed to get a
# stable context: dotnet-tests' job is now `dotnet tests`, lint-doc-links' is
# `Lint docs links`.
gh api repos/OWNER/REPO/rulesets/<id> -X PUT --input augmented-ruleset.json
```

## Fragile check names

A required check is matched by literal string, so these break silently:

- **Matrix-interpolated names.** `Unity <version> <mode>` is generated from
  `.github/unity-versions.json`. Bumping or adding a Unity version changes the
  set of check names: the new leg is not required until added to the ruleset,
  and a removed version leaves a required name that never reports and hangs
  auto-merge. Update the ruleset in the same change that edits
  `.github/unity-versions.json`. `script-tests.yml` is the same shape: its job
  `name: Script tests (${{ matrix.os }})` expands to three real contexts --
  `Script tests (ubuntu-latest)`, `Script tests (macos-latest)`,
  `Script tests (windows-latest)` -- so require all three by their expanded
  names, never the shorthand.
- **Jobs with no `name:`.** Their check-run context is the bare job id -- the
  `dotnet-tests` job reports as `test`, and `lint-doc-links`'s job as `lint`
  (verified against a live PR's check runs). Generic ids like `test`/`lint`
  collide across workflows and are easy to mis-type, so give those jobs a unique,
  stable `name:` before requiring them.
- **Renames.** Renaming a job's `name:` drops the old required check (which then
  never reports) without any error. Treat required-check names as an API.
- **`pull_request_target`.** Do not require the auto-fix workflows. Their visible
  `Format and propose changes` job is path-filtered (absent on non-matching pull
  requests), and the `_fork` jobs run under `pull_request_target` in the
  base-repo context. Require the dedicated lint gates instead.

## Maintenance

When the required set or a workflow changes, keep them in sync:

1. Adding a required workflow: give its gating job a stable `name:`, make it
   always-report (above), then add the name to the ruleset.
1. Renaming a required job's `name:`: update the ruleset name in the same change.
1. Bumping `.github/unity-versions.json`: update the `Unity <version> <mode>`
   contexts in the ruleset in the same change.
1. Drift check: compare the ruleset's `required_status_checks` contexts against
   the job names produced by the workflows. `gh api .../rulesets/<id>` lists the
   configured contexts; the workflow `name:` fields are the source of truth.

## Verification

After applying the ruleset:

1. Open a throwaway doc-only pull request and a code-only pull request.
1. Confirm every required check reports (runs or skips) on both, so neither
   hangs waiting for an absent check.
1. Confirm auto-merge completes only once all required checks are green.
1. Delete the throwaway pull requests.

## See Also

- [CI and GitHub Settings](../ops/ci-and-github-settings.md)
- [Perf-Numbers Auto-Commit](perf-numbers-auto-commit.md)
- [Unity Version Single Source of Truth](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/skills/github-actions/unity-version-single-source.md)
