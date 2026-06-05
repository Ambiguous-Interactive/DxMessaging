---
title: "Native Git Hook Bootstrap"
id: "native-git-hooks"
category: "scripting"
version: "1.5.0"
created: "2026-05-22"
updated: "2026-06-04"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/hooks/"
    - path: "scripts/install-git-hooks.js"
    - path: "scripts/lib/hook-validation-stamp.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "git-hooks"
  - "cross-platform"
  - "automation"
  - "pre-push"

complexity:
  level: "intermediate"
  reasoning: "Requires native Git hook behavior plus cross-platform Node and npm shim handling"

impact:
  performance:
    rating: "low"
    details: "Runs only at Git hook boundaries and delegates to existing preflight commands"
  maintainability:
    rating: "high"
    details: "Makes hook installation versioned and automatic instead of a local manual step"
  testability:
    rating: "high"
    details: "The native hook wrapper and installer are covered by Jest contract tests"

prerequisites:
  - "Git installed locally"
  - "Node installed locally"

dependencies:
  packages: []
  skills:
    - "cross-platform-compatibility"
    - "integrity-gate-robustness"

applies_to:
  languages:
    - "JavaScript"
  frameworks:
    - "Git"
  versions:
    node: ">=18.0"

aliases:
  - "core.hooksPath"
  - "pre-push hook"
  - "hook bootstrap"

related:
  - "cross-platform-compatibility"
  - "integrity-gate-robustness"
  - "jest-hook-robustness"

status: "stable"
---

# Native Git Hook Bootstrap

> **One-line summary**: Keep Git hooks versioned under `scripts/hooks`, install them automatically with `core.hooksPath`, and make pre-push run the same preflight agents should run before hooks fire.

## Overview

The repository uses `pre-commit` for hook orchestration, but the Git hook entrypoints themselves are versioned under `scripts/hooks`. `scripts/install-git-hooks.js` sets local `core.hooksPath` to that directory during `postinstall`, so contributors and agents do not need a manual hook install step.

The native hook wrappers are intentionally small Node scripts. They avoid Bash, PowerShell, Husky, and shell-string composition at the Git boundary. Substantive cross-platform automation can still use PowerShell 7+ when that is the right tool, but the hook bootstrap must only require Git, Node, npm, Python when `pre-commit` needs auto-installation, and the repository files.

## Rules

