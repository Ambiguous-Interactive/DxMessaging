---
title: "Git Hook Performance Budget"
id: "git-hook-performance"
category: "performance"
version: "1.4.0"
created: "2026-05-02"
updated: "2026-05-02"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".pre-commit-config.yaml"
    - path: "scripts/lib/precommit-perf-score.js"
    - path: "scripts/__tests__/hook-perf-budget.test.js"
    - path: "scripts/measure-hook-wallclock.js"
    - path: "scripts/run-staged-validators.js"
    - path: "scripts/run-staged-md-pipeline.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "git-hooks"
  - "pre-commit"
  - "ci-cd"
  - "performance"
  - "developer-experience"
  - "tooling"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of pre-commit hook execution model and Windows process spawn cost"

impact:
  performance:
    rating: "high"
    details: "Keeps local commit/push hooks fast and change-aware while exhaustive parity runs in CI/manual preflight"
  maintainability:
    rating: "high"
    details: "Static scorer plus wall-clock CI measurement catch regressions in PR review"
  testability:
    rating: "high"
    details: "Integration test, unit-style scorer tests, version-parity tests, and a wall-clock harness all enforce the budget"

prerequisites:
  - "Familiarity with pre-commit hook configuration"
  - "Understanding of process spawn cost on Windows"

dependencies:
  packages: []
  skills: []

applies_to:
  languages:
    - "JavaScript"
    - "YAML"
  frameworks:
    - "pre-commit"
  versions:
    pre-commit: ">=3.0"

aliases:
  - "Pre-commit performance"
  - "Hook budget"

related:
  - "cross-platform-compatibility"
  - "git-hook-performance-tooling"

status: "stable"
---

# Git Hook Performance Budget

> **One-line summary**: Local commit and push hooks must stay fast and change-aware; a static scorer plus a wall-clock CI job enforce the budget, while exhaustive parity stays in CI/manual `npm run preflight:pre-push`.

## Budget

- native pre-push: no-op under 2 seconds and one-file pushed range under 8 seconds wall-clock on Linux.
- pre-commit on a single-file commit: under 8 seconds wall-clock on Linux (proxy for Windows).
- Native pre-push validates pushed ranges via `scripts/run-native-prepush.js`; full `--all-files` parity is explicit CI/manual `npm run preflight:pre-push`.
- The static scorer enforces TWO ceilings:
  - Total budget (10): cumulative anti-pattern score across all
    pre-commit-stage hooks. Catches accumulated drift.
  - Per-hook ceiling (3): final score on any single hook. Catches
    single-rule regressions that would hide under the total budget's
    slack (with the real config at score 2, a stray `bash -lc` (5)
    lands at total 7 -- under 10 but well over the per-hook ceiling).
