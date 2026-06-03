---
title: "Git Hook Performance: Stages and Tooling"
id: "git-hook-performance-tooling"
category: "performance"
version: "1.1.0"
created: "2026-05-02"
updated: "2026-05-02"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".pre-commit-config.yaml"
    - path: "scripts/run-staged-validators.js"
    - path: "scripts/run-staged-md-pipeline.js"
    - path: "scripts/measure-hook-wallclock.js"
    - path: ".github/workflows/hook-perf-measurement.yml"
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
  reasoning: "Requires familiarity with pre-commit hook stages and Node startup cost"

impact:
  performance:
    rating: "high"
    details: "Documents the stage placement and consolidation rules that keep the pipeline under budget"
  maintainability:
    rating: "high"
    details: "Centralizes the operational guidance that the budget skill links to"
  testability:
    rating: "medium"
    details: "Tooling described here is enforced by tests in the budget skill"

prerequisites:
  - "Familiarity with the budget skill (git-hook-performance)"

dependencies:
  packages: []
  skills:
    - "git-hook-performance"

applies_to:
  languages:
    - "JavaScript"
    - "YAML"
  frameworks:
    - "pre-commit"
  versions:
    pre-commit: ">=3.0"

aliases:
  - "Hook stages"
  - "Hook tooling"

related:
  - "git-hook-performance"

status: "stable"
---

# Git Hook Performance: Stages and Tooling

> **One-line summary**: Where each hook lives (pre-commit / pre-push / CI), how to consolidate per-file validators, how to measure wall-clock, and the new-hook checklist.

This page is the operational companion to the budget skill at
[Git Hook Performance Budget](git-hook-performance.md). Read the budget
skill first for the scoring rules and the waiver mechanics; this page
covers the workflow questions that drop out of those rules.

## What lives where

The pipeline divides along three axes: cost, scope, and recovery.

- pre-commit (must be fast and per-file):
  - Formatters that mutate the staged file (csharpier, prettier for
    JSON/YAML/asmdef/asmref, fix-eol, fix-csharp-underscore-methods,
    sync-banner-version).
  - The consolidated markdown pipeline at
    `scripts/run-staged-md-pipeline.js` (round-4): one Node process
    that runs fix-md036-headings, fix-md029-md051, prettier --write,
    markdownlint-cli2 --fix, and the three doc validators
    (validate-docs-ascii, validate-doc-code-patterns,
    validate-docs-prose) in sequence on every staged `.md` /
    `.markdown` file. Replaces five separate hooks.
  - The consolidated C# validator runner at
    `scripts/run-staged-validators.js` (the same three validators,
    narrowed to `.cs` for the round-4 pipeline split).
  - Cheap structural validators that read only one or two files
    (validate-skills, validate-vscode-settings,
    validate-pre-commit-tooling, validate-lychee-config,
    validate-changelog-policy, eol-bom-check, conflict-markers,
    skills-index-regen, update-llms-txt).
