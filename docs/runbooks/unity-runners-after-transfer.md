# Unity Runners After Repository Transfer Runbook

This runbook explains how to restore self-hosted Unity runner access after a repository is transferred between GitHub organizations. Keep execution notes local. Do not paste secrets, screenshots of organization settings, or other private account metadata into this file or any tracked follow-up.

## Symptom

- A queued Unity workflow run (for example `Unity Tests`, `Unity IL2CPP`, `Unity Benchmarks`, or the `unity-checks` job in `Release`) stays queued indefinitely.
- The GitHub Actions UI shows the job waiting for a runner. There is no error, no warning, and the run never starts.
- The organization's self-hosted runners report Online and Idle in the GitHub UI, with labels that exactly match the workflow's `runs-on` request (`self-hosted`, `Windows`, `RAM-64GB`).
- The watchdog defined in `.github/workflows/stuck-job-watchdog.yml` does not recover the run because no idle runner is visible to the repository, which means the watchdog's matching rule does not fire.

## Root cause

After a repository transfer between GitHub organizations, the destination organization's runner groups do not automatically include the transferred repository in their repository-access list. When a runner group is configured as "Selected repositories", any repository that is not explicitly listed cannot dispatch jobs to that group's runners. The dispatcher does not log an error in this state; the job simply stays queued.

