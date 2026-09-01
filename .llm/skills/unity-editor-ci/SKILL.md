---
name: unity-editor-ci
description: "Unity CI on self-hosted Windows runners: the unity-tests.yml matrix of 4 editor-scoped jobs that each run editmode, playmode, and standalone, manual administrator installation under RUNNER_TOOL_CACHE/u6-v3, validation-only workflow checks with ensure-editor.ps1 -RequireHealthyExisting, the organization build-lock and timeout invariants that protect the two-seat Unity serial, Windows host prerequisites for 0xC0000135 startup failures, and repo-wide GitHub Action version pins. Use when bumping a Unity version, changing an editor job or test mode, triaging an IL2CPP-only, license, or editor-validation failure, or editing a Unity workflow."
metadata:
  category: "unity"
  tags: "unity, ci, matrix, il2cpp, lts, game-ci"
---

# Unity Editor CI

The active Unity workflows run `scripts/unity/run-ci-tests.ps1` directly on self-hosted
Windows runners. `unity-tests.yml` is a four-version matrix. Each editor-scoped
job runs `editmode`, `playmode`, and `standalone` as separate invocations under
one lock and cleanup window. The oldest and current endpoint jobs also run the
stripped shipping-fidelity player. Both player modes build `StandaloneWindows64`
IL2CPP players from runner-local projects under
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
  protect it: `strategy.max-parallel: 1` serializes editor jobs WITHIN a run, and the external
  `Ambiguous-Interactive/ambiguous-organization-build-lock` actions admit at most two runners
  ACROSS runs and repositories.
- `max-parallel: 1` goes under `strategy:` on the matrix workflows (`unity-tests.yml`,
  `unity-benchmarks.yml`, and `perf-numbers.yml`). A native
  `concurrency.group: wallstop-organization-builds` is repository-scoped and is FORBIDDEN.
- Timeout invariant: every step before and including the cleanup gate has an explicit positive
  timeout. Editor validation is capped at `10`, and the acquire step cap (`305`) exceeds its
  internal wait (`300`). Grouped correctness invocations use `90`/`90`/`150` caps, and the
  endpoint-only shipping invocation uses `150`. Cleanup uses `5`/`2`/`5`/`2` for
  return/classify/release/gate. `unity-tests.yml` uses a `1050`-minute job cap. Other licensed
  jobs retain their `900`-minute cap. Each cap must retain at least 60 minutes beyond the sum
  of its enforced step caps.
- Editors and modules are installed manually by a runner administrator under
  `RUNNER_TOOL_CACHE/u6-v3`. Workflows MUST NOT install, repair, uninstall, or quarantine
  editors. Validate with `-CiManagedOnly -RequireHealthyExisting` before acquiring the lock.

### The compute-unity-assemblies is-empty gate

- Each Unity invocation has its own compute id. Grouped correctness uses `compute`,
  `compute_playmode`, and `compute_standalone`; the last one is runtime-only.
  The matching Unity work step may skip an empty assembly selection, but lock acquisition remains
  unconditional because each static matrix is structurally non-empty and the
  analyzer must be able to prove every acquisition.
- `Verify tests actually ran` must require `steps.compute.outcome == 'success'` plus either
  `is-empty == 'true'` or a non-skipped Unity run step, and receives
  `expected-empty: ${{ steps.compute.outputs.is-empty }}`. Never gate verify on is-empty alone.

### Editor validation and manual maintenance (`scripts/unity/ensure-editor.ps1`)

- CI must pass `-CiManagedOnly -RequireHealthyExisting` plus `-ProvisioningProfile`
  explicitly. The grouped correctness job validates `StandaloneWindowsIl2Cpp` once because
  that superset serves all test modes and the endpoint-only shipping invocation. Other jobs use
  `EditorOnly` for editmode, playmode, benchmarks, and release checks, or
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
  `setup-dotnet@v6`, `setup-python@v6`, `upload-artifact@v7`, `download-artifact@v8`,
  `github-script@v9`, `create-github-app-token@v3`, `attest-build-provenance@v4`,
  `deploy-pages@v5`, `upload-pages-artifact@v5`. Verify a tag exists upstream
  (`git ls-remote --tags`) before calling a version invalid; bump all instances in one PR.

## References

| Document                                                                                    | Purpose                                                                                                                         |
| ------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------- |
| [github-actions-version-consistency.md](./references/github-actions-version-consistency.md) | Repo-wide action major pins, the audit and upstream-tag verification commands, and artifact action pairing                      |
| [unity-ci-matrix.md](./references/unity-ci-matrix.md)                                       | The editor-grouped matrix, build-lock and timeout invariants, is-empty gate, IL2CPP-only failure catalog, and log reading order |
| [unity-editor-cli-bootstrap.md](./references/unity-editor-cli-bootstrap.md)                 | ensure-editor.ps1 internals: PATH refresh, getter-based discovery, module desired state, and quarantine/reinstall repair        |
| [unity-runner-host-prereqs.md](./references/unity-runner-host-prereqs.md)                   | The four-layer Windows host prereq defense, both VC++ generations, and detection contracts                                      |
| [unity-version-single-source.md](./references/unity-version-single-source.md)               | The canonical unity-versions.json contract, the three consumer policies, and how to bump a version                              |
