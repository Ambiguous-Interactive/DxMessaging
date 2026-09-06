#!/usr/bin/env pwsh
# Exercise the maintained process owner with real portable child executables.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runner = Join-Path (Split-Path -Parent $PSScriptRoot) 'run-ci-tests.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if (@($errors).Count) { throw ($errors.Message -join '; ') }
foreach ($definition in $ast.EndBlock.Statements) {
    if ($definition -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
        Invoke-Expression $definition.Extent.Text
    }
}
# These fixtures contain no compiler diagnostics or native crash codes.
$script:CatastrophicPatterns = @()
$script:NativeExitCodeDescriptions = @{}
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("dxm-editor-watchdog-" + [guid]::NewGuid().ToString('N'))
$pwsh = (Get-Process -Id $PID).Path
$terminal = 'Test run completed. Exiting with code 0 (Ok). Run completed.'
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    foreach ($case in @('normal', 'shutdown', 'stderr-shutdown', 'lookalike', 'failed-exit')) {
        $scriptPath = Join-Path $fixture "$case.ps1"
        $logPath = Join-Path $fixture "$case.log"
        $body = switch ($case) {
            'normal' { "1..64 | ForEach-Object { [Console]::Out.WriteLine(('o' * 2048)); [Console]::Error.WriteLine(('e' * 2048)) }; [Console]::Out.WriteLine('$terminal'); [Console]::Error.WriteLine('stderr retained'); Write-Output 'stdout retained'; exit 0" }
            'shutdown' { "Write-Output '$terminal'; Wait-Event -Timeout 30 | Out-Null" }
            'stderr-shutdown' { "[Console]::Error.WriteLine('$terminal'); Wait-Event -Timeout 30 | Out-Null" }
            'lookalike' { "Write-Output 'prefix $terminal'; Wait-Event -Timeout 2 | Out-Null; exit 7" }
            'failed-exit' { "Write-Output '$terminal'; exit 7" }
        }
        [System.IO.File]::WriteAllText($scriptPath, $body)
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $exitCode = Invoke-UnityEditor -EditorPath $pwsh `
            -Arguments @('-NoLogo', '-NoProfile', '-File', $scriptPath) `
            -Label $case -LogPath $logPath -TimeoutSeconds 15 -ShutdownTimeoutSeconds 1
        $timer.Stop()
        $expected = switch ($case) { 'normal' { 0 } 'shutdown' { 124 } 'stderr-shutdown' { 124 } default { 7 } }
        if ($exitCode -ne $expected) { throw "$case returned $exitCode instead of $expected" }
        if ($case -in @('shutdown', 'stderr-shutdown') -and $timer.Elapsed.TotalSeconds -ge 10) {
            throw 'Completed work waited for the total timeout instead of the shutdown deadline.'
        }
        $log = Get-Content -LiteralPath $logPath -Raw
        if ($case -eq 'normal' -and ($log -notmatch 'stderr retained' -or $log -notmatch 'stdout retained')) {
            throw 'The process owner lost a redirected stream.'
        }
    }

    $childScript = Join-Path $fixture 'child-tree.ps1'
    $treePidPath = Join-Path $fixture 'tree-child.pid'
    [System.IO.File]::WriteAllText($childScript, @'
param([string]$PidPath)
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Process -Id $PID).Path
$start.Arguments = '-NoLogo -NoProfile -Command "Wait-Event -Timeout 30 | Out-Null"'
$start.UseShellExecute = $false
$child = [System.Diagnostics.Process]::Start($start)
[IO.File]::WriteAllText($PidPath, [string]$child.Id)
Write-Output "child-pid=$($child.Id)"
Write-Output 'Test run completed. Exiting with code 0 (Ok). Run completed.'
Wait-Event -Timeout 30 | Out-Null
'@)
    $treeLog = Join-Path $fixture 'child-tree.log'
    try {
        $treeExit = Invoke-UnityEditor -EditorPath $pwsh `
            -Arguments @('-NoLogo', '-NoProfile', '-File', $childScript, '-PidPath', $treePidPath) `
            -Label 'shutdown process tree' -LogPath $treeLog -TimeoutSeconds 15 -ShutdownTimeoutSeconds 1
        $treeText = Get-Content -LiteralPath $treeLog -Raw
        if ($treeExit -ne 124 -or $treeText -notmatch 'child-pid=(\d+)') { throw 'Child-tree fixture did not run.' }
        $childProcess = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
        if ($childProcess) {
            try {
                if (-not $childProcess.HasExited) { throw 'Shutdown left its child process alive.' }
            } finally { $childProcess.Dispose() }
        }
    } finally {
        if (Test-Path -LiteralPath $treePidPath) {
            $childProcess = Get-Process -Id ([int][IO.File]::ReadAllText($treePidPath)) -ErrorAction SilentlyContinue
            if ($childProcess) {
                try {
                    if (-not $childProcess.HasExited) { $childProcess.Kill($true) }
                    if (-not $childProcess.WaitForExit(5000)) { throw 'Child-tree fixture cleanup did not exit.' }
                } finally { $childProcess.Dispose() }
            }
        }
    }

    # A parent that exits before its pipe-holding child cannot be tree-killed by
    # walking the exited parent. Match ensure-editor's existing nonretryable policy.
    $orphanScript = Join-Path $fixture 'orphan-parent.ps1'
    $orphanPidPath = Join-Path $fixture 'orphan-child.pid'
    $orphanSource = @'
