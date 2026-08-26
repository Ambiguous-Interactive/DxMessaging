[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselinePath,

    [Parameter(Mandatory = $true)]
    [string[]]$EvidencePaths,

    [Parameter(Mandatory = $true)]
    [string]$ProcessEvidencePath,

    [Parameter(Mandatory = $true)]
    [string]$CpuProfilePath,

    [Parameter(Mandatory = $true)]
    [string]$SummaryJsonPath,

    [Parameter(Mandatory = $true)]
    [string]$SummaryMarkdownPath,

    [string]$BracketManifestPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    throw "Comparison baseline does not exist: $BaselinePath"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$bracketManifestSha256 = $null
$candidatePaths = @()
if (-not [string]::IsNullOrWhiteSpace($BracketManifestPath)) {
    $resolvedBracketManifestPath = if ([System.IO.Path]::IsPathRooted($BracketManifestPath)) {
        $BracketManifestPath
    }
    else {
        Join-Path $repoRoot $BracketManifestPath
    }
    if (-not (Test-Path -LiteralPath $resolvedBracketManifestPath -PathType Leaf)) {
        throw "Bracket manifest does not exist: $resolvedBracketManifestPath"
    }
    $bracketManifestSha256 = (Get-FileHash -LiteralPath $resolvedBracketManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $bracketManifest = Get-Content -LiteralPath $resolvedBracketManifestPath -Raw | ConvertFrom-Json
    $candidatePaths = @($bracketManifest.candidatePaths)
    if ($candidatePaths.Count -eq 0) {
        throw 'Bracket manifest candidatePaths must contain at least one runtime source path.'
    }
}
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

function Get-ExactString {
    param(
        [object]$Value,
        [string]$Field
    )

    if ($Value -isnot [string]) {
        throw "Evidence field '$Field' must be a string."
    }
    return [string]$Value
}

function Get-ExactBoolean {
    param(
        [object]$Value,
        [string]$Field
    )

    if ($Value -isnot [bool]) {
        throw "Evidence field '$Field' must be a Boolean."
    }
    return [bool]$Value
}

function Get-OptionalErrorString {
    param(
        [object]$Value,
        [string]$Field
    )

    if ($null -eq $Value) {
        return ''
    }
    return Get-ExactString -Value $Value -Field $Field
}

Push-Location $repoRoot
try {
    foreach ($requiredEvidencePath in @($ProcessEvidencePath, $CpuProfilePath)) {
        if (-not (Test-Path -LiteralPath $requiredEvidencePath -PathType Leaf)) {
            throw "Required comparison execution evidence does not exist: $requiredEvidencePath"
        }
    }
    $processEvidence = Get-Content -LiteralPath $ProcessEvidencePath -Raw | ConvertFrom-Json
    $cpuProfile = Get-Content -LiteralPath $CpuProfilePath -Raw | ConvertFrom-Json
    $expectedExecutionProfileId = 'highest-efficiency-class-affinity-normal-v1'
    $profileSchemaVersion = Get-ExactInt64 -Value $cpuProfile.schemaVersion -Field 'cpuProfile.schemaVersion'
    $executionProfileId = Get-ExactString -Value $cpuProfile.executionProfileId -Field 'cpuProfile.executionProfileId'
    $profileSource = Get-ExactString -Value $cpuProfile.source -Field 'cpuProfile.source'
    $selectionPolicy = Get-ExactString -Value $cpuProfile.selectionPolicy -Field 'cpuProfile.selectionPolicy'
    $cpuModel = Get-ExactString -Value $cpuProfile.cpuModel -Field 'cpuProfile.cpuModel'
    $processorGroup = Get-ExactInt64 -Value $cpuProfile.processorGroup -Field 'cpuProfile.processorGroup'
    $logicalProcessorCount = Get-ExactInt64 -Value $cpuProfile.logicalProcessorCount -Field 'cpuProfile.logicalProcessorCount'
    $selectedLogicalProcessorCount = Get-ExactInt64 -Value $cpuProfile.selectedLogicalProcessorCount -Field 'cpuProfile.selectedLogicalProcessorCount'
    $declaredSelectedCoreCount = Get-ExactInt64 -Value $cpuProfile.selectedCoreCount -Field 'cpuProfile.selectedCoreCount'
    $profileAffinityMask = Get-ExactString -Value $cpuProfile.affinityMask -Field 'cpuProfile.affinityMask'
    $profilePriorityClass = Get-ExactString -Value $cpuProfile.priorityClass -Field 'cpuProfile.priorityClass'
    $declaredEfficiencyClasses = @($cpuProfile.efficiencyClasses)
    for ($classIndex = 0; $classIndex -lt $declaredEfficiencyClasses.Count; $classIndex++) {
        [void](Get-ExactInt64 -Value $declaredEfficiencyClasses[$classIndex].value -Field "cpuProfile.efficiencyClasses[$classIndex].value")
        [void](Get-ExactInt64 -Value $declaredEfficiencyClasses[$classIndex].logicalProcessorCount -Field "cpuProfile.efficiencyClasses[$classIndex].logicalProcessorCount")
    }
    if (
        $profileSchemaVersion -ne 1 -or
        $executionProfileId -cne $expectedExecutionProfileId -or
        $profileSource -cne 'GetSystemCpuSetInformation' -or
        $selectionPolicy -cne 'maximum EfficiencyClass' -or
        $cpuModel -notmatch 'i9-13900KF' -or
        $processorGroup -ne 0 -or
        $logicalProcessorCount -ne 32 -or
        $declaredEfficiencyClasses.Count -ne 2 -or
        $selectedLogicalProcessorCount -ne 16 -or
        $declaredSelectedCoreCount -ne 8 -or
        @($cpuProfile.selectedLogicalProcessorIndices).Count -ne 16 -or
        $profileAffinityMask -cnotmatch '^0x[0-9A-F]+$' -or
        $profilePriorityClass -cne 'Normal'
    ) {
        throw 'The comparison CPU profile does not match the pinned highest-efficiency-class execution contract.'
    }
    $profileCpuSets = @($cpuProfile.cpuSets)
    $typedCpuSets = [System.Collections.Generic.List[object]]::new()
    for ($cpuSetIndex = 0; $cpuSetIndex -lt $profileCpuSets.Count; $cpuSetIndex++) {
        $cpuSet = $profileCpuSets[$cpuSetIndex]
        $typedCpuSets.Add([ordered]@{
            group = Get-ExactInt64 -Value $cpuSet.group -Field "cpuProfile.cpuSets[$cpuSetIndex].group"
            logicalProcessorIndex = Get-ExactInt64 -Value $cpuSet.logicalProcessorIndex -Field "cpuProfile.cpuSets[$cpuSetIndex].logicalProcessorIndex"
            coreIndex = Get-ExactInt64 -Value $cpuSet.coreIndex -Field "cpuProfile.cpuSets[$cpuSetIndex].coreIndex"
            efficiencyClass = Get-ExactInt64 -Value $cpuSet.efficiencyClass -Field "cpuProfile.cpuSets[$cpuSetIndex].efficiencyClass"
            allocated = Get-ExactBoolean -Value $cpuSet.allocated -Field "cpuProfile.cpuSets[$cpuSetIndex].allocated"
            allocatedToTargetProcess = Get-ExactBoolean -Value $cpuSet.allocatedToTargetProcess -Field "cpuProfile.cpuSets[$cpuSetIndex].allocatedToTargetProcess"
        })
    }
    $profileCpuSets = @($typedCpuSets.ToArray())
    $profileGroups = @(
        $profileCpuSets | ForEach-Object { $_.group } | Sort-Object -Unique
    )
    $maximumEfficiencyClass = [int](
        ($profileCpuSets | ForEach-Object { $_.efficiencyClass } | Measure-Object -Maximum).Maximum
    )
    $selectedCpuSets = @($profileCpuSets | Where-Object {
        $_.efficiencyClass -eq $maximumEfficiencyClass
    })
    $selectedIndices = @(
        $selectedCpuSets |
            ForEach-Object { $_.logicalProcessorIndex } |
            Sort-Object
    )
    $declaredIndices = @(
        $cpuProfile.selectedLogicalProcessorIndices |
            ForEach-Object {
                Get-ExactInt64 -Value $_ -Field 'cpuProfile.selectedLogicalProcessorIndices'
            } |
            Sort-Object
    )
    [uint64]$recomputedAffinityMask = 0
    foreach ($logicalProcessorIndex in $selectedIndices) {
        if ($logicalProcessorIndex -lt 0 -or $logicalProcessorIndex -gt 62) {
            throw 'The comparison CPU profile contains an unsupported logical processor index.'
        }
        $recomputedAffinityMask = $recomputedAffinityMask -bor (
            [uint64]1 -shl $logicalProcessorIndex
        )
    }
    $recomputedAffinityText = '0x{0:X}' -f $recomputedAffinityMask
    $indexDifference = @(
        Compare-Object -ReferenceObject $declaredIndices -DifferenceObject $selectedIndices
    )
    $selectedCoreCount = @(
        $selectedCpuSets | ForEach-Object { $_.coreIndex } | Sort-Object -Unique
    ).Count
    if (
        $profileCpuSets.Count -ne 32 -or
        $profileGroups.Count -ne 1 -or
        $profileGroups[0] -ne 0 -or
        (Get-ExactInt64 -Value $cpuProfile.selectedEfficiencyClass -Field 'cpuProfile.selectedEfficiencyClass') -ne $maximumEfficiencyClass -or
        $selectedIndices.Count -ne 16 -or
        $indexDifference.Count -ne 0 -or
        $selectedCoreCount -ne 8 -or
        $profileAffinityMask -cne $recomputedAffinityText
    ) {
        throw 'The comparison CPU profile selection does not match its retained CPU-set topology.'
    }
    $processSchemaVersion = Get-ExactInt64 -Value $processEvidence.schemaVersion -Field 'process.schemaVersion'
    $processId = Get-ExactInt64 -Value $processEvidence.processId -Field 'process.processId'
    $requestedAffinityMask = Get-ExactString -Value $processEvidence.requestedProcessorAffinityMask -Field 'process.requestedProcessorAffinityMask'
    $actualAffinityMask = Get-ExactString -Value $processEvidence.actualProcessorAffinityMask -Field 'process.actualProcessorAffinityMask'
    $affinityError = Get-OptionalErrorString -Value $processEvidence.processorAffinityError -Field 'process.processorAffinityError'
    $requestedPriorityClass = Get-ExactString -Value $processEvidence.requestedPriorityClass -Field 'process.requestedPriorityClass'
    $actualPriorityClass = Get-ExactString -Value $processEvidence.actualPriorityClass -Field 'process.actualPriorityClass'
    $priorityError = Get-OptionalErrorString -Value $processEvidence.processorPriorityError -Field 'process.processorPriorityError'
    $settingsVerified = Get-ExactBoolean -Value $processEvidence.processSettingsVerified -Field 'process.processSettingsVerified'
    $settingsError = Get-OptionalErrorString -Value $processEvidence.processSettingsError -Field 'process.processSettingsError'
    [void](Get-ExactInt64 -Value $processEvidence.exitCode -Field 'process.exitCode')
    [void](Get-ExactBoolean -Value $processEvidence.timedOut -Field 'process.timedOut')
    if (
        $processSchemaVersion -ne 1 -or
        $processId -le 0 -or
        $requestedAffinityMask -cne $profileAffinityMask -or
        $actualAffinityMask -cne $profileAffinityMask -or
        -not [string]::IsNullOrWhiteSpace($affinityError) -or
        $requestedPriorityClass -cne $profilePriorityClass -or
        $actualPriorityClass -cne $profilePriorityClass -or
        -not [string]::IsNullOrWhiteSpace($priorityError) -or
        -not $settingsVerified -or
        -not [string]::IsNullOrWhiteSpace($settingsError)
    ) {
        throw 'The comparison player process evidence does not match its CPU profile.'
    }

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
    if ($publishedCommit -cnotmatch '^[0-9a-f]{40}$') {
        throw "Published comparison rows must carry a full lowercase Git SHA-1: '$publishedCommit'."
    }
    if ($publishedPlatform -cnotmatch '^Standalone IL2CPP x64 Release \(WindowsPlayer; Unity [^)]+\)$') {
        throw "Published comparison rows use an invalid platform: '$publishedPlatform'."
    }

    $headCommitOutput = @(& git -C $repoRoot rev-parse HEAD 2>&1)
    if ($LASTEXITCODE -ne 0 -or $headCommitOutput.Count -ne 1) {
        throw 'Could not resolve the checked-out Git commit for paired comparison provenance.'
    }
    $headCommit = ([string]$headCommitOutput[0]).Trim()
    if ($headCommit -cne $publishedCommit) {
        throw "Checked-out HEAD '$headCommit' does not match published commit '$publishedCommit'."
    }
    $measuredSourcePaths = @(
        'Runtime',
        'Tests/Runtime/Benchmarks',
        'Tests/Runtime/Comparisons',
        'Tests/Runtime/Scripts/Messages'
    )
    & git -C $repoRoot diff --quiet -- $measuredSourcePaths
    $workingDiffExitCode = $LASTEXITCODE
    if ($workingDiffExitCode -eq 1) {
        throw 'Measured runtime or benchmark sources differ from the checked-out commit.'
    }
    if ($workingDiffExitCode -ne 0) {
        throw "Could not verify measured working-tree sources; git diff exited $workingDiffExitCode."
    }
    & git -C $repoRoot diff --cached --quiet -- $measuredSourcePaths
    $indexDiffExitCode = $LASTEXITCODE
    if ($indexDiffExitCode -eq 1) {
        throw 'The index contains measured runtime or benchmark source changes.'
    }
    if ($indexDiffExitCode -ne 0) {
        throw "Could not verify measured index sources; git diff exited $indexDiffExitCode."
    }
    $sourceTreeOutput = @(& git -C $repoRoot rev-parse "${publishedCommit}^{tree}" 2>&1)
    if ($LASTEXITCODE -ne 0 -or $sourceTreeOutput.Count -ne 1) {
        throw 'Could not resolve the published Git source tree for paired comparison provenance.'
    }
    $sourceTree = ([string]$sourceTreeOutput[0]).Trim()
    if ($sourceTree -cnotmatch '^[0-9a-f]{40}$') {
        throw "Paired comparison source tree is not a full lowercase Git tree SHA-1: '$sourceTree'."
    }
    $candidateSourceSha256 = $null
    if ($candidatePaths.Count -ne 0) {
        foreach ($candidatePath in $candidatePaths) {
            $candidatePathRows = @(& git -C $repoRoot ls-tree -r --full-tree $publishedCommit -- $candidatePath 2>&1)
            if ($LASTEXITCODE -ne 0 -or $candidatePathRows.Count -eq 0) {
                throw "Could not resolve candidate source path '$candidatePath' at the published commit."
            }
        }
        $gitArguments = @('-C', $repoRoot, 'ls-tree', '-r', '--full-tree', $publishedCommit, '--') + $candidatePaths
        $candidateSourceRows = @(& git @gitArguments 2>&1)
        if ($LASTEXITCODE -ne 0 -or $candidateSourceRows.Count -eq 0) {
            throw 'Could not resolve every predeclared candidate source path at the published commit.'
        }
        $candidateSourcePayload = ($candidateSourceRows -join "`n") + "`n"
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $candidateSourceHash = $sha256.ComputeHash(
                [System.Text.Encoding]::UTF8.GetBytes($candidateSourcePayload)
            )
        }
        finally {
            $sha256.Dispose()
        }
        $candidateSourceSha256 = -join @($candidateSourceHash | ForEach-Object { $_.ToString('x2') })
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
        schemaVersion = 2
        platform = $publishedPlatform
        commit = $publishedCommit
        sourceTree = $sourceTree
        candidateSourceSha256 = $candidateSourceSha256
        executionProfile = [ordered]@{
            id = $cpuProfile.executionProfileId
            cpuModel = $cpuProfile.cpuModel
            source = $cpuProfile.source
            selectionPolicy = $cpuProfile.selectionPolicy
            selectedEfficiencyClass = $cpuProfile.selectedEfficiencyClass
            selectedLogicalProcessorIndices = @($cpuProfile.selectedLogicalProcessorIndices)
            affinityMask = $cpuProfile.affinityMask
            priorityClass = $cpuProfile.priorityClass
        }
        protocol = $pairedProtocol
        cycles = $pairedCycles
        minimumCycleActiveMilliseconds = $pairedMinimumCycleActiveMilliseconds
        batchOperations = $pairedBatchOperations
        materialityBandPercent = $pairedMaterialityBandPercent
        bracketManifestSha256 = $bracketManifestSha256
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
    $markdown.Add("Protocol: ``$($summary.protocol)``; commit: ``$publishedCommit``; source tree: ``$sourceTree``; platform: $publishedPlatform.")
    if ($null -ne $summary.candidateSourceSha256) {
        $markdown.Add("Candidate source SHA-256: ``$($summary.candidateSourceSha256)``.")
    }
    $markdown.Add("Execution profile: ``$($summary.executionProfile.id)``; affinity: ``$($summary.executionProfile.affinityMask)``; priority: ``$($summary.executionProfile.priorityClass)``.")
    if ($null -ne $summary.bracketManifestSha256) {
        $markdown.Add("Bracket manifest SHA-256: ``$($summary.bracketManifestSha256)``.")
    }
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
