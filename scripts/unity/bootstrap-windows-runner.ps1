#Requires -Version 5.1
<#
.SYNOPSIS
    Idempotent one-shot Windows runner bootstrap for Unity CI prerequisites.

.DESCRIPTION
    Installs (or detects, when -DetectOnly) the host-OS prerequisites that
    Unity Editor needs to launch on a self-hosted Windows GitHub Actions
    runner. Root cause this script addresses: Unity.exe failed at startup
    with exit code -1073741515 (0xC0000135 / STATUS_DLL_NOT_FOUND) on
    DAD-MACHINE because the Microsoft Visual C++ Redistributables were not
    installed -- the OS loader could not resolve a DLL Unity.exe needs.
    Unity 2021.3 / 2022.3 / 6000.x ALL depend on BOTH the VC++ 2010
    runtime (MSVCP100.dll / MSVCR100.dll -- this is the load-bearing missing
    DLL identified in production run 70874414898) AND the VC++ 2015-2022
    runtime (VCRUNTIME140.dll / VCRUNTIME140_1.dll / MSVCP140.dll). The two
    are SEPARATE redistributable packages: installing only the modern
    2015-2022 generation leaves MSVCP100 unresolved. GitHub-hosted windows-2022
    ships both preinstalled; self-hosted runners do not. The existing
    ensure-editor.ps1 retries a Unity reinstall on that failure, which is
    futile -- the missing DLL is on the OS, not in the Unity install.

    Prerequisites covered (each is independently detected + remediated):

      1. Microsoft Visual C++ 2010 SP1 x64 Redistributable (version
         10.0.40219.325). Installs MSVCP100.dll + MSVCR100.dll into
         C:\Windows\System32. Unity 2021/2022/6000 depend on this 2010-era
         runtime in addition to the modern one (Unity Discussions:
         https://discussions.unity.com/t/what-c-redistributable-does-unity3d-editor-require/244474).
         Detect (primary): file-level probe of System32 for MSVCP100.dll AND
                 MSVCR100.dll. Both must exist.
         Detect (secondary, only if file probe passes): registry confirms a
                 "blessed" install via
                 HKLM:\SOFTWARE\Microsoft\VisualStudio\10.0\VC\VCRedist\x64
                 (Installed=1) on either the native 64-bit view OR Wow6432Node.
         Install: Download
                  https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe
                  then & vcredist_x64.exe /q /norestart. NOTE the silent
                  switches differ from the modern generation: /q (not /quiet)
                  and no /install verb. Authenticode signature is verified
                  before launch (Microsoft signs both 2010 and 2015-2022
                  redistributables).

      2. Microsoft Visual C++ 2015-2022 x64 Redistributable
         Detect (primary): file-level probe of System32 for
                 VCRUNTIME140.dll, VCRUNTIME140_1.dll, MSVCP140.dll. The OS
                 loader uses the actual files on disk -- the entire bug class
                 this script exists for is "DLL missing on disk", so file
                 presence is the authoritative signal. ALL three must exist.
         Detect (secondary, only if file probe passes): registry confirms a
                 "blessed" install via
                 HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64
                 (Installed=1 + Bld>=26020). The 26020 threshold separates VS
                 2017 15.5+ (the first version that ships VCRUNTIME140_1.dll)
                 from older 14.0 RTM. The x64 redist registers only in the
                 native 64-bit view, never Wow6432Node, so we deliberately
                 do NOT probe the WOW mirror.
         Install: Download https://aka.ms/vc14/vc_redist.x64.exe then
                  & vc_redist.x64.exe /install /quiet /norestart. URL is
                  pinned via [ValidatePattern] to known Microsoft hosts;
                  Authenticode signature is verified before launch.

      3. Windows long-paths
         Detect: HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem!LongPathsEnabled
         Install: New-ItemProperty ... LongPathsEnabled -Value 1 (DWORD).

      4. Windows Defender exclusions for the Unity install root and the runner
         workspace directory. Path inputs are validated against an allow-list
         so a hostile/misconfigured RUNNER_WORKSPACE cannot exclude C:\.
         Skipped gracefully when Defender is absent.

      5. PowerShell 7 (pwsh) -- installed via winget --scope user when missing.

      6. UCRT on downlevel Windows. Modern Windows 10+/Server 2019+ ship UCRT
         preinstalled, so on those hosts this step is a silent no-op. On
         downlevel hosts we emit ::error:: pointing at the KB2999226 download
         page (we do not auto-download the MSU because it is a one-time
         operator action and the URL is host-specific).

    The script is non-fatal across prereqs: each install path catches its own
    exceptions and converts them into a recorded failure. One prereq's
    failure never short-circuits the others. The final summary line lists
    every prereq's final state and exit code reflects the worst outcome.

    Designed for self-hosted GitHub Actions runners running as NETWORK SERVICE
    (non-admin). On a non-admin shell, prereqs that need HKLM writes (VC++ 2010,
    VC++ 2015-2022, LongPathsEnabled) will fail at install time with Access
    Denied -- the script catches that and emits a SPECIFIC ::error:: telling
    the operator to either run the script as Administrator or trigger
    .github/workflows/runner-bootstrap.yml from the Actions UI. We deliberately
    do NOT auto-elevate via Start-Process -Verb RunAs because UAC would hang
    a non-interactive CI run.

    --- Import contract -----------------------------------------------------
    DOT-SOURCING THIS SCRIPT MUST NOT MUTATE THE CALLER'S SESSION PREFERENCES.
    Specifically `Set-StrictMode`, `$ErrorActionPreference`, and
    `$PSNativeCommandUseErrorActionPreference` are configured ONLY inside the
    `if ($invokedAsScript)` dispatcher at the bottom of the file. Each
    function that needs StrictMode does its own LOCAL `Set-StrictMode` (which
    is scoped to that function and unwinds on return). Contract tests
    (powershell-syntax.test.js et al.) dot-source the script for parse + AST
    inspection; that path must stay side-effect-free with respect to caller
    preferences.

    NOTE: dot-sourcing WILL bind the script's param-block variables
    (`$DetectOnly`, `$UnityInstallRoot`, `$VcRedistUrl`, `$VcRedist2010Url`,
    `$DownloadTimeoutSeconds`, `$InstallTimeoutSeconds`) and the
    `$invokedAsScript` dispatcher flag in the caller's scope. That is
    standard PowerShell behaviour for any dot-sourced script with a
    `param()` block and cannot be avoided; tests that care should run in a
    sub-shell or use Push-Variable/Pop-Variable patterns.

.PARAMETER DetectOnly
    Detect-and-report only; never installs. Exit code: 0 = every prereq OK,
    2 = at least one prereq missing. Used by contract tests and by the
    per-job preflight when DXM_RUNNER_DISABLE_AUTO_BOOTSTRAP=1.

.PARAMETER UnityInstallRoot
    Defender-exclusion path for Unity editor installs. Defaults to the
    UNITY_EDITOR_INSTALL_ROOT env var, falling back to C:\Unity\Editors.

.PARAMETER VcRedistUrl
    Canonical Microsoft VC++ 2015-2022 redist download URL. Override-able for
    tests. The [ValidatePattern] pins the host to Microsoft-controlled domains
    so a hostile caller cannot redirect us to an attacker-controlled binary.
    After download, Authenticode signature is verified before exec.

.PARAMETER VcRedist2010Url
    Canonical Microsoft VC++ 2010 SP1 x64 redist download URL. Defaults to
    https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe
    (the only canonical URL Microsoft publishes for this artifact; there is no
    aka.ms shortcut because VS 2010 extended support ended 2020-07-14).
    Override-able for tests. The [ValidatePattern] restricts the host to
    Microsoft-controlled domains (download.microsoft.com and *.microsoft.com).
    Authenticode signature is verified before exec.

.PARAMETER DownloadTimeoutSeconds
    HTTP timeout for the VC++ redist download. Default 600 seconds.
    WHY 600: the redist is ~25 MB; on a slow CI link with TLS handshake and
    HTTP redirect chain (aka.ms -> download.visualstudio.microsoft.com),
    300s has been observed to time out under sustained load. 600s gives
    headroom without masking a truly broken connection.

.PARAMETER InstallTimeoutSeconds
    Hard cap on the VC++ redist installer wait. Default 900 seconds.
    WHY 900: Microsoft's redist installer can stall for several minutes on
    pending Windows reboot states (it scans servicing state). 900s (15min)
    is the largest value that still lets a hung installer fail-fast within
    the 30-minute self-hosted-runner job budget.

.EXAMPLE
    .\bootstrap-windows-runner.ps1
        # Install every missing prereq (run from elevated shell).

.EXAMPLE
    .\bootstrap-windows-runner.ps1 -DetectOnly
        # Exit 0 if every prereq is present; exit 2 if any are missing.

.NOTES
    Parses cleanly on Linux pwsh (enforced by scripts/__tests__/powershell-syntax.test.js).
    Every Windows-only call site (registry, Add-MpPreference, winget) is wrapped
    in a Test-IsWindowsHost gate so this file is safe to load on Linux/macOS.
#>
[CmdletBinding()]
[OutputType([int])]
param(
    [switch]$DetectOnly,

    [string]$UnityInstallRoot = $(if ($env:UNITY_EDITOR_INSTALL_ROOT) { $env:UNITY_EDITOR_INSTALL_ROOT } else { 'C:\Unity\Editors' }),

    # WHY [ValidatePattern]: an over-broad caller could pass
    # `-VcRedistUrl http://evil.example.com/dropper.exe` and we would happily
    # download + Authenticode-fail (or worse, exec). Pin host up-front; the
    # post-download signature check is defense-in-depth.
    [ValidatePattern('^https://(aka\.ms|download\.visualstudio\.microsoft\.com|[A-Za-z0-9._-]+\.microsoft\.com)/')]
    [string]$VcRedistUrl = 'https://aka.ms/vc14/vc_redist.x64.exe',

    # WHY a separate parameter for the 2010 redist URL: the VC++ 2010 SP1
    # download lives at a different (and more URL-specific) Microsoft host than
    # the modern redist. There is no aka.ms shortcut for the 2010 generation
    # because VS 2010 extended support ended 2020-07-14, so the only canonical
    # URL Microsoft publishes is the direct download.microsoft.com path with
    # the artifact GUID. The [ValidatePattern] still pins the host to
    # Microsoft-controlled domains (download.microsoft.com is the canonical
    # CDN endpoint; *.microsoft.com is the broader allowance for any future
    # redirect-host migration). Authenticode signature is verified post-
    # download regardless of the URL.
    [ValidatePattern('^https://(download\.microsoft\.com|[A-Za-z0-9._-]+\.microsoft\.com)/')]
    [string]$VcRedist2010Url = 'https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe',

    [int]$DownloadTimeoutSeconds = 600,

    [int]$InstallTimeoutSeconds = 900
)

# IMPORTANT: top-level body is intentionally side-effect-free on dot-source.
# Set-StrictMode, $ErrorActionPreference, and $PSNativeCommandUseErrorActionPreference
# are configured EXCLUSIVELY inside the $invokedAsScript dispatcher at the
# bottom of the file. See the "Import contract" section in the .DESCRIPTION.

# CI annotation helpers -- mirror ensure-editor.ps1's Write-CiNotice pattern.
# All three MUST emit single-line Write-Host so they survive ConciseView's
# word-wrap (see reference_pwsh_error_wrap_test_fragility). Never use
# Write-Output (the runner annotation parser does not treat the standard
# output stream identically to host output in every host configuration).

function Write-CiNotice {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::notice::$Message"
}

function Write-CiWarning {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::warning::$Message"
}

function Write-CiError {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::error::$Message"
}

function Test-IsWindowsHost {
    # Single source of truth for "is this a real Windows host". Used as a top-
    # level gate on every install/registry call so the script loads cleanly
    # on Linux pwsh (which is where powershell-syntax.test.js exercises it).
    # We deliberately do NOT consult $IsWindows here -- PS 5.1 lacks that
    # built-in (it is a PS 6+/7 automatic variable) and Set-StrictMode -Version
    # Latest would throw on the access. DirectorySeparatorChar is universally
    # available and is '\\' on Windows, '/' everywhere else.
    return ([System.IO.Path]::DirectorySeparatorChar -eq '\')
}

function Test-IsAdministrator {
    # Safe to call on Linux (returns $false). On Windows, returns $true iff
    # the current process holds the Administrator role.
    #
    # TEST-ONLY hermeticity override (same spirit as DXM_UNITY_FAKE_LONGPATHS_ENABLED
    # in ensure-editor.ps1): when DXM_RUNNER_FAKE_IS_ADMIN is set to '1' or '0',
    # honor that BEFORE the real probe. This lets the helper-mutation suite
    # behaviorally test the admin / non-admin tier paths from a single
    # invocation on any OS -- without it, a Linux test of the dispatcher's
    # non-admin Defender-skip path requires manual shell-launching tricks.
    # Real-world admin/non-admin detection is unaffected (the env var is never
    # set in production / CI).
    if ($env:DXM_RUNNER_FAKE_IS_ADMIN -eq '1') {
        return $true
    }
    if ($env:DXM_RUNNER_FAKE_IS_ADMIN -eq '0') {
        return $false
    }
    if (-not (Test-IsWindowsHost)) {
        return $false
    }
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

function Test-IsAccessDeniedException {
    # WHY a dedicated helper: the previous regex match against the English
    # message text ('denied|elevation|administrator|not allowed') silently
    # failed on German/French/Japanese Windows installs (see F13). Match the
    # exception SHAPE instead -- HResult E_ACCESSDENIED is locale-invariant.
    # Returns $true iff the caught error looks like a Windows access-denied
    # failure. NEVER throws.
    param([Parameter(Mandatory = $true)]$ErrorRecord)

    try {
        $ex = $ErrorRecord.Exception
        while ($null -ne $ex) {
            # Locale-invariant: System.UnauthorizedAccessException and
            # SecurityException are the canonical .NET shapes.
            $typeName = $ex.GetType().FullName
            if ($typeName -eq 'System.UnauthorizedAccessException') { return $true }
            if ($typeName -eq 'System.Security.SecurityException') { return $true }
            if ($typeName -eq 'Microsoft.PowerShell.Cmdletization.Cim.CimJobException') { return $true }
            # HResult E_ACCESSDENIED = 0x80070005 ((int)-2147024891). Some
            # cmdlets wrap the Win32 error in a generic exception but the
            # HResult is preserved.
            try {
                $hr = [int]$ex.HResult
                if ($hr -eq -2147024891) { return $true }
            } catch { }
            $ex = $ex.InnerException
        }
        # Last resort: the English regex fallback. Kept so unfamiliar
        # exception shapes still surface as access-denied when the message
        # clearly says so on en-US hosts (the common case).
        $message = [string]$ErrorRecord.Exception.Message
        if ($message -match 'denied|elevation|administrator|not allowed') {
            return $true
        }
    } catch { }
    return $false
}

function Get-RegistryItemPropertySafe {
    # Wrapper around Get-ItemPropertyValue that swallows "key not found" /
    # "property not found" / wrong-type and returns $null. NEVER throws.
    # Centralized so the VC++ probes do not duplicate try/catch noise.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            return $null
        }
        $value = Get-ItemPropertyValue -LiteralPath $Path -Name $Name -ErrorAction Stop
        return $value
    } catch {
        return $null
    }
}

function Test-VcRedistModernFilesOnDisk {
    # WHY this is the PRIMARY detection signal: the entire bug class this
    # script exists for is "OS loader cannot find VCRUNTIME140_1.dll".
    # Registry presence is not the authoritative signal -- the actual file
    # on disk is. If any of the three required files is missing, the OS
    # loader will fail Unity.exe at startup regardless of what the registry
    # says. Returns @{ present; missing }. NEVER throws.
    # All three DLLs ship in the VS 2015-2022 redist installer; all are
    # consumed by Unity.exe's import table.
    if (-not (Test-IsWindowsHost)) {
        return @{ present = $true; missing = @() }
    }
    $required = @(
        'C:\Windows\System32\VCRUNTIME140.dll',
        'C:\Windows\System32\VCRUNTIME140_1.dll',
        'C:\Windows\System32\MSVCP140.dll'
    )
    $missing = New-Object 'System.Collections.Generic.List[string]'
    foreach ($f in $required) {
        try {
            if (-not (Test-Path -LiteralPath $f)) {
                $missing.Add($f) | Out-Null
            }
        } catch {
            # Test-Path can throw on permission-denied paths; treat as missing.
            $missing.Add($f) | Out-Null
        }
    }
    return @{ present = ($missing.Count -eq 0); missing = @($missing.ToArray()) }
}

function Test-VcRedist2010FilesOnDisk {
    # PRIMARY detection signal for the VC++ 2010 SP1 x64 Redistributable.
    # Mirrors Test-VcRedistModernFilesOnDisk: the OS loader uses the actual
    # files on disk, not the registry. The 2010 redist installs TWO load-
    # bearing DLLs into System32: MSVCP100.dll (C++ Standard Library) and
    # MSVCR100.dll (C Runtime). Both must exist or Unity 2021.3 / 2022.3 /
    # 6000.x will fail at startup with 0xC0000135 (this is the load-bearing
    # missing DLL identified in production run 70874414898 -- the bootstrap
    # was previously installing only the modern 2015-2022 generation and
    # missing MSVCP100 entirely).
    # Returns @{ present; missing }. NEVER throws. Non-Windows hosts return
    # the vacuous-OK shape so callers can compose without crashing on Linux
    # (the dot-source-safety tests rely on this).
    if (-not (Test-IsWindowsHost)) {
        return @{ present = $true; missing = @() }
    }
    # Use Join-Path to be explicit that we are probing the OS-resolved
    # System32 directory (a pinned `C:\Windows\System32\...` literal would
    # be wrong if WINDIR is ever non-default; mirror the modern probe's
    # literal style except prefer the env-var-derived join here so we get
    # both styles covered).
    $system32 = Join-Path $env:WINDIR 'System32'
    $required = @(
        (Join-Path $system32 'MSVCP100.dll'),
        (Join-Path $system32 'MSVCR100.dll')
    )
    $missing = New-Object 'System.Collections.Generic.List[string]'
    foreach ($f in $required) {
        try {
            if (-not (Test-Path -LiteralPath $f)) {
                $missing.Add($f) | Out-Null
            }
        } catch {
            # Test-Path can throw on permission-denied paths; treat as missing.
            $missing.Add($f) | Out-Null
        }
    }
    return @{ present = ($missing.Count -eq 0); missing = @($missing.ToArray()) }
}

function Test-VcRedistModernInstalledAtRegistryView {
    # Probes the native registry view for the VC++ 2015-2022 x64 redist
    # signature. Returns hashtable @{ installed = $bool; reason = $string }
    # so the caller can both log the diagnostic and short-circuit on the
    # first positive hit.
    #
    # WHY Bld >= 26020 (not 30000): VS 2017 15.5 is the first toolset that
    # ships VCRUNTIME140_1.dll, and its redist registers Bld values from
    # 26020 upward. The previous 30000 threshold false-negatived every
    # VS 2017 + VS 2019 (<= 16.7) install, which is exactly the lineage
    # most self-hosted runners come from. See:
    # https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist
    param([Parameter(Mandatory = $true)][string]$BaseKey)

    $runtimesKey = Join-Path $BaseKey 'Microsoft\VisualStudio\14.0\VC\Runtimes\X64'
    $servicingKey = Join-Path $BaseKey 'Microsoft\DevDiv\VC\Servicing\14.0\RuntimeMinimum'

    $installed = Get-RegistryItemPropertySafe -Path $runtimesKey -Name 'Installed'
    $bld = Get-RegistryItemPropertySafe -Path $runtimesKey -Name 'Bld'
    if ($installed -eq 1 -and $null -ne $bld) {
        $bldInt = 0
        if ([int]::TryParse([string]$bld, [ref]$bldInt)) {
            if ($bldInt -ge 26020) {
                return @{ installed = $true; reason = "$runtimesKey (Installed=1, Bld=$bldInt >= 26020 [VCRUNTIME140_1.dll introduced in VS 2017 15.5])" }
            }
        }
    }

    $version = Get-RegistryItemPropertySafe -Path $servicingKey -Name 'Version'
    if ($version) {
        return @{ installed = $true; reason = "$servicingKey (Version=$version)" }
    }

    return @{ installed = $false; reason = "neither $runtimesKey nor $servicingKey indicates installed" }
}

function Test-VcRedistModernInstalled {
    # Two-stage detection for the VC++ 2015-2022 x64 Redistributable.
    # PRIMARY: file-level probe of System32 for the three required DLLs.
    # SECONDARY: registry confirmation that the install is "blessed" (so we
    # do not false-positive on a partial / hand-copied DLL set without a
    # real installer record).
    #
    # WHY this order: the OS loader uses files on disk, not the registry.
    # If files are missing, Unity.exe will fail to start regardless of what
    # the registry says. If files are present but registry says not-installed
    # (extremely unusual), we still proceed because the loader will succeed --
    # we Write-CiWarning so the operator notices the inconsistency.
    #
    # WHY only the native registry view (NOT Wow6432Node): the x64 redist
    # registers exclusively in the native 64-bit view. Probing Wow6432Node
    # was dead code: the x64 installer never writes there. If a 32-bit
    # installer mirrored the keys it would be a false positive for the x64
    # DLL set we actually need on disk.
    #
    # Linux-safe: returns $true vacuously when not on Windows so callers can
    # ignore the prereq on non-Windows hosts (this script never runs the
    # install path on non-Windows, but the helper is still callable from
    # tests).
    if (-not (Test-IsWindowsHost)) {
        return $true
    }

    Set-StrictMode -Version Latest

    $fileProbe = Test-VcRedistModernFilesOnDisk
    if (-not $fileProbe.present) {
        $missingList = ($fileProbe.missing -join ', ')
        Write-CiNotice "VC++ 2015-2022 redist file probe missing: $missingList"
        return $false
    }

    # Files are present -- confirm registry agrees (for sanity).
    $regProbe = Test-VcRedistModernInstalledAtRegistryView -BaseKey 'HKLM:\SOFTWARE'
    if ($regProbe.installed) {
        Write-CiNotice "VC++ 2015-2022 redist detected: files on disk + registry agrees via $($regProbe.reason)"
        return $true
    }

    # Files exist but registry disagrees. Trust the loader's POV (files
    # decide) but flag the inconsistency.
    Write-CiWarning "VC++ 2015-2022 redist DLLs are present on disk but registry probe says not-installed ($($regProbe.reason)). Treating as installed (the OS loader uses files, not the registry); operator may want to verify the install record is intact."
    return $true
}

function Test-VcRedist2010InstalledAtRegistryView {
    # Probes a specific registry view (native HKLM:\SOFTWARE OR Wow6432Node)
    # for the VC++ 2010 SP1 x64 redist signature. Returns hashtable
    # @{ installed = $bool; reason = $string }. NEVER throws.
    #
    # WHY both views are probed (caller composes via OR): unlike the modern
    # 2015-2022 generation, the 2010 SP1 redist installer has been observed
    # to write its install record to BOTH the native and Wow6432Node views
    # depending on the install host's bitness / install path / installer
    # variant (Microsoft's installer technology for VS 2010 differs from
    # the modern VC redist). A single registry view probe would false-
    # negative on hosts where the install record landed in the other view.
    # Test-VcRedist2010Installed below composes the two views with OR.
    param([Parameter(Mandatory = $true)][string]$BaseKey)

    $vcredistKey = Join-Path $BaseKey 'Microsoft\VisualStudio\10.0\VC\VCRedist\x64'

    $installed = Get-RegistryItemPropertySafe -Path $vcredistKey -Name 'Installed'
    if ($installed -eq 1) {
        return @{ installed = $true; reason = "$vcredistKey (Installed=1)" }
    }

    return @{ installed = $false; reason = "$vcredistKey does not indicate installed" }
}

function Test-VcRedist2010Installed {
    # Two-stage detection for the VC++ 2010 SP1 x64 Redistributable.
    # PRIMARY: file-level probe of System32 for MSVCP100.dll + MSVCR100.dll.
    # SECONDARY: registry confirmation via either the native HKLM:\SOFTWARE
    # view OR the Wow6432Node mirror (the 2010 installer has been observed
    # to write to either depending on host configuration -- unlike the
    # modern 2015-2022 redist, which writes exclusively to the native view).
    #
    # Returns hashtable @{ installed = $bool; reason = $string } so the caller
    # can both log the diagnostic and short-circuit. The shape mirrors the
    # composite-return pattern of Test-VcRedistModernInstalled's underlying
    # registry helper; we add the outer hashtable here so Install-VcRedist2010's
    # admin pre-check can return early with a meaningful reason field.
    #
    # WHY this order: the OS loader uses files on disk, not the registry.
    # If files are missing, Unity.exe will fail to start regardless of what
    # the registry says. If files are present but registry says not-installed
    # (extremely unusual), we still proceed because the loader will succeed --
    # we Write-CiWarning so the operator notices the inconsistency.
    #
    # Linux-safe: returns @{ installed = $true; reason = '...' } vacuously
    # when not on Windows so callers can ignore the prereq on non-Windows
    # hosts (this script never runs the install path on non-Windows, but the
    # helper is still callable from tests).
    if (-not (Test-IsWindowsHost)) {
        return @{ installed = $true; reason = 'not a Windows host (vacuous OK)' }
    }

    Set-StrictMode -Version Latest

    $fileProbe = Test-VcRedist2010FilesOnDisk
    if (-not $fileProbe.present) {
        $missingList = ($fileProbe.missing -join ', ')
        Write-CiNotice "VC++ 2010 redist file probe missing: $missingList"
        return @{ installed = $false; reason = "file probe missing: $missingList" }
    }

    # Files are present -- confirm registry agrees (for sanity). Composite
    # OR over native + Wow6432Node: the 2010 installer can land in either
    # view depending on host configuration. At least one positive hit is
    # sufficient to call the install "blessed".
    $nativeProbe = Test-VcRedist2010InstalledAtRegistryView -BaseKey 'HKLM:\SOFTWARE'
    if ($nativeProbe.installed) {
        Write-CiNotice "VC++ 2010 redist detected: files on disk + registry agrees via $($nativeProbe.reason)"
        return @{ installed = $true; reason = $nativeProbe.reason }
    }

    $wowProbe = Test-VcRedist2010InstalledAtRegistryView -BaseKey 'HKLM:\SOFTWARE\Wow6432Node'
    if ($wowProbe.installed) {
        Write-CiNotice "VC++ 2010 redist detected: files on disk + registry agrees via $($wowProbe.reason)"
        return @{ installed = $true; reason = $wowProbe.reason }
    }

    # Files exist but neither registry view agrees. Trust the loader's POV
    # (files decide) but flag the inconsistency.
    $combinedReason = "$($nativeProbe.reason); $($wowProbe.reason)"
    Write-CiWarning "VC++ 2010 redist DLLs are present on disk but neither registry view indicates installed ($combinedReason). Treating as installed (the OS loader uses files, not the registry); operator may want to verify the install record is intact."
    return @{ installed = $true; reason = "files present on disk but registry inconsistent: $combinedReason" }
}

function Test-VcRedistAuthenticodeSignatureMicrosoft {
    # WHY: even with the [ValidatePattern]-pinned URL, we want defense-in-
    # depth before exec'ing a downloaded binary. A signed-and-trusted
    # Microsoft certificate is the strongest signal we can verify locally.
    # Returns @{ valid; reason }. NEVER throws.
    param([Parameter(Mandatory = $true)][string]$FilePath)

    if (-not (Test-IsWindowsHost)) {
        # Get-AuthenticodeSignature is Windows-only.
        return @{ valid = $false; reason = "Authenticode signature verification skipped on non-Windows" }
    }
    try {
        $sig = Get-AuthenticodeSignature -FilePath $FilePath -ErrorAction Stop
        if (-not $sig) {
            return @{ valid = $false; reason = "Get-AuthenticodeSignature returned null" }
        }
        if ($sig.Status -ne 'Valid') {
            return @{ valid = $false; reason = "Authenticode Status='$($sig.Status)' (expected 'Valid')" }
        }
        $subject = ''
        try { $subject = [string]$sig.SignerCertificate.Subject } catch { $subject = '' }
        if ($subject -notmatch 'Microsoft Corporation') {
            return @{ valid = $false; reason = "Authenticode signer '$subject' does not match 'Microsoft Corporation'" }
        }
        return @{ valid = $true; reason = "Authenticode Valid, signer=$subject" }
    } catch {
        return @{ valid = $false; reason = "Get-AuthenticodeSignature threw: $($_.Exception.Message)" }
    }
}

function Invoke-VcRedistModernDownload {
    # Downloads vc_redist.x64.exe (the VC++ 2015-2022 redist) to a temp path.
    # Returns hashtable @{ success = $bool; path = $string; reason = $string }.
    # NEVER throws. On failure, the partial temp file is cleaned up in the
    # finally block so we never leave litter for the next bootstrap run.
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    Set-StrictMode -Version Latest

    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("vc_redist.x64.{0}.exe" -f ([Guid]::NewGuid().ToString('N')))
    $success = $false
    try {
        # WHY explicit TLS 1.2/1.3: Windows PowerShell 5.1's
        # ServicePointManager default is Ssl3|Tls (TLS 1.0). aka.ms and
        # download.visualstudio.microsoft.com reject anything below TLS 1.2.
        # THIS is the bootstrap -- it runs FIRST, BEFORE pwsh exists, so
        # PS 5.1 is the realistic runtime here. Without this block,
        # Invoke-WebRequest fails with "The underlying connection was closed:
        # An unexpected error occurred on a send." on a stock Server 2019.
        try {
            $tls12 = [Net.SecurityProtocolType]::Tls12
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor $tls12
            # Tls13 may not be defined on older .NET Framework (4.7.2-);
            # ignore in catch so we still get TLS 1.2.
            try {
                $tls13 = [Net.SecurityProtocolType]::Tls13
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor $tls13
            } catch { }
        } catch {
            Write-CiWarning "Could not configure TLS 1.2/1.3 (continuing; download may fail on older Windows): $($_.Exception.Message)"
        }

        # -UseBasicParsing is REQUIRED for Windows PowerShell 5.1 compatibility:
        # without it, Invoke-WebRequest tries to instantiate IE's COM parser
        # which fails on server SKUs. PS7's Invoke-WebRequest ignores the
        # parameter so we can pass it unconditionally.
        # -MaximumRedirection 5: aka.ms is a redirector to
        # download.visualstudio.microsoft.com; the default of 0 would fail.
        # -UserAgent: some Microsoft CDNs heuristically rate-limit the
        # default PowerShell UA; a deterministic UA gives us a paper trail
        # without changing behavior.
        Invoke-WebRequest -Uri $Url -OutFile $tempPath -UseBasicParsing -TimeoutSec $TimeoutSeconds -MaximumRedirection 5 -UserAgent 'DXMessaging-RunnerBootstrap/1.0' -ErrorAction Stop
        if (-not (Test-Path -LiteralPath $tempPath)) {
            return @{ success = $false; path = $tempPath; reason = "Invoke-WebRequest returned without an exception but no file appeared at $tempPath" }
        }
        $size = (Get-Item -LiteralPath $tempPath).Length
        if ($size -le 0) {
            return @{ success = $false; path = $tempPath; reason = "Downloaded file at $tempPath is 0 bytes (likely a transient failure)" }
        }
        $success = $true
        return @{ success = $true; path = $tempPath; reason = "downloaded $size bytes to $tempPath" }
    } catch {
        return @{ success = $false; path = $tempPath; reason = "Invoke-WebRequest failed: $($_.Exception.Message)" }
    } finally {
        # WHY this cleanup: previously a failed Invoke-WebRequest left a
        # partial file at $tempPath (e.g. half-downloaded after a timeout).
        # The next bootstrap run would generate a different GUID temp path,
        # so the litter accumulated indefinitely. The success path keeps
        # the file for the caller to install; the failure path scrubs it.
        if (-not $success) {
            try {
                if (Test-Path -LiteralPath $tempPath) {
                    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
                }
            } catch { }
        }
    }
}

function Invoke-VcRedistModernInstaller {
    # Runs the vc_redist.x64.exe installer (VC++ 2015-2022 generation) with
    # the silent/no-restart flags. Uses Start-Process -PassThru and a manual
    # WaitForExit($ms) so we can enforce a timeout (Start-Process has no
    # -Timeout parameter). Returns hashtable
    # @{ success = $bool; exitCode = $int; reason = $string }. NEVER throws.
    # Treats well-known installer exit codes per Microsoft docs:
    #   0    = installed (success)
    #   1638 = newer version already installed (treat as success)
    #   3010 = success-restart-required (treat as success + warning)
    #   1602 = user cancel (failure)
    #   1603 = fatal install error (failure)
    # Reference:
    #   https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
    param(
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    Set-StrictMode -Version Latest

    if (-not (Test-Path -LiteralPath $InstallerPath)) {
        return @{ success = $false; exitCode = -1; reason = "installer path $InstallerPath does not exist" }
    }

    # Resolve the installer log path up-front so both success-mode reasons
    # (e.g. 1638/3010) AND failure-mode reasons include it. The redist
    # installer writes dd_vcredist_amd64_*.log under %TEMP%.
    $logHint = "${env:TEMP}\dd_vcredist_amd64_*.log"

    $process = $null
    try {
        $process = Start-Process -FilePath $InstallerPath -ArgumentList @('/install', '/quiet', '/norestart') -PassThru -ErrorAction Stop
        $exited = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $exited) {
            # $pid is a read-only AUTOMATIC variable in PowerShell (the running
            # pwsh's own PID) — assigning to it throws under StrictMode + Stop.
            # Use a renamed local so the timeout-kill diagnostic survives.
            $killedPid = $process.Id
            try { $process.Kill() } catch { } # best-effort
            # WHY blocking WaitForExit() after Kill(): without it, the
            # Process object may still hold an unsignaled handle while the
            # kernel finishes process termination, leading to handle leaks
            # under repeated bootstrap loops. The unbounded WaitForExit()
            # is a finite wait in practice (Kill() targets STATUS_CONTROL_C
            # / TerminateProcess which the kernel completes in milliseconds).
            try { $process.WaitForExit() } catch { }
            return @{ success = $false; exitCode = -1; reason = "installer did not exit within $TimeoutSeconds seconds; killed process $killedPid (see $logHint)" }
        }
        $code = $process.ExitCode
        switch ($code) {
            0 { return @{ success = $true; exitCode = $code; reason = "installer reported success (exit 0); log: $logHint" } }
            1638 { return @{ success = $true; exitCode = $code; reason = "newer version already installed (exit 1638; idempotent); log: $logHint" } }
            3010 { return @{ success = $true; exitCode = $code; reason = "success-restart-required (exit 3010; a reboot is recommended but not enforced here); log: $logHint" } }
            1602 { return @{ success = $false; exitCode = $code; reason = "user cancel (exit 1602); log: $logHint" } }
            1603 { return @{ success = $false; exitCode = $code; reason = "fatal install error (exit 1603); see $logHint" } }
            default { return @{ success = $false; exitCode = $code; reason = "unrecognized installer exit code $code; see $logHint" } }
        }
    } catch {
        $message = $_.Exception.Message
        # Detect the Access Denied case via exception SHAPE (not English
        # regex). Surface it as a specific actionable error.
        if (Test-IsAccessDeniedException -ErrorRecord $_) {
            return @{ success = $false; exitCode = -1; reason = "access denied (likely non-admin): $message" }
        }
        return @{ success = $false; exitCode = -1; reason = "Start-Process threw: $message" }
    } finally {
        # WHY Dispose(): Process is IDisposable and holds a kernel handle
        # to the child process even after exit. Repeated bootstrap runs
        # without Dispose accumulate handles and (eventually) GDI/User
        # objects via the parent token. Wrap in try/catch because Dispose
        # itself can throw if the handle was already cleaned up.
        if ($null -ne $process) {
            try { $process.Dispose() } catch { }
        }
    }
}

function Invoke-VcRedist2010Download {
    # Downloads the VC++ 2010 SP1 x64 redist (vcredist_x64.exe, version
    # 10.0.40219.325) to a temp path. Mirrors Invoke-VcRedistModernDownload
    # in structure (TLS 1.2/1.3 setup, partial-file cleanup, deterministic
    # user-agent) but uses a different default URL because Microsoft does
    # NOT publish an aka.ms shortcut for VS 2010 (extended support ended
    # 2020-07-14). The default download.microsoft.com path is the only
    # canonical Microsoft URL for this artifact.
    # Returns hashtable @{ success = $bool; path = $string; reason = $string }.
    # NEVER throws. On failure, the partial temp file is cleaned up in the
    # finally block so we never leave litter for the next bootstrap run.
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    Set-StrictMode -Version Latest

    # WHY a different temp file prefix: keeps the modern + 2010 install
    # processes' temp files distinguishable in the logs (a quick `ls $env:TEMP`
    # immediately separates "the 2010 path stalled" from "the modern path
    # stalled" when debugging on the host).
    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("vcredist_x64_2010.{0}.exe" -f ([Guid]::NewGuid().ToString('N')))
    $success = $false
    try {
        # WHY explicit TLS 1.2/1.3: Windows PowerShell 5.1's
        # ServicePointManager default is Ssl3|Tls (TLS 1.0). The download
        # endpoint (download.microsoft.com) rejects anything below TLS 1.2.
        # This is the bootstrap -- it runs FIRST, BEFORE pwsh exists, so
        # PS 5.1 is the realistic runtime. Without this block,
        # Invoke-WebRequest fails with "The underlying connection was closed:
        # An unexpected error occurred on a send." on a stock Server 2019.
        try {
            $tls12 = [Net.SecurityProtocolType]::Tls12
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor $tls12
            # Tls13 may not be defined on older .NET Framework (4.7.2-);
            # ignore in catch so we still get TLS 1.2.
            try {
                $tls13 = [Net.SecurityProtocolType]::Tls13
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor $tls13
            } catch { }
        } catch {
            Write-CiWarning "Could not configure TLS 1.2/1.3 (continuing; download may fail on older Windows): $($_.Exception.Message)"
        }

        # -UseBasicParsing is REQUIRED for Windows PowerShell 5.1 compatibility
        # (mirror modern download). -MaximumRedirection 5 keeps us tolerant of
        # any Microsoft CDN redirect chain that might be introduced; the
        # current URL is a direct download but using the same redirection
        # tolerance as the modern path future-proofs us.
        # -UserAgent: deterministic UA gives us a paper trail in Microsoft
        # CDN logs without changing behavior.
        Invoke-WebRequest -Uri $Url -OutFile $tempPath -UseBasicParsing -TimeoutSec $TimeoutSeconds -MaximumRedirection 5 -UserAgent 'DXMessaging-RunnerBootstrap/1.0' -ErrorAction Stop
        if (-not (Test-Path -LiteralPath $tempPath)) {
            return @{ success = $false; path = $tempPath; reason = "Invoke-WebRequest returned without an exception but no file appeared at $tempPath" }
        }
        $size = (Get-Item -LiteralPath $tempPath).Length
        if ($size -le 0) {
            return @{ success = $false; path = $tempPath; reason = "Downloaded file at $tempPath is 0 bytes (likely a transient failure)" }
        }
        $success = $true
        return @{ success = $true; path = $tempPath; reason = "downloaded $size bytes to $tempPath" }
    } catch {
        return @{ success = $false; path = $tempPath; reason = "Invoke-WebRequest failed: $($_.Exception.Message)" }
    } finally {
        # WHY this cleanup: a failed Invoke-WebRequest can leave a partial file
        # at $tempPath (e.g. half-downloaded after a timeout). The next
        # bootstrap run would generate a different GUID temp path, so the
        # litter accumulated indefinitely. The success path keeps the file
        # for the caller to install; the failure path scrubs it.
        if (-not $success) {
            try {
                if (Test-Path -LiteralPath $tempPath) {
                    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
                }
            } catch { }
        }
    }
}

function Invoke-VcRedist2010Installer {
    # Runs the vcredist_x64.exe installer (VC++ 2010 SP1 generation) with
    # the silent / no-restart flags SPECIFIC to the 2010 installer
    # technology. Uses Start-Process -PassThru and a manual WaitForExit($ms)
    # so we can enforce a timeout (Start-Process has no -Timeout parameter).
    # Returns hashtable @{ success = $bool; exitCode = $int; reason = $string }.
    # NEVER throws. Treats well-known installer exit codes per Microsoft docs:
    #   0    = installed (success)
    #   1638 = newer version already installed (treat as success)
    #   3010 = success-restart-required (treat as success + warning)
    #   1602 = user cancel (failure)
    #   1603 = fatal install error (failure)
    # Reference:
    #   https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
    #
    # CRITICAL DIFFERENCE FROM Invoke-VcRedistModernInstaller: the VC++ 2010
    # installer uses `/q /norestart` (NOT `/install /quiet /norestart`). The
    # 2010-era Microsoft installer technology predates the unified `/install`
    # verb and exposes a different (older) silent-install switch set.
    # Passing `/install /quiet /norestart` to vcredist_x64.exe (2010) causes
    # the installer to silently FAIL (the unrecognized arguments cause the
    # installer to fall through to its "show help" code path which never
    # actually installs). The /q switch is the load-bearing one for 2010.
    param(
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    Set-StrictMode -Version Latest

    if (-not (Test-Path -LiteralPath $InstallerPath)) {
        return @{ success = $false; exitCode = -1; reason = "installer path $InstallerPath does not exist" }
    }

    # WHY a different installer-log filename: the 2010 redist installer
    # writes to a separate log filename than the modern installer
    # (dd_vcredist_*_2010 vs dd_vcredist_amd64). We don't know the exact
    # filename pattern Microsoft chose for the VS 2010 generation (the
    # MSI internally), so the hint stays generic: %TEMP% is the location.
    $logHint = "${env:TEMP}\dd_*.log (and ${env:TEMP}\*VC*Redist*.log if present)"

    $process = $null
    try {
        # NOTE: /q (not /quiet) and NO /install verb. Pinned via the static
        # contract test so a future refactor cannot silently regress to the
        # modern switches that would cause a no-op install.
        $process = Start-Process -FilePath $InstallerPath -ArgumentList @('/q', '/norestart') -PassThru -ErrorAction Stop
        $exited = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $exited) {
            # $pid is a read-only AUTOMATIC variable in PowerShell (the running
            # pwsh's own PID) — assigning to it throws under StrictMode + Stop.
            # Use a renamed local so the timeout-kill diagnostic survives.
            $killedPid = $process.Id
            try { $process.Kill() } catch { } # best-effort
            # Blocking WaitForExit() after Kill() to prevent handle leaks
            # (mirror modern installer's pattern).
            try { $process.WaitForExit() } catch { }
            return @{ success = $false; exitCode = -1; reason = "installer did not exit within $TimeoutSeconds seconds; killed process $killedPid (see $logHint)" }
        }
        $code = $process.ExitCode
        switch ($code) {
            0 { return @{ success = $true; exitCode = $code; reason = "installer reported success (exit 0); log: $logHint" } }
            1638 { return @{ success = $true; exitCode = $code; reason = "newer version already installed (exit 1638; idempotent); log: $logHint" } }
            3010 { return @{ success = $true; exitCode = $code; reason = "success-restart-required (exit 3010; a reboot is recommended but not enforced here); log: $logHint" } }
            1602 { return @{ success = $false; exitCode = $code; reason = "user cancel (exit 1602); log: $logHint" } }
            1603 { return @{ success = $false; exitCode = $code; reason = "fatal install error (exit 1603); see $logHint" } }
            default { return @{ success = $false; exitCode = $code; reason = "unrecognized installer exit code $code; see $logHint" } }
        }
    } catch {
        $message = $_.Exception.Message
        # Detect the Access Denied case via exception SHAPE (not English
        # regex). Surface it as a specific actionable error.
        if (Test-IsAccessDeniedException -ErrorRecord $_) {
            return @{ success = $false; exitCode = -1; reason = "access denied (likely non-admin): $message" }
        }
        return @{ success = $false; exitCode = -1; reason = "Start-Process threw: $message" }
    } finally {
        # Dispose() the Process to release the kernel handle (mirror modern
        # installer's pattern). Try/catch because Dispose itself can throw
        # if the handle was already cleaned up.
        if ($null -ne $process) {
            try { $process.Dispose() } catch { }
        }
    }
}

function Install-VcRedistModern {
    # Download + Authenticode verify + install + post-install probe for the
    # VC++ 2015-2022 x64 Redistributable. Returns hashtable
    # @{ success = $bool; reason = $string }. NEVER throws -- every failure
    # is captured and the call site emits an annotation from $reason.
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][int]$DownloadTimeoutSeconds,
        [Parameter(Mandatory = $true)][int]$InstallTimeoutSeconds
    )

    Set-StrictMode -Version Latest

    if (-not (Test-IsWindowsHost)) {
        return @{ success = $false; reason = "not a Windows host" }
    }

    # WHY pre-check Test-IsAdministrator: vcredist install writes HKLM and
    # System32 -- both require elevation. Detecting non-admin BEFORE the
    # download + Authenticode check produces a faster, more deterministic
    # error path and matches the modern installer's pre-check posture for
    # parity. (The download/sig-check would still work on non-admin but the
    # installer would access-denied; bailing here saves the round-trip.)
    if (-not (Test-IsAdministrator)) {
        return @{ success = $false; reason = "access denied (likely non-admin): VC++ 2015-2022 redist install requires Administrator. Run the bootstrap from an elevated shell or trigger .github/workflows/runner-bootstrap.yml." }
    }

    $download = Invoke-VcRedistModernDownload -Url $Url -TimeoutSeconds $DownloadTimeoutSeconds
    if (-not $download.success) {
        return @{ success = $false; reason = "download failed: $($download.reason)" }
    }

    try {
        # WHY signature verification BEFORE exec: even though
        # [ValidatePattern] pins the URL to Microsoft hosts, a hostile
        # man-in-the-middle (or compromised CDN entry) could serve a
        # binary that looks legit but is not Microsoft-signed. The
        # Authenticode signature anchors trust at the Microsoft certificate.
        $sigCheck = Test-VcRedistAuthenticodeSignatureMicrosoft -FilePath $download.path
        if (-not $sigCheck.valid) {
            return @{ success = $false; reason = "Authenticode signature check failed: $($sigCheck.reason). Refusing to execute downloaded installer." }
        }

        $install = Invoke-VcRedistModernInstaller -InstallerPath $download.path -TimeoutSeconds $InstallTimeoutSeconds
        if (-not $install.success) {
            return @{ success = $false; reason = "installer failed (exit $($install.exitCode)): $($install.reason)" }
        }

        # WHY a file-level post-install probe (in addition to the registry
        # check inside Test-VcRedistModernInstalled): the installer can report
        # success exit 0 even when the kernel cancelled mid-extract (rare
        # but observed after Defender quarantine of a DLL). The OS loader
        # cares about the actual files on disk -- if ANY of the three are
        # missing post-install, we know the install lied. F8/F15.
        $fileProbe = Test-VcRedistModernFilesOnDisk
        if (-not $fileProbe.present) {
            $missingList = ($fileProbe.missing -join ', ')
            return @{ success = $false; reason = "installer reported success (exit $($install.exitCode)) but post-install file probe missing: $missingList. check ${env:TEMP}\dd_vcredist_amd64_*.log" }
        }

        # Post-install re-probe: file probe passed; combine with registry
        # check via Test-VcRedistModernInstalled for a final canonical answer.
        if (-not (Test-VcRedistModernInstalled)) {
            return @{ success = $false; reason = "installer reported success (exit $($install.exitCode)) but post-install probe still shows not-installed; check ${env:TEMP}\dd_vcredist_amd64_*.log" }
        }

        if ($install.exitCode -eq 3010) {
            Write-CiWarning "VC++ 2015-2022 redist installer requested a reboot (exit 3010). The runtime is installed but a reboot is recommended before the next Unity job."
        }
        return @{ success = $true; reason = $install.reason }
    } finally {
        # Best-effort cleanup of the downloaded installer (we have what we need).
        try { Remove-Item -LiteralPath $download.path -Force -ErrorAction SilentlyContinue } catch { }
    }
}