param([string]$PidPath)
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Process -Id $PID).Path
$start.Arguments = '-NoLogo -NoProfile -Command "Wait-Event -Timeout 30 | Out-Null"'
$start.UseShellExecute = $false
$child = [System.Diagnostics.Process]::Start($start)
[IO.File]::WriteAllText($PidPath, [string]$child.Id)
Write-Output 'Test run completed. Exiting with code 0 (Ok). Run completed.'
exit 0
'@
    [System.IO.File]::WriteAllText($orphanScript, $orphanSource)
    $orphanLog = Join-Path $fixture 'orphan.log'
    $safetyFailure = $null
    $orphanExit = $null
    $orphanClock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $orphanExit = Invoke-UnityEditor -EditorPath $pwsh `
            -Arguments @('-NoLogo', '-NoProfile', '-File', $orphanScript, '-PidPath', $orphanPidPath) `
            -Label 'exited parent with inherited pipe' -LogPath $orphanLog `
            -TimeoutSeconds 15 -ShutdownTimeoutSeconds 1
    } catch {
        $safetyFailure = $_.Exception
    } finally {
        $orphanClock.Stop()
        # Only this fixture owns this child; production must not discover unrelated
        # editor processes by name or broaden termination beyond its known tree.
        if (Test-Path -LiteralPath $orphanPidPath) {
            $orphanProcess = Get-Process -Id ([int][IO.File]::ReadAllText($orphanPidPath)) -ErrorAction SilentlyContinue
            if ($orphanProcess) {
                try {
                    if (-not $orphanProcess.HasExited) { $orphanProcess.Kill($true) }
                    if (-not $orphanProcess.WaitForExit(5000)) { throw 'Orphan fixture cleanup did not exit.' }
                } finally { $orphanProcess.Dispose() }
            }
        }
    }
    if ($orphanClock.Elapsed.TotalSeconds -ge 12) { throw 'Inherited-pipe cleanup exceeded its bounded deadline.' }
    if ($null -ne $safetyFailure) {
        if ($safetyFailure.Message -notmatch 'safe process-tree termination could not be confirmed' -or
            -not $safetyFailure.Data['DxMessagingNonRetryable']) {
            throw "Inherited-pipe failure lost its nonretryable classification: $safetyFailure"
        }
        if (-not (Test-Path -LiteralPath $orphanLog)) { throw 'Inherited-pipe failure lost its diagnostic log.' }
    } elseif (-not $IsWindows -or $orphanExit -ne 0) {
        throw 'Inherited-pipe orphan was accepted as confirmed tree cleanup.'
    }
    # Windows may create independent console handles instead of inheriting the
    # parent's redirected pipes, as in the existing provisioning heartbeat fixture.

    # EOF is independent of process exit. Close both OS descriptors and continue
    # past the old five-second reap window; the total deadline still owns the run.
    $node = (Get-Command node -ErrorAction Stop).Source
    $closedStreams = Invoke-ProcessWithTreeKillTimeout -FilePath $node `
        -Arguments @('-e', "const fs = require('node:fs'); fs.closeSync(1); fs.closeSync(2); setTimeout(() => process.exit(7), 6000)") `
        -Label 'closed output with live process' -LogPath (Join-Path $fixture 'closed-streams.log') -TimeoutSeconds 15
    if ($closedStreams.TimedOut -or $closedStreams.ExitCode -ne 7) {
        throw 'Closing output streams caused premature process termination.'
    }

    # No terminal signal: use the total deadline, preserving the incomplete-run failure.
    $result = Invoke-ProcessWithTreeKillTimeout -FilePath $pwsh `
        -Arguments @('-NoLogo', '-NoProfile', '-Command', 'Wait-Event -Timeout 30 | Out-Null') `
        -Label 'incomplete work' -LogPath (Join-Path $fixture 'incomplete.log') -TimeoutSeconds 1
    if (-not $result.TimedOut -or $result.CompletionObserved -or $result.ExitCode -ne 124) {
        throw 'Incomplete work must remain a timed-out failure without a completion signal.'
    }
    if (Get-Process -Id $result.ProcessId -ErrorAction SilentlyContinue) {
        throw 'The timed-out process is still alive.'
    }

    # A forced exit never manufactures a passing result; exercise the real validator.
    $resultsPath = Join-Path $fixture 'results.xml'
    foreach ($case in @('missing', 'malformed', 'empty', 'failed', 'passed')) {
        if (Test-Path -LiteralPath $resultsPath) { Remove-Item -LiteralPath $resultsPath }
        $xml = switch ($case) {
            'malformed' { '<test-run' }
            'empty' { '<test-run total="0" passed="0" failed="0" skipped="0" />' }
            'failed' { '<test-run total="1" passed="0" failed="1" skipped="0" />' }
            'passed' { '<test-run total="1" passed="1" failed="0" skipped="0" />' }
        }
        if ($null -ne $xml) { [System.IO.File]::WriteAllText($resultsPath, [string]$xml) }
        $rejected = $false
        try {
            Test-NUnitResults -Path $resultsPath -Label $case -UnityExitCode 124
        } catch {
            $rejected = $true
            if ($case -eq 'passed') { throw }
        }
        if ($rejected -ne ($case -ne 'passed')) { throw "$case had the wrong artifact verdict." }
    }
    Write-Host 'Editor watchdog and result-validation cases passed.'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
