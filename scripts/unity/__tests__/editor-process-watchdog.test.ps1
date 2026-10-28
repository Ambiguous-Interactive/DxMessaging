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
        $processDetails = $null
        $exitCode = Invoke-UnityEditor -EditorPath $pwsh `
            -Arguments @('-NoLogo', '-NoProfile', '-File', $scriptPath) `
            -Label $case -LogPath $logPath -TimeoutSeconds 15 -ShutdownTimeoutSeconds 1 `
            -ProcessResultRef ([ref]$processDetails)
        $timer.Stop()
        $expected = switch ($case) { 'normal' { 0 } 'shutdown' { 124 } 'stderr-shutdown' { 124 } default { 7 } }
        if ($exitCode -ne $expected) { throw "$case returned $exitCode instead of $expected" }
        if ($processDetails.ExitCode -ne $exitCode -or
            $processDetails.TimedOut -ne ($expected -eq 124) -or
            $processDetails.CompletionObserved -ne ($case -ne 'lookalike') -or
            -not $processDetails.ProcessSettingsVerified -or $processDetails.ProcessId -le 0) {
            throw "$case lost process-owner evidence: $($processDetails | ConvertTo-Json -Compress)"
        }
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
    $treeStartedUtc = [DateTime]::UtcNow.ToString('O')
    $treeClock = [Diagnostics.Stopwatch]::StartNew()
    $treeProcessDetails = $null
    try {
        $treeExit = Invoke-UnityEditor -EditorPath $pwsh `
            -Arguments @('-NoLogo', '-NoProfile', '-File', $childScript, '-PidPath', $treePidPath) `
            -Label 'shutdown process tree' -LogPath $treeLog -TimeoutSeconds 15 -ShutdownTimeoutSeconds 1 `
            -ProcessResultRef ([ref]$treeProcessDetails)
        $treeClock.Stop()
        $treeEndedUtc = [DateTime]::UtcNow.ToString('O')
        $treeText = Get-Content -LiteralPath $treeLog -Raw
        if ($treeExit -ne 124 -or $treeText -notmatch 'child-pid=(\d+)') {
            $elapsed = $treeClock.Elapsed.TotalSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
            $details = $treeProcessDetails | ConvertTo-Json -Compress
            throw "Child-tree fixture did not run: exit=$treeExit; pidFileExists=$(Test-Path -LiteralPath $treePidPath); startedUtc=$treeStartedUtc; endedUtc=$treeEndedUtc; elapsedSeconds=$elapsed; processResult=$details; log=$treeText"
        }
        $childProcess = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
        if ($childProcess) {
            try {
                if (-not $childProcess.HasExited) { throw 'Shutdown left its child process alive.' }
            } finally { $childProcess.Dispose() }
        }
    } finally {
        $treeClock.Stop()
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
    # Exercise the maintained Editor argument builder through the real process owner.
    $argumentDump = Join-Path $fixture 'dump-arguments.ps1'
    [System.IO.File]::WriteAllText($argumentDump, @'
param([string]$OutputPath)
[System.IO.File]::WriteAllText($OutputPath, (ConvertTo-Json -InputObject @($args) -Compress))
'@)
    $cardinalityFixture = 'DxMessaging.Tests.Runtime.Benchmarks.HandlerCardinalityBenchmarks'
    $selections = @(
        @{ Name = 'unfiltered'; Category = ''; Filter = '' },
        @{ Name = 'coverage'; Category = '!PerfBench'; Filter = '' },
        @{ Name = 'cardinality'; Category = 'PerfBench'; Filter = $cardinalityFixture },
        @{ Name = 'remaining'; Category = 'PerfBench'; Filter = "!$cardinalityFixture" },
        @{ Name = 'pattern'; Category = 'Allocation;MemoryReclaim'; Filter = 'Fixture\.Method\(two words\)' }
    )
    foreach ($selection in $selections) {
        $outputPath = Join-Path $fixture ("arguments-{0}.json" -f $selection.Name)
        $arguments = @(Get-UnityEditorTestArguments `
            -ProjectPath (Join-Path $fixture 'project with spaces') -TestPlatform playmode `
            -ResultsPath (Join-Path $fixture 'results with spaces.xml') `
            -AssemblyNames 'Assembly.One;Assembly.Two' `
            -TestCategory $selection.Category -TestFilter $selection.Filter `
            -AdditionalArguments @('-CacheServerIPAddress', 'cache.example:10080'))
        if (@($arguments | Where-Object { $_ -eq '-testFilter' }).Count -ne [int]([bool]$selection.Filter)) {
            throw "$($selection.Name) has the wrong test-name filter count."
        }
        if ($selection.Filter) {
            $index = [array]::IndexOf($arguments, '-testFilter')
            if ($arguments[$index + 1] -cne $selection.Filter) { throw 'TestFilter changed before launch.' }
        }
        $roundtrip = Invoke-ProcessWithTreeKillTimeout -FilePath $pwsh `
            -Arguments (@('-NoLogo', '-NoProfile', '-File', $argumentDump, '-OutputPath', $outputPath) + $arguments) `
            -Label $selection.Name -LogPath (Join-Path $fixture ("arguments-{0}.log" -f $selection.Name)) `
            -TimeoutSeconds 15
        if ($roundtrip.ExitCode -ne 0 -or $roundtrip.TimedOut) { throw 'Editor argument transport failed.' }
        $actual = @(Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json)
        if ((ConvertTo-Json -InputObject $actual -Compress) -cne (ConvertTo-Json -InputObject $arguments -Compress)) {
            throw "$($selection.Name) changed arguments across the actual child process."
        }
    }
    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    foreach ($mode in @('standalone', 'shipping')) {
        $rejection = & $pwsh -NoLogo -NoProfile -NonInteractive -File $runner `
            -UnityVersion '2021.3.45f1' -TestMode $mode -AssemblyNames 'Assembly.One' `
            -ArtifactsPath (Join-Path $fixture "rejected-$mode") -RepoRoot $repoRoot `
            -TestFilter $cardinalityFixture -GenerateOnly -SkipCiAnalyzers 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -or $rejection -notmatch 'TestFilter is supported only for EditMode and PlayMode') {
            throw "$mode did not reject an unsupported Editor name filter: $rejection"
        }
    }
    # Execute the maintained guard with owned file layouts; these placeholders are never launched.
    $buildOutputCases = @(
        @('partial', 'IL2CPP', 'partial', 'ok', 'missing IL2CPP'),
        @('metadata-missing', 'IL2CPP', 'no-metadata', 'ok', 'missing IL2CPP'),
        @('native-missing', 'IL2CPP', 'no-native', 'ok', 'missing IL2CPP'),
        @('empty-native', 'IL2CPP', 'empty-native', 'ok', 'empty IL2CPP'),
        @('empty-metadata', 'IL2CPP', 'empty-metadata', 'ok', 'empty IL2CPP'),
        @('complete', 'IL2CPP', 'complete', 'ok', '^$'),
        @('cached-native', 'IL2CPP', 'cached', 'ok', '^$'),
        @('mono', 'Mono2x', 'partial', 'ok', '^$'),
        @('failed-complete', 'IL2CPP', 'complete', 'failed', 'reported a failed'),
        @('failed-mono', 'Mono2x', 'partial', 'failed', 'reported a failed'),
        @('benign-postbuild', 'IL2CPP', 'complete', 'shutdown', '^$'),
        @('lookalike', 'IL2CPP', 'complete', 'lookalike', '^$'),
        @('missing-log', 'IL2CPP', 'complete', 'missing', 'missing build log'),
        @('qualified-failure', 'IL2CPP', 'complete', 'qualified-failed', 'reported a failed'),
        @('missing-exe', 'IL2CPP', 'missing-exe', 'ok', 'missing exe'),
        @('stale-exe', 'IL2CPP', 'stale', 'ok', 'stale exe'),
        @('missing-data', 'IL2CPP', 'missing-data', 'ok', 'missing player data')
    )
    foreach ($case in $buildOutputCases) {
        $name, $backend, $payload, $logKind, $expectedProblem = $case
        $directory = Join-Path $fixture ("player output/$name")
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $exe = Join-Path $directory 'Player.exe'
        $data = Join-Path $directory 'Player_Data'
        $started = [DateTime]::UtcNow.AddSeconds(-1)
        if ($payload -ne 'missing-exe') { [IO.File]::WriteAllText($exe, 'fixture executable; never launched') }
        if ($payload -ne 'missing-data') { New-Item -ItemType Directory -Path $data | Out-Null }
        $native = Join-Path $directory 'GameAssembly.dll'
        $metadata = Join-Path $data 'il2cpp_data/Metadata/global-metadata.dat'
        if ($payload -notin @('partial', 'no-native', 'missing-exe', 'missing-data', 'stale')) {
            [IO.File]::WriteAllText($native, $(if ($payload -eq 'empty-native') { '' } else { 'native fixture' }))
        }
        if ($payload -notin @('partial', 'no-metadata', 'missing-exe', 'missing-data', 'stale')) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $metadata) -Force | Out-Null
            [IO.File]::WriteAllText($metadata, $(if ($payload -eq 'empty-metadata') { '' } else { 'metadata fixture' }))
        }
        if ($payload -eq 'cached') {
            (Get-Item -LiteralPath $native).LastWriteTimeUtc = $started.AddYears(-1)
            (Get-Item -LiteralPath $metadata).LastWriteTimeUtc = $started.AddYears(-1)
        }
        if ($payload -eq 'stale') { (Get-Item -LiteralPath $exe).LastWriteTimeUtc = $started.AddHours(-1) }
        $buildLog = Join-Path $directory 'build.log'
        if ($logKind -ne 'missing') {
            $body = switch ($logKind) {
                'failed' { 'TestLaunchFailedException: Player build failed' }
                'qualified-failed' { 'UnityEditor.TestTools.TestRunner.TestLaunchFailedException: Player build failed' }
                'shutdown' { "Build completed with a result of 'Succeeded'`nFatal error. System.AccessViolationException during shutdown" }
                'lookalike' { 'Note: TestLaunchFailedException: Player build failed is an example message' }
                default { "Build completed with a result of 'Succeeded'" }
            }
            [IO.File]::WriteAllText($buildLog, $body)
        }
        $problem = [string](Test-StandalonePlayerBuildOutput -ExpectedExe $exe `
            -BuildStartedUtc $started -BuildLogPath $buildLog -ScriptingBackend $backend)
        if ($problem -notmatch $expectedProblem) { throw "$name returned '$problem', expected '$expectedProblem'." }
    }
    # Bind the helper's accepted backends to the public runner contract.
    $runnerBackend = @($ast.ParamBlock.Parameters | Where-Object {
        $_.Name.VariablePath.UserPath -ceq 'StandaloneScriptingBackend'
    })
    $outputDefinition = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $_.Name -ceq 'Test-StandalonePlayerBuildOutput'
    })
    $outputBackend = @($outputDefinition.Body.ParamBlock.Parameters | Where-Object {
        $_.Name.VariablePath.UserPath -ceq 'ScriptingBackend'
    })
    $runnerValues = @($runnerBackend.Attributes | Where-Object { $_.TypeName.Name -ceq 'ValidateSet' })
    $outputValues = @($outputBackend.Attributes | Where-Object { $_.TypeName.Name -ceq 'ValidateSet' })
    if ($runnerBackend.Count -ne 1 -or $outputBackend.Count -ne 1 -or
        $runnerValues.Count -ne 1 -or $outputValues.Count -ne 1 -or
        (ConvertTo-Json -InputObject @($runnerValues.PositionalArguments.Value) -Compress) -cne
        (ConvertTo-Json -InputObject @($outputValues.PositionalArguments.Value) -Compress)) {
        throw 'Build output validation does not accept the public runner backend values.'
    }
    $outputCalls = @($ast.FindAll({ param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Test-StandalonePlayerBuildOutput'
    }, $true))
    if ($outputCalls.Count -ne 2) { throw 'Review every maintained output-validation callsite.' }
    foreach ($call in $outputCalls) {
        $arguments = @{}
        for ($index = 1; $index -lt $call.CommandElements.Count; ++$index) {
            $element = $call.CommandElements[$index]
            if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
                $arguments[$element.ParameterName] = $call.CommandElements[$index + 1].Extent.Text
            }
        }
        if ($arguments.BuildLogPath -cne '$logPath') { throw 'Build output validation lost its current build log.' }
        $expectedBackend = switch ($arguments.BuildStartedUtc) {
            '$shippingBuildStartedUtc' { "'IL2CPP'" }
            '$standaloneBuildStartedUtc' { '$StandaloneScriptingBackend' }
            default { throw 'Unknown standalone output-validation caller.' }
        }
        if ($arguments.ScriptingBackend -cne $expectedBackend) { throw 'Build output validation lost the selected backend.' }
    }
    Write-Host 'Seventeen build-output layouts and both current-log/backend callsites passed.'
    Write-Host 'Editor watchdog and result-validation and argument-transport cases passed.'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
