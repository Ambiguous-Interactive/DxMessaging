---
title: "Unity Runner Host Prerequisites"
id: "unity-runner-host-prereqs"
category: "unity"
version: "1.0.0"
created: "2026-05-26"
updated: "2026-05-26"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: "scripts/unity/bootstrap-windows-runner.ps1"
    - path: ".github/actions/assert-unity-host-prereqs/action.yml"
    - path: ".github/workflows/runner-bootstrap.yml"
    - path: "scripts/unity/ensure-editor.ps1"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "windows"
  - "prerequisites"
  - "vcredist"
  - "vcredist-2010"
  - "vcredist-2015-2022"
  - "msvcp100"
  - "bootstrap"
  - "self-hosted"
  - "0xC0000135"

complexity:
  level: "intermediate"
  reasoning: "Requires Windows loader, HKLM detection, pwsh bootstrap, and Unity repair-boundary knowledge."

impact:
  performance:
    rating: "none"
    details: "One-time runner setup; fast-fails Unity reinstall loops when the fault is host-level."
  maintainability:
    rating: "high"
    details: "Keeps host prereq install, CI preflight, and Unity startup diagnostics tied to one script contract."
  testability:
    rating: "high"
    details: "Hermetic env-var seams and contract tests pin detection, workflow targeting, and short-circuit behavior."

prerequisites:
  - "Familiarity with Windows PowerShell and HKLM-scoped install detection"
  - "Awareness of Authenticode verification for downloaded installers"
  - "Understanding that a fresh runner may not have PowerShell 7 yet"

dependencies:
  packages: []
  skills:
    - "unity-editor-cli-bootstrap"

applies_to:
  languages:
    - "PowerShell"
  frameworks:
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Windows runner bootstrap"
  - "0xC0000135 runbook"
  - "STATUS_DLL_NOT_FOUND fix"
  - "VC++ redistributable bootstrap"
  - "VC++ 2010 redistributable bootstrap"
  - "MSVCP100 fix"

related:
  - "unity-editor-cli-bootstrap"
  - "unity-ci-matrix"
  - "unity-license-bootstrap"

status: "stable"
---

<!-- trigger: windows, vcredist, vcruntime, msvcp140, msvcp100, msvcr100, vcredist-2010, 0xC0000135, STATUS_DLL_NOT_FOUND, bootstrap, host-prereqs, longpaths, defender, winget, pwsh | Self-hosted Windows runner host-OS prerequisites for Unity Editor startup | Core -->

# Unity Runner Host Prerequisites

> **One-line summary**: Use a four-layer defense for self-hosted Windows
> Unity runners: bootstrap host prerequisites (BOTH VC++ 2010 and 2015-2022
> generations), preflight every Unity job, short-circuit host-level startup
> faults, and keep an Actions recovery path.

## Overview

`scripts/unity/bootstrap-windows-runner.ps1` is the canonical Windows-host
prereq installer for Unity CI. It detects and installs BOTH the Microsoft
Visual C++ 2010 SP1 x64 Redistributable (ships `MSVCP100.dll`/`MSVCR100.dll`
-- load-bearing missing DLL identified in production run 70874414898) AND
the Microsoft Visual C++ 2015-2022 x64 Redistributable (ships
`VCRUNTIME140.dll`/`MSVCP140.dll`), enables Windows long paths, adds
guarded Windows Defender exclusions for the Unity install root and runner
workspace, installs PowerShell 7 (`pwsh`) through `winget`, and audits UCRT
on downlevel Windows. It is idempotent and supports `-DetectOnly`.

