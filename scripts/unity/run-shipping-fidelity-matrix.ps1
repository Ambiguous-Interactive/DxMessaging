#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UnityVersion,
    [Parameter(Mandatory = $true)][string]$UnityInstallRoot,
    [Parameter(Mandatory = $true)][string]$ArtifactsPath,
    [Parameter(Mandatory = $true)][string]$ProjectPathRoot,
    [Parameter(Mandatory = $true)][string]$CachePath,
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$RunnerPath = (Join-Path $PSScriptRoot 'run-ci-tests.ps1'),
    [switch]$RetainNativePayload,
    [switch]$RunIncrementalHighSemantic
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $RunnerPath -PathType Leaf)) {
    throw "Unity CI runner not found: $RunnerPath"
}
if ($RetainNativePayload -and $RunIncrementalHighSemantic) {
    throw 'Native payload retention and the incremental build factor are separate manual evidence modes.'
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

function Copy-NativePayloadEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$CellArtifactsPath,
        [Parameter(Mandatory = $true)][string]$PlayerRoot,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $sourceManifestPath = Join-Path $CellArtifactsPath 'shipping-player-manifest.json'
    $manifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json
    if (
        [int]$manifest.schemaVersion -ne 2 -or
        [string]$manifest.topologyId -cne 'semantic-18-v1' -or
        -not [bool]$manifest.playerDirectoryManifestMatches
    ) {
        throw 'Native payload retention requires the validated High semantic player manifest.'
    }
    $roles = @(
        [ordered]@{ Name = 'DxmShippingPlayer.exe'; Suffix = 'DxmShippingPlayer.exe' },
        [ordered]@{ Name = 'GameAssembly.dll'; Suffix = 'GameAssembly.dll' },
        [ordered]@{ Name = 'global-metadata.dat'; Suffix = 'global-metadata.dat' }
    )
    if (Test-Path -LiteralPath $Destination) {
        throw "Native payload destination already exists: $Destination"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -LiteralPath $sourceManifestPath -Destination (Join-Path $Destination 'source-player-manifest.json')
    foreach ($role in $roles) {
        $matches = @(
            $manifest.playerDirectoryManifestBefore.files |
                Where-Object {
                    $path = [string]$_.path
                    $path -ceq $role.Suffix -or $path.EndsWith("/$($role.Suffix)", [StringComparison]::Ordinal)
                }
        )
        if ($matches.Count -ne 1) {
            throw "Native player manifest must contain exactly one $($role.Suffix)."
        }
        $sourcePath = $PlayerRoot
        foreach ($segment in ([string]$matches[0].path).Split('/')) {
            $sourcePath = Join-Path $sourcePath $segment
        }
        $source = Get-Item -LiteralPath $sourcePath -ErrorAction Stop
        $sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
        if (
            [long]$source.Length -ne [long]$matches[0].length -or
            $sourceHash -cne [string]$matches[0].sha256
        ) {
            throw "Native payload $($role.Suffix) differs from its validated player manifest."
        }
        $destinationPath = Join-Path $Destination $role.Name
        Copy-Item -LiteralPath $source.FullName -Destination $destinationPath
        $copy = Get-Item -LiteralPath $destinationPath -ErrorAction Stop
        $copyHash = (Get-FileHash -LiteralPath $copy.FullName -Algorithm SHA256).Hash
        if ([long]$copy.Length -ne [long]$matches[0].length -or $copyHash -cne $sourceHash) {
            throw "Native payload copy $($role.Name) differs from its validated source."
        }
    }
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
            $runIncremental = $RunIncrementalHighSemantic -and $shippingCaseId -ceq 'high-semantic-18'
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
                -ShippingIncrementalBuild:$runIncremental `
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
            $cellRow = Read-ShippingCellEvidence `
                -Path (Join-Path (Join-Path $ArtifactsPath $shippingCaseId) 'shipping-cell-evidence.json') `
                -CellId $shippingCaseId
            $cellRows.Add($cellRow)
        } catch {
            $failure = "{0}: passed its shipping proof but wrote unusable evidence: {1}" -f
                $shippingCaseId, $_.Exception.Message
            $failures.Add($failure)
            $unreadableEvidenceCellIds.Add($shippingCaseId)
            Write-Warning "Shipping-fidelity cell evidence is unusable; continuing to preserve later evidence. $failure"
            continue
        }
        if ($RetainNativePayload -and $shippingCaseId -ceq 'high-semantic-18') {
            try {
                $cellArtifactsPath = Join-Path $ArtifactsPath $shippingCaseId
                $playerRoot = Join-Path (
                    Join-Path $ProjectPathRoot "$UnityVersion-shipping-$shippingCaseId"
                ) 'Build/DxmShippingPlayer'
                Copy-NativePayloadEvidence `
                    -CellArtifactsPath $cellArtifactsPath `
                    -PlayerRoot $playerRoot `
                    -Destination "$ArtifactsPath-native-payload"
            } catch {
                $failure = "{0}: native payload retention failed: {1}" -f
                    $shippingCaseId, $_.Exception.Message
                $failures.Add($failure)
                Write-Warning "Shipping-fidelity native payload is unusable. $failure"
            }
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
