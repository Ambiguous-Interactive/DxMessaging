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
        'Write-JsonArtifact',
        'Assert-ExactJsonPropertyNames',
        'Assert-JsonValueType',
        'Get-ExpectedShippingShapeNames',
        'Assert-ExactJsonStringArray',
        'Assert-NoShippingTestAssemblies',
        'Test-ShippingAssemblyEvidence',
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
    $typedStart = $playerText.IndexOf('s_UntypedPhase = false;', $rootEnd, [StringComparison]::Ordinal)
    $typedEnd = $playerText.IndexOf('s_UntypedPhase = true;', $typedStart, [StringComparison]::Ordinal)
    $untypedEnd = $playerText.IndexOf('result.typedDispatchCount', $typedEnd, [StringComparison]::Ordinal)
    Assert-That 'shipping player exposes ordered source segments for every dispatch phase' (
        $rootStart -ge 0 -and $rootEnd -gt $rootStart -and
        $typedStart -gt $rootEnd -and $typedEnd -gt $typedStart -and $untypedEnd -gt $typedEnd
    )
    $rootSegment = $playerText.Substring($rootStart, $rootEnd - $rootStart)
    $typedSegment = $playerText.Substring($typedStart, $typedEnd - $typedStart)
    $untypedSegment = $playerText.Substring($typedEnd, $untypedEnd - $typedEnd)
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
        schemaVersion = 2
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
    }
    [System.IO.File]::WriteAllText($resultPath, ($positiveResult | ConvertTo-Json -Depth 10))
    Test-ShippingFidelityResult `
        -Path $resultPath `
        -ExpectedMode positive `
        -ExpectedProfileId $profile.profileId `
        -ExpectedProfileSha256 $profileSha256 `
        -ExpectedUnityVersion '6000.3.16f1' `
        -ExpectedTopology semantic `
        -ExpectedMessageTypeCount 18

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
        [System.IO.File]::WriteAllText($resultPath, ($cardinalityResult | ConvertTo-Json -Depth 10))
        Test-ShippingFidelityResult `
            -Path $resultPath `
            -ExpectedMode positive `
            -ExpectedProfileId $profile.profileId `
            -ExpectedProfileSha256 $profileSha256 `
            -ExpectedUnityVersion '6000.3.16f1' `
            -ExpectedTopology cardinality `
            -ExpectedMessageTypeCount $cardinality
    }

    foreach ($property in $positiveResult.GetEnumerator()) {
        $mistypedResult = Copy-JsonValue -Value $positiveResult
        $mistypedResult.($property.Key) = if ($property.Value -is [bool]) {
            $property.Value.ToString().ToLowerInvariant()
        } elseif ($property.Value -is [int] -or $property.Value -is [long]) {
            $property.Value.ToString()
        } elseif ($property.Value -is [string]) {
            $false
        } else {
            [string]$property.Value[0]
        }
        [System.IO.File]::WriteAllText($resultPath, ($mistypedResult | ConvertTo-Json -Depth 10))
        Assert-Fails "shipping result $($property.Key) type" {
            Test-ShippingFidelityResult `
                -Path $resultPath `
                -ExpectedMode positive `
                -ExpectedProfileId $profile.profileId `
                -ExpectedProfileSha256 $profileSha256 `
                -ExpectedUnityVersion '6000.3.16f1' `
                -ExpectedTopology semantic `
                -ExpectedMessageTypeCount 18
        } 'must be a JSON'
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
                -ExpectedMessageTypeCount 18
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
    [System.IO.File]::WriteAllText($resultPath, ($mutantResult | ConvertTo-Json -Depth 10))
    Test-ShippingFidelityResult `
        -Path $resultPath `
        -ExpectedMode missing-root-mutant `
        -ExpectedProfileId $profile.profileId `
        -ExpectedProfileSha256 $profileSha256 `
        -ExpectedUnityVersion '6000.3.16f1' `
        -ExpectedTopology semantic `
        -ExpectedMessageTypeCount 18

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
