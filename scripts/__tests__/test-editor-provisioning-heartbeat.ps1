#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Behavioral tests for editor-provisioning heartbeat and wall-clock guards.

.DESCRIPTION
    Extracts the real timeout runner from ensure-editor.ps1 without executing
    the script's provisioning flow. Child pwsh processes model the four
    liveness states that CI must distinguish:

    - repeated, byte-identical progress remains alive and completes;
    - a silent child is killed by the heartbeat sentinel;
    - a noisy child that never finishes is killed by the wall-clock sentinel;
    - a quick-exit parent exercises the platform's detached-child pipe semantics.
#>
[CmdletBinding()]
param(
    [switch]$VerboseOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSScriptRoot
$target = Join-Path $scriptRoot 'unity/ensure-editor.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $target,
    [ref]$tokens,
    [ref]$errors
)
if (@($errors).Count -gt 0) {
    throw "ensure-editor.ps1 has parse errors: $(@($errors.Message) -join '; ')"
}

$functionNames = @(
    'Invoke-WithRetry',
    'ConvertTo-ProcessArgumentLine',
    'Get-CliProgressTriple',
    'Get-LastCliProgressMessage',
    'Get-CollapsedCliOutputTail',
    'Get-EnsureEditorProgressStallSeconds',
    'Get-EnsureEditorProgressNoticeIntervalSeconds',
    'Confirm-UnityCliDirectChildExit',
    'Invoke-UnityCliCaptureWithTimeout'
)
foreach ($name in $functionNames) {
    $definition = $ast.Find(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $name
        },
        $true
    )
    if (-not $definition) {
        throw "Function '$name' was not found in ensure-editor.ps1."
    }
    Invoke-Expression $definition.Extent.Text
}

$passed = 0
$failed = 0
function Assert-That {
    param([string]$Description, [bool]$Condition)
    if ($Condition) {
        if ($VerboseOutput) {
            Write-Host "  PASS: $Description"
        }
        $script:passed++
        return
    }
    Write-Host "  FAIL: $Description"
    $script:failed++
}

function ConvertTo-EncodedCommand {
    param([Parameter(Mandatory = $true)][string]$Code)
    return [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Code))
}

function New-FakeTerminationProcess {
    param([bool]$ExitOnSecondWait)

    $fake = [pscustomobject]@{
        WaitCalls        = 0
        KillCalls        = 0
        HasExited        = $false
        ExitOnSecondWait = $ExitOnSecondWait
    }
    $fake | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param([int]$Milliseconds)
        $this.WaitCalls++
        if ($this.ExitOnSecondWait -and $this.WaitCalls -ge 2) {
            $this.HasExited = $true
            return $true
        }
        return $false
    }
    $fake | Add-Member -MemberType ScriptMethod -Name Kill -Value {
        param([bool]$EntireProcessTree)
        $this.KillCalls++
    }
    return $fake
}