function Install-VcRedist2010 {
    # Download + Authenticode verify + install + post-install probe for the
    # VC++ 2010 SP1 x64 Redistributable. Returns hashtable
    # @{ success = $bool; reason = $string }. NEVER throws -- every failure
    # is captured and the call site emits an annotation from $reason.
    #
    # MIRRORS Install-VcRedistModern's flow:
    #   1. Test-IsAdministrator pre-check (HKLM writes require elevation).
    #   2. Skip if already installed (idempotent re-run).
    #   3. Download via Invoke-VcRedist2010Download.
    #   4. Authenticode signature check via the shared
    #      Test-VcRedistAuthenticodeSignatureMicrosoft helper (Microsoft signs
    #      both 2010 and 2015-2022 redistributables with the same certificate
    #      chain).
    #   5. Launch via Invoke-VcRedist2010Installer (uses /q /norestart, NOT
    #      /install /quiet /norestart -- the 2010 installer's switches differ
    #      from the modern generation).
    #   6. Post-install file-on-disk re-verify; if the files are still missing
    #      after an exit-0 install, treat it as a failure and surface the
    #      installer-log hint.
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][int]$DownloadTimeoutSeconds,
        [Parameter(Mandatory = $true)][int]$InstallTimeoutSeconds
    )

    Set-StrictMode -Version Latest

    if (-not (Test-IsWindowsHost)) {
        return @{ success = $false; reason = "not a Windows host" }
    }

    # WHY pre-check Test-IsAdministrator FIRST: the 2010 redist install
    # writes HKLM and System32. Both require elevation. Detecting non-admin
    # BEFORE the download + Authenticode check produces a faster, more
    # deterministic error path. Mirrors the access-denied posture of the
    # modern installer.
    if (-not (Test-IsAdministrator)) {
        return @{ success = $false; reason = "access denied (likely non-admin): VC++ 2010 redist install requires Administrator. Run the bootstrap from an elevated shell or trigger .github/workflows/runner-bootstrap.yml." }
    }

    # Idempotent skip: if Test-VcRedist2010Installed already reports success,
    # there is nothing for us to do. (The Invoke-BootstrapStep wrapper already
    # gates on Detect; this is belt-and-suspenders for any direct caller.)
    $detect = Test-VcRedist2010Installed
    if ($detect.installed) {
        return @{ success = $true; reason = "already installed: $($detect.reason)" }
    }

    $download = Invoke-VcRedist2010Download -Url $Url -TimeoutSeconds $DownloadTimeoutSeconds
    if (-not $download.success) {
        return @{ success = $false; reason = "download failed: $($download.reason)" }
    }

    try {
        # WHY signature verification BEFORE exec: even though
        # [ValidatePattern] pins the URL to Microsoft hosts, a hostile
        # man-in-the-middle (or compromised CDN entry) could serve a
        # binary that looks legit but is not Microsoft-signed. The
        # Authenticode signature anchors trust at the Microsoft certificate.
        # Microsoft signs BOTH the 2010 and 2015-2022 redistributables, so
        # we reuse the same Test-VcRedistAuthenticodeSignatureMicrosoft helper.
        $sigCheck = Test-VcRedistAuthenticodeSignatureMicrosoft -FilePath $download.path
        if (-not $sigCheck.valid) {
            return @{ success = $false; reason = "Authenticode signature check failed: $($sigCheck.reason). Refusing to execute downloaded installer." }
        }

        $install = Invoke-VcRedist2010Installer -InstallerPath $download.path -TimeoutSeconds $InstallTimeoutSeconds
        if (-not $install.success) {
            return @{ success = $false; reason = "installer failed (exit $($install.exitCode)): $($install.reason)" }
        }

        # WHY a file-level post-install probe: the installer can report
        # success exit 0 even when something went wrong on disk (e.g. AV
        # quarantine of MSVCP100.dll mid-extract -- rare but observed). The
        # OS loader cares about the actual files on disk -- if either
        # MSVCP100.dll or MSVCR100.dll is missing post-install, we know the
        # install lied and Unity will still fail with 0xC0000135.
        $fileProbe = Test-VcRedist2010FilesOnDisk
        if (-not $fileProbe.present) {
            $missingList = ($fileProbe.missing -join ', ')
            return @{ success = $false; reason = "installer reported success (exit $($install.exitCode)) but post-install file probe missing: $missingList. Check %TEMP% for the installer log." }
        }

        # Post-install re-probe via the composite Test-VcRedist2010Installed
        # for the canonical answer (files + registry).
        $postDetect = Test-VcRedist2010Installed
        if (-not $postDetect.installed) {
            return @{ success = $false; reason = "installer reported success (exit $($install.exitCode)) but post-install probe still shows not-installed: $($postDetect.reason). Check %TEMP% for the installer log." }
        }

        if ($install.exitCode -eq 3010) {
            Write-CiWarning "VC++ 2010 redist installer requested a reboot (exit 3010). The runtime is installed but a reboot is recommended before the next Unity job."
        }
        return @{ success = $true; reason = $install.reason }
    } finally {
        # Best-effort cleanup of the downloaded installer (we have what we need).
        try { Remove-Item -LiteralPath $download.path -Force -ErrorAction SilentlyContinue } catch { }
    }
}

