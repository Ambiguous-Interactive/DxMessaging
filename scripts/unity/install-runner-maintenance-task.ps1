#Requires -Version 5.1
<#
.SYNOPSIS
    Install or update the DxMessaging Windows runner maintenance scheduled task.
#>
[CmdletBinding()]
[OutputType([int])]
param(
    [string]$RepositoryUrl = $(if ($env:DXM_RUNNER_MAINTENANCE_REPOSITORY_URL) { $env:DXM_RUNNER_MAINTENANCE_REPOSITORY_URL } else { 'https://github.com/Ambiguous-Interactive/DxMessaging.git' }),

    [string]$Branch = $(if ($env:DXM_RUNNER_MAINTENANCE_BRANCH) { $env:DXM_RUNNER_MAINTENANCE_BRANCH } else { 'master' }),

    [string]$MaintenanceRoot = 'C:\ProgramData\DxMessaging\runner-maintenance',

    [string]$TaskName = 'DxMessaging Runner Maintenance',

    [string[]]$UnityVersions = @('2021.3.45f1', '2022.3.45f1', '6000.3.16f1'),

    [ValidateSet('EditorOnly', 'StandaloneWindowsIl2Cpp', 'Android', 'Full')]
    [string]$ProvisioningProfile = 'StandaloneWindowsIl2Cpp',

    [string]$InstallRoot = $(if ($env:UNITY_EDITOR_INSTALL_ROOT) { $env:UNITY_EDITOR_INSTALL_ROOT } else { 'C:\Unity\Editors' }),

    [switch]$Force
)

function Write-CiNotice {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::notice::$Message"
}

function Write-CiError {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::error::$Message"
}

function Test-IsWindowsHost {
    return ([System.IO.Path]::DirectorySeparatorChar -eq '\')
}

function Quote-TaskArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '"' + ($Value -replace '"', '\"') + '"'
}

function Invoke-GitChecked {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $exitCode = 0
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $global:LASTEXITCODE = 0
        & git @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $exitCode"
    }
}

function Install-RunnerMaintenanceTask {
    param(
        [string]$RepositoryUrl,
        [string]$Branch,
        [string]$MaintenanceRoot,
        [string]$TaskName,
        [string[]]$UnityVersions,
        [string]$ProvisioningProfile,
        [string]$InstallRoot,
        [switch]$Force
    )

    if (-not (Test-IsWindowsHost)) {
        Write-CiNotice "install-runner-maintenance-task.ps1 detected non-Windows host; nothing to install."
        return 0
    }

    New-Item -ItemType Directory -Force -Path $MaintenanceRoot | Out-Null
    $repoDir = Join-Path $MaintenanceRoot 'repo'
    $diagnosticsRoot = Join-Path $MaintenanceRoot 'diagnostics'
    New-Item -ItemType Directory -Force -Path $diagnosticsRoot | Out-Null

    if (-not (Test-Path -LiteralPath (Join-Path $repoDir '.git') -PathType Container)) {
        if (Test-Path -LiteralPath $repoDir) {
            Remove-Item -LiteralPath $repoDir -Recurse -Force
        }
        Invoke-GitChecked -Arguments @('clone', '--branch', $Branch, '--single-branch', $RepositoryUrl, $repoDir) -WorkingDirectory $MaintenanceRoot
    } else {
        Invoke-GitChecked -Arguments @('fetch', '--prune', 'origin', $Branch) -WorkingDirectory $repoDir
        Invoke-GitChecked -Arguments @('checkout', $Branch) -WorkingDirectory $repoDir
        Invoke-GitChecked -Arguments @('pull', '--ff-only', 'origin', $Branch) -WorkingDirectory $repoDir
    }

    $scriptPath = Join-Path $repoDir 'scripts\unity\maintain-windows-runner.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "Maintenance script not found at $scriptPath"
    }

    $versionArgs = @($UnityVersions | ForEach-Object { Quote-TaskArgument $_ })
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Quote-TaskArgument $scriptPath),
        '-UnityVersions'
    ) + $versionArgs + @(
        '-ProvisioningProfile', (Quote-TaskArgument $ProvisioningProfile),
        '-InstallRoot', (Quote-TaskArgument $InstallRoot),
        '-DiagnosticsRoot', (Quote-TaskArgument $diagnosticsRoot)
    )
    if ($Force) {
        $arguments += '-Force'
    }

    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ($arguments -join ' ')
    $triggers = @(
        (New-ScheduledTaskTrigger -AtStartup),
        (New-ScheduledTaskTrigger -Daily -At 3:17am)
    )
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 12)
    $task = New-ScheduledTask -Action $action -Trigger $triggers -Principal $principal -Settings $settings

    Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null
    Write-CiNotice "Registered scheduled task '$TaskName' for startup and daily runner maintenance."
    Write-CiNotice "Maintenance clone: $repoDir"
    Write-CiNotice "Diagnostics root: $diagnosticsRoot"
    return 0
}

$invokedAsScript = $MyInvocation.InvocationName -ne '' -and $MyInvocation.InvocationName -ne '.'

if ($invokedAsScript) {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $PSNativeCommandUseErrorActionPreference = $false

    try {
        $exit = Install-RunnerMaintenanceTask `
            -RepositoryUrl $RepositoryUrl `
            -Branch $Branch `
            -MaintenanceRoot $MaintenanceRoot `
            -TaskName $TaskName `
            -UnityVersions $UnityVersions `
            -ProvisioningProfile $ProvisioningProfile `
            -InstallRoot $InstallRoot `
            -Force:$Force.IsPresent
        exit $exit
    } catch {
        Write-CiError "Failed to install runner maintenance task: $($_.Exception.Message)"
        exit 1
    }
}