> **Unity 2021.3 / 2022.3 / 6000.x ALL depend on BOTH VC++ generations.**
> The 2010 runtime (`MSVCP100.dll` / `MSVCR100.dll`) is required IN ADDITION
> to VC++ 2015-2022 (`VCRUNTIME140.dll` / `MSVCP140.dll`). The two are
> SEPARATE Microsoft packages -- installing one does NOT install the other.
> ([Unity Discussions reference](https://discussions.unity.com/t/what-c-redistributable-does-unity3d-editor-require/244474))

Three entry points consume it:

1. Operators run it locally on the runner host when they have access.
1. `.github/workflows/runner-bootstrap.yml` exposes a `workflow_dispatch`
   recovery path for operators who only have Actions access. It hard-fails
   wrong-target dispatch instead of bootstrapping an unintended runner.
1. `.github/actions/assert-unity-host-prereqs/` runs at the start of every
   Unity job. On success it exports `DXM_RUNNER_PREREQ_INSTALLED=1`.

`scripts/unity/ensure-editor.ps1` keeps the repair boundary honest. If the
native Unity startup probe returns `0xC0000135 / STATUS_DLL_NOT_FOUND`, it
emits a single-line `::error::` annotation and fails before a managed Unity
quarantine/reinstall. Missing loader DLLs are host damage, not editor damage.

## Problem Statement

Freshly imaged self-hosted Windows runners can miss DLLs that GitHub-hosted
Windows runners already include. Unity then fails during `Provision Unity
Editor` with `-1073741515 (0xC0000135 / STATUS_DLL_NOT_FOUND)`. The two
common cases are:

- Missing `MSVCP100.dll` / `MSVCR100.dll` (VC++ 2010 SP1 generation -- the
  load-bearing miss identified in production run 70874414898).
- Missing `VCRUNTIME140.dll` / `VCRUNTIME140_1.dll` / `MSVCP140.dll` (VC++
  2015-2022 generation -- the original DAD-MACHINE failure).

The two are SEPARATE Microsoft redistributable packages -- installing one
does NOT install the other. The bootstrap installs both in one pass.

Before this defense, `ensure-editor.ps1` treated that startup failure as a
Unity-install fault. It quarantined and reinstalled the editor, failed the same
startup probe again, and made each matrix cell pay minutes of unrecoverable
work. The fix belongs at the host-OS prereq layer and must be reachable without
manual host access whenever possible.

## Solution

Keep four layers in sync:

1. **One-shot installer**:
   `scripts/unity/bootstrap-windows-runner.ps1` detects every host prereq,
   repairs missing supported prereqs by default, and supports `-DetectOnly`.
1. **Per-job preflight**:
   `.github/actions/assert-unity-host-prereqs/action.yml` invokes the same
   script before Unity work and exports `DXM_RUNNER_PREREQ_INSTALLED=1`.
1. **Startup short-circuit**:
   `scripts/unity/ensure-editor.ps1` recognizes `0xC0000135`, prints
   context-aware guidance, and refuses futile Unity reinstall retries.
1. **Operator auto-recovery**:
   `.github/workflows/runner-bootstrap.yml` lets an Actions operator run the
   bootstrap from the UI when direct runner access is unavailable.

The partition matters. The bootstrap script owns installation and detection;
the composite makes it every-job hygiene; `ensure-editor.ps1` prevents a
host-level failure from masquerading as editor corruption; the workflow gives
operators a no-host-access recovery path.

## Prereqs Managed

- **Microsoft Visual C++ 2010 SP1 x64 Redistributable** (version
  `10.0.40219.325`): primary remediation for `0xC0000135` when the missing
  DLL is `MSVCP100.dll` or `MSVCR100.dll` (identified in production run
  70874414898). The script downloads
  `https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe`
  (no aka.ms shortcut exists for VS 2010) and verifies the Authenticode
  signature before launch. Uses silent-install switches `/q /norestart`
  (DIFFERENT from the modern generation's `/install /quiet /norestart`).
- **Microsoft Visual C++ 2015-2022 x64 Redistributable**: primary remediation
  for `0xC0000135` when the missing DLL is `VCRUNTIME140*.dll` or
  `MSVCP140.dll`; installs the modern 14.x VC++ runtime DLLs Unity imports.
  The script downloads `https://aka.ms/vc14/vc_redist.x64.exe` and verifies
  the Authenticode signature before launch.
- **Windows long paths**: writes
  `HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem!LongPathsEnabled = 1` to
  prevent deterministic long-path unpack failures.
- **Windows Defender exclusions**: adds allow-listed exclusions for
  `C:\Unity\Editors` and the active runner workspace; skips cleanly when
  Defender is unavailable.
- **PowerShell 7 (`pwsh`)**: installs through `winget install --id
Microsoft.PowerShell --scope user`, so Administrator is not required for
  this prereq.
- **UCRT sanity check**: modern Windows ships UCRT in-box; downlevel Windows
  emits an actionable KB2999226 error instead of attempting a host-specific MSU
  install.

When running non-admin, HKLM-backed repairs such as both VC++ generations
and long paths can fail with Access Denied. The script reports that
directly and points to the elevated local-host path or the Actions recovery
workflow. It does not use `Start-Process -Verb RunAs`; UAC prompts would
hang non-interactive CI.

## Detection Contracts

Treat file-on-disk probes as authoritative when they reflect loader behavior:

- **VC++ 2010** detection first checks `C:\Windows\System32` for
  `MSVCP100.dll` and `MSVCR100.dll` (BOTH must exist). Only after file
  presence passes does the script consult the registry: either
  `HKLM:\SOFTWARE\Microsoft\VisualStudio\10.0\VC\VCRedist\x64!Installed = 1`
  OR the `Wow6432Node` mirror (the 2010 installer can register in either
  view depending on host configuration -- unlike the modern generation,
  which writes exclusively to the native view).
- **VC++ 2015-2022** detection first checks `C:\Windows\System32` for
  `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`, and `MSVCP140.dll`. All three
  must exist. Only after file presence passes does the script consult the
  native 64-bit `HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64`
  view for `Installed = 1` and `Bld >= 26020`.
- Long-path support is enabled only when
  `HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem!LongPathsEnabled` is `1`.
- Defender exclusions are verified with `Get-MpPreference -ExclusionPath`.
- PowerShell 7 uses `Get-Command pwsh -ErrorAction SilentlyContinue`, matching
  the command later `shell: pwsh` steps need on PATH.
- Downlevel UCRT uses `Get-HotFix KB2999226`; Windows 10+ and Server 2019+
  treat UCRT as in-box.

## Environment Contracts

- `DXM_RUNNER_DISABLE_AUTO_BOOTSTRAP=1` forces `-DetectOnly` in the composite
  and workflow, regardless of workflow input.
- `DXM_RUNNER_PREREQ_INSTALLED=1` is exported by the composite after a
  successful preflight. `ensure-editor.ps1` reads it to distinguish "preflight
  never ran" from "preflight passed; investigate a different missing DLL."
- `DXM_UNITY_FAKE_IMPORTS` and `DXM_UNITY_FAKE_LONGPATHS_ENABLED` are
  test-only seams. Production must not set them.

Mode precedence is: non-Windows host skips; env override forces detect-only;
non-truthy workflow/composite input chooses detect-only; truthy input allows
auto-install.

## Verification

Keep these tests aligned with any change to the bootstrap or short-circuit:

- `scripts/__tests__/unity-runner-host-prereq-contract.test.js`
- `scripts/__tests__/unity-ensure-editor-production-contract.test.js`
- `scripts/__tests__/unity-runner-script-contract.test.js`
- `scripts/__tests__/powershell-syntax.test.js`
- `scripts/__tests__/hermetic-host-env-policy.test.js`

Mutation-test the guards, not only the green path. A fresh runner missing VC++
must still surface through file-on-disk probes and must not fall through to a
managed Unity reinstall.

## See Also

- [Unity Editor CLI Bootstrap](./unity-editor-cli-bootstrap.md)
- [Unity CI Matrix](./unity-ci-matrix.md)
- [Unity License Bootstrap](./unity-license-bootstrap.md)
- [Unity runner transfer runbook](../../../docs/runbooks/unity-runners-after-transfer.md)
- Microsoft Visual C++ Redistributable downloads:
  <https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist>
- Windows long-path support:
  <https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation>