function Test-LongPathsEnabled {
    # Mirrors Test-WindowsLongPathSupport in ensure-editor.ps1 (read it for
    # style) -- registry-only probe; honors the same DXM_UNITY_FAKE_LONGPATHS_ENABLED
    # test override so contract tests can deterministically exercise both
    # branches on every OS. Returns $true (enabled) / $false (disabled or
    # unreadable). NEVER throws. Difference from ensure-editor.ps1: we
    # collapse the $null "unreadable" case into $false here, because the
    # bootstrap WANTS to remediate any non-true state.
    if ($env:DXM_UNITY_FAKE_LONGPATHS_ENABLED -eq '1') {
        return $true
    }
    if ($env:DXM_UNITY_FAKE_LONGPATHS_ENABLED -eq '0') {
        return $false
    }
    if (-not (Test-IsWindowsHost)) {
        return $true
    }
    try {
        $value = Get-ItemPropertyValue -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name 'LongPathsEnabled' -ErrorAction Stop
        if ($null -eq $value) {
            return $false
        }
        return ([int]$value -ne 0)
    } catch {
        return $false
    }
}

function Enable-LongPaths {
    # Sets the LongPathsEnabled DWORD to 1. Requires HKLM write -- on a
    # non-admin shell New-ItemProperty throws "Requested registry access is
    # not allowed". We catch that and convert to a specific actionable
    # ::error:: at the call site. Returns @{ success; reason }. NEVER throws.
    if (-not (Test-IsWindowsHost)) {
        return @{ success = $false; reason = "not a Windows host" }
    }
    # WHY pre-check Test-IsAdministrator: catches the access-denied case
    # locale-invariantly BEFORE the cmdlet throws, so the failure reason
    # is deterministic regardless of Windows display language.
    if (-not (Test-IsAdministrator)) {
        return @{ success = $false; reason = "not running as Administrator; HKLM write of LongPathsEnabled requires elevation" }
    }
    try {
        New-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name 'LongPathsEnabled' -Value 1 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
        # Re-probe to confirm the write actually landed.
        if (Test-LongPathsEnabled) {
            return @{ success = $true; reason = "LongPathsEnabled set to 1 (re-probe confirmed)" }
        }
        return @{ success = $false; reason = "New-ItemProperty returned without throwing but re-probe still reports disabled" }
    } catch {
        $message = $_.Exception.Message
        if (Test-IsAccessDeniedException -ErrorRecord $_) {
            return @{ success = $false; reason = "access denied (likely non-admin): $message" }
        }
        return @{ success = $false; reason = "New-ItemProperty threw: $message" }
    }
}

