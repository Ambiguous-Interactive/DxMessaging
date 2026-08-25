Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
$scriptPath = Join-Path $repoRoot 'scripts' 'unity' 'require-comparison-rows.ps1'
$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "dxm-comparison-row-gate-$([guid]::NewGuid())"
$baselinePath = Join-Path $tempDirectory 'comparison-rows.csv'
$evidencePath = Join-Path $tempDirectory 'results.xml'
$secondEvidencePath = Join-Path $tempDirectory 'unity.log'
$playerLogPath = Join-Path $tempDirectory 'player.log'
$summaryJsonPath = Join-Path $tempDirectory 'paired-comparison-summary.json'
$summaryMarkdownPath = Join-Path $tempDirectory 'paired-comparison-summary.md'
$csvHeader = 'scenario,platform,commit,runIndex,emitsPerSec,gcAllocations,wallClockMs,gcAllocatedBytes'

function Write-Baseline {
    param(
        [string[]]$Scenarios,
        [string]$Commit = 'abc1234',
        [string]$Platform = 'Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1)'
    )

    $rows = @($csvHeader) + @($Scenarios | ForEach-Object { "$($_),$Platform,$Commit,0,1,-1,1,-1" })
    [System.IO.File]::WriteAllText($baselinePath, ($rows -join "`n") + "`n")
}

function New-PairedRecord {
    param(
        [string]$Scenario,
        [string]$First = 'DxMessaging',
        [string]$Second = 'MessagePipe',
        [double[]]$Ratios = @(0.5, 0.51, 0.49, 0.505)
    )

    $logSum = 0.0
    foreach ($ratio in $Ratios) {
        $logSum += [math]::Log($ratio)
    }
    $headline = [math]::Exp($logSum / $Ratios.Count)
    $firstSeconds = 1.5
    $cycleMeasurements = @($Ratios | ForEach-Object {
        [ordered]@{
            firstOperations = 40000
            firstActiveSeconds = $firstSeconds
            secondOperations = 40000
            secondActiveSeconds = $firstSeconds * $_
            firstToSecondRatio = $_
        }
    })
    $minimum = ($Ratios | Measure-Object -Minimum).Minimum
    $maximum = ($Ratios | Measure-Object -Maximum).Maximum
    [ordered]@{
        scenario = $Scenario
        first = $First
        second = $Second
        platform = 'Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1)'
        commit = 'abc1234'
        protocol = 'interleaved-abba-baab-v1'
        cycles = 4
        minimumCycleActiveMilliseconds = 625
        batchOperations = 10000
        firstToSecondRatio = $headline
        aggregateRateRatio = ($Ratios | Measure-Object -Average).Average
        cycleRatioSpreadPercent = ($maximum / $minimum - 1.0) * 100.0
        cycleRatios = $Ratios
        cycleMeasurements = $cycleMeasurements
    }
}

function Write-Evidence {
    param(
        [object[]]$Records,
        [string]$Path = $evidencePath
    )

    $lines = @($Records | ForEach-Object {
        $json = $_ | ConvertTo-Json -Compress
        "<output>DXM_PAIRED_COMPARISON $([System.Net.WebUtility]::HtmlEncode($json))</output>"
    })
    [System.IO.File]::WriteAllText($Path, ($lines -join "`n") + "`n")
    if ($Path -eq $evidencePath) {
        $playerLines = @('Comparison_DxMessaging_GlobalToOne,chronological-marker') + $lines
        [System.IO.File]::WriteAllText($playerLogPath, ($playerLines -join "`n") + "`n")
    }
}

function Invoke-Gate {
    param([string[]]$EvidenceInputs = @($evidencePath, $playerLogPath))

    & $scriptPath `
        -BaselinePath $baselinePath `
        -EvidencePaths $EvidenceInputs `
        -SummaryJsonPath $summaryJsonPath `
        -SummaryMarkdownPath $summaryMarkdownPath
}

function Assert-GateFails {
    param(
        [string[]]$Scenarios,
        [string]$MessagePattern,
        [object[]]$Records
    )

    Write-Baseline -Scenarios $Scenarios
    if ($null -ne $Records) {
        Write-Evidence -Records $Records
    }
    try {
        Invoke-Gate
        throw 'The comparison row gate accepted invalid rows.'
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw
        }
    }
}

