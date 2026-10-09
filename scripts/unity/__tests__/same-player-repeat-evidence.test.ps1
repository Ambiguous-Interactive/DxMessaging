#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Exercises the real same-player manifest, process-affinity, and host-probe helpers.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runnerPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'run-ci-tests.ps1'
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$workflowPath = Join-Path $repoRoot '.github/workflows/perf-numbers.yml'
$stabilityScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'require-same-player-stability.ps1'
$cpuProfileScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'resolve-performance-cpu-profile.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $runnerPath,
    [ref]$tokens,
    [ref]$parseErrors
)
if ($parseErrors -and $parseErrors.Count -gt 0) {
    throw "run-ci-tests.ps1 has parse errors: $($parseErrors.Message -join '; ')"
}

foreach ($name in @(
    'ConvertTo-UnityFileUriPath',
    'Get-ComparisonPackages',
    'New-ManifestJson',
    'Get-StandaloneHostConditionSnapshot',
    'Get-StandalonePlayerManifest',
    'Test-StandalonePilotHostTelemetry',
    'Write-JsonArtifact',
    'ConvertTo-ProcessArgumentLine',
    'Invoke-ProcessWithTreeKillTimeout'
)) {
    $definition = $ast.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $name
        },
        $true
    ) | Select-Object -First 1
    if (-not $definition) {
        throw "Function '$name' was not found in run-ci-tests.ps1."
    }
    Invoke-Expression $definition.Extent.Text
}

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][bool]$Condition
    )
    if (-not $Condition) {
        throw "Assertion failed: $Description"
    }
}

function Write-TestJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Assert-StabilityGateFails {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$ArtifactsPath,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    $failedAsExpected = $false
    try {
        & $stabilityScriptPath -ArtifactsPath $ArtifactsPath
    } catch {
        $failedAsExpected = $_.Exception.Message.Contains($ExpectedMessage)
    }
    Assert-That $Description $failedAsExpected
}