function Test-DefenderExclusionPathAllowed {
    # WHY: previously the bootstrap would happily forward any string in
    # $env:RUNNER_WORKSPACE to Add-MpPreference, including pathological
    # values like 'C:\' or '\' which would whitelist the ENTIRE filesystem
    # from Defender on-access scanning -- a massive privilege amplification
    # (F2/F10). This helper enforces an allow-list:
    #   1. Must start with $UnityInstallRoot prefix, OR
    #   2. Contain a known runner workspace fragment (\_work\,
    #      \actions-runner\, \runners\), OR
    #   3. Match nothing on the allow-list -> reject.
    # We also reject single drive roots and any string shorter than 8 chars
    # as a coarse-grained sanity check.
    # Returns $true if the path is safe to exclude; $false otherwise.
    # NEVER throws.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$UnityInstallRoot
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    # Reject single-drive roots: 'C:', 'C:\', 'C:/', 'D:\', etc. The regex
    # below tolerates either separator and an optional trailing one.
    if ($Path -match '^[A-Za-z]:[\\/]?$') {
        return $false
    }

    # Reject obviously-too-short paths. Most legitimate paths are much
    # longer; 8 chars is well below 'C:\Unity'.
    if ($Path.Length -lt 8) {
        return $false
    }

    # Reject UNC roots like '\\server\share' without a subpath.
    if ($Path -match '^[\\/]+[^\\/]+[\\/][^\\/]+[\\/]?$') {
        # OK: '\\server\share\sub'. NOT OK: '\\server\share' or '\\server\share\'.
        # The simple regex above matches `\\server\share` -- so reject.
        # (A subpath would produce two more `\sub` segments and not match.)
        if ($Path -notmatch '[\\/].+[\\/].+[\\/].+') {
            return $false
        }
    }

    # Comparison must be case-insensitive on Windows paths.
    $cmp = [System.StringComparison]::OrdinalIgnoreCase

    # Rule 1: prefix match against UnityInstallRoot. Use StartsWith with the
    # case-insensitive comparer rather than a regex match (UnityInstallRoot
    # is unsanitized user input and could contain regex metacharacters).
    # C4 (final-review nit): require a path-separator boundary after the
    # prefix so `C:\Unity\Editors` does NOT accidentally match a sibling
    # directory like `C:\Unity\EditorsBackup\...`. Equality (exact match)
    # is also allowed -- the root itself is a valid exclusion target.
    if (-not [string]::IsNullOrWhiteSpace($UnityInstallRoot)) {
        $rootNoTrail = $UnityInstallRoot.TrimEnd('\', '/')
        if ([string]::Equals($Path, $rootNoTrail, $cmp) -or
            $Path.StartsWith($rootNoTrail + '\', $cmp) -or
            $Path.StartsWith($rootNoTrail + '/', $cmp)) {
            return $true
        }
    }

    # Rule 2: contains a known runner workspace fragment.
    $workspaceFragments = @('\_work\', '/_work/', '\actions-runner\', '/actions-runner/', '\runners\', '/runners/')
    foreach ($frag in $workspaceFragments) {
        if ($Path.IndexOf($frag, $cmp) -ge 0) {
            return $true
        }
    }

    return $false
}

function Get-DefenderExclusionPaths {
    # Returns the array of paths to add to Defender exclusions. Always
    # includes $UnityInstallRoot; additionally includes $env:RUNNER_WORKSPACE
    # when defined (the GitHub Actions runner workspace dir holds the
    # Unity Library cache that Defender on-access scanning would otherwise
    # thrash). De-duplicates by normalized path. Untrusted/suspicious paths
    # are rejected via Test-DefenderExclusionPathAllowed -- a rejected path
    # produces a Write-CiWarning but does not abort.
    #
    # IMPORTANT: callers MUST splat the return value with @() at the call
    # site so an empty-array return survives the PowerShell-pipeline
    # unwrap. Returning an empty List[string].ToArray() unwraps to
    # AutomationNull, which would then fail to bind to a Mandatory
    # [string[]] parameter downstream (F1).
    param([Parameter(Mandatory = $true)][string]$UnityInstallRoot)

    $candidates = New-Object 'System.Collections.Generic.List[string]'
    if (-not [string]::IsNullOrWhiteSpace($UnityInstallRoot)) {
        $candidates.Add($UnityInstallRoot) | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_WORKSPACE)) {
        $candidates.Add($env:RUNNER_WORKSPACE) | Out-Null
    }

    # Validate each candidate against the allow-list.
    $validated = New-Object 'System.Collections.Generic.List[string]'
    foreach ($p in $candidates) {
        if (Test-DefenderExclusionPathAllowed -Path $p -UnityInstallRoot $UnityInstallRoot) {
            $validated.Add($p) | Out-Null
        } else {
            Write-CiWarning "rejecting suspicious exclusion path '$p' (not in allow-list; would amplify Defender exclusions). Allowed: paths under UnityInstallRoot ('$UnityInstallRoot') or containing \_work\ / \actions-runner\ / \runners\."
        }
    }

    # De-dup case-insensitively (Windows paths are case-insensitive).
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $result = New-Object 'System.Collections.Generic.List[string]'
    foreach ($p in $validated) {
        if ($seen.Add($p)) {
            $result.Add($p) | Out-Null
        }
    }
    # NOTE: returning .ToArray() on an empty list unwraps to AutomationNull
    # in the PowerShell pipeline. Callers must @()-wrap the call to keep an
    # empty array shape; we document that contract on the param block above.
    return $result.ToArray()
}

function Test-DefenderAvailable {
    # Returns $true iff Get-MpPreference is callable AND returns a
    # non-null object. Defender may be disabled, uninstalled, or missing
    # the Defender PowerShell module entirely on Server Core SKUs.
    # NEVER throws.
    #
    # Side-effect: on success, caches the resolved $pref object in
    # $script:DxmDefenderPref so callers can read ExclusionPath without
    # racing a second Get-MpPreference call (F21). The cache is local to
    # this script-scope variable; tests that re-import the script start
    # with a clean cache automatically.
    if (-not (Test-IsWindowsHost)) {
        return $false
    }
    try {
        $cmd = Get-Command -Name 'Get-MpPreference' -ErrorAction SilentlyContinue
        if (-not $cmd) {
            return $false
        }
        $pref = Get-MpPreference -ErrorAction Stop
        if ($null -eq $pref) {
            return $false
        }
        $script:DxmDefenderPref = $pref
        return $true
    } catch {
        return $false
    }
}

function Test-DefenderExclusion {
    # Returns $true iff $Path appears (case-insensitively) in the current
    # Defender ExclusionPath set. Caller MUST first verify Test-DefenderAvailable.
    # Uses $script:DxmDefenderPref when available (cached by
    # Test-DefenderAvailable) to avoid the race where Get-MpPreference
    # disappears between the availability probe and this call.
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-IsWindowsHost)) {
        return $true
    }
    try {
        $pref = $null
        # Use cached pref if available.
        try { $pref = $script:DxmDefenderPref } catch { $pref = $null }
        if ($null -eq $pref) {
            $pref = Get-MpPreference -ErrorAction Stop
        }
        $existing = @($pref.ExclusionPath)
        foreach ($e in $existing) {
            if ($null -eq $e) { continue }
            if ([string]::Equals([string]$e, $Path, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        return $false
    } catch {
        return $false
    }
}

function Add-DefenderExclusion {
    # Adds $Path to Defender ExclusionPath. Returns @{ success; reason }.
    # NEVER throws. Add-MpPreference is idempotent w.r.t. duplicates (the
    # cmdlet collapses), but we still gate on Test-DefenderExclusion so the
    # idempotent re-run path emits a ::notice:: rather than a redundant
    # "Add succeeded".
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-IsWindowsHost)) {
        return @{ success = $false; reason = "not a Windows host" }
    }
    # WHY pre-check admin: catches non-admin BEFORE the cmdlet throws, so
    # the failure reason does not depend on a locale-specific exception
    # message. Add-MpPreference requires admin on every Windows SKU.
    if (-not (Test-IsAdministrator)) {
        return @{ success = $false; reason = "not running as Administrator; Add-MpPreference requires elevation" }
    }
    try {
        Add-MpPreference -ExclusionPath $Path -ErrorAction Stop
        # Invalidate the cached pref so subsequent Test-DefenderExclusion
        # calls see the freshly added entry.
        $script:DxmDefenderPref = $null
        return @{ success = $true; reason = "Add-MpPreference applied to $Path" }
    } catch {
        $message = $_.Exception.Message
        if (Test-IsAccessDeniedException -ErrorRecord $_) {
            return @{ success = $false; reason = "access denied (Defender ExclusionPath requires admin): $message" }
        }
        return @{ success = $false; reason = "Add-MpPreference threw: $message" }
    }
}

function Test-PowerShell7Installed {
    # Returns $true iff Get-Command pwsh resolves. Note: this script itself
    # runs in either Windows PowerShell 5.1 or PowerShell 7+ -- it does NOT
    # require pwsh to load. The check is purely to ensure subsequent Unity
    # CI steps (which DO `shell: pwsh`) will find it on PATH.
    if (-not (Test-IsWindowsHost)) {
        return $true
    }
    try {
        $cmd = Get-Command -Name 'pwsh' -ErrorAction SilentlyContinue
        return ($null -ne $cmd)
    } catch {
        return $false
    }
}

function Install-PowerShell7 {
    # Installs PowerShell 7 via winget. We use winget specifically because:
    #  1. It is the Microsoft-blessed package manager (no third-party sites).
    #  2. With --scope user, it puts pwsh on the runner-service user's PATH
    #     -- exactly where subsequent CI steps need it, and the user-scope
    #     install does NOT require admin elevation (works under the
    #     NETWORK SERVICE / local user the runner usually runs as).
    #  3. It accepts --silent + --accept-source-agreements + --accept-package-agreements
    #     to run non-interactively (required for CI).
    # Returns @{ success; reason }. NEVER throws.
    if (-not (Test-IsWindowsHost)) {
        return @{ success = $false; reason = "not a Windows host" }
    }

    $winget = Get-Command -Name 'winget' -ErrorAction SilentlyContinue
    if (-not $winget) {
        return @{ success = $false; reason = "winget is not on PATH; install App Installer (winget) from the Microsoft Store, or manually download pwsh from https://github.com/PowerShell/PowerShell/releases" }
    }

    try {
        # WHY $wingetArgs (not $args): $args is a PowerShell AUTOMATIC
        # variable (reserved for positional inputs); reassigning it inside
        # a function silently clobbers caller-side $args. Renamed to avoid
        # the foot-gun (F5).
        # WHY --scope user: see function header.
        $wingetArgs = @(
            'install',
            '--id', 'Microsoft.PowerShell',
            '--source', 'winget',
            '--scope', 'user',
            '--silent',
            '--accept-source-agreements',
            '--accept-package-agreements'
        )
        $process = Start-Process -FilePath $winget.Source -ArgumentList $wingetArgs -PassThru -Wait -NoNewWindow -ErrorAction Stop
        $code = $process.ExitCode
        # C6 (final-review nit): winget's User-scope install updates the
        # User PATH registry value, but the CURRENT process's $env:Path was
        # captured at spawn and isn't refreshed. The post-install
        # Test-PowerShell7Installed re-detect via `Get-Command pwsh` would
        # false-negative until the next CI step. Refresh $env:Path from the
        # Machine + User registry views BEFORE the success return so the
        # re-detect sees the newly installed pwsh. Wrap in try/catch:
        # missing/unreadable PATH from either scope is non-fatal (winget
        # itself succeeded; the next process will have the correct PATH).
        if ($code -eq 0 -or $code -eq -1978335189 -or $code -eq -1978335212 -or $code -eq -1978335153 -or $code -eq -1978334975) {
            try {
                $machinePath = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
                $userPath = [System.Environment]::GetEnvironmentVariable('Path', 'User')
                $segments = @($env:Path, $machinePath, $userPath) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
                $env:Path = ($segments -join ';')
            } catch { }
        }
        if ($code -eq 0) {
            return @{ success = $true; reason = "winget install Microsoft.PowerShell exit 0" }
        }
        # WHY these specific codes are success: winget treats "already
        # installed / no applicable update / etc." as nonzero exits.
        # Idempotent re-runs of this bootstrap MUST not flip the prereq to
        # 'failed' just because pwsh is already there. Reference:
        #   https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md
        # The decimal values below are the signed-int form of the original
        # 0x8A150xxx codes (winget propagates HRESULT via process exit).
        switch ($code) {
            -1978335189 {
                # 0x8A15002B APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE
                return @{ success = $true; reason = "winget reports no applicable update (exit $code; already at latest, idempotent)" }
            }
            -1978335212 {
                # 0x8A150014 APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER
                return @{ success = $true; reason = "winget reports no applicable installer (exit $code; treated as already-installed for idempotency)" }
            }
            -1978335153 {
                # 0x8A15004F APPINSTALLER_CLI_ERROR_NO_APPLICABLE_UPGRADE
                return @{ success = $true; reason = "winget reports no applicable upgrade (exit $code; already at latest, idempotent)" }
            }
            -1978334975 {
                # 0x8A150101 APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE (alt path)
                return @{ success = $true; reason = "winget reports update not applicable (exit $code; already installed, idempotent)" }
            }
            default {
                return @{ success = $false; reason = "winget install Microsoft.PowerShell exited with code $code" }
            }
        }
    } catch {
        return @{ success = $false; reason = "Start-Process winget threw: $($_.Exception.Message)" }
    }
}

function Test-UcrtPresent {
    # The Universal C Runtime (UCRT) ships preinstalled on Windows 10+/Server
    # 2019+. On those modern hosts UCRT is always present and this returns
    # $true silently. On downlevel hosts (Windows 7/8/Server 2012R2) we
    # check for KB2999226 via Get-HotFix and return its presence.
    # NEVER throws. Returns $true on non-Windows for the same vacuous-OK
    # reason as Test-VcRedistModernInstalled.
    if (-not (Test-IsWindowsHost)) {
        return $true
    }
    try {
        $os = [Environment]::OSVersion.Version
        # Build is what differentiates Win10+/Server2019+ from older SKUs:
        # OSVersion.Version.Major returns 10 for Win10, Server2016, Server2019, Server2022,
        # Win11. Pre-Win10 returns 6 (Win7/8/Server2012/Server2012R2). On 10+ UCRT
        # is part of the OS image.
        if ($os.Major -ge 10) {
            return $true
        }
        # Downlevel: probe for KB2999226 (UCRT update for Win7SP1/Win8.1/Server2008R2/Server2012R2).
        # Get-HotFix is slow but reliable. The fast WMI alternative requires
        # admin so we accept the cost.
        $kb = Get-HotFix -Id 'KB2999226' -ErrorAction SilentlyContinue
        return ($null -ne $kb)
    } catch {
        return $false
    }
}

function Invoke-BootstrapStep {
    # Generic recording/orchestration wrapper for a single prereq. Always
    # runs Detect; if missing AND not DetectOnly, runs Install + re-Detect.
    # Returns hashtable @{ name; finalState; detail; critical }. NEVER throws.
    #
    #   finalState options:
    #     'ok'             -- present (no install needed) OR install succeeded
    #     'installed'      -- install succeeded this run
    #     'install-failed' -- install path returned success=$false (admin issue,
    #                         download failure, installer exit, etc.)
    #     'missing'        -- DetectOnly mode + detect returned $false
    #     'skipped'        -- intentionally not applicable (e.g. Defender absent)
    #
    # The `critical` flag (default $true) classifies the prereq as either
    # LOAD-BEARING for Unity correctness (Unity won't start without it -- the
    # dispatcher exits 1 on failure) or BEST-EFFORT (perf optimization only;
    # the dispatcher warns but does not fail). Defender exclusions are the
    # canonical best-effort case: they speed up the Android NDK unpack but
    # Unity runs without them. See Invoke-WindowsRunnerBootstrap's exit-code
    # resolution for how this propagates.
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$DetectFn,
        [Parameter(Mandatory = $true)][scriptblock]$InstallFn,
        [bool]$DetectOnly = $false,
        [bool]$Critical = $true
    )

    Write-Host "::group::bootstrap step: $Name"
    try {
        $present = $false
        try {
            $present = [bool](& $DetectFn)
        } catch {
            Write-CiError "$Name detection threw: $($_.Exception.Message)"
            return @{ name = $Name; finalState = 'install-failed'; detail = "detect threw: $($_.Exception.Message)"; critical = $Critical }
        }

        if ($present) {
            Write-CiNotice "$Name already present (no action needed)"
            return @{ name = $Name; finalState = 'ok'; detail = 'already present'; critical = $Critical }
        }

        if ($DetectOnly) {
            Write-CiWarning "$Name is MISSING (DetectOnly mode; not installing)"
            return @{ name = $Name; finalState = 'missing'; detail = 'missing in DetectOnly mode'; critical = $Critical }
        }

        Write-Host "$Name is missing -- attempting install"
        $result = $null
        try {
            $result = & $InstallFn
        } catch {
            Write-CiError "$Name install threw: $($_.Exception.Message)"
            return @{ name = $Name; finalState = 'install-failed'; detail = "install threw: $($_.Exception.Message)"; critical = $Critical }
        }

        if ($null -eq $result -or -not ($result -is [hashtable])) {
            Write-CiError "$Name install function returned unexpected payload (expected hashtable); treating as failure"
            return @{ name = $Name; finalState = 'install-failed'; detail = 'install returned non-hashtable'; critical = $Critical }
        }

        $success = $false
        try { $success = [bool]$result.success } catch { $success = $false }
        $reason = ''
        try { $reason = [string]$result.reason } catch { $reason = '' }

        if (-not $success) {
            Write-CiError "$Name install FAILED: $reason"
            return @{ name = $Name; finalState = 'install-failed'; detail = "install failed: $reason"; critical = $Critical }
        }

        # Re-probe to confirm install actually fixed the detection.
        $postPresent = $false
        try {
            $postPresent = [bool](& $DetectFn)
        } catch {
            Write-CiError "$Name post-install re-detect threw: $($_.Exception.Message)"
            return @{ name = $Name; finalState = 'install-failed'; detail = "post-install detect threw: $($_.Exception.Message)"; critical = $Critical }
        }
        if (-not $postPresent) {
            Write-CiError "$Name install reported success but post-install detection still says missing: $reason"
            return @{ name = $Name; finalState = 'install-failed'; detail = "install ok but re-detect failed; reason: $reason"; critical = $Critical }
        }

        Write-CiNotice "$Name installed: $reason"
        return @{ name = $Name; finalState = 'installed'; detail = $reason; critical = $Critical }
    } finally {
        Write-Host "::endgroup::"
    }
}

