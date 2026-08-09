<!-- trigger: unity, version, single-source, canonical, drift, validate-unity-versions | Single-source Unity version file and drift validator | Core -->

# Unity Version Single Source of Truth

> **One-line summary**: `.github/unity-versions.json` is the canonical Unity
> version list for all CI; `scripts/validate-unity-versions.js`
> (`npm run validate:unity-versions`) fails loud until every static workflow
> matrix and script mirror matches it.

## When to Use

- Bumping, adding, or removing a Unity version that CI builds or tests against.
- Adding a new workflow or runner script that needs a Unity version.
- Triaging a `validate:unity-versions` failure in actionlint CI or locally.

## When NOT to Use

- Changing which test assemblies run. That is the asmdef-discovery module (see
  [unity-ci-matrix](./unity-ci-matrix.md)).
- The `package.json` `unity` field. That declares the package's minimum
  supported Editor, not the CI build set.

## The Canonical File

`.github/unity-versions.json` holds two keys and nothing else:

```json
{
  "all": ["2021.3.45f1", "2022.3.45f1", "6000.3.16f1", "6000.5.2f1"],
  "release": "2022.3.45f1"
}
```

- `all` is the full set of Unity versions CI exercises. The validator requires it
  to be a non-empty array of valid version literals, with no duplicates, strictly
  ascending by the leading `major.minor.patch` triple (one build per line).
- `latest` is DEFINED as the last element of `all`. It is never stored as its own
  key. `perf-numbers.yml` tracks this newest version.
- `release` is the version the release pipeline pins. The validator requires it
  to be a member of `all`.

## Why a Split: Read the File vs Validated Mirror

Licensed workflow matrices deliberately do not read the JSON at runtime. Their
literal static values let the organization build-lock analyzer enumerate and
attest every lock identity before execution. Local scripts also need literal
defaults, so the validator keeps all mirrors aligned:

- `mirror-all` consumers carry the full canonical set.
- `mirror-latest` consumers carry only the newest canonical entry.
- `mirror-release` consumers carry the selected release entry.

The result: start a bump in `.github/unity-versions.json`, then update exactly
the static mirrors the validator reports.

## The Four Consumer Policies

`scripts/validate-unity-versions.js` assigns each file one policy.

- `no-literals`: the file must contain zero Unity version literals in code. This
  is the default for active workflows not explicitly registered.

- `mirror-all`: the set of code literals must equal `all` exactly.
  - `.github/workflows/unity-tests.yml`
  - `.github/workflows/unity-benchmarks.yml`
  - `.github/workflows/runner-bootstrap.yml`
  - `scripts/unity/maintain-windows-runner.ps1`
  - `scripts/unity/install-runner-maintenance-task.ps1`

- `mirror-release`: every code literal must equal `release`, and there must be at
  least one.
  - `.github/workflows/release.yml`

- `mirror-latest`: every code literal must equal the last `all` entry.
  - `.github/workflows/perf-numbers.yml`

Excluded from scanning: the canonical file itself, and everything under
`.github/workflows-disabled/` (an intentionally unchecked archive). The
validator strips inline `#`
comments before scanning, so a version mentioned in a comment does not count as a
code literal.

## Static Workflow Mirrors

The three licensed matrix workflows use literal matrices. Their policy entries
make a canonical-version bump fail until every static mirror is updated in the
same change.

## How to Bump a Version

1. Edit `.github/unity-versions.json`. Append or change entries in `all` (keep
   it strictly ascending), and set `release` if the pinned release version
   moves. Adding a newer entry to the end of `all` redefines `latest`.
1. Run the validator:

   ```bash
   npm run validate:unity-versions
   ```

1. If the validator flags a `mirror-all`, `mirror-latest`, or `mirror-release`
   consumer, update that file so its literal set matches the new canonical
   value, then re-run the validator until it passes. A `no-literals` failure
   means an unregistered workflow introduced a version literal.

The validator prints the resolved `all`, `latest`, `release`, and the count of
consumer files checked on success, so you can confirm the bump landed.

## Enforcement Points

- `.github/workflows/ci.yml` runs it in the `Lint GitHub Actions workflows` job,
  so drift blocks the merge.
- `npm run validate:all` runs it locally.

The validator is pure Node (only `fs`, `path`, `JSON.parse`, and a regex), so it
runs in CI without an `npm install` step.

## See Also

- [Unity CI Matrix](./unity-ci-matrix.md)
- [Devcontainer Cache Contract](../../unity-editor-conventions/references/devcontainer-cache-contract.md)
- [GitHub Actions Workflow Consistency](../../github-workflow-consistency/references/workflow-consistency.md)

## References

- Canonical file: `.github/unity-versions.json`
- Validator: `scripts/validate-unity-versions.js`
