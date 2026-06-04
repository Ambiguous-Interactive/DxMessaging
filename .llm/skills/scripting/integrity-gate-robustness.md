---
title: "Integrity Gate Robustness"
id: "integrity-gate-robustness"
category: "scripting"
version: "1.1.0"
created: "2026-05-18"
updated: "2026-05-19"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/lib/node-modules-integrity.js"
    - path: "scripts/lib/integrity-gate-with-recovery.js"
    - path: "scripts/lib/path-classifier.js"
    - path: "scripts/run-managed-jest.js"
    - path: "scripts/run-managed-prettier.js"
    - path: "scripts/run-managed-cspell.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "integrity"
  - "auto-repair"
  - "pre-commit"
  - "pre-push"
  - "windows"
  - "cross-platform"
  - "tooling"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding of partial-extract failure modes, npm ci semantics, and the path-classifier dispatch"

impact:
  performance:
    rating: "low"
    details: "Adds one fs.statSync per integrity target before tool invocation; sub-millisecond on healthy installs"
  maintainability:
    rating: "high"
    details: "Centralizes the auto-repair decision and partial-extract detection that every managed wrapper shares"
  testability:
    rating: "high"
    details: "Pure modules with explicit dependency injection; refusal cases all have direct unit tests"

prerequisites:
  - "Familiarity with pre-commit hook configuration"
  - "Understanding of Jest 27+ test runner architecture"
  - "Understanding of npm ci semantics (lockfile-driven reinstall)"

dependencies:
  packages: []
  skills:
    - "jest-hook-robustness"
    - "cross-platform-compatibility"

applies_to:
  languages:
    - "JavaScript"
  frameworks:
    - "Jest"
    - "Prettier"
    - "cspell"
    - "pre-commit"
  versions:
    node: ">=18.0"
    npm: ">=7.0"

aliases:
  - "Partial extract detection"
  - "Auto-repair gate"

related:
  - "jest-hook-robustness"
  - "cross-platform-compatibility"
  - "let-tools-resolve-modules"

status: "stable"
---

# Integrity Gate Robustness

> **One-line summary**: The managed wrappers (Jest, Prettier, cspell) probe
> `node_modules` integrity before invoking the underlying tool; on failure,
> they may auto-repair via `npm ci` only when the working copy is safe.

## Overview

Pre-push hooks in this repository must survive a partial `node_modules`
install. The integrity gate is the shared "probe -> auto-repair ->
re-probe" flow that every managed wrapper (`scripts/run-managed-jest.js`,
`scripts/run-managed-prettier.js`, `scripts/run-managed-cspell.js`) runs
BEFORE invoking the underlying tool, so a Jest/Prettier/cspell failure
that is really a partial extract is recovered automatically without
prompting the operator.

## Solution

1. Probe `node_modules` for the critical files in `INTEGRITY_TARGETS`
   via `scripts/lib/node-modules-integrity.js`. On Windows, also scan
   for zero-byte `*.node` native bindings.
1. If the probe fails, consult `isAutoRepairAllowed` (refuses
   mid-rebase, with a dirty lockfile, or when
   `DXMSG_HOOK_NO_AUTOREPAIR=1`). Refusal -> print banner and exit 1.
