#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$unityScriptsRoot = Split-Path -Parent $PSScriptRoot
$runnerPath = Join-Path $unityScriptsRoot 'run-ci-tests.ps1'
$matrixRunnerPath = Join-Path $unityScriptsRoot 'run-shipping-fidelity-matrix.ps1'
$validatorPath = Join-Path $unityScriptsRoot 'validate-il2cpp-profile.ps1'
$profilePath = Join-Path $repoRoot '.github/perf/shipping-fidelity-il2cpp-profile.v1.json'
$profileCases = @(
    [ordered]@{
        FileName = 'shipping-fidelity-il2cpp-minimal-profile.v1.json'
        ProfileId = 'shipping-fidelity-il2cpp-minimal-player-v1'
        ManagedStrippingLevel = 'Minimal'
    },
    [ordered]@{
        FileName = 'shipping-fidelity-il2cpp-low-profile.v1.json'
        ProfileId = 'shipping-fidelity-il2cpp-low-player-v1'
        ManagedStrippingLevel = 'Low'
    },
    [ordered]@{
        FileName = 'shipping-fidelity-il2cpp-medium-profile.v1.json'
        ProfileId = 'shipping-fidelity-il2cpp-medium-player-v1'
        ManagedStrippingLevel = 'Medium'
    },
    [ordered]@{
        FileName = 'shipping-fidelity-il2cpp-profile.v1.json'
        ProfileId = 'shipping-fidelity-il2cpp-player-v1'
        ManagedStrippingLevel = 'High'
    }
)
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dxm-shipping-fidelity-{0}" -f [guid]::NewGuid().ToString('N'))

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][bool]$Condition
    )

    if (-not $Condition) {
        throw "Assertion failed: $Description"
    }
}

function Assert-Fails {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    try {
        & $Action
    } catch {
        if ($_.Exception.Message.Contains($ExpectedMessage)) {
            return
        }
        throw "Expected '$Description' to contain '$ExpectedMessage', observed '$($_.Exception.Message)'."
    }
    throw "Expected failure: $Description"
}

function Copy-JsonValue {
    param([Parameter(Mandatory = $true)]$Value)
    return $Value | ConvertTo-Json -Depth 10 | ConvertFrom-Json
}