This is a configuration-state issue, not the intermittent dispatcher bug tracked upstream as [GitHub Community Discussion #186811](https://github.com/orgs/community/discussions/186811). The dispatcher bug applies when an idle matching runner is visible to the repository through the GitHub API but never receives the job. If the API does not list the runner at all for the repository, this runbook applies instead.

## Diagnose with the GitHub CLI

Run the following commands from any workstation with `gh auth login` already completed.

List the organization's runner groups, including each group's visibility setting:

```bash
gh api orgs/Ambiguous-Interactive/actions/runner-groups \
  -q '.runner_groups[] | {id, name, visibility, allows_public_repositories}'
```

For a runner group whose visibility is `selected`, list the repositories that currently have access:

```bash
gh api orgs/Ambiguous-Interactive/actions/runner-groups/<group-id>/repositories \
  -q '.repositories[] | {id, name, full_name}'
```

If the transferred repository name does not appear in that list, the dispatcher has no path to the group's runners from this repository, which matches the symptom above.

Cross-check by listing runners that the repository itself can see:

```bash
gh api repos/Ambiguous-Interactive/DxMessaging/actions/runners \
  -q '.runners[] | {id, name, status, busy, labels: [.labels[].name]}'
```

When this list is empty or omits the expected runner names while the organization-level inventory shows them online, the access list is the cause.

## Resolution

Choose one of the following resolutions inside the destination organization. Either resolution restores dispatch; pick the one that matches the organization's security model.

Add the transferred repository to the selected list:

1. Organization Settings.
1. Actions.
1. Runner groups.
1. Default.
1. Repository access.
1. Add the transferred repository to the list.
1. Save.

Change the group's visibility to all repositories:

1. Organization Settings.
1. Actions.
1. Runner groups.
1. Default.
1. Repository access.
1. Set visibility to all repositories.
1. Save.

The second resolution avoids future per-transfer maintenance but exposes the runners to every repository in the organization. Use it only when that exposure is acceptable for the runner group's security posture.

After applying the chosen resolution, re-run the queued workflow from the Actions tab. The preflight job added to each Unity workflow validates runner access from `ubuntu-latest` before any matrix entry attempts to dispatch onto self-hosted; a green preflight confirms the fix.

## Preflight diagnostic in this repository

Unity workflows in this repository run a `runner-preflight` job on `ubuntu-latest` before the self-hosted matrix. That preflight queries `gh api orgs/${OWNER}/actions/runners` first and, on 403/404 (the default `secrets.GITHUB_TOKEN` cannot list org-scoped runners under most org policies), falls back to `gh api repos/${GITHUB_REPOSITORY}/actions/runners`. If both endpoints fail (typically a 403 from each because the token is unscoped for runner administration), the preflight emits a `::warning::` and exits 0 (soft pass). The preflight must NEVER be more strict than the no-preflight baseline; its only job is to surface a fast, clear failure when it can prove the runner inventory is wrong.

### Upgrading the soft pass to a hard pass

The default `secrets.GITHUB_TOKEN` cannot list runners under a repo-level scope strict enough to reflect the runner-group ACL, so the preflight falls back to a soft pass on most installations. To upgrade the soft-pass path to a hard-pass:

1. Mint a fine-grained personal access token (or a GitHub App installation token) holding the repository-level "Administration: read" permission, scoped to `Ambiguous-Interactive/DxMessaging` only. Do NOT use a classic PAT with `admin:org`, and do NOT use the fine-grained "Organization administration: read" permission: both grant org-wide visibility, which causes `gh api orgs/<org>/actions/runners` to return the entire org runner inventory regardless of any individual repository's runner-group ACL. That would let the preflight see runners as online and silently pass even when the post-transfer ACL is broken, which is exactly the pitfall this runbook addresses.
1. Add the token as a repository secret named `RUNNER_AUDIT_PAT`.
1. Wire the workflow to prefer `RUNNER_AUDIT_PAT` over `GITHUB_TOKEN` when set, and to query the repo-scoped endpoint `repos/<owner>/<repo>/actions/runners`. That endpoint enforces the runner-group ACL: if the repository does not have access to a runner via its group, the runner is invisible there, which is the live ACL state we want the preflight to detect. The preflight retains the same soft-pass behavior if the secret is absent, so this is opt-in.

The rationale is deliberate: we want the upgrade token to FAIL when the ACL is misconfigured, not paper over it; that is why we use the repo-scoped "Administration: read" permission rather than any org admin scope. Without that property the hard-pass mode would be worse than the soft-pass mode it replaces.

This is intentionally documented but NOT enabled by default: the soft pass is the correct conservative behavior given the threat model. Operators see a `::warning::` annotation rather than a green check, and the existing watchdog + manual unstick workflows continue to recover any actually-stuck job.

Because `administration` is not a valid `permissions:` key for the workflow-scoped `GITHUB_TOKEN`, the only way to grant the preflight read access to the runner inventory under a repo-level scope is to provision an external token (PAT or app installation token) via `RUNNER_AUDIT_PAT` (see above). Without that, the preflight falls back to the soft-pass path, which is the design intent.

### Follow-up: composite action factoring

The preflight shell currently lives inline in three workflows (`unity-tests.yml`, `unity-benchmarks.yml`, `release.yml`). A composite action under `.github/actions/runner-access-preflight/` would deduplicate the block. Out of scope for the current change; track here so the next maintainer can find it.

If the preflight passes but the matrix job still stays queued, the cause is more likely the dispatcher bug (see [GitHub Community Discussion #186811](https://github.com/orgs/community/discussions/186811)) than the access list. Use the recovery workflows in this repository: [unstick-run.yml](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/unstick-run.yml) for manual recovery and [stuck-job-watchdog.yml](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/stuck-job-watchdog.yml) for the automated path.

## PowerShell 7 prerequisite on self-hosted runners

Self-hosted Windows Unity runners require **PowerShell 7 (`pwsh`)** in
addition to Git Bash. Every Unity workflow consumes the
`print-self-hosted-runner-diagnostics` composite action
(`.github/actions/print-self-hosted-runner-diagnostics/action.yml`) before its
own steps, and that action plus several Unity build steps run with
`shell: pwsh`. PowerShell 7 is _not_ the Windows-built-in PowerShell 5.1
(`powershell`); it is a separate install that provides the `pwsh` executable.

### Symptom

- A self-hosted Unity job fails almost immediately with
  `##[error]pwsh: command not found`.
- The failure originates from the first `shell: pwsh` step the agent reaches.
- Git Bash and the runner agent are otherwise healthy.

The diagnostics composite action now fails fast with a clear, actionable
error annotation (`pwsh missing on self-hosted runner`) when `pwsh` is absent,
so this state no longer surfaces only as the cryptic
`pwsh: command not found`. The preflight step that emits that error runs under
Windows PowerShell 5.1, which is always present, so it executes even when
PowerShell 7 is missing.

### Install PowerShell 7

On a machine with winget:

```powershell
winget install --id Microsoft.PowerShell --source winget
```

For machines without winget, download and run the latest MSI installer from
the official releases page:
<https://github.com/PowerShell/PowerShell/releases>.

### Verify

Open a **new** shell (so the updated PATH is picked up) and confirm:

```powershell
pwsh -v
Get-Command pwsh
```

`pwsh -v` should print the installed PowerShell 7 version, and
`Get-Command pwsh` should resolve to the installed executable's path.

### Restart the runner agent

After installing PowerShell 7, restart the self-hosted runner service/agent
(or refresh the machine's PATH and restart the runner) so the agent process
sees `pwsh` on its PATH. The runner agent inherits its environment at start
time; until it is restarted it will keep reporting `pwsh: command not found`
even though a fresh interactive shell can find `pwsh`. Re-run the queued
Unity workflow once the agent is back online.

## Git compression tools for Actions cache

Self-hosted Windows Unity runners also need Git for Windows' Unix tools
available to GitHub Actions cache steps. `actions/cache` restores and saves
archives through `tar` and `gzip`; when the runner PATH exposes Git Bash but
omits `C:\Program Files\Git\usr\bin`, cache post steps can warn with
`gzip: command not found` and fail to save the Unity Library cache.

The `print-self-hosted-runner-diagnostics` composite action now prepends Git's
`usr\bin` directory to `$GITHUB_PATH` when it finds both `gzip.exe` and
`tar.exe`, and emits a warning when that directory is absent. This makes the
current job reliable and gives the operator an actionable host configuration
signal for future runs.

To verify locally on the runner:

```powershell
Get-Command gzip.exe
Get-Command tar.exe
```

If either command is missing, install Git for Windows or add
`C:\Program Files\Git\usr\bin` to the runner service PATH, then restart the
runner agent so the updated environment is inherited.

## Windows host prerequisites (0xC0000135 / STATUS_DLL_NOT_FOUND)

Unity Editor cannot launch on a self-hosted Windows runner unless the host has
a small set of OS-level prerequisites installed. GitHub-hosted `windows-2022`
images ship with these preinstalled; freshly imaged self-hosted runners
generally do not. This section is the operator-actionable fix for that gap.

The repo ships a one-shot bootstrap script
([`scripts/unity/bootstrap-windows-runner.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/bootstrap-windows-runner.ps1))
and a `workflow_dispatch`-only auto-recovery workflow
([`.github/workflows/runner-bootstrap.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/runner-bootstrap.yml))
plus a per-job preflight composite action
([`.github/actions/assert-unity-host-prereqs/action.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/actions/assert-unity-host-prereqs/action.yml))
that wraps the script. Together they form a four-layer defense: a one-shot
host installer, a per-job preflight that runs the same installer in
detect-or-install mode, an `ensure-editor.ps1` short-circuit that fails fast
when Unity itself reports `0xC0000135` instead of looping on a futile editor
reinstall, and the operator-facing workflow that recovers the host without
RDP/SSH access. See
[`.llm/skills/unity/unity-runner-host-prereqs.md`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/skills/unity/unity-runner-host-prereqs.md)
for the LLM/AI-agent reference.

### Symptom

- A Unity job on a self-hosted Windows runner fails very early (typically
  within the first ~6 minutes of `Provision Unity Editor`) with
  `Unity startup provisioning probe exit code: -1073741515 (0xC0000135 / STATUS_DLL_NOT_FOUND)`.
- `ensure-editor.ps1`'s provisioning summary classifies the failure as
  "host OS/runtime prerequisite damage", not a package/test failure.
- The error annotation in the Actions log links here and to the bootstrap
  script.
- The same job re-run on a freshly imaged Windows runner reliably reproduces
  the failure; the same job on a runner that has had `bootstrap-windows-runner.ps1`
  applied passes.

### Root cause

The Windows OS loader cannot resolve a DLL that `Unity.exe` imports. There are
TWO independent Microsoft Visual C++ Redistributable packages Unity depends on
and BOTH must be installed on the host:

- **VC++ 2010 SP1 x64 Redistributable** (version `10.0.40219.325`) -- ships
  `MSVCP100.dll` and `MSVCR100.dll`. Identified in `production run 70874414898`
  as the load-bearing missing DLL on both self-hosted Windows runners.
  [Unity Discussions confirms](https://discussions.unity.com/t/what-c-redistributable-does-unity3d-editor-require/244474)
  that Unity 2021 / 2022 / 6000 ALL depend on this 2010-era runtime in
  addition to the modern one.
- **VC++ 2015-2022 x64 Redistributable** -- ships `VCRUNTIME140.dll`,
  `VCRUNTIME140_1.dll`, and `MSVCP140.dll`. The original failure cause
  identified on DAD-MACHINE.

The two are SEPARATE Microsoft packages -- installing one does NOT install
the other. GitHub-hosted `windows-2022` runners include both preinstalled;
self-hosted runners do not unless an operator has installed them.

Because the missing dependency is at the OS level, retrying the Unity install
cannot help; `ensure-editor.ps1` short-circuits as soon as it sees
`0xC0000135` from the startup probe so the job fails fast with an actionable
annotation rather than burning ~13 minutes per matrix cell on a futile editor
reinstall.

> **WHICH PATH TO USE.** Two operator paths follow.
>
> - **First-time fix (or any time the workflow does not yet live on the
>   default branch)**: jump to **Local recovery: bootstrap script on the
>   host** below. The script lives in the repo, so any local clone of any
>   branch works. No GitHub Actions involvement.
> - **Every subsequent regression after the bootstrap workflow is on the
>   default branch**: use **Auto-recovery: workflow_dispatch** below.
>   `workflow_dispatch` triggers only register from the default branch, so
>   the **Run workflow** button (and `gh workflow run`) only become
>   available after this PR (or any future PR carrying
>   `.github/workflows/runner-bootstrap.yml`) is merged.

`bootstrap-windows-runner.ps1` addresses three other foundational host
concerns in the same pass: Windows long-path support (the prerequisite that
unblocks the Android NDK 93% unpack failure described in the next section),
Windows Defender exclusions for the Unity install root and the runner
workspace, and PowerShell 7 (`pwsh`).

### Auto-recovery: workflow_dispatch (no host access required)

Use this path when you can read the Actions UI but cannot RDP/SSH to the
runner host. The workflow installs every prereq idempotently and uploads a
transcript artifact.

> **HARD PREREQUISITE: `runner-bootstrap.yml` must be on the default
> branch (`master`) before this path works at all.** GitHub Actions only
> registers `workflow_dispatch` triggers from workflow files that exist on
> the default branch; until this PR is merged, the **Run workflow** button
> does NOT appear in the Actions UI and `gh workflow run runner-bootstrap.yml`
> fails with `could not find any workflows named runner-bootstrap.yml`.
> Use the **Local recovery** path below for the FIRST-TIME runner repair
> (it has no merge dependency: the script lives in the repo and runs from
> any branch's checkout). Once merged, this Actions-UI path becomes the
> low-friction option for every subsequent regression.

1. (HARD-FAIL prerequisite) Take the OTHER runner offline first. Both
   self-hosted Windows runners share the labels `self-hosted, Windows,
RAM-64GB`, so the scheduler picks either machine; this workflow HARD-FAILS
   on wrong-target dispatch (exit 1, by design) to refuse silent bootstraps
   of an unintended machine. Offline the unwanted runner by opening
   **Settings -> Actions -> Runners**, clicking the runner, and selecting
   **Remove runner** (or stop the runner service on the host with
   `Stop-Service actions.runner.*`), then bring it back online after the
   bootstrap completes.
1. Open **Actions -> Runner Bootstrap (Windows) -> Run workflow**.
1. Pick `runner-label`: the name of the runner you want to bootstrap
   (`DAD-MACHINE` or `ELI-MACHINE`).
1. Pick `detect-only`: leave `false` (the default) to auto-install every
   missing prereq. Set to `true` to audit without mutating the host (the run
   exits 2 if anything is missing).
1. Click **Run workflow**.
1. Wait for the run to finish (~5-10 minutes on a healthy network) and
   confirm a green status. The run uploads a transcript artifact named
   `runner-bootstrap-<runner>-<run-id>-<attempt>`.
1. Re-run the failed Unity job. The next provisioning attempt should pass.

### Local recovery: bootstrap script on the host

Use this path when you can RDP/SSH/console into the runner host.

1. Sign in to the runner host (RDP, SSH, or local console).
1. Open **Windows PowerShell 5.1 OR PowerShell 7 as Administrator**.
   Administrator is required because the VC++ redistributable and the
   `LongPathsEnabled` registry write touch `HKLM`.
1. `cd` to any local clone of the repo (the actions-runner workspace works):

   ```powershell
   cd C:\path\to\actions-runner\_work\<repo>\<repo>
   ```

1. Run the bootstrap script:

   ```powershell
   .\scripts\unity\bootstrap-windows-runner.ps1
   ```

1. The script detects each prereq and installs only what is missing. It is
   idempotent: re-running it on a healthy host is a no-op and exits 0.
1. After the script reports success, re-run the failed Unity job from the
   Actions UI. No runner-agent restart is required for the redistributable;
   `LongPathsEnabled` and the `pwsh` install do require a fresh agent shell,
   which the next job naturally creates.

### What the bootstrap installs

The bootstrap script detects each prereq independently and remediates only
what is missing. One prereq's failure does not short-circuit the others; the
final exit code reflects the worst outcome across all of them.

- **Microsoft Visual C++ 2010 SP1 x64 Redistributable**, version
  `10.0.40219.325` (the load-bearing missing DLL identified in production
  run 70874414898). Installs `MSVCP100.dll` and `MSVCR100.dll`. Unity 2021.3
  / 2022.3 / 6000.x ALL depend on this 2010-era runtime in addition to the
  modern one ([Unity Discussions confirms](https://discussions.unity.com/t/what-c-redistributable-does-unity3d-editor-require/244474)).
  This is a SEPARATE Microsoft package from the 2015-2022 generation -- the
  modern installer does NOT install MSVCP100. Downloaded from the canonical
  Microsoft URL
  `https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe`
  (no `aka.ms` shortcut exists for VS 2010 because extended support ended
  2020-07-14) and verified by Authenticode signature before launch. Uses
  silent-install switches `/q /norestart` (DIFFERENT from the modern
  generation's `/install /quiet /norestart`).
- **Microsoft Visual C++ 2015-2022 x64 Redistributable** (the original
  DAD-MACHINE fix). Installs `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`,
  `MSVCP140.dll`, and the rest of the 14.x C/C++ runtime that Unity links
  against. Downloaded from the canonical Microsoft URL
  `https://aka.ms/vc14/vc_redist.x64.exe` and verified by Authenticode
  signature before launch.
- **Windows long-path support.** Writes
  `HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem!LongPathsEnabled = 1`.
  Resolves the Android NDK 93% unpack failure at the legacy MAX_PATH boundary
  (see the next section for the underlying root cause).
- **Windows Defender exclusions** for `C:\Unity\Editors` and the active
  runner workspace (**best-effort, perf optimization**). Prevents Defender
  from transient-locking NDK files during unpack. Skipped gracefully when
  Defender is absent. Also **skipped on non-admin per-job preflight runs**
  (the runner agent service typically runs as `NETWORK SERVICE`, which
  cannot call `Add-MpPreference`); Defender management is not a correctness
  requirement for Unity startup, so a non-admin runner does not attempt it.
  To install or refresh exclusions, run the bootstrap from an elevated
  shell on the host (see Local recovery above) or trigger
  `runner-bootstrap.yml`.
- **PowerShell 7 (`pwsh`)** via `winget install --id Microsoft.PowerShell
--scope user`. The `--scope user` install means Administrator is not
  required for `pwsh` itself.
- **UCRT** sanity check. Modern Windows (Windows 10+, Server 2019+) already
  ship UCRT. On downlevel Windows the script probes for KB2999226 and emits
  an actionable `::error::` pointing at the KB download page rather than
  attempting the MSU install itself (the URL is host-specific and is a
  one-time operator action).

### Audit only (no install)

Use `-DetectOnly` for a read-only audit of host state. The script reports
every prereq's status without mutating anything. Exit codes:

- `0`: every prereq is present.
- `2`: at least one prereq is missing.
- non-zero, non-2: an unrecoverable error occurred during detection.

```powershell
.\scripts\unity\bootstrap-windows-runner.ps1 -DetectOnly
```

The same audit is available via the workflow: pick `detect-only: true` on the
`Run workflow` dialog.

### Verification

After bootstrap, sanity-check the host from any PowerShell session on the
runner:

```powershell
pwsh -v
Test-Path 'C:\Windows\System32\VCRUNTIME140_1.dll'
Test-Path 'C:\Windows\System32\MSVCP100.dll'
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem').LongPathsEnabled
```

Expected output:

- `pwsh -v` reports PowerShell 7.x.
- BOTH `Test-Path` checks return `True` (VCRUNTIME140_1.dll is from the
  VC++ 2015-2022 redist; MSVCP100.dll is from the VC++ 2010 SP1 redist --
  Unity needs both).
- `LongPathsEnabled` is `1`.

The next Unity job's `Print runner diagnostics` step runs the per-job
preflight (`assert-unity-host-prereqs`). A green preflight is the live
confirmation that recovery worked.

### When auto-install fails

Common failure modes and the remediation for each:

- **Runner agent is not running as Administrator.** Both VC++ redists
  (2010 and 2015-2022) and `LongPathsEnabled` need `HKLM` writes. The
  script detects access-denied via a locale-safe exception-type check and
  emits an actionable `::error::` pointing at this section. Fix: re-run
  the script from an elevated shell on the host, or configure the runner
  agent service account with local admin rights and re-trigger the
  workflow.
- **Network failure during the VC++ download.** Re-trigger the workflow. The
  script pins each generation's URL to its canonical Microsoft host
  (`https://aka.ms/vc14/vc_redist.x64.exe` for the 2015-2022 generation;
  `https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe`
  for the 2010 SP1 generation) so a transient failure is a real network
  issue, not a redirect drift.
- **VC++ Authenticode signature mismatch.** The script refuses to launch an
  installer (either generation) that is not signed by Microsoft. If this
  fires, do NOT bypass: the download was corrupted or the host has been
  redirected. Investigate before re-running.
- **`winget` is missing.** Some self-hosted images ship without the
  `App Installer` package. Install **App Installer** from the Microsoft Store
  on the host (or use the standalone installer linked from the
  [PowerShell releases page](https://github.com/PowerShell/PowerShell/releases)),
  then re-trigger.
- **Downlevel Windows (Windows 7, Server 2012 R2).** The UCRT step emits an
  `::error::` pointing at the KB2999226 download page. Install the MSU
  manually, reboot, and re-trigger. Modern runners should not hit this path.

### Persistent 0xC0000135 after VC++ + long-paths are confirmed installed

**Most common cause: missing Microsoft Visual C++ 2010 Redistributable
(x64).** The bootstrap installs this automatically -- confirm it ran
successfully AS ADMIN at least once on the host (the `vcredist-2010` step
in the summary line must say `ok`, not `install-failed`). If the summary
shows `vcredist-2010=install-failed` while `vcredist-2015-2022=ok`, the
host is missing the 2010 generation's `MSVCP100.dll` / `MSVCR100.dll`
which Unity depends on independently of the modern redist.

The per-job preflight (`assert-unity-host-prereqs`) has already exported
`DXM_RUNNER_PREREQ_INSTALLED=1` (`vcredist-2010=ok vcredist-2015-2022=ok
long-paths=ok`), yet Unity itself still exits `0xC0000135` from the
startup probe. The DLL that the Windows loader cannot resolve is therefore
**NOT** in either VC++ Redistributable bundle (both 2010 SP1 and 2015-2022
have been installed). `ensure-editor.ps1` now resolves every Unity.exe
import against the Windows loader search path (KnownDLLs / Unity install
dir / System32 / Windows / PATH, both regular and delay-loaded imports)
and emits an annotation that NAMES the specific missing DLL(s) instead
of a truncated list. The annotation lives on a single line and looks
roughly like:

```text
::error title=Unity <version> host prerequisite missing::Unity <version> native startup failed with exit -1073741515 (0xC0000135 / STATUS_DLL_NOT_FOUND). The Windows loader could not resolve a DLL Unity.exe imports. Preflight ran successfully at job start (VC++ 2010/VC++ 2015-2022/long-paths/Defender/pwsh OK), so this is a DIFFERENT missing DLL (Unity-version-specific or corrupt install). Re-running the bootstrap script will NOT help. If the missing DLL is MSVCP100.dll, the host needs Microsoft Visual C++ 2010 Redistributable; the bootstrap script's 'vcredist-2010' step installs this. MISSING DLL(s): <names>. ... Resolved: <S> system + <U> editor + <W> Windows + <P> PATH + <K> KnownDLLs out of <N> total imports. Probe log: ...
```

#### If the missing DLL is a SYSTEM library (`CRYPT32.dll`, `bcrypt.dll`, `KERNEL32.dll`, `ucrtbase.dll`, `api-ms-win-*.dll`, etc.)

The host's Windows install is damaged or incomplete. Bootstrap cannot
fix this; the system component itself is gone. Repair on the host:

1. From an elevated PowerShell on the host:

   ```powershell
   sfc /scannow
   DISM /Online /Cleanup-Image /RestoreHealth
   ```

   `sfc` repairs a known-bad system file from the local component store;
   `DISM` repairs the component store itself from Windows Update if it
   has been corrupted. Both are idempotent.

1. If `sfc`/`DISM` cannot repair the file, reimage the runner. A
   Windows install that has lost a core system DLL has had something
   destructive happen to it (failed Windows Update, manual DLL
   deletion, malware cleanup); reimaging is faster and safer than
   patching the running install.

#### If the missing DLL is UNITY-SHIPPED (`libfbxsdk.dll`, `optix.*.dll`, `OpenImageDenoise.dll`, `umbraoptimizer64.dll`, `*compress*.dll`, `FreeImage.dll`, `WinPixEventRuntime.dll`, etc.)

The Unity install is partial or corrupt. The annotation includes the
`partial or corrupt` hint when it detects any Unity-shipped DLL in the
missing list. Quarantine and reinstall:

1. Stop the runner agent so it does not hold files open:

   ```powershell
   Stop-Service actions.runner.*
   ```

1. Move the corrupt install to a quarantine directory (timestamp so
   re-runs do not clobber prior quarantines):

   ```powershell
   $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
   New-Item -ItemType Directory -Force -Path 'C:\Unity\Editors\_quarantine' | Out-Null
   Move-Item 'C:\Unity\Editors\<version>' "C:\Unity\Editors\_quarantine\<version>-$ts"
   Start-Service actions.runner.*
   ```

1. On the next CI run, `ensure-editor.ps1`'s auto-install path will
   re-download Unity into the now-empty managed root.

Alternatively, force `ensure-editor.ps1` to perform the managed
reinstall in-line from CI without a host-side operation. Set
`DXM_UNITY_FORCE_REINSTALL=1` on the re-trigger (workflow-dispatch env,
matrix env, or step-level env): the env var **bypasses the 0xC0000135
short-circuit** and falls through to the existing repair pipeline. The
bypass exists exactly for this case (operator has confirmed the missing
DLL is Unity-shipped, not OS). Do NOT set this env var when the missing
DLL is a system library; the reinstall will not help and will burn ~6
minutes per matrix cell.

#### If ALL imports resolve but Unity still fails 0xC0000135

The annotation says `All Unity.exe imports resolve on the loader search
path, yet the OS loader still failed`. This is rare; the loader is
failing on a **transitive** dependency (one of Unity's direct imports
has its own unresolved import) or a loader-init-time security policy
block (EDR / AppLocker / Code Integrity Guard).

1. Install the Windows SDK debug tools on the host
   (`gflags.exe` ships with the Debugging Tools for Windows / Windows
   SDK), then enable loader snaps for `Unity.exe`:

   ```powershell
   gflags.exe -i Unity.exe +sls
   ```

1. **CRITICAL: loader snaps go to the kernel debug output stream, NOT
   to the Application event log.** To actually capture them, download
   [DebugView from
   Sysinternals](https://learn.microsoft.com/en-us/sysinternals/downloads/debugview),
   run `Dbgview.exe` **as Administrator**, and enable
   **Capture -> Capture Kernel** AND **Capture -> Capture Global
   Win32**. Then run `Unity.exe` (or trigger the CI job). The loader
   snap output (`LdrpLoadDll`, `LdrpProcessRelocationBlock`,
   `LdrpSnapModule`, `LdrpFindOrMapDll`, etc.) appears in DebugView's
   window. Save the capture (File -> Save) for the next debug pass.

1. Disable loader snaps after diagnosing (gflags settings persist):

   ```powershell
   gflags.exe -i Unity.exe -sls
   ```

1. If the failure is an EDR / AppLocker / CIG block, the event-log
   entry (Security log, NOT Application) will name the policy. Add
   `Unity.exe` and the Unity install dir to the relevant allowlist on
   the host.

##### Known red herring: Event 1534 (`tiledatamodelsvc`)

The Application event log on every Windows 10 1809+ host emits
continuous **Event ID 1534** warnings from `User Profile Service`:

```text
Profile notification of event Load for component
{B31118B2-1F49-48E5-B6F5-BC21CAEC56FB} failed, error code is See
Tracelogging for error details.
```

`{B31118B2-...}` is `tiledatamodelsvc` (Tile Data Model service),
which Microsoft removed in Windows 10 1809 but left a stale
registration entry under
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileNotification`.

**This is NOT related to Unity's `0xC0000135`.** The "Load" in
"Profile notification of event Load" is **user profile load**
(logon), not **DLL load** -- a different Windows subsystem
(`UserProfileSvc`) from the loader (`ntdll!Ldrp*`). Every Windows
10 1809+ machine emits this warning continuously; Unity runs fine
on most of them.

If the noise is bothersome, delete the orphan registry key (silences
the warning; no effect on Unity):

```powershell
Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileNotification\TDL' -Force -ErrorAction SilentlyContinue
```

Sources:
[Microsoft Q&A 1534](https://learn.microsoft.com/en-us/answers/questions/2185615/user-profile-event-trigger-1534),
[gHacks: Event ID 1534 warnings](https://www.ghacks.net/2018/12/29/windows-10-user-profile-service-event-id-1534-warnings/).

### Cross-references

- Bootstrap script: [`scripts/unity/bootstrap-windows-runner.ps1`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/unity/bootstrap-windows-runner.ps1).
- Per-job preflight composite: [`.github/actions/assert-unity-host-prereqs/action.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/actions/assert-unity-host-prereqs/action.yml).
- Auto-recovery workflow: [`.github/workflows/runner-bootstrap.yml`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.github/workflows/runner-bootstrap.yml).
- LLM-agent skill: [`.llm/skills/unity/unity-runner-host-prereqs.md`](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/skills/unity/unity-runner-host-prereqs.md).
- The [PowerShell 7 prerequisite](#powershell-7-prerequisite-on-self-hosted-runners)
  section above documents the `pwsh: command not found` failure that the
  same bootstrap fixes.
- The next section,
  [Android NDK install failures and Windows long-path (MAX_PATH) enablement](#android-ndk-install-failures-and-windows-long-path-max_path-enablement),
  documents the NDK 93% unpack failure; the bootstrap script's
  `LongPathsEnabled` step is the durable runner-side remediation for that
  failure mode.

## Android NDK install failures and Windows long-path (MAX_PATH) enablement

The Android provisioning profile installs `android` plus
`android-sdk-ndk-tools`, a multi-GB Google download whose **NDK unpack** phase
fails flakily on Windows. Non-Android Unity CI jobs should use `EditorOnly` or
`StandaloneWindowsIl2Cpp` and should not enter this path.

### Symptom

- The base editor and any profile-selected non-Android modules provision fine,
  but an Android-profile run fails on the Android tier with a message like
  `Unity <version> Android CI module install FAILED after N attempt(s)`.
- The Unity CLI reaches roughly **93%** of the install and then dies, frequently
  with **exit code 6**, while the NDK extraction is in progress.
- `ensure-editor.ps1`'s post-mortem (printed automatically on Android exhaustion)
  reports the **deepest NDK absolute path length** and the **`LongPathsEnabled`
  state**, and emits a `::warning::` pointing here when the deepest NDK path is at
  or beyond ~240 characters while Windows long-path support is not enabled. After
  bounded Android-only retries, the script may escalate to managed
  quarantine/reinstall unless editor repair is disabled.

### Root cause

The Android NDK tree contains very deeply nested toolchain paths. When Windows
long-path support is disabled, the NDK extraction hits the legacy **MAX_PATH
(260-character)** limit mid-unpack and the install fails. Antivirus file-locking
on the freshly written files can also interrupt the unpack.

### Fix on the runner

1. **Enable long paths** (`LongPathsEnabled = 1`). Via the registry:

   ```powershell
   New-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' `
     -Name 'LongPathsEnabled' -Value 1 -PropertyType DWord -Force
   ```

   Or via Group Policy: **Computer Configuration -> Administrative Templates ->
   System -> Filesystem -> Enable Win32 long paths -> Enabled**. Restart the
   self-hosted runner agent (and ideally reboot) so the change takes effect.

1. **(Optional) Add a Windows Defender exclusion** for the Unity install root
   (`C:\Unity\Editors`) so Defender does not transiently lock NDK files during
   extraction:

   ```powershell
   Add-MpPreference -ExclusionPath 'C:\Unity\Editors'
   ```

After enabling long paths, re-run the workflow. The post-mortem's
`LongPathsEnabled` line should now read `True`, and the deep-path `::warning::`
should no longer fire.

## Local documentation validation

To reproduce the strict-mode mkdocs build that runs in CI:

```bash
npm run validate:docs:strict
```

That command installs the pinned `requirements-docs.txt` and runs `mkdocs build --strict --site-dir _site`. Use `npm run validate:docs` for the much faster out-of-tree link guard alone (no mkdocs install required).

## Audit log

- Record the date, operator initials, and the resolution chosen in the operator log only.
- Do not paste organization settings screenshots, repository identifiers from other organizations, or runner registration tokens.
- Note follow-ups in the team's private operator log, not in tracked files.