try {
    [System.IO.Directory]::CreateDirectory($tempDirectory) | Out-Null
    $pairedFixturePath = Join-Path $repoRoot 'Tests/Runtime/Comparisons/Paired/PairedDxMessagingMessagePipeTests.cs'
    $pairedFixture = Get-Content -LiteralPath $pairedFixturePath -Raw
    $pairedAsmdefPath = Join-Path $repoRoot 'Tests/Runtime/Comparisons/Paired/WallstopStudios.DxMessaging.Tests.ZZ.Runtime.Comparisons.Paired.asmdef'
    $pairedAsmdef = Get-Content -LiteralPath $pairedAsmdefPath -Raw | ConvertFrom-Json
    if ($pairedAsmdef.name -ne 'WallstopStudios.DxMessaging.Tests.ZZ.Runtime.Comparisons.Paired') {
        throw 'The paired fixture assembly must sort after every canonical comparison assembly.'
    }

    $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts' 'unity' 'comparison-supported-scenarios.json') -Raw | ConvertFrom-Json
    $expected = @($manifest.PSObject.Properties | ForEach-Object {
        $technology = $_.Name
        @($_.Value | ForEach-Object { "Comparison_${technology}_$_" })
    })
    $pairedScenarios = @('GlobalToOne', 'GlobalToMany', 'KeyedToOne', 'Filtered', 'PostProcess', 'FilteredPostProcess', 'StructNoBox')
    $validRecords = @($pairedScenarios | ForEach-Object { New-PairedRecord -Scenario $_ })

    Write-Baseline -Scenarios $expected
    Write-Evidence -Records $validRecords
    Invoke-Gate
    $summary = Get-Content -LiteralPath $summaryJsonPath -Raw | ConvertFrom-Json
    $summaryMarkdown = Get-Content -LiteralPath $summaryMarkdownPath -Raw
    if (
        $summary.schemaVersion -ne 1 -or
        $summary.protocol -ne 'interleaved-abba-baab-v1' -or
        @($summary.rows).Count -ne 7 -or
        $summary.allRowsWithinMaterialityBand -ne $false -or
        @($summary.rows | Where-Object { $_.withinMaterialityBand -ne $false }).Count -ne 0 -or
        -not $summaryMarkdown.Contains('| no |')
    ) {
        throw 'The paired comparison gate did not write the expected summary artifacts.'
    }
    foreach ($scenario in $pairedScenarios) {
        if (-not $summaryMarkdown.Contains("``$scenario``")) {
            throw "The paired Markdown summary omitted '$scenario'."
        }
    }

    $stableRecords = @($pairedScenarios | ForEach-Object {
        New-PairedRecord -Scenario $_ -Ratios @(0.5, 0.501, 0.499, 0.5)
    })
    Write-Evidence -Records $stableRecords
    Invoke-Gate
    $stableSummary = Get-Content -LiteralPath $summaryJsonPath -Raw | ConvertFrom-Json
    $stableMarkdown = Get-Content -LiteralPath $summaryMarkdownPath -Raw
    if (
        $stableSummary.allRowsWithinMaterialityBand -ne $true -or
        @($stableSummary.rows | Where-Object { $_.withinMaterialityBand -ne $true }).Count -ne 0 -or
        -not $stableMarkdown.Contains('| yes |')
    ) {
        throw 'Stable paired records did not render a passing user-facing verdict.'
    }

    $pairedLine = Get-Content -LiteralPath $evidencePath -TotalCount 1
    [System.IO.File]::WriteAllText(
        $playerLogPath,
        "$pairedLine`nComparison_DxMessaging_GlobalToOne,late-canonical-marker`n"
    )
    try {
        Invoke-Gate
        throw 'The comparison row gate accepted paired load before canonical rows completed.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'paired fixture ran before canonical') {
            throw
        }
    }

    Assert-GateFails -Scenarios @() -MessagePattern 'no published comparison rows' -Records $validRecords
    Assert-GateFails -Scenarios @($expected | Select-Object -SkipLast 1) -MessagePattern 'do not match' -Records $validRecords
    Assert-GateFails -Scenarios @($expected + 'Comparison_Unknown_GlobalToOne') -MessagePattern 'do not match' -Records $validRecords
    Assert-GateFails -Scenarios @($expected + $expected[0]) -MessagePattern 'do not match' -Records $validRecords
    $wrongCasePublished = @($expected)
    $wrongCasePublished[0] = $wrongCasePublished[0].ToLowerInvariant()
    Assert-GateFails -Scenarios $wrongCasePublished -MessagePattern 'do not match|no published comparison rows' -Records $validRecords

    Write-Baseline -Scenarios $expected
    Write-Evidence -Records @()
    Assert-GateFails -Scenarios $expected -MessagePattern 'chronological player log' -Records $null
    Assert-GateFails -Scenarios $expected -MessagePattern 'pinned scenario set' -Records @($validRecords | Select-Object -SkipLast 1)

    $wrongOrder = @($validRecords)
    $wrongOrder[0] = New-PairedRecord -Scenario 'GlobalToOne' -First 'MessagePipe' -Second 'DxMessaging'
    Assert-GateFails -Scenarios $expected -MessagePattern 'DxMessaging first and MessagePipe second' -Records $wrongOrder

    $wrongCaseScenario = @($validRecords)
    $wrongCaseScenario[0] = New-PairedRecord -Scenario 'globalToOne'
    Assert-GateFails -Scenarios $expected -MessagePattern 'pinned scenario set' -Records $wrongCaseScenario

    $wrongCaseTechnology = @($validRecords)
    $wrongCaseTechnology[0] = New-PairedRecord -Scenario 'GlobalToOne' -First 'dxMessaging'
    Assert-GateFails -Scenarios $expected -MessagePattern 'DxMessaging first and MessagePipe second' -Records $wrongCaseTechnology

    $wrongProvenance = @($validRecords)
    $wrongProvenance[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongProvenance[0].commit = 'wrong-commit'
    Assert-GateFails -Scenarios $expected -MessagePattern 'platform and commit must match' -Records $wrongProvenance

    $wrongCaseCommit = @($validRecords)
    $wrongCaseCommit[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongCaseCommit[0].commit = 'ABC1234'
    Assert-GateFails -Scenarios $expected -MessagePattern 'platform and commit must match' -Records $wrongCaseCommit

    $wrongCasePlatform = @($validRecords)
    $wrongCasePlatform[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongCasePlatform[0].platform = $wrongCasePlatform[0].platform.Replace('Standalone', 'standalone')
    Assert-GateFails -Scenarios $expected -MessagePattern 'platform and commit must match' -Records $wrongCasePlatform

    $wrongProtocol = @($validRecords)
    $wrongProtocol[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongProtocol[0].cycles = 5
    Assert-GateFails -Scenarios $expected -MessagePattern 'pinned interleaved ABBA/BAAB protocol' -Records $wrongProtocol

    $wrongProtocolId = @($validRecords)
    $wrongProtocolId[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongProtocolId[0].protocol = 'unknown-protocol'
    Assert-GateFails -Scenarios $expected -MessagePattern 'pinned interleaved ABBA/BAAB protocol' -Records $wrongProtocolId

    $wrongCaseProtocol = @($validRecords)
    $wrongCaseProtocol[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongCaseProtocol[0].protocol = 'INTERLEAVED-ABBA-BAAB-V1'
    Assert-GateFails -Scenarios $expected -MessagePattern 'pinned interleaved ABBA/BAAB protocol' -Records $wrongCaseProtocol

    $wrongMinimum = @($validRecords)
    $wrongMinimum[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongMinimum[0].minimumCycleActiveMilliseconds = 500
    Assert-GateFails -Scenarios $expected -MessagePattern 'pinned interleaved ABBA/BAAB protocol' -Records $wrongMinimum

    $wrongBatchSize = @($validRecords)
    $wrongBatchSize[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongBatchSize[0].batchOperations = 1000
    Assert-GateFails -Scenarios $expected -MessagePattern 'pinned interleaved ABBA/BAAB protocol' -Records $wrongBatchSize

    $fractionalCycles = @($validRecords)
    $fractionalCycles[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $fractionalCycles[0].cycles = 4.1
    Assert-GateFails -Scenarios $expected -MessagePattern 'exact integer' -Records $fractionalCycles

    $fractionalMinimum = @($validRecords)
    $fractionalMinimum[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $fractionalMinimum[0].minimumCycleActiveMilliseconds = 625.1
    Assert-GateFails -Scenarios $expected -MessagePattern 'exact integer' -Records $fractionalMinimum

    $fractionalBatchSize = @($validRecords)
    $fractionalBatchSize[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $fractionalBatchSize[0].batchOperations = 10000.1
    Assert-GateFails -Scenarios $expected -MessagePattern 'exact integer' -Records $fractionalBatchSize

    $quotedCycles = @($validRecords)
    $quotedCycles[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $quotedCycles[0].cycles = '4'
    Assert-GateFails -Scenarios $expected -MessagePattern 'must be numeric' -Records $quotedCycles

    $wrongHeadline = @($validRecords)
    $wrongHeadline[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongHeadline[0].firstToSecondRatio = 0.75
    Assert-GateFails -Scenarios $expected -MessagePattern 'not the geometric mean' -Records $wrongHeadline

    $wrongSpread = @($validRecords)
    $wrongSpread[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $wrongSpread[0].cycleRatioSpreadPercent = 0.0
    Assert-GateFails -Scenarios $expected -MessagePattern 'cycle spread does not match' -Records $wrongSpread

    $invalidRatio = @($validRecords)
    $invalidRatio[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $invalidRatio[0].cycleRatios = @(0.5, 0.0, 0.49, 0.505)
    Assert-GateFails -Scenarios $expected -MessagePattern 'non-positive or non-finite cycle ratio' -Records $invalidRatio

    $invalidAggregate = @($validRecords)
    $invalidAggregate[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $invalidAggregate[0].aggregateRateRatio = -1.0
    Assert-GateFails -Scenarios $expected -MessagePattern 'non-positive or non-finite rate ratio' -Records $invalidAggregate

    $mismatchedAggregate = @($validRecords)
    $mismatchedAggregate[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $mismatchedAggregate[0].aggregateRateRatio = 0.75
    Assert-GateFails -Scenarios $expected -MessagePattern 'aggregate ratio does not match' -Records $mismatchedAggregate

    $shortCycle = @($validRecords)
    $shortCycle[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $shortCycle[0].cycleMeasurements[0].firstActiveSeconds = 0.5
    Assert-GateFails -Scenarios $expected -MessagePattern 'did not reach the minimum active time' -Records $shortCycle

    $invalidRetainedRatio = @($validRecords)
    $invalidRetainedRatio[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $invalidRetainedRatio[0].cycleMeasurements[0].firstToSecondRatio = -1.0
    Assert-GateFails -Scenarios $expected -MessagePattern 'non-positive or non-finite retained ratio' -Records $invalidRetainedRatio

    $unbalancedOperations = @($validRecords)
    $unbalancedOperations[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $unbalancedOperations[0].cycleMeasurements[0].secondOperations = 80000
    Assert-GateFails -Scenarios $expected -MessagePattern 'balanced ABBA/BAAB operation counts' -Records $unbalancedOperations

    $fractionalOperations = @($validRecords)
    $fractionalOperations[0] = New-PairedRecord -Scenario 'GlobalToOne'
    $fractionalOperations[0].cycleMeasurements[0].firstOperations = 40000.5
    Assert-GateFails -Scenarios $expected -MessagePattern 'exact integer' -Records $fractionalOperations

    Write-Baseline -Scenarios $expected -Commit ''
    Write-Evidence -Records $validRecords
    try {
        Invoke-Gate
        throw 'The comparison row gate accepted a blank published commit.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'non-empty commit') {
            throw
        }
    }

    Write-Baseline -Scenarios $expected -Platform 'Editor PlayMode Mono'
    Write-Evidence -Records $validRecords
    try {
        Invoke-Gate
        throw 'The comparison row gate accepted a non-Standalone platform.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'invalid platform') {
            throw
        }
    }

    Write-Baseline -Scenarios $expected -Platform 'standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1)'
    $wrongCasePlatformRecords = @($pairedScenarios | ForEach-Object {
        $record = New-PairedRecord -Scenario $_
        $record.platform = 'standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1)'
        $record
    })
    Write-Evidence -Records $wrongCasePlatformRecords
    try {
        Invoke-Gate
        throw 'The comparison row gate accepted a wrongly cased Standalone platform.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'invalid platform') {
            throw
        }
    }

    Write-Baseline -Scenarios $expected
    Write-Evidence -Records $validRecords
    $conflict = New-PairedRecord -Scenario 'GlobalToOne'
    $conflict.firstToSecondRatio = 0.75
    Write-Evidence -Records @($conflict) -Path $secondEvidencePath
    try {
        Invoke-Gate -EvidenceInputs @($evidencePath, $playerLogPath, $secondEvidencePath)
        throw 'The comparison row gate accepted conflicting evidence inputs.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'conflicting evidence records') {
            throw
        }
    }
}
finally {
    if (Test-Path -LiteralPath $tempDirectory) {
        Remove-Item -LiteralPath $tempDirectory -Recurse -Force
    }
}
