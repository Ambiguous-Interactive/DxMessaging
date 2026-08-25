#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates repeated launches of one Standalone comparison player.

.DESCRIPTION
    Verifies the complete player directory manifest, per-run results,
    processor-affinity evidence, and host-condition probes. Writes JSON and
    Markdown reports for the nine DxMessaging comparison rows. A max/min spread
    above the materiality band is reported as a warning and retained as evidence;
    malformed evidence fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$ArtifactsPath,

    [ValidateRange(2, 10)]
    [int]$ExpectedRunCount = 3,

    [ValidateRange(0.01, 100.0)]
    [double]$MaterialityBandPercent = 3.0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedArtifactsPath = (Resolve-Path -LiteralPath $ArtifactsPath).Path
$repeatRoot = Join-Path $resolvedArtifactsPath 'same-player-repeats'
$evidencePath = Join-Path $repeatRoot 'same-player-evidence.json'
if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    throw "Same-player evidence was not written to $evidencePath."
}

$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
if ($evidence.schemaVersion -ne 1) {
    throw "Expected same-player evidence schemaVersion 1; found '$($evidence.schemaVersion)'."
}
$runs = @($evidence.runs)
if ($evidence.runCount -ne $ExpectedRunCount -or $runs.Count -ne $ExpectedRunCount) {
    throw "Expected $ExpectedRunCount same-player launches; evidence recorded runCount=$($evidence.runCount), records=$($runs.Count)."
}
$expectedRunIndexes = 1..$ExpectedRunCount
if ((@($runs | ForEach-Object { $_.runIndex }) -join ',') -ne ($expectedRunIndexes -join ',')) {
    throw "Same-player evidence did not record the exact run sequence $($expectedRunIndexes -join ',')."
}
if (
    $evidence.canonicalPublishedRunIndex -ne 1 -or
    $evidence.playerDirectoryManifestMatches -ne $true
) {
    throw 'Same-player evidence did not preserve canonical run 1 with an unchanged player directory manifest.'
}
$manifestBefore = $evidence.playerDirectoryManifestBefore
$manifestAfter = $evidence.playerDirectoryManifestAfter
if (
    $manifestBefore.schemaVersion -ne 1 -or
    $manifestAfter.schemaVersion -ne 1
) {
    throw 'Player directory manifests must use schemaVersion 1.'
}
$beforeFiles = @($manifestBefore.files)
$afterFiles = @($manifestAfter.files)
if (
    $manifestBefore.fileCount -ne $beforeFiles.Count -or
    $manifestAfter.fileCount -ne $afterFiles.Count -or
    $beforeFiles.Count -eq 0 -or
    $beforeFiles.Count -ne $afterFiles.Count
) {
    throw 'Player directory manifests have missing or inconsistent file counts.'
}
for ($manifestIndex = 0; $manifestIndex -lt $beforeFiles.Count; $manifestIndex++) {
    $beforeFile = $beforeFiles[$manifestIndex]
    $afterFile = $afterFiles[$manifestIndex]
    $relativePath = [string]$beforeFile.path
    if (
        [string]::IsNullOrWhiteSpace($relativePath) -or
        $relativePath.Contains('\') -or
        [System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath -match '(^|/)\.\.(/|$)' -or
        [string]$afterFile.path -cne $relativePath -or
        [long]$beforeFile.length -lt 0 -or
        [long]$afterFile.length -ne [long]$beforeFile.length -or
        [string]$beforeFile.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        [string]$afterFile.sha256 -cne [string]$beforeFile.sha256
    ) {
        throw "Player directory manifest entry $manifestIndex is missing, invalid, or changed."
    }
    if (
        $manifestIndex -gt 0 -and
        [System.StringComparer]::Ordinal.Compare(
            [string]$beforeFiles[$manifestIndex - 1].path,
            $relativePath
        ) -ge 0
    ) {
        throw 'Player directory manifest paths are not unique and ordinal-sorted.'
    }
}

foreach ($runRecord in $runs) {
    $runIndex = [int]$runRecord.runIndex
    $runNumber = '{0:D2}' -f $runIndex
    $expectedResultsPath = if ($runIndex -eq 1) {
        'results.xml'
    } else {
        "same-player-repeats/run-$runNumber/repeat-$runNumber-results.xml"
    }
    $expectedPlayerLogPath = if ($runIndex -eq 1) {
        'player.log'
    } else {
        "same-player-repeats/run-$runNumber/repeat-$runNumber-player.log"
    }
    $expectedHostConditionsFile = "run-$runNumber-host-conditions.json"
    if (
        [string]$runRecord.resultsPath -cne $expectedResultsPath -or
        [string]$runRecord.playerLogPath -cne $expectedPlayerLogPath -or
        [string]$runRecord.hostConditionsFile -cne $expectedHostConditionsFile
    ) {
        throw "Player run $runIndex did not use its exact managed evidence paths."
    }
    if ([int]$runRecord.processId -le 0) {
        throw "Player run $runIndex did not record its process id."
    }
    if ([string]::IsNullOrWhiteSpace([string]$runRecord.processorAffinityMask)) {
        throw "Player run $runIndex did not record its actual processor affinity."
    }
    if ($runRecord.timedOut -ne $false) {
        throw "Player run $runIndex timed out and cannot support stability evidence."
    }
    $resultPath = Join-Path $resolvedArtifactsPath ([string]$runRecord.resultsPath)
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Player run $runIndex results were not written to $resultPath."
    }
    $playerLogPath = Join-Path $resolvedArtifactsPath ([string]$runRecord.playerLogPath)
    if (-not (Test-Path -LiteralPath $playerLogPath -PathType Leaf)) {
        throw "Player run $runIndex log was not written to $playerLogPath."
    }
    $hostPath = Join-Path $repeatRoot ([string]$runRecord.hostConditionsFile)
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
        throw "Player run $runIndex host conditions were not written to $hostPath."
    }
    $hostConditions = Get-Content -LiteralPath $hostPath -Raw | ConvertFrom-Json
    if (
        $hostConditions.schemaVersion -ne 1 -or
        $hostConditions.runIndex -ne $runIndex -or
        $hostConditions.runCount -ne $ExpectedRunCount -or
        $hostConditions.playerProcessId -ne $runRecord.processId -or
        $hostConditions.playerProcessorAffinityMask -ne $runRecord.processorAffinityMask -or
        $hostConditions.timedOut -ne $false
    ) {
        throw "Player run $runIndex host evidence does not match its run record or records a timeout."
    }
    $snapshotTimestamps = @{}
    foreach ($expectedPhase in @('before', 'after')) {
        $snapshot = $hostConditions.$expectedPhase
        $timestamp = [DateTimeOffset]::MinValue
        if (
            $null -eq $snapshot -or
            $snapshot.phase -ne $expectedPhase -or
            -not [DateTimeOffset]::TryParse([string]$snapshot.timestampUtc, [ref]$timestamp)
        ) {
            throw "Player run $runIndex has invalid $expectedPhase snapshot timing evidence."
        }
        $snapshotTimestamps[$expectedPhase] = $timestamp
        $logicalProcessors = @($snapshot.logicalProcessors)
        $processors = @($snapshot.processors)
        $logicalFrequencyCount = @(
            $logicalProcessors | Where-Object { $_.frequencyMhz -gt 0 }
        ).Count
        $packageFrequencyCount = @(
            $processors | Where-Object { $_.currentClockMhz -gt 0 }
        ).Count
        if ($logicalFrequencyCount -eq 0 -and $packageFrequencyCount -eq 0) {
            throw "Player run $runIndex did not record active CPU frequency around the run."
        }
        $logicalLoadCount = @(
            $logicalProcessors | Where-Object { $null -ne $_.loadPercent }
        ).Count
        $packageLoadCount = @(
            $processors | Where-Object { $null -ne $_.loadPercent }
        ).Count
        if (
            $null -eq $snapshot.totalCpuLoadPercent -and
            $logicalLoadCount -eq 0 -and
            $packageLoadCount -eq 0
        ) {
            throw "Player run $runIndex did not record host CPU load around the run."
        }
        if ($null -eq $snapshot.acpiThermalZones -or $null -eq $snapshot.acpiThermalZones.available) {
            throw "Player run $runIndex did not record the thermal probe result."
        }
    }
    if ($snapshotTimestamps.before -gt $snapshotTimestamps.after) {
        throw "Player run $runIndex recorded its before snapshot after its after snapshot."
    }
}

