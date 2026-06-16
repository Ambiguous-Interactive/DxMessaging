# Required Status Checks Runbook

This runbook is the reference for making CI a required gate for the default
branch, so that auto-merge blocks a pull request until the chosen checks pass.
It records which checks are safe to require today, which remediated workflows
must be verified before they can be required, how to apply the ruleset, and how
to keep the required set from silently breaking.

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

Auto-merge is enabled. The full CI gate is **applied and live (2026-06-16)** --
repository ruleset `Required CI - Unity Tests (default branch)` (id `17663217`,
active, `~DEFAULT_BRANCH`) now requires **15 contexts**: the `Unity CI Success`
aggregate plus the 14 remediated static/correctness gates from
[Augmenting the gate](#augmenting-the-gate-done-2026-06-15). The
`bot-auto-commit` App (id `3977200`) stays in `bypass_actors` (mode `always`) so
the perf-doc auto-commit still pushes to `master`. The remediation merged via
PR #232 (`c42f8a4`); all 14 static contexts were verified
present-and-reporting on a real PR (#232) before the augment.

> **Planned follow-up (chosen 2026-06-15): collapse static checks to a
> `CI Success` aggregate.** The 14 individual static contexts are an interim.
> The agreed end state is a new `ci.yml` that hosts the ubuntu static checks as
> jobs with a single `CI Success` alls-green gate (`re-actors/alls-green`).
> Unity is already represented by its `Unity CI Success` aggregate. After the
> static aggregate lands on `master` and is verified run-or-skip on real PRs,
> the ruleset switches to require just those 1-2 aggregate contexts. The design
> is tracked in the local `REMAINING-WORK-PLAN.md` Workstream B. Until then the
> 15-context set is the live gate.

## Currently applied required gate: Unity Tests

[`unity-tests.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/unity-tests.yml)
is currently applied as the required correctness gate. Its `pull_request` trigger
has no `paths:` filter, so the workflow always starts. A `matrix-config` job
lists the changed files and, only when every changed file is one of the two
CI-owned perf-doc artifacts
(`docs/architecture/performance.md`, `docs/architecture/perf-baseline.csv`),
sets a `ci-owned-docs-only` output that skips the licensed matrix. Dependabot
and fork pull requests can also skip the licensed matrix because Unity serial
secrets are unavailable.

The required Unity check name is the stable aggregate:

```text
Unity CI Success
```

`Unity CI Success` has `if: ${{ always() }}` and uses
`re-actors/alls-green` over `matrix-config`, `runner-preflight`, and the
`unity-tests` matrix, with intentional matrix skips allowed. Do **not** require
the expanded matrix job names (`Unity <version> <mode>`), `Resolve Unity test
matrix`, or `Self-hosted runner access preflight`. When a job-level `if:` skips
a matrix before expansion, GitHub can report only one skipped check with the
literal name `Unity ${{ matrix.unity-version }} ${{ matrix.test-mode }}`, so
requiring the expanded names leaves auto-merge waiting for absent checks.

## Remediated gates to verify before requiring

These correctness/style gates now keep their `pull_request` trigger unfiltered,
use a `changes` detector job for the path decision, and fail closed if detection
fails. They are safe to add to branch protection only after the remediation is
merged to `master` and verified on real PRs.

| Workflow                | Stable required check name                 | Skips only after detecting no relevant files |
| ----------------------- | ------------------------------------------ | -------------------------------------------- |
| `csharpier-check.yml`   | Check C# formatting                        | doc-only PRs                                 |
| `dotnet-tests.yml`      | dotnet tests                               | doc-only PRs                                 |
| `json-format-check.yml` | Check JSON/.asmdef formatting              | PRs with no JSON/asmdef/Prettier config      |
| `markdownlint.yml`      | Lint repository Markdown                   | code-only PRs                                |
| `spellcheck.yml`        | Check spelling                             | PRs with no scanned types                    |
| `validate-banner.yml`   | Validate banner SVG                        | PRs off the banner paths                     |
| `validate-llms-txt.yml` | Check llms.txt is up-to-date               | PRs off the llms paths                       |
| `yaml-format-lint.yml`  | Prettier and yamllint                      | non-YAML/non-Prettier-config PRs             |
| `actionlint.yml`        | Lint GitHub Actions workflows              | non-workflow PRs                             |
| `script-tests.yml`      | Script tests (ubuntu/macos/windows-latest) | PRs off its paths                            |
| `validate-docs.yml`     | Validate Documentation Build               | code-only PRs                                |
| `lint-doc-links.yml`    | Lint docs links                            | code-only PRs                                |

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

Copy the required-gate pattern. Keep the workflow trigger unfiltered, and move
the path decision into the workflow so the check is always present. A first job
lists the changed files (the way `unity-tests.yml` does, via
`gh api .../files`) and sets an output that the gate job uses. The required gate
must fail closed: it may skip only when change detection succeeds and explicitly
emits `relevant=false`. If the `changes` job fails, is skipped, or emits an
unexpected value, the required gate runs a diagnostic guard step and fails
instead of reporting a skipped success.

For a single required job, put the decision in the required job's `if:` so the
job reports `skipped` after successful detection emits `relevant=false`.

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
    if: ${{ always() && (needs.changes.result != 'success' || needs.changes.outputs.relevant != 'false') }}
    runs-on: ubuntu-latest
    steps:
      - name: Validate change detection
        if: ${{ needs.changes.result != 'success' || (needs.changes.outputs.relevant != 'true' && needs.changes.outputs.relevant != 'false') }}
        run: |
          echo "::error::Change detection concluded '${{ needs.changes.result }}' with relevant='${{ needs.changes.outputs.relevant }}'. Required gates only skip after successful detection emits relevant=false."
          exit 1

      - run: ./check.sh
```

When `changes.relevant` is `false` and `changes` succeeded, `gate` is skipped,
reports success, and the required check `Check C# formatting` is still present.
The job `name:` is the required-check string, so it must be stable and not depend
on the job id.

For a required matrix job, do **not** put a falsifiable `if:` on the matrix job
itself. GitHub can evaluate that guard before matrix expansion and report only
one skipped check with the literal matrix expression. Either require an
always-reporting aggregate job, or keep the matrix job at `if: ${{ always() }}`
and gate the expensive steps internally. `script-tests.yml` uses the latter
pattern so the three required OS contexts are present even when no script files
changed.

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
          { "context": "Unity CI Success" },
          { "context": "Lint repository Markdown" },
          { "context": "Check C# formatting" },
          { "context": "dotnet tests" },
          { "context": "Check JSON/.asmdef formatting" },
          { "context": "Check spelling" },
          { "context": "Validate banner SVG" },
          { "context": "Check llms.txt is up-to-date" },
          { "context": "Prettier and yamllint" },
          { "context": "Lint GitHub Actions workflows" },
          { "context": "Script tests (ubuntu-latest)" },
          { "context": "Script tests (macos-latest)" },
          { "context": "Script tests (windows-latest)" },
          { "context": "Validate Documentation Build" },
          { "context": "Lint docs links" }
        ]
      }
    }
  ],
  "bypass_actors": []
}
JSON
```

Add the auto-commit App to `bypass_actors` (by its integration/app id, mode
`always`) so the perf-doc auto-commit keeps reaching the default branch. Add or
remove names in `required_status_checks` only after the corresponding workflow
has been verified present-and-reporting on real pull requests.

## Augmenting the gate (DONE 2026-06-15)

The path-filtered gates listed above were remediated to the always-report pattern
(each gained a `changes` job that lists the PR's files via `gh api` -- failing
safe to "run" if that call errors -- and each required gate fails closed if
change detection itself fails or emits no valid output). The remediation merged
via PR #232 (`c42f8a4`). **The augment is now LIVE:** ruleset `17663217`
requires `Unity CI Success` plus these 14 remediated ones (15 total). Each of the
14 static contexts was
verified present-and-reporting `success` on PR #232's check-runs (a real PR that
exercised the remediated workflows) before the augment; the skip-success path is
structural. Single required jobs carry a fail-closed job-level `if:` and matrix
required jobs keep `if: ${{ always() }}` at the job level while skipping
expensive steps internally. GitHub counts skipped required checks as passing, and
the workflows always trigger on `pull_request`.

The 14 remediated contexts (the source of the augment):

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

If devcontainer image changes should gate merges, also add
`Build + smoke-test devcontainer image` after verifying its run-or-skip behavior
on real PRs.

```bash
# PUT the existing ruleset with the augmented contexts (keep target/conditions/
# bypass_actors; extend required_status_checks). Two jobs were renamed to get a
# stable context: dotnet-tests' job is now `dotnet tests`, lint-doc-links' is
# `Lint docs links`.
gh api repos/OWNER/REPO/rulesets/<id> -X PUT --input augmented-ruleset.json
```

## Fragile check names

A required check is matched by literal string, so these break silently:

- **Matrix-interpolated names.** Do not require `Unity <version> <mode>` names.
  The Unity matrix is generated from `.github/unity-versions.json`, and
  job-level skip paths can prevent matrix expansion entirely, producing only the
  literal skipped check `Unity ${{ matrix.unity-version }} ${{ matrix.test-mode }}`.
  Require `Unity CI Success` instead. `script-tests.yml` is still a required
  matrix gate today: its job `name: Script tests (${{ matrix.os }})` expands to
  three real contexts -- `Script tests (ubuntu-latest)`,
  `Script tests (macos-latest)`, `Script tests (windows-latest)` -- and keeps
  `if: ${{ always() }}` at the job level so those contexts always report.
- **Jobs with no `name:`.** Their check-run context is the bare job id. Generic
  ids like `test`/`lint` collide across workflows and are easy to mistype, so
  every required job must keep a unique, stable `name:`.
- **Renames.** Renaming a job's `name:` drops the old required check (which then
  never reports) without any error. Treat required-check names as an API.
- **`pull_request_target`.** Do not require the auto-fix workflows. Their visible
  `Format and propose changes` job is path-filtered (absent on non-matching pull
  requests), and the `_fork` jobs run under `pull_request_target` in the
  base-repo context. Require the dedicated lint gates instead.

## Maintenance

When the required set or a workflow changes, keep them in sync:

1. Adding a required workflow: give its gating job a stable `name:`, make it
   always-report (above), make it fail closed on change-detection failures, then
   add the name to the ruleset.
1. Renaming a required job's `name:`: update the ruleset name in the same change.
1. Bumping `.github/unity-versions.json`: do not edit the ruleset while it
   requires `Unity CI Success`; verify the aggregate still reports on a real PR
   or dispatch run after the version change merges.
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
