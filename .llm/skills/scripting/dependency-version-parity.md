---
title: "Dependency Version Parity"
id: "dependency-version-parity"
category: "scripting"
version: "1.0.0"
created: "2026-06-04"
updated: "2026-06-04"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/lib/dependency-version-parity.js"
    - path: "scripts/lib/dependency-drift-recovery.js"
    - path: "scripts/validate-dependency-version-parity.js"
    - path: "scripts/repair-node-tooling.js"
    - path: "scripts/__tests__/dependency-version-parity.test.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "dependencies"
  - "lockfile"
  - "auto-repair"
  - "pre-push"
  - "cross-platform"
  - "tooling"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding that package-lock.json is gitignored and that npm ci vs npm install reconcile differently"

impact:
  performance:
    rating: "low"
    details: "Happy path is a handful of synchronous JSON reads; npm install runs only on real drift"
  maintainability:
    rating: "high"
    details: "Kills the root-npm-package.json manifest-vs-installed drift class with one offline detector reused by every gate"
  testability:
    rating: "high"
    details: "Pure detector + injected-fake recovery; the live repo invariant runs in script-tests + CI"

prerequisites:
  - "Understanding of npm ci semantics (lockfile-driven reinstall)"
  - "Familiarity with pre-commit / pre-push hook configuration"

dependencies:
  packages: []
  skills:
    - "integrity-gate-robustness"
    - "jest-hook-robustness"

applies_to:
  languages:
    - "JavaScript"
  frameworks:
    - "Jest"
    - "cspell"
    - "pre-commit"
  versions:
    node: ">=18.0"
    npm: ">=7.0"

aliases:
  - "Lockstep version pins"
  - "Gitignored lockfile drift"

related:
  - "integrity-gate-robustness"
  - "jest-hook-robustness"
  - "cross-platform-compatibility"

status: "stable"
---

# Dependency Version Parity

> **One-line summary**: `package.json` (committed, EXACT pins) is the source
> of truth; `package-lock.json` is gitignored. After any pin change the local
> lockfile + `node_modules` can go stale, so reconcile with `npm install`,
> never `npm ci`.

## The failure class

The native pre-push Jest suite failed with `cspell-lib` installed at `10.0.0`
while `package.json` declared `10.0.1`. The chain:

1. `package.json` is the committed source of truth and pins tools EXACTLY
   (`"cspell": "10.0.1"`, no `^`/`~`).
1. `package-lock.json` is GITIGNORED (per-machine, regenerated; never
   committed).
1. A `git pull` or Dependabot bump advances the manifest, but a developer
   machine that ran `npm install` during the old pin still has a stale local
   lockfile + `node_modules`.
1. The `npm ci`-based auto-repair (`getNpmRecoveryCommand` picks `npm ci`
   whenever a lockfile exists) faithfully reinstalls the stale lockfile and
   RE-CEMENTS the old version. `npm ci` can never reconcile a manifest change.
1. The file/resolver integrity probes only check PRESENCE and LOADABILITY,
   never VERSION, so the drift surfaced only at the slow last-resort native
   hook.

CI never hits this: a fresh checkout has no lockfile, so CI runs `npm install`
and always matches `package.json`. The class only bites a developer machine
with a stale local lockfile.

## The invariant

`scripts/lib/dependency-version-parity.js` (`probeDependencyVersionParity`)
enforces, offline:

- For every EXACT-pinned direct dependency: the installed
  `node_modules/<name>/package.json` version AND the local lockfile's
  resolved version (when a lockfile is present) MUST equal the pin.
- For RANGE pins (`^`/`~`/`>=`/`1.x`/`*`): the dependency must be PRESENT (an
  absent range dep is still `not-installed` drift). The detector takes NO
  `semver` dependency by design -- deps are kept minimal and the drift class is
  EXACT pins (which need only string equality); a stale lockfile cannot violate
  a `^`/`~` range the way it strands an exact pin. The satisfier is an injected
  hook (`semverSatisfiesFn`) used by tests, with no production default.
- Specs that are not version-comparable offline (`git+`, `file:`,
  `workspace:`, `npm:` alias, `link:`) are skipped.

