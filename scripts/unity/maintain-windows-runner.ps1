#Requires -Version 5.1
<#
.SYNOPSIS
    Canonical self-hosted Windows runner maintenance entrypoint for Unity CI.

.DESCRIPTION
    Runs host prerequisite bootstrap first, then audits or repairs the Unity
    editor/module desired state for every requested Unity version. This script
    does not activate Unity, does not require Unity license secrets, and does
    not acquire the organization Unity build lock.

    In repair mode, ensure-editor.ps1 is allowed to install or repair editors.
    In DetectOnly mode, ensure-editor.ps1 is invoked with -RequireHealthyExisting
    so audits fail fast without mutating the runner.
#>
[CmdletBinding()]
[OutputType([int])]
param(
    [switch]$DetectOnly,

    [string[]]$UnityVersions = @('2021.3.45f1', '2022.3.45f1', '6000.3.16f1'),

    [ValidateSet('EditorOnly', 'StandaloneWindowsIl2Cpp', 'Android', 'Full')]
    [string]$ProvisioningProfile = 'StandaloneWindowsIl2Cpp',

    [string]$InstallRoot = $(if ($env:UNITY_EDITOR_INSTALL_ROOT) { $env:UNITY_EDITOR_INSTALL_ROOT } else { 'C:\Unity\Editors' }),

    [switch]$Force,

    [string]$DiagnosticsRoot = $(if ($env:DXM_RUNNER_MAINTENANCE_DIAGNOSTICS_ROOT) { $env:DXM_RUNNER_MAINTENANCE_DIAGNOSTICS_ROOT } else { '' })
)

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
    return ([System.IO.Path]::DirectorySeparatorChar -eq '\')
}

function Test-IsAdministrator {
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

function Get-RepositoryRef {
    param([string]$Root)

    $result = @{
        sha = ''
        ref = ''
    }

    try {
        $sha = & git -C $Root rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0) {
            $result['sha'] = [string]$sha
        }
    } catch { }

    try {
        $ref = & git -C $Root rev-parse --abbrev-ref HEAD 2>$null
        if ($LASTEXITCODE -eq 0) {
            $result['ref'] = [string]$ref
        }
    } catch { }

    if ([string]::IsNullOrWhiteSpace($result['sha'])) {
        $result['sha'] = $env:GITHUB_SHA
    }
    if ([string]::IsNullOrWhiteSpace($result['ref'])) {
        $result['ref'] = $env:GITHUB_REF
    }

    return $result
}

function Get-RunnerBusyProcesses {
    $names = @('Runner.Worker', 'Unity')
    $busy = New-Object System.Collections.Generic.List[object]
    foreach ($name in $names) {
        try {
            $processes = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
            foreach ($process in $processes) {
                $busy.Add([pscustomobject]@{
                        name = $process.ProcessName
                        id   = $process.Id
                    }) | Out-Null
            }
        } catch { }
    }
    return @($busy.ToArray())
}

function New-MaintenanceSummary {
    param(
        [string]$Mode,
        [string]$DiagnosticsRoot,
        [string]$RepoRoot
    )

    $repo = @(Get-RepositoryRef -Root $RepoRoot)[0]
    return [ordered]@{
        runnerName            = $env:RUNNER_NAME
        machineName           = $env:COMPUTERNAME
        isAdmin               = [bool](Test-IsAdministrator)
        repoSha               = $repo['sha']
        repoRef               = $repo['ref']
        mode                  = $Mode
        startedUtc            = [DateTime]::UtcNow.ToString('o')
        finishedUtc           = ''
        installRoot           = $InstallRoot
        provisioningProfile   = $ProvisioningProfile
        unityVersions         = @($UnityVersions)
        diagnosticsRoot       = $DiagnosticsRoot
        hostBootstrapExit     = $null
        versionSummaries      = @()
        finalClassification   = 'not-finished'
        missingModules        = @()
        startupProbeLogPath   = ''
        exitClass             = 'not-finished'
        exitCode              = $null
    }
}

function Get-MissingModulesFromSummary {
    param($Summary)

    $missing = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Summary -or $null -eq $Summary.requiredModulePresence) {
        return @()
    }

    $properties = @($Summary.requiredModulePresence.PSObject.Properties)
    foreach ($property in $properties) {
        if ($property.Value -eq $false) {
            $missing.Add([string]$property.Name) | Out-Null
        }
    }
    return @($missing.ToArray())
}

function Read-ProvisioningSummary {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    try {
        return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    } catch {
        Write-CiWarning "Could not parse provisioning summary at ${Path}: $($_.Exception.Message)"
        return $null
    }
}