$script:UnityCliPath = (Get-Command pwsh -ErrorAction Stop).Source
$savedNoticeInterval = $env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS
$savedStallSeconds = $env:DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS
try {
    $env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS = '1'

    $noisyFinite = @'
1..7 | ForEach-Object {
    $line = '{"type":"progress","pct":50,"msg":"Installing Unity...","phase":"install"}'
    if ($_ % 2 -eq 0) {
        [Console]::Error.WriteLine($line)
    } else {
        Write-Output $line
    }
    Start-Sleep -Milliseconds 500
}
'@
    $script:noisyFiniteResult = $null
    $noticeOutput = @(& {
        $script:noisyFiniteResult = Invoke-UnityCliCaptureWithTimeout `
            -Arguments @('-NoLogo', '-NoProfile', '-EncodedCommand', (ConvertTo-EncodedCommand $noisyFinite)) `
            -TimeoutSeconds 10 `
            -StallSeconds 3
    } 6>&1)
    $result = $script:noisyFiniteResult
    $noticeText = (@($noticeOutput | ForEach-Object { [string]$_ }) -join "`n")
    Assert-That 'repeated stdout/stderr progress completes successfully' $result.Success
    Assert-That 'repeated stdout/stderr progress preserves native exit 0' ($result.ExitCode -eq 0)
    Assert-That 'repeated stdout/stderr progress is not heartbeat-killed' (-not $result.StallKilled)
    Assert-That 'repeated stdout/stderr progress is not wall-clock-killed' (-not $result.TimedOutWallClock)
    Assert-That 'repeated stdout/stderr progress confirms direct child exit' $result.DirectChildExited
    Assert-That 'every repeated stdout/stderr progress line is captured' (@($result.Output).Count -eq 7)
    Assert-That 'periodic notice reports monotonic idle elapsed time' ($noticeText -match 'install heartbeat:.*idleElapsed=\d+s')

    $env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS = '0'
    $env:DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS = '1'
    $silent = 'Start-Sleep -Seconds 10'
    $result = Invoke-UnityCliCaptureWithTimeout `
        -Arguments @('-NoLogo', '-NoProfile', '-EncodedCommand', (ConvertTo-EncodedCommand $silent)) `
        -TimeoutSeconds 10
    Assert-That 'silent child fails' (-not $result.Success)
    Assert-That 'silent child receives heartbeat sentinel 125' ($result.ExitCode -eq 125)
    Assert-That 'silent child is attributed to heartbeat' $result.StallKilled
    Assert-That 'silent child is not attributed to wall clock' (-not $result.TimedOutWallClock)
    Assert-That 'silent child exit is confirmed' $result.DirectChildExited

    $noisyForever = @'
while ($true) {
    Write-Output 'still alive'
    Start-Sleep -Milliseconds 100
}
'@
    $result = Invoke-UnityCliCaptureWithTimeout `
        -Arguments @('-NoLogo', '-NoProfile', '-EncodedCommand', (ConvertTo-EncodedCommand $noisyForever)) `
        -TimeoutSeconds 1 `
        -StallSeconds 5
    Assert-That 'endless noisy child fails' (-not $result.Success)
    Assert-That 'endless noisy child receives wall-clock sentinel 124' ($result.ExitCode -eq 124)
    Assert-That 'endless noisy child is not attributed to heartbeat' (-not $result.StallKilled)
    Assert-That 'endless noisy child is attributed to wall clock' $result.TimedOutWallClock
    Assert-That 'endless noisy child exit is confirmed' $result.DirectChildExited
    Assert-That 'endless noisy child emitted activity before its deadline' (@($result.Output).Count -gt 0)

    # cspell:ignore libc
    $closedPipes = @'
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class NativePipeClose { [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int id); [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle); [DllImport("libc")] private static extern int close(int fd); public static void Close() { if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { CloseHandle(GetStdHandle(-11)); CloseHandle(GetStdHandle(-12)); } else { close(1); close(2); } } }'
[NativePipeClose]::Close()
Start-Sleep -Seconds 20
'@
    $result = Invoke-UnityCliCaptureWithTimeout `
        -Arguments @('-NoLogo', '-NoProfile', '-EncodedCommand', (ConvertTo-EncodedCommand $closedPipes)) `
        -TimeoutSeconds 5 `
        -StallSeconds 0
    Assert-That 'closed-pipe child fails after the second reap termination' (-not $result.Success)
    Assert-That 'closed-pipe child receives wrapper sentinel 124' ($result.ExitCode -eq 124)
    Assert-That 'closed-pipe child is attributed to wrapper wall timeout' $result.TimedOutWallClock
    Assert-That 'closed-pipe child is not attributed to heartbeat' (-not $result.StallKilled)
    Assert-That 'closed-pipe child exit is confirmed' $result.DirectChildExited

    # 2026-07-31: Synchronize on the descendant PID before testing the real
    # tree-termination helper. The former one-second heartbeat probe mixed
    # PowerShell startup time into this contract and could kill the parent
    # before it had created or reported the descendant.
    $descendantCode = ConvertTo-EncodedCommand 'Start-Sleep -Seconds 10'
    $pwshPath = $script:UnityCliPath.Replace("'", "''")
    $treePidPath = Join-Path ([IO.Path]::GetTempPath()) "dxm-heartbeat-tree-$([Guid]::NewGuid().ToString('N')).pid"
    $treePidLiteral = $treePidPath.Replace("'", "''")
    $descendantParent = @"
`$child = Start-Process -FilePath '$pwshPath' -ArgumentList @('-NoLogo', '-NoProfile', '-EncodedCommand', '$descendantCode') -PassThru
[IO.File]::WriteAllText('$treePidLiteral', [string]`$child.Id)
Start-Sleep -Seconds 30
"@
    $treeWatcher = [IO.FileSystemWatcher]::new(
        [IO.Path]::GetDirectoryName($treePidPath),
        [IO.Path]::GetFileName($treePidPath)
    )
    $treeWatcher.EnableRaisingEvents = $true
    $treeParent = $null
    $treeConfirmation = $null
    $descendantProcess = $null
    $descendantId = 0
    $descendantRemovedByTreeKill = $false
    try {
        $treeParent = Start-Process `
            -FilePath $script:UnityCliPath `
            -ArgumentList @('-NoLogo', '-NoProfile', '-EncodedCommand', (ConvertTo-EncodedCommand $descendantParent)) `
            -PassThru
        if (-not (Test-Path -LiteralPath $treePidPath -PathType Leaf)) {
            [void]$treeWatcher.WaitForChanged([IO.WatcherChangeTypes]::Created, 10000)
        }
        if (Test-Path -LiteralPath $treePidPath -PathType Leaf) {
            $descendantId = [int][IO.File]::ReadAllText($treePidPath)
            $descendantProcess = Get-Process -Id $descendantId -ErrorAction SilentlyContinue
        }
        $treeConfirmation = Confirm-UnityCliDirectChildExit -Process $treeParent
        if ($null -ne $descendantProcess) {
            $descendantRemovedByTreeKill = [bool]$descendantProcess.WaitForExit(5000)
        }
    } finally {
        try {
            $treeWatcher.Dispose()
        } finally {
            try {
                try {
                    if ($null -ne $treeParent -and -not $treeParent.HasExited) {
                        $treeParent.Kill($true)
                        [void]$treeParent.WaitForExit(5000)
                    }
                } finally {
                    if ($null -ne $treeParent) {
                        $treeParent.Dispose()
                    }
                }
            } finally {
                try {
                    if ($descendantId -gt 0) {
                        Stop-Process -Id $descendantId -Force -ErrorAction SilentlyContinue
                        if ($null -ne (Get-Process -Id $descendantId -ErrorAction SilentlyContinue)) {
                            throw "Tree-probe descendant process $descendantId survived cleanup."
                        }
                    }
                } finally {
                    try {
                        if ($null -ne $descendantProcess) {
                            $descendantProcess.Dispose()
                        }
                    } finally {
                        Remove-Item -LiteralPath $treePidPath -Force -ErrorAction SilentlyContinue
                        if (Test-Path -LiteralPath $treePidPath) {
                            throw "Tree-probe PID file '$treePidPath' survived cleanup."
                        }
                    }
                }
            }
        }
    }
    Assert-That 'tree probe captures the descendant process id' ($descendantId -gt 0)
    Assert-That 'tree probe requests process-tree termination' (
        $null -ne $treeConfirmation -and $treeConfirmation.TerminationRequested
    )
    Assert-That 'tree probe confirms direct child exit' (
        $null -ne $treeConfirmation -and $treeConfirmation.DirectChildExited
    )
    Assert-That 'tree termination removes the descendant' $descendantRemovedByTreeKill

    $secondAttemptSuccess = New-FakeTerminationProcess -ExitOnSecondWait $true
    $confirmation = Confirm-UnityCliDirectChildExit -Process $secondAttemptSuccess
    Assert-That 'second reap path performs two waits and one tree request' (
        $secondAttemptSuccess.WaitCalls -eq 2 -and
        $secondAttemptSuccess.KillCalls -eq 1 -and
        $confirmation.TerminationRequested
    )
    Assert-That 'second reap path can confirm the direct child exit' $confirmation.DirectChildExited
    Assert-That 'successful second reap remains retry-safe' (-not $confirmation.TerminationUnconfirmed)

    $secondAttemptFailure = New-FakeTerminationProcess -ExitOnSecondWait $false
    $confirmation = Confirm-UnityCliDirectChildExit -Process $secondAttemptFailure
    Assert-That 'exhausted second reap performs two waits and one tree request' (
        $secondAttemptFailure.WaitCalls -eq 2 -and
        $secondAttemptFailure.KillCalls -eq 1 -and
        $confirmation.TerminationRequested
    )
    Assert-That 'exhausted second reap leaves direct child unconfirmed' (-not $confirmation.DirectChildExited)
    Assert-That 'exhausted second reap fails closed' $confirmation.TerminationUnconfirmed

    $orphanPidPath = Join-Path ([IO.Path]::GetTempPath()) "dxm-heartbeat-orphan-$([Guid]::NewGuid().ToString('N')).pid"
    $orphanPidLiteral = $orphanPidPath.Replace("'", "''")
    $orphanParent = @"
`$child = Start-Process -FilePath '$pwshPath' -ArgumentList @('-NoLogo', '-NoProfile', '-EncodedCommand', '$descendantCode') -PassThru
[IO.File]::WriteAllText('$orphanPidLiteral', [string]`$child.Id)
"@
    $orphanAttempts = 0
    $orphanResult = $null
    $orphanSafetyErrorEscaped = $false
    $orphanSafetyMarker = $false
    $orphanId = 0
    $orphanWasAlive = $false
    try {
        $orphanResult = Invoke-WithRetry -MaxAttempts 3 -DelaySeconds 0 -Action {
            $script:orphanAttempts++
            Invoke-UnityCliCaptureWithTimeout `
                -Arguments @('-NoLogo', '-NoProfile', '-EncodedCommand', (ConvertTo-EncodedCommand $orphanParent)) `
                -TimeoutSeconds 10 `
                -StallSeconds 1
        }
    } catch {
        $orphanSafetyErrorEscaped = ($_.Exception.Message -match 'safe process-tree termination could not be confirmed')
        $orphanSafetyMarker = [bool]$_.Exception.Data['DxMessagingNonRetryable']
    } finally {
        if (Test-Path -LiteralPath $orphanPidPath -PathType Leaf) {
            $orphanId = [int][IO.File]::ReadAllText($orphanPidPath)
            $orphanWasAlive = $null -ne (Get-Process -Id $orphanId -ErrorAction SilentlyContinue)
            Stop-Process -Id $orphanId -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $orphanPidPath -Force -ErrorAction SilentlyContinue
        }
    }
    Assert-That 'quick-exit parent leaves a live detached descendant' ($orphanId -gt 0 -and $orphanWasAlive)
    if ($IsWindows) {
        Assert-That 'Windows detached descendant does not retain the redirected pipes' (
            $null -ne $orphanResult -and $orphanResult.Success
        )
        Assert-That 'Windows direct-child completion does not produce a process-safety marker' (
            -not $orphanSafetyErrorEscaped -and -not $orphanSafetyMarker
        )
        Assert-That 'Windows detached-child path is attempted exactly once' ($orphanAttempts -eq 1)
    } else {
        Assert-That 'inherited-pipe orphan produces the process-safety error' $orphanSafetyErrorEscaped
        Assert-That 'inherited-pipe orphan marks the error as non-retryable' $orphanSafetyMarker
        Assert-That 'inherited-pipe orphan path is attempted exactly once' ($orphanAttempts -eq 1)
    }
} finally {
    if ($null -eq $savedNoticeInterval) {
        Remove-Item Env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS -ErrorAction SilentlyContinue
    } else {
        $env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS = $savedNoticeInterval
    }
    if ($null -eq $savedStallSeconds) {
        Remove-Item Env:DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS -ErrorAction SilentlyContinue
    } else {
        $env:DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS = $savedStallSeconds
    }
}

Write-Host "Unity editor heartbeat tests: $passed passed, $failed failed."
if ($failed -gt 0) {
    exit 1
}
