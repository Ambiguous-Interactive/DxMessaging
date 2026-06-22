# Required Status Checks Runbook

This runbook is the reference for making CI a required gate for the default
branch, so that auto-merge blocks a pull request until the chosen checks pass.
It records which checks are safe to require today, which remediated workflows
must be verified before they can be required, how to apply the ruleset, and how
to keep the required set from silently breaking.

The brief branch-protection checklist in [CI and GitHub Settings](../ops/ci-and-github-settings.md#branch-and-tag-protection)
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

Auto-merge is enabled. The full CI gate is **applied and live (2026-06-16)**:
repository ruleset `Required CI - Unity Tests (default branch)` (id `17663217`,
active, `~DEFAULT_BRANCH`) now requires **15 contexts**: the `Unity CI Success`
aggregate plus the 14 remediated static/correctness gates from
[Augmenting the gate](#augmenting-the-gate-done-2026-06-15). The
`bot-auto-commit` App (id `3977200`) stays in `bypass_actors` (mode `always`) so
the perf-doc auto-commit still pushes to `master`. The remediation merged via
PR #232 (`c42f8a4`); all 14 static contexts were verified
present-and-reporting on a real PR (#232) before the augment.

> **Aggregate follow-up implemented in branch, not yet live in the ruleset.**
> `.github/workflows/ci.yml` consolidates the 14 static contexts into one
> `CI Success` alls-green gate (`re-actors/alls-green`). Unity is already
> represented by `Unity CI Success`. After this workflow lands on `master` and
> `CI Success` is verified on real PRs, switch ruleset `17663217` from the
> interim 15-context set to the two aggregate contexts: `CI Success` and
> `Unity CI Success`.

## Aggregate Gates

The target ruleset requires exactly these stable aggregate contexts:

```text
CI Success
Unity CI Success
```

Do not add individual static job names or Unity matrix names to the ruleset
after the aggregate switch. The aggregate jobs are the API that branch
protection consumes.

### CI Success

[`ci.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/ci.yml)
hosts the static correctness/style gates as jobs, with a final `CI Success` job
that has `if: ${{ always() }}` and uses `re-actors/alls-green` over every static
job. Static jobs also use `if: ${{ always() }}` and no-op internally when their
change detector says a PR or push does not touch relevant files. This keeps
every context present without allowing skipped dependencies in `CI Success`.

`CI Success` covers:

- `Lint repository Markdown`
- `Check C# formatting`
- `dotnet tests`
- `Check JSON/.asmdef formatting`
- `Check spelling`
- `Validate banner SVG`
- `Check llms.txt is up-to-date`
- `Prettier and yamllint`
- `Lint GitHub Actions workflows`
- `Script tests (ubuntu-latest)`
- `Script tests (macos-latest)`
- `Script tests (windows-latest)`
- `Validate Documentation Build`
- `Lint docs links`

### Unity CI Success

[`unity-tests.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/unity-tests.yml)
hosts the Unity correctness gate. Its `pull_request` trigger has no `paths:`
filter, so the workflow always starts. A `matrix-config` job lists changed files
and, only when every changed file is one of the two CI-owned perf-doc artifacts
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

## Interim Live Static Contexts

Until `CI Success` lands on `master` and is verified on real PRs, ruleset
`17663217` still requires the 14 legacy static contexts individually. They are
listed here so operators can audit or roll back the migration if needed.

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
- `deploy-docs.yml` (deploy is push-only; its PR build duplicates the
  `Validate Documentation Build` job in `ci.yml`), and the
  schedule/dispatch/release workflows, none of which have a `pull_request`
  trigger (`unity-benchmarks.yml`, `release*.yml`, `runner-bootstrap.yml`,
  `update-llms-txt.yml`, `update-issue-template-versions.yml`, `sync-wiki.yml`,
  `markdown-link-validity.yml`, `stuck-job-watchdog.yml`, `unstick-run.yml`,
  `devcontainer-prebuild.yml`, `unity-gameci-experiment.yml`).

## Remediation: make a gate always-report

Copy the required-gate pattern. Keep the workflow trigger unfiltered, and move
the path decision into the workflow so the check is always present. A first job
lists the changed paths via `gh api .../files`, includes `previous_filename` for
renames, and sets an output that the gate job uses. The required gate must fail
closed: it may skip only when change detection succeeds and explicitly emits
`relevant=false`. If the `changes` job fails, is skipped, or emits an unexpected
value, the required gate runs a diagnostic guard step and fails instead of
reporting a skipped success.

For an individually required job, put the decision in the required job's `if:`
so the job reports `skipped` after successful detection emits `relevant=false`.
For a job covered by an aggregate, keep the job-level condition at
`if: ${{ always() }}` and skip expensive steps internally. The aggregate should
not allow skipped dependencies unless the skipped dependency is intentional and
documented, as in `Unity CI Success`.

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
            --paginate --jq '.[] | .filename, (.previous_filename // empty)')"
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
and gate the expensive steps internally. `ci.yml` uses the latter pattern for
the three script-test OS contexts.

## Applying the ruleset

Prefer a repository ruleset over classic branch protection: rulesets support a
bypass list (needed so the perf-doc auto-commit App can push to the default
branch) and are API-manageable. Applying it needs repository admin; the commands
below are a template to run with an admin token, not something CI performs.

```bash
# List existing rulesets and current required checks.
gh api repos/Ambiguous-Interactive/DxMessaging/rulesets

# Inspect the current live ruleset. Confirm bypass_actors still includes the
# bot-auto-commit App (id 3977200) before applying any update.
gh api repos/Ambiguous-Interactive/DxMessaging/rulesets/17663217 \
  --jq '{
    name,
    enforcement,
    conditions,
    bypass_actors,
    contexts: [
      .rules[]
      | select(.type == "required_status_checks")
      | .parameters.required_status_checks[].context
    ]
  }'

# Build the aggregate update payload from the existing ruleset. This preserves
# conditions, pull-request settings, and bypass_actors while replacing only the
# required status-check contexts.
gh api repos/Ambiguous-Interactive/DxMessaging/rulesets/17663217 \
  --jq '{
    name,
    target,
    enforcement,
    conditions,
    bypass_actors,
    rules: [
      .rules[]
      | {type, parameters}
      | with_entries(select(.value != null))
      | if .type == "required_status_checks" then
          .parameters.required_status_checks = [
            {"context": "CI Success"},
            {"context": "Unity CI Success"}
          ]
        else
          .
        end
    ]
  }' > aggregate-ruleset.json

# Review the payload, especially bypass_actors, before applying it.
jq '.bypass_actors, (.rules[] | select(.type == "required_status_checks"))' \
  aggregate-ruleset.json

gh api repos/Ambiguous-Interactive/DxMessaging/rulesets/17663217 \
  -X PUT \
  --input aggregate-ruleset.json
```

Do not use `POST` for the aggregate switch; that creates a second ruleset
instead of updating the live one. Do not replace `bypass_actors` with an empty
array; the existing bot-auto-commit App bypass (integration id `3977200`, mode
`always`) must remain so the perf-doc auto-commit keeps reaching the default
branch. Add or remove names in `required_status_checks` only after the
corresponding workflow has been verified present-and-reporting on real pull
requests.

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

## Switching to the aggregate ruleset

After `.github/workflows/ci.yml` lands on `master`, verify `CI Success` reports
on a real documentation-only PR and a real code/workflow PR. Then update ruleset
`17663217` so `required_status_checks` contains only:

```text
CI Success
Unity CI Success
```

Keep the existing `conditions`, `bypass_actors`, and pull-request rule intact.
Do not remove the interim 14 static contexts before `CI Success` has reported on
the default branch; requiring an absent aggregate hangs auto-merge the same way
the old path-filtered workflows did.

## Fragile check names

A required check is matched by literal string, so these break silently:

- **Matrix-interpolated names.** Do not require `Unity <version> <mode>` names.
  The Unity matrix is generated from `.github/unity-versions.json`, and
  job-level skip paths can prevent matrix expansion entirely, producing only the
  literal skipped check `Unity ${{ matrix.unity-version }} ${{ matrix.test-mode }}`.
  Require `Unity CI Success` instead. Static script tests are inside `ci.yml`;
  the matrix still expands to `Script tests (ubuntu-latest)`,
  `Script tests (macos-latest)`, and `Script tests (windows-latest)`, but branch
  protection should require only `CI Success` after the aggregate switch.
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

1. Adding a static required check: add it as a job in `ci.yml`, add it to
   `ci-success.needs`, make it always-report, make it fail closed on
   change-detection failures, and update
   `scripts/__tests__/ci-aggregate-workflow.test.js`.
1. Renaming a static job's `name:`: keep `CI Success` unchanged, and update docs
   that list the human-readable job name.
1. Renaming `CI Success` or `Unity CI Success`: update the ruleset in the same
   change after the new name has reported on real PRs.
1. Bumping `.github/unity-versions.json`: do not edit the ruleset while it
   requires `Unity CI Success`; verify the aggregate still reports on a real PR
   or dispatch run after the version change merges.
1. Drift check: compare the ruleset's `required_status_checks` contexts against
   `CI Success` and `Unity CI Success`. `gh api .../rulesets/<id>` lists the
   configured contexts; aggregate job `name:` fields are the source of truth.

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