function Write-MaintenanceSummary {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)][string]$Root
    )

    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $jsonPath = Join-Path $Root 'runner-maintenance-summary.json'
    $textPath = Join-Path $Root 'runner-maintenance-summary.txt'

    $Summary.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $Summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    $lines = @(
        'DxMessaging runner maintenance summary',
        "classification=$($Summary.finalClassification)",
        "exitClass=$($Summary.exitClass)",
        "exitCode=$($Summary.exitCode)",
        "mode=$($Summary.mode)",
        "runnerName=$($Summary.runnerName)",
        "machineName=$($Summary.machineName)",
        "isAdmin=$($Summary.isAdmin)",
        "repoSha=$($Summary.repoSha)",
        "repoRef=$($Summary.repoRef)",
        "hostBootstrapExit=$($Summary.hostBootstrapExit)",
        "installRoot=$($Summary.installRoot)",
        "provisioningProfile=$($Summary.provisioningProfile)",
        "unityVersions=$($Summary.unityVersions -join ',')",
        "missingModules=$($Summary.missingModules -join ',')",
        "startupProbeLogPath=$($Summary.startupProbeLogPath)"
    )
    foreach ($versionSummary in @($Summary.versionSummaries)) {
        $lines += "version[$($versionSummary.unityVersion)] classification=$($versionSummary.finalClassification) exit=$($versionSummary.exitCode) summary=$($versionSummary.summaryPath)"
    }
    $lines | Set-Content -LiteralPath $textPath -Encoding UTF8

    Write-CiNotice "Runner maintenance summary: $jsonPath"
}

