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

# Fields this summary renders. Requiring them is the consumer's own contract,
# not a second copy of the writer's full shape: a cell that cannot supply a
# column cannot be summarized, and must be reported rather than counted as
# complete with a hole in it.
$renderedCellFields = @(
    'libraryState',
    'messageTypeCount',
    'buildDurationMs',
    'editorBuildWallClockMs',
    'playerTotalBytes',
    'gameAssemblyBytes',
    'timings'
)
$renderedTimingFields = @(
    'engineStartToRunMs',
    'firstTypedDispatchUs',
    'dispatchLoopNsPerOp',
    'dispatchLoopShape'
)

function Read-ShippingCellEvidence {
    # Copy whatever the runner wrote, after proving the summary can render it.
    # Every field was validated before it was written, so the full shape is not
    # re-declared here.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$CellId
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "cell evidence missing at $Path"
    }
    $evidence = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($evidence -isnot [pscustomobject]) {
        throw "cell evidence at $Path is not a JSON object"
    }
    if (
        -not $evidence.PSObject.Properties['schemaVersion'] -or
        [int]$evidence.schemaVersion -ne 1
    ) {
        throw "cell evidence at $Path is not schema version 1"
    }
    $missingFields = @($renderedCellFields | Where-Object { -not $evidence.PSObject.Properties[$_] })
    if ($missingFields.Count -gt 0) {
        throw "cell evidence at $Path is missing rendered fields: $($missingFields -join ', ')"
    }
    if ($evidence.timings -isnot [pscustomobject]) {
        throw "cell evidence at $Path has no timings object"
    }
    $missingTimings = @(
        $renderedTimingFields | Where-Object { -not $evidence.timings.PSObject.Properties[$_] }
    )
    if ($missingTimings.Count -gt 0) {
        throw "cell evidence at $Path is missing rendered timings: $($missingTimings -join ', ')"
    }
    $row = [ordered]@{}
    foreach ($property in $evidence.PSObject.Properties) {
        $row[$property.Name] = $property.Value
    }
    # Seeded last so a stray cellId in the file cannot override the id of the
    # cell that actually produced it.
    $row['cellId'] = $CellId
    return $row
}

$failures = [System.Collections.Generic.List[string]]::new()
$failedCellIds = [System.Collections.Generic.List[string]]::new()
$unreadableEvidenceCellIds = [System.Collections.Generic.List[string]]::new()
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
        # The cell's stripping and AOT-root proof already passed. Unusable
        # diagnostic evidence is still a failure because producing it is the
        # point of this slice, but it is reported as its own class so nobody
        # reads it as a stripping regression.
        try {
            $cellRows.Add((
                Read-ShippingCellEvidence `
                    -Path (Join-Path (Join-Path $ArtifactsPath $shippingCaseId) 'shipping-cell-evidence.json') `
                    -CellId $shippingCaseId
            ))
        } catch {
            $failure = "{0}: passed its shipping proof but wrote unusable evidence: {1}" -f
                $shippingCaseId, $_.Exception.Message
            $failures.Add($failure)
            $unreadableEvidenceCellIds.Add($shippingCaseId)
            Write-Warning "Shipping-fidelity cell evidence is unusable; continuing to preserve later evidence. $failure"
        }
    }
}

# One summary per endpoint editor. Everything here is characterization of one
# clean build and one fresh player launch, never a published benchmark row.
# Only the file write is best-effort: a full disk must not hide the cell
# failures reported below, but it must not invent a passing leg either, so the
# write is the only thing allowed to fail quietly.
$matrixEvidencePath = Join-Path $ArtifactsPath 'shipping-matrix-evidence.json'
try {
    New-Item -ItemType Directory -Force -Path $ArtifactsPath | Out-Null
    $matrixEvidence = [ordered]@{
        schemaVersion = 1
        measurementClass = 'characterization'
        unityVersion = $UnityVersion
        cellCount = $shippingProfiles.Count * $shippingTopologies.Count
        completedCellCount = $cellRows.Count
        failedCells = @($failedCellIds.ToArray())
        unreadableEvidenceCells = @($unreadableEvidenceCellIds.ToArray())
        cells = @($cellRows.ToArray())
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        $matrixEvidencePath,
        (($matrixEvidence | ConvertTo-Json -Depth 10) + "`n"),
        $utf8NoBom
    )
} catch {
    Write-Warning "Could not write the shipping-fidelity matrix summary: $($_.Exception.Message)"
}

# The dispatch shape is printed because the semantic cell loops a class message
# and the cardinality cells loop a struct. Only rows sharing a shape are
# comparable with each other. Read-ShippingCellEvidence already proved every
# rendered field exists, so this loop cannot fail on a missing column; the
# finally still closes the log group if anything else does.
$tableFormat = '{0,-26} {1,-7} {2,5} {3,9} {4,9} {5,12} {6,12} {7,9} {8,9} {9,9} {10,-34}'
Write-Host "::group::Shipping-fidelity matrix characterization (Unity $UnityVersion)"
try {
    Write-Host (
        $tableFormat -f 'cell', 'library', 'types', 'build s', 'editor s', 'player B',
        'GameAssembly', 'start ms', 'first us', 'loop ns', 'dispatch loop shape'
    )
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
            ('{0:F1}' -f [double]$row['timings'].dispatchLoopNsPerOp),
            [string]$row['timings'].dispatchLoopShape
        )
    }
    foreach ($failedCellId in $failedCellIds) {
        Write-Host ('{0,-26} FAILED' -f $failedCellId)
    }
    foreach ($unreadableCellId in $unreadableEvidenceCellIds) {
        Write-Host ('{0,-26} EVIDENCE UNUSABLE' -f $unreadableCellId)
    }
    Write-Host "Matrix evidence: $matrixEvidencePath"
} finally {
    Write-Host '::endgroup::'
}

if ($failures.Count -gt 0) {
    throw "Shipping-fidelity cell failures: $($failures -join '; ')"
}