- Hooks that legitimately need a high-cost pattern must declare so via a
  `# perf-allow[<rule-ids>]: <substantive reason>` comment that names
  every waived rule. See [How to opt out](#how-to-opt-out). A waived rule
  does NOT count toward either ceiling.
- The wall-clock harness at `scripts/measure-hook-wallclock.js` enforces
  the per-scenario Linux budget directly; the scorer is the
  cross-platform proxy.

### Why two ceilings

Every defined rule is either =< 3 (small-cost) or >= 5 (high-cost), so
the per-hook ceiling of 3 mechanically partitions them: any single
high-cost rule trips the per-hook test on its own, and so does any
combination of small-cost rules summing above 3 on one entry. Examples:

- `bash -lc` (3) + `npx --yes` (2) on one hook -> final 5 -> per-hook
  violation (total budget would have allowed it).
- `npm install` (5) on one hook -> final 5 -> per-hook violation.
- `bash -lc` alone -> final 3 -> AT the ceiling, not in violation.
- `npm pack` (5) waived by `# perf-allow[npm-spawn]: <reason>` ->
  final 0 -> no violation (post-waiver score is what counts).

## Anti-patterns

The scorer at `scripts/lib/precommit-perf-score.js` walks every hook in
`.pre-commit-config.yaml`. For hooks that run at the `pre-commit` stage
(including hooks with no `stages:` declaration, since pre-commit defaults
to `[pre-commit]`), each rule below adds points to the pipeline budget.
Each rule has a stable ID used by the perf-allow waiver format described
in [How to opt out](#how-to-opt-out).

- `+5` `[scans-the-world]` `pass_filenames: false` with no `files:` filter.
  The hook scans the entire repo on every commit. Add a `files:` regex or
  pass staged paths through to the script.
- `+3` `[scans-the-world-with-files]` `pass_filenames: false` with a
  `files:` filter. The hook still pays the scan cost (the script does not
  receive the staged file list as argv). Switch to `pass_filenames: true`
  and accept `[files...]` argv when the script can consume them.
- `+5` `[always-run]` `always_run: true`. The hook fires on every commit
  regardless of staged input. Replace with a `files:` regex.
- `+5` `[npm-spawn]` Entry contains `npm pack`, `npm install`, `npm exec`,
  `npm test`, or `npm run validate:npm-meta`. These spawn heavy npm child
  processes and belong at pre-push.
- `+5` `[dotnet-no-batch]` Entry uses `dotnet tool run` without
  `require_serial: true`. Without serialization, pre-commit spawns one
  tool process per file. With `require_serial`, all staged files batch
  into one invocation.
- `+5` `[jest-at-pre-commit]` Entry runs Jest (via `run-managed-jest.js`
  or bare `jest`) at the pre-commit stage. Jest startup alone costs five
  to fifteen seconds. Move test runs to pre-push.
- `+2` `[npx-cold-start]` Entry uses `npx --yes`. On a cold cache the
  package downloads before the hook runs. Most hooks should prefer the
  `bash -c '[ -f node_modules/<pkg>/<bin> ] && node ...; else npx --yes
<pkg>@<pinned> ...'` shape so cold-cache fallback works without a
  managed Node wrapper.
- `+3` `[bash-login-shell]` Entry uses `bash -lc` or `bash --login -c`.
  Login shells load `~/.bash_profile`, nvm/fnm init, and similar profile
  scripts. That adds 100 to 500 ms per fire for nothing. Use `bash -c`.
- `+3` `[node-double-spawn]` Entry runs
  `node scripts/run-managed-<name>.js` where the wrapper exists only to
  spawn another Node or npx process. The double-spawn cost is roughly
  600 to 1200 ms on Windows. Inline the version-pinned fallback into a
  `bash -c` entry instead, and validate the pinned version against
  `package.json` with a parity test (see
  `scripts/__tests__/cspell-version-parity.test.js` and
  `scripts/__tests__/prettier-version-parity.test.js`). Only the Jest
  wrapper is exempt because managed Jest orchestrates a deterministic
  local-vs-fallback Jest invocation that cannot be expressed inline,
  and Jest only fires at pre-push so the cost is paid once per push.
- `+3` `[npm-run-at-hook]` Entry uses `npm run <script>`. npm wraps the
  actual script and adds roughly 500 ms to 1 s of node startup before
  the work begins. Inline the script body or call the underlying binary
  directly.

Total budget: 10 (cumulative across all pre-commit hooks). Per-hook
ceiling: 3 (final score on any single hook). The integration test fails
if either threshold is breached.

The companion skill
[Git Hook Performance: Stages and Tooling](git-hook-performance-tooling.md)
covers stage placement (what lives where), the consolidated validator
runner, the wall-clock harness, and the new-hook checklist. This page
focuses on the budget itself and the waiver mechanics.

## How to opt out

Add a `# perf-allow[<rule-ids>]: <reason>` comment immediately above the
offending `- id: <hookId>` line. The bracketed list MUST enumerate the
exact rule IDs the waiver covers. A rule that fires but is not listed
still contributes to the budget, so adding `always_run: true` to a hook
that already carries a waiver for `scans-the-world-with-files` re-trips
the budget instead of being silently absorbed.

Reasons must be substantive: the scorer rejects reasons shorter than 25
characters, reasons that match a stop-word list (`x`, `n/a`, `todo`,
`tbd`, `noop`, `fixme`, `idk`, `meh`, `ok`, `legacy`, `because`), and
reasons consisting only of punctuation or whitespace. Rejected
directives surface in the scorer report (`rejected reason: '...' on
hook ...`) and do NOT exempt the hook.

The legacy `# perf-allow: <reason>` form (no brackets) is rejected with
a clear error pointing at the new format.

Example:

```yaml
- repo: local
  hooks:
    # perf-allow[scans-the-world-with-files]: gated to .config/dotnet-tools.json via files: regex; fires only when the tool manifest changes, and `dotnet tool restore` reads the manifest itself rather than the staged path list
    - id: dotnet-tool-restore
      name: Install .NET tools
      entry: dotnet tool restore
      language: system
      files: '^\.config/dotnet-tools\.json$'
      pass_filenames: false
      stages:
        - pre-commit
        - pre-push
```

The `dotnet-tool-restore` hook keeps `pass_filenames: false` because the
entry script does not consume the staged file list, but the `files:`
filter narrows execution to a single path. The waiver captures that
reasoning AND scopes the exemption to the single rule that fires. A
future change that, for example, adds `always_run: true` would trip the
`always-run` rule outside the waiver list and fail the budget test.

## Where the budget tests live

- Integration test (real config):
  `scripts/__tests__/hook-perf-budget.test.js`. Scores the real
  `.pre-commit-config.yaml` and fails on either total-budget or
  per-hook-ceiling breach.
- Unit tests (synthetic configs):
  `scripts/__tests__/precommit-perf-score.test.js`. Verifies each
  scoring rule and the per-hook ceiling independently.
- Stage-policy guards:
  `scripts/__tests__/pre-commit-hook-stage-policy.test.js`.
- Version parity:
  `scripts/__tests__/cspell-version-parity.test.js` and
  `scripts/__tests__/prettier-version-parity.test.js` enforce that
  inlined `<pkg>@<version>` literals match `package.json`.
- Defense in depth:
  `scripts/validate-pre-commit-tooling.js` calls `scoreConfig` and
  surfaces the same total-budget and per-hook-ceiling violations.
- Wall-clock CI:
  `.github/workflows/hook-perf-measurement.yml` runs
  `scripts/measure-hook-wallclock.js` on PRs that touch hook config or
  scripts.

## Known caveats

### Adding a new skill takes two commits

The `skills-index-regen` and `update-llms-txt` hooks regenerate
`.llm/skills/index.md` and `llms.txt` whenever an `.llm/skills/*.md`
input changes. Pre-commit treats "files were modified by this hook" as
a hook failure, so the FIRST commit that adds a new skill file fails
after the regenerator stages its output. Run `git commit` again with
the same message; the regenerator now produces identical output, the
hook passes, and the commit succeeds. Subsequent commits to existing
skills do not hit this. Moving the regenerators to pre-push only would
lose the staging guarantee (the index would drift between the skill
commit and the next push), so the two-commit cost is deliberate.

### cspell moved to pre-push (round-3)

cspell costs about 5.5 s per fire (about 3.6 s of dictionary loading)
and was the biggest contributor to the single-file pre-commit budget.
Moving it to pre-push removes that cost from every commit while keeping
the gate at push time and in CI. Trade-off: a typo lands in the working
tree until the developer pushes; CI catches it before merge.

### validate-llms-txt moved to CI (round-3)

The `validate-llms-txt` pre-push hook ran `npm run validate:llms-txt`
(npm + Jest, about 9 s per push). The full generator-contract Jest suite
now lives only in `.github/workflows/validate-llms-txt.yml`; the
pre-push hook is now a cheap freshness diff
(`node scripts/update-llms-txt.js --check`).

### Wall-clock measurements diverge by host

The scorer is deterministic per-config; actual wall-clock varies across
hosts. Post round-4 Linux dev container: single-file pre-commit lands
around 4 to 5 s for `.cs` and 7 to 9 s for `.md` (cold cache adds
1 to 2 s). Windows is roughly 2x Linux; the .md pre-commit path
projects to 12 to 16 s on Windows -- over the 10 s goal but under the
previous 16 s ceiling. CI workflow
`.github/workflows/hook-perf-measurement.yml` enforces the Linux
budget directly.

## See Also

- [Git Hook Performance: Stages and Tooling](git-hook-performance-tooling.md)
- [Cross-Platform Script Compatibility](../scripting/cross-platform-compatibility.md)
- [JavaScript Code Quality](../scripting/javascript-code-quality.md)
- [Jest Hook Robustness](../scripting/jest-hook-robustness.md)

## References

- [pre-commit hook stages](https://pre-commit.com/#confining-hooks-to-run-at-certain-stages)
- [pre-commit require_serial](https://pre-commit.com/#hooks-require_serial)

## Changelog

| Version | Date       | Changes                                                                                                                                                                                                                                                                                                                                                                       |
| ------- | ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1.0.0   | 2026-05-02 | Initial version                                                                                                                                                                                                                                                                                                                                                               |
| 1.1.0   | 2026-05-02 | Round-3 wall-clock fixes: consolidated doc validators, dropped managed cspell/markdownlint wrappers, moved cspell to pre-push, added new scoring rules and a wall-clock CI harness                                                                                                                                                                                            |
| 1.2.0   | 2026-05-02 | Round-4 wall-clock fixes: collapsed five .md hooks (prettier/.md, markdown-structure-fix, markdown-link-fragment-list-fix, markdownlint, run-staged-validators/.md) into one in-process pipeline; inlined the prettier hook entry; added prettier-version-parity test; removed the prettier wrapper exemption from node-double-spawn                                          |
| 1.3.0   | 2026-05-02 | Round-5: added per-hook ceiling (3) alongside the total budget (10) so single-rule regressions on one hook (e.g. dropping a `bash -lc` (5 points) into one entry) cannot hide under the cumulative slack of the total budget; surfaced new perHookViolations[] in scoreConfig output and wired through formatReport, the integration test, and validate-pre-commit-tooling.js |