function Invoke-DefenderBootstrap {
    # Wraps Defender exclusion logic into one step that produces a single
    # final-state record. Defender is structurally different from the other
    # prereqs: it has N paths to configure rather than 1, and the cmdlet may
    # be missing entirely on hosts without Defender (Server Core, Defender
    # disabled). We handle those branches inline so the per-path failures
    # roll up into a single summary entry.
    #
    # IMPORTANT: this prereq is BEST-EFFORT (critical = $false). Defender
    # exclusions are a perf optimization (faster Android NDK unpack); Unity
    # itself runs fine without them. EVERY return hashtable from this
    # function MUST declare `critical = $false` so the dispatcher's
    # tier-aware exit-code logic treats a Defender failure as a non-gating
    # warning rather than a hard fail. The 2026-05-26 production regression
    # that motivated this: per-job preflight ran as NETWORK SERVICE
    # (non-admin) and Add-MpPreference threw access-denied; the bootstrap
    # treated that as a critical failure and exited 1, failing every Unity
    # cell even though every load-bearing prereq (VC++, long-paths, pwsh,
    # UCRT) was already installed by an operator's manual elevated run.
    #
    # WHY skip-on-non-admin (the first branch below): a non-admin shell
    # cannot manage Defender exclusions at all (Add-MpPreference requires
    # admin on every Windows SKU; Get-MpPreference may report a stale or
    # empty exclusion list to the unprivileged caller). Calling either is
    # pointless and noisy. We short-circuit with a single ::notice:: and
    # return a 'skipped-non-admin' state that the dispatcher recognises as
    # non-failure.
    #
    # WHY [AllowEmptyCollection]: callers @()-wrap an array that may be
    # empty (e.g. when every candidate path was rejected by the allow-list).
    # An empty [string[]] is a valid input -- the explicit "no paths
    # configured" branch handles it -- so a Mandatory+empty combination
    # would otherwise prevent the function from ever being called in that
    # legitimate case (F1).
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Paths,
        [bool]$DetectOnly = $false
    )

    Write-Host "::group::bootstrap step: defender-exclusion"
    try {
        # Non-admin short-circuit: Defender management is admin-only. Bail
        # out BEFORE we touch Test-DefenderAvailable / Test-DefenderExclusion
        # so we never hit a partial/misleading state. This is a notice, not
        # an error -- Defender exclusion is best-effort (critical = $false).
        if (-not (Test-IsAdministrator)) {
            Write-CiNotice "Skipping Defender exclusion: running as non-admin. Defender exclusions are a perf optimization (faster Android NDK unpack) and NOT a correctness requirement for Unity startup. Run the bootstrap from an elevated shell on the host to manage exclusions."
            return @{ name = 'defender-exclusion'; finalState = 'skipped-non-admin'; detail = 'non-admin shell cannot manage Defender exclusions; skipped (best-effort prereq)'; critical = $false }
        }

        if (-not (Test-DefenderAvailable)) {
            Write-CiNotice "Defender not present (Get-MpPreference unavailable); skipping exclusion configuration."
            return @{ name = 'defender-exclusion'; finalState = 'skipped'; detail = 'Defender not available'; critical = $false }
        }

        if ($null -eq $Paths -or $Paths.Length -eq 0) {
            Write-CiNotice "No Defender exclusion paths configured; skipping."
            return @{ name = 'defender-exclusion'; finalState = 'skipped'; detail = 'no paths configured'; critical = $false }
        }

        $missing = New-Object 'System.Collections.Generic.List[string]'
        foreach ($p in $Paths) {
            if (-not (Test-DefenderExclusion -Path $p)) {
                $missing.Add($p) | Out-Null
            } else {
                Write-CiNotice "Defender exclusion already present for $p"
            }
        }

        if ($missing.Count -eq 0) {
            return @{ name = 'defender-exclusion'; finalState = 'ok'; detail = "all $($Paths.Length) path(s) already excluded"; critical = $false }
        }

        if ($DetectOnly) {
            $list = ($missing -join ', ')
            Write-CiWarning "Defender exclusion(s) MISSING (DetectOnly mode): $list"
            return @{ name = 'defender-exclusion'; finalState = 'missing'; detail = "missing in DetectOnly mode: $list"; critical = $false }
        }

        $failures = New-Object 'System.Collections.Generic.List[string]'
        $installed = New-Object 'System.Collections.Generic.List[string]'
        foreach ($p in $missing) {
            $r = Add-DefenderExclusion -Path $p
            if ($r.success) {
                $installed.Add($p) | Out-Null
                Write-CiNotice "Defender exclusion added for $p"
            } else {
                $failures.Add("$p ($($r.reason))") | Out-Null
                Write-CiError "Defender exclusion FAILED for ${p}: $($r.reason)"
            }
        }

        if ($failures.Count -gt 0) {
            return @{ name = 'defender-exclusion'; finalState = 'install-failed'; detail = "failed for $($failures.Count): $($failures -join '; ')"; critical = $false }
        }
        return @{ name = 'defender-exclusion'; finalState = 'installed'; detail = "added: $($installed -join ', ')"; critical = $false }
    } finally {
        Write-Host "::endgroup::"
    }
}

