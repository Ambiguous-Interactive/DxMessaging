---
title: "Unity Version Single Source of Truth"
id: "unity-version-single-source"
category: "github-actions"
version: "1.0.0"
created: "2026-06-03"
updated: "2026-06-03"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".github/unity-versions.json"
    - path: "scripts/validate-unity-versions.js"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "github-actions"
  - "ci-cd"
  - "unity"
  - "version"
  - "drift"
  - "single-source"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding which CI consumers can read JSON at runtime and which must keep validated literals."

impact:
  performance:
    rating: "low"
    details: "The validator is pure Node and runs in well under a second"
  maintainability:
    rating: "high"
    details: "One JSON file is the single source for every Unity version literal in CI"
  testability:
    rating: "high"
    details: "A dependency-free validator plus a node:test suite pin the contract on every PR"

prerequisites:
  - "Familiarity with GitHub Actions workflow syntax"
  - "Awareness of the self-hosted Unity CI topology"

dependencies:
  packages: []
  skills:
    - "unity-ci-matrix"

applies_to:
  languages:
    - "YAML"
    - "JSON"
    - "PowerShell"
    - "Bash"
  frameworks:
    - "GitHub Actions"
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Unity version drift"
  - "Canonical Unity versions"

related:
  - "unity-ci-matrix"
  - "workflow-consistency"
  - "devcontainer-cache-contract"

status: "stable"
---

<!-- trigger: unity, version, single-source, canonical, drift, validate-unity-versions | Single-source Unity version file and drift validator | Core -->

# Unity Version Single Source of Truth

> **One-line summary**: `.github/unity-versions.json` is the canonical Unity version list for all CI; `scripts/validate-unity-versions.js` (`npm run validate:unity-versions`) fails loud if any workflow or script drifts, so a version bump touches only the JSON.

## When to Use

- Bumping, adding, or removing a Unity version that CI builds or tests against.
- Adding a new workflow or runner script that needs a Unity version.
- Triaging a `validate:unity-versions` failure in actionlint CI or locally.

## When NOT to Use

- Changing which test assemblies run. That is the asmdef-discovery module (see
  [unity-ci-matrix](../unity/unity-ci-matrix.md)).
- The `package.json` `unity` field. That declares the package's minimum
  supported Editor, not the CI build set.

## The Canonical File

`.github/unity-versions.json` holds two keys and nothing else:

```json
{
  "all": ["2021.3.45f1", "2022.3.45f1", "6000.3.16f1"],
  "release": "2022.3.45f1"
}
```

- `all` is the full set of Unity versions CI exercises. The validator requires it
  to be a non-empty array of valid version literals, with no duplicates, strictly
  ascending by the leading `major.minor.patch` triple (one build per line).
- `latest` is DEFINED as the last element of `all`. It is never stored as its own
  key. `perf-numbers.yml` and `unity-benchmarks.yml` track this newest version.
- `release` is the version the release pipeline and the GameCI experiment pin. The
  validator requires it to be a member of `all`.

## Why a Split: Read the File vs Validated Mirror

The clean answer would be for every consumer to read the JSON at runtime. Most
cannot. A workflow that runs ubuntu-bash can `jq` the file at runtime; a
self-hosted PowerShell step, a GameCI static input `default:`, and a local
`.ps1` / `.sh` entrypoint default cannot read it the same way. So the contract
splits consumers by capability:

- The bash-matrix resolvers read the file at runtime, carry zero literals, and are
  truly DRY.
- The static consumers keep a literal, and the validator keeps that literal honest
  with a loud failure on drift.

The result: bump versions only in `.github/unity-versions.json`, and the
validator tells you precisely which mirror, if any, went stale.

## The Three Consumer Policies

`scripts/validate-unity-versions.js` assigns each file one policy.

- `no-literals`: the file must contain zero Unity version literal in code, because
  it reads the canonical file at runtime via `jq`.
  - `.github/workflows/perf-numbers.yml`
  - `.github/workflows/unity-tests.yml`
  - `.github/workflows/unity-benchmarks.yml`
  - This is also the DEFAULT for every other active `.github/workflows/*.yml`, so
    a new workflow that hardcodes a version is caught with no extra wiring.

- `mirror-all`: the set of code literals must equal `all` exactly.
  - `.github/workflows/runner-bootstrap.yml`
  - `scripts/unity/maintain-windows-runner.ps1`
  - `scripts/unity/install-runner-maintenance-task.ps1`

- `mirror-release`: every code literal must equal `release`, and there must be at
  least one.
  - `.github/workflows/release.yml`
  - `.github/workflows/unity-gameci-experiment.yml`

Excluded from scanning: the canonical file itself, and everything under
`.github/workflows-disabled/` (an intentionally unchecked archive). The
validator strips inline `#`
comments before scanning, so a version mentioned in a comment does not count as a
code literal.

## The perf-numbers.yml Fix

`perf-numbers.yml` previously hardcoded the latest version in three places. The
`runner-preflight` job now resolves the latest version from the canonical file
and exposes `latest-version` and `unity-versions` outputs. The `perf-benchmarks`
matrix and both downstream `LATEST_VERSION` envs consume those outputs, so the
newest version lives in exactly one place.

## How to Bump a Version

1. Edit `.github/unity-versions.json` only. Append or change entries in `all`
   (keep it strictly ascending), and set `release` if the pinned release version
   moves. Adding a newer entry to the end of `all` redefines `latest`.
1. Run the validator:

   ```bash
   npm run validate:unity-versions
   ```

1. If the validator flags a `mirror-all` or `mirror-release` consumer, update that
   file so its literal set matches the new canonical value, then re-run the
   validator until it passes. A `no-literals` failure means a workflow hardcoded a
   version that it should read at runtime via `jq` instead.

The validator prints the resolved `all`, `latest`, `release`, and the count of
consumer files checked on success, so you can confirm the bump landed.

## Enforcement Points

- `.github/workflows/actionlint.yml` runs it in CI, so drift blocks the merge.
- `npm run validate:all` runs it locally.

The validator is pure Node (only `fs`, `path`, `JSON.parse`, and a regex), so it
runs in CI without an `npm install` step.

## See Also

- [Unity CI Matrix](../unity/unity-ci-matrix.md)
- [Devcontainer Cache Contract](../unity/devcontainer-cache-contract.md)
- [GitHub Actions Workflow Consistency](./workflow-consistency.md)

## References

- Canonical file: `.github/unity-versions.json`
- Validator: `scripts/validate-unity-versions.js`
