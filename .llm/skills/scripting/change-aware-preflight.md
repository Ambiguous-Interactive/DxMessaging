---
title: "Change-Aware Preflight"
id: "change-aware-preflight"
category: "scripting"
version: "1.3.0"
created: "2026-05-29"
updated: "2026-06-01"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/preflight.js"
    - path: "scripts/lib/changed-files.js"
    - path: "scripts/lib/precommit-stage-model.js"
    - path: "scripts/hooks/preflight-before-push-guard.js"
    - path: "scripts/hooks/preflight-on-stop.js"
    - path: "scripts/lib/hook-validation-stamp.js"
    - path: ".pre-commit-config.yaml"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "pre-commit"
  - "pre-push"
  - "automation"
  - "cross-platform"
  - "tooling"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of pre-commit stage selection, the change-set definition, and the Claude Code hook lifecycle"

impact:
  performance:
    rating: "medium"
    details: "Runs only what changed; the push guard has a direct changed-file cspell lane and defers heavy Jest suites to the native pre-push hook"
  maintainability:
    rating: "high"
    details: "Collapses 22 per-file imperatives into one automated command and a dispatch table"
  testability:
    rating: "high"
    details: "Four contract tests pin delegation, the change-set, the hook shapes, and cross-platform spawn policy"

prerequisites:
  - "Node installed locally"
  - "Familiarity with the pre-commit hook configuration"

dependencies:
  packages: []
  skills:
    - "native-git-hooks"
    - "cross-platform-compatibility"

applies_to:
  languages:
    - "JavaScript"
  frameworks:
    - "pre-commit"
  versions:
    node: ">=18.0"

aliases:
  - "npm run preflight"
  - "change-aware checks"
  - "preflight guard"

related:
  - "native-git-hooks"
  - "git-hook-performance"
  - "cross-platform-compatibility"

status: "stable"
---

# Change-Aware Preflight

> **One-line summary**: `npm run preflight` inspects exactly what you changed,
> delegates file -> hook selection to pre-commit, and runs the relevant checks
> in-loop so failures surface before the git hook -- which stays the last-resort
> backstop.

## Overview

You do not need to remember per-file checks before declaring a task done or
pushing. `npm run preflight` computes the change-set (committed range vs the
integration base + staged + unstaged + untracked), self-heals
`node_modules` / `pre-commit` first, and runs the lint / spelling / doc /
changelog / YAML / policy checks that apply to those files. A PreToolUse guard
first runs changed-file cspell over the full change-set before likely
`git commit` / `git push` commands, then runs full-scope preflight and BLOCKS
the git operation on failures. Commit commands are scoped to the pre-commit
stage for speed; after a successful commit guard, a validation stamp lets the
native pre-commit hook skip the repeated run only while HEAD, index content, and
pathspec-limited unstaged/untracked changelog-relevant content are unchanged.
The direct cspell pass catches already-committed, generated, and shell-written
spelling failures even when the broader preflight is too slow for the hook
timeout. A Stop hook runs preflight (working-tree scope, advisory only) when you
end a turn. The
PostToolUse edit guard also runs file-scoped cspell for the same extensions as
the native pre-push cspell hook (`.md`, `.markdown`, `.cs`, `.json`, `.yml`,
`.yaml`, `.ps1`, `.js`) and runs changelog coverage when a likely user-visible
path or `CHANGELOG.md` is edited, so spelling and missing-release-note failures
surface while the file is being edited. The native git hook is the exhaustive,
tool-agnostic backstop, not the first signal.

For full parity on demand, run `npm run preflight:pre-push`.

## Solution

`scripts/preflight.js` is a change-aware orchestrator. It does NOT re-implement
pre-commit's file matching; it asks pre-commit to select hooks per stage via
`--from-ref/--to-ref` (committed range) plus `--files` (working tree). When
pre-commit / Python is unavailable it falls back to the already-maintained npm
`check:*` / `validate:*` entrypoints (Node-direct mode), never parsing hook
`entry` strings.

### Flags