function New-StabilityFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][double[]]$RunValues,
        [Parameter(Mandatory = $true)][string[]]$Scenarios
    )

    $repeatRoot = Join-Path $Path 'same-player-repeats'
    New-Item -ItemType Directory -Force -Path $repeatRoot | Out-Null
    $records = New-Object System.Collections.Generic.List[object]
    for ($index = 0; $index -lt $RunValues.Count; $index++) {
        $runIndex = $index + 1
        $runNumber = '{0:D2}' -f $runIndex
        if ($runIndex -eq 1) {
            $relativeResultsPath = 'results.xml'
            $relativeLogPath = 'player.log'
        } else {
            $relativeResultsPath = "same-player-repeats/run-$runNumber/repeat-$runNumber-results.xml"
            $relativeLogPath = "same-player-repeats/run-$runNumber/repeat-$runNumber-player.log"
            New-Item `
                -ItemType Directory `
                -Force `
                -Path (Split-Path -Parent (Join-Path $Path $relativeResultsPath)) |
                Out-Null
        }
        $rows = foreach ($scenario in $Scenarios) {
            "Comparison_DxMessaging_$scenario,Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1),aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa,0,$($RunValues[$index]),0,5000,0"
        }
        [System.IO.File]::WriteAllLines((Join-Path $Path $relativeResultsPath), $rows)
        [System.IO.File]::WriteAllText((Join-Path $Path $relativeLogPath), 'fixture')

        $hostConditionsFile = "run-$runNumber-host-conditions.json"
        $beforeTimestamp = [DateTime]::UtcNow
        $snapshot = [ordered]@{
            phase = 'before'
            timestampUtc = $beforeTimestamp.ToString('O')
            logicalProcessors = @([ordered]@{ frequencyMhz = 4000; loadPercent = 20 })
            processors = @()
            totalCpuLoadPercent = 20
            acpiThermalZones = [ordered]@{ available = $false; zones = @() }
        }
        $afterSnapshot = [ordered]@{} + $snapshot
        $afterSnapshot.phase = 'after'
        $afterSnapshot.timestampUtc = $beforeTimestamp.AddSeconds(1).ToString('O')
        Write-TestJson `
            -Path (Join-Path $repeatRoot $hostConditionsFile) `
            -Value ([ordered]@{
                schemaVersion = 1
                runIndex = $runIndex
                runCount = $RunValues.Count
                playerProcessId = 1000 + $runIndex
                playerProcessorAffinityMask = '0x3'
                timedOut = $false
                before = $snapshot
                after = $afterSnapshot
            })
        $records.Add([ordered]@{
                runIndex = $runIndex
                resultsPath = $relativeResultsPath
                playerLogPath = $relativeLogPath
                hostConditionsFile = $hostConditionsFile
                processId = 1000 + $runIndex
                processorAffinityMask = '0x3'
                timedOut = $false
            })
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        fileCount = 1
        files = @([ordered]@{
                path = 'DxmTestPlayer.exe'
                length = 3
                sha256 = 'A' * 64
            })
    }
    Write-TestJson `
        -Path (Join-Path $repeatRoot 'same-player-evidence.json') `
        -Value ([ordered]@{
            schemaVersion = 1
            runCount = $RunValues.Count
            canonicalPublishedRunIndex = 1
            playerDirectoryManifestMatches = $true
            playerDirectoryManifestBefore = $manifest
            playerDirectoryManifestAfter = $manifest
            runs = @($records.ToArray())
        })
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dxm-same-player-{0}" -f [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $executablePath = Join-Path $fixtureRoot 'DxmTestPlayer.exe'
    $gameAssemblyPath = Join-Path $fixtureRoot 'GameAssembly.dll'
    $metadataPath = Join-Path $fixtureRoot 'global-metadata.dat'
    $unityPlayerPath = Join-Path $fixtureRoot 'UnityPlayer.dll'
    $nestedDirectory = Join-Path $fixtureRoot 'DxmTestPlayer_Data/Managed'
    New-Item -ItemType Directory -Force -Path $nestedDirectory | Out-Null
    $arbitraryPlayerFilePath = Join-Path $nestedDirectory 'arbitrary-player-file.bin'
    [System.IO.File]::WriteAllText($executablePath, 'exe-v1')
    [System.IO.File]::WriteAllText($gameAssemblyPath, 'game-v1')
    [System.IO.File]::WriteAllText($metadataPath, 'metadata-v1')
    [System.IO.File]::WriteAllText($unityPlayerPath, 'unity-v1')
    [System.IO.File]::WriteAllText($arbitraryPlayerFilePath, 'arbitrary-v1')

    $manifestBefore = Get-StandalonePlayerManifest -ExecutablePath $executablePath
    [System.IO.File]::WriteAllText($arbitraryPlayerFilePath, 'arbitrary-v2')
    $manifestAfter = Get-StandalonePlayerManifest -ExecutablePath $executablePath
    Assert-That 'the manifest includes every nested player file in ordinal path order' (
        $manifestBefore.fileCount -eq 5 -and
        (@($manifestBefore.files.path) -join ',') -ceq (
            'DxmTestPlayer.exe,' +
            'DxmTestPlayer_Data/Managed/arbitrary-player-file.bin,' +
            'GameAssembly.dll,UnityPlayer.dll,global-metadata.dat'
        ) -and
        @($manifestBefore.files | Where-Object { $_.path -eq 'DxmTestPlayer_Data/Managed/arbitrary-player-file.bin' }).Count -eq 1
    )
    $arbitraryBefore = @(
        $manifestBefore.files |
            Where-Object { $_.path -eq 'DxmTestPlayer_Data/Managed/arbitrary-player-file.bin' }
    )[0]
    $arbitraryAfter = @(
        $manifestAfter.files |
            Where-Object { $_.path -eq 'DxmTestPlayer_Data/Managed/arbitrary-player-file.bin' }
    )[0]
    Assert-That 'a mutation to an arbitrary player file changes the complete manifest' (
        $arbitraryBefore.sha256 -ne $arbitraryAfter.sha256
    )
    Assert-That 'unchanged player files keep their manifest entries' (
        @($manifestBefore.files | Where-Object { $_.path -eq 'GameAssembly.dll' })[0].sha256 -eq
        @($manifestAfter.files | Where-Object { $_.path -eq 'GameAssembly.dll' })[0].sha256
    )

    $snapshot = Get-StandaloneHostConditionSnapshot -Phase 'test'
    Assert-That 'the snapshot records its phase and timestamp' (
        $snapshot.phase -eq 'test' -and -not [string]::IsNullOrWhiteSpace($snapshot.timestampUtc)
    )
    Assert-That 'the snapshot distinguishes logical, package, and ACPI thermal probes' (
        $null -ne $snapshot.logicalProcessors -and
        $null -ne $snapshot.processors -and
        $null -ne $snapshot.acpiThermalZones.available
    )

    $jsonPath = Join-Path $fixtureRoot 'evidence.json'
    Write-JsonArtifact -Path $jsonPath -Value $snapshot
    $bytes = [System.IO.File]::ReadAllBytes($jsonPath)
    Assert-That 'JSON evidence is UTF-8 without a BOM' (
        $bytes.Length -ge 3 -and
        -not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    )
    $parsed = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    Assert-That 'JSON evidence round-trips' ($parsed.phase -eq 'test')

    $topologyFixturePath = Join-Path $fixtureRoot 'cpu-set-topology.json'
    $cpuProfilePath = Join-Path $fixtureRoot 'performance-cpu-profile.json'
    $cpuSets = @(for ($logicalIndex = 0; $logicalIndex -lt 32; $logicalIndex++) {
        [ordered]@{
            id = $logicalIndex + 100
            group = 0
            logicalProcessorIndex = $logicalIndex
            coreIndex = if ($logicalIndex % 4 -lt 2) {
                [math]::Floor($logicalIndex / 4)
            } else {
                8 + [math]::Floor($logicalIndex / 2)
            }
            lastLevelCacheIndex = 0
            numaNodeIndex = 0
            efficiencyClass = if ($logicalIndex % 4 -lt 2) { 8 } else { 0 }
            parked = $false
            allocated = $false
            allocatedToTargetProcess = $false
        }
    })
    Write-TestJson -Path $topologyFixturePath -Value $cpuSets
    & $cpuProfileScriptPath `
        -OutputJson $cpuProfilePath `
        -TopologyFixturePath $topologyFixturePath `
        -CpuModelFixture '13th Gen Intel(R) Core(TM) i9-13900KF'
    $cpuProfile = Get-Content -LiteralPath $cpuProfilePath -Raw | ConvertFrom-Json
    Assert-That 'the CPU profile derives the highest-efficiency partition from topology' (
        $cpuProfile.executionProfileId -ceq 'highest-efficiency-class-affinity-normal-v1' -and
        $cpuProfile.affinityMask -ceq '0x33333333' -and
        $cpuProfile.priorityClass -ceq 'Normal' -and
        $cpuProfile.logicalProcessorCount -eq 32 -and
        $cpuProfile.selectedLogicalProcessorCount -eq 16 -and
        $cpuProfile.selectedCoreCount -eq 8 -and
        @($cpuProfile.efficiencyClasses).Count -eq 2
    )

    # 2026-10-08: a cadence-loop exit diagnosed end coverage from an earlier
    # sample. Keep every frozen rejection rule and check the actual tail.
    $collectorPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'collect-perf-host-characterization.ps1'
    $profileHash = (Get-FileHash -LiteralPath $cpuProfilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $collectorHash = (Get-FileHash -LiteralPath $collectorPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $baseUtc = [DateTimeOffset]::Parse('2026-10-08T00:00:00Z').UtcDateTime
    $healthCases = @(
        @{ Name = 'healthy'; Times = @(0, 1, 2, 3, 4); End = 4.2; Stop = 4.5; Reasons = @() },
        @{ Name = 'cadence with covered tail'; Times = @(0, 1, 4.025548, 5.05, 6.075, 7.1); End = 7.316284; Stop = 7.5; Reasons = @('sample-cadence-outside-frozen-range') },
        @{ Name = 'cadence with true end gap'; Times = @(0, 1, 4.025548); End = 6.025548; Stop = 6.5; Reasons = @('sample-cadence-outside-frozen-range', 'telemetry-gap-at-player-end') },
        @{ Name = 'cadence then malformed tail'; Times = @(0, 1, 4.025548, 'invalid'); End = 5.2; Stop = 5.5; Reasons = @('sample-cadence-outside-frozen-range', 'invalid-telemetry-timestamps') },
        @{ Name = 'cadence then after stop'; Times = @(0, 1, 4.025548, 8.5); End = 7; Stop = 8; Reasons = @('sample-cadence-outside-frozen-range', 'sample-after-player-stop') },
        @{ Name = 'duplicate timestamp'; Times = @(0, 1, 1, 2); End = 2.2; Stop = 2.5; Reasons = @('sample-cadence-outside-frozen-range') },
        @{ Name = 'backward timestamp'; Times = @(0, 1, 0.9, 2); End = 2.2; Stop = 2.5; Reasons = @('sample-cadence-outside-frozen-range') },
        @{ Name = 'first interval exemption'; Times = @(0, 0.01, 1.01); End = 1.2; Stop = 1.5; Reasons = @() },
        @{ Name = 'minimum interval inclusive'; Times = @(0, 1, 1.5, 2); End = 2.2; Stop = 2.5; Reasons = @() },
        @{ Name = 'below minimum interval'; Times = @(0, 1, 1.4999999, 2.4999999); End = 2.7; Stop = 3; Reasons = @('sample-cadence-outside-frozen-range') },
        @{ Name = 'maximum interval inclusive'; Times = @(0, 1.5, 3); End = 3.2; Stop = 3.5; Reasons = @() },
        @{ Name = 'above maximum interval'; Times = @(0, 1.5000001, 2.5000001); End = 2.7; Stop = 3; Reasons = @('sample-cadence-outside-frozen-range') },
        @{ Name = 'end gap inclusive'; Times = @(0, 1, 2); End = 3.5; Stop = 3.6; Reasons = @() },
        @{ Name = 'end gap outside limit'; Times = @(0, 1, 2); End = 3.5000001; Stop = 3.6; Reasons = @('telemetry-gap-at-player-end') },
        @{ Name = 'sample after stop'; Times = @(0, 1, 2.5); End = 2; Stop = 2.4; Reasons = @('sample-after-player-stop') },
        @{ Name = 'malformed tail'; Times = @(0, 1, 'invalid'); End = 2.2; Stop = 2.5; Reasons = @('invalid-telemetry-timestamps') }
    )
    foreach ($drift in @(
        @('collector', 'telemetry-collector-source-drift'),
        @('profile', 'cpu-profile-drift'),
        @('envelope', 'telemetry-envelope-or-affinity'),
        @('schema', 'telemetry-schema-or-completeness'),
        @('power', 'power-plan-drift'),
        @('sensor', 'sensor-read-error')
    )) {
        $healthCases += @{ Name = $drift[0]; Times = @(0, 1, 2); End = 2.2; Stop = 2.5; Reasons = @($drift[1]) }
    }
    Assert-That 'the fixed health screen retains all 22 cases' ($healthCases.Count -eq 22)
    $healthFailures = @()
    foreach ($case in $healthCases) {
        $samples = @(foreach ($offset in $case.Times) {
            [ordered]@{
                timestampUtc = if ($offset -is [string]) { $offset } else {
                    $baseUtc.AddTicks([long][Math]::Round($offset * 10000000)).ToString('O')
                }
                errors = @()
                processorCounters = @($cpuProfile.selectedLogicalProcessorIndices | ForEach-Object {
                    [ordered]@{ name = "0,$_"; ProcessorFrequency = 4000; PercentProcessorPerformance = 100; PercentProcessorTime = 20; PercentPerformanceLimit = 100; PerformanceLimitFlags = 0 }
                })
                nativePower = [ordered]@{
                    errors = @(); lastSleepInterruptTime100ns = 0; lastWakeInterruptTime100ns = 0
                    processors = @($cpuProfile.selectedLogicalProcessorIndices | ForEach-Object {
                        [ordered]@{ number = $_; maxMhz = 4000; mhzLimit = 4000 }
                    })
                }
            }
        })
        $power = [ordered]@{
            active = @{ exitCode = 0; text = '381b4222-f694-41f0-9685-ff5bb260df2e' }
            processorQuery = @{ exitCode = 0 }
            processorQuerySha256 = 'a12f432bf3f070fd84e28fa53073a3326b89fd1962713ea1c9b2fde0506d06ba'
        }
        $sleep = [ordered]@{
            lastBootUpTimeUtc = $baseUtc.AddDays(-1).ToString('O'); errors = @()
            queries = @(@{ status = 'ok' }, @{ status = 'no-matches' }); recentSleepWakeEvents = @()
        }
        $telemetry = [ordered]@{
            schemaVersion = 1; purpose = 'player-time-host-telemetry'; loadMode = 'player-workload-v1'
            stopReason = 'player-finished'; requestedSampleCount = 3600; requestedCadenceSeconds = 1
            actualSampleCount = $samples.Count; samples = $samples; sourceSha256 = $collectorHash
            cpuProfile = @{ sha256 = $profileHash; affinityMask = $cpuProfile.affinityMask; executionProfileId = $cpuProfile.executionProfileId }
            processorCount = $cpuProfile.logicalProcessorCount; powerBefore = $power; powerAfter = $power
            sleepBefore = $sleep; sleepAfter = $sleep; nativeAfter = @{ errors = @() }
        }
        switch ($case.Name) {
            'collector' { $telemetry.sourceSha256 = 'invalid' }
            'profile' { $telemetry.cpuProfile.sha256 = 'invalid' }
            'schema' { $telemetry.actualSampleCount = 99 }
            'power' { $power.active.exitCode = 1 }
            'sensor' { $samples[0].errors = @('read failure') }
        }
        $telemetryPath = Join-Path $fixtureRoot 'health-telemetry.json'
        $envelopePath = Join-Path $fixtureRoot 'health-envelope.json'
        Write-TestJson -Path $telemetryPath -Value $telemetry
        $envelope = [ordered]@{
            schemaVersion = 1; telemetryProcessorAffinityMask = '0xFFFF0000'
            telemetryReadyUtc = $baseUtc.AddSeconds(-0.2).ToString('O')
            playerStartUtc = $baseUtc.AddSeconds(-0.1).ToString('O')
            playerEndUtc = $baseUtc.AddTicks([long][Math]::Round($case.End * 10000000)).ToString('O')
            telemetryStopUtc = $baseUtc.AddTicks([long][Math]::Round($case.Stop * 10000000)).ToString('O')
            unredactedTelemetrySha256 = if ($case.Name -eq 'envelope') { 'invalid' } else {
                (Get-FileHash -LiteralPath $telemetryPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        Write-TestJson -Path $envelopePath -Value $envelope
        $valid = Test-StandalonePilotHostTelemetry -TelemetryPath $telemetryPath -EnvelopePath $envelopePath -CpuProfilePath $cpuProfilePath -CollectorPath $collectorPath
        $verdict = Get-Content -LiteralPath "$telemetryPath.health.json" -Raw | ConvertFrom-Json
        $expected = @($case.Reasons | Sort-Object)
        $observed = @($verdict.reasons | Sort-Object)
        $actualHash = (Get-FileHash -LiteralPath $telemetryPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $matches = $valid -eq ($expected.Count -eq 0) -and $verdict.valid -eq $valid -and
            ($observed -join '|') -ceq ($expected -join '|') -and
            $verdict.telemetrySha256 -ceq $actualHash -and $verdict.cpuProfileSha256 -ceq $profileHash -and
            $verdict.thermalPackageTemperature -ceq 'unmeasured' -and
            $verdict.effectiveProcessorFrequency -ceq 'unmeasured'
        if (-not $matches) { $healthFailures += "$($case.Name): expected=$($expected -join ',');observed=$($observed -join ',');valid=$valid" }
        Write-Host "Health case: $($case.Name);valid=$valid;reasons=$($observed -join ',')"
    }
    Assert-That "all frozen health classifications and final-sample diagnostics match: $($healthFailures -join '; ')" ($healthFailures.Count -eq 0)
    $invalidCpuSets = @($cpuSets | ForEach-Object {
        $copy = [ordered]@{}
        foreach ($property in $_.GetEnumerator()) {
            $copy[$property.Key] = $property.Value
        }
        $copy
    })
    $invalidCpuSets[0].group = 1
    $invalidTopologyPath = Join-Path $fixtureRoot 'invalid-cpu-set-topology.json'
    Write-TestJson -Path $invalidTopologyPath -Value $invalidCpuSets
    $invalidTopologyFailed = $false
    try {
        & $cpuProfileScriptPath `
            -OutputJson (Join-Path $fixtureRoot 'invalid-performance-cpu-profile.json') `
            -TopologyFixturePath $invalidTopologyPath `
            -CpuModelFixture '13th Gen Intel(R) Core(TM) i9-13900KF'
    } catch {
        $invalidTopologyFailed = $_.Exception.Message.Contains(
            'requires one processor group 0'
        )
    }
    Assert-That 'a mixed processor-group topology fails profile resolution closed' (
        $invalidTopologyFailed
    )
    if ($IsWindows) {
        $nativeCpuProfilePath = Join-Path $fixtureRoot 'native-performance-cpu-profile.json'
        & $cpuProfileScriptPath -OutputJson $nativeCpuProfilePath
        $nativeCpuProfile = Get-Content -LiteralPath $nativeCpuProfilePath -Raw |
            ConvertFrom-Json
        $nativeLogicalIndices = @(
            $nativeCpuProfile.cpuSets | ForEach-Object { $_.logicalProcessorIndex }
        )
        Assert-That 'the native Windows CPU-set parser returns unique usable records' (
            $nativeCpuProfile.source -ceq 'GetSystemCpuSetInformation' -and
            @($nativeCpuProfile.cpuSets).Count -gt 0 -and
            @($nativeLogicalIndices | Sort-Object -Unique).Count -eq
            @($nativeCpuProfile.cpuSets).Count -and
            $nativeCpuProfile.affinityMask -cmatch '^0x[0-9A-F]+$'
        )
    }

    $processLog = Join-Path $fixtureRoot 'process.log'
    $pwshPath = (Get-Process -Id $PID).Path
    $processArguments = @{
        FilePath = $pwshPath
        Arguments = @('-NoLogo', '-NoProfile', '-Command', 'Wait-Event -Timeout 1 | Out-Null; exit 0')
        TimeoutSeconds = 30
        LogPath = $processLog
        Label = 'same-player process evidence test'
    }
    $expectedAffinityMask = $null
    if (-not $IsMacOS) {
        $parentAffinityMask = (Get-Process -Id $PID).ProcessorAffinity.ToInt64()
        $expectedAffinityMask = $parentAffinityMask -band (-$parentAffinityMask)
        $processArguments.ProcessorAffinityMask = $expectedAffinityMask
    }
    $processResult = Invoke-ProcessWithTreeKillTimeout @processArguments
    Assert-That 'the process helper returns success and a process id' (
        $processResult.ExitCode -eq 0 -and $processResult.ProcessId -gt 0
    )
    $hasProcessorAffinity = -not [string]::IsNullOrWhiteSpace(
        [string]$processResult.ProcessorAffinityMask
    )
    $hasProcessorAffinityError = -not [string]::IsNullOrWhiteSpace(
        [string]$processResult.ProcessorAffinityError
    )
    if ($IsMacOS) {
        # Investigation 2026-08-25: macOS does not implement Process.ProcessorAffinity.
        # Require its explicit probe error here; the Windows-only evidence gate below still
        # rejects a missing affinity value, and Windows/Linux test hosts require the real value.
        Assert-That 'the macOS process helper captures its platform affinity probe error' (
            -not $hasProcessorAffinity -and $hasProcessorAffinityError
        )
    } else {
        Assert-That 'the process helper applies and verifies the requested child affinity' (
            $hasProcessorAffinity -and
            -not $hasProcessorAffinityError -and
            $processResult.ProcessorAffinityMask -ceq ('0x{0:X}' -f $expectedAffinityMask) -and
            $processResult.ProcessSettingsVerified -eq $true -and
            [string]::IsNullOrWhiteSpace([string]$processResult.ProcessSettingsError)
        )

        $unavailableAffinityMask = $null
        for ($bitIndex = 0; $bitIndex -lt 62; $bitIndex++) {
            $candidateMask = [long]1 -shl $bitIndex
            if (($parentAffinityMask -band $candidateMask) -eq 0) {
                $unavailableAffinityMask = $candidateMask
                break
            }
        }
        if ($null -ne $unavailableAffinityMask) {
            $invalidProcessArguments = @{
                FilePath = $pwshPath
                Arguments = @('-NoLogo', '-NoProfile', '-Command', 'Wait-Event -Timeout 5 | Out-Null; exit 0')
                TimeoutSeconds = 30
                LogPath = (Join-Path $fixtureRoot 'invalid-process-settings.log')
                Label = 'invalid process settings test'
                ProcessorAffinityMask = $unavailableAffinityMask
            }
            $invalidProcessResult = Invoke-ProcessWithTreeKillTimeout @invalidProcessArguments
            Assert-That 'an unavailable affinity mask fails process settings closed' (
                $invalidProcessResult.ExitCode -eq -1 -and
                $invalidProcessResult.ProcessSettingsVerified -eq $false -and
                -not [string]::IsNullOrWhiteSpace(
                    [string]$invalidProcessResult.ProcessSettingsError
                )
            )
        }
    }

    $runnerText = Get-Content -LiteralPath $runnerPath -Raw
    $buildIndex = $runnerText.IndexOf('$buildResult = Invoke-ProcessWithTreeKillTimeout')
    $repeatIndex = $runnerText.IndexOf('for ($playerRunIndex = 1;')
    Assert-That 'one editor build remains outside and before the repeat loop' (
        $buildIndex -ge 0 -and $repeatIndex -gt $buildIndex
    )
    Assert-That 'run 1 stays canonical while repeat filenames are noncanonical' (
        $runnerText.Contains('$currentResultsPath = $resultsPath') -and
        $runnerText.Contains('"repeat-$runNumber-results.xml"') -and
        $runnerText.Contains('"repeat-$runNumber-player.log"')
    )
    Assert-That 'the managed repeat root is cleared before reuse' (
        $runnerText.Contains('Remove-Item -LiteralPath $samePlayerEvidenceRoot -Recurse -Force')
    )
    Assert-That 'the runner captures and compares complete player manifests' (
        $runnerText.Contains('Get-StandalonePlayerManifest') -and
        $runnerText.Contains('playerDirectoryManifestMatches')
    )
    Assert-That 'every standalone run records verified affinity and priority evidence' (
        $runnerText.Contains("Join-Path `$ArtifactsPath 'standalone-process.json'") -and
        $runnerText.Contains('actualProcessorAffinityMask = $result.ProcessorAffinityMask') -and
        $runnerText.Contains('actualPriorityClass = $result.ProcessorPriorityClass') -and
        $runnerText.Contains('if (-not $result.ProcessSettingsVerified)')
    )

    $workflowText = Get-Content -LiteralPath $workflowPath -Raw
    Assert-That 'the comparison workflow uses the runner default of one player launch' (
        -not $workflowText.Contains('StandalonePlayerRunCount')
    )
    Assert-That 'the retired multi-launch gate is not part of the comparison workflow' (
        -not $workflowText.Contains('./scripts/unity/require-same-player-stability.ps1')
    )
    Assert-That 'the perf workflow derives and verifies one known host partition' (
        $workflowText.Contains('./scripts/unity/resolve-performance-cpu-profile.ps1') -and
        $workflowText.Contains('StandalonePlayerProcessorAffinityMask = [Convert]::ToInt64(') -and
        $workflowText.Contains('StandalonePlayerPriorityClass = $env:DXM_PERF_PRIORITY_CLASS') -and
        $workflowText.Contains("executionProfileId -cne 'highest-efficiency-class-affinity-normal-v1'") -and
        $workflowText.Contains('$profile.selectedLogicalProcessorCount -ne 16') -and
        $workflowText.Contains('$evidence.actualProcessorAffinityMask -cne $profile.affinityMask') -and
        $workflowText.Contains('$evidence.actualPriorityClass -cne $profile.priorityClass')
    )

    $scenarioModulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'perf-scenarios.js'
    $scenarios = @(
        & node -e 'require(process.argv[1]).COMPARISON_SCENARIO_ORDER.forEach((scenario) => console.log(scenario))' `
            $scenarioModulePath
    )
    Assert-That 'the fixture uses all comparison scenarios' (
        $LASTEXITCODE -eq 0 -and $scenarios.Count -eq 9
    )

    $stableFixturePath = Join-Path $fixtureRoot 'stable-artifacts'
    New-Item -ItemType Directory -Force -Path $stableFixturePath | Out-Null
    New-StabilityFixture `
        -Path $stableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    & $stabilityScriptPath -ArtifactsPath $stableFixturePath
    $stableReportPath = Join-Path $stableFixturePath 'same-player-repeats/same-player-stability.json'
    $stableReport = Get-Content -LiteralPath $stableReportPath -Raw | ConvertFrom-Json
    Assert-That 'three stable samples pass without median or outlier removal' (
        $stableReport.allRowsStable -eq $true -and
        $stableReport.calculation -eq '(maximum / minimum - 1) * 100' -and
        $stableReport.platform -eq 'Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1)' -and
        $stableReport.commit -eq 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' -and
        @($stableReport.rows).Count -eq 9
    )

    $unstableFixturePath = Join-Path $fixtureRoot 'unstable-artifacts'
    New-Item -ItemType Directory -Force -Path $unstableFixturePath | Out-Null
    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1050000) `
        -Scenarios $scenarios
    & $stabilityScriptPath -ArtifactsPath $unstableFixturePath
    $unstableReportPath = Join-Path $unstableFixturePath 'same-player-repeats/same-player-stability.json'
    $unstableReport = Get-Content -LiteralPath $unstableReportPath -Raw | ConvertFrom-Json
    Assert-That 'an unstable spread remains a successful evidence verdict' (
        $unstableReport.allRowsStable -eq $false -and
        @($unstableReport.rows | Where-Object { $_.withinMaterialityBand -eq $false }).Count -eq 9
    )

    $invalidEvidencePath = Join-Path $unstableFixturePath 'same-player-repeats/same-player-evidence.json'
    $invalidEvidence = Get-Content -LiteralPath $invalidEvidencePath -Raw | ConvertFrom-Json
    $invalidEvidence.runs[1].processorAffinityMask = ''
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    $invalidFailed = $false
    try {
        & $stabilityScriptPath -ArtifactsPath $unstableFixturePath
    } catch {
        $invalidFailed = $_.Exception.Message.Contains('did not record its actual processor affinity')
    }
    Assert-That 'malformed process evidence fails closed' $invalidFailed

    $invalidEvidence.runs[1].processorAffinityMask = '0x3'
    $invalidEvidence.playerDirectoryManifestAfter.files[0].sha256 = 'E' * 64
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    $identityMismatchFailed = $false
    try {
        & $stabilityScriptPath -ArtifactsPath $unstableFixturePath
    } catch {
        $identityMismatchFailed = $_.Exception.Message.Contains('manifest entry 0 is missing, invalid, or changed')
    }
    Assert-That 'the gate independently rejects a changed player manifest entry' $identityMismatchFailed

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $invalidEvidence = Get-Content -LiteralPath $invalidEvidencePath -Raw | ConvertFrom-Json
    $invalidEvidence.schemaVersion = 2
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    Assert-StabilityGateFails `
        -Description 'the aggregate evidence schema fails closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'Expected same-player evidence schemaVersion 1'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $invalidEvidence = Get-Content -LiteralPath $invalidEvidencePath -Raw | ConvertFrom-Json
    $invalidEvidence.runs[1].resultsPath = 'same-player-repeats/run-02/wrong.xml'
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    Assert-StabilityGateFails `
        -Description 'noncanonical managed repeat paths fail closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'did not use its exact managed evidence paths'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $invalidEvidence = Get-Content -LiteralPath $invalidEvidencePath -Raw | ConvertFrom-Json
    $invalidEvidence.runs[1].timedOut = $true
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    Assert-StabilityGateFails `
        -Description 'a timed-out player remains valid NUnit output but fails stability evidence' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'timed out and cannot support stability evidence'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $hostPath = Join-Path $unstableFixturePath 'same-player-repeats/run-02-host-conditions.json'
    $invalidHost = Get-Content -LiteralPath $hostPath -Raw | ConvertFrom-Json
    $invalidHost.schemaVersion = 2
    Write-TestJson -Path $hostPath -Value $invalidHost
    Assert-StabilityGateFails `
        -Description 'the per-run host schema fails closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'host evidence does not match its run record'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $invalidHost = Get-Content -LiteralPath $hostPath -Raw | ConvertFrom-Json
    $invalidHost.after.timestampUtc = (
        [DateTimeOffset]::Parse([string]$invalidHost.before.timestampUtc).AddMinutes(-1).ToString('O')
    )
    Write-TestJson -Path $hostPath -Value $invalidHost
    Assert-StabilityGateFails `
        -Description 'reversed before and after timestamps fail closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'recorded its before snapshot after its after snapshot'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $thirdRunPath = Join-Path $unstableFixturePath 'same-player-repeats/run-03/repeat-03-results.xml'
    $thirdRunText = Get-Content -LiteralPath $thirdRunPath -Raw
    $thirdRunText = $thirdRunText.Replace(
        'Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1)',
        'Standalone IL2CPP x64 Debug (WindowsPlayer; Unity 6000.3.16f1)'
    )
    [System.IO.File]::WriteAllText($thirdRunPath, $thirdRunText)
    Assert-StabilityGateFails `
        -Description 'a non-Release repeat platform fails closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'recorded a non-published platform'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $thirdRunText = Get-Content -LiteralPath $thirdRunPath -Raw
    $thirdRunText = $thirdRunText.Replace(
        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    )
    [System.IO.File]::WriteAllText($thirdRunPath, $thirdRunText)
    Assert-StabilityGateFails `
        -Description 'a different measured commit in one repeat fails closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'did not preserve one platform and measured commit'
} finally {
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

# Exercise the actual event collector with a Windows event API fixture.
$collectorPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'collect-perf-host-characterization.ps1'
$collectorTokens = $null
$collectorErrors = $null
$collectorAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $collectorPath, [ref]$collectorTokens, [ref]$collectorErrors
)
Assert-That 'event collector parses' (@($collectorErrors).Count -eq 0)
$eventFunction = $collectorAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-HostEventEvidence'
}, $true)
Invoke-Expression $eventFunction.Extent.Text
$script:eventFixtureCount = 0
$script:eventFixtureMode = 'normal'
$script:eventQueries = New-Object System.Collections.Generic.List[object]
function Get-WinEvent {
    [CmdletBinding()]
    param([hashtable]$FilterHashtable, [int]$MaxEvents)
    $script:eventQueries.Add(@{ query = $FilterHashtable; maxEvents = $MaxEvents })
    if ($script:eventFixtureMode -eq 'denied') {
        throw [UnauthorizedAccessException]::new('System log access denied')
    }
    if ($script:eventFixtureMode -eq 'missing') {
        $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
            [InvalidOperationException]::new('No matching events'), 'NoMatchingEventsFound',
            [System.Management.Automation.ErrorCategory]::ObjectNotFound, $null
        ))
    }
    if ($FilterHashtable.ProviderName -ne 'Microsoft-Windows-Kernel-General') { return }
    for ($index = 0; $index -lt $script:eventFixtureCount; $index++) {
        $event = [pscustomobject]@{
            ProviderName = $FilterHashtable.ProviderName; Id = 1; RecordId = $index + 100
            TimeCreated = [DateTimeOffset]::Parse('2026-10-08T03:05:00Z').UtcDateTime
            RawXml = '<Event><Data Name="OldTime">2026-10-08T03:04:57Z</Data><Data Name="NewTime">2026-10-08T03:05:00Z</Data></Event>'
        }
        $event | Add-Member -MemberType ScriptMethod -Name ToXml -Value { $this.RawXml }
        $event
    }
}
try {
    $start = [DateTimeOffset]::Parse('2026-10-08T03:04:00Z')
    $end = [DateTimeOffset]::Parse('2026-10-08T03:06:00Z')
    foreach ($count in @(0, 1, 2)) {
        $script:eventFixtureCount = $count
        $script:eventQueries.Clear()
        $evidence = Get-HostEventEvidence -StartUtc $start -EndUtc $end
        Assert-That "event cardinality $count is retained" ($evidence.events.Count -eq $count)
        Assert-That 'all providers are queried and empty output is explicit' (
            $evidence.queries.Count -eq 3 -and $evidence.queries[1].status -eq 'no-matches' -and $evidence.errors.Count -eq 0
        )
        foreach ($call in $script:eventQueries) {
            Assert-That 'query reads System with exact UTC bounds and overflow detection' (
                $call.query.LogName -eq 'System' -and $call.query.StartTime -eq $start.UtcDateTime -and
                $call.query.EndTime -eq $end.UtcDateTime -and $call.query.StartTime.Kind -eq [DateTimeKind]::Utc -and
                $call.maxEvents -eq 1001
            )
        }
        if ($count -ne 0) {
            Assert-That 'raw clock payload is retained' (
                $evidence.events[0].xml.Contains('OldTime') -and $evidence.events[0].xml.Contains('NewTime')
            )
        }
    }
    $script:eventFixtureMode = 'missing'
    $evidence = Get-HostEventEvidence -StartUtc $start -EndUtc $end
    Assert-That 'native no-matches is successful empty evidence' (
        $evidence.events.Count -eq 0 -and $evidence.errors.Count -eq 0 -and
        @($evidence.queries | Where-Object status -eq 'no-matches').Count -eq 3
    )
    $script:eventFixtureMode = 'denied'
    $evidence = Get-HostEventEvidence -StartUtc $start -EndUtc $end
    Assert-That 'access failures are not successful empty evidence' (
        $evidence.errors.Count -eq 3 -and @($evidence.queries | Where-Object status -eq 'error').Count -eq 3
    )
    $script:eventFixtureMode = 'normal'
    $script:eventFixtureCount = 1001
    $evidence = Get-HostEventEvidence -StartUtc $start -EndUtc $end
    Assert-That 'overflow is bounded and explicitly incomplete' (
        $evidence.events.Count -eq 1000 -and $evidence.queries[0].status -eq 'truncated' -and $evidence.errors.Count -eq 1
    )
    foreach ($invalidEnd in @($start, $start.AddSeconds(-1), $start.AddHours(25))) {
        $rejected = $false
        try { Get-HostEventEvidence -StartUtc $start -EndUtc $invalidEnd | Out-Null }
        catch { $rejected = $_.Exception.Message.Contains('Event log window must be increasing') }
        Assert-That 'invalid query windows are rejected' $rejected
    }
} finally { Remove-Item Function:Get-WinEvent }

# Exercise the actual sensor helper's elapsed-time brackets around the UTC capture.
$sensorFunction = $collectorAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-SensorSample'
}, $true)
Invoke-Expression $sensorFunction.Extent.Text
$StopSignalPath = 'sensor-clock-fixture'
$script:sensorFixtureFailure = $false
function Get-Counter {
    [CmdletBinding()]
    param([string[]]$Counter)
    $script:counterReadTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
    if ($script:sensorFixtureFailure) { throw 'fixture counter failure' }
    return [pscustomobject]@{ CounterSamples = @([pscustomobject]@{
        InstanceName = '0,0'; Path = '\Processor Information(0,0)\Processor Frequency'; CookedValue = 3000
    }) }
}
function Get-NativePowerState {
    $script:nativeReadTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
    return [ordered]@{ status = 'fixture' }
}
try {
    foreach ($failure in @($false, $true)) {
        $script:sensorFixtureFailure = $failure
        $sample = Get-SensorSample
        Assert-That 'samples retain elapsed-time clock brackets' ($sample.Contains('monotonicClock'))
        $capture = $sample.monotonicClock
        Assert-That 'clock metadata describes the actual runtime counter' (
            $capture.frequencyTicksPerSecond -ceq [string][System.Diagnostics.Stopwatch]::Frequency -and
            $capture.isHighResolution -eq [System.Diagnostics.Stopwatch]::IsHighResolution
        )
        Assert-That 'sensor reads occur before the UTC bracket' (
            [long]$capture.readStartTicks -le $script:counterReadTicks -and
            $script:counterReadTicks -le $script:nativeReadTicks -and
            $script:nativeReadTicks -le [long]$capture.utcBeforeTicks -and
            [long]$capture.utcBeforeTicks -le [long]$capture.utcAfterTicks
        )
        $copy = $sample | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        foreach ($field in @('frequencyTicksPerSecond', 'readStartTicks', 'utcBeforeTicks', 'utcAfterTicks')) {
            Assert-That "clock $field retains decimal-string precision through JSON" (
                $capture.$field -is [string] -and $capture.$field -cmatch '^\d+$' -and
                $copy.monotonicClock.$field -is [string] -and $copy.monotonicClock.$field -ceq $capture.$field
            )
        }
        Assert-That 'clock diagnostics do not hide sensor errors' (
            $sample.errors.Count -eq [int]$failure -and
            (-not $failure -or $sample.errors[0].Contains('fixture counter failure'))
        )
    }
} finally {
    Remove-Item Function:Get-Counter, Function:Get-NativePowerState
    Remove-Variable StopSignalPath
}

# The comparison host must supply the real dependencies of pinned source.
$PackageName = 'com.wallstop-studios.dxmessaging'
$TestFrameworkVersion = '1.4.5'
$PerformanceFrameworkVersion = '3.4.2'
$comparisonManifest = New-ManifestJson -Root $repoRoot -RepoRoot $repoRoot -IncludeComparisons | ConvertFrom-Json
foreach ($name in @('com.unity.modules.physics', 'com.unity.modules.physics2d')) {
    $module = $comparisonManifest.dependencies.PSObject.Properties[$name]
    Assert-That "comparison manifest includes required $name" ($module -and $module.Value -ceq '1.0.0')
}
$jobsDependency = $comparisonManifest.dependencies.PSObject.Properties['com.unity.jobs']
Assert-That 'comparison manifest pins the actual old batch provider' ($jobsDependency -and $jobsDependency.Value -ceq '0.50.0-preview.9')
foreach ($mode in @('ordinary', 'shipping')) {
    $plain = New-ManifestJson -Root $repoRoot -RepoRoot $repoRoot -ShippingFidelity:($mode -ceq 'shipping') | ConvertFrom-Json
    Assert-That "$mode manifest has no optional Jobs dependency" (-not $plain.dependencies.PSObject.Properties['com.unity.jobs'])
}

# Exercise the actual opt-in SDK floor routing and complete-scope gates.
$floorWorkflowPath = Join-Path $repoRoot '.github/workflows/runner-bootstrap.yml'
$floorMetadata = & node -e 'const fs=require("fs"), yaml=require("yaml"); console.log(JSON.stringify(yaml.parse(fs.readFileSync(process.argv[1],"utf8"))));' $floorWorkflowPath
if ($LASTEXITCODE -ne 0) { throw 'Cannot load the real SDK floor workflow.' }
$floorWorkflow = $floorMetadata | ConvertFrom-Json
$floor = $floorWorkflow.jobs.'native-sdk-floor'
Assert-That 'floor skips the unlicensed bootstrap job' ($floorWorkflow.jobs.bootstrap.if -ceq '${{ !inputs[''native-sdk-floor''] && inputs[''native-sdk-log-source''] == '''' && !inputs[''native-sdk-registry-probe''] && !inputs[''native-sdk-upm-network-probe''] }}')
Assert-That 'floor requires registration preflight and a 600-minute budget' ($floor.needs[0] -ceq 'runner-preflight' -and $floor.'timeout-minutes' -eq 600)
$floorWork = @($floor.steps | Where-Object { $_.PSObject.Properties['id'] -and $_.id -ceq 'run_sdk_floor' })[0]
Assert-That 'floor keeps all CPU/Burst allocation cases in PlayMode' ($floorWork.run -match '-TestMode playmode' -and $floorWork.run -match '-TestCategory NativeSdkCpu' -and $floorWork.run -notmatch '-CanonicalProfilePath|-StandalonePlayerBatchOrders')
$floorExpected = Get-Content -LiteralPath (Join-Path $repoRoot '.github/perf/native-sdk-floor-identities.v1.json') -Raw | ConvertFrom-Json
Assert-That 'floor retains 745 distinct identities' ($floorExpected.caseCount -eq 745 -and @($floorExpected.identities | Sort-Object -Unique).Count -eq 745)
$floorRoute = [scriptblock]::Create($floor.steps[0].run)
$floorVerifyText = @($floor.steps | Where-Object { $_.name -ceq 'Require the complete CPU/Burst floor scope' })[0].run.Replace('${{ github.run_id }}', '1').Replace('${{ github.run_attempt }}', '1')
$floorVerify = [scriptblock]::Create($floorVerifyText)
$floorFixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('dxm-sdk-floor-' + [Guid]::NewGuid().ToString('N'))
$floorLocation = Get-Location
$floorEnvironment = @{}
foreach ($key in @('DXM_FLOOR_REQUEST', 'RUNNER_NAME', 'DXM_FLOOR_OTHER_MODES')) { $floorEnvironment[$key] = [Environment]::GetEnvironmentVariable($key) }
try {
    foreach ($route in @(
        @{ request = 'ELI-MACHINE'; runner = 'ELI-MACHINE'; other = 'false'; accepted = $true },
        @{ request = 'DAD-MACHINE'; runner = 'ELI-MACHINE'; other = 'false'; accepted = $false },
        @{ request = 'ELI-MACHINE'; runner = 'DAD-MACHINE'; other = 'false'; accepted = $false },
        @{ request = 'ELI-MACHINE'; runner = 'ELI-MACHINE'; other = 'true'; accepted = $false }
    )) {
        $env:DXM_FLOOR_REQUEST = $route.request; $env:RUNNER_NAME = $route.runner; $env:DXM_FLOOR_OTHER_MODES = $route.other
        $accepted = $true
        try { & $floorRoute } catch { $accepted = $false }
        Assert-That "floor routing request=$($route.request),runner=$($route.runner),other=$($route.other)" ($accepted -eq $route.accepted)
    }
    $floorArtifacts = Join-Path $floorFixtureRoot '.artifacts/unity/native-sdk-floor/1-1'
    New-Item -ItemType Directory -Path $floorArtifacts -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $floorFixtureRoot '.github/perf') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot '.github/perf/native-sdk-floor-identities.v1.json') -Destination (Join-Path $floorFixtureRoot '.github/perf/native-sdk-floor-identities.v1.json')
    Set-Location -LiteralPath $floorFixtureRoot
    foreach ($variant in @('complete', 'missing', 'duplicate', 'skipped', 'wrong-unity', 'missing-jobs', 'wrong-jobs', 'wrong-provider', 'wrong-provider-package', 'wrong-provider-version', 'foreign-source', 'missing-source-hash', 'missing-diagnostic', 'diagnostic-error', 'wrong-phase', 'wrong-wrapper', 'before-state-read', 'missing-trace', 'trace-error', 'trace-debug', 'trace-runtime', 'trace-main', 'trace-truncated', 'trace-changed', 'trace-empty', 'trace-size', 'trace-name', 'trace-hash', 'input-marker', 'missing-inputs', 'input-phase', 'input-path', 'input-status', 'input-hash', 'input-time', 'input-length', 'input-duplicate')) {
        $names = @($floorExpected.identities)
        if ($variant -ceq 'missing') { $names = @($names[1..($names.Count - 1)]) }
        if ($variant -ceq 'duplicate') { $names[0] = $names[1] }
        $version = if ($variant -ceq 'wrong-unity') { '6000.4.6f1' } else { '2021.3.45f1' }
        $pins = [ordered]@{ 'com.unity.burst' = '1.6.6'; 'com.unity.collections' = '1.2.3'; 'com.unity.mathematics' = '1.2.6'; 'com.unity.jobs' = '0.50.0-preview.9' }
        $packages = @($pins.Keys | ForEach-Object { @{ name = $_; actualVersion = $pins[$_]; expectedVersion = $pins[$_]; source = 'Registry'; compilerSources = @(@{ path = 'package.json'; sha256 = ('a' * 64) }) } })
        if ($variant -ceq 'missing-jobs') { $packages = @($packages[0..2]) }
        if ($variant -ceq 'wrong-jobs') { $packages[3].actualVersion = '0.70.0-preview.7' }
        if ($variant -ceq 'foreign-source') { $packages[3].source = 'Local' }
        if ($variant -ceq 'missing-source-hash') { $packages[3].compilerSources[0].sha256 = '' }
        $provider = if ($variant -ceq 'wrong-provider') { 'Unity.Collections' } else { 'Unity.Jobs' }
        $providerPackage = if ($variant -ceq 'wrong-provider-package') { 'com.unity.collections' } else { 'com.unity.jobs' }
        $providerVersion = if ($variant -ceq 'wrong-provider-version') { '0.70.0-preview.7' } else { '0.50.0-preview.9' }
        Write-TestJson -Path (Join-Path $floorArtifacts 'sdk-admission.json') -Value @{ schemaVersion = 2; unityVersion = $version; packages = $packages; batchAssembly = $provider; batchPackage = $providerPackage; batchVersion = $providerVersion }
        foreach ($phase in @('before', 'after')) {
            $diagnosticPath = Join-Path $floorArtifacts "sdk-direct-call.$phase.json"
            if ($variant -ceq 'missing-diagnostic' -and $phase -ceq 'after') {
                if (Test-Path -LiteralPath $diagnosticPath) { Remove-Item -LiteralPath $diagnosticPath }
                continue
            }
            $records = @(
                @{ declaringType = 'DxMessaging.Tests.Runtime.NativeCollectionsResearch.PureBatchKernels'; methodName = 'Direct'; ilHex = '00282a'; generatedTypes = @() },
                @{ declaringType = 'DxMessaging.Tests.Runtime.NativeCollectionsResearch.VectorPureKernels'; methodName = 'ScalarDirect'; ilHex = '00282a'; generatedTypes = @() },
                @{ declaringType = 'DxMessaging.Tests.Runtime.NativeCollectionsResearch.VectorPureKernels'; methodName = 'VectorDirect'; ilHex = '00282a'; generatedTypes = @() }
            )
            if ($variant -ceq 'wrong-wrapper') { $records[0].methodName = 'Pointer' }
            $errors = @(if ($variant -ceq 'diagnostic-error') { 'fixture observation error' })
            $recordPhase = if ($variant -ceq 'wrong-phase') { 'unknown' } else { $phase }
            $stateCaptured = $phase -ceq 'after' -or $variant -ceq 'before-state-read'
            Write-TestJson -Path $diagnosticPath -Value @{ schemaVersion = 1; phase = $recordPhase; stateCaptured = $stateCaptured; methods = $records; errors = $errors }
        }
        $tracePath = Join-Path $floorArtifacts 'sdk-burst-trace.json'
        if ($variant -ceq 'missing-trace') {
            if (Test-Path -LiteralPath $tracePath) { Remove-Item -LiteralPath $tracePath }
        } else {
            $logs = @('burst.log', 'burst-thread-0.log' | ForEach-Object {
                @{ name = $_; status = 'ok'; sourceLengthBytes = 7; capturedBytes = 7; sha256 = ('a' * 64); content = 'fixture' }
            })
            if ($variant -ceq 'trace-main') { $logs[0].name = 'burst-thread-1.log' }
            if ($variant -ceq 'trace-truncated') { $logs[0].status = 'truncated' }
            if ($variant -ceq 'trace-changed') { $logs[0].status = 'changed' }
            if ($variant -ceq 'trace-empty') { $logs[0].content = '' }
            if ($variant -ceq 'trace-size') { $logs[0].sourceLengthBytes = 4 * 1024 * 1024 + 1 }
            if ($variant -ceq 'trace-name') { $logs[0].name = '../foreign.log' }
            if ($variant -ceq 'trace-hash') { $logs[0].sha256 = '' }
            $runtimeFiles = @(
                @{ name = 'Burst.Compiler.IL.dll'; loadedAssemblyObserved = $false; sha256 = 'c425260738fdc8afe30520acba118df0fb488bbaaff98a912b3f3e4e6e850b8b' },
                @{ name = 'Burst.Backend.dll'; loadedAssemblyObserved = $false; sha256 = '8c927c75b2bc3aca96169bbe1a9076b37889f1ed395b22f16f37061a5ce16298' }
            )
            if ($variant -ceq 'trace-runtime') { $runtimeFiles[0].sha256 = 'b' * 64 }
            $traceErrors = @(if ($variant -ceq 'trace-error') { 'fixture trace error' })
            $debug = if ($variant -ceq 'trace-debug') { '3' } else { '1' }
            Write-TestJson -Path $tracePath -Value @{ schemaVersion = 1; debugLevel = $debug; logs = $logs; runtimeFiles = $runtimeFiles; errors = $traceErrors }
        }
        [IO.File]::WriteAllText((Join-Path $floorArtifacts 'compiler-inputs-complete.marker'), $(if ($variant -ceq 'input-marker') { 'incomplete' } else { 'DxmCiTestConfigurator.PrepareCompilerInputs completed' }))
        foreach ($phase in @('before', 'after')) {
            $inputPath = Join-Path $floorArtifacts "compiler-inputs.$phase.json"
            $inputFiles = @('Assets/csc.rsp', 'Assets/Editor/DxMessaging.BaseCallIgnore.txt' | ForEach-Object {
                @{ path = $_; status = 'present'; lengthBytes = 0; sha256 = 'a' * 64; lastWriteUtc = '2026-10-09T00:00:00Z' }
            })
            if ($phase -ceq 'after') {
                if ($variant -ceq 'input-path') { $inputFiles[0].path = 'Assets/foreign.rsp' }
                if ($variant -ceq 'input-status') { $inputFiles[0].status = 'missing' }
                if ($variant -ceq 'input-hash') { $inputFiles[0].sha256 = 'b' * 64 }
                if ($variant -ceq 'input-time') { $inputFiles[0].lastWriteUtc = '2026-10-09T00:01:00Z' }
                if ($variant -ceq 'input-length') { $inputFiles[0].lengthBytes = 1 }
                if ($variant -ceq 'input-duplicate') { $inputFiles[1].path = $inputFiles[0].path }
            }
            if ($variant -ceq 'missing-inputs' -and $phase -ceq 'before') {
                Remove-Item -LiteralPath $inputPath -ErrorAction SilentlyContinue
            } else {
                $inputPhase = if ($variant -ceq 'input-phase' -and $phase -ceq 'before') { 'after' } else { $phase }
                Write-TestJson -Path $inputPath -Value @{ schemaVersion = 1; phase = $inputPhase; files = $inputFiles }
            }
        }
        $xml = [System.Text.StringBuilder]::new('<test-run>')
        for ($index = 0; $index -lt $names.Count; $index++) {
            $result = if ($variant -ceq 'skipped' -and $index -eq 0) { 'Skipped' } else { 'Passed' }
            $escaped = [System.Security.SecurityElement]::Escape($names[$index])
            [void]$xml.Append("<test-case fullname=`"$escaped`" result=`"$result`"/>")
        }
        [void]$xml.Append('</test-run>')
        [System.IO.File]::WriteAllText((Join-Path $floorArtifacts 'results.xml'), $xml.ToString())
        $accepted = $true
        try { & $floorVerify } catch { $accepted = $false; if ($variant -ceq 'complete') { throw } }
        Assert-That "floor complete-scope gate variant=$variant" ($accepted -eq ($variant -ceq 'complete'))
    }
} finally {
    Set-Location -LiteralPath $floorLocation.Path
    foreach ($key in $floorEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $floorEnvironment[$key]) }
    if (Test-Path -LiteralPath $floorFixtureRoot) { Remove-Item -LiteralPath $floorFixtureRoot -Recurse -Force }
}

# Read real files through the package collector, including bounded and invalid input.
$packageFileFunction = $collectorAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-UnityPackageFileEvidence'
}, $true)
Assert-That 'package and native diagnostics share the actual bounded file reader' ($null -ne $packageFileFunction)
Invoke-Expression $packageFileFunction.Extent.Text
$packageFunction = $collectorAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-UnityPackageLogEvidence'
}, $true)
Invoke-Expression $packageFunction.Extent.Text
$packageFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('dxm-package-logs-' + [Guid]::NewGuid().ToString('N'))
$packageEnvironment = @{}
foreach ($key in @('LOCALAPPDATA', 'ALLUSERSPROFILE', 'DXM_LOG_SOURCE', 'DXM_REQUESTED_RUNNER', 'RUNNER_NAME', 'DXM_OTHER_LOG_MODES')) {
    $packageEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    $env:LOCALAPPDATA = Join-Path $packageFixtureRoot 'user'
    $env:ALLUSERSPROFILE = Join-Path $packageFixtureRoot 'system'
    $project = Join-Path $packageFixtureRoot 'project'
    $cache = Join-Path $packageFixtureRoot 'cache'
    foreach ($path in @((Join-Path $env:LOCALAPPDATA 'Unity/Editor'), (Join-Path $env:ALLUSERSPROFILE 'Unity/Editor'),
        (Join-Path $project 'Packages'), (Join-Path $project 'Library/PackageCache'), $cache)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    $userLog = Join-Path $env:LOCALAPPDATA 'Unity/Editor/upm.log'
    $systemLog = Join-Path $env:ALLUSERSPROFILE 'Unity/Editor/upm.log'
    [IO.File]::WriteAllText($userLog, 'user package failure')
    [IO.File]::WriteAllText($systemLog, 'system package failure')
    [IO.File]::WriteAllText((Join-Path $project 'Packages/manifest.json'), '{"dependencies":{}}')
    $before = (Get-FileHash -LiteralPath $userLog).Hash
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache
    Assert-That 'both documented account logs are retained without changing source' (
        $evidence.errors.Count -eq 0 -and $evidence.files[0].content -ceq 'user package failure' -and
        $evidence.files[2].content -ceq 'system package failure' -and
        (Get-FileHash -LiteralPath $userLog).Hash -ceq $before
    )
    Assert-That 'missing logs, lock and cache children remain explicit' (
        $evidence.files[1].status -ceq 'missing' -and $evidence.files[5].status -ceq 'missing' -and
        $evidence.directories[3].status -ceq 'missing'
    )
    $diagnostics = Join-Path $packageFixtureRoot 'installed-diagnostics'
    New-Item -ItemType Directory -Path (Join-Path $diagnostics 'nested') -Force | Out-Null
    $launcher = Join-Path $diagnostics 'RunUnityPackageManagerDiagnostics.bat'
    $launcherText = [string]::Join("`r`n", @('@echo off', 'echo unexpected > "%~dp0executed.txt"', ''))
    [IO.File]::WriteAllText($launcher, $launcherText)
    [IO.File]::WriteAllText((Join-Path $diagnostics 'nested/should-not-read.txt'), 'private child fixture')
    $launcherHash = (Get-FileHash -LiteralPath $launcher).Hash
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache -InstalledDiagnostics $diagnostics
    $launcherRecord = @($evidence.files | Where-Object { $_.kind -ceq 'diagnostic-launcher' })[0]
    Assert-That 'installed launcher capture reads exact text and shallow metadata without execution or mutation' (
        $evidence.errors.Count -eq 0 -and $launcherRecord.status -ceq 'ok' -and
        $launcherRecord.content -ceq $launcherText -and
        (Get-FileHash -LiteralPath $launcher).Hash -ceq $launcherHash -and
        -not (Test-Path -LiteralPath (Join-Path $diagnostics 'executed.txt')) -and
        @($evidence.directories[-1].entries | Where-Object { $_.name -ceq 'nested' -and $_.directory }).Count -eq 1 -and
        @($evidence.files | Where-Object { $_.path.EndsWith('should-not-read.txt') }).Count -eq 0
    )
    Remove-Item -LiteralPath $launcher
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache -InstalledDiagnostics $diagnostics
    Assert-That 'missing requested launcher is explicit and fails completeness' (
        $evidence.files[-1].status -ceq 'missing' -and $evidence.errors.Count -gt 0
    )
    [IO.File]::WriteAllBytes($launcher, [byte[]]@(0xff, 0xff, 0xff))
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache -InstalledDiagnostics $diagnostics
    Assert-That 'invalid installed launcher text is a read error' (
        $evidence.files[-1].status -ceq 'error' -and $evidence.errors.Count -gt 0
    )
    [IO.File]::WriteAllText($launcher, ('x' * (4 * 1024 * 1024 + 1)))
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache -InstalledDiagnostics $diagnostics
    Assert-That 'oversized launcher retains a bounded prefix and cannot become complete' (
        $evidence.files[-1].status -ceq 'truncated' -and
        $evidence.files[-1].content.Length -eq 4 * 1024 * 1024 -and $evidence.errors.Count -gt 0
    )
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache
    Assert-That 'default log collection does not inspect an unrequested diagnostic installation' (
        $evidence.errors.Count -eq 0 -and $evidence.files.Count -eq 6 -and $evidence.directories.Count -eq 5
    )
    [IO.File]::WriteAllText($userLog, ('x' * (4 * 1024 * 1024 + 1)))
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache
    Assert-That 'oversized logs retain a bounded prefix and fail completeness' (
        $evidence.files[0].status -ceq 'truncated' -and $evidence.files[0].content.Length -eq 4 * 1024 * 1024 -and
        $evidence.errors.Count -eq 1
    )
    [IO.File]::WriteAllBytes($userLog, [byte[]]@(0xff, 0xff, 0xff))
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache
    Assert-That 'invalid text is an error rather than an empty success' (
        $evidence.files[0].status -ceq 'error' -and $evidence.errors.Count -eq 1
    )
    Remove-Item -LiteralPath $userLog, $systemLog
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache
    Assert-That 'absent service logs fail completeness with retained missing statuses' (
        $evidence.files[0].status -ceq 'missing' -and $evidence.errors.Count -eq 1
    )
    [IO.File]::WriteAllText($systemLog, 'system log')
    for ($index = 0; $index -lt 201; $index++) {
        [IO.File]::WriteAllText((Join-Path $cache "entry-$index"), 'metadata only')
    }
    $evidence = Get-UnityPackageLogEvidence -Project $project -Cache $cache
    Assert-That 'directory inventories are bounded and explicitly incomplete' (
        $evidence.directories[2].status -ceq 'truncated' -and $evidence.directories[2].entries.Count -eq 200 -and
        $evidence.errors.Count -eq 1
    )
    $capture = @($floorWorkflow.jobs.'package-log-capture'.steps | Where-Object { $_.name -ceq 'Capture retained Unity Package Manager diagnostics' })[0]
    Assert-That 'workflow reads the fixed managed diagnostic launcher without invoking it' (
        $capture.run.Contains('u6-v3/2021.3.45f1/Editor/Data/Resources/PackageManager/Diagnostics') -and
        $capture.run.Contains('-InstalledDiagnosticsPath $diagnostics') -and
        -not $capture.run.Contains('RunUnityPackageManagerDiagnostics.bat')
    )
    $route = [scriptblock]::Create(($capture.run -split '\$source =', 2)[0])
    foreach ($case in @(
        @{ request = 'ELI-MACHINE'; runner = 'ELI-MACHINE'; other = 'false'; source = '37859571795,1'; accepted = $true },
        @{ request = 'DAD-MACHINE'; runner = 'ELI-MACHINE'; other = 'false'; source = '37859571795,1'; accepted = $false },
        @{ request = 'ELI-MACHINE'; runner = 'DAD-MACHINE'; other = 'false'; source = '37859571795,1'; accepted = $false },
        @{ request = 'ELI-MACHINE'; runner = 'ELI-MACHINE'; other = 'true'; source = '37859571795,1'; accepted = $false },
        @{ request = 'ELI-MACHINE'; runner = 'ELI-MACHINE'; other = 'false'; source = '../outside,1'; accepted = $false },
        @{ request = 'ELI-MACHINE'; runner = 'ELI-MACHINE'; other = 'false'; source = '123,0'; accepted = $false }
    )) {
        $env:DXM_REQUESTED_RUNNER = $case.request; $env:RUNNER_NAME = $case.runner
        $env:DXM_OTHER_LOG_MODES = $case.other; $env:DXM_LOG_SOURCE = $case.source
        $accepted = $true
        try { & $route } catch { $accepted = $false }
        Assert-That "package log admission source=$($case.source),runner=$($case.runner),other=$($case.other)" ($accepted -eq $case.accepted)
    }
    $redaction = @($floorWorkflow.jobs.'package-log-capture'.steps | Where-Object { $_.name -ceq 'Redact retained package log artifacts' })[0]
    $upload = @($floorWorkflow.jobs.'package-log-capture'.steps | Where-Object { $_.name -ceq 'Upload retained package log artifacts' })[0]
    $tooling = @($floorWorkflow.jobs.'package-log-capture'.steps | Where-Object {
        $_.PSObject.Properties['id'] -and $_.id -ceq 'log_tooling'
    })[0]
    Assert-That 'package logs install the locked redactor dependencies without lifecycle scripts' (
        $tooling.run.Contains('Copy-Item package.json, package-lock.json') -and
        ($tooling.run -match '\bnpm\s+ci\s+--prefix') -and $tooling.run.Contains('--ignore-scripts') -and
        $tooling.run.Contains('NODE_PATH=') -and $tooling.'timeout-minutes' -eq 2
    )
    Assert-That 'package log upload requires this run redaction and registration preflight' (
        $floorWorkflow.jobs.'package-log-capture'.needs[0] -ceq 'runner-preflight' -and
        $floorWorkflow.jobs.'package-log-capture'.'timeout-minutes' -eq 10 -and
        $redaction.uses -ceq './.github/actions/redact-unity-artifacts' -and
        $redaction.if.Contains("steps.log_node.outcome == 'success'") -and
        $redaction.if.Contains("steps.log_tooling.outcome == 'success'") -and
        $upload.if.Contains("steps.redact_logs.outcome == 'success'") -and
        $floor.if.Contains("inputs['native-sdk-log-source'] == ''") -and
        $floorWorkflow.jobs.'pilot-contract-smoke'.if.Contains("inputs['native-sdk-log-source'] == ''")
    )
} finally {
    foreach ($key in $packageEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $packageEnvironment[$key]) }
    if (Test-Path -LiteralPath $packageFixtureRoot) { Remove-Item -LiteralPath $packageFixtureRoot -Recurse -Force }
}

# Exercise the real bounded HTTP helper with synthetic responses, never real package claims.
$downloadFunction = $collectorAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-RegistryArchiveEvidence'
}, $true)
Invoke-Expression $downloadFunction.Extent.Text
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public sealed class RegistryFixtureHandler : HttpMessageHandler
{
    public string Mode;
    public int Requests;
    public string Uri;
    public bool HadAuthorization;
    public bool Cancellable;
    public static readonly byte[] Body = Encoding.UTF8.GetBytes("synthetic archive fixture");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        Uri = request.RequestUri.ToString();
        HadAuthorization = request.Headers.Authorization != null;
        Cancellable = token.CanBeCanceled;
        HttpResponseMessage response = new HttpResponseMessage(
            Mode == "http" ? HttpStatusCode.ServiceUnavailable :
            Mode == "redirect" ? HttpStatusCode.Found : HttpStatusCode.OK);
        response.Content = Mode == "read-error" ?
            (HttpContent)new StreamContent(new RegistryFailingStream()) :
            new ByteArrayContent(Mode == "empty" ? new byte[0] : Body);
        if (Mode == "oversized") response.Content.Headers.ContentLength = 536870913L;
        if (Mode == "wrong-length") response.Content.Headers.ContentLength = Body.Length + 1;
        return Task.FromResult(response);
    }
}
public sealed class RegistryFailingStream : Stream
{
    private bool sent;
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (sent) throw new IOException("fixture aborted after partial body");
        sent = true;
        buffer[offset] = 1;
        return 1;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        try { return Task.FromResult(Read(buffer, offset, count)); }
        catch (Exception error) { return Task.FromException<int>(error); }
    }
    public override void Flush() { throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
}
'@
$fixtureHasher = [Security.Cryptography.SHA1]::Create()
try {
    $fixtureDigest = [BitConverter]::ToString($fixtureHasher.ComputeHash([RegistryFixtureHandler]::Body)).Replace('-', '').ToLowerInvariant()
} finally { $fixtureHasher.Dispose() }
foreach ($archiveRoute in @('Gateway', 'Cdn')) {
    $expectedUrl = if ($archiveRoute -ceq 'Cdn') {
        'https://cdn.packages.unity.com/tarballs/com.unity.burst/com.unity.burst-1.6.6/da63315718cf3bf3d11ff958633b4b67dc8d2426.tgz'
    } else {
        'https://download.packages.unity.com/com.unity.burst/-/com.unity.burst-1.6.6.tgz'
    }
    foreach ($mode in @('ok', 'http', 'redirect', 'oversized', 'read-error', 'wrong-length', 'empty', 'digest')) {
        $handler = [RegistryFixtureHandler]::new(); $handler.Mode = $mode
        $client = [Net.Http.HttpClient]::new($handler)
        try {
            $expected = if ($mode -eq 'digest') { '0' * 40 } else { $fixtureDigest }
            $result = if ($archiveRoute -ceq 'Gateway') {
                # Omit the route to exercise the retained original default case.
                Get-RegistryArchiveEvidence -ExpectedSha1 $expected -Client $client
            } else {
                Get-RegistryArchiveEvidence -ExpectedSha1 $expected -ArchiveRoute $archiveRoute -Client $client
            }
            Assert-That "HTTP fixture $archiveRoute/$mode uses one fixed cancellable request without credentials" (
                $handler.Requests -eq 1 -and $handler.Cancellable -and -not $handler.HadAuthorization -and
                $handler.Uri -ceq $expectedUrl -and $result.archiveRoute -ceq $archiveRoute
            )
            Assert-That "HTTP fixture $archiveRoute/$mode preserves outcome and declared limits" (
                ($result.status -ceq 'success') -eq ($mode -eq 'ok') -and
                $result.maximumBytes -eq 536870912L -and $result.deadlineSeconds -eq 120
            )
            if ($mode -eq 'ok') {
                Assert-That 'complete download retains both digests and byte count' (
                    $result.bodyComplete -and $result.digestMatches -and $result.sha1 -ceq $fixtureDigest -and
                    $result.sha256.Length -eq 64 -and $result.bytesReceived -eq [RegistryFixtureHandler]::Body.Length
                )
            } elseif ($mode -eq 'read-error') {
                Assert-That 'partial body errors preserve prefix evidence without completeness' (
                    -not $result.bodyComplete -and $result.bytesReceived -eq 1 -and $result.sha256.Length -eq 64 -and
                    $result.error.Contains('fixture aborted after partial body')
                )
            } elseif ($mode -eq 'wrong-length' -or $mode -eq 'empty') {
                Assert-That 'EOF does not admit an empty or length-mismatched body' ($result.eofReached -and -not $result.bodyComplete)
            }
        } finally { $client.Dispose() }
    }
}

$handler = [RegistryFixtureHandler]::new(); $handler.Mode = 'ok'
$client = [Net.Http.HttpClient]::new($handler)
try {
    $rejected = $false
    try {
        Get-RegistryArchiveEvidence -ExpectedSha1 $fixtureDigest -ArchiveRoute 'https://example.com/archive.tgz' -Client $client | Out-Null
    } catch { $rejected = $true }
    Assert-That 'arbitrary archive URL is rejected before any request' ($rejected -and $handler.Requests -eq 0)
    $result = Get-RegistryArchiveEvidence -ExpectedSha1 $fixtureDigest -ArchiveRoute 'cdn' -Client $client
    Assert-That 'case-insensitive Cdn parameter still selects the canonical fixed object' (
        $result.status -ceq 'success' -and $result.url.StartsWith('https://cdn.packages.unity.com/')
    )
} finally { $client.Dispose() }
# Exercise actual invocation/report admission with a synthetic native boundary.
$networkFunction = $collectorAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Invoke-InstalledUpmDiagnostic'
}, $true)
Assert-That 'collector defines the actual installed UPM diagnostic invocation' ($null -ne $networkFunction)
Invoke-Expression $networkFunction.Extent.Text
$networkFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('dxm-upm-network-' + [Guid]::NewGuid().ToString('N'))
$networkLocalAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = Join-Path $networkFixtureRoot 'service profile with spaces'
    $profileLog = Join-Path $env:LOCALAPPDATA 'Unity/Editor/upm-diag.log'
    New-Item -ItemType Directory -Path (Split-Path -Parent $profileLog) -Force | Out-Null
    $diagnosticDirectory = Join-Path $networkFixtureRoot 'package manager with spaces/Diagnostics'
    $nativeBinary = Join-Path $diagnosticDirectory 'bin/UnityPackageManagerDiagnostics.exe'
    $nativeServer = Join-Path $networkFixtureRoot 'package manager with spaces/Server/UnityPackageManager.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $nativeBinary), (Split-Path -Parent $nativeServer) -Force | Out-Null
    foreach ($networkFixtureMode in @('ok', 'nonzero', 'missing-exit', 'string-exit', 'launch-error',
        'missing-report', 'empty-report', 'oversized-report', 'missing-binary', 'missing-server', 'stale-output',
        'missing-log', 'empty-log', 'stale-log', 'invalid-log', 'oversized-log',
        'invalid-prior-log', 'oversized-prior-log', 'unset-log-environment')) {
        [IO.File]::WriteAllText($nativeBinary, 'synthetic native binary boundary')
        [IO.File]::WriteAllText($nativeServer, 'synthetic package manager boundary')
        $env:LOCALAPPDATA = Join-Path $networkFixtureRoot 'service profile with spaces'
        [IO.File]::WriteAllText($profileLog, 'prior report-creation error retained before invocation')
        $priorLogHash = (Get-FileHash -LiteralPath $profileLog).Hash
        $nativeHash = (Get-FileHash -LiteralPath $nativeBinary).Hash
        $caseOutput = Join-Path $networkFixtureRoot "output with spaces/$networkFixtureMode/evidence.json"
        $caseReport = Join-Path (Split-Path -Parent $caseOutput) 'upm-network-report'
        $caseReportFile = Join-Path $caseReport 'upm-diagnostic-report.txt'
        if ($networkFixtureMode -ceq 'invalid-prior-log') { [IO.File]::WriteAllBytes($profileLog, [byte[]]@(0xff, 0xff, 0xff)) }
        if ($networkFixtureMode -ceq 'oversized-prior-log') { [IO.File]::WriteAllText($profileLog, ('x' * (4 * 1024 * 1024 + 1))) }
        if ($networkFixtureMode -ceq 'unset-log-environment') { $env:LOCALAPPDATA = '' }
        if ($networkFixtureMode -ceq 'missing-binary') { Remove-Item -LiteralPath $nativeBinary }
        if ($networkFixtureMode -ceq 'missing-server') { Remove-Item -LiteralPath $nativeServer }
        if ($networkFixtureMode -ceq 'stale-output') {
            New-Item -ItemType Directory -Path $caseReport -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $caseReport 'upm-diagnostic-report.txt'), 'stale report must not admit a new run')
        }
        $nativeCalls = [Collections.Generic.List[string]]::new()
        $execute = {
            param($nativeFile, $nativeArguments, $consolePath)
            $nativeCalls.Add($nativeFile)
            Assert-That 'native diagnostic arguments preserve four fixed tokens and paths with spaces' (
                $nativeFile -ceq $nativeBinary -and $nativeArguments.Count -eq 4 -and
                $nativeArguments[0] -ceq '-o' -and $nativeArguments[1] -ceq $caseReportFile -and
                $nativeArguments[2] -ceq '-p' -and $nativeArguments[3] -ceq $nativeServer
            )
            Assert-That 'prelaunch capture does not alter the native profile log' ((Get-FileHash -LiteralPath $profileLog).Hash -ceq $priorLogHash)
            if ($networkFixtureMode -ceq 'launch-error') { throw [IO.IOException]::new('synthetic launch refused') }
            [IO.File]::WriteAllText($consolePath, 'synthetic native console')
            if ($networkFixtureMode -ceq 'missing-log') { Remove-Item -LiteralPath $profileLog }
            elseif ($networkFixtureMode -ceq 'empty-log') { [IO.File]::WriteAllText($profileLog, '') }
            elseif ($networkFixtureMode -ceq 'invalid-log') { [IO.File]::WriteAllBytes($profileLog, [byte[]]@(0xff, 0xff, 0xff)) }
            elseif ($networkFixtureMode -ceq 'oversized-log') { [IO.File]::WriteAllText($profileLog, ('x' * (4 * 1024 * 1024 + 1))) }
            elseif ($networkFixtureMode -cne 'stale-log') { [IO.File]::WriteAllText($profileLog, 'current native diagnostic log') }
            if ($networkFixtureMode -cne 'missing-report') {
                $body = if ($networkFixtureMode -ceq 'empty-report') { '' }
                    elseif ($networkFixtureMode -ceq 'oversized-report') { 'x' * (4 * 1024 * 1024 + 1) }
                    else { 'synthetic native report' }
                [IO.File]::WriteAllText($nativeArguments[1], $body)
            }
            if ($networkFixtureMode -ceq 'missing-exit') { return }
            if ($networkFixtureMode -ceq 'string-exit') { return '0' }
            if ($networkFixtureMode -ceq 'nonzero') { return 7 }
            return 0
        }.GetNewClosure()
        $result = Invoke-InstalledUpmDiagnostic -DiagnosticsDirectory $diagnosticDirectory -EvidencePath $caseOutput -CollectorSourcePath $collectorPath -Execute $execute
        $expectedCalls = if ($networkFixtureMode -in @('missing-binary', 'missing-server', 'stale-output',
            'invalid-prior-log', 'oversized-prior-log', 'unset-log-environment')) { 0 } else { 1 }
        Assert-That "native diagnostic $networkFixtureMode retains its actual invocation/completeness outcome" (
            ($result.status -ceq 'completed') -eq ($networkFixtureMode -ceq 'ok') -and
            $nativeCalls.Count -eq $expectedCalls -and (Test-Path -LiteralPath $caseOutput)
        )
        if ($expectedCalls -eq 1) {
            Assert-That 'prior diagnostic failure is retained without rewriting its source before execution' (
                $result.defaultLogBefore.status -ceq 'ok' -and
                $result.defaultLogBefore.content -ceq 'prior report-creation error retained before invocation' -and
                $result.defaultLogBefore.capturedTextSha256BeforeRedaction -ceq $priorLogHash.ToLowerInvariant() -and
                (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $caseOutput) 'upm-diag-before.log') -Raw) -ceq $result.defaultLogBefore.content
            )
        }
        if ($networkFixtureMode -ceq 'ok') {
            Assert-That 'completed native invocation retains binary and report provenance' (
                $result.sourceSha256 -ceq (Get-FileHash -LiteralPath $collectorPath -Algorithm SHA256).Hash.ToLowerInvariant() -and
                $result.defaultLogCurrent -and $result.defaultLogAfter.content -ceq 'current native diagnostic log' -and
                $result.exitCode -eq 0 -and $result.executionCompleted -and $result.binaryFiles.Count -eq 2 -and
                $result.reports.Count -eq 3 -and @($result.reports | Where-Object { $_.status -cne 'ok' }).Count -eq 0
            )
        } elseif ($networkFixtureMode -ceq 'nonzero') {
            Assert-That 'nonzero native exit retains complete reports as failed execution' (
                $result.exitCode -eq 7 -and $result.executionCompleted -and $result.reports.Count -eq 3
            )
        }
        if (Test-Path -LiteralPath $nativeBinary) {
            Assert-That 'native diagnostic does not mutate the installed executable' ((Get-FileHash -LiteralPath $nativeBinary).Hash -ceq $nativeHash)
        }
    }
} finally {
    $env:LOCALAPPDATA = $networkLocalAppData
    if (Test-Path -LiteralPath $networkFixtureRoot) { Remove-Item -LiteralPath $networkFixtureRoot -Recurse -Force }
}

$diagnosticJob = $floorWorkflow.jobs.'package-log-capture'
$expectedSteps = @('Checkout', 'Require isolated package diagnostic mode', 'Setup Node.js for package log redaction',
    'Install artifact tooling dependencies', 'Capture retained Unity Package Manager diagnostics',
    'Verify exact Burst registry archive download', 'Run installed UPM network diagnostics', 'Redact retained package log artifacts', 'Upload retained package log artifacts')
Assert-That 'unlicensed package job contains only its declared diagnostic and artifact work' (
    ($diagnosticJob.steps.name -join '|') -ceq ($expectedSteps -join '|') -and
    $diagnosticJob.'timeout-minutes' -eq 10 -and $diagnosticJob.needs[0] -ceq 'runner-preflight'
)
$archiveStep = @($diagnosticJob.steps | Where-Object { $_.name -ceq 'Verify exact Burst registry archive download' })[0]
Assert-That 'workflow registers the distinct canonical CDN case with the unchanged digest' (
    $archiveStep.run.Contains('-RegistryArchiveRoute Cdn') -and
    $archiveStep.env.DXM_EXPECTED_BURST_SHA1 -ceq 'da63315718cf3bf3d11ff958633b4b67dc8d2426'
)
$diagnosticRoute = [scriptblock]::Create($diagnosticJob.steps[1].run)
$diagnosticEnvironment = @{}
foreach ($key in @('DXM_REQUESTED_RUNNER', 'RUNNER_NAME', 'DXM_LOG_SOURCE', 'DXM_REGISTRY_PROBE', 'DXM_NETWORK_PROBE', 'DXM_OTHER_PACKAGE_MODES')) {
    $diagnosticEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    foreach ($case in @(
        @{ source = ''; probe = 'true'; network = 'false'; other = 'false'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $true },
        @{ source = '37859571795,1'; probe = 'false'; network = 'false'; other = 'false'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $true },
        @{ source = '37859571795,1'; probe = 'true'; network = 'false'; other = 'false'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $false },
        @{ source = ''; probe = 'false'; network = 'false'; other = 'false'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $false },
        @{ source = ''; probe = 'true'; network = 'false'; other = 'true'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $false },
        @{ source = '../x,1'; probe = 'false'; network = 'false'; other = 'false'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $false },
        @{ source = ''; probe = 'true'; network = 'false'; other = 'false'; runner = 'DAD-MACHINE'; request = 'ELI-MACHINE'; accept = $false },
        @{ source = ''; probe = 'true'; network = 'false'; other = 'false'; runner = 'ELI-MACHINE'; request = 'DAD-MACHINE'; accept = $false },
        @{ source = ''; probe = 'false'; network = 'true'; other = 'false'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $true },
        @{ source = ''; probe = 'true'; network = 'true'; other = 'false'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $false },
        @{ source = '37859571795,1'; probe = 'false'; network = 'true'; other = 'false'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $false },
        @{ source = ''; probe = 'false'; network = 'true'; other = 'true'; runner = 'ELI-MACHINE'; request = 'ELI-MACHINE'; accept = $false },
        @{ source = ''; probe = 'false'; network = 'true'; other = 'false'; runner = 'DAD-MACHINE'; request = 'ELI-MACHINE'; accept = $false }
    )) {
        $env:DXM_REQUESTED_RUNNER = $case.request; $env:RUNNER_NAME = $case.runner
        $env:DXM_LOG_SOURCE = $case.source; $env:DXM_REGISTRY_PROBE = $case.probe; $env:DXM_NETWORK_PROBE = $case.network; $env:DXM_OTHER_PACKAGE_MODES = $case.other
        $accepted = $true
        try { & $diagnosticRoute } catch { $accepted = $false }
        Assert-That "diagnostic mode logs=$($case.source),probe=$($case.probe),network=$($case.network),other=$($case.other),runner=$($case.runner)" ($accepted -eq $case.accept)
    }
} finally {
    foreach ($key in $diagnosticEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $diagnosticEnvironment[$key]) }
}
Assert-That 'registry diagnostic cannot start a bootstrap, pilot or SDK floor' (
    $floorWorkflow.jobs.bootstrap.if.Contains("!inputs['native-sdk-registry-probe']") -and
    $floorWorkflow.jobs.'pilot-contract-smoke'.if.Contains("!inputs['native-sdk-registry-probe']") -and
    $floor.if.Contains("!inputs['native-sdk-registry-probe']")
)

$networkStep = @($diagnosticJob.steps | Where-Object { $_.name -ceq 'Run installed UPM network diagnostics' })[0]
Assert-That 'installed UPM network diagnostic keeps the fixed source, mode and work bound' (
    $networkStep.'timeout-minutes' -eq 5 -and $networkStep.shell -ceq 'pwsh' -and
    $networkStep.run.Contains('u6-v3/2021.3.45f1/Editor/Data/Resources/PackageManager/Diagnostics') -and
    $networkStep.run.Contains('-UpmNetworkOnly -InstalledDiagnosticsPath $diagnostics') -and
    $networkStep.run.Contains("-OutputPath '.artifacts/runner-bootstrap/upm-network-diagnostics.json'")
)
Assert-That 'network diagnostic excludes all bootstrap, pilot and SDK floor work' (
    $floorWorkflow.jobs.bootstrap.if.Contains("!inputs['native-sdk-upm-network-probe']") -and
    $floorWorkflow.jobs.'pilot-contract-smoke'.if.Contains("!inputs['native-sdk-upm-network-probe']") -and
    $floor.if.Contains("!inputs['native-sdk-upm-network-probe']")
)

Write-Host 'same-player repeat evidence tests passed'
