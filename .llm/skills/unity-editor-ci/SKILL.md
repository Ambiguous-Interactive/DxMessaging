---
name: unity-editor-ci
description: "Unity CI on self-hosted Windows runners: the unity-tests.yml matrix of 4 Unity versions x {editmode, playmode, standalone}, manual administrator installation under RUNNER_TOOL_CACHE/u6-v3, validation-only workflow checks with ensure-editor.ps1 -RequireHealthyExisting, the organization build-lock and timeout invariants that protect the two-seat Unity serial, Windows host prerequisites for 0xC0000135 startup failures, and repo-wide GitHub Action version pins. Use when bumping a Unity version, adding a matrix cell, triaging an IL2CPP-only, license, or editor-validation failure, or editing a Unity workflow."
metadata:
  category: "unity"
  tags: "unity, ci, matrix, il2cpp, lts, game-ci"
---

# Unity Editor CI

The active Unity workflows run `scripts/unity/run-ci-tests.ps1` directly on self-hosted
Windows runners. `unity-tests.yml` is one unified matrix of four Unity versions x
`{editmode, playmode, standalone}` = 12 cells; `standalone` builds and runs a
`StandaloneWindows64` IL2CPP player from a runner-local project under
`$RUNNER_WORKSPACE/dxm-u/t/<version>-<mode>/`.

## When to use

- Adding, bumping, or removing a Unity version in CI.
- Triaging an IL2CPP-only failure that does not reproduce in EditMode.
- A Unity job fails before any test output: license activation, editor provisioning, missing
  DLL, hung domain reload.
- Editing any workflow that consumes `compute-unity-assemblies`, the build lock, or timeouts.
- Auditing or bumping GitHub Action major versions.

## Rules

### Version single source of truth

- `.github/unity-versions.json` holds exactly two keys: `all` (non-empty, no duplicates,
  strictly ascending by `major.minor.patch`) and `release` (must be a member of `all`).
  `latest` is DEFINED as the last element of `all` and is never stored as its own key.
- Bump versions ONLY in that file, then run `npm run validate:unity-versions`.
- `scripts/validate-unity-versions.js` assigns each consumer one policy:
  `no-literals` (the default for unregistered workflows), `mirror-all`
  (`unity-tests.yml`, `unity-benchmarks.yml`, runner bootstrap and maintenance
  scripts), `mirror-latest` (`perf-numbers.yml`), and `mirror-release`
  (`release.yml`). Licensed workflow matrices stay literal and static so the
  organization lock analyzer can attest every matrix identity.
- `.github/workflows-disabled/` and the canonical file itself are excluded from scanning.
  `ci.yml` runs the validator in the `Lint GitHub Actions workflows` job, so drift blocks merge.

### License seat, lock, and timeouts

- The Unity serial has two activation seats org-wide with no server-side reclaim. Two controls
  protect it: `strategy.max-parallel: 1` serializes cells WITHIN a run, and the external
  `Ambiguous-Interactive/ambiguous-organization-build-lock` actions admit at most two runners
  ACROSS runs and repositories.
- `max-parallel: 1` goes under `strategy:` on the matrix workflows (`unity-tests.yml`,
  `unity-benchmarks.yml`, and `perf-numbers.yml`). A native
  `concurrency.group: wallstop-organization-builds` is repository-scoped and is FORBIDDEN.
- Timeout invariant: every step before and including the cleanup gate has an explicit positive
  timeout. Editor validation is capped at `10`, the acquire step cap (`305`) exceeds its
  internal wait (`300`), licensed work is capped at `120` to `180`, and cleanup uses `5`/`2`/`5`/`2`
  for return/classify/release/gate. The `900`-minute job cap must retain at least 60 minutes
  beyond the sum of those enforced step caps.
- Editors and modules are installed manually by a runner administrator under
  `RUNNER_TOOL_CACHE/u6-v3`. Workflows MUST NOT install, repair, uninstall, or quarantine
  editors. Validate with `-CiManagedOnly -RequireHealthyExisting` before acquiring the lock.

### The compute-unity-assemblies is-empty gate

- The compute step carries `id: compute`. Editor validation and the Unity work step
  may skip an empty assembly selection, but lock acquisition remains
  unconditional because each static matrix is structurally non-empty and the
  analyzer must be able to prove every acquisition.
- `Verify tests actually ran` must require `steps.compute.outcome == 'success'` plus either
  `is-empty == 'true'` or a non-skipped Unity run step, and receives
  `expected-empty: ${{ steps.compute.outputs.is-empty }}`. Never gate verify on is-empty alone.

### Editor validation and manual maintenance (`scripts/unity/ensure-editor.ps1`)