1. Put native Git hook entrypoints in `scripts/hooks` with extensionless names for commit/push boundaries only (`pre-commit`, `pre-push`).
1. Use `#!/usr/bin/env node` and argument-array process launches.
1. Route npm and npx through `spawnPlatformCommandSync` so Windows shims do not depend on ad hoc shell behavior.
1. Keep dependency repair automatic. Managed Node tools should run the integrity gate and recover with `npm ci` when safe. The `pre-commit` executable is repaired by `scripts/ensure-pre-commit.js`, which uses an existing executable when available and otherwise installs pinned `pre-commit==4.6.0` with Python/pip.
1. Every mutating pre-commit hook must restage inside the pre-commit process through `scripts/run-and-restage.js` or `scripts/run-and-stage.js`. Do not rely on a native hook retry to stage after pre-commit restores a user's unstaged changes.
1. `pre-commit` must first honor a current validation stamp written by the agent PreToolUse commit guard. The stamp must cover HEAD, the staged index tree, and pathspec-limited unstaged/untracked changelog-relevant content, never status-only. That matches the pre-commit stage plus the changelog validator's local change scope without paying a full-worktree status walk. When the stamp matches, the wrapper may skip the repeated native run. Otherwise it must run `node scripts/repair-node-tooling.js`, then `node scripts/ensure-pre-commit.js`, then delegate to `pre-commit run --hook-stage pre-commit`. It retries only when the failed pass changed that same fingerprint, so successful restaged auto-fixes do not require a manual second commit attempt and non-mutating failures are not paid twice.
1. `pre-push` delegates to `scripts/run-native-prepush.js`. The runner reads Git's pre-push stdin, skips deleted refs and no-op updates before bootstrapping tooling, validates existing remote refs with the exact `remote_oid..local_oid` range, and validates new refs from the merge-base with the pushed remote's default branch (`refs/remotes/<remote>/HEAD`, then `refs/remotes/<remote>/master|main`) before origin/local fallbacks. After a clean full-scope or explicit-range `npm run preflight` with no caller-provided `SKIP`, a pre-push stamp may skip only when Git's exact pushed range matches the validated range exactly; stale, missing, narrower, broader, skipped, diverged, multi-tip, or no-base pushes still run native validation. Only when no base exists does it fall back to the exhaustive `npm run preflight:pre-push` command. It strips caller-provided `SKIP` before validation and calls `scripts/preflight.js --stage=pre-push --profile=guard --range-from <from> --range-to <to> --no-worktree`; local push must not run `--all-files`, `npm run doctor`, or `scripts/run-prepush-parallel.js`.
1. Exhaustive parity belongs to CI/manual `npm run preflight:pre-push`. `scripts/run-prepush-parallel.js` is retained only as an explicit exhaustive accelerator: it runs the mutating hooks (csharpier, dotnet-tool-restore) serially FIRST (so auto-fixers/restagers heal the tree before the read-only freshness checks observe it), then the read-only pre-push hooks plus the preflight-only validators concurrently, with the same coverage as `npm run preflight:pre-push`. It dedups the subset Jest suites against the full `script-tests` suite and the cspell subsets against the cspell hook. Coverage equivalence and the dedup proofs are pinned by `scripts/__tests__/run-prepush-parallel.test.js`.
1. Do not add native `post-checkout`, `post-merge`, or `post-rewrite` hooks. Git-event hooks run after ordinary checkout/merge/rewrite operations, can dirty the tree unexpectedly, and force bootstrap cost onto non-boundary operations. Keep repair and normalization in explicit preflight/doctor/format commands and in commit/push backstops.
1. `scripts/install-git-hooks.js` must refuse to configure `core.hooksPath` unless every required native hook is present.
1. `postinstall` may warn, but must not make `npm install` fatal when hook installation or local Git performance bootstrap cannot run outside a Git worktree. `scripts/configure-local-git-performance.js` may enable local `core.untrackedCache=true` when `git update-index --test-untracked-cache` passes, and may enable built-in `core.fsmonitor=true` only on supported Windows/macOS Git versions.
1. Cross-platform path invariant: never assert path containment with a bare `path.relative(...).startsWith("..")` (it is wrong across Windows drives -- use `isPathInsideDirectory`/`isPathOutsideDirectory`/`isOutsideRelative` from `scripts/lib/path-classifier.js`), and never root a `check-eol`/`fix-eol` test fixture under `os.tmpdir()` at all (`check-eol`'s case-sensitive `Temp` exclusion drops every Windows `os.tmpdir()` path, so the spawn passes vacuously -- and `git init`-ing the fixture dir does NOT remedy it; there is no git-init escape hatch). Instead root the fixture in an in-repo, NON-excluded scratch dir (`fs.mkdtempSync(path.join(REPO_ROOT, "dxm-...-"))`, with the prefix gitignored) and assert admissibility via the exported `isPathExcluded(dir) === false` from `check-eol.js`. Both are pinned by `scripts/__tests__/path-containment-policy.test.js` under `script-parser-tests`. See [Cross-Platform Script Compatibility](./cross-platform-compatibility.md#cross-drive-path-containment-windows).
1. Keep the cross-platform targeted Jest gate parser shared in `scripts/lib/cross-platform-preflight-gate.js`; `validate:workflows` must fail if `.github/workflows/cross-platform-preflight.yml` stops exposing a parseable bash continued list or PowerShell `$tests = @(...)` splat for `--runTestsByPath`.

## Agent Workflow

For hook-gated tooling, workflow, script, or `.llm` edits:

1. Run focused tests while editing.
1. Run `npm run repair:node-tooling`.
1. Run `npm run doctor`.
1. Run `npm run preflight:pre-push` when exhaustive CI/manual parity is needed.
1. Treat the Git hook as a final backstop only.

## Verification

- `node scripts/run-managed-jest.js --runTestsByPath scripts/__tests__/native-git-hooks.test.js`
- `npm run doctor`
- `npm run preflight:pre-push`

## See Also

- [Cross-Platform Script Compatibility](./cross-platform-compatibility.md)
- [Integrity Gate Robustness](./integrity-gate-robustness.md)
- [Jest Hook Robustness](./jest-hook-robustness.md)

## Changelog

| Version | Date       | Changes                                                                                                                                                                                                                                                               |
| ------- | ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1.5.0   | 2026-06-04 | Switched native pre-push to the fast range-aware `run-native-prepush.js` path, moved exhaustive parity to CI/manual `npm run preflight:pre-push`, and limited pre-push stamp skips to exact ranges validated without caller `SKIP`.                                   |
| 1.4.0   | 2026-06-01 | Added the now-retired full-surface pre-push validation stamp so an agent-run full pre-push preflight could make native pre-push a sub-second last-resort confirmation when the validated push surface was unchanged.                                                  |
| 1.3.0   | 2026-06-01 | Added the HEAD + index-tree + changelog-local pre-commit validation stamp, required retry decisions to use the same fingerprint, and documented the ban on native post-checkout/post-merge/post-rewrite mutating hooks.                                               |
| 1.2.0   | 2026-05-30 | Added the cross-platform path invariant rule: never bare `path.relative(...).startsWith("..")` (wrong across Windows drives), never root a `check-eol`/`fix-eol` fixture under `os.tmpdir()` (no git-init escape hatch); pinned by `path-containment-policy.test.js`. |
| 1.1.0   | 2026-05-30 | Documented the `run-prepush-parallel.js` parallel pre-push executor (fix-before-check ordering, coverage equivalence with `preflight:pre-push`, suite/cspell dedup).                                                                                                  |
| 1.0.0   | 2026-05-22 | Initial version: native hook bootstrap, automatic dependency repair, restage-in-process rule, and `install-git-hooks.js` refusal invariant.                                                                                                                           |
