---
title: "Unity Editor CLI Bootstrap"
id: "unity-editor-cli-bootstrap"
category: "unity"
version: "1.3.0"
created: "2026-05-20"
updated: "2026-05-24"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/unity/ensure-editor.ps1"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "cli"
  - "powershell"
  - "path"
  - "bootstrap"
  - "self-hosted"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding Windows registry PATH scopes plus the getter-vs-setter surface of a beta Unity CLI whose flags are not fully documented."

impact:
  performance:
    rating: "none"
    details: "One-time per-runner installer cost; does not affect steady-state run time"
  maintainability:
    rating: "high"
    details: "Centralizes session-PATH refresh, a best-effort invoker, and getter-based discovery so a beta CLI flag drift cannot break Unity bootstrap"
  testability:
    rating: "medium"
    details: "powershell-syntax.test.js reparses the script; unity-runner-script-contract.test.js pins the PATH and resilience tokens"

prerequisites:
  - "Familiarity with Windows PowerShell 5.1 and Set-StrictMode"
  - "Awareness of Windows Machine vs User registry PATH scopes"

dependencies:
  packages: []
  skills:
    - "unity-ci-matrix"

applies_to:
  languages:
    - "PowerShell"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Unity CLI PATH refresh"
  - "ensure-editor bootstrap"

related:
  - "unity-ci-matrix"
  - "headless-test-runner"
  - "unity-license-bootstrap"
  - "unity-runner-host-prereqs"

status: "stable"
---

<!-- trigger: unity, cli, path, bootstrap, ensure-editor, powershell, install-path | Standalone Unity CLI install + session PATH refresh + getter-based discovery on self-hosted runners | Core -->

# Unity Editor CLI Bootstrap

> **One-line summary**: `scripts/unity/ensure-editor.ps1` installs the standalone Unity CLI on a self-hosted Windows runner, refreshes the session `$env:PATH`, discovers the editor through layered CLI-aware resolution, and enforces a profile-scoped Unity editor desired state with quarantine/reinstall repair.

## Overview

