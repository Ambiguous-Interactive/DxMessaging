#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UnityVersion,
    [Parameter(Mandatory = $true)][string]$UnityInstallRoot,
    [Parameter(Mandatory = $true)][string]$ArtifactsPath,
    [Parameter(Mandatory = $true)][string]$ProjectPathRoot,
    [Parameter(Mandatory = $true)][string]$CachePath,
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$RunnerPath = (Join-Path $PSScriptRoot 'run-ci-tests.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $RunnerPath -PathType Leaf)) {
    throw "Unity CI runner not found: $RunnerPath"
}

$shippingProfiles = @(
    [ordered]@{
        Level = 'minimal'
        Path = '.github/perf/shipping-fidelity-il2cpp-minimal-profile.v1.json'
    },
    [ordered]@{
        Level = 'low'
        Path = '.github/perf/shipping-fidelity-il2cpp-low-profile.v1.json'
    },
    [ordered]@{
        Level = 'medium'
        Path = '.github/perf/shipping-fidelity-il2cpp-medium-profile.v1.json'
    },
    [ordered]@{
        Level = 'high'
        Path = '.github/perf/shipping-fidelity-il2cpp-profile.v1.json'
    }
)
$shippingTopologies = @(
    [ordered]@{ Id = 'semantic-18'; Kind = 'semantic'; MessageTypeCount = 18 },
    [ordered]@{ Id = 'cardinality-1'; Kind = 'cardinality'; MessageTypeCount = 1 },
    [ordered]@{ Id = 'cardinality-16'; Kind = 'cardinality'; MessageTypeCount = 16 },
    [ordered]@{ Id = 'cardinality-256'; Kind = 'cardinality'; MessageTypeCount = 256 },
    [ordered]@{ Id = 'cardinality-1000'; Kind = 'cardinality'; MessageTypeCount = 1000 }
)

# SYNC: keep both lists identical to Write-ShippingCellEvidence and
# Test-ShippingStartupTimings in run-ci-tests.ps1. The matrix summary copies each
# cell row verbatim and fails a cell whose evidence drifts from this contract.
$cellEvidencePropertyNames = @(
    'schemaVersion',
    'profileId',
    'profileSha256',
    'managedStrippingLevel',
    'topologyId',
    'messageTypeCount',
    'unityVersion',
    'libraryState',
    'editorBuildWallClockMs',
    'buildDurationMs',
    'reportedTotalTimeMs',
    'reportedTotalSizeBytes',
    'buildStepCount',
    'playerFileCount',
    'playerTotalBytes',
    'playerExecutableBytes',
    'gameAssemblyBytes',
    'positivePlayerWallClockMs',
    'mutantPlayerWallClockMs',
    'timings'
)
$cellTimingPropertyNames = @(
    'engineStartToRunMs',
    'stopwatchFrequency',
    'stopwatchIsHighResolution',
    'busConstructionUs',
    'rootProbePhaseUs',
    'registrationPhaseUs',
    'firstTypedDispatchUs',
    'firstTypedDispatchCount',
    'typedPhaseUs',
    'untypedPhaseUs',
    'warmDispatchShape',
    'warmDispatchCount',
    'warmDispatchNsPerOp',
    'trimUs',
    'teardownUs'
)

function Assert-ExactPropertyNames {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Value -isnot [pscustomobject]) {
        throw "$Label must be a JSON object."
    }
    $actualNames = [string[]]@($Value.PSObject.Properties.Name)
    [Array]::Sort($actualNames, [System.StringComparer]::Ordinal)
    $expectedNames = [string[]]@($Expected)
    [Array]::Sort($expectedNames, [System.StringComparer]::Ordinal)
    if (($actualNames -join "`n") -cne ($expectedNames -join "`n")) {
        throw "$Label has unexpected JSON properties. Expected [$($expectedNames -join ', ')], observed [$($actualNames -join ', ')]."
    }
}

function Read-ShippingCellEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$CellId
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "cell evidence missing at $Path"
    }
    $evidence = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-ExactPropertyNames -Value $evidence -Expected $cellEvidencePropertyNames -Label "cell evidence at $Path"
    Assert-ExactPropertyNames -Value $evidence.timings -Expected $cellTimingPropertyNames -Label "cell evidence timings at $Path"
    if ([int]$evidence.schemaVersion -ne 1) {
        throw "cell evidence at $Path is not schema version 1"
    }
    $row = [ordered]@{ cellId = $CellId }
    foreach ($propertyName in $cellEvidencePropertyNames) {
        $row[$propertyName] = $evidence.$propertyName
    }
    return $row
}

