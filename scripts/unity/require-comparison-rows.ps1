[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselinePath,

    [Parameter(Mandatory = $true)]
    [string[]]$EvidencePaths,

    [Parameter(Mandatory = $true)]
    [string]$SummaryJsonPath,

    [Parameter(Mandatory = $true)]
    [string]$SummaryMarkdownPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    throw "Comparison baseline does not exist: $BaselinePath"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
# SYNC: BenchmarkProtocol.PairedProtocolId, PairedMeasurementCycles,
# PairedMinimumCycleActiveMilliseconds, BatchSize, and PairedMaterialityBandPercent define these values.
$pairedProtocol = 'interleaved-abba-baab-v1'
$pairedCycles = 4
$pairedMinimumCycleActiveMilliseconds = 625
$pairedBatchOperations = 10000
$pairedMaterialityBandPercent = 3.0

function Get-NumericDouble {
    param(
        [object]$Value,
        [string]$Field
    )

    if ($null -eq $Value) {
        throw "Paired comparison field '$Field' must be numeric."
    }
    $typeCode = [System.Type]::GetTypeCode($Value.GetType())
    $numericTypeCodes = @(
        [System.TypeCode]::Byte,
        [System.TypeCode]::SByte,
        [System.TypeCode]::Int16,
        [System.TypeCode]::UInt16,
        [System.TypeCode]::Int32,
        [System.TypeCode]::UInt32,
        [System.TypeCode]::Int64,
        [System.TypeCode]::UInt64,
        [System.TypeCode]::Single,
        [System.TypeCode]::Double,
        [System.TypeCode]::Decimal
    )
    if ($typeCode -notin $numericTypeCodes) {
        throw "Paired comparison field '$Field' must be numeric."
    }

    $numeric = [double]$Value
    if ([double]::IsNaN($numeric) -or [double]::IsInfinity($numeric)) {
        throw "Paired comparison field '$Field' must be finite."
    }
    return $numeric
}

function Get-ExactInt64 {
    param(
        [object]$Value,
        [string]$Field
    )

    $numeric = Get-NumericDouble -Value $Value -Field $Field
    if (
        [math]::Truncate($numeric) -ne $numeric -or
        $numeric -lt [long]::MinValue -or
        $numeric -gt [long]::MaxValue
    ) {
        throw "Paired comparison field '$Field' must be an exact integer."
    }
    return [long]$numeric
}

Push-Location $repoRoot
try {
    $expected = @(& node -e "require('./scripts/unity/perf-scenarios.js').COMPARISON_SUPPORTED_SCENARIO_IDS.forEach((id) => console.log(id))")
    if ($LASTEXITCODE -ne 0) {
        throw 'Comparison capability manifest evaluation failed.'
    }

    $baselineRows = @(Import-Csv -LiteralPath $BaselinePath)
    $publishedRows = @($baselineRows | Where-Object { $_.scenario -clike 'Comparison_*' })
    $actual = @($publishedRows | ForEach-Object { $_.scenario })
    $expectedSorted = @($expected | Sort-Object)
    $actualSorted = @($actual | Sort-Object)

    if ($expectedSorted.Count -ne 48) {
        throw "The pinned comparison capability manifest must contain exactly 48 rows; found $($expectedSorted.Count)."
    }
    if ($actualSorted.Count -eq 0) {
        throw 'The comparison baseline contains no published comparison rows.'
    }

    $difference = @(Compare-Object -CaseSensitive -ReferenceObject $expectedSorted -DifferenceObject $actualSorted)
    if ($expectedSorted.Count -ne $actualSorted.Count -or $difference.Count -ne 0) {
        $difference | Format-Table | Out-String | Write-Host
        throw 'Published comparison rows do not match the pinned capability manifest.'
    }

    $publishedPlatforms = @($publishedRows | ForEach-Object { $_.platform } | Sort-Object -Unique -CaseSensitive)
    $publishedCommits = @($publishedRows | ForEach-Object { $_.commit } | Sort-Object -Unique -CaseSensitive)
    if ($publishedPlatforms.Count -ne 1 -or $publishedCommits.Count -ne 1) {
        throw 'Published comparison rows must contain exactly one platform and commit.'
    }
    $publishedPlatform = $publishedPlatforms[0]
    $publishedCommit = $publishedCommits[0]
    if ([string]::IsNullOrWhiteSpace($publishedCommit)) {
        throw 'Published comparison rows must carry a non-empty commit.'
    }
    if ($publishedPlatform -cnotmatch '^Standalone IL2CPP x64 Release \(WindowsPlayer; Unity [^)]+\)$') {
        throw "Published comparison rows use an invalid platform: '$publishedPlatform'."
    }

    # SYNC: PairedDxMessagingMessagePipeTests.cs uses this MessagePipe capability set and
    # excludes SubUnsub because allocation/GC work can contaminate the adjacent bridge batch.
    $expectedPaired = @(& node -e "require('./scripts/unity/perf-scenarios.js').COMPARISON_SUPPORTED_SCENARIOS.MessagePipe.filter((id) => id !== 'SubUnsub').forEach((id) => console.log(id))")
    if ($LASTEXITCODE -ne 0) {
        throw 'Paired comparison capability manifest evaluation failed.'
    }
    if ($expectedPaired.Count -ne 7) {
        throw "The paired comparison protocol must contain exactly 7 scenarios; found $($expectedPaired.Count)."
    }

    $evidenceFiles = @($EvidencePaths | ForEach-Object {
        if (-not (Test-Path -LiteralPath $_ -PathType Leaf)) {
            throw "Paired comparison evidence input does not exist: $_"
        }
        (Resolve-Path -LiteralPath $_).Path
    })
    if ($evidenceFiles.Count -eq 0) {
        throw 'No paired comparison evidence inputs were provided.'
    }
    $playerLogs = @($evidenceFiles | Where-Object {
        [System.IO.Path]::GetFileName($_) -eq 'player.log'
    })
    if ($playerLogs.Count -ne 1) {
        throw "Paired comparison evidence requires exactly one chronological player.log; found $($playerLogs.Count)."
    }
    $playerLogText = [System.Net.WebUtility]::HtmlDecode(
        (Get-Content -LiteralPath $playerLogs[0] -Raw)
    )
    $canonicalMarkers = @([regex]::Matches($playerLogText, '(?<!Paired)Comparison_[A-Za-z0-9]+_[A-Za-z0-9]+'))
    $pairedMarkers = @([regex]::Matches($playerLogText, 'DXM_PAIRED_COMPARISON\s+\{'))
    if ($canonicalMarkers.Count -eq 0 -or $pairedMarkers.Count -eq 0) {
        throw 'The chronological player log must contain canonical and paired comparison markers.'
    }
    if ($canonicalMarkers[-1].Index -gt $pairedMarkers[0].Index) {
        throw 'The high-load paired fixture ran before canonical comparison evidence completed.'
    }

    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($evidenceFile in $evidenceFiles) {
        $raw = Get-Content -LiteralPath $evidenceFile -Raw
        $decoded = [System.Net.WebUtility]::HtmlDecode($raw)
        $matches = @([regex]::Matches($decoded, 'DXM_PAIRED_COMPARISON\s+(\{[^\r\n<]+\})'))
        foreach ($match in $matches) {
            try {
                $records.Add(($match.Groups[1].Value | ConvertFrom-Json))
            }
            catch {
                throw "Malformed paired comparison evidence in '$evidenceFile': $($match.Groups[1].Value)"
            }
        }
    }
    if ($records.Count -eq 0) {
        throw 'The comparison results contain no paired comparison evidence.'
    }

    $actualPaired = @($records | ForEach-Object { [string]$_.scenario } | Sort-Object -Unique -CaseSensitive)
    $pairedDifference = @(Compare-Object -CaseSensitive -ReferenceObject @($expectedPaired | Sort-Object) -DifferenceObject $actualPaired)
    if ($expectedPaired.Count -ne $actualPaired.Count -or $pairedDifference.Count -ne 0) {
        $pairedDifference | Format-Table | Out-String | Write-Host
        throw 'Paired comparison evidence does not match the pinned scenario set.'
    }

    $summaryRows = [System.Collections.Generic.List[object]]::new()
    foreach ($scenario in $expectedPaired) {
        $scenarioRecords = @($records | Where-Object { $_.scenario -ceq $scenario })
        $payloads = @($scenarioRecords | ForEach-Object { $_ | ConvertTo-Json -Compress } | Sort-Object -Unique -CaseSensitive)
        if ($payloads.Count -ne 1) {
            throw "Paired comparison '$scenario' emitted conflicting evidence records."
        }

        $record = $scenarioRecords[0]
        if ($record.first -cne 'DxMessaging' -or $record.second -cne 'MessagePipe') {
            throw "Paired comparison '$scenario' must measure DxMessaging first and MessagePipe second."
        }
        if ($record.platform -cne $publishedPlatform -or $record.commit -cne $publishedCommit) {
            throw "Paired comparison '$scenario' platform and commit must match the published comparison rows."
        }

        $recordCycles = Get-ExactInt64 -Value $record.cycles -Field "$scenario.cycles"
        $recordMinimumMilliseconds = Get-ExactInt64 -Value $record.minimumCycleActiveMilliseconds -Field "$scenario.minimumCycleActiveMilliseconds"
        $recordBatchOperations = Get-ExactInt64 -Value $record.batchOperations -Field "$scenario.batchOperations"
        if (
            $record.protocol -cne $pairedProtocol -or
            $recordCycles -ne $pairedCycles -or
            $recordMinimumMilliseconds -ne $pairedMinimumCycleActiveMilliseconds -or
            $recordBatchOperations -ne $pairedBatchOperations
        ) {
            throw "Paired comparison '$scenario' does not use the pinned interleaved ABBA/BAAB protocol."
        }

        $ratios = @($record.cycleRatios)
        if ($ratios.Count -ne $pairedCycles) {
            throw "Paired comparison '$scenario' must contain exactly $pairedCycles raw cycle ratios; found $($ratios.Count)."
        }
        $numericRatios = @($ratios | ForEach-Object { Get-NumericDouble -Value $_ -Field "$scenario.cycleRatios" })
        $invalidRatios = @($numericRatios | Where-Object {
            $_ -le 0 -or [double]::IsNaN($_) -or [double]::IsInfinity($_)
        })
        if ($invalidRatios.Count -ne 0) {
            throw "Paired comparison '$scenario' contains a non-positive or non-finite cycle ratio."
        }

        $cycleMeasurements = @($record.cycleMeasurements)
        if ($cycleMeasurements.Count -ne $pairedCycles) {
            throw "Paired comparison '$scenario' must retain exactly $pairedCycles cycle measurements."
        }
        $firstTotalOperations = 0L
        $firstTotalSeconds = 0.0
        $secondTotalOperations = 0L
        $secondTotalSeconds = 0.0
        for ($cycleIndex = 0; $cycleIndex -lt $pairedCycles; $cycleIndex++) {
            $cycleMeasurement = $cycleMeasurements[$cycleIndex]
            $firstOperations = Get-ExactInt64 -Value $cycleMeasurement.firstOperations -Field "$scenario.cycleMeasurements[$cycleIndex].firstOperations"
            $firstSeconds = Get-NumericDouble -Value $cycleMeasurement.firstActiveSeconds -Field "$scenario.cycleMeasurements[$cycleIndex].firstActiveSeconds"
            $secondOperations = Get-ExactInt64 -Value $cycleMeasurement.secondOperations -Field "$scenario.cycleMeasurements[$cycleIndex].secondOperations"
            $secondSeconds = Get-NumericDouble -Value $cycleMeasurement.secondActiveSeconds -Field "$scenario.cycleMeasurements[$cycleIndex].secondActiveSeconds"
            $cycleRatio = Get-NumericDouble -Value $cycleMeasurement.firstToSecondRatio -Field "$scenario.cycleMeasurements[$cycleIndex].firstToSecondRatio"
            $minimumSeconds = $pairedMinimumCycleActiveMilliseconds / 1000.0
            if ($firstOperations -le 0 -or $secondOperations -le 0) {
                throw "Paired comparison '$scenario' cycle $cycleIndex contains a non-positive operation count."
            }
            $operationsPerWorkloadSuperCycle = 4 * $pairedBatchOperations
            if (
                $firstOperations -ne $secondOperations -or
                $firstOperations % $operationsPerWorkloadSuperCycle -ne 0
            ) {
                throw "Paired comparison '$scenario' cycle $cycleIndex does not retain balanced ABBA/BAAB operation counts."
            }
            if (
                $cycleRatio -le 0 -or
                [double]::IsNaN($cycleRatio) -or
                [double]::IsInfinity($cycleRatio)
            ) {
                throw "Paired comparison '$scenario' cycle $cycleIndex contains a non-positive or non-finite retained ratio."
            }
            foreach ($activeSeconds in @($firstSeconds, $secondSeconds)) {
                if (
                    $activeSeconds -lt $minimumSeconds -or
                    [double]::IsNaN($activeSeconds) -or
                    [double]::IsInfinity($activeSeconds)
                ) {
                    throw "Paired comparison '$scenario' cycle $cycleIndex did not reach the minimum active time."
                }
            }

            $recomputedCycleRatio = ($firstOperations / $firstSeconds) / ($secondOperations / $secondSeconds)
            $cycleRelativeError = [math]::Abs($cycleRatio - $recomputedCycleRatio) / $recomputedCycleRatio
            $rawRelativeError = [math]::Abs($numericRatios[$cycleIndex] - $recomputedCycleRatio) / $recomputedCycleRatio
            if ($cycleRelativeError -gt 1e-12 -or $rawRelativeError -gt 1e-12) {
                throw "Paired comparison '$scenario' cycle $cycleIndex ratio does not match its work and active time."
            }

            $firstTotalOperations += $firstOperations
            $firstTotalSeconds += $firstSeconds
            $secondTotalOperations += $secondOperations
            $secondTotalSeconds += $secondSeconds
        }

        $headline = Get-NumericDouble -Value $record.firstToSecondRatio -Field "$scenario.firstToSecondRatio"
        $aggregate = Get-NumericDouble -Value $record.aggregateRateRatio -Field "$scenario.aggregateRateRatio"
        $spread = Get-NumericDouble -Value $record.cycleRatioSpreadPercent -Field "$scenario.cycleRatioSpreadPercent"
        foreach ($value in @($headline, $aggregate)) {
            if ($value -le 0 -or [double]::IsNaN($value) -or [double]::IsInfinity($value)) {
                throw "Paired comparison '$scenario' contains a non-positive or non-finite rate ratio."
            }
        }
        $recomputedAggregate = ($firstTotalOperations / $firstTotalSeconds) / ($secondTotalOperations / $secondTotalSeconds)
        $aggregateRelativeError = [math]::Abs($aggregate - $recomputedAggregate) / $recomputedAggregate
        if ($aggregateRelativeError -gt 1e-12) {
            throw "Paired comparison '$scenario' aggregate ratio does not match its retained cycles."
        }
        if ($spread -lt 0 -or [double]::IsNaN($spread) -or [double]::IsInfinity($spread)) {
            throw "Paired comparison '$scenario' contains an invalid cycle spread."
        }

        $logSum = 0.0
        foreach ($ratio in $numericRatios) {
            $logSum += [math]::Log($ratio)
        }
        $recomputedHeadline = [math]::Exp($logSum / $numericRatios.Count)
        $relativeError = [math]::Abs($headline - $recomputedHeadline) / $recomputedHeadline
        if ($relativeError -gt 1e-12) {
            throw "Paired comparison '$scenario' headline is not the geometric mean of its raw cycle ratios."
        }

        $minimum = ($numericRatios | Measure-Object -Minimum).Minimum
        $maximum = ($numericRatios | Measure-Object -Maximum).Maximum
        $recomputedSpread = ($maximum / $minimum - 1.0) * 100.0
        if ([math]::Abs($spread - $recomputedSpread) -gt 1e-9) {
            throw "Paired comparison '$scenario' cycle spread does not match its raw cycle ratios."
        }

        $summaryRows.Add([ordered]@{
            scenario = $scenario
            firstToSecondRatio = $headline
            aggregateRateRatio = $aggregate
            cycleRatioSpreadPercent = $spread
            withinMaterialityBand = $spread -le $pairedMaterialityBandPercent
            cycleRatios = $numericRatios
            cycleMeasurements = $cycleMeasurements
        })
    }

    $summary = [ordered]@{
        schemaVersion = 1
        platform = $publishedPlatform
        commit = $publishedCommit
        protocol = $pairedProtocol
        cycles = $pairedCycles
        minimumCycleActiveMilliseconds = $pairedMinimumCycleActiveMilliseconds
        batchOperations = $pairedBatchOperations
        materialityBandPercent = $pairedMaterialityBandPercent
        allRowsWithinMaterialityBand = @($summaryRows | Where-Object { -not $_.withinMaterialityBand }).Count -eq 0
        rows = $summaryRows
    }
    $summaryDirectory = Split-Path -Parent $SummaryJsonPath
    $markdownDirectory = Split-Path -Parent $SummaryMarkdownPath
    foreach ($directory in @($summaryDirectory, $markdownDirectory) | Sort-Object -Unique) {
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        }
    }
    [System.IO.File]::WriteAllText(
        $SummaryJsonPath,
        ($summary | ConvertTo-Json -Depth 6) + "`n"
    )

    $markdown = [System.Collections.Generic.List[string]]::new()
    $markdown.Add('### In-process paired comparison')
    $markdown.Add('')
    $markdown.Add("Protocol: ``$($summary.protocol)``; commit: ``$publishedCommit``; platform: $publishedPlatform.")
    $markdown.Add('')
    $markdown.Add("| Scenario | DxMessaging / MessagePipe | Raw cycle spread | Within $pairedMaterialityBandPercent% band |")
    $markdown.Add('| --- | ---: | ---: | :---: |')
    foreach ($row in $summaryRows) {
        $ratioText = ([double]$row.firstToSecondRatio).ToString('F6', [System.Globalization.CultureInfo]::InvariantCulture)
        $spreadText = ([double]$row.cycleRatioSpreadPercent).ToString('F2', [System.Globalization.CultureInfo]::InvariantCulture)
        $bandText = if ($row.withinMaterialityBand) { 'yes' } else { 'no' }
        $markdown.Add("| ``$($row.scenario)`` | $ratioText | $spreadText% | $bandText |")
    }
    [System.IO.File]::WriteAllText($SummaryMarkdownPath, ($markdown -join "`n") + "`n")
}
finally {
    Pop-Location
}