1. Otherwise run `npm ci --no-audit --no-fund`. On success, re-probe in
   a fresh Node subprocess (defeats the parent's stat/module cache).
   Subprocess probe ok -> proceed to tier dispatch.
1. Any failure prints the actionable repair banner and exits 1.

## Why the gate exists

A partial `npm install` (interrupted by long paths, antivirus, sleep, or
a network blip) can leave critical files like
`node_modules/jest-circus/build/runner.js` missing or zero-byte. The
next `npm install` reports "up to date" because the lockfile hash
matches and skips re-extraction, so Jest later emits
`testRunner option was not found` even though the JS-level resolver
appeared to succeed. The integrity gate
(`scripts/lib/node-modules-integrity.js`) closes that loop by probing
the on-disk critical-file list BEFORE Jest is invoked and calling
`npm ci` when a partial extract is detected.

## State diagram

```text
probe --(ok)----------------------> tier dispatch
  |
  (fail) -> auto-repair-allowed?
              |
              (refused / NO_AUTOREPAIR=1) -> banner + status=1 (or degraded)
              |
              (allowed) -> npm ci -> subprocess re-probe
                                       |
                                       (ok)   -> tier dispatch
                                       (fail) -> banner + status=1
```

After the gate succeeds (initial probe or recovery), the wrapper
proceeds to the existing local -> isolated -> npm-exec -> npx cascade.

## What auto-repair does NOT fix

The gate intentionally refuses to run `npm ci` when the operator's
working copy could be silently overwritten. Refusal cases:

- `npm` is not on PATH (`getNpmMajorVersion` returns `null`).
- `package-lock.json` has unstaged changes; `git diff --quiet` would
  exit non-zero. Auto-recovery here would clobber lockfile edits.
- A rebase is in progress (`.git/rebase-merge` or `.git/rebase-apply`
  exists). Touching `node_modules` mid-rebase could leave the working
  copy in an inconsistent state.
- Antivirus is actively quarantining files. `npm ci` will run but the
  next probe will still fail; in that case, the user must add an AV
  exclusion for `node_modules/` (Defender, Symantec, etc.).
- Operator override via `DXMSG_HOOK_NO_AUTOREPAIR=1`.

CI runners are NOT special-cased; set `DXMSG_HOOK_NO_AUTOREPAIR=1` to
fail instead of repair. `npm ci` CANNOT fix a dependency VERSION drift:
`package-lock.json` is gitignored, so drift is reconciled via `npm install`.

## Environment variables

| Variable                           | Effect                                                                                                                                                                                                                                |
| ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DXMSG_HOOK_SKIP_INTEGRITY=1`      | Bypass the integrity gate entirely. Tier dispatch runs without any pre-flight probe. Does NOT disable the orthogonal regenerable-cache heal in `repair-node-tooling.js` (that has its own opt-out, `DXMSG_HOOK_NO_REGENERABLE_HEAL`). |
| `DXMSG_HOOK_NO_AUTOREPAIR=1`       | Still probe, but skip `npm ci` on failure. Proceed to tier dispatch with a degraded gate (banner is printed).                                                                                                                         |
| `DXMSG_HOOK_AGGRESSIVE_RECOVERY=1` | `rm -rf node_modules` before `npm ci`. Use when the partial extract has gone past simple `npm ci` recovery.                                                                                                                           |

All three honor `isTruthyEnv` semantics from
`scripts/lib/jest-error-decoder.js`: `0`, `false`, `no`, `off`, and the
empty string are treated as falsy.

## Path-classifier dispatch

When Jest emits `MISSING_TEST_RUNNER` AFTER the gate has already passed
(rare but possible -- e.g. the captured path was healthy at probe time
but went bad between the probe and Jest's resolver), the dispatcher
classifies the captured runner path via
`scripts/lib/path-classifier.js`:

- `"repo"` -> route to `npm ci` recovery (gated by `isAutoRepairAllowed`).
- `"isolated"` -> route to isolated cache reset (legacy behavior).
- `"unknown"` -> refuse to auto-repair; print banner only.

This keeps tier-level recovery scoped to the directory the failure
actually came from.

## Windows-only: zero-byte native binaries

`findZeroByteNativeBinaries({ repoRoot })` scans `node_modules/**/*.node`
for size-zero files on Windows only (returns `[]` immediately on
Linux/macOS). The canonical failure: antivirus truncates a native
binding mid-write, leaving the JS probe passing but `require()` of the
native module crashing the parent process. The gate concatenates any
offenders to `missing[]` with `tool: "<native-binding>"`,
`reason: "zero-byte"` so the same downstream banner flow applies.

## Resolver probe

The file-only integrity probe is blind to the failure mode where
`node_modules/jest-circus/build/runner.js` is present on disk (probe OK)
but `require.resolve('jest-circus/runner')` THROWS at runtime. The
canonical Windows trigger is a missing or broken
`@unrs/resolver-binding-win32-x64-msvc` native binding: the JS file is
fine but the resolver chain cannot find it because the native binding
that backs `unrs-resolver` failed to load.

`probeResolverHealth({ repoRoot })` (in
`scripts/lib/node-modules-integrity.js`) closes that gap. It spawns a
fresh Node subprocess and runs a layered probe:

1. `require("unrs-resolver")` from the repo (falls back to
   `require("jest-resolve")` which transitively pulls it in). Throws at
   module load when the native binding is broken.
1. `new ResolverFactory({}).sync(repoRoot, spec)` for each
   `DEFAULT_RESOLVER_SPECIFIERS` entry (currently
   `["jest-circus/runner"]`). A half-loaded binding can survive
   `require()` but throw here.
1. `Module.createRequire(repoRoot/package.json).resolve(spec)` -- legacy
   belt-and-suspenders that catches missing peer deps and broken
   `exports` maps the unrs-resolver layers do not surface as cleanly.

Throws are reported as `{ specifier, error }` and merged into
`missing[]` with `tool: "<resolver>"`; the same `npm ci` recovery
path applies. After `npm ci`, the gate re-runs `probeResolverHealth`
(subprocess freshness is owned by that function). A contract test in
`scripts/__tests__/node-modules-integrity.test.js` pins the literal
`unrs-resolver` token in the inline script source so a future refactor
cannot regress this probe to Node-only resolution.

## Gate caching

`runIntegrityGateWithRecovery` memoizes its success verdict
per-repoRoot in a module-level `Map` for the lifetime of the parent
Node process. This amortizes the resolver-probe subprocess spawn
across the managed wrappers (`run-managed-jest`,
`run-managed-prettier`, `run-managed-cspell`) in a single hook: only
the first wrapper pays the spawn cost. Failure verdicts are NOT cached
because they carry side effects the next caller must observe fresh.
Tests use `__clearIntegrityGateCacheForTests()` to reset between cases.

## Cross-platform path-separator policy

User-facing log lines (`warnFn`, `console.warn`, `console.error`) in
the integrity-gate / managed-Jest code must emit POSIX-separator paths
even on Windows. The helpers live in
`scripts/lib/path-classifier.js`:

- `toPosixPath(value)` -- pure separator swap (`\` -> `/`). Idempotent
  on POSIX input. Maps `null` / `undefined` to `""` (no `"undefined"`
  leak in log lines); coerces other non-string primitives via
  `String(value)` and then swaps separators. Safe to use inside
  template literals without runtime type narrowing.
- `toRepoPosixRelative(absPath, repoRoot)` -- POSIX-relative when
  `absPath` lives under `repoRoot`; POSIX-absolute (via `toPosixPath`)
  fallback otherwise.

The contract is enforced by
`scripts/__tests__/cross-platform-path-handling.test.js`, which walks
the integrity-gate / managed-Jest source files and fails if any
`warnFn`/`console.warn`/`console.error` interpolation of a path-like
identifier (`*Path`, `*Dir`, `*Root`) is not wrapped in `toPosixPath` or
`toRepoPosixRelative`. The scope is scoped to the call sites that
participate in pre-push integrity recovery; widening the scope is a
deliberate addition to `SCAN_FILES` in that test.

`formatIntegrityFailure` (in `scripts/lib/node-modules-integrity.js`)
POSIX-normalizes its `relPath` input before formatting, so any caller
that records a backslash-flavored relPath in `missing[]` still produces
a uniform single-line summary across platforms.

## DXMSG_HOOK_NO_AUTOREPAIR interaction

When `DXMSG_HOOK_NO_AUTOREPAIR=1` is set, the gate still probes
(file + resolver health). On failure, it short-circuits BEFORE `npm ci`
and prints the repair banner with an extra root cause
("auto-repair disabled by DXMSG_HOOK_NO_AUTOREPAIR=1 (operator
override)") plus two `Either:`-prefixed unset alternatives:

- POSIX: `unset DXMSG_HOOK_NO_AUTOREPAIR`
- PowerShell: `Remove-Item Env:\DXMSG_HOOK_NO_AUTOREPAIR`

The `Either:` prefix distinguishes the POSIX-or-PowerShell alternatives
from the numbered sequential repair steps that precede them. Tier
dispatch then proceeds in degraded mode so the underlying tool surfaces
its own final error.

## See Also

- [Jest Hook Robustness](./jest-hook-robustness.md) -- the
  `--testRunner`-injection contract that the gate supplements.
- [Cross-Platform Script Compatibility](./cross-platform-compatibility.md)
- [Let Tools Resolve Modules](./let-tools-resolve-modules.md)

## Changelog

| Version | Date       | Changes                                                                                                                                       |
| ------- | ---------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| 1.0.0   | 2026-05-18 | Initial split-off from `jest-hook-robustness.md`; documents the gate and auto-repair UX.                                                      |
| 1.1.0   | 2026-05-19 | Add resolver probe, cross-platform path-separator policy + contract test, and DXMSG_HOOK_NO_AUTOREPAIR banner hint with shell-specific unset. |
