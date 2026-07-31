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

function Wait-ForPublishedProcessId {
    # Blocks until an atomically published PID file holds a parseable, positive
    # process id, then returns it; returns 0 when the timeout elapses first.
    #
    # A FileSystemWatcher cannot do this job: the publisher completes the file
    # under a staging name and renames it into place, and a same-directory
    # rename raises Renamed, not Created. Waiting on Created therefore always
    # exhausts its timeout and synchronizes on nothing. Validating the parsed
    # content is what makes the read safe -- a partially written or empty file
    # simply fails to parse and the wait continues.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $published = 0
            $raw = $null
            try {
                $raw = [IO.File]::ReadAllText($Path)
            } catch [IO.IOException] {
                $raw = $null
            }
            if (
                $null -ne $raw -and
                [int]::TryParse($raw.Trim(), [ref]$published) -and
                $published -gt 0
            ) {
                return $published
            }
        }
        if ($elapsed.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            return 0
        }
        Start-Sleep -Milliseconds 25
    }
}

function Wait-ForProcessExit {
    # Stop-Process only REQUESTS termination, so verifying with an immediate
    # Get-Process races the kernel: a successful kill can still look like a
    # surviving process. Poll to a bound instead and report what actually
    # happened.
    param(
        [Parameter(Mandatory = $true)][int]$Id,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while ($null -ne (Get-Process -Id $Id -ErrorAction SilentlyContinue)) {
        if ($elapsed.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            return $false
        }
        Start-Sleep -Milliseconds 25
    }
    return $true
}

function Invoke-WrapperTreeProbe {
    # Drives Invoke-UnityCliCaptureWithTimeout end to end with a parent that publishes a
    # descendant PID, then reports whether that descendant survived the wrapper's own
    # termination. Returns the wrapper result plus the descendant's fate; the caller asserts.
    param(
        [Parameter(Mandatory = $true)][string]$PwshPath,
        [Parameter(Mandatory = $true)][string]$DescendantCode,
        [Parameter(Mandatory = $true)][string]$TailCode,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][int]$StallSeconds,
        [string]$PreambleCode = ''
    )

    $pidPath = Join-Path ([IO.Path]::GetTempPath()) "dxm-heartbeat-wrapper-$([Guid]::NewGuid().ToString('N')).pid"
    $finalLiteral = $pidPath.Replace("'", "''")
    $stagingLiteral = "$pidPath.tmp".Replace("'", "''")
    $parent = @"
$PreambleCode
`$child = Start-Process -FilePath '$PwshPath' -ArgumentList @('-NoLogo', '-NoProfile', '-EncodedCommand', '$DescendantCode') -PassThru
[IO.File]::WriteAllText('$stagingLiteral', [string]`$child.Id)
[IO.File]::Move('$stagingLiteral', '$finalLiteral')
$TailCode
"@
    $descendantId = 0
    try {
        $wrapperResult = Invoke-UnityCliCaptureWithTimeout `
            -Arguments @('-NoLogo', '-NoProfile', '-EncodedCommand', (ConvertTo-EncodedCommand $parent)) `
            -TimeoutSeconds $TimeoutSeconds `
            -StallSeconds $StallSeconds
        $descendantId = Wait-ForPublishedProcessId -Path $pidPath -TimeoutSeconds 5
        $removed = $descendantId -gt 0 -and (Wait-ForProcessExit -Id $descendantId -TimeoutSeconds 10)
        return [pscustomobject]@{
            Result       = $wrapperResult
            DescendantId = $descendantId
            Removed      = [bool]$removed
        }
    } finally {
        try {
            # Invoke-UnityCliCaptureWithTimeout THROWS on its non-retryable process-safety
            # error, in which case the try block never reached the publication read and
            # $descendantId is still 0. Read it here, before the file is removed, or the
            # descendant leaks along with the only record of which process it was.
            if ($descendantId -le 0) {
                $descendantId = Wait-ForPublishedProcessId -Path $pidPath -TimeoutSeconds 5
            }
            if ($descendantId -gt 0) {
                Stop-Process -Id $descendantId -Force -ErrorAction SilentlyContinue
            }
        } finally {
            Remove-PublishedProcessIdFile -Path $pidPath
        }
    }
}

function Remove-PublishedProcessIdFile {
    # Removes both halves of an atomic PID publish and fails loudly if either
    # survives, so a leaked temp file is a test failure rather than debris.
    param([Parameter(Mandatory = $true)][string]$Path)

    $candidates = @($Path, "$Path.tmp")
    foreach ($candidate in $candidates) {
        Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
    }
    $surviving = @($candidates | Where-Object { Test-Path -LiteralPath $_ })
    if ($surviving.Count -gt 0) {
        throw "PID file cleanup failed: $($surviving -join ', ')."
    }
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
    # The descendant must outlive every window this probe can wait through, or a
    # descendant that SURVIVED tree termination could exit on its own and look
    # like one that was killed. The bound is 25s: up to 10s publishing the PID,
    # up to 10s inside Confirm-UnityCliDirectChildExit (a 5s reap, a tree kill,
    # a second 5s reap), and a 5s exit observation. 60s leaves 35s of margin.
    $descendantCode = ConvertTo-EncodedCommand 'Start-Sleep -Seconds 60'
    $pwshPath = $script:UnityCliPath.Replace("'", "''")
    $treePidPath = Join-Path ([IO.Path]::GetTempPath()) "dxm-heartbeat-tree-$([Guid]::NewGuid().ToString('N')).pid"
    $treePidLiteral = $treePidPath.Replace("'", "''")
    $treePidStagingLiteral = "$treePidPath.tmp".Replace("'", "''")
    $descendantParent = @"
`$child = Start-Process -FilePath '$pwshPath' -ArgumentList @('-NoLogo', '-NoProfile', '-EncodedCommand', '$descendantCode') -PassThru
[IO.File]::WriteAllText('$treePidStagingLiteral', [string]`$child.Id)
[IO.File]::Move('$treePidStagingLiteral', '$treePidLiteral')
Start-Sleep -Seconds 60
"@
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
        $descendantId = Wait-ForPublishedProcessId -Path $treePidPath -TimeoutSeconds 10
        if ($descendantId -gt 0) {
            $descendantProcess = Get-Process -Id $descendantId -ErrorAction SilentlyContinue
        }
        $treeConfirmation = Confirm-UnityCliDirectChildExit -Process $treeParent
        if ($null -ne $descendantProcess) {
            $descendantRemovedByTreeKill = [bool]$descendantProcess.WaitForExit(5000)
        }
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
                    if (-not (Wait-ForProcessExit -Id $descendantId -TimeoutSeconds 10)) {
                        throw "Tree-probe descendant process $descendantId survived cleanup."
                    }
                }
            } finally {
                try {
                    if ($null -ne $descendantProcess) {
                        $descendantProcess.Dispose()
                    }
                } finally {
                    Remove-PublishedProcessIdFile -Path $treePidPath
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

    # The probe above drives Confirm-UnityCliDirectChildExit directly, which is what makes it
    # independent of startup timing. Invoke-UnityCliCaptureWithTimeout has its OWN Kill($true)
    # call sites for the stall and wall-clock paths, though, and they are the ones CI actually
    # runs. If either regressed to a bare Kill(), the parent would exit before
    # Confirm-UnityCliDirectChildExit ran, that helper would see an already-exited direct child
    # and skip tree termination, and a reparented Unity installer would be orphaned holding the
    # editor tree -- with the direct probe above still green. Cover both wrapper paths.
    # `lastActivityMs` starts at 0, so the FIRST stall window runs from process launch, not from
    # the first line of output. Emitting before spawning the descendant is what keeps the nested
    # Start-Process and the PID write out of that first window, leaving only pwsh's own startup
    # inside it; publication then resets the clock before the silence that trips the heartbeat.
    #
    # This is a wide margin, not a structural guarantee. The stall clock cannot be made to start
    # at publication -- that is the wrapper's contract, and this test does not get to change it.
    # Eight seconds against a sub-second cold start is the margin, and the probe proves it held
    # rather than assuming it: the wrapper must have READ the published marker before it killed,
    # so a cold start that ever did overrun the window fails a named assertion instead of quietly
    # voiding the tree check below.
    $stallTail = @'
Write-Output 'published'
Start-Sleep -Seconds 120
'@
    $stallProbe = Invoke-WrapperTreeProbe `
        -PwshPath $pwshPath `
        -DescendantCode $descendantCode `
        -TailCode $stallTail `
        -TimeoutSeconds 60 `
        -StallSeconds 8 `
        -PreambleCode "Write-Output 'starting'"
    Assert-That 'wrapper stall path publishes a descendant' ($stallProbe.DescendantId -gt 0)
    Assert-That 'wrapper stall path killed only after publication' (
        @($stallProbe.Result.Output) -contains 'published'
    )
    Assert-That 'wrapper stall path is attributed to the heartbeat' $stallProbe.Result.StallKilled
    Assert-That 'wrapper stall termination removes the descendant' $stallProbe.Removed

    $wallClockTail = @'
while ($true) {
    Write-Output 'working'
    Start-Sleep -Milliseconds 200
}
'@
    # The wall clock runs from process launch, so unlike the stall path its margin cannot be made
    # structural. Ten seconds against a sub-second publish is a wide margin, and a publish that
    # did miss fails the first assertion loudly instead of quietly voiding the tree check.
    $wallClockProbe = Invoke-WrapperTreeProbe `
        -PwshPath $pwshPath `
        -DescendantCode $descendantCode `
        -TailCode $wallClockTail `
        -TimeoutSeconds 10 `
        -StallSeconds 30
    Assert-That 'wrapper wall-clock path publishes a descendant' ($wallClockProbe.DescendantId -gt 0)
    Assert-That 'wrapper wall-clock path is attributed to the wall clock' (
        $wallClockProbe.Result.TimedOutWallClock
    )
    Assert-That 'wrapper wall-clock termination removes the descendant' $wallClockProbe.Removed

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
    $orphanPidStagingLiteral = "$orphanPidPath.tmp".Replace("'", "''")
    $orphanParent = @"
`$child = Start-Process -FilePath '$pwshPath' -ArgumentList @('-NoLogo', '-NoProfile', '-EncodedCommand', '$descendantCode') -PassThru
[IO.File]::WriteAllText('$orphanPidStagingLiteral', [string]`$child.Id)
[IO.File]::Move('$orphanPidStagingLiteral', '$orphanPidLiteral')
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
        try {
            $orphanId = Wait-ForPublishedProcessId -Path $orphanPidPath -TimeoutSeconds 5
            if ($orphanId -gt 0) {
                $orphanWasAlive = $null -ne (Get-Process -Id $orphanId -ErrorAction SilentlyContinue)
                Stop-Process -Id $orphanId -Force -ErrorAction SilentlyContinue
            }
        } finally {
            Remove-PublishedProcessIdFile -Path $orphanPidPath
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