- CI must pass `-CiManagedOnly -RequireHealthyExisting` plus `-ProvisioningProfile`
  explicitly: `EditorOnly` for editmode, playmode, benchmarks, and release checks;
  `StandaloneWindowsIl2Cpp` for standalone (verifies `windows-il2cpp`); `Android` and `Full`
  remain manual-maintenance profiles.
- `unity install-path` with NO arguments is a GETTER. The SET form uses a flag (`-s`, then
  `--set` as fallback) and is best-effort only; discovery always relies on the getter.
- The installer only writes the User-scope registry PATH, so the session PATH must be
  refreshed from both Machine and User scopes with `%LOCALAPPDATA%\Unity\bin` prepended and
  the existing `$env:PATH` appended LAST to preserve process-only entries.
- Any module install must pass `--accept-eula`, built in exactly one place
  (`Get-UnityCliModuleInstallArguments`). `android-open-jdk` is deliberately absent from the
  requested `-m` ids (its real id is version-pinned) but IS in the verified-on-disk groups.
- Module presence is decided by disk probes, not CLI exit codes. Missing `core` groups trigger
  quarantine to `<install-root>\_quarantine\<version>-<timestamp>-<id>` plus reinstall; missing
  `android` groups retry through `Install-UnityAndroidModules` first.
  `DXM_UNITY_DISABLE_EDITOR_REPAIR=1` is for debugging the installer only.
- The script must stay valid PowerShell 5.1 under `Set-StrictMode -Version Latest` and reparse
  under Linux `pwsh`.

### Windows host prerequisites

- A `0xC0000135` / `STATUS_DLL_NOT_FOUND` startup failure is host damage, not editor damage.
  `ensure-editor.ps1` emits a single-line `::error::` and refuses to loop on a Unity reinstall.
- Unity 2021.3, 2022.3, and 6000.x need BOTH the VC++ 2010 SP1 x64 Redistributable
  (`MSVCP100.dll`, `MSVCR100.dll`) and the VC++ 2015-2022 x64 Redistributable
  (`VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`, `MSVCP140.dll`). They are separate packages.
- A runner administrator runs `scripts/unity/bootstrap-windows-runner.ps1` directly on the
  host to install both and enable
  `HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem!LongPathsEnabled = 1`, adds Defender
  exclusions, and install `pwsh` via winget. `.github/actions/assert-unity-host-prereqs` and
  `runner-bootstrap.yml` always use `-DetectOnly`; workflows never install host prerequisites.

### Runner-local projects, logs, and action versions

- Fixed runners reuse scope-isolated projects under `$RUNNER_WORKSPACE/dxm-u/{t,b,p}/` and
  per-version package caches under `$RUNNER_WORKSPACE/dxm-c/`. Do not transfer Unity
  `Library/` directories through `actions/cache`; checkout must not clean these external paths.
  Existing custom cache directories require `.dxmessaging-ci-cache`; retry cleanup revalidates
  ownership and permits only the cache root's `upm/` and `npm/` children.
- Read a failing log in order: pre-Unity setup, `LICENSE SYSTEM`, `[Licensing]` / editor
  startup, `Reloading assemblies`, `Run tests on platform`, `Test results saved at`.
- Keep action majors consistent repo-wide across `.github/workflows/` and
  `.github/workflows-disabled/`: `checkout@v7`, `cache/restore@v6`, `cache/save@v6`, `setup-node@v7`,
  `setup-dotnet@v5`, `setup-python@v6`, `upload-artifact@v7`, `download-artifact@v8`,
  `github-script@v9`, `create-github-app-token@v3`, `attest-build-provenance@v4`,
  `deploy-pages@v5`, `upload-pages-artifact@v5`. Verify a tag exists upstream
  (`git ls-remote --tags`) before calling a version invalid; bump all instances in one PR.

## References

| Document                                                                                    | Purpose                                                                                                                  |
| ------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| [github-actions-version-consistency.md](./references/github-actions-version-consistency.md) | Repo-wide action major pins, the audit and upstream-tag verification commands, and artifact action pairing               |
| [unity-ci-matrix.md](./references/unity-ci-matrix.md)                                       | The 12-cell matrix, build-lock and timeout invariants, is-empty gate, IL2CPP-only failure catalog, and log reading order |
| [unity-editor-cli-bootstrap.md](./references/unity-editor-cli-bootstrap.md)                 | ensure-editor.ps1 internals: PATH refresh, getter-based discovery, module desired state, and quarantine/reinstall repair |
| [unity-runner-host-prereqs.md](./references/unity-runner-host-prereqs.md)                   | The four-layer Windows host prereq defense, both VC++ generations, and detection contracts                               |
| [unity-version-single-source.md](./references/unity-version-single-source.md)               | The canonical unity-versions.json contract, the three consumer policies, and how to bump a version                       |