It reads each installed manifest DIRECTLY off disk because `exports` maps
block `require("cspell-lib/package.json")`. Pure synchronous JSON reads -- no
network, no shell, no `node_modules` execution -- identical on Linux, macOS,
and Windows. Drift reasons: `installed-mismatch`, `not-installed`,
`lockfile-stale` (installed is correct but a stale lockfile would let a later
`npm ci` re-cement the wrong version), `range-unsatisfied`.

## Zero-touch recovery

`scripts/lib/dependency-drift-recovery.js` (`repairDependencyDrift`) runs
FIRST in `scripts/repair-node-tooling.js`, BEFORE the npm-ci integrity gate:

- Happy path: a few JSON reads, returns immediately -- no lock, no spawn.
- On real drift: runs `npm install --no-audit --no-fund` under the SHARED
  `node_modules` repair lock (`dxmsg-node-modules-repair.lock`, the same lock
  the npm-ci gate uses, so the two npm mutators never overlap), then re-probes.
- `npm install`, not `npm ci`, because the lockfile is gitignored and may be
  stale; only `npm install` rewrites the lockfile + `node_modules` to satisfy
  the manifest. By the time the npm-ci gate could run, the tree is consistent.

It honors `DXMSG_HOOK_NO_AUTOREPAIR=1` (skip the reconcile) and is itself
skipped by `DXMSG_HOOK_SKIP_INTEGRITY=1` (it lives after that early return in
`repair-node-tooling.js`). Probe/spawn failures are best-effort and never
abort the bootstrap -- the authoritative pass/fail is the validators below.

## Defense in depth (so the class cannot recur)

- `scripts/repair-node-tooling.js` -- runs first in the native hook + agentic
  preflight; reconciles via `npm install` (zero manual touch). NOTE: at the
  git-commit/push boundary this layer auto-REPAIRS the drift rather than
  blocking on it; the hard FAIL gates are the native pre-push validator + the
  live Jest invariant below, plus the edit-time advisory.
- `scripts/validate-node-tooling.js` -- postinstall + preflight; hard failure
  listing each drift.
- `scripts/doctor.js` `node_modules freshness` section -- `npm run doctor`
  read-only report of drift.
- `scripts/validate-dependency-version-parity.js` -- the post-edit guard runs
  it on `package.json` / `package-lock.json` edits for an edit-time signal back
  to the agent. Run it directly with `npm run validate:dependency-parity`.
- `scripts/__tests__/dependency-version-parity.test.js` live invariant --
  `script-tests` + CI; fails the push if any exact pin drifts.

## What NOT to do

- Do NOT one-side a pin (e.g. bump `cspell` without `cspell-lib`). The cspell
  monorepo publishes `cspell` and `cspell-lib` in lockstep; keep their pins
  equal. `scripts/__tests__/cspell-version-parity.test.js` enforces the
  package.json-internal lockstep.
- Do NOT "fix" drift with `npm ci` -- it honors the stale gitignored lockfile
  and re-cements the wrong version. Always `npm install` (or
  `npm run repair:node-tooling`, which does it automatically).
- Do NOT assume this also covers `.config/dotnet-tools.json` or
  `.unity-test-project/Packages/packages-lock.json`. It is scoped to the root
  npm `package.json`. Those siblings do NOT share the gitignored-stale-lockfile
  failure mode: `dotnet tool restore` reconciles directly from its manifest,
  and the Unity `packages-lock.json` is COMMITTED (authoritative), not
  gitignored. A future drift in THOSE is a separate class needing its own guard.
- Do NOT add a committed `package-lock.json` to "solve" this; the repo
  deliberately gitignores it. The detector + `npm install` recovery is the fix.

## See Also

- [Integrity Gate Robustness](./integrity-gate-robustness.md) -- the
  file-presence / resolver-health gate this version check complements.
- [Jest Hook Robustness](./jest-hook-robustness.md)
- [Cross-Platform Script Compatibility](./cross-platform-compatibility.md)

## Changelog

| Version | Date       | Changes                                                          |
| ------- | ---------- | ---------------------------------------------------------------- |
| 1.0.0   | 2026-06-04 | Initial: offline parity detector + `npm install` drift recovery. |