try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null

    $matrixInvocationLogPath = Join-Path $fixtureRoot 'matrix-invocations.txt'
    $mockRunnerPath = Join-Path $fixtureRoot 'mock-run-ci-tests.ps1'
    $escapedMatrixInvocationLogPath = $matrixInvocationLogPath.Replace("'", "''")
    [System.IO.File]::WriteAllText(
        $mockRunnerPath,
        @"
param(
    [string]`$UnityVersion,
    [string]`$UnityInstallRoot,
    [string]`$TestMode,
    [string]`$AssemblyNames,
    [string]`$ArtifactsPath,
    [string]`$RepoRoot,
    [string]`$ProjectPath,
    [string]`$CachePath,
    [string]`$CanonicalProfilePath,
    [string]`$ShippingTopology,
    [int]`$ShippingMessageTypeCount,
    [string]`$LicenseReturnOwner,
    [switch]`$ReleaseCodeOptimization,
    [switch]`$ReleasePlayerBuild
)

Add-Content -LiteralPath '$escapedMatrixInvocationLogPath' -Value (([ordered]@{
    unityVersion = `$UnityVersion
    unityInstallRoot = `$UnityInstallRoot
    testMode = `$TestMode
    assemblyNames = `$AssemblyNames
    artifactsPath = `$ArtifactsPath
    repoRoot = `$RepoRoot
    projectPath = `$ProjectPath
    cachePath = `$CachePath
    canonicalProfilePath = `$CanonicalProfilePath
    shippingTopology = `$ShippingTopology
    shippingMessageTypeCount = `$ShippingMessageTypeCount
    licenseReturnOwner = `$LicenseReturnOwner
    releaseCodeOptimization = `$ReleaseCodeOptimization.IsPresent
    releasePlayerBuild = `$ReleasePlayerBuild.IsPresent
} | ConvertTo-Json -Compress))
if (
    `$CanonicalProfilePath -clike '*-minimal-profile.v1.json' -and
    `$ShippingTopology -ceq 'semantic'
) {
    throw 'synthetic Minimal failure'
}
# Emit the same cell evidence shape the real runner writes so the matrix
# wrapper's summary contract is exercised end to end. The low-Low semantic cell
# writes a truncated file to prove one unusable cell fails without discarding
# the remaining evidence.
New-Item -ItemType Directory -Force -Path `$ArtifactsPath | Out-Null
`$cellEvidencePath = Join-Path `$ArtifactsPath 'shipping-cell-evidence.json'
if (
    `$CanonicalProfilePath -clike '*-low-profile.v1.json' -and
    `$ShippingTopology -ceq 'semantic'
) {
    [System.IO.File]::WriteAllText(`$cellEvidencePath, '{"schemaVersion":1}')
    return
}
`$cellEvidence = [ordered]@{
    schemaVersion = 1
    profileId = 'mock-profile'
    profileSha256 = ('a' * 64)
    managedStrippingLevel = 'High'
    topologyId = "`$ShippingTopology-`$ShippingMessageTypeCount-v1"
    messageTypeCount = `$ShippingMessageTypeCount
    unityVersion = `$UnityVersion
    libraryState = 'cold'
    editorBuildWallClockMs = 90000.0
    buildDurationMs = 60000.0
    reportedTotalTimeMs = 59000.0
    reportedTotalSizeBytes = 33000000
    buildStepCount = 12
    playerFileCount = 40
    playerTotalBytes = 34000000
    playerExecutableBytes = 640000
    gameAssemblyBytes = 22000000
    positivePlayerWallClockMs = 1500.0
    mutantPlayerWallClockMs = 900.0
    timings = [ordered]@{
        engineStartToRunMs = 210.5
        stopwatchFrequency = 10000000
        stopwatchIsHighResolution = `$true
        busConstructionUs = 12.5
        rootProbePhaseUs = 800.25
        registrationPhaseUs = 320.75
        firstTypedDispatchUs = 45.5
        firstTypedDispatchCount = 1
        typedPhaseUs = 90.5
        untypedPhaseUs = 130.25
        warmDispatchShape = 'DxmShippingCardinalityMessage0001'
        warmDispatchCount = 1000000
        warmDispatchNsPerOp = 21.5
        trimUs = 60.5
        teardownUs = 40.25
    }
}
[System.IO.File]::WriteAllText(
    `$cellEvidencePath,
    ((`$cellEvidence | ConvertTo-Json -Depth 10) + "``n")
)
"@
    )
    Assert-Fails 'shipping matrix aggregates a first-cell failure after every profile runs' {
        & $matrixRunnerPath `
            -UnityVersion '6000.3.16f1' `
            -UnityInstallRoot (Join-Path $fixtureRoot 'unity') `
            -ArtifactsPath (Join-Path $fixtureRoot 'matrix-artifacts') `
            -ProjectPathRoot (Join-Path $fixtureRoot 'matrix-projects') `
            -CachePath (Join-Path $fixtureRoot 'matrix-cache') `
            -RepoRoot $repoRoot `
            -RunnerPath $mockRunnerPath
    } 'Shipping-fidelity cell failures: minimal-semantic-18'
    $matrixInvocations = @(
        Get-Content -LiteralPath $matrixInvocationLogPath | ConvertFrom-Json
    )
    $expectedMatrixCases = [System.Collections.Generic.List[object]]::new()
    foreach ($profileCase in $profileCases) {
        foreach ($topologyCase in @(
            [ordered]@{ Id = 'semantic-18'; Kind = 'semantic'; MessageTypeCount = 18 },
            [ordered]@{ Id = 'cardinality-1'; Kind = 'cardinality'; MessageTypeCount = 1 },
            [ordered]@{ Id = 'cardinality-16'; Kind = 'cardinality'; MessageTypeCount = 16 },
            [ordered]@{ Id = 'cardinality-256'; Kind = 'cardinality'; MessageTypeCount = 256 },
            [ordered]@{ Id = 'cardinality-1000'; Kind = 'cardinality'; MessageTypeCount = 1000 }
        )) {
            $expectedMatrixCases.Add([ordered]@{
                    Level = "$($profileCase.ManagedStrippingLevel.ToLowerInvariant())-$($topologyCase.Id)"
                    Profile = $profileCase.FileName
                    Topology = $topologyCase.Kind
                    MessageTypeCount = $topologyCase.MessageTypeCount
                })
        }
    }
    Assert-That 'shipping matrix continues through every profile after an early failure' (
        $matrixInvocations.Count -eq $expectedMatrixCases.Count
    )
    for ($matrixIndex = 0; $matrixIndex -lt $expectedMatrixCases.Count; $matrixIndex++) {
        $matrixInvocation = $matrixInvocations[$matrixIndex]
        $expectedMatrixCase = $expectedMatrixCases[$matrixIndex]
        Assert-That "$($expectedMatrixCase.Level) shipping matrix delegates the exact runner contract" (
            $matrixInvocation.unityVersion -ceq '6000.3.16f1' -and
            $matrixInvocation.unityInstallRoot -ceq (Join-Path $fixtureRoot 'unity') -and
            $matrixInvocation.testMode -ceq 'shipping' -and
            $matrixInvocation.assemblyNames -ceq '' -and
            $matrixInvocation.artifactsPath -ceq (
                Join-Path (Join-Path $fixtureRoot 'matrix-artifacts') $expectedMatrixCase.Level
            ) -and
            $matrixInvocation.repoRoot -ceq $repoRoot -and
            $matrixInvocation.projectPath -ceq (
                Join-Path (Join-Path $fixtureRoot 'matrix-projects') (
                    "6000.3.16f1-shipping-$($expectedMatrixCase.Level)"
                )
            ) -and
            $matrixInvocation.cachePath -ceq (Join-Path $fixtureRoot 'matrix-cache') -and
            $matrixInvocation.canonicalProfilePath -ceq (
                Join-Path $repoRoot ".github/perf/$($expectedMatrixCase.Profile)"
            ) -and
            $matrixInvocation.shippingTopology -ceq $expectedMatrixCase.Topology -and
            [int]$matrixInvocation.shippingMessageTypeCount -eq $expectedMatrixCase.MessageTypeCount -and
            $matrixInvocation.licenseReturnOwner -ceq 'Central' -and
            $matrixInvocation.releaseCodeOptimization -eq $true -and
            $matrixInvocation.releasePlayerBuild -eq $true
        )
    }

    $matrixEvidence = Get-Content -LiteralPath (
        Join-Path (Join-Path $fixtureRoot 'matrix-artifacts') 'shipping-matrix-evidence.json'
    ) -Raw | ConvertFrom-Json
    $matrixEvidenceCells = @($matrixEvidence.cells)
    $matrixFailedCells = @($matrixEvidence.failedCells)
    $expectedFailedCells = @('minimal-semantic-18', 'low-semantic-18')
    Assert-That 'shipping matrix summarizes every completed cell and names the failed cells' (
        [int]$matrixEvidence.schemaVersion -eq 1 -and
        $matrixEvidence.unityVersion -ceq '6000.3.16f1' -and
        [int]$matrixEvidence.cellCount -eq $expectedMatrixCases.Count -and
        [int]$matrixEvidence.completedCellCount -eq ($expectedMatrixCases.Count - $expectedFailedCells.Count) -and
        $matrixEvidenceCells.Count -eq ($expectedMatrixCases.Count - $expectedFailedCells.Count) -and
        ($matrixFailedCells -join "`n") -ceq ($expectedFailedCells -join "`n")
    )
    $matrixEvidenceCellIds = @($matrixEvidenceCells | ForEach-Object { $_.cellId })
    $expectedMatrixEvidenceCellIds = @(
        $expectedMatrixCases |
            ForEach-Object { $_.Level } |
            Where-Object { $expectedFailedCells -cnotcontains $_ }
    )
    Assert-That 'shipping matrix keeps completed cells in dependency order' (
        ($matrixEvidenceCellIds -join "`n") -ceq ($expectedMatrixEvidenceCellIds -join "`n")
    )
    $firstMatrixCell = $matrixEvidenceCells[0]
    Assert-That 'shipping matrix copies each cell build, size, and cold-start row verbatim' (
        $firstMatrixCell.cellId -ceq 'minimal-cardinality-1' -and
        [int]$firstMatrixCell.messageTypeCount -eq 1 -and
        $firstMatrixCell.topologyId -ceq 'cardinality-1-v1' -and
        $firstMatrixCell.libraryState -ceq 'cold' -and
        [double]$firstMatrixCell.buildDurationMs -eq 60000.0 -and
        [double]$firstMatrixCell.editorBuildWallClockMs -eq 90000.0 -and
        [long]$firstMatrixCell.playerTotalBytes -eq 34000000 -and
        [long]$firstMatrixCell.gameAssemblyBytes -eq 22000000 -and
        [double]$firstMatrixCell.timings.engineStartToRunMs -eq 210.5 -and
        [double]$firstMatrixCell.timings.warmDispatchNsPerOp -eq 21.5
    )

    $runnerText = Get-Content -LiteralPath $runnerPath -Raw
    Assert-That 'cardinality generation uses PowerShell 5.1-safe typed phase call lists' (
        $runnerText.Contains('$probeCalls = [System.Collections.Generic.List[string]]::new()') -and
        $runnerText.Contains('$registrationCalls = [System.Collections.Generic.List[string]]::new()') -and
        $runnerText.Contains('$typedCalls = [System.Collections.Generic.List[string]]::new()') -and
        $runnerText.Contains('$untypedCalls = [System.Collections.Generic.List[string]]::new()') -and
        -not [regex]::IsMatch($runnerText, '\[regex\]::Matches\([\s\S]*?\)\.Value\s+-join')
    )
    $tokens = $null
    $parseErrors = $null
    $runnerAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $runnerPath,
        [ref]$tokens,
        [ref]$parseErrors
    )
    if (@($parseErrors).Count -gt 0) {
        throw "run-ci-tests.ps1 has parse errors: $($parseErrors.Message -join '; ')"
    }
    foreach ($functionName in @(
        'Resolve-FullPath',
        'ConvertTo-UnityFileUriPath',
        'Test-IsReparsePoint',
        'Write-CiNotice',
        'Write-JsonArtifact',
        'Assert-ExactJsonPropertyNames',
        'Assert-JsonValueType',
        'Get-ExpectedShippingShapeNames',
        'Assert-ExactJsonStringArray',
        'Assert-NoShippingTestAssemblies',
        'Test-ShippingAssemblyEvidence',
        'Test-ShippingBuildReport',
        'Test-ShippingStartupTimings',
        'Write-ShippingCellEvidence',
        'Assert-ShippingPlayerDirectoryManifest',
        'Test-ShippingPlayerManifestEvidence',
        'Write-ShippingPackageResolutionEvidence',
        'Test-ShippingFidelityResult'
    )) {
        $definition = $runnerAst.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $functionName
            },
            $true
        ) | Select-Object -First 1
        if (-not $definition) {
            throw "Function '$functionName' was not found in run-ci-tests.ps1."
        }
        Invoke-Expression $definition.Extent.Text
    }

    foreach ($profileCase in $profileCases) {
        $caseProfilePath = Join-Path $repoRoot ".github/perf/$($profileCase.FileName)"
        Assert-That "$($profileCase.ManagedStrippingLevel) shipping profile exists" (
            Test-Path -LiteralPath $caseProfilePath -PathType Leaf
        )
        $caseProfile = Get-Content -LiteralPath $caseProfilePath -Raw | ConvertFrom-Json
        Assert-That "$($profileCase.ManagedStrippingLevel) shipping profile has an exact identity" (
            $caseProfile.profileId -ceq $profileCase.ProfileId -and
            $caseProfile.configuration.managedStrippingLevel -ceq $profileCase.ManagedStrippingLevel -and
            -not [bool]$caseProfile.buildOptions.includeTestAssemblies
        )
        & $validatorPath -ProfilePath $caseProfilePath -ProfileOnly

        $mismatchedProfile = Copy-JsonValue -Value $caseProfile
        $mismatchedProfile.configuration.managedStrippingLevel = if (
            $profileCase.ManagedStrippingLevel -ceq 'High'
        ) {
            'Medium'
        } else {
            'High'
        }
        $mismatchedProfilePath = Join-Path $fixtureRoot (
            "mismatched-$($profileCase.ManagedStrippingLevel.ToLowerInvariant())-profile.json"
        )
        [System.IO.File]::WriteAllText(
            $mismatchedProfilePath,
            ($mismatchedProfile | ConvertTo-Json -Depth 10)
        )
        Assert-Fails "$($profileCase.ManagedStrippingLevel) profile rejects a different level" {
            & $validatorPath -ProfilePath $mismatchedProfilePath -ProfileOnly
        } 'differs'

        $caseName = $profileCase.ManagedStrippingLevel.ToLowerInvariant()
        $caseProjectPath = Join-Path $fixtureRoot "profile-$caseName-project"
        & $runnerPath `
            -UnityVersion '6000.3.16f1' `
            -TestMode shipping `
            -AssemblyNames '' `
            -ArtifactsPath (Join-Path $fixtureRoot "profile-$caseName-artifacts") `
            -RepoRoot $repoRoot `
            -ProjectPath $caseProjectPath `
            -CachePath (Join-Path $fixtureRoot "profile-$caseName-cache") `
            -CanonicalProfilePath $caseProfilePath `
            -GenerateOnly
        $caseConfiguratorText = Get-Content -LiteralPath (
            Join-Path $caseProjectPath 'Assets/Editor/DxmCiTestConfigurator.cs'
        ) -Raw
        $caseBuilderText = Get-Content -LiteralPath (
            Join-Path $caseProjectPath 'Assets/Editor/DxmShippingFidelityBuilder.cs'
        ) -Raw
        $expectedLevelCall = "ManagedStrippingLevel.$($profileCase.ManagedStrippingLevel));"
        Assert-That "$($profileCase.ManagedStrippingLevel) profile reaches generated configuration" (
            $caseConfiguratorText.Contains($expectedLevelCall) -and
            $caseBuilderText.Contains($expectedLevelCall) -and
            $caseBuilderText.Contains($profileCase.ProfileId) -and
            $caseConfiguratorText.Contains('PlayerSettings.stripEngineCode = false;')
        )
    }

    $builderSourceText = Get-Content -LiteralPath (
        Join-Path (Join-Path $fixtureRoot 'profile-high-project') 'Assets/Editor/DxmShippingFidelityBuilder.cs'
    ) -Raw
    $stopwatchStartIndex = $builderSourceText.IndexOf(
        'System.Diagnostics.Stopwatch buildStopwatch = System.Diagnostics.Stopwatch.StartNew();',
        [StringComparison]::Ordinal
    )
    $builderBuildPlayerIndex = $builderSourceText.IndexOf(
        'BuildPipeline.BuildPlayer(options)',
        [StringComparison]::Ordinal
    )
    $stopwatchStopIndex = $builderSourceText.IndexOf(
        'buildStopwatch.Stop();',
        [StringComparison]::Ordinal
    )
    $writeReportIndex = $builderSourceText.IndexOf(
        'WriteBuildReportEvidence(',
        [StringComparison]::Ordinal
    )
    $builderResultCheckIndex = $builderSourceText.IndexOf(
        'if (report.summary.result != BuildResult.Succeeded)',
        [StringComparison]::Ordinal
    )
    Assert-That 'shipping builder times only BuildPipeline and writes its report before the result check' (
        $stopwatchStartIndex -ge 0 -and
        $builderBuildPlayerIndex -gt $stopwatchStartIndex -and
        $stopwatchStopIndex -gt $builderBuildPlayerIndex -and
        $writeReportIndex -gt $stopwatchStopIndex -and
        $builderResultCheckIndex -gt $writeReportIndex -and
        $builderSourceText.Contains('RequireEnvironmentVariable("DXM_SHIPPING_BUILD_REPORT_PATH")') -and
        $builderSourceText.Contains('public string topologyId = "semantic-18-v1";') -and
        $builderSourceText.Contains('public int messageTypeCount = 18;') -and
        $builderSourceText.Contains('reportedTotalSizeBytes = (long)report.summary.totalSize') -and
        $builderSourceText.Contains('reportedTotalTimeMs = report.summary.totalTime.TotalMilliseconds')
    )
    Assert-That 'shipping runner passes the build report path and clears it afterwards' (
        [regex]::Matches(
            $runnerText,
            '\$env:DXM_SHIPPING_BUILD_REPORT_PATH = \$shippingBuildReportPath'
        ).Count -eq 1 -and
        [regex]::Matches($runnerText, "'DXM_SHIPPING_BUILD_REPORT_PATH'").Count -eq 2
    )

    foreach ($cardinality in @(1, 16, 256, 1000)) {
        $cardinalityProjectPath = Join-Path $fixtureRoot "cardinality-$cardinality-project"
        $cardinalityArtifactsPath = Join-Path $fixtureRoot "cardinality-$cardinality-artifacts"
        & $runnerPath `
            -UnityVersion '6000.3.16f1' `
            -TestMode shipping `
            -AssemblyNames '' `
            -ArtifactsPath $cardinalityArtifactsPath `
            -RepoRoot $repoRoot `
            -ProjectPath $cardinalityProjectPath `
            -CachePath (Join-Path $fixtureRoot "cardinality-$cardinality-cache") `
            -CanonicalProfilePath $profilePath `
            -ShippingTopology cardinality `
            -ShippingMessageTypeCount $cardinality `
            -GenerateOnly

        $cardinalitySourcePath = Join-Path (
            Join-Path $cardinalityProjectPath 'Assets'
        ) 'DxmShippingCardinalityTopology.cs'
        $cardinalitySource = Get-Content -LiteralPath $cardinalitySourcePath -Raw
        $expectedCardinalityShapes = @(
            Get-ExpectedShippingShapeNames `
                -Topology cardinality `
                -MessageTypeCount $cardinality
        )
        Assert-That "cardinality $cardinality emits the exact ordered inventory" (
            $expectedCardinalityShapes.Count -eq $cardinality -and
            $expectedCardinalityShapes[0] -ceq 'DxmShippingCardinalityMessage0001' -and
            $expectedCardinalityShapes[-1] -ceq ('DxmShippingCardinalityMessage{0:D4}' -f $cardinality)
        )
        Assert-That "cardinality $cardinality generates every closed message and exercise phase" (
            [regex]::Matches(
                $cardinalitySource,
                '\[DxUntargetedMessage\]\s+public readonly partial struct DxmShippingCardinalityMessage\d{4}'
            ).Count -eq $cardinality -and
            [regex]::Matches(
                $cardinalitySource,
                'token\.RegisterUntargeted<DxmShippingCardinalityMessage\d{4}>'
            ).Count -eq $cardinality -and
            [regex]::Matches(
                $cardinalitySource,
                'bus\.UntargetedBroadcast\(ref message\d{4}\);'
            ).Count -eq $cardinality -and
            [regex]::Matches(
                $cardinalitySource,
                'bus\.UntypedUntargetedBroadcast\(message\d{4}\);'
            ).Count -eq $cardinality -and
            [regex]::Matches(
                $cardinalitySource,
                'private static void HandleDxmShippingCardinalityMessage\d{4}'
            ).Count -eq $cardinality
        )
        $cardinalityCompilerOptions = @(
            Get-Content -LiteralPath (Join-Path $cardinalityProjectPath 'Assets/csc.rsp')
        )
        Assert-That "cardinality $cardinality selects only the cardinality topology" (
            $cardinalityCompilerOptions -ccontains '-define:DXM_SHIPPING_CARDINALITY_TOPOLOGY' -and
            $cardinalityCompilerOptions -cnotcontains '-define:DXM_SHIPPING_SEMANTIC_TOPOLOGY'
        )
        $cardinalityPlayerSource = Get-Content -LiteralPath (
            Join-Path $cardinalityProjectPath 'Assets/DxmShippingFidelityPlayer.cs'
        ) -Raw
        Assert-That "cardinality $cardinality binds its result evidence to the topology" (
            $cardinalityPlayerSource.Contains("public string topologyId = `"cardinality-$cardinality-v1`";") -and
            $cardinalityPlayerSource.Contains("public int messageTypeCount = $cardinality;")
        )
        Assert-That "cardinality $cardinality warms and times the first generated message" (
            $cardinalitySource.Contains('DxmShippingCardinalityMessage0001 firstMessage = default;') -and
            $cardinalitySource.Contains('bus.UntargetedBroadcast(ref firstMessage);') -and
            $cardinalitySource.Contains('for (int i = 0; i < WarmDispatchIterations; i++)') -and
            $cardinalitySource.Contains(
                'RecordWarmDispatch(timings, "DxmShippingCardinalityMessage0001", warmMicroseconds);'
            ) -and
            $cardinalitySource.Contains('timings.trimUs = ElapsedMicroseconds(phaseStart);') -and
            $cardinalitySource.Contains('timings.teardownUs = ElapsedMicroseconds(phaseStart);')
        )
        $cardinalityInputManifest = Get-Content -LiteralPath (
            Join-Path $cardinalityArtifactsPath 'shipping-project-inputs.json'
        ) -Raw | ConvertFrom-Json
        $cardinalityInputPaths = @($cardinalityInputManifest.files.path)
        Assert-That "cardinality $cardinality provenance includes the exact topology source" (
            [int]$cardinalityInputManifest.schemaVersion -eq 2 -and
            $cardinalityInputManifest.topologyId -ceq "cardinality-$cardinality-v1" -and
            $cardinalityInputManifest.topologyKind -ceq 'cardinality' -and
            [int]$cardinalityInputManifest.messageTypeCount -eq $cardinality -and
            (@($cardinalityInputManifest.expectedShapes) -join "`n") -ceq (
                $expectedCardinalityShapes -join "`n"
            ) -and
            $cardinalityInputPaths.Count -eq 8 -and
            $cardinalityInputPaths -ccontains 'Assets/DxmShippingCardinalityTopology.cs'
        )
    }

    Assert-Fails 'semantic topology rejects a cardinality count' {
        & $runnerPath `
            -UnityVersion '6000.3.16f1' `
            -TestMode shipping `
            -AssemblyNames '' `
            -ArtifactsPath (Join-Path $fixtureRoot 'bad-semantic-count-artifacts') `
            -RepoRoot $repoRoot `
            -ProjectPath (Join-Path $fixtureRoot 'bad-semantic-count-project') `
            -CachePath (Join-Path $fixtureRoot 'bad-semantic-count-cache') `
            -CanonicalProfilePath $profilePath `
            -ShippingTopology semantic `
            -ShippingMessageTypeCount 16 `
            -GenerateOnly
    } 'semantic topology with 18 message types'
    Assert-Fails 'cardinality topology rejects the semantic count' {
        & $runnerPath `
            -UnityVersion '6000.3.16f1' `
            -TestMode shipping `
            -AssemblyNames '' `
            -ArtifactsPath (Join-Path $fixtureRoot 'bad-cardinality-count-artifacts') `
            -RepoRoot $repoRoot `
            -ProjectPath (Join-Path $fixtureRoot 'bad-cardinality-count-project') `
            -CachePath (Join-Path $fixtureRoot 'bad-cardinality-count-cache') `
            -CanonicalProfilePath $profilePath `
            -ShippingTopology cardinality `
            -ShippingMessageTypeCount 18 `
            -GenerateOnly
    } 'cardinality topology with 1, 16, 256, or 1000 message types'

    $projectPath = Join-Path $fixtureRoot 'project'
    $artifactsPath = Join-Path $fixtureRoot 'artifacts'
    $cachePath = Join-Path $fixtureRoot 'cache'
    & $runnerPath `
        -UnityVersion '6000.3.16f1' `
        -TestMode shipping `
        -AssemblyNames '' `
        -ArtifactsPath $artifactsPath `
        -RepoRoot $repoRoot `
        -ProjectPath $projectPath `
        -CachePath $cachePath `
        -CanonicalProfilePath $profilePath `
        -GenerateOnly

    $manifest = Get-Content -LiteralPath (Join-Path $projectPath 'Packages/manifest.json') -Raw | ConvertFrom-Json
    $manifestPropertyNames = @($manifest.PSObject.Properties.Name)
    Assert-That 'shipping manifest contains only dependencies' (
        $manifestPropertyNames.Count -eq 1 -and $manifestPropertyNames[0] -ceq 'dependencies'
    )
    $dependencyNames = @($manifest.dependencies.PSObject.Properties.Name)
    Assert-That 'shipping manifest contains only the package under test' (
        $dependencyNames.Count -eq 1 -and $dependencyNames[0] -ceq 'com.wallstop-studios.dxmessaging'
    )
    Assert-That 'shipping manifest omits Unity Test Framework' (
        $manifest.dependencies.PSObject.Properties.Name -cnotcontains 'com.unity.test-framework'
    )

    $assetFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $projectPath 'Assets') -File -Recurse |
            Select-Object -ExpandProperty Name
    )
    Assert-That 'shipping project emits only compiler options, the configurator, builder, and player sources' (
        $assetFiles.Count -eq 4 -and
        $assetFiles -ccontains 'csc.rsp' -and
        $assetFiles -ccontains 'DxmCiTestConfigurator.cs' -and
        $assetFiles -ccontains 'DxmShippingFidelityBuilder.cs' -and
        $assetFiles -ccontains 'DxmShippingFidelityPlayer.cs'
    )
    Assert-That 'shipping project omits test callbacks and build modifiers' (
        $assetFiles -cnotcontains 'DxmCiStandaloneTestCallback.cs' -and
        $assetFiles -cnotcontains 'DxmCiStandaloneBuildModifier.cs'
    )
    $projectInputManifestPath = Join-Path $artifactsPath 'shipping-project-inputs.json'
    $projectInputManifest = Get-Content -LiteralPath $projectInputManifestPath -Raw | ConvertFrom-Json
    $projectInputPaths = @($projectInputManifest.files.path)
    $expectedShapeNames = @(
        Get-ExpectedShippingShapeNames -Topology semantic -MessageTypeCount 18
    )
    Assert-That 'shipping project-input evidence contains the exact seven generated inputs' (
        [int]$projectInputManifest.schemaVersion -eq 2 -and
        $projectInputManifest.topologyId -ceq 'semantic-18-v1' -and
        $projectInputManifest.topologyKind -ceq 'semantic' -and
        [int]$projectInputManifest.messageTypeCount -eq 18 -and
        (@($projectInputManifest.expectedShapes) -join "`n") -ceq ($expectedShapeNames -join "`n") -and
        $projectInputPaths.Count -eq 7 -and
        $projectInputPaths -ccontains 'Assets/csc.rsp' -and
        $projectInputPaths -ccontains 'Assets/DxmShippingFidelityPlayer.cs' -and
        $projectInputPaths -ccontains 'Assets/Editor/DxmCiTestConfigurator.cs' -and
        $projectInputPaths -ccontains 'Assets/Editor/DxmShippingFidelityBuilder.cs' -and
        $projectInputPaths -ccontains 'Packages/manifest.json' -and
        $projectInputPaths -ccontains 'ProjectSettings/EditorSettings.asset' -and
        $projectInputPaths -ccontains 'ProjectSettings/ProjectVersion.txt'
    )
    $staleInputPaths = @(
        'Assets/StaleShippingProbe.cs',
        'Assets/StaleShippingPlugin.dll',
        'Assets/link.xml',
        'Packages/packages-lock.json',
        'Packages/com.example.stale/package.json',
        'Packages/com.example.stale/Runtime/StalePackageProbe.cs',
        'ProjectSettings/StaleShippingSettings.asset',
        'UserSettings/StaleShippingPreferences.asset'
    )
    foreach ($staleInputRelativePath in $staleInputPaths) {
        $staleInputPath = Join-Path $projectPath $staleInputRelativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $staleInputPath) | Out-Null
        [System.IO.File]::WriteAllText($staleInputPath, 'stale shipping input')
    }
    & $runnerPath `
        -UnityVersion '6000.3.16f1' `
        -TestMode shipping `
        -AssemblyNames '' `
        -ArtifactsPath $artifactsPath `
        -RepoRoot $repoRoot `
        -ProjectPath $projectPath `
        -CachePath $cachePath `
        -CanonicalProfilePath $profilePath `
        -GenerateOnly
    foreach ($staleInputRelativePath in $staleInputPaths) {
        Assert-That "shipping project reuse removes $staleInputRelativePath" (
            -not (Test-Path -LiteralPath (Join-Path $projectPath $staleInputRelativePath))
        )
    }
    $packagesAfterReuse = @(Get-ChildItem -LiteralPath (Join-Path $projectPath 'Packages') -Force)
    Assert-That 'GenerateOnly leaves only the reviewed manifest under Packages' (
        $packagesAfterReuse.Count -eq 1 -and $packagesAfterReuse[0].Name -ceq 'manifest.json'
    )
    $directoryLinkType = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
        'Junction'
    } else {
        'SymbolicLink'
    }
    $externalRootTarget = Join-Path $fixtureRoot 'external-root-target'
    New-Item -ItemType Directory -Force -Path $externalRootTarget | Out-Null
    $externalRootSentinel = Join-Path $externalRootTarget 'root-sentinel.txt'
    [System.IO.File]::WriteAllText($externalRootSentinel, 'must survive root reparse cleanup')
    $assetsRoot = Join-Path $projectPath 'Assets'
    Remove-Item -LiteralPath $assetsRoot -Recurse -Force
    New-Item -ItemType $directoryLinkType -Path $assetsRoot -Target $externalRootTarget | Out-Null
    & $runnerPath `
        -UnityVersion '6000.3.16f1' `
        -TestMode shipping `
        -AssemblyNames '' `
        -ArtifactsPath $artifactsPath `
        -RepoRoot $repoRoot `
        -ProjectPath $projectPath `
        -CachePath $cachePath `
        -CanonicalProfilePath $profilePath `
        -GenerateOnly
    Assert-That 'shipping reuse unlinks an input-root reparse point without traversal' (
        (Test-Path -LiteralPath $externalRootSentinel -PathType Leaf) -and
        -not (Test-IsReparsePoint -Path $assetsRoot) -and
        (Test-Path -LiteralPath (Join-Path $assetsRoot 'DxmShippingFidelityPlayer.cs') -PathType Leaf)
    )

    $externalChildTarget = Join-Path $fixtureRoot 'external-child-target'
    New-Item -ItemType Directory -Force -Path $externalChildTarget | Out-Null
    $externalChildSentinel = Join-Path $externalChildTarget 'child-sentinel.txt'
    [System.IO.File]::WriteAllText($externalChildSentinel, 'must survive child reparse cleanup')
    $linkedChildPath = Join-Path $assetsRoot 'StaleLinkedInput'
    New-Item -ItemType $directoryLinkType -Path $linkedChildPath -Target $externalChildTarget | Out-Null
    & $runnerPath `
        -UnityVersion '6000.3.16f1' `
        -TestMode shipping `
        -AssemblyNames '' `
        -ArtifactsPath $artifactsPath `
        -RepoRoot $repoRoot `
        -ProjectPath $projectPath `
        -CachePath $cachePath `
        -CanonicalProfilePath $profilePath `
        -GenerateOnly
    Assert-That 'shipping reuse unlinks a child reparse point without traversal' (
        (Test-Path -LiteralPath $externalChildSentinel -PathType Leaf) -and
        -not (Test-Path -LiteralPath $linkedChildPath) -and
        (Test-Path -LiteralPath (Join-Path $assetsRoot 'DxmShippingFidelityPlayer.cs') -PathType Leaf)
    )

    $danglingRootTarget = Join-Path $fixtureRoot 'dangling-root-target'
    New-Item -ItemType Directory -Force -Path $danglingRootTarget | Out-Null
    Remove-Item -LiteralPath $assetsRoot -Recurse -Force
    New-Item -ItemType $directoryLinkType -Path $assetsRoot -Target $danglingRootTarget | Out-Null
    [System.IO.Directory]::Delete($danglingRootTarget, $true)
    & $runnerPath `
        -UnityVersion '6000.3.16f1' `
        -TestMode shipping `
        -AssemblyNames '' `
        -ArtifactsPath $artifactsPath `
        -RepoRoot $repoRoot `
        -ProjectPath $projectPath `
        -CachePath $cachePath `
        -CanonicalProfilePath $profilePath `
        -GenerateOnly
    Assert-That 'shipping reuse replaces a dangling input-root directory link' (
        -not (Test-IsReparsePoint -Path $assetsRoot) -and
        (Test-Path -LiteralPath (Join-Path $assetsRoot 'DxmShippingFidelityPlayer.cs') -PathType Leaf)
    )

    $danglingChildTarget = Join-Path $fixtureRoot 'dangling-child-target'
    New-Item -ItemType Directory -Force -Path $danglingChildTarget | Out-Null
    $danglingChildPath = Join-Path $assetsRoot 'DanglingLinkedInput'
    New-Item -ItemType $directoryLinkType -Path $danglingChildPath -Target $danglingChildTarget | Out-Null
    [System.IO.Directory]::Delete($danglingChildTarget, $true)
    & $runnerPath `
        -UnityVersion '6000.3.16f1' `
        -TestMode shipping `
        -AssemblyNames '' `
        -ArtifactsPath $artifactsPath `
        -RepoRoot $repoRoot `
        -ProjectPath $projectPath `
        -CachePath $cachePath `
        -CanonicalProfilePath $profilePath `
        -GenerateOnly
    Assert-That 'shipping reuse removes a dangling child directory link' (
        -not (Test-Path -LiteralPath $danglingChildPath) -and
        (Test-Path -LiteralPath (Join-Path $assetsRoot 'DxmShippingFidelityPlayer.cs') -PathType Leaf)
    )

    $manifest = Get-Content -LiteralPath (Join-Path $projectPath 'Packages/manifest.json') -Raw | ConvertFrom-Json
    $packageLockPath = Join-Path $projectPath 'Packages/packages-lock.json'
    $generatedManifestPath = Join-Path $projectPath 'Packages/manifest.json'
    $generatedManifestSha256 = (
        Get-FileHash -LiteralPath $generatedManifestPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $manifestDependencyValue = [string]$manifest.dependencies.'com.wallstop-studios.dxmessaging'
    $packageLock = [ordered]@{
        dependencies = [ordered]@{
            'com.wallstop-studios.dxmessaging' = [ordered]@{
                version = $manifestDependencyValue
                depth = 0
                source = 'local'
                dependencies = [ordered]@{}
            }
        }
    }
    $packageEvidenceArguments = @{
        ProjectPath = $projectPath
        ArtifactsPath = $artifactsPath
        ExpectedRepoRoot = $repoRoot
        ExpectedManifestSha256 = $generatedManifestSha256
    }
    [System.IO.File]::WriteAllText($packageLockPath, ($packageLock | ConvertTo-Json -Depth 10))
    Write-ShippingPackageResolutionEvidence @packageEvidenceArguments
    $resolvedPackageEvidence = Get-Content `
        -LiteralPath (Join-Path $artifactsPath 'shipping-resolved-package-inputs.json') `
        -Raw |
        ConvertFrom-Json
    Assert-That 'shipping resolution evidence hashes the exact manifest and generated lock' (
        @($resolvedPackageEvidence.files).Count -eq 2 -and
        @($resolvedPackageEvidence.files.path)[0] -ceq 'Packages/manifest.json' -and
        @($resolvedPackageEvidence.files.path)[1] -ceq 'Packages/packages-lock.json' -and
        $resolvedPackageEvidence.resolvedPackage.packageId -ceq 'com.wallstop-studios.dxmessaging' -and
        $resolvedPackageEvidence.resolvedPackage.source -ceq 'local' -and
        [int]$resolvedPackageEvidence.resolvedPackage.depth -eq 0 -and
        $resolvedPackageEvidence.resolvedPackage.versionScheme -ceq 'file'
    )
    $badPackageLock = Copy-JsonValue -Value $packageLock
    $badPackageLock.dependencies.'com.wallstop-studios.dxmessaging'.source = 'registry'
    [System.IO.File]::WriteAllText($packageLockPath, ($badPackageLock | ConvertTo-Json -Depth 10))
    Assert-Fails 'shipping package resolution rejects a non-local lock' {
        Write-ShippingPackageResolutionEvidence @packageEvidenceArguments
    } 'reviewed checkout'
    $badPackageLock = Copy-JsonValue -Value $packageLock
    $badPackageLock.dependencies.'com.wallstop-studios.dxmessaging'.version = 'file:/another/checkout'
    [System.IO.File]::WriteAllText($packageLockPath, ($badPackageLock | ConvertTo-Json -Depth 10))
    Assert-Fails 'shipping package resolution rejects another local checkout' {
        Write-ShippingPackageResolutionEvidence @packageEvidenceArguments
    } 'reviewed checkout'
    $badPackageLock = Copy-JsonValue -Value $packageLock
    $badPackageLock.dependencies.'com.wallstop-studios.dxmessaging'.depth = '0'
    [System.IO.File]::WriteAllText($packageLockPath, ($badPackageLock | ConvertTo-Json -Depth 10))
    Assert-Fails 'shipping package resolution rejects a mistyped lock depth' {
        Write-ShippingPackageResolutionEvidence @packageEvidenceArguments
    } 'must be a JSON integer'
    $badPackageLock = Copy-JsonValue -Value $packageLock
    $badPackageLock.dependencies | Add-Member `
        -NotePropertyName 'com.example.stale' `
        -NotePropertyValue ([pscustomobject]@{})
    [System.IO.File]::WriteAllText($packageLockPath, ($badPackageLock | ConvertTo-Json -Depth 10))
    Assert-Fails 'shipping package resolution rejects an extra lock dependency' {
        Write-ShippingPackageResolutionEvidence @packageEvidenceArguments
    } 'exact one-package graph'
    [System.IO.File]::WriteAllText($packageLockPath, ($packageLock | ConvertTo-Json -Depth 10))
    $staleEmbeddedPackagePath = Join-Path $projectPath 'Packages/com.example.stale/package.json'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $staleEmbeddedPackagePath) | Out-Null
    [System.IO.File]::WriteAllText($staleEmbeddedPackagePath, '{}')
    Assert-Fails 'shipping package resolution rejects an embedded package' {
        Write-ShippingPackageResolutionEvidence @packageEvidenceArguments
    } 'unexpected entry'
    Remove-Item -LiteralPath (Join-Path $projectPath 'Packages/com.example.stale') -Recurse -Force
    $mutatedManifest = Copy-JsonValue -Value $manifest
    $mutatedManifest.dependencies.'com.wallstop-studios.dxmessaging' = 'file:/another/checkout'
    [System.IO.File]::WriteAllText($generatedManifestPath, ($mutatedManifest | ConvertTo-Json -Depth 10))
    Assert-Fails 'shipping package resolution rejects post-resolution manifest drift' {
        Write-ShippingPackageResolutionEvidence @packageEvidenceArguments
    } 'manifest hash changed'
    [System.IO.File]::WriteAllText($generatedManifestPath, ($manifest | ConvertTo-Json -Depth 10))

    $configuratorText = Get-Content -LiteralPath (Join-Path $projectPath 'Assets/Editor/DxmCiTestConfigurator.cs') -Raw
    $builderText = Get-Content -LiteralPath (Join-Path $projectPath 'Assets/Editor/DxmShippingFidelityBuilder.cs') -Raw
    $playerText = Get-Content -LiteralPath (Join-Path $projectPath 'Assets/DxmShippingFidelityPlayer.cs') -Raw
    $runtimeSettingsText = Get-Content -LiteralPath (
        Join-Path $repoRoot 'Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs'
    ) -Raw
    $runtimeSettingsCreatorText = Get-Content -LiteralPath (
        Join-Path $repoRoot 'Editor/Settings/DxMessagingRuntimeSettingsCreator.cs'
    ) -Raw
    Assert-That 'runtime settings keep optional IMGUI calls in the editor assembly' (
        -not $runtimeSettingsText.Contains('EditorGUIUtility') -and
        $runtimeSettingsCreatorText.Contains('EditorGUIUtility.PingObject(asset);') -and
        $runtimeSettingsCreatorText.Contains(
            'Assets/Create/Wallstop Studios/DxMessaging/Runtime Settings (in Resources)'
        )
    )
    Assert-That 'shipping configuration defers engine stripping until the builder assembly has loaded' (
        $configuratorText.Contains('PlayerSettings.stripEngineCode = false;')
    )
    Assert-That 'shipping builder invokes BuildPipeline directly' (
        $builderText.Contains('BuildPipeline.BuildPlayer(options)')
    )
    $applyProfileIndex = $builderText.IndexOf('ApplyShippingProfile();', [StringComparison]::Ordinal)
    $prebuildEvidenceIndex = $builderText.IndexOf(
        'Environment.GetEnvironmentVariable("DXM_PREBUILD_CONFIG_PROFILE_PATH")',
        [StringComparison]::Ordinal
    )
    $buildPlayerIndex = $builderText.IndexOf(
        'BuildPipeline.BuildPlayer(options)',
        [StringComparison]::Ordinal
    )
    Assert-That 'shipping builder applies the complete reviewed profile before evidence and build' (
        $applyProfileIndex -ge 0 -and
        $prebuildEvidenceIndex -gt $applyProfileIndex -and
        $buildPlayerIndex -gt $prebuildEvidenceIndex -and
        $builderText.Contains('PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.IL2CPP);') -and
        $builderText.Contains('PlayerSettings.SetApiCompatibilityLevel(standalone, ApiCompatibilityLevel.NET_Standard);') -and
        $builderText.Contains('PlayerSettings.SetManagedStrippingLevel(standalone, ManagedStrippingLevel.High);') -and
        $builderText.Contains('Il2CppCompilerConfiguration.Release);') -and
        $builderText.Contains('PlayerSettings.gcIncremental = true;') -and
        $builderText.Contains('PlayerSettings.stripEngineCode = true;') -and
        $builderText.Contains('Il2CppCodeGeneration.OptimizeSpeed);')
    )
    Assert-That 'shipping builder excludes test assemblies and player connections' (
        $builderText.Contains('~BuildOptions.IncludeTestAssemblies') -and
        $builderText.Contains('~BuildOptions.AutoRunPlayer') -and
        $builderText.Contains('~BuildOptions.ConnectToHost') -and
        $builderText.Contains('~BuildOptions.ConnectWithProfiler')
    )
    $shapeCases = @(
        @{ Type = 'DxmShippingPublicUntargetedClass'; Variable = 'publicUntargetedClass'; Kind = 'Untargeted'; Handler = 'HandlePublicUntargetedClass' },
        @{ Type = 'DxmShippingPublicUntargetedStruct'; Variable = 'publicUntargetedStruct'; Kind = 'Untargeted'; Handler = 'HandlePublicUntargetedStruct' },
        @{ Type = 'DxmShippingPublicTargetedClass'; Variable = 'publicTargetedClass'; Kind = 'Targeted'; Handler = 'HandlePublicTargetedClass' },
        @{ Type = 'DxmShippingPublicTargetedStruct'; Variable = 'publicTargetedStruct'; Kind = 'Targeted'; Handler = 'HandlePublicTargetedStruct' },
        @{ Type = 'DxmShippingPublicBroadcastClass'; Variable = 'publicBroadcastClass'; Kind = 'Broadcast'; Handler = 'HandlePublicBroadcastClass' },
        @{ Type = 'DxmShippingPublicBroadcastStruct'; Variable = 'publicBroadcastStruct'; Kind = 'Broadcast'; Handler = 'HandlePublicBroadcastStruct' },
        @{ Type = 'NestedUntargetedClass'; Variable = 'nestedUntargetedClass'; Kind = 'Untargeted'; Handler = 'HandleNestedUntargetedClass' },
        @{ Type = 'NestedUntargetedStruct'; Variable = 'nestedUntargetedStruct'; Kind = 'Untargeted'; Handler = 'HandleNestedUntargetedStruct' },
        @{ Type = 'NestedTargetedClass'; Variable = 'nestedTargetedClass'; Kind = 'Targeted'; Handler = 'HandleNestedTargetedClass' },
        @{ Type = 'NestedTargetedStruct'; Variable = 'nestedTargetedStruct'; Kind = 'Targeted'; Handler = 'HandleNestedTargetedStruct' },
        @{ Type = 'NestedBroadcastClass'; Variable = 'nestedBroadcastClass'; Kind = 'Broadcast'; Handler = 'HandleNestedBroadcastClass' },
        @{ Type = 'NestedBroadcastStruct'; Variable = 'nestedBroadcastStruct'; Kind = 'Broadcast'; Handler = 'HandleNestedBroadcastStruct' },
        @{ Type = 'PublicNestedUntargetedClass'; Variable = 'publicNestedUntargetedClass'; Kind = 'Untargeted'; Handler = 'HandlePublicNestedUntargetedClass' },
        @{ Type = 'PublicNestedUntargetedStruct'; Variable = 'publicNestedUntargetedStruct'; Kind = 'Untargeted'; Handler = 'HandlePublicNestedUntargetedStruct' },
        @{ Type = 'PublicNestedTargetedClass'; Variable = 'publicNestedTargetedClass'; Kind = 'Targeted'; Handler = 'HandlePublicNestedTargetedClass' },
        @{ Type = 'PublicNestedTargetedStruct'; Variable = 'publicNestedTargetedStruct'; Kind = 'Targeted'; Handler = 'HandlePublicNestedTargetedStruct' },
        @{ Type = 'PublicNestedBroadcastClass'; Variable = 'publicNestedBroadcastClass'; Kind = 'Broadcast'; Handler = 'HandlePublicNestedBroadcastClass' },
        @{ Type = 'PublicNestedBroadcastStruct'; Variable = 'publicNestedBroadcastStruct'; Kind = 'Broadcast'; Handler = 'HandlePublicNestedBroadcastStruct' }
    )
    $rootStart = $playerText.IndexOf('List<string> rootedUntypedShapes', [StringComparison]::Ordinal)
    $rootEnd = $playerText.IndexOf('long rootedUntypedProbeCount', $rootStart, [StringComparison]::Ordinal)
    $firstTypedStart = $playerText.IndexOf('s_Phase = PhaseFirstTyped;', $rootEnd, [StringComparison]::Ordinal)
    $typedStart = $playerText.IndexOf('s_Phase = PhaseTyped;', $firstTypedStart, [StringComparison]::Ordinal)
    $typedEnd = $playerText.IndexOf('s_Phase = PhaseUntyped;', $typedStart, [StringComparison]::Ordinal)
    $untypedEnd = $playerText.IndexOf('result.typedDispatchCount', $typedEnd, [StringComparison]::Ordinal)
    Assert-That 'shipping player exposes ordered source segments for every dispatch phase' (
        $rootStart -ge 0 -and $rootEnd -gt $rootStart -and
        $firstTypedStart -gt $rootEnd -and $typedStart -gt $firstTypedStart -and
        $typedEnd -gt $typedStart -and $untypedEnd -gt $typedEnd
    )
    $rootSegment = $playerText.Substring($rootStart, $rootEnd - $rootStart)
    $firstTypedSegment = $playerText.Substring($firstTypedStart, $typedStart - $firstTypedStart)
    $typedSegment = $playerText.Substring($typedStart, $typedEnd - $typedStart)
    $untypedSegment = $playerText.Substring($typedEnd, $untypedEnd - $typedEnd)
    Assert-That 'shipping player measures exactly one first typed dispatch before the typed phase' (
        [regex]::Matches(
            $firstTypedSegment,
            [regex]::Escape('bus.UntargetedBroadcast(ref publicUntargetedClass);')
        ).Count -eq 1 -and
        $firstTypedSegment.Contains('timings.firstTypedDispatchUs = ElapsedMicroseconds(phaseStart);')
    )
    foreach ($shapeCase in $shapeCases) {
        $type = $shapeCase.Type
        $variable = $shapeCase.Variable
        $kind = $shapeCase.Kind
        $handler = $shapeCase.Handler
        $rootArguments = if ($kind -ceq 'Untargeted') {
            "bus, $variable, rootedUntypedShapes"
        } else {
            "bus, route, $variable, rootedUntypedShapes"
        }
        $typedInvocation = if ($kind -ceq 'Untargeted') {
            "bus.UntargetedBroadcast(ref $variable);"
        } elseif ($kind -ceq 'Targeted') {
            "bus.TargetedBroadcast(ref route, ref $variable);"
        } else {
            "bus.SourcedBroadcast(ref route, ref $variable);"
        }
        $untypedInvocation = if ($kind -ceq 'Untargeted') {
            "bus.UntypedUntargetedBroadcast($variable);"
        } elseif ($kind -ceq 'Targeted') {
            "bus.UntypedTargetedBroadcast(route, $variable);"
        } else {
            "bus.UntypedSourcedBroadcast(route, $variable);"
        }
        $registrationInvocation = if ($kind -ceq 'Untargeted') {
            "token.RegisterUntargeted<$type>($handler)"
        } else {
            "token.Register$kind<$type>(route, $handler)"
        }
        Assert-That "$type has the expected source-generator attribute" (
            [regex]::Matches(
                $playerText,
                "\[Dx$($kind)Message\]\s+(?:public|private)\s+(?:sealed\s+partial\s+class|readonly\s+partial\s+struct)\s+$type"
            ).Count -eq 1
        )
        Assert-That "$type is probed once before registration with its observed interface type" (
            [regex]::Matches(
                $rootSegment,
                [regex]::Escape("Probe$($kind)Root($rootArguments);")
            ).Count -eq 1
        )
        Assert-That "$type is registered with its distinct handler" (
            $playerText.Contains($registrationInvocation)
        )
        Assert-That "$type is dispatched once through its typed path" (
            [regex]::Matches($typedSegment, [regex]::Escape($typedInvocation)).Count -eq 1
        )
        Assert-That "$type is dispatched once through its post-registration untyped path" (
            [regex]::Matches($untypedSegment, [regex]::Escape($untypedInvocation)).Count -eq 1
        )
        Assert-That "$type handler records the observed concrete shape" (
            $playerText.Contains("$handler(in $type message) => Count(`"$type`");")
        )
    }
    $messageAttributeCount = [regex]::Matches(
        $playerText,
        '\[Dx(?:Untargeted|Targeted|Broadcast)Message\]'
    ).Count
    Assert-That 'shipping player covers all 18 visibility, nesting, kind, and representation shapes' (
        $messageAttributeCount -eq 18
    )
    Assert-That 'shipping player contains the private missing-root mutant' (
        $playerText.Contains('private sealed class MissingRootUntargetedMessage') -and
        $playerText.Contains('no rooted dispatch bridge was registered')
    )
    Assert-That 'shipping player checks the forbidden test define' (
        $playerText.Contains('#if UNITY_INCLUDE_TESTS') -and
        $playerText.Contains('result.unityIncludeTests = true;') -and
        $playerText.Contains('if (result.unityIncludeTests)') -and
        -not [regex]::IsMatch($playerText, 'result\.unityIncludeTests = true;\s*throw')
    )
    Assert-That 'shipping player serializes evidence without an optional Unity engine module' (
        -not $playerText.Contains('JsonUtility') -and
        $playerText.Contains('private static string QuoteJson(string value)') -and
        $playerText.Contains('private static string SerializeStringArray(string[] values)') -and
        $playerText.Contains('File.WriteAllText(path, json);')
    )
    Assert-That 'shipping player records every cold-start phase with Stopwatch' (
        $playerText.Contains('private const int WarmDispatchIterations = 1000000;') -and
        $playerText.Contains('timings.busConstructionUs = ElapsedMicroseconds(phaseStart);') -and
        $playerText.Contains('timings.rootProbePhaseUs = ElapsedMicroseconds(phaseStart);') -and
        $playerText.Contains('timings.registrationPhaseUs = ElapsedMicroseconds(phaseStart);') -and
        $playerText.Contains('timings.firstTypedDispatchUs = ElapsedMicroseconds(phaseStart);') -and
        $playerText.Contains('timings.typedPhaseUs = ElapsedMicroseconds(phaseStart);') -and
        $playerText.Contains('timings.untypedPhaseUs = ElapsedMicroseconds(phaseStart);') -and
        $playerText.Contains('timings.trimUs = ElapsedMicroseconds(phaseStart);') -and
        $playerText.Contains('timings.teardownUs = ElapsedMicroseconds(phaseStart);') -and
        $playerText.Contains('_ = bus.Trim(true);') -and
        $playerText.Contains('result.timings.engineStartToRunMs = engineStartToRunMs;')
    )
    Assert-That 'shipping player counts the first and warm dispatch phases separately' (
        $playerText.Contains('public int schemaVersion = 3;') -and
        $playerText.Contains('s_Phase = PhaseFirstTyped;') -and
        $playerText.Contains('s_Phase = PhaseWarm;') -and
        -not $playerText.Contains('s_UntypedPhase') -and
        $playerText.Contains('if (s_FirstTypedDispatchCount != 1 || s_TypedDispatchCount != 18') -and
        $playerText.Contains('throw new InvalidOperationException("Shipping timing values must be finite numbers.");')
    )

    $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
    $profileSha256 = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $assemblyEvidencePath = Join-Path $fixtureRoot 'shipping-assemblies.json'
    $assemblyEvidence = [ordered]@{
        schemaVersion = 1
        profileId = $profile.profileId
        profileSha256 = $profileSha256
        unityVersion = '6000.3.16f1'
        includeTestAssemblies = $false
        playerAssemblies = @('Assembly-CSharp', 'WallstopStudios.DxMessaging')
    }
    [System.IO.File]::WriteAllText($assemblyEvidencePath, ($assemblyEvidence | ConvertTo-Json -Depth 10))
    Test-ShippingAssemblyEvidence `
        -Path $assemblyEvidencePath `
        -ExpectedProfileId $profile.profileId `
        -ExpectedProfileSha256 $profileSha256 `
        -ExpectedUnityVersion '6000.3.16f1'
    foreach ($property in $assemblyEvidence.GetEnumerator()) {
        $mistypedEvidence = Copy-JsonValue -Value $assemblyEvidence
        $mistypedEvidence.($property.Key) = if ($property.Value -is [bool]) {
            $property.Value.ToString().ToLowerInvariant()
        } elseif ($property.Value -is [int] -or $property.Value -is [long]) {
            $property.Value.ToString()
        } elseif ($property.Value -is [string]) {
            $false
        } else {
            [string]$property.Value[0]
        }
        [System.IO.File]::WriteAllText($assemblyEvidencePath, ($mistypedEvidence | ConvertTo-Json -Depth 10))
        Assert-Fails "shipping assembly evidence $($property.Key) type" {
            Test-ShippingAssemblyEvidence `
                -Path $assemblyEvidencePath `
                -ExpectedProfileId $profile.profileId `
                -ExpectedProfileSha256 $profileSha256 `
                -ExpectedUnityVersion '6000.3.16f1'
        } 'must be a JSON'
    }
    foreach ($badAssemblyMutation in @(
        'include-tests',
        'loaded-test-assembly',
        'unexpected-player-assembly',
        'mistyped-assembly-name',
        'extra-property'
    )) {
        $mutatedEvidence = Copy-JsonValue -Value $assemblyEvidence
        if ($badAssemblyMutation -ceq 'include-tests') {
            $mutatedEvidence.includeTestAssemblies = $true
        } elseif ($badAssemblyMutation -ceq 'loaded-test-assembly') {
            $mutatedEvidence.playerAssemblies += 'nunit.framework'
        } elseif ($badAssemblyMutation -ceq 'unexpected-player-assembly') {
            $mutatedEvidence.playerAssemblies += 'StaleShippingPlugin'
        } elseif ($badAssemblyMutation -ceq 'mistyped-assembly-name') {
            $mutatedEvidence.playerAssemblies = @('Assembly-CSharp', 42)
        } else {
            $mutatedEvidence | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
        }
        [System.IO.File]::WriteAllText($assemblyEvidencePath, ($mutatedEvidence | ConvertTo-Json -Depth 10))
        $expectedAssemblyFailure = if ($badAssemblyMutation -ceq 'mistyped-assembly-name') {
            'must be a JSON'
        } else {
            'Shipping'
        }
        Assert-Fails "shipping assembly evidence $badAssemblyMutation" {
            Test-ShippingAssemblyEvidence `
                -Path $assemblyEvidencePath `
                -ExpectedProfileId $profile.profileId `
                -ExpectedProfileSha256 $profileSha256 `
                -ExpectedUnityVersion '6000.3.16f1'
        } $expectedAssemblyFailure
    }

    # One complete timing block shared by the cell-evidence and player-result
    # fixtures, so both exercise the full contract rather than a subset.
    $positiveTimings = [ordered]@{
        engineStartToRunMs = 210.5
        stopwatchFrequency = 10000000
        stopwatchIsHighResolution = $true
        busConstructionUs = 12.5
        rootProbePhaseUs = 800.25
        registrationPhaseUs = 320.75
        firstTypedDispatchUs = 45.5
        firstTypedDispatchCount = 1
        typedPhaseUs = 90.5
        untypedPhaseUs = 130.25
        warmDispatchShape = 'DxmShippingPublicUntargetedClass'
        warmDispatchCount = 1000000
        warmDispatchNsPerOp = 21.5
        trimUs = 60.5
        teardownUs = 40.25
    }

    $buildReportPath = Join-Path $fixtureRoot 'shipping-build-report.json'
    $buildReportStartedUtc = [datetime]::UtcNow
    $unixEpochUtc = [datetime]::new(1970, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
    $buildReportStartedUnixMs = [long](($buildReportStartedUtc.ToUniversalTime()) - $unixEpochUtc).TotalMilliseconds
    $buildReport = [ordered]@{
        schemaVersion = 1
        profileId = $profile.profileId
        profileSha256 = $profileSha256
        topologyId = 'semantic-18-v1'
        messageTypeCount = 18
        unityVersion = '6000.3.16f1'
        buildResult = 'Succeeded'
        buildStartedUnixMs = $buildReportStartedUnixMs + 2000
        buildEndedUnixMs = $buildReportStartedUnixMs + 65000
        buildDurationMs = 63000.5
        reportedTotalTimeMs = 62000.25
        reportedTotalSizeBytes = 33000000
        steps = @(
            [ordered]@{ name = 'Build player'; depth = 0; durationMs = 60000.5 },
            [ordered]@{ name = 'Compile scripts'; depth = 1; durationMs = 2000.25 }
        )
    }
    $buildReportArguments = @{
        Path = $buildReportPath
        ExpectedProfileId = $profile.profileId
        ExpectedProfileSha256 = $profileSha256
        ExpectedUnityVersion = '6000.3.16f1'
        ExpectedTopology = 'semantic'
        ExpectedMessageTypeCount = 18
        BuildStartedUtc = $buildReportStartedUtc
    }
    [System.IO.File]::WriteAllText($buildReportPath, ($buildReport | ConvertTo-Json -Depth 10))
    Test-ShippingBuildReport @buildReportArguments
    # An integral JSON duration must still validate: ConvertFrom-Json types 63000
    # as [long] and 63000.5 as [double], and both are numbers.
    $integralBuildReport = Copy-JsonValue -Value $buildReport
    $integralBuildReport.buildDurationMs = 63000
    $integralBuildReport.reportedTotalTimeMs = 0
    $integralBuildReport.steps[0].durationMs = 0
    [System.IO.File]::WriteAllText($buildReportPath, ($integralBuildReport | ConvertTo-Json -Depth 10))
    Test-ShippingBuildReport @buildReportArguments
    foreach ($buildReportMutation in @(
        'missing-property',
        'extra-property',
        'wrong-schema',
        'wrong-profile',
        'wrong-topology',
        'wrong-count',
        'wrong-unity',
        'failed-result',
        'start-type',
        'end-type',
        'stale-start',
        'inverted-range',
        'zero-duration',
        'negative-reported-time',
        'negative-size',
        'empty-steps',
        'step-not-object',
        'step-extra-property',
        'step-missing-property',
        'step-name-type',
        'step-depth-type',
        'step-duration-type',
        'negative-step-depth',
        'negative-step-duration',
        'duration-type',
        'size-type'
    )) {
        $mutatedBuildReport = Copy-JsonValue -Value $buildReport
        switch ($buildReportMutation) {
            'missing-property' { $mutatedBuildReport.PSObject.Properties.Remove('steps') }
            'extra-property' {
                $mutatedBuildReport | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
            }
            'wrong-schema' { $mutatedBuildReport.schemaVersion = 2 }
            'wrong-profile' { $mutatedBuildReport.profileId = 'canonical-il2cpp-verdict-player-v1' }
            'wrong-topology' { $mutatedBuildReport.topologyId = 'cardinality-18-v1' }
            'wrong-count' { $mutatedBuildReport.messageTypeCount = 16 }
            'wrong-unity' { $mutatedBuildReport.unityVersion = '2021.3.45f1' }
            'failed-result' { $mutatedBuildReport.buildResult = 'Failed' }
            'start-type' { $mutatedBuildReport.buildStartedUnixMs = '1756000000000' }
            'end-type' { $mutatedBuildReport.buildEndedUnixMs = 1.5 }
            'stale-start' {
                $mutatedBuildReport.buildStartedUnixMs = $buildReportStartedUnixMs - 600000
            }
            'inverted-range' {
                $mutatedBuildReport.buildEndedUnixMs = $buildReportStartedUnixMs + 1000
            }
            'zero-duration' { $mutatedBuildReport.buildDurationMs = 0 }
            'negative-reported-time' { $mutatedBuildReport.reportedTotalTimeMs = -1.0 }
            'negative-size' { $mutatedBuildReport.reportedTotalSizeBytes = -1 }
            'empty-steps' { $mutatedBuildReport.steps = @() }
            # Two elements keep this an array on both PowerShell hosts, so the
            # per-step object check is what fails rather than the array check.
            'step-not-object' { $mutatedBuildReport.steps = @('Build player', 'Compile scripts') }
            'step-extra-property' {
                $mutatedBuildReport.steps[0] |
                    Add-Member -NotePropertyName unexpected -NotePropertyValue $true
            }
            'step-missing-property' { $mutatedBuildReport.steps[0].PSObject.Properties.Remove('depth') }
            'step-name-type' { $mutatedBuildReport.steps[0].name = $false }
            'step-depth-type' { $mutatedBuildReport.steps[0].depth = '0' }
            'step-duration-type' { $mutatedBuildReport.steps[0].durationMs = '60000.5' }
            'negative-step-depth' { $mutatedBuildReport.steps[0].depth = -1 }
            'negative-step-duration' { $mutatedBuildReport.steps[0].durationMs = -1.0 }
            'duration-type' { $mutatedBuildReport.buildDurationMs = '63000.5' }
            default { $mutatedBuildReport.reportedTotalSizeBytes = '33000000' }
        }
        $mutatedBuildReportJson = $mutatedBuildReport | ConvertTo-Json -Depth 10
        if ($buildReportMutation -ceq 'empty-steps') {
            # Windows PowerShell 5.1 and PowerShell 7 disagree on how an empty
            # array property round-trips, so write the empty array literally.
            # Step objects contain no ']', making the character-class match exact.
            $mutatedBuildReportJson = $mutatedBuildReportJson -replace '"steps":\s*\[[^\]]*\]', '"steps": []'
        }
        [System.IO.File]::WriteAllText($buildReportPath, $mutatedBuildReportJson)
        # Type mutations report the JSON path; every other mutation reports the
        # named contract. Both categories must fail closed.
        $expectedBuildReportFailure = if ($buildReportMutation -clike '*-type') {
            'must be a JSON'
        } else {
            'Shipping build report'
        }
        Assert-Fails "shipping build report $buildReportMutation" {
            Test-ShippingBuildReport @buildReportArguments
        } $expectedBuildReportFailure
    }
    Remove-Item -LiteralPath $buildReportPath -Force
    Assert-Fails 'shipping build report must exist' {
        Test-ShippingBuildReport @buildReportArguments
    } 'did not write a build report'
    [System.IO.File]::WriteAllText($buildReportPath, ($buildReport | ConvertTo-Json -Depth 10))

    $cellEvidencePath = Join-Path $fixtureRoot 'shipping-cell-evidence.json'
    $cellPositiveResultPath = Join-Path $fixtureRoot 'cell-positive-result.json'
    $cellPlayerManifest = [ordered]@{
        schemaVersion = 1
        fileCount = 3
        files = @(
            [ordered]@{ path = 'DxmShippingPlayer.exe'; length = 640000; sha256 = ('A' * 64) },
            [ordered]@{ path = 'GameAssembly.dll'; length = 22000000; sha256 = ('B' * 64) },
            [ordered]@{
                path = 'DxmShippingPlayer_Data/globalgamemanagers'
                length = 2048
                sha256 = ('C' * 64)
            }
        )
    }
    $cellEvidenceArguments = @{
        Path = $cellEvidencePath
        BuildReportPath = $buildReportPath
        PositiveResultPath = $cellPositiveResultPath
        PlayerDirectoryManifest = $cellPlayerManifest
        ProfileId = $profile.profileId
        ProfileSha256 = $profileSha256
        ManagedStrippingLevel = 'High'
        Topology = 'semantic'
        MessageTypeCount = 18
        UnityVersion = '6000.3.16f1'
        LibraryState = 'cold'
        EditorBuildWallClockMs = 90000.0
        PositivePlayerWallClockMs = 1500.0
        MutantPlayerWallClockMs = 900.0
    }
    [System.IO.File]::WriteAllText(
        $cellPositiveResultPath,
        ([ordered]@{ timings = $positiveTimings } | ConvertTo-Json -Depth 10)
    )
    Write-ShippingCellEvidence @cellEvidenceArguments
    $cellEvidence = Get-Content -LiteralPath $cellEvidencePath -Raw | ConvertFrom-Json
    Assert-That 'shipping cell evidence joins build, size, and cold-start observations' (
        [int]$cellEvidence.schemaVersion -eq 1 -and
        $cellEvidence.profileId -ceq $profile.profileId -and
        $cellEvidence.managedStrippingLevel -ceq 'High' -and
        $cellEvidence.topologyId -ceq 'semantic-18-v1' -and
        [int]$cellEvidence.messageTypeCount -eq 18 -and
        $cellEvidence.libraryState -ceq 'cold' -and
        [double]$cellEvidence.editorBuildWallClockMs -eq 90000.0 -and
        [double]$cellEvidence.buildDurationMs -eq 63000.5 -and
        [double]$cellEvidence.reportedTotalTimeMs -eq 62000.25 -and
        [long]$cellEvidence.reportedTotalSizeBytes -eq 33000000 -and
        [int]$cellEvidence.buildStepCount -eq 2 -and
        [int]$cellEvidence.playerFileCount -eq 3 -and
        [long]$cellEvidence.playerTotalBytes -eq 22642048 -and
        [long]$cellEvidence.playerExecutableBytes -eq 640000 -and
        [long]$cellEvidence.gameAssemblyBytes -eq 22000000 -and
        [double]$cellEvidence.positivePlayerWallClockMs -eq 1500.0 -and
        [double]$cellEvidence.mutantPlayerWallClockMs -eq 900.0 -and
        [double]$cellEvidence.timings.warmDispatchNsPerOp -eq 21.5
    )
    # The matrix wrapper declares the cell contract independently of the runner
    # that writes it. Compare the wrapper's declared names with what the real
    # writer just produced so the SYNC note cannot rot into a silent mismatch.
    $matrixAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $matrixRunnerPath,
        [ref]$tokens,
        [ref]$parseErrors
    )
    if (@($parseErrors).Count -gt 0) {
        throw "run-shipping-fidelity-matrix.ps1 has parse errors: $($parseErrors.Message -join '; ')"
    }
    foreach ($contractVariableName in @('cellEvidencePropertyNames', 'cellTimingPropertyNames')) {
        $assignment = $matrixAst.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left.Extent.Text -ceq "`$$contractVariableName"
            },
            $true
        ) | Select-Object -First 1
        if (-not $assignment) {
            throw "Variable '$contractVariableName' was not found in run-shipping-fidelity-matrix.ps1."
        }
        Invoke-Expression $assignment.Extent.Text
    }
    $writtenCellNames = [string[]]@($cellEvidence.PSObject.Properties.Name)
    $declaredCellNames = [string[]]@($cellEvidencePropertyNames)
    [Array]::Sort($writtenCellNames, [System.StringComparer]::Ordinal)
    [Array]::Sort($declaredCellNames, [System.StringComparer]::Ordinal)
    $writtenTimingNames = [string[]]@($cellEvidence.timings.PSObject.Properties.Name)
    $declaredTimingNames = [string[]]@($cellTimingPropertyNames)
    [Array]::Sort($writtenTimingNames, [System.StringComparer]::Ordinal)
    [Array]::Sort($declaredTimingNames, [System.StringComparer]::Ordinal)
    Assert-That 'shipping matrix declares exactly the cell contract the runner writes' (
        ($writtenCellNames -join "`n") -ceq ($declaredCellNames -join "`n")
    )
    Assert-That 'shipping matrix declares exactly the timing contract the player writes' (
        ($writtenTimingNames -join "`n") -ceq ($declaredTimingNames -join "`n")
    )

    foreach ($missingPlayerFile in @('DxmShippingPlayer.exe', 'GameAssembly.dll')) {
        $incompleteManifest = [ordered]@{
            schemaVersion = 1
            fileCount = 2
            files = @($cellPlayerManifest['files'] | Where-Object { $_['path'] -cne $missingPlayerFile })
        }
        $incompleteArguments = $cellEvidenceArguments.Clone()
        $incompleteArguments.PlayerDirectoryManifest = $incompleteManifest
        Assert-Fails "shipping cell evidence rejects a player without $missingPlayerFile" {
            Write-ShippingCellEvidence @incompleteArguments
        } 'must contain DxmShippingPlayer.exe and the IL2CPP GameAssembly.dll'
    }

    $playerManifestPath = Join-Path $fixtureRoot 'shipping-player-manifest.json'
    $hashA = 'A'.PadRight(64, 'A')
    $hashB = 'B'.PadRight(64, 'B')
    $playerDirectoryManifest = [ordered]@{
        schemaVersion = 1
        fileCount = 2
        files = @(
            [ordered]@{ path = 'DxmShippingPlayer.exe'; length = 1024; sha256 = $hashA },
            [ordered]@{ path = 'DxmShippingPlayer_Data/globalgamemanagers'; length = 2048; sha256 = $hashB }
        )
    }
    $playerManifest = [ordered]@{
        schemaVersion = 2
        topologyId = 'semantic-18-v1'
        messageTypeCount = 18
        playerDirectoryManifestMatches = $true
        playerDirectoryManifestBefore = $playerDirectoryManifest
        playerDirectoryManifestAfter = Copy-JsonValue -Value $playerDirectoryManifest
        runs = @('positive', 'missing-root-mutant')
    }
    [System.IO.File]::WriteAllText($playerManifestPath, ($playerManifest | ConvertTo-Json -Depth 10))
    Test-ShippingPlayerManifestEvidence `
        -Path $playerManifestPath `
        -ExpectedTopology semantic `
        -ExpectedMessageTypeCount 18
    $oneFileDirectoryManifest = [ordered]@{
        schemaVersion = 1
        fileCount = 1
        files = @(
            [ordered]@{ path = 'DxmShippingPlayer.exe'; length = 1024; sha256 = $hashA }
        )
    }
    $oneFilePlayerManifest = Copy-JsonValue -Value $playerManifest
    $oneFilePlayerManifest.playerDirectoryManifestBefore = $oneFileDirectoryManifest
    $oneFilePlayerManifest.playerDirectoryManifestAfter = Copy-JsonValue -Value $oneFileDirectoryManifest
    [System.IO.File]::WriteAllText(
        $playerManifestPath,
        ($oneFilePlayerManifest | ConvertTo-Json -Depth 10)
    )
    Test-ShippingPlayerManifestEvidence `
        -Path $playerManifestPath `
        -ExpectedTopology semantic `
        -ExpectedMessageTypeCount 18
    foreach ($property in $playerManifest.GetEnumerator()) {
        $mistypedManifest = Copy-JsonValue -Value $playerManifest
        $mistypedManifest.($property.Key) = if ($property.Value -is [bool]) {
            $property.Value.ToString().ToLowerInvariant()
        } elseif ($property.Value -is [int] -or $property.Value -is [long]) {
            $property.Value.ToString()
        } elseif ($property.Value -is [string]) {
            $false
        } elseif ($property.Key -ceq 'runs') {
            'positive'
        } else {
            $false
        }
        [System.IO.File]::WriteAllText(
            $playerManifestPath,
            ($mistypedManifest | ConvertTo-Json -Depth 10)
        )
        Assert-Fails "shipping player manifest $($property.Key) type" {
            Test-ShippingPlayerManifestEvidence `
                -Path $playerManifestPath `
                -ExpectedTopology semantic `
                -ExpectedMessageTypeCount 18
        } 'must be a JSON'
    }
    foreach ($manifestMutation in @(
        'missing-property',
        'extra-property',
        'wrong-topology',
        'wrong-count',
        'false-match',
        'changed-after-manifest',
        'wrong-runs'
    )) {
        $mutatedManifest = Copy-JsonValue -Value $playerManifest
        if ($manifestMutation -ceq 'missing-property') {
            $mutatedManifest.PSObject.Properties.Remove('runs')
        } elseif ($manifestMutation -ceq 'extra-property') {
            $mutatedManifest | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
        } elseif ($manifestMutation -ceq 'wrong-topology') {
            $mutatedManifest.topologyId = 'cardinality-18-v1'
        } elseif ($manifestMutation -ceq 'wrong-count') {
            $mutatedManifest.messageTypeCount = 16
        } elseif ($manifestMutation -ceq 'false-match') {
            $mutatedManifest.playerDirectoryManifestMatches = $false
        } elseif ($manifestMutation -ceq 'changed-after-manifest') {
            $mutatedManifest.playerDirectoryManifestAfter.files[0].sha256 = $hashB
        } else {
            $mutatedManifest.runs = @('missing-root-mutant', 'positive')
        }
        [System.IO.File]::WriteAllText(
            $playerManifestPath,
            ($mutatedManifest | ConvertTo-Json -Depth 10)
        )
        Assert-Fails "shipping player manifest $manifestMutation" {
            Test-ShippingPlayerManifestEvidence `
                -Path $playerManifestPath `
                -ExpectedTopology semantic `
                -ExpectedMessageTypeCount 18
        } 'Shipping player manifest'
    }
    foreach ($nestedMutation in @(
        'missing-property',
        'extra-property',
        'wrong-schema',
        'wrong-file-count',
        'empty-files',
        'file-not-object',
        'file-extra-property',
        'path-type',
        'length-type',
        'hash-type',
        'blank-path',
        'negative-length',
        'malformed-hash',
        'duplicate-path',
        'unsorted-path'
    )) {
        $mutatedManifest = Copy-JsonValue -Value $playerManifest
        $nestedManifest = $mutatedManifest.playerDirectoryManifestBefore
        if ($nestedMutation -ceq 'missing-property') {
            $nestedManifest.PSObject.Properties.Remove('files')
        } elseif ($nestedMutation -ceq 'extra-property') {
            $nestedManifest | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
        } elseif ($nestedMutation -ceq 'wrong-schema') {
            $nestedManifest.schemaVersion = 2
        } elseif ($nestedMutation -ceq 'wrong-file-count') {
            $nestedManifest.fileCount = 1
        } elseif ($nestedMutation -ceq 'empty-files') {
            $nestedManifest.fileCount = 0
            $nestedManifest.files = @()
        } elseif ($nestedMutation -ceq 'file-not-object') {
            $nestedManifest.files = @('DxmShippingPlayer.exe')
            $nestedManifest.fileCount = 1
        } elseif ($nestedMutation -ceq 'file-extra-property') {
            $nestedManifest.files[0] | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
        } elseif ($nestedMutation -ceq 'path-type') {
            $nestedManifest.files[0].path = $false
        } elseif ($nestedMutation -ceq 'length-type') {
            $nestedManifest.files[0].length = '1024'
        } elseif ($nestedMutation -ceq 'hash-type') {
            $nestedManifest.files[0].sha256 = $false
        } elseif ($nestedMutation -ceq 'blank-path') {
            $nestedManifest.files[0].path = ''
        } elseif ($nestedMutation -ceq 'negative-length') {
            $nestedManifest.files[0].length = -1
        } elseif ($nestedMutation -ceq 'malformed-hash') {
            $nestedManifest.files[0].sha256 = 'not-a-sha256'
        } elseif ($nestedMutation -ceq 'duplicate-path') {
            $nestedManifest.files[1].path = $nestedManifest.files[0].path
        } else {
            $firstFile = $nestedManifest.files[0]
            $nestedManifest.files[0] = $nestedManifest.files[1]
            $nestedManifest.files[1] = $firstFile
        }
        [System.IO.File]::WriteAllText(
            $playerManifestPath,
            ($mutatedManifest | ConvertTo-Json -Depth 10)
        )
        Assert-Fails "shipping player nested manifest $nestedMutation" {
            Test-ShippingPlayerManifestEvidence `
                -Path $playerManifestPath `
                -ExpectedTopology semantic `
                -ExpectedMessageTypeCount 18
        } 'shippingPlayerManifest.playerDirectoryManifestBefore'
    }

    $resultPath = Join-Path $fixtureRoot 'shipping-result.json'
    $positiveResult = [ordered]@{
        schemaVersion = 3
        profileId = $profile.profileId
        profileSha256 = $profileSha256
        topologyId = 'semantic-18-v1'
        messageTypeCount = 18
        unityVersion = '6000.3.16f1'
        mode = 'positive'
        success = $true
        unityIncludeTests = $false
        rootedUntypedProbeCount = 18
        typedDispatchCount = 18
        untypedDispatchCount = 18
        rootedUntypedShapes = @($expectedShapeNames)
        typedDispatchShapes = @($expectedShapeNames)
        untypedDispatchShapes = @($expectedShapeNames)
        missingRootFailureObserved = $false
        failureType = ''
        failureMessage = ''
        loadedAssemblies = @('Assembly-CSharp', 'WallstopStudios.DxMessaging')
        timings = $positiveTimings
    }
    [System.IO.File]::WriteAllText($resultPath, ($positiveResult | ConvertTo-Json -Depth 10))
    Test-ShippingFidelityResult `
        -Path $resultPath `
        -ExpectedMode positive `
        -ExpectedProfileId $profile.profileId `
        -ExpectedProfileSha256 $profileSha256 `
        -ExpectedUnityVersion '6000.3.16f1' `
        -ExpectedTopology semantic `
        -ExpectedMessageTypeCount 18 `
        -ExpectedWarmDispatchCount 1000000

    foreach ($cardinality in @(1, 16, 256, 1000)) {
        $cardinalityResult = Copy-JsonValue -Value $positiveResult
        $cardinalityShapeNames = @(
            Get-ExpectedShippingShapeNames -Topology cardinality -MessageTypeCount $cardinality
        )
        $cardinalityResult.topologyId = "cardinality-$cardinality-v1"
        $cardinalityResult.messageTypeCount = $cardinality
        $cardinalityResult.rootedUntypedProbeCount = $cardinality
        $cardinalityResult.typedDispatchCount = $cardinality
        $cardinalityResult.untypedDispatchCount = $cardinality
        $cardinalityResult.rootedUntypedShapes = @($cardinalityShapeNames)
        $cardinalityResult.typedDispatchShapes = @($cardinalityShapeNames)
        $cardinalityResult.untypedDispatchShapes = @($cardinalityShapeNames)
        $cardinalityResult.timings.warmDispatchShape = $cardinalityShapeNames[0]
        [System.IO.File]::WriteAllText($resultPath, ($cardinalityResult | ConvertTo-Json -Depth 10))
        Test-ShippingFidelityResult `
            -Path $resultPath `
            -ExpectedMode positive `
            -ExpectedProfileId $profile.profileId `
            -ExpectedProfileSha256 $profileSha256 `
            -ExpectedUnityVersion '6000.3.16f1' `
            -ExpectedTopology cardinality `
            -ExpectedMessageTypeCount $cardinality `
            -ExpectedWarmDispatchCount 1000000
    }

    foreach ($property in $positiveResult.GetEnumerator()) {
        $mistypedResult = Copy-JsonValue -Value $positiveResult
        $mistypedResult.($property.Key) = if ($property.Value -is [bool]) {
            $property.Value.ToString().ToLowerInvariant()
        } elseif ($property.Value -is [int] -or $property.Value -is [long]) {
            $property.Value.ToString()
        } elseif ($property.Value -is [string]) {
            $false
        } elseif ($property.Key -ceq 'timings') {
            'not-an-object'
        } else {
            [string]$property.Value[0]
        }
        [System.IO.File]::WriteAllText($resultPath, ($mistypedResult | ConvertTo-Json -Depth 10))
        $expectedMistypeFailure = if ($property.Key -ceq 'timings') {
            'must be a JSON object'
        } else {
            'must be a JSON'
        }
        Assert-Fails "shipping result $($property.Key) type" {
            Test-ShippingFidelityResult `
                -Path $resultPath `
                -ExpectedMode positive `
                -ExpectedProfileId $profile.profileId `
                -ExpectedProfileSha256 $profileSha256 `
                -ExpectedUnityVersion '6000.3.16f1' `
                -ExpectedTopology semantic `
                -ExpectedMessageTypeCount 18 `
                -ExpectedWarmDispatchCount 1000000
        } $expectedMistypeFailure
    }
    foreach ($badResultMutation in @(
        'failed',
        'missing-root-probe-count',
        'wrong-count',
        'duplicate-rooted-shape',
        'duplicate-typed-shape',
        'missing-untyped-shape',
        'loaded-test-assembly',
        'mistyped-assembly-name',
        'extra-property'
    )) {
        $mutatedResult = Copy-JsonValue -Value $positiveResult
        if ($badResultMutation -ceq 'failed') {
            $mutatedResult.success = $false
            $mutatedResult.failureType = 'System.InvalidOperationException'
        } elseif ($badResultMutation -ceq 'missing-root-probe-count') {
            $mutatedResult.rootedUntypedProbeCount = 0
        } elseif ($badResultMutation -ceq 'wrong-count') {
            $mutatedResult.untypedDispatchCount = 5
        } elseif ($badResultMutation -ceq 'duplicate-rooted-shape') {
            $mutatedResult.rootedUntypedShapes[1] = $mutatedResult.rootedUntypedShapes[0]
        } elseif ($badResultMutation -ceq 'duplicate-typed-shape') {
            $mutatedResult.typedDispatchShapes[1] = $mutatedResult.typedDispatchShapes[0]
        } elseif ($badResultMutation -ceq 'missing-untyped-shape') {
            $mutatedResult.untypedDispatchShapes = @($mutatedResult.untypedDispatchShapes)[0..16]
        } elseif ($badResultMutation -ceq 'loaded-test-assembly') {
            $mutatedResult.loadedAssemblies += 'UnityEngine.TestRunner'
        } elseif ($badResultMutation -ceq 'mistyped-assembly-name') {
            $mutatedResult.loadedAssemblies = @('Assembly-CSharp', 42)
        } else {
            $mutatedResult | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
        }
        [System.IO.File]::WriteAllText($resultPath, ($mutatedResult | ConvertTo-Json -Depth 10))
        $expectedResultFailure = if ($badResultMutation -ceq 'mistyped-assembly-name') {
            'must be a JSON'
        } elseif ($badResultMutation -clike '*shape') {
            'shape inventory'
        } else {
            'Shipping'
        }
        Assert-Fails "shipping result $badResultMutation" {
            Test-ShippingFidelityResult `
                -Path $resultPath `
                -ExpectedMode positive `
                -ExpectedProfileId $profile.profileId `
                -ExpectedProfileSha256 $profileSha256 `
                -ExpectedUnityVersion '6000.3.16f1' `
                -ExpectedTopology semantic `
                -ExpectedMessageTypeCount 18 `
                -ExpectedWarmDispatchCount 1000000
        } $expectedResultFailure
    }
    $mutantResult = Copy-JsonValue -Value $positiveResult
    $mutantResult.mode = 'missing-root-mutant'
    $mutantResult.rootedUntypedProbeCount = 0
    $mutantResult.typedDispatchCount = 0
    $mutantResult.untypedDispatchCount = 0
    $mutantResult.rootedUntypedShapes = @()
    $mutantResult.typedDispatchShapes = @()
    $mutantResult.untypedDispatchShapes = @()
    $mutantResult.missingRootFailureObserved = $true
    foreach ($idleTimingProperty in @(
        'busConstructionUs',
        'rootProbePhaseUs',
        'registrationPhaseUs',
        'firstTypedDispatchUs',
        'typedPhaseUs',
        'untypedPhaseUs',
        'warmDispatchNsPerOp',
        'trimUs',
        'teardownUs'
    )) {
        $mutantResult.timings.$idleTimingProperty = 0
    }
    $mutantResult.timings.firstTypedDispatchCount = 0
    $mutantResult.timings.warmDispatchCount = 0
    $mutantResult.timings.warmDispatchShape = ''
    [System.IO.File]::WriteAllText($resultPath, ($mutantResult | ConvertTo-Json -Depth 10))
    Test-ShippingFidelityResult `
        -Path $resultPath `
        -ExpectedMode missing-root-mutant `
        -ExpectedProfileId $profile.profileId `
        -ExpectedProfileSha256 $profileSha256 `
        -ExpectedUnityVersion '6000.3.16f1' `
        -ExpectedTopology semantic `
        -ExpectedMessageTypeCount 18 `
        -ExpectedWarmDispatchCount 1000000

    # A mutant that quietly performed a dispatch phase, or a positive run whose
    # warm loop, first-dispatch count, or warm shape drifted, must fail closed.
    $busyMutant = Copy-JsonValue -Value $mutantResult
    $busyMutant.timings.registrationPhaseUs = 5.5
    [System.IO.File]::WriteAllText($resultPath, ($busyMutant | ConvertTo-Json -Depth 10))
    Assert-Fails 'shipping missing-root mutant timings must stay idle' {
        Test-ShippingFidelityResult `
            -Path $resultPath `
            -ExpectedMode missing-root-mutant `
            -ExpectedProfileId $profile.profileId `
            -ExpectedProfileSha256 $profileSha256 `
            -ExpectedUnityVersion '6000.3.16f1' `
            -ExpectedTopology semantic `
            -ExpectedMessageTypeCount 18 `
            -ExpectedWarmDispatchCount 1000000
    } 'must stay idle for the missing-root mutant'

    foreach ($timingMutation in @(
        'missing-property',
        'extra-property',
        'negative-phase',
        'zero-engine-start',
        'zero-frequency',
        'wrong-first-dispatch-count',
        'wrong-warm-count',
        'zero-warm-rate',
        'wrong-warm-shape',
        'phase-type',
        'count-type',
        'shape-type',
        'resolution-type'
    )) {
        $mutatedTimingResult = Copy-JsonValue -Value $positiveResult
        $mutatedTimings = $mutatedTimingResult.timings
        if ($timingMutation -ceq 'missing-property') {
            $mutatedTimings.PSObject.Properties.Remove('trimUs')
        } elseif ($timingMutation -ceq 'extra-property') {
            $mutatedTimings | Add-Member -NotePropertyName unexpected -NotePropertyValue 1.0
        } elseif ($timingMutation -ceq 'negative-phase') {
            $mutatedTimings.registrationPhaseUs = -1.0
        } elseif ($timingMutation -ceq 'zero-engine-start') {
            $mutatedTimings.engineStartToRunMs = 0
        } elseif ($timingMutation -ceq 'zero-frequency') {
            $mutatedTimings.stopwatchFrequency = 0
        } elseif ($timingMutation -ceq 'wrong-first-dispatch-count') {
            $mutatedTimings.firstTypedDispatchCount = 2
        } elseif ($timingMutation -ceq 'wrong-warm-count') {
            $mutatedTimings.warmDispatchCount = 999999
        } elseif ($timingMutation -ceq 'zero-warm-rate') {
            $mutatedTimings.warmDispatchNsPerOp = 0
        } elseif ($timingMutation -ceq 'wrong-warm-shape') {
            $mutatedTimings.warmDispatchShape = 'DxmShippingPublicUntargetedStruct'
        } elseif ($timingMutation -ceq 'phase-type') {
            $mutatedTimings.registrationPhaseUs = '320.75'
        } elseif ($timingMutation -ceq 'count-type') {
            $mutatedTimings.warmDispatchCount = '1000000'
        } elseif ($timingMutation -ceq 'shape-type') {
            $mutatedTimings.warmDispatchShape = $false
        } else {
            $mutatedTimings.stopwatchIsHighResolution = 'true'
        }
        [System.IO.File]::WriteAllText($resultPath, ($mutatedTimingResult | ConvertTo-Json -Depth 10))
        Assert-Fails "shipping result timings $timingMutation" {
            Test-ShippingFidelityResult `
                -Path $resultPath `
                -ExpectedMode positive `
                -ExpectedProfileId $profile.profileId `
                -ExpectedProfileSha256 $profileSha256 `
                -ExpectedUnityVersion '6000.3.16f1' `
                -ExpectedTopology semantic `
                -ExpectedMessageTypeCount 18 `
                -ExpectedWarmDispatchCount 1000000
        } 'shippingResult.timings'
    }

    & $validatorPath -ProfilePath $profilePath -ProfileOnly
    foreach ($mutation in @(
        @{ Path = 'managedStrippingLevel'; Value = 'Disabled' },
        @{ Path = 'managedStrippingLevel'; Value = 'Medium' },
        @{ Path = 'includeTestAssemblies'; Value = $true }
    )) {
        $mutatedProfile = Copy-JsonValue -Value $profile
        if ($mutation.Path -ceq 'managedStrippingLevel') {
            $mutatedProfile.configuration.managedStrippingLevel = $mutation.Value
        } else {
            $mutatedProfile.buildOptions.includeTestAssemblies = $mutation.Value
        }
        $mutatedPath = Join-Path $fixtureRoot "bad-$($mutation.Path).json"
        [System.IO.File]::WriteAllText($mutatedPath, ($mutatedProfile | ConvertTo-Json -Depth 10))
        Assert-Fails "shipping profile $($mutation.Path) mutation" {
            & $validatorPath -ProfilePath $mutatedPath -ProfileOnly
        } 'differs'
    }

    Assert-Fails 'shipping assembly names must be empty' {
        & $runnerPath `
            -UnityVersion '6000.3.16f1' `
            -TestMode shipping `
            -AssemblyNames 'WallstopStudios.DxMessaging.Tests' `
            -ArtifactsPath (Join-Path $fixtureRoot 'bad-artifacts') `
            -RepoRoot $repoRoot `
            -ProjectPath (Join-Path $fixtureRoot 'bad-project') `
            -CachePath (Join-Path $fixtureRoot 'bad-cache') `
            -CanonicalProfilePath $profilePath `
            -GenerateOnly
    } 'AssemblyNames must be empty'

    Write-Host 'Shipping-fidelity harness contract tests passed.'
} finally {
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