The active Unity workflows run directly on self-hosted Windows runners and use the standalone Unity CLI (`unity`) to install editors and modules on demand. `scripts/unity/ensure-editor.ps1` is the bootstrap: if `unity` is not already on PATH it downloads and runs the official installer, then provisions the editor according to `-ProvisioningProfile`. CI must pass the profile explicitly. `EditorOnly` installs/verifies only the editor, `StandaloneWindowsIl2Cpp` adds only `windows-il2cpp`, `Android` adds Android player support plus SDK/NDK/OpenJDK disk proof, and `Full` preserves the historical broad module set for manual compatibility. The heavy/flaky Android SDK/NDK payload is scoped to `Android` or `Full`, because the NDK unpack can fail (~93%, exit 6) on Windows runners without long-path support. OpenJDK is NOT in any requested `-m` list -- it arrives as a dependency of `android-sdk-ndk-tools` and is only verified on disk afterward (see [CI module desired state and repair](#ci-module-desired-state-and-repair)).

Editor provisioning runs before the organization Unity license lock. The locked section should only activate/return the serial license and run tests; editor install/repair can take tens of minutes and must not block other licensed Unity jobs.

The standalone CLI is a moving beta surface (`v0.1.0-beta.x`). Some flags are undocumented and differ between releases, so the script treats every optional operation as best-effort and never lets an uncertain flag abort the install. Two failure modes drove the current design: the installer leaves `unity` off the current session PATH, and `unity install-path` was once called with a positional directory argument, which the CLI rejected.

The script is parsed cross-platform: `scripts/__tests__/powershell-syntax.test.js` reparses the `.ps1` on Linux `pwsh`, so it must stay valid PowerShell 5.1 under `Set-StrictMode -Version Latest` even though the Windows-only registry calls never execute on Linux.

## Problem Statement

Two distinct problems, both fatal to a naive bootstrap:

1. **Session PATH lag.** The installer at `https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1` writes `%LOCALAPPDATA%\Unity\bin\unity.exe` and appends that directory to the User-scope registry PATH only. A registry write does not mutate the running process `$env:PATH`, so `Get-Command unity` immediately after install still fails even on a perfect install. The registry write can also lag behind the binary on disk.
1. **Getter-vs-setter confusion.** `unity install-path` with NO arguments is a GETTER that prints the current editor install directory. The original code passed a positional directory to it, producing the failure `too many arguments for 'install-path'. Expected 0 arguments but got 1`. The SET form uses a flag, not a positional argument.

Under `Set-StrictMode -Version Latest`, reading an uninitialized variable is a terminating error, so module-scope state the helpers depend on must be initialized up front.

## Solution

The script initializes `$script:UnityCliPath = 'unity'` after `Set-StrictMode`, refreshes the session PATH from the registry through a bounded retry loop, and discovers the editor through layered, defensive strategies.

### Standalone CLI surface: VERIFIED vs UNCERTAIN

VERIFIED (relied on directly by the script):

- `unity install <version>` -- positional version; `-m <id...>` adds one or more modules. CI requests only the module ids selected by `-ProvisioningProfile` (see [CI module desired state and repair](#ci-module-desired-state-and-repair)); `EditorOnly` sends no `-m` list. Any module install MUST also pass `--accept-eula`; the EULA-bearing Android SDK/NDK modules otherwise abort the whole install with "One or more modules require license acceptance. Pass --accept-eula ...". A single source-of-truth builder (`Get-UnityCliModuleInstallArguments`) constructs the install arg vector so `--accept-eula` cannot drift between call sites.
- `unity install-path` with NO arguments -- a 0-arg GETTER that prints the current editor install directory. Passing a positional directory is the root cause of the "Expected 0 arguments but got 1" failure.
- `unity editors -i` (with `--format json` for parsing; bare `-i` for the diagnostic dump).
- `unity install-modules -e <version> -m <id...>` (captured, then classified against disk); the install also carries `--accept-eula` (same EULA requirement as `install`). The `-l` flag lists installable module ids (best-effort sanity check only) and carries NO `--accept-eula`.

UNCERTAIN (treated best-effort; docs punt to `--help`):

- The install-path SET flag. The Hub CLI uses `-s <dir>`; the standalone CLI likely mirrors `-s`/`--set`. The script tries `-s` then `--set`, then continues.
- The `editors --format json` schema (field names are scanned, not assumed; a wrapper object such as `{"editors":[...]}` is flattened).
- The standalone CLI's exact output and whether every module id appears literally in `-l`. The `-l` listing is a best-effort sanity check; disk probes decide whether the desired module groups are present.
- `-c <changeset>` (only meaningful for non-release builds). The script never passes it, so it is NOT exercised here.

Sources: docs.unity.com/en-us/hub/unity-cli, docs.unity.com/en-us/hub/cli-overview.

### Refresh the session PATH from the registry

```powershell
function Update-SessionPathFromRegistry {
    $segments = New-Object System.Collections.Generic.List[string]

    # Guard the INPUT: Join-Path throws on a null $env:LOCALAPPDATA.
    if ($env:LOCALAPPDATA) { $segments.Add((Join-Path $env:LOCALAPPDATA 'Unity\bin')) }

    $machinePath = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
    if ($machinePath) { $segments.Add($machinePath) }

    $userPath = [System.Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath) { $segments.Add($userPath) }

    # Preserve process-only PATH entries (e.g. node from setup-node via
    # $GITHUB_PATH) by appending the existing $env:PATH as the FINAL segment.
    if ($env:PATH) { $segments.Add($env:PATH) }

    $env:PATH = (($segments | Where-Object { $_ -and $_.Trim().Length -gt 0 }) -join ';')
}
```

Read BOTH Machine and User registry scopes, null-guard each, prepend the installer's known `%LOCALAPPDATA%\Unity\bin` target in case the registry write lags, and -- because this `.ps1` shares the caller's process environment -- append the existing `$env:PATH` LAST so process-only entries (node added by `setup-node` via `$GITHUB_PATH`) survive instead of being clobbered. `Ensure-UnityCli` re-checks `Get-Command unity` about 3 times, refreshing PATH each pass with `Start-Sleep -Seconds 2` between tries; on exhaustion it falls back to the absolute `%LOCALAPPDATA%\Unity\bin\unity.exe`, and only a genuinely missing binary throws the original message verbatim (so historical log greps keep matching).

### Three invokers with distinct contracts

- `Invoke-UnityCliSafe` -- NON-throwing best-effort. Returns `$true`/`$false`, merges stderr into stdout (`2>&1`) so a beta CLI writing usage to stderr cannot trip `$ErrorActionPreference = 'Stop'`. Used for optional effects such as setting the install path.
- `Get-UnityCliOutput` -- CAPTURING, non-throwing. Returns stdout lines or `$null`. It deliberately keeps getter output OFF this script's success stream because the caller (`run-ci-tests.ps1`) reads our LAST stdout line as the resolved editor path via `Select-Object -Last 1`; getter chatter must never leak there.
- `Invoke-UnityCliCapture` -- CAPTURING, non-throwing, and live-streaming. Returns success, exit code, and output lines so editor installs and module installs can be classified against disk proof before deciding whether to repair or fail.

### Best-effort SET, getter-based authoritative discovery

`Set-UnityCliInstallPath` calls `Invoke-UnityCliSafe -Arguments @('install-path', '-s', $Root)`, then `--set` on failure, then emits a `::notice::` (NOT an error) and continues. Setting the path is an optimization for local discovery, but CI-managed mutation is stricter: before any `install`, `install-modules`, or `uninstall`, `Confirm-UnityCliManagedInstallRoot` verifies the getter-reported CLI install root is inside the configured managed root and fails before mutation if it is not.

`Get-UnityCliInstallRoot` runs the 0-arg GETTER `Get-UnityCliOutput -Arguments @('install-path')` and takes the last path-like line (validated by `Test-LooksLikeAbsolutePath`, which accepts only `C:\...` or `\\...`). This reports the CLI's REAL install location whether or not the SET succeeded, so discovery never depends on the uncertain set flag.

`Resolve-InstalledEditor` layers: (a) probe under the getter-reported root, (b) the candidate-path search under the configured `$InstallRoot`, (c) a defensive parse of `unity editors -i --format json`. In CI-managed mode, any discovered editor outside the configured install root is ignored so a manual `ProgramFiles` install cannot defeat repair. Version-scoped Unity CLI mutations are also blocked unless the CLI getter root is inside `$InstallRoot`, because the CLI's version-only commands otherwise target its current install root. `Resolve-EditorFromCliJson` wraps `ConvertFrom-Json` in try/catch -- malformed or banner-prefixed beta output returns `$null` instead of throwing -- and scans candidate version fields and path fields rather than assuming a schema.

### CI module desired state and repair

A single source of truth, `Get-UnityCiModuleSpec`, defines every module group once as ordered rows of `Id` / `Requested` / `Verified` / `Tier` / `Profiles`. Everything else DERIVES from the selected profile's rows -- the requested `-m` ids (`Get-UnityCiModuleIds` = rows where `Requested`), the verified-on-disk groups (`Get-UnityCiVerifiedModuleGroups` = rows where `Verified`), skipped module groups (`Get-UnityCiSkippedModuleGroups`), per-tier id subsets (`Get-UnityCiModuleIdsForTier`), and the per-id tier lookup (`Get-UnityCiModuleTier`) -- so the lists can never silently drift from one another (the historical bug class). The `core` tier contains desktop/web/Linux build-support modules; the `android` tier is the heavy/flaky multi-GB download installed in isolation when selected.

The bootstrap keeps two DELIBERATELY DECOUPLED lists, because "what we ASK the CLI to install" and "what we PROVE landed on disk" are different questions:

**Requested module ids passed to `-m`** (`Get-UnityCiModuleIds`, derived from the selected profile) -- the ids handed to `unity install -m`/`install-modules -m`:

- `EditorOnly`: none.
- `StandaloneWindowsIl2Cpp`: `windows-il2cpp`.
- `Android`: `android`, `android-sdk-ndk-tools`.
- `Full`: `windows-il2cpp`, `webgl`, `android`, `android-sdk-ndk-tools`, `linux-mono`, `linux-il2cpp`.

`android-open-jdk` is intentionally ABSENT from the requested list. The standalone beta CLI rejects the bare id (`Couldn't find module "android-open-jdk". Did you mean: android-open-jdk-11.0.14.1+1`) because its real id is version-pinned and that suffix drifts across Unity versions; hardcoding it would re-break on the next bump. OpenJDK instead arrives automatically as a DEPENDENCY of `android-sdk-ndk-tools`, so requesting that group brings it along.

**Module groups verified on disk** (`Get-UnityCiVerifiedModuleGroups`, iterated by `Get-MissingUnityCiModuleGroups`) -- the selected profile's on-disk truth required after any install/repair:

- `EditorOnly`: none.
- `StandaloneWindowsIl2Cpp`: `windows-il2cpp`.
- `Android`: `android`, `android-sdk-ndk-tools`, `android-open-jdk`.
- `Full`: `windows-il2cpp`, `webgl`, `android`, `android-sdk-ndk-tools`, `android-open-jdk`, `linux-mono`, `linux-il2cpp`.

`Ensure-UnityCiModules` treats `install-modules` output as diagnostic, not authoritative. It accepts a non-zero CLI result only when disk probes prove all required module groups are present:

- Windows standalone IL2CPP: concrete player leaves such as `WindowsPlayer.exe`, `UnityPlayer.dll`, or `GameAssembly.dll` under known `win64_player_*_il2cpp` variations.
- WebGL: `UnityEditor.WebGL.Extensions.dll` plus concrete Emscripten toolchain proof such as `BuildTools\Emscripten\emscripten\emcc.py` or `BuildTools\Emscripten\emscripten\emscripten-version.txt` (the canonical nested path Unity documents).
- Android: concrete Android player leaves such as `UnityEditor.Android.Extensions.dll`.
- Android SDK/NDK tools: `SDK\platform-tools\adb(.exe)`, `NDK\source.properties`, and an LLVM `clang++(.exe)` leaf under `NDK\toolchains\llvm\prebuilt`.
- Android OpenJDK: `OpenJDK\bin\java(.exe)`.
- Linux Mono support: concrete player leaves such as `LinuxPlayer` or `UnityPlayer.so` under known `linux64_player_*_mono` variations.
- Linux IL2CPP support: concrete player leaves such as `LinuxPlayer` or `UnityPlayer.so` under known `linux64_player_*_il2cpp` variations.

`Ensure-UnityCiModules` is PROFILE- AND TIER-AWARE: `EditorOnly` skips module work entirely; other profiles verify only their selected groups, partition missing groups by tier, and handle each with the right strategy. A missing selected `core` group is serious and uses the heavy quarantine/reinstall repair below. A missing selected `android` group is expected flakiness and is handled first by the dedicated, bounded `Install-UnityAndroidModules` step.

`Install-UnityAndroidModules` retries the Android `install-modules` up to `DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS` times (default 3), clearing only the partial `AndroidPlayer\NDK` / `SDK` payload (`Clear-PartialAndroidModulePayload`, scoped strictly inside the editor dir) between attempts so a half-written NDK tree cannot poison the next try. Disk is the source of truth, so an exit 6 with the Android groups present on disk is treated as success. On exhaustion it emits `Write-UnityModuleInstallPostMortem` (per-group present/MISSING, NDK/SDK file counts, the deepest NDK absolute path length, and the Windows long-path state via `Test-WindowsLongPathSupport`; it raises a MAX_PATH `::warning::` when the deepest path is >= 240 chars and long paths are disabled -- the prime suspect for the NDK unpack failure, remediated per `docs/runbooks/unity-runners-after-transfer.md`) and then escalates to managed quarantine/reinstall with the selected profile unless repair is disabled.

For a missing selected `core` group, repair is enabled by default. The script first tries `unity uninstall <version>` as a cleanup hint. It then quarantines any remaining managed version directory to `<install-root>\_quarantine\<version>-<timestamp>-<id>` and runs a fresh `unity install <version>` with the selected profile's requested module ids (the same single-source-of-truth arg vector as the primary install). Repair install retries once when the CLI still reports "already installed" without a resolvable managed editor, clearing stale CLI metadata with another uninstall before retrying. Repair is deliberately bounded to the configured install root; the script refuses to move arbitrary `ProgramFiles` installs. In CI-managed mode, repair resolution also keeps `-ManagedOnly` so a host install cannot be selected after reinstall. The same uninstall-plus-version-directory quarantine path handles partial installs where the CLI reports "already installed" but no `Unity.exe` leaf exists, so stale CLI metadata cannot keep returning the same no-op state.

Set `DXM_UNITY_DISABLE_EDITOR_REPAIR=1` only when debugging the installer itself. Normal CI should keep repair enabled so manually copied, partial, or non-Hub/non-CLI-managed editors converge to a known-good CLI-managed install.

After module validation, `ensure-editor.ps1` runs a native startup probe before the license lock. If startup fails, the script performs one managed reinstall AND re-runs `Ensure-UnityCiModules` with the selected profile, then probes again. A second startup failure is classified as host OS/runtime prerequisite damage (for example missing native DLLs such as `0xC0000135` / `STATUS_DLL_NOT_FOUND`), not a package/test failure.

The `0xC0000135` failure mode has its own short-circuit: both probe sites emit a wrap-immune single-line `::error::` annotation BEFORE the throw and refuse to loop on a futile Unity reinstall (the missing DLL is on the OS, not in the Unity install). The host-OS prereqs themselves are remediated out-of-band by [Unity Runner Host Prerequisites](./unity-runner-host-prereqs.md) -- a per-job preflight composite (`.github/actions/assert-unity-host-prereqs`) auto-installs the Microsoft Visual C++ 2015-2022 Redistributable, Windows long-path support, Defender exclusions, and PowerShell 7, exporting `DXM_RUNNER_PREREQ_INSTALLED=1` on success so the short-circuit annotation can adjust the suggested cause when the preflight has already run.

## Verification

- `scripts/__tests__/powershell-syntax.test.js` reparses the edited `.ps1`; keep it valid PowerShell 5.1.
- `scripts/__tests__/unity-runner-script-contract.test.js` pins both the PATH fix (`$script:UnityCliPath`, `GetEnvironmentVariable`, `Update-SessionPathFromRegistry`, the absolute-path fallback) and the resilience design: `install-path` SET uses `-s` (never a positional `$Root`), `Invoke-UnityCliSafe` exists, `Get-UnityCliInstallRoot` reads `@('install-path')`, the defensive `--format json` / `ConvertFrom-Json` discovery, the requested/verified CI module lists, and quarantine/reinstall repair.
- `scripts/__tests__/unity-ensure-editor-production-contract.test.js` pins the `--accept-eula` contract: the single source-of-truth `Get-UnityCliModuleInstallArguments` is the ONLY place that builds a module-install (`install`/`install-modules` ... `-m` ...) arg vector, every live install call site routes through it, and the requested `-m` ids exclude `android-open-jdk` while the verified groups include it.
- `scripts/__tests__/unity-ensure-editor-il2cpp-idempotency.test.js` covers disk-proof idempotency, managed-install quarantine/reinstall, and the `DXM_UNITY_DISABLE_EDITOR_REPAIR=1` refusal path.
- `scripts/__tests__/unity-workflow-shape.test.js` requires the active Unity workflows to provision editors before acquiring the organization Unity lock and to export `UNITY_EDITOR_PATH` for the locked test step.

## See Also

- [Unity Runner Host Prerequisites](./unity-runner-host-prereqs.md)
- [Unity CI Matrix](./unity-ci-matrix.md)
- [Headless Test Runner](./headless-test-runner.md)
- [Unity License Bootstrap](./unity-license-bootstrap.md)

## References

- Unity CLI docs: https://docs.unity.com/en-us/hub/unity-cli
- Unity Add Modules docs: https://docs.unity.com/en-us/hub/add-modules
- Unity CLI overview: https://docs.unity.com/en-us/hub/cli-overview
- Standalone CLI installer: https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1
- Bootstrap script: `scripts/unity/ensure-editor.ps1`