$canonicalCollisions = @(
    Get-ChildItem -LiteralPath $repeatRoot -File -Recurse |
        Where-Object { $_.Name -in @('results.xml', 'player.log') }
)
if ($canonicalCollisions.Count -gt 0) {
    throw 'Diagnostic repeats used canonical publication filenames.'
}

$scenarioModulePath = Join-Path $PSScriptRoot 'perf-scenarios.js'
$expectedDxScenarios = @(
    & node -e "require(process.argv[1]).COMPARISON_SCENARIO_ORDER.forEach((scenario) => console.log('Comparison_DxMessaging_' + scenario))" $scenarioModulePath
)
if ($LASTEXITCODE -ne 0 -or $expectedDxScenarios.Count -eq 0) {
    throw 'Could not resolve the expected DxMessaging comparison scenarios.'
}

$extractorPath = Join-Path $PSScriptRoot 'extract-perf-baseline.js'
$measurements = @{}
$measuredPlatform = $null
$measuredCommit = $null
foreach ($runRecord in $runs) {
    $resultPath = Join-Path $resolvedArtifactsPath ([string]$runRecord.resultsPath)
    $csvPath = Join-Path $repeatRoot ('run-{0:D2}.csv' -f [int]$runRecord.runIndex)
    & node $extractorPath `
        --input $resultPath `
        --scope Standalone `
        --output $csvPath `
        --replace
    if ($LASTEXITCODE -ne 0) {
        throw "Could not extract player run $($runRecord.runIndex) into $csvPath."
    }
    $rows = @(Import-Csv -LiteralPath $csvPath)
    foreach ($scenario in $expectedDxScenarios) {
        $matchingRows = @($rows | Where-Object { $_.scenario -eq $scenario })
        if ($matchingRows.Count -ne 1) {
            throw "Player run $($runRecord.runIndex) recorded $($matchingRows.Count) rows for $scenario."
        }
        $rowPlatform = [string]$matchingRows[0].platform
        $rowCommit = [string]$matchingRows[0].commit
        if ($rowPlatform -notmatch '^Standalone IL2CPP x64 Release \(WindowsPlayer; Unity [^)]+\)$') {
            throw "Player run $($runRecord.runIndex) recorded a non-published platform for ${scenario}: '$rowPlatform'."
        }
        if ([string]::IsNullOrWhiteSpace($rowCommit)) {
            throw "Player run $($runRecord.runIndex) did not record the measured commit for $scenario."
        }
        if ($null -eq $measuredPlatform) {
            $measuredPlatform = $rowPlatform
            $measuredCommit = $rowCommit
        } elseif ($rowPlatform -cne $measuredPlatform -or $rowCommit -cne $measuredCommit) {
            throw "Player run $($runRecord.runIndex) did not preserve one platform and measured commit across repeat rows."
        }
        if (-not $measurements.ContainsKey($scenario)) {
            $measurements[$scenario] = New-Object System.Collections.Generic.List[double]
        }
        $value = [double]::Parse(
            $matchingRows[0].emitsPerSecond,
            [Globalization.CultureInfo]::InvariantCulture
        )
        $measurements[$scenario].Add($value)
    }
}