| Flag                     | Meaning                                                                                                                                                                                              |
| ------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--profile=guard\|full`  | `guard` = fast subset (DEFERS the heavy Jest suites `script-parser-tests` / `script-tests` / `unity-contract-tests` to the native pre-push hook); `full` = everything change-scoped. Default `full`. |
| `--scope=worktree\|full` | `worktree` = staged+unstaged+untracked only (SKIPS base resolution + the committed range; fast on a many-commit branch); `full` = committed range + working tree. Default `full`.                    |
| `--stage=<name>`         | Restrict to one stage. Default: the agent-relevant `pre-commit` + `pre-push` stages present in the config.                                                                                           |
| `--base=<ref>`           | Explicit integration base (CI passes the PR base).                                                                                                                                                   |
| `--files=<a,b,...>`      | Explicit working-tree file list (comma-separated or repeated).                                                                                                                                       |
| `--all`                  | Exhaustive `--all-files` parity per stage.                                                                                                                                                           |
| `--json`                 | Emit a machine-readable status object instead of human output.                                                                                                                                       |
| `--no-recover`           | Skip the `node_modules` / pre-commit auto-recovery (the guard passes this so recovery is not paid twice in a session).                                                                               |

The `--json` status object: `{ status: { kind: "ok"|"checks-failed"|
"infra-unavailable", failures[], policyFailures[], warnings[] }, scope, profile,
mode, base, changedFileCount }`. Exit code is non-zero IFF
`kind === "checks-failed"`. Policy/security hook failures populate
`policyFailures[]` and ALWAYS force `checks-failed` -- they never fail open.

### File -> validator dispatch (with remediation)

This table is the operational residue of the former per-file context.md rules.
`npm run preflight` runs the matching command for you; this is the manual
equivalent and the remediation when a check fails.

| When you change                                                                                              | Validator(s) preflight runs                                                                                                         | On failure                                                                                                                                                                                                                                                                             |
| ------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `.cs`, `.md`, `.json`, `.yml`, `.yaml`, `.ps1`, `.js`                                                        | `node scripts/run-managed-cspell.js --no-progress --no-summary <files>`                                                             | Update `.cspell.json` in the same change for legitimate domain terms; before pre-push run `npm run check:cspell:all` for fast all-file attribution.                                                                                                                                    |
| User-visible code (`Runtime/`, `Samples~/`, user-facing `Editor/`, shipped `SourceGenerators/`)              | `npm run validate:changelog:coverage`                                                                                               | The PostToolUse guard also runs this in-loop. Update `CHANGELOG.md`; resolve `W002` by rewriting the entry around user impact, or move internal-only detail to developer docs.                                                                                                         |
| Markdown (`.md`)                                                                                             | `node scripts/run-staged-md-pipeline.js <md>` (normalize-ascii, md029/md051, prettier, markdownlint, ASCII/code/prose validators)   | Run `node scripts/normalize-docs-ascii.js <md>` and `node scripts/fix-md029-md051.js <md>` to auto-fix; ordered lists use MD029 `one` style; internal links must match MD051 slugs.                                                                                                    |
| C# XML doc comments (`.cs`)                                                                                  | `node scripts/run-staged-validators.js <cs>` (ASCII / code-patterns / prose)                                                        | Docs and `///` comments must be pure ASCII; every C# sample must compile -- see the doc skills.                                                                                                                                                                                        |
| `.yml` / `.yaml` (incl. `.github/workflows/**`, `.github/actions/**`)                                        | YAML format + line-length (`fix-yaml-comments-line-length`, `fix-yaml-block-scalar-line-length`, prettier); yamllint via pre-commit | Auto-fix with `npm run format:yaml:comments` / `npm run format:yaml:lines`; for non-breakable overflow shorten or externalize per [YAML Line-Length Budget](../github-actions/yaml-line-length.md). For workflows also `npm run validate:workflows` + `npm run check:workflow-cspell`. |
| `.pre-commit-config.yaml`, `scripts/*` hook tooling, `.github/workflows/*.yml`                               | pre-commit-stage hooks + pre-push validators                                                                                        | Editing files gated by `script-parser-tests` / `script-tests` / `unity-contract-tests` requires those Jest suites (deferred under `--profile=guard`; run via `npm run preflight:pre-push`).                                                                                            |
| `package.json`, `package-lock.json`, `scripts/lib/node-modules-integrity.js`, `scripts/run-managed-*.js`     | `validate-npm-meta`, integrity gate                                                                                                 | Also run `npm run doctor`. On a `testRunner option was not found` / `jest-circus` error, run `npm run doctor` and consult [Jest Hook Robustness](./jest-hook-robustness.md).                                                                                                           |
| Repository tests or banner tooling                                                                           | `validate-banner` / `sync-banner-version`                                                                                           | If banner drift is reported, run `npm run sync:banner` and re-check.                                                                                                                                                                                                                   |
| Shipping content (`Runtime/`, `Editor/`, `SourceGenerators/`, `Samples~/`, `docs/`, root `*.md`, `llms.txt`) | `validate-no-plan-vocabulary`                                                                                                       | Never introduce `PLAN.md` / `T0.0` / `P0.0`-style references; treat a failure as a prose rewrite, not a hook bypass. See [No PLAN Vocabulary](../documentation/no-plan-vocabulary.md).                                                                                                 |
| Any untracked-and-unignored repo-root path                                                                   | `validate-untracked-policy`                                                                                                         | Commit the file or extend `.gitignore` / `.npmignore`.                                                                                                                                                                                                                                 |
| `.llm/context.md`, `.llm/skills/**/*.md`                                                                     | `npm run validate:llm-policy` (after `npm run repair:llm-policy`)                                                                   | Fix skill size/schema, index freshness, and markdown policy before hook-time.                                                                                                                                                                                                          |