$failures = [System.Collections.Generic.List[string]]::new()
$failedCellIds = [System.Collections.Generic.List[string]]::new()
$cellRows = [System.Collections.Generic.List[object]]::new()

foreach ($shippingProfile in $shippingProfiles) {
    foreach ($shippingTopology in $shippingTopologies) {
        $shippingCaseId = "$($shippingProfile.Level)-$($shippingTopology.Id)"
        $cellSucceeded = $false
        try {
            & $RunnerPath `
                -UnityVersion $UnityVersion `
                -UnityInstallRoot $UnityInstallRoot `
                -TestMode shipping `
                -AssemblyNames '' `
                -ArtifactsPath (Join-Path $ArtifactsPath $shippingCaseId) `
                -RepoRoot $RepoRoot `
                -ProjectPath (Join-Path $ProjectPathRoot "$UnityVersion-shipping-$shippingCaseId") `
                -CachePath $CachePath `
                -CanonicalProfilePath (Join-Path $RepoRoot $shippingProfile.Path) `
                -ShippingTopology $shippingTopology.Kind `
                -ShippingMessageTypeCount $shippingTopology.MessageTypeCount `
                -LicenseReturnOwner Central `
                -ReleaseCodeOptimization `
                -ReleasePlayerBuild
            $cellSucceeded = $true
        } catch {
            $failure = "{0}: {1}" -f $shippingCaseId, $_.Exception.Message
            $failures.Add($failure)
            $failedCellIds.Add($shippingCaseId)
            Write-Warning "Shipping-fidelity cell failed; continuing to preserve later evidence. $failure"
        }
        if (-not $cellSucceeded) {
            continue
        }
        try {
            $cellRows.Add((
                Read-ShippingCellEvidence `
                    -Path (Join-Path (Join-Path $ArtifactsPath $shippingCaseId) 'shipping-cell-evidence.json') `
                    -CellId $shippingCaseId
            ))
        } catch {
            $failure = "{0}: {1}" -f $shippingCaseId, $_.Exception.Message
            $failures.Add($failure)
            $failedCellIds.Add($shippingCaseId)
            Write-Warning "Shipping-fidelity cell evidence is unusable; continuing to preserve later evidence. $failure"
        }
    }
}

# One summary per endpoint editor: every completed cell's build-time, size, and
# cold-start row plus the IDs of cells that failed. The file is written before
# the aggregate throw so a partial matrix still leaves readable evidence.
New-Item -ItemType Directory -Force -Path $ArtifactsPath | Out-Null
$matrixEvidencePath = Join-Path $ArtifactsPath 'shipping-matrix-evidence.json'
$matrixEvidence = [ordered]@{
    schemaVersion = 1
    unityVersion = $UnityVersion
    cellCount = $shippingProfiles.Count * $shippingTopologies.Count
    completedCellCount = $cellRows.Count
    failedCells = @($failedCellIds.ToArray())
    cells = @($cellRows.ToArray())
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    $matrixEvidencePath,
    (($matrixEvidence | ConvertTo-Json -Depth 10) + "`n"),
    $utf8NoBom
)

$tableFormat = '{0,-26} {1,-7} {2,5} {3,9} {4,9} {5,12} {6,12} {7,9} {8,9} {9,8} {10,9}'
Write-Host "::group::Shipping-fidelity matrix evidence (Unity $UnityVersion)"
Write-Host ($tableFormat -f 'cell', 'library', 'types', 'build s', 'editor s', 'player B', 'GameAssembly', 'start ms', 'first us', 'warm ns', 'trim us')
foreach ($row in $cellRows) {
    Write-Host (
        $tableFormat -f
        $row['cellId'],
        $row['libraryState'],
        $row['messageTypeCount'],
        ('{0:F1}' -f ([double]$row['buildDurationMs'] / 1000.0)),
        ('{0:F1}' -f ([double]$row['editorBuildWallClockMs'] / 1000.0)),
        $row['playerTotalBytes'],
        $row['gameAssemblyBytes'],
        ('{0:F0}' -f [double]$row['timings'].engineStartToRunMs),
        ('{0:F1}' -f [double]$row['timings'].firstTypedDispatchUs),
        ('{0:F1}' -f [double]$row['timings'].warmDispatchNsPerOp),
        ('{0:F1}' -f [double]$row['timings'].trimUs)
    )
}
foreach ($failedCellId in $failedCellIds) {
    Write-Host ('{0,-26} FAILED' -f $failedCellId)
}
Write-Host "Matrix evidence: $matrixEvidencePath"
Write-Host '::endgroup::'

if ($failures.Count -gt 0) {
    throw "Shipping-fidelity cell failures: $($failures -join '; ')"
}