$reportRows = New-Object System.Collections.Generic.List[object]
$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add('# Same-player comparison stability')
$markdown.Add('')
$markdown.Add(
    "Materiality band: maximum/minimum spread <= $($MaterialityBandPercent.ToString('F2', [Globalization.CultureInfo]::InvariantCulture))%. No median or outlier removal."
)
$markdown.Add('')
$runHeaders = @($expectedRunIndexes | ForEach-Object { "Run $_" })
$markdown.Add("| Scenario | $($runHeaders -join ' | ') | Max/min spread | Gate |")
$markdown.Add("| --- | $(@($expectedRunIndexes | ForEach-Object { '---:' }) -join ' | ') | ---: | --- |")
$allStable = $true
foreach ($scenario in $expectedDxScenarios) {
    $samples = @($measurements[$scenario].ToArray())
    if (
        $samples.Count -ne $ExpectedRunCount -or
        @($samples | Where-Object { $_ -le 0 }).Count -gt 0
    ) {
        throw "$scenario did not produce $ExpectedRunCount positive throughput samples."
    }
    $minimum = [double]($samples | Measure-Object -Minimum).Minimum
    $maximum = [double]($samples | Measure-Object -Maximum).Maximum
    $spreadPercent = (($maximum / $minimum) - 1.0) * 100.0
    $stable = $spreadPercent -le $MaterialityBandPercent
    $allStable = $allStable -and $stable
    $displayName = $scenario.Substring('Comparison_DxMessaging_'.Length)
    $gate = if ($stable) { 'Pass' } else { 'Fail' }
    $reportRows.Add([ordered]@{
            scenario = $displayName
            emitsPerSecond = $samples
            minimum = $minimum
            maximum = $maximum
            maxMinSpreadPercent = $spreadPercent
            withinMaterialityBand = $stable
        })
    $sampleCells = @($samples | ForEach-Object { $_.ToString('N0') })
    $markdown.Add(
        "| $displayName | $($sampleCells -join ' | ') | $($spreadPercent.ToString('N2'))% | $gate |"
    )
}

$report = [ordered]@{
    schemaVersion = 1
    materialityBandPercent = $MaterialityBandPercent
    calculation = '(maximum / minimum - 1) * 100'
    platform = $measuredPlatform
    commit = $measuredCommit
    allRowsStable = $allStable
    rows = @($reportRows.ToArray())
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText(
    (Join-Path $repeatRoot 'same-player-stability.json'),
    ($report | ConvertTo-Json -Depth 8) + "`n",
    $utf8NoBom
)
[IO.File]::WriteAllText(
    (Join-Path $repeatRoot 'same-player-stability.md'),
    ($markdown -join "`n") + "`n",
    $utf8NoBom
)
if ($allStable) {
    Write-Host "All same-player DxMessaging comparison rows stayed inside the $MaterialityBandPercent% band."
} else {
    Write-Host "::warning::At least one same-player DxMessaging comparison row exceeded the $MaterialityBandPercent% band; retain the report as an instability verdict."
}
Write-Host "Verified $ExpectedRunCount launches of one comparison player with host-condition evidence."