### Node-direct fallback and the loud yamllint skip

When `ensurePreCommit()` returns `{ok:false}` (`missing-python` /
`install-failed` / `module-unavailable`), preflight routes each targeted hook id
to its npm/node entrypoint (`NODE_DIRECT_MAP`) gated by the change-set. Policy /
security hooks run regardless and never fail open. yamllint CANNOT run without
Python, so when YAML changed preflight emits a LOUD top-level `WARNING` (in
`--json.warnings[]` and on stderr) -- never a silent skip -- and CI plus the
native pre-push hook enforce it on a Python-equipped machine. Any targeted hook
id with neither a map entry nor an explicit `NODE_DIRECT_EXEMPT` reason emits a
visible warning rather than passing silently.

### The `always_run` two-pass note

pre-commit's `always_run` (and whole-repo `pass_filenames:false`) hooks fire in
BOTH the `--from-ref` and `--files` passes. To avoid running them twice,
preflight runs two passes ONLY when both a committed range AND uncommitted files
exist; otherwise it runs the single relevant pass. This never under-runs.

### Division of labor

| Layer                         | Scope                      | When                         | Behavior                                                                                                                                                                                                                                                                                                                                                                |
| ----------------------------- | -------------------------- | ---------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| PreToolUse git-boundary guard | full (committed + working) | before `git commit` / `push` | BLOCKS on changed-file cspell failures before preflight; then blocks on `checks-failed` / any `policyFailures`; commit commands add `--stage=pre-commit`; allows on infra-unavailable with a warning.                                                                                                                                                                   |
| Stop hook                     | worktree only              | end of turn                  | ADVISORY ONLY -- emits a `systemMessage`, NEVER `decision:block`, always exits 0. Covers "declared done without pushing".                                                                                                                                                                                                                                               |
| PostToolUse edit guard        | one edited file + coverage | after file edits             | ADVISORY ONLY -- runs file-scoped cspell for cspell-covered extensions plus changelog coverage, packaging, and doc validators relevant to the edited file.                                                                                                                                                                                                              |
| Native `pre-commit` hook      | pre-commit stage           | real commit boundary         | Last-resort backstop. Skips only when the PreToolUse commit guard wrote a matching validation stamp for HEAD, index tree, and changelog-relevant local content; otherwise runs the real hook stage.                                                                                                                                                                     |
| Native `pre-push` hook        | `--all` parity             | real push boundary           | The exhaustive, tool-agnostic guarantee. Skips only when a successful full `npm run preflight:pre-push` or earlier native pre-push wrote a matching stamp for HEAD, Git index file hash, unstaged tracked worktree diff metadata plus changed tracked file bytes, and the empty untracked/unignored path set; otherwise delegates to `scripts/run-prepush-parallel.js`. |

Both hooks honor the `DXMSG_PREFLIGHT_ACTIVE=1` re-entrancy sentinel so a `git`
call made inside preflight is not re-guarded.

### Close the loop

If `npm run preflight` passes but a git hook later fails, that is a preflight
bug, not a reason to bypass the hook. Extend coverage -- add the file pattern to
the change-set, the stage to the targeted set, or the hook id to the Node-direct
map / exempt list -- so preflight and the hooks never diverge.

## Verification

- `node scripts/run-managed-jest.js --runTestsByPath scripts/__tests__/preflight-engine-contract.test.js scripts/__tests__/preflight-changed-files.test.js scripts/__tests__/claude-preflight-hooks-contract.test.js scripts/__tests__/preflight-cross-platform.test.js`
- `npm run preflight` (change-aware) and `npm run preflight -- --all` (parity)

## See Also

- [Native Git Hook Bootstrap](./native-git-hooks.md)
- [Git Hook Performance Budget](../performance/git-hook-performance.md)
- [Cross-Platform Script Compatibility](./cross-platform-compatibility.md)
- [YAML Line-Length Budget](../github-actions/yaml-line-length.md)