function Invoke-UcrtBootstrap {
    # Wraps the UCRT prereq: silent skip on modern Windows, error with
    # manual-install link on downlevel hosts missing KB2999226. We do NOT
    # auto-install KB2999226 because the MSU URL is host-architecture-specific
    # and the operator action is one-time.
    #
    # UCRT is CRITICAL (critical = $true) on the hosts that need it: Unity
    # links against the Universal C Runtime and will fail to start without
    # it. On modern Windows (Win10+/Server2019+) UCRT is part of the OS
    # image so this step is a silent no-op there; the `critical = $true`
    # flag is propagated either way so the dispatcher's tier-aware exit-code
    # logic treats a downlevel-host miss as a gating failure.
    param([bool]$DetectOnly = $false)

    Write-Host "::group::bootstrap step: ucrt"
    try {
        if (-not (Test-IsWindowsHost)) {
            Write-CiNotice "UCRT step skipped (not Windows)"
            return @{ name = 'ucrt'; finalState = 'skipped'; detail = 'not Windows'; critical = $true }
        }
        $os = [Environment]::OSVersion.Version
        if ($os.Major -ge 10) {
            Write-CiNotice "UCRT shipped with Windows $($os) (Win10+/Server2019+ has UCRT built in); no action needed."
            return @{ name = 'ucrt'; finalState = 'ok'; detail = "Windows $os has UCRT built in"; critical = $true }
        }
        if (Test-UcrtPresent) {
            Write-CiNotice "UCRT (KB2999226) detected on downlevel Windows $os"
            return @{ name = 'ucrt'; finalState = 'ok'; detail = "KB2999226 present on $os"; critical = $true }
        }
        # Downlevel + KB missing -- this is an operator action; emit a
        # clear ::error:: with the manual-install link. Do not try to
        # auto-download an MSU (size + arch + KB ID varies per host).
        $link = 'https://support.microsoft.com/help/2999226'
        Write-CiError "UCRT (KB2999226) is missing on downlevel Windows $os. Unity may fail to start without it. Install KB2999226 manually from $link (operator action; this bootstrap does NOT auto-install MSU files)."
        if ($DetectOnly) {
            return @{ name = 'ucrt'; finalState = 'missing'; detail = "KB2999226 missing on $os (DetectOnly)"; critical = $true }
        }
        return @{ name = 'ucrt'; finalState = 'install-failed'; detail = "KB2999226 missing on $os (manual install required)"; critical = $true }
    } finally {
        Write-Host "::endgroup::"
    }
}