- pre-push (cost gate; tests, networked checks, repo-wide scans):
  - cspell (spell-check; about 5.5 s per fire is too slow for commit
    cadence).
  - skills-index-check, validate-npm-meta, script-parser-tests,
    script-tests, actionlint, yamllint.
  - check-llms-txt-fresh (cheap diff against a freshly generated
    `llms.txt`).
  - run-staged-validators on `.cs` (provides the validator gate
    redundantly at push time so unrelated commits cannot land
    documentation drift via the C# XML doc-comment surface).
- CI only (anything over 5 seconds or that reads from the network):
  - Full Jest suite (`.github/workflows/script-tests.yml`).
  - validate-llms-txt full generator-contract suite
    (`.github/workflows/validate-llms-txt.yml`).
  - markdownlint sweep across the whole repo
    (`.github/workflows/markdownlint.yml`).
  - The wall-clock measurement harness
    (`.github/workflows/hook-perf-measurement.yml`).
  - validate-docs-prose, validate-docs-ascii, and
    validate-doc-code-patterns each run as standalone jobs on every
    PR via `.github/workflows/docs-lint.yml` (round-4 added the prose
    job).

## Consolidating validators and fixers

There are two consolidated runners. Adding a new check should target
one of them rather than introducing a fresh hook.

### `scripts/run-staged-md-pipeline.js` (markdown path)

For any new check that runs on `.md` / `.markdown` files, wire it into
the markdown pipeline. The pipeline currently chains, in one Node
process:

1. `fix-md036-headings.processMarkdownContent` (in-process auto-fix).
1. `fix-md029-md051.processMarkdownContent` (in-process auto-fix).
1. `prettier --write` via the `prettier` programmatic API
   (`format()` + `resolveConfig()`).
1. `markdownlint-cli2 --fix` via the `main(params)` API the package
   exports from its `.mjs` entry (round-4 used dynamic import from
   CommonJS).
1. `validate-docs-ascii.scanContent`,
   `validate-doc-code-patterns.scanMarkdown`, and
   `validate-docs-prose.scanContent`.

Round-4 superseded the earlier guidance that "fixers must remain
separate hooks." Pre-commit reports "files were modified by this
hook" the same way whether five hooks or one hook performed the
rewrite, so consolidating the fixers does not change the user-visible
UX. The pipeline tracks rewrites via mtime/size so it can report a
stable "auto-fixed N file(s); re-stage to commit" message.

A new markdown check qualifies for inclusion when:

- It is per-file (input is a single file's content; no cross-file state).
- It exports a stable `processMarkdownContent(content)` (for fixers)
  or `scanContent(filePath, content)` / `scanMarkdown(...)` (for
  validators) function.

### `scripts/run-staged-validators.js` (C# path)

For any new per-file `.cs` validator, prefer adding it to
`scripts/run-staged-validators.js`. The runner imports each
validator's `scanContent` API and calls them in one Node process;
each extra hook entry costs 200 to 600 ms of Node startup on Windows.

A new validator qualifies for consolidation when:

- It is per-file (input is a single file's content; no cross-file state).
- It exports a stable `scanContent(filePath, content)` or
  `scanFile(filePath)` function whose return shape includes
  `violations[]`.
- Its `files:` regex is a subset of the consolidated runner's filter
  (`\.cs$` excluding `Library/`, `Temp/`, `node_modules/`, `obj/`,
  `bin/`, and `*/bin/` `*/obj/`).

When consolidation is not appropriate, document the decision in the
new hook's description field so the next reviewer does not relitigate
it.

## Wall-clock measurement

The harness at `scripts/measure-hook-wallclock.js` measures real
wall-clock for a small set of representative scenarios and fails when
any scenario exceeds its per-scenario budget (8 seconds on Linux).

```bash
node scripts/measure-hook-wallclock.js          # human-readable
node scripts/measure-hook-wallclock.js --json   # machine-readable
```

The harness is not a pre-commit hook (it touches files and is too slow
for that cadence). The `.github/workflows/hook-perf-measurement.yml`
workflow runs it on every PR that touches `.pre-commit-config.yaml` or
any `scripts/` file, and on a weekly cron, and fails the PR if any
scenario regresses past budget.

## Adding a new hook (checklist)

1. Default to `stages: [pre-push]` for tests, network calls, and tool
   spawns.
1. Reserve `pre-commit` for staged-file formatters, the consolidated
   validator runner, and cheap structural validators.
1. Always set both `files:` and (where the script can ignore generated
   directories) `exclude:` filters.
1. For external-process hooks (`dotnet`, `java`, language toolchains),
   set `require_serial: true` so pre-commit batches all staged files
   into a single invocation.
1. Avoid `bash -lc`. Use `bash -c` unless you have a very specific
   reason to load login profiles.
1. After editing `.pre-commit-config.yaml`, run:

   ```bash
   node scripts/run-managed-jest.js --runTestsByPath \
     scripts/__tests__/hook-perf-budget.test.js \
     scripts/__tests__/precommit-perf-score.test.js \
     scripts/__tests__/pre-commit-hook-stage-policy.test.js
   ```

1. For changes that look like they could affect wall-clock (a new hook,
   a hook moved between stages, a wrapper script added or removed), run
   `node scripts/measure-hook-wallclock.js` locally before pushing.

## See Also

- [Git Hook Performance Budget](git-hook-performance.md)
- [Cross-Platform Script Compatibility](../scripting/cross-platform-compatibility.md)

## References

- [pre-commit hook stages](https://pre-commit.com/#confining-hooks-to-run-at-certain-stages)
- [pre-commit require_serial](https://pre-commit.com/#hooks-require_serial)

## Changelog

| Version | Date       | Changes                                                                                                                                                                                                                                                                                           |
| ------- | ---------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1.0.0   | 2026-05-02 | Initial split from git-hook-performance to honor the 300-line skill cap.                                                                                                                                                                                                                          |
| 1.1.0   | 2026-05-02 | Round-4: documented the new run-staged-md-pipeline.js (in-process .md fixer + prettier + markdownlint + validators), revised the "fixers cannot be consolidated" carve-out to reflect that pre-commit's modified-file UX is identical with one hook, and recorded the new wall-clock projections. |