function Invoke-WindowsRunnerMaintenance {
    param(
        [switch]$DetectOnly,
        [string[]]$UnityVersions = @('2021.3.45f1', '2022.3.45f1', '6000.3.16f1'),
        [ValidateSet('EditorOnly', 'StandaloneWindowsIl2Cpp', 'Android', 'Full')]
        [string]$ProvisioningProfile = 'StandaloneWindowsIl2Cpp',
        [string]$InstallRoot = $(if ($env:UNITY_EDITOR_INSTALL_ROOT) { $env:UNITY_EDITOR_INSTALL_ROOT } else { 'C:\Unity\Editors' }),
        [switch]$Force,
        [string]$DiagnosticsRoot
    )

    if (-not (Test-IsWindowsHost)) {
        Write-CiNotice "maintain-windows-runner.ps1 detected non-Windows host; nothing to maintain."
        return 0
    }

    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    if ([string]::IsNullOrWhiteSpace($DiagnosticsRoot)) {
        $DiagnosticsRoot = Join-Path $repoRoot '.artifacts\runner-maintenance'
    }
    New-Item -ItemType Directory -Force -Path $DiagnosticsRoot | Out-Null

    $mode = if ($DetectOnly) { 'audit' } else { 'repair' }
    $summary = New-MaintenanceSummary -Mode $mode -DiagnosticsRoot $DiagnosticsRoot -RepoRoot $repoRoot

    $mutex = $null
    $hasMutex = $false
    try {
        $mutex = New-Object System.Threading.Mutex -ArgumentList $false, 'Global\DxMessagingUnityRunnerMaintenance'
        $hasMutex = $mutex.WaitOne(0)
        if (-not $hasMutex) {
            Write-CiWarning "Runner maintenance is already running; busy/skipped."
            $summary.finalClassification = 'busy-skipped'
            $summary.exitClass = 'mutex-busy'
            $summary.exitCode = 3
            Write-MaintenanceSummary -Summary $summary -Root $DiagnosticsRoot
            return 3
        }

        $busyProcesses = @(Get-RunnerBusyProcesses)
        if ($busyProcesses.Count -gt 0 -and -not $Force) {
            $details = ($busyProcesses | ForEach-Object { "$($_.name)[$($_.id)]" }) -join ', '
            Write-CiWarning "Runner maintenance skipped because runner/editor processes are active: $details. Re-run with -Force only from an intentionally dedicated maintenance window."
            $summary.finalClassification = 'busy-skipped'
            $summary.exitClass = 'runner-busy'
            $summary.exitCode = 4
            Write-MaintenanceSummary -Summary $summary -Root $DiagnosticsRoot
            return 4
        }

        $bootstrap = Join-Path $PSScriptRoot 'bootstrap-windows-runner.ps1'
        if (-not (Test-Path -LiteralPath $bootstrap -PathType Leaf)) {
            throw "bootstrap-windows-runner.ps1 not found at $bootstrap"
        }

        Write-CiNotice "Running Windows runner host bootstrap before Unity provisioning (mode=$mode)."
        $global:LASTEXITCODE = 0
        if ($DetectOnly) {
            & $bootstrap -DetectOnly
        } else {
            & $bootstrap
        }
        $summary.hostBootstrapExit = $LASTEXITCODE

        $ensureEditor = Join-Path $PSScriptRoot 'ensure-editor.ps1'
        if (-not (Test-Path -LiteralPath $ensureEditor -PathType Leaf)) {
            throw "ensure-editor.ps1 not found at $ensureEditor"
        }

        $anyProvisioningFailed = $false
        $allMissingModules = New-Object System.Collections.Generic.List[string]
        foreach ($version in @($UnityVersions)) {
            $versionDiagnosticsRoot = Join-Path $DiagnosticsRoot $version
            New-Item -ItemType Directory -Force -Path $versionDiagnosticsRoot | Out-Null
            $summaryPath = Join-Path $versionDiagnosticsRoot 'ensure-editor-summary.json'
            $ensureArgs = @{
                UnityVersion         = $version
                InstallRoot          = $InstallRoot
                CiManagedOnly        = $true
                ProvisioningProfile = $ProvisioningProfile
                DiagnosticsPath      = $summaryPath
            }
            if ($DetectOnly) {
                $ensureArgs.RequireHealthyExisting = $true
            }

            $versionExit = 0
            $editorPath = ''
            try {
                $global:LASTEXITCODE = 0
                $editorPath = (& $ensureEditor @ensureArgs | Select-Object -Last 1)
                $versionExit = $LASTEXITCODE
                if ($null -eq $versionExit) {
                    $versionExit = 0
                }
            } catch {
                $versionExit = 1
                Write-CiError "Unity $version maintenance failed: $($_.Exception.Message)"
            }

            $provisioningSummary = Read-ProvisioningSummary -Path $summaryPath
            $hasProvisioningSummary = $null -ne $provisioningSummary
            $classification = if ($provisioningSummary -and $provisioningSummary.finalClassification) {
                [string]$provisioningSummary.finalClassification
            } elseif ($versionExit -eq 0) {
                'success'
            } else {
                'failed'
            }
            $missing = @(Get-MissingModulesFromSummary -Summary $provisioningSummary)
            foreach ($missingModule in $missing) {
                $allMissingModules.Add("$version/$missingModule") | Out-Null
            }
            if ($classification -ne 'success' -or (-not $hasProvisioningSummary -and $versionExit -ne 0)) {
                $anyProvisioningFailed = $true
            }

            $probeLog = Join-Path (Join-Path $InstallRoot '_probes') "$version-startup-probe.log"
            if ([string]::IsNullOrWhiteSpace($summary.startupProbeLogPath) -and (Test-Path -LiteralPath $probeLog -PathType Leaf)) {
                $summary.startupProbeLogPath = $probeLog
            }

            $summary.versionSummaries += [pscustomobject]@{
                unityVersion        = $version
                exitCode            = $versionExit
                finalClassification = $classification
                editorPath          = $editorPath
                summaryPath         = $summaryPath
                textSummaryPath     = (Join-Path $versionDiagnosticsRoot 'ensure-editor-summary.txt')
                startupProbeLogPath = $probeLog
                missingModules      = $missing
            }
        }

        $summary.missingModules = @($allMissingModules.ToArray())
        if ($summary.hostBootstrapExit -ne 0) {
            $summary.finalClassification = 'host-bootstrap-failed'
            $summary.exitClass = 'host-bootstrap-failed'
            $summary.exitCode = [int]$summary.hostBootstrapExit
        } elseif ($anyProvisioningFailed) {
            $summary.finalClassification = if ($DetectOnly) { 'audit-missing' } else { 'provisioning-failed' }
            $summary.exitClass = $summary.finalClassification
            $summary.exitCode = 2
        } else {
            $summary.finalClassification = 'success'
            $summary.exitClass = 'success'
            $summary.exitCode = 0
        }

        Write-MaintenanceSummary -Summary $summary -Root $DiagnosticsRoot
        return [int]$summary.exitCode
    } catch {
        Write-CiError "Runner maintenance failed: $($_.Exception.Message)"
        $summary.finalClassification = 'failed'
        $summary.exitClass = 'failed'
        $summary.exitCode = 1
        Write-MaintenanceSummary -Summary $summary -Root $DiagnosticsRoot
        return 1
    } finally {
        if ($hasMutex -and $mutex) {
            $mutex.ReleaseMutex() | Out-Null
        }
        if ($mutex) {
            $mutex.Dispose()
        }
    }
}

$invokedAsScript = $MyInvocation.InvocationName -ne '' -and $MyInvocation.InvocationName -ne '.'

if ($invokedAsScript) {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $PSNativeCommandUseErrorActionPreference = $false

    $exit = Invoke-WindowsRunnerMaintenance `
        -DetectOnly:$DetectOnly.IsPresent `
        -UnityVersions $UnityVersions `
        -ProvisioningProfile $ProvisioningProfile `
        -InstallRoot $InstallRoot `
        -Force:$Force.IsPresent `
        -DiagnosticsRoot $DiagnosticsRoot
    exit $exit
}