function Format-BootstrapSummary {
    # Produces the single-line ::notice:: summary expected by the
    # workflow/composite. Each entry is "name=state"; states are short
    # ('ok', 'installed', 'install-failed', 'missing', 'skipped',
    # 'skipped-non-admin'). Best-effort (non-critical) prereqs are
    # suffixed with `*` so the operator can immediately distinguish a
    # load-bearing failure from a perf-optimization miss. A separate
    # ::notice:: legend line explains the suffix; the dispatcher emits
    # that legend immediately after the summary.
    # WHY Write-CiWarning on the catch path (F22): silently swallowing
    # malformed results made debugging "summary shows no entries" hard.
    # Surface the malformed object so the operator can see it.
    param([Parameter(Mandatory = $true)][object[]]$Results)

    $parts = New-Object 'System.Collections.Generic.List[string]'
    foreach ($r in $Results) {
        if ($null -eq $r) { continue }
        $name = ''
        $state = ''
        # Default to critical=$true for any result lacking the flag --
        # safest default if a future caller forgets to propagate it.
        $isCritical = $true
        try {
            $name = [string]$r.name
            $state = [string]$r.finalState
            try {
                if ($null -ne $r.critical) {
                    $isCritical = [bool]$r.critical
                }
            } catch {
                $isCritical = $true
            }
        } catch {
            Write-CiWarning "Format-BootstrapSummary: malformed result entry: $r ($($_.Exception.Message))"
            $name = '<unknown>'
            $state = '<unknown>'
        }
        if ([string]::IsNullOrWhiteSpace($name)) { $name = '<unknown>' }
        if ([string]::IsNullOrWhiteSpace($state)) { $state = '<unknown>' }
        $suffix = if ($isCritical) { '' } else { '*' }
        $parts.Add("$name=$state$suffix") | Out-Null
    }
    return ($parts -join ' ')
}

# --- Top-level dispatcher -----------------------------------------------------
# This is the ONLY place that invokes the prereq functions. Everything above is
# Linux-parse-safe (no registry, no Defender, no winget calls at module load).
# The dispatcher early-returns on non-Windows with a clear ::error::, so the
# top-level body never reaches the install paths on Linux/macOS.

function Invoke-WindowsRunnerBootstrap {
    param(
        [Parameter(Mandatory = $true)][bool]$DetectOnly,
        [Parameter(Mandatory = $true)][string]$UnityInstallRoot,
        [Parameter(Mandatory = $true)][string]$VcRedistUrl,
        [Parameter(Mandatory = $true)][string]$VcRedist2010Url,
        [Parameter(Mandatory = $true)][int]$DownloadTimeoutSeconds,
        [Parameter(Mandatory = $true)][int]$InstallTimeoutSeconds
    )

    Set-StrictMode -Version Latest

    if (-not (Test-IsWindowsHost)) {
        # Hard-fail on non-Windows EXCEPT in DetectOnly mode: tests want to
        # confirm the script parses + the gate fires. DetectOnly emits a
        # ::notice:: instead so test runs on Linux don't trip an ::error::.
        if ($DetectOnly) {
            Write-CiNotice "bootstrap-windows-runner.ps1 detected non-Windows host; nothing to do in DetectOnly mode. skipping (not Windows)"
            return 0
        }
        Write-CiError "bootstrap-windows-runner.ps1 is Windows-only. Detected directory separator '$([System.IO.Path]::DirectorySeparatorChar)' which is not '\'. Run this on a self-hosted Windows runner or via .github/workflows/runner-bootstrap.yml."
        return 1
    }

    $mode = if ($DetectOnly) { 'DetectOnly' } else { 'Install' }
    $isAdmin = Test-IsAdministrator
    Write-CiNotice "bootstrap-windows-runner.ps1 mode=$mode admin=$isAdmin UnityInstallRoot=$UnityInstallRoot"
    if (-not $isAdmin -and -not $DetectOnly) {
        # Not a hard-fail in itself (PowerShell 7 install via winget per-user
        # works without admin; vcredist/long-paths/Defender each emit their
        # own specific access-denied diagnostic when applicable), but flag
        # explicitly so any subsequent 'access denied' has context. List
        # ALL admin-requiring prereqs: critical (VC++ 2010 install,
        # VC++ 2015-2022 install, LongPathsEnabled, all HKLM writes) AND
        # best-effort (Defender, which is now SKIPPED on non-admin per the
        # 2026-05-26 tiering fix -- prior code treated Defender's non-admin
        # failure as a critical failure and exited 1).
        Write-CiWarning "bootstrap-windows-runner.ps1 running NON-admin. Critical prereqs that require HKLM writes (VC++ 2010 redist install, VC++ 2015-2022 redist install, LongPathsEnabled) will fail with Access Denied; best-effort prereqs that require admin (Defender exclusions) are SKIPPED with a notice. To install missing critical prereqs, run from an elevated shell, OR trigger .github/workflows/runner-bootstrap.yml from the Actions UI."
    }

    $results = New-Object 'System.Collections.Generic.List[object]'

    # Step 1: VC++ 2010 redist (the load-bearing missing DLL identified in
    # production run 70874414898: MSVCP100.dll). Older runtime first by
    # convention -- newer-shadows-older means installing 2010 first cannot
    # break the modern install, while installing the modern one first MIGHT
    # cause the 2010 installer to refuse on a partially-shared registry view.
    # CRITICAL: Unity 2021/2022/6000 won't start without MSVCP100.dll +
    # MSVCR100.dll on the OS. SEPARATE Microsoft package from the 2015-2022
    # generation -- the modern installer never lays these down.
    $results.Add((
            Invoke-BootstrapStep -Name 'vcredist-2010' -DetectOnly $DetectOnly -Critical $true `
                -DetectFn { ([bool]((Test-VcRedist2010Installed).installed)) } `
                -InstallFn { Install-VcRedist2010 -Url $VcRedist2010Url -DownloadTimeoutSeconds $DownloadTimeoutSeconds -InstallTimeoutSeconds $InstallTimeoutSeconds }
        )) | Out-Null

    # Step 2: VC++ 2015-2022 redist (THE root cause of the DAD-MACHINE failure).
    # CRITICAL: Unity won't start without VCRUNTIME140*.dll on the OS.
    $results.Add((
            Invoke-BootstrapStep -Name 'vcredist-2015-2022' -DetectOnly $DetectOnly -Critical $true `
                -DetectFn { Test-VcRedistModernInstalled } `
                -InstallFn { Install-VcRedistModern -Url $VcRedistUrl -DownloadTimeoutSeconds $DownloadTimeoutSeconds -InstallTimeoutSeconds $InstallTimeoutSeconds }
        )) | Out-Null

    # Step 3: Long-paths.
    # CRITICAL: the Android NDK unpack hits MAX_PATH at ~240 chars without it.
    $results.Add((
            Invoke-BootstrapStep -Name 'long-paths' -DetectOnly $DetectOnly -Critical $true `
                -DetectFn { Test-LongPathsEnabled } `
                -InstallFn { Enable-LongPaths }
        )) | Out-Null

    # Step 4: Defender exclusion (N paths -> 1 step).
    # BEST-EFFORT (critical = $false, set inside Invoke-DefenderBootstrap):
    # Defender exclusions are a perf optimization (faster Android NDK unpack)
    # and NOT a correctness requirement for Unity startup. The dispatcher's
    # tier-aware exit-code logic treats a failure here as a non-gating
    # warning. The @()-wrap is REQUIRED: Get-DefenderExclusionPaths returns
    # ToArray() on a List[string], which unwraps to AutomationNull when
    # empty. Without the @() splat, Invoke-DefenderBootstrap's Mandatory
    # [string[]]$Paths would fail to bind on the empty-array path (F1).
    $paths = @(Get-DefenderExclusionPaths -UnityInstallRoot $UnityInstallRoot)
    $results.Add((Invoke-DefenderBootstrap -Paths $paths -DetectOnly $DetectOnly)) | Out-Null

    # Step 5: PowerShell 7.
    # CRITICAL: many CI composites use `shell: pwsh` -- without it the very
    # next step would fail with `pwsh: command not found`.
    $results.Add((
            Invoke-BootstrapStep -Name 'pwsh' -DetectOnly $DetectOnly -Critical $true `
                -DetectFn { Test-PowerShell7Installed } `
                -InstallFn { Install-PowerShell7 }
        )) | Out-Null

    # Step 6: UCRT (modern Windows: silent OK; downlevel: ::error:: + manual link).
    # CRITICAL on the hosts that need it (downlevel Win/Server); silent
    # no-op on Win10+/Server2019+ which ship UCRT preinstalled.
    $results.Add((Invoke-UcrtBootstrap -DetectOnly $DetectOnly)) | Out-Null

    # Summary line + exit-code resolution. Best-effort prereqs are suffixed
    # with `*` in the summary; emit a legend so the operator immediately
    # understands the distinction without grepping the script.
    $summary = Format-BootstrapSummary -Results $results
    Write-CiNotice "bootstrap-windows-runner summary: $summary"
    # WHY a separate legend line: the summary is already long; splicing the
    # explanation inline would push the readable name=state pairs off-screen
    # on a narrow CI log view. A dedicated ::notice:: keeps both lines short.
    Write-CiNotice "(* = best-effort prereq; not load-bearing for Unity correctness)"

    # Tier-aware exit-code resolution. Failures are partitioned into:
    #   - CRITICAL: load-bearing for Unity correctness (vcredist-2010,
    #     vcredist-2015-2022, long-paths, pwsh, ucrt). A critical failure exits
    #     1 and the operator MUST remediate (elevated re-run or
    #     workflow_dispatch) before the next Unity job can pass.
    #   - BEST-EFFORT: perf optimizations or convenience (defender-exclusion).
    #     A best-effort failure produces a ::warning:: and the dispatcher
    #     continues -- Unity correctness is unaffected. This is the
    #     2026-05-26 fix that unblocks the per-job preflight when it runs
    #     as NETWORK SERVICE (non-admin) on the self-hosted runner.
    # Default unset `critical` to $true (safest: a future maintainer who
    # forgets to set the flag gets the gating behaviour, NOT the silent-pass).
    $criticalFailed = New-Object 'System.Collections.Generic.List[object]'
    $bestEffortFailed = New-Object 'System.Collections.Generic.List[object]'
    $anyMissing = $false
    foreach ($r in $results) {
        # F16: guard against $null in the result list. Format-BootstrapSummary
        # already skips $null but the exit-code resolution did not.
        if ($null -eq $r) { continue }
        $state = ''
        $isCritical = $true
        try {
            $state = [string]$r.finalState
            try {
                if ($null -ne $r.critical) {
                    $isCritical = [bool]$r.critical
                }
            } catch {
                # Treat a missing/unreadable critical flag as critical -- the
                # safest default (it preserves the pre-tiering behaviour for
                # any caller that hasn't been migrated yet).
                $isCritical = $true
            }
        } catch {
            # Treat a malformed entry as a critical failure regardless of mode.
            # Surface a warning so the operator sees we recovered from it.
            Write-CiWarning "bootstrap-windows-runner: malformed result entry while computing exit code; treating as critical failure: $($_.Exception.Message)"
            $criticalFailed.Add($r) | Out-Null
            continue
        }
        if ($state -eq 'install-failed' -or $state -like '*failed') {
            if ($isCritical -ne $false) {
                $criticalFailed.Add($r) | Out-Null
            } else {
                $bestEffortFailed.Add($r) | Out-Null
            }
        } elseif ($state -eq 'missing') {
            $anyMissing = $true
        }
    }

    if ($bestEffortFailed.Count -gt 0) {
        # Non-gating warning: log each best-effort failure so the operator
        # has a paper trail, but do NOT trigger exit 1. The summary line
        # already marks these with `*` so a quick scan reveals the tier.
        foreach ($f in $bestEffortFailed) {
            $fName = '<unknown>'
            $fState = '<unknown>'
            try { $fName = [string]$f.name } catch { }
            try { $fState = [string]$f.finalState } catch { }
            Write-CiWarning "best-effort prereq '$fName' did not complete ($fState); continuing because it is not load-bearing for Unity correctness."
        }
    }

    if ($criticalFailed.Count -gt 0) {
        Write-CiError "bootstrap-windows-runner: one or more CRITICAL prereqs failed to install. See individual ::error:: lines above. To remediate: run this script from an elevated PowerShell, or trigger .github/workflows/runner-bootstrap.yml from the Actions UI."
        return 1
    }
    if ($DetectOnly -and $anyMissing) {
        Write-CiWarning "bootstrap-windows-runner (DetectOnly): one or more prereqs are missing; exit 2."
        return 2
    }
    return 0
}

# Only execute the dispatcher when this script is INVOKED -- not when it is
# dot-sourced by tests for function inspection. PSScriptRoot is reliable enough
# here; tests that want to inspect functions can dot-source from a different
# script.
$invokedAsScript = $MyInvocation.InvocationName -ne '' -and $MyInvocation.InvocationName -ne '.'

if ($invokedAsScript) {
    # Per the import contract documented in .DESCRIPTION: session-level mode
    # changes happen ONLY here, inside the invocation gate. Dot-source paths
    # (tests doing `. ./bootstrap-windows-runner.ps1`) bypass this block and
    # therefore do NOT see Set-StrictMode / $EAP / $PSNCUEAP leak.
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    # Same rationale as scripts/unity/ensure-editor.ps1 lines 42-52: pin
    # $PSNativeCommandUseErrorActionPreference = $false so LASTEXITCODE-based
    # handling is authoritative across hosts/versions. PS 5.1 lacks this
    # variable; the assignment is harmless + StrictMode-safe there (it just
    # creates a regular variable in scope).
    $PSNativeCommandUseErrorActionPreference = $false

    $exit = Invoke-WindowsRunnerBootstrap `
        -DetectOnly:$DetectOnly.IsPresent `
        -UnityInstallRoot $UnityInstallRoot `
        -VcRedistUrl $VcRedistUrl `
        -VcRedist2010Url $VcRedist2010Url `
        -DownloadTimeoutSeconds $DownloadTimeoutSeconds `
        -InstallTimeoutSeconds $InstallTimeoutSeconds
    exit $exit
}
