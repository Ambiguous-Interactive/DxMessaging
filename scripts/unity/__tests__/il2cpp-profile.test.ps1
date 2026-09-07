#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$validatorPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'validate-il2cpp-profile.ps1'
$runnerPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'run-ci-tests.ps1'
$workflowPath = Join-Path $repoRoot '.github/workflows/perf-numbers.yml'
$testUnityVersion = '6000.3.16f1'
$sourceProfilePath = Join-Path $repoRoot '.github/perf/canonical-il2cpp-profile.v1.json'
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dxm-il2cpp-profile-{0}" -f [guid]::NewGuid().ToString('N'))

function Write-TestJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 10))
}

function Copy-JsonValue {
    param([Parameter(Mandatory = $true)]$Value)
    return $Value | ConvertTo-Json -Depth 10 | ConvertFrom-Json
}

function Assert-Fails {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [string]$ExpectedMessage
    )

    $failed = $false
    try {
        & $Action
    } catch {
        $failed = [string]::IsNullOrWhiteSpace($ExpectedMessage) -or
            $_.Exception.Message.Contains($ExpectedMessage)
    }
    if (-not $failed) {
        throw "Expected failure: $Description"
    }
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

try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $profilePath = Join-Path $fixtureRoot 'profile.json'
    Copy-Item -LiteralPath $sourceProfilePath -Destination $profilePath
    $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
    $profileSha256 = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash.ToLowerInvariant()

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
    foreach ($name in @(
        'Get-ComparisonSourceEvidence',
        'Get-StandalonePlayerManifest',
        'Write-JsonArtifact',
        'New-ConfiguratorSource',
        'New-StandaloneBuildModifierSource',
        'New-StandaloneTestCallbackSource'
    )) {
        $definition = $runnerAst.FindAll(
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

    $generatedSources = @(
        New-ConfiguratorSource -CanonicalProfileId $profile.profileId -CanonicalProfileSha256 $profileSha256
        New-StandaloneTestCallbackSource -CanonicalProfileId $profile.profileId -CanonicalProfileSha256 $profileSha256
    )
    foreach ($source in $generatedSources) {
        Assert-That 'generated C# embeds the profile ID' ($source.Contains($profile.profileId))
        Assert-That 'generated C# embeds the profile SHA-256' ($source.Contains($profileSha256))
    }
    $buildModifierSource = New-StandaloneBuildModifierSource -CanonicalProfileId $profile.profileId
    $applyStart = $generatedSources[0].IndexOf('public static void Apply()')
    $compilerPreparation = $generatedSources[0].IndexOf('DxMessaging.Editor.SetupCscRsp.PrepareCompilerInputs();', $applyStart)
    $compilationChange = $generatedSources[0].IndexOf('CompilationPipeline.codeOptimization =', $applyStart)
    $completionMarker = $generatedSources[0].IndexOf('File.WriteAllText(markerPath,', $applyStart)
    Assert-That 'configuration prepares compiler inputs before changing compilation settings and writing its success marker' (
        $compilerPreparation -gt $applyStart -and $compilerPreparation -lt $compilationChange -and
        $compilationChange -lt $completionMarker
    )
    Assert-That 'the configurator pins OptimizeSpeed' (
        $generatedSources[0].Contains('Il2CppCodeGeneration.OptimizeSpeed')
    )
    Assert-That 'configure and build observe Unity registered package resolution' (
        $generatedSources[0].Contains('PackageInfo.GetAllRegisteredPackages()') -and
        $buildModifierSource.Contains('DxmCiTestConfigurator.WriteComparisonPackageResolution();')
    )
    Assert-That 'the build evidence reads final BuildReport options' (
        $buildModifierSource.Contains('report.summary.options')
    )
    Assert-That 'the build process records prebuild and postbuild configuration' (
        $buildModifierSource.Contains('DXM_PREBUILD_CONFIG_PROFILE_PATH') -and
        $buildModifierSource.Contains('DXM_POSTBUILD_CONFIG_PROFILE_PATH')
    )
    Assert-That 'the build evidence uses Unity ForceEnableAssertions flag' (
        $generatedSources[0].Contains('BuildOptions.ForceEnableAssertions') -and
        -not $generatedSources[0].Contains('BuildOptions.EnableAssertions')
    )
    Assert-That 'the player records Debug.isDebugBuild' (
        $generatedSources[1].Contains('Debug.isDebugBuild')
    )

    $runnerText = Get-Content -LiteralPath $runnerPath -Raw
    $workflowText = Get-Content -LiteralPath $workflowPath -Raw
    Assert-That 'the runner archives the exact profile file' (
        $runnerText.Contains('Copy-Item -LiteralPath $resolvedCanonicalProfilePath')
    )
    foreach ($kind in @('configuration', 'buildOptions', 'runtime')) {
        Assert-That "the runner validates $kind evidence" ($runnerText.Contains("-EvidenceKind $kind"))
    }
    $evidenceCommands = @($runnerAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.CommandElements[0].Extent.Text -eq '$profileValidatorPath' -and
            @($node.CommandElements | Where-Object {
                $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                    $_.ParameterName -eq 'EvidenceKind'
            }).Count -gt 0
    }, $true))
    Assert-That 'the runner contains profile evidence validation calls' ($evidenceCommands.Count -gt 0)
    foreach ($command in $evidenceCommands) {
        $elements = @($command.CommandElements)
        $versionArguments = @(for ($index = 1; $index -lt $elements.Count - 1; ++$index) {
            if ($elements[$index] -is [System.Management.Automation.Language.CommandParameterAst] -and
                $elements[$index].ParameterName -eq 'ExpectedUnityVersion') {
                $elements[$index + 1].Extent.Text
            }
        })
        Assert-That "profile evidence validation at line $($command.Extent.StartLineNumber) binds the requested editor" (
            $versionArguments.Count -eq 1 -and $versionArguments[0] -ceq '$UnityVersion'
        )
    }
    Assert-That 'the performance standalone leg passes the canonical profile' (
        $workflowText.Contains("CanonicalProfilePath = '.github/perf/canonical-il2cpp-profile.v1.json'")
    )
    Assert-That 'canonical profile changes invalidate historical benchmark comparison' (
        $workflowText.Contains("|\.github/perf/canonical-il2cpp-profile\.v1\.json`$'")
    )
    Assert-That 'profile validator changes invalidate historical benchmark comparison' (
        $workflowText.Contains('|validate-il2cpp-profile)')
    )

    & $validatorPath -ProfilePath $profilePath -ProfileOnly -ExpectedSha256 $profileSha256

    $badProfilePath = Join-Path $fixtureRoot 'bad-profile.json'
    foreach ($semanticMutation in @(
        @{ Group = 'configuration'; Property = 'buildTarget'; Value = 'StandaloneLinux64' },
        @{ Group = 'configuration'; Property = 'il2cppCodeGeneration'; Value = 'OptimizeSize' },
        @{ Group = 'buildOptions'; Property = 'developmentBuild'; Value = $true },
        @{ Group = 'runtime'; Property = 'debugBuild'; Value = $true }
    )) {
        $mutatedProfile = Copy-JsonValue -Value $profile
        $mutatedProfile.($semanticMutation.Group).($semanticMutation.Property) = $semanticMutation.Value
        Write-TestJson -Path $badProfilePath -Value $mutatedProfile
        Assert-Fails "$($semanticMutation.Group).$($semanticMutation.Property) fixed profile value" {
            & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
        }
    }
    foreach ($kind in @('configuration', 'buildOptions', 'runtime')) {
        foreach ($property in $profile.$kind.PSObject.Properties) {
            $wrongTypeProfile = Copy-JsonValue -Value $profile
            $wrongTypeProfile.$kind.($property.Name) = if ($property.Value -is [bool]) {
                $property.Value.ToString().ToLowerInvariant()
            } else {
                $false
            }
            Write-TestJson -Path $badProfilePath -Value $wrongTypeProfile
            Assert-Fails "$kind.$($property.Name) profile type" {
                & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
            }

            $missingProfileValue = Copy-JsonValue -Value $profile
            $missingProfileValue.$kind.PSObject.Properties.Remove($property.Name)
            Write-TestJson -Path $badProfilePath -Value $missingProfileValue
            Assert-Fails "$kind.$($property.Name) missing from profile" {
                & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
            }
        }
    }

    foreach ($kind in @('configuration', 'buildOptions', 'runtime')) {
        $evidencePath = Join-Path $fixtureRoot "$kind.json"
        $evidence = [ordered]@{
            schemaVersion = $profile.schemaVersion
            profileId = $profile.profileId
            profileSha256 = $profileSha256
            evidenceKind = $kind
            unityVersion = $testUnityVersion
            values = Copy-JsonValue -Value $profile.$kind
        }
        Write-TestJson -Path $evidencePath -Value $evidence
        & $validatorPath `
            -ProfilePath $profilePath `
            -EvidencePath $evidencePath `
            -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion `
            -ExpectedSha256 $profileSha256

        foreach ($wrongVersion in @('2021.3.45f1', '6000.3.16F1', '6000.3.16f1 ')) {
            $wrongVersionEvidence = Copy-JsonValue -Value $evidence
            $wrongVersionEvidence.unityVersion = $wrongVersion
            Write-TestJson -Path $evidencePath -Value $wrongVersionEvidence
            Assert-Fails "$kind evidence from a different editor ($wrongVersion)" -ExpectedMessage "$kind.unityVersion differs" {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion
            }
        }
        Write-TestJson -Path $evidencePath -Value $evidence
        foreach ($missingExpectedVersion in @($null, '', ' ')) {
            Assert-Fails "$kind missing expected editor version" -ExpectedMessage 'ExpectedUnityVersion is required' {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $missingExpectedVersion
            }
        }

        foreach ($property in $profile.$kind.PSObject.Properties) {
            $mutated = Copy-JsonValue -Value $evidence
            $mutated.values.($property.Name) = if ($property.Value -is [bool]) {
                -not $property.Value
            } else {
                "$($property.Value)-drift"
            }
            Write-TestJson -Path $evidencePath -Value $mutated
            Assert-Fails "$kind.$($property.Name) drift" {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion
            }
        }

        $missing = Copy-JsonValue -Value $evidence
        $firstPropertyName = @($profile.$kind.PSObject.Properties)[0].Name
        $missing.values.PSObject.Properties.Remove($firstPropertyName)
        Write-TestJson -Path $evidencePath -Value $missing
        Assert-Fails "$kind missing value" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion
        }

        $extra = Copy-JsonValue -Value $evidence
        $extra.values | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
        Write-TestJson -Path $evidencePath -Value $extra
        Assert-Fails "$kind extra value" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion
        }
    }

    $runtimeEvidencePath = Join-Path $fixtureRoot 'runtime.json'
    $runtimeEvidence = [ordered]@{
        schemaVersion = 1
        profileId = $profile.profileId
        profileSha256 = $profileSha256
        evidenceKind = 'runtime'
        unityVersion = $testUnityVersion
        values = [ordered]@{ debugBuild = $false }
    }
    $runtimeEvidence.unexpected = $true
    Write-TestJson -Path $runtimeEvidencePath -Value $runtimeEvidence
    Assert-Fails 'extra evidence property' {
        & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
    }

    $runtimeEvidence.Remove('unexpected')
    $runtimeEvidence.schemaVersion = '1'
    Write-TestJson -Path $runtimeEvidencePath -Value $runtimeEvidence
    Assert-Fails 'string evidence schema version' {
        & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
    }

    $runtimeEvidence.schemaVersion = 1
    foreach ($metadataField in @('profileId', 'profileSha256', 'evidenceKind', 'unityVersion')) {
        $missingMetadata = Copy-JsonValue -Value $runtimeEvidence
        $missingMetadata.PSObject.Properties.Remove($metadataField)
        Write-TestJson -Path $runtimeEvidencePath -Value $missingMetadata
        Assert-Fails "missing evidence $metadataField" -ExpectedMessage "missing=$metadataField" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
        }
    }
    $wrongMetadataValues = [ordered]@{
        profileId = 'different-profile-v1'
        profileSha256 = '0' * 64
        evidenceKind = 'configuration'
        unityVersion = $false
    }
    foreach ($metadataField in $wrongMetadataValues.Keys) {
        $wrongMetadata = Copy-JsonValue -Value $runtimeEvidence
        $wrongMetadata.schemaVersion = 1
        $wrongMetadata.$metadataField = $wrongMetadataValues[$metadataField]
        Write-TestJson -Path $runtimeEvidencePath -Value $wrongMetadata
        Assert-Fails "wrong evidence $metadataField" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
        }
    }
    Assert-Fails 'missing evidence file' -ExpectedMessage 'does not exist' {
        & $validatorPath `
            -ProfilePath $profilePath `
            -EvidencePath (Join-Path $fixtureRoot 'missing-evidence.json') `
            -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
    }

    Assert-Fails 'wrong expected profile hash' {
        & $validatorPath -ProfilePath $profilePath -ProfileOnly -ExpectedSha256 ('0' * 64)
    }

    foreach ($topLevelField in @('schemaVersion', 'profileId', 'configuration', 'buildOptions', 'runtime')) {
        $missingTopLevel = Copy-JsonValue -Value $profile
        $missingTopLevel.PSObject.Properties.Remove($topLevelField)
        Write-TestJson -Path $badProfilePath -Value $missingTopLevel
        Assert-Fails "missing profile $topLevelField" {
            & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
        }
    }

    $badProfile = Copy-JsonValue -Value $profile
    $badProfile.configuration | Add-Member -NotePropertyName unexpected -NotePropertyValue 'value'
    Write-TestJson -Path $badProfilePath -Value $badProfile
    Assert-Fails 'extra canonical profile property' {
        & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
    }

    $badProfile = Copy-JsonValue -Value $profile
    $badProfile.profileId = 'unsupported-il2cpp-profile-v1'
    Write-TestJson -Path $badProfilePath -Value $badProfile
    Assert-Fails 'unsupported profile ID lists every accepted profile' -ExpectedMessage (
        "Supported profileIds: 'canonical-il2cpp-verdict-player-v1', " +
        "'shipping-fidelity-il2cpp-minimal-player-v1', " +
        "'shipping-fidelity-il2cpp-low-player-v1', " +
        "'shipping-fidelity-il2cpp-medium-player-v1', " +
        "'shipping-fidelity-il2cpp-player-v1'."
    ) {
        & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
    }

    $badProfile = Copy-JsonValue -Value $profile
    $badProfile.schemaVersion = '1'
    Write-TestJson -Path $badProfilePath -Value $badProfile
    Assert-Fails 'string canonical profile schema version' {
        & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
    }

    [System.IO.File]::WriteAllText($badProfilePath, '{')
    Assert-Fails 'invalid canonical profile JSON' {
        & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
    }

    $sourceFixture = Join-Path $fixtureRoot 'comparison-sources'
    $packageFixture = Join-Path $fixtureRoot 'resolved-messagepipe'
    foreach ($directory in @('scripts/unity', '.github', 'Runtime')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $sourceFixture $directory) | Out-Null
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $packageFixture 'Runtime') | Out-Null
    $resolvedPackagesPath = Join-Path $fixtureRoot 'resolved-packages.json'
    $ledgerFixturePath = Join-Path $sourceFixture 'scripts/unity/comparison-semantic-ledger-v1.json'
    $catalogFixturePath = Join-Path $sourceFixture 'scripts/unity/comparison-evidence-catalog-v1.json'
    $packageSourcePath = Join-Path $packageFixture 'Runtime/Broker.cs'
    $repositorySourcePath = Join-Path $sourceFixture 'Runtime/Contract.cs'
    $contractBytes = [System.Text.Encoding]::UTF8.GetBytes("source contract`n")
    [System.IO.File]::WriteAllBytes($packageSourcePath, $contractBytes)
    $contractSha256 = (Get-FileHash -LiteralPath $packageSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($repositorySourcePath, "source contract`r`n")
    [System.IO.File]::WriteAllText((Join-Path $sourceFixture 'scripts/unity/comparison-contract-v1.schema.json'), '{}')
    Write-TestJson -Path (Join-Path $packageFixture 'package.json') -Value @{ name = 'com.cysharp.messagepipe'; version = '1.8.2' }
    Write-TestJson -Path (Join-Path $sourceFixture '.github/comparison-packages.json') -Value @{ packages = @{ 'com.cysharp.messagepipe' = '1.8.2' } }
    Write-TestJson -Path $ledgerFixturePath -Value @{ identity = @{ messagePipe = @{ version = '1.8.2' }; byteDomain = 'test bytes' } }
    $catalogFixture = @{ sources = @{
        dxToken = @{ origin = 'repository'; path = 'Runtime/Contract.cs'; sha256 = $contractSha256 }
        package = @{ origin = 'messagepipe-unity'; packagePath = 'Runtime/Broker.cs'; sha256 = $contractSha256 }
    } }
    Write-TestJson -Path $catalogFixturePath -Value $catalogFixture
    $resolvedFixture = @{ packages = @(@{ name = 'com.cysharp.messagepipe'; version = '1.8.2'; resolvedPath = $packageFixture }) }
    Write-TestJson -Path $resolvedPackagesPath -Value $resolvedFixture
    $sourceEvidence = Get-ComparisonSourceEvidence -RepoRoot $sourceFixture -ResolvedPackagesPath $resolvedPackagesPath
    Assert-That 'actual resolved package bytes and portable repository CRLF hash pass' ($sourceEvidence.sources.Count -eq 2)
    foreach ($expected in @(
        @('dxToken', 'Runtime/Contract.cs', $repositorySourcePath),
        @('package', 'Runtime/Broker.cs', $packageSourcePath)
    )) {
        $sourceRecords = @($sourceEvidence.sources | Where-Object { $_.sourceRef -ceq $expected[0] })
        Assert-That "source reference $($expected[0]) survives exactly once" ($sourceRecords.Count -eq 1)
        Assert-That "source reference $($expected[0]) retains path and both byte domains" (
            $sourceRecords[0].path -ceq $expected[1] -and
            $sourceRecords[0].sha256 -ceq $contractSha256 -and
            $sourceRecords[0].compilerInputSha256 -ceq (Get-FileHash -LiteralPath $expected[2] -Algorithm SHA256).Hash.ToLowerInvariant()
        )
    }
    Assert-That 'source evidence binds its schema and catalog' ($sourceEvidence.schemaSha256.Length -eq 64 -and $sourceEvidence.catalogSha256.Length -eq 64)
    $artifactFixture = Join-Path $fixtureRoot 'comparison-artifacts'
    $playerFixture = Join-Path $fixtureRoot 'player/DxmTestPlayer_Data/il2cpp_data/Metadata'
    New-Item -ItemType Directory -Force -Path $artifactFixture, $playerFixture | Out-Null
    $playerExecutable = Join-Path $fixtureRoot 'player/DxmTestPlayer.exe'
    # These files are hashed by the real manifest producer, never executed.
    foreach ($file in @($playerExecutable, (Join-Path $fixtureRoot 'player/GameAssembly.dll'), (Join-Path $playerFixture 'global-metadata.dat'))) {
        [System.IO.File]::WriteAllBytes($file, $contractBytes)
    }
    $sourceEvidence['playerDirectoryManifest'] = Get-StandalonePlayerManifest -ExecutablePath $playerExecutable
    $resultFixturePath = Join-Path $artifactFixture 'results.xml'
    $logFixturePath = Join-Path $artifactFixture 'player.log'
    [System.IO.File]::WriteAllText($resultFixturePath, '<test-run total="1" passed="1" failed="0" skipped="0"><test-case name="comparison" result="Passed" /></test-run>')
    [System.IO.File]::WriteAllText($logFixturePath, "Comparison fixture completed.`n")
    $sourceEvidence['runs'] = @(@{
        runIndex = 1
        unredactedResultsSha256 = (Get-FileHash -LiteralPath $resultFixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
        unredactedPlayerLogSha256 = (Get-FileHash -LiteralPath $logFixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    })
    $sourceEvidencePath = Join-Path $artifactFixture 'comparison-source-evidence.json'
    Write-JsonArtifact -Path $sourceEvidencePath -Value $sourceEvidence
    $evidenceBefore = (Get-FileHash -LiteralPath $sourceEvidencePath -Algorithm SHA256).Hash
    $redactorPath = Join-Path $repoRoot 'scripts/unity/redact-unity-artifacts.js'
    & node $redactorPath $artifactFixture
    Assert-That 'complete produced comparison evidence passes production redaction' ($LASTEXITCODE -eq 0)
    Assert-That 'public source, player and run identities survive byte-for-byte' (
        (Get-FileHash -LiteralPath $sourceEvidencePath -Algorithm SHA256).Hash -ceq $evidenceBefore
    )
    $sourceEvidence['accessToken'] = @{ nested = 'must not be accepted as a credential container' }
    Write-JsonArtifact -Path $sourceEvidencePath -Value $sourceEvidence
    & node $redactorPath $artifactFixture
    Assert-That 'true sensitive containers remain rejected' ($LASTEXITCODE -eq 2)
    foreach ($mutation in @('byte', 'missing', 'case', 'version', 'duplicate', 'hash')) {
        [System.IO.File]::WriteAllBytes($packageSourcePath, $contractBytes)
        $catalogFixture.sources.package.packagePath = 'Runtime/Broker.cs'
        $catalogFixture.sources.package.sha256 = $contractSha256
        $resolvedFixture.packages = @(@{ name = 'com.cysharp.messagepipe'; version = '1.8.2'; resolvedPath = $packageFixture })
        switch ($mutation) {
            'byte' { [System.IO.File]::AppendAllText($packageSourcePath, 'x') }
            'missing' { Remove-Item -LiteralPath $packageSourcePath }
            'case' { $catalogFixture.sources.package.packagePath = 'Runtime/broker.cs' }
            'version' { $resolvedFixture.packages[0].version = '1.8.1' }
            'duplicate' { $resolvedFixture.packages += $resolvedFixture.packages[0] }
            'hash' { $catalogFixture.sources.package.sha256 = '0' * 64 }
        }
        Write-TestJson -Path $catalogFixturePath -Value $catalogFixture
        Write-TestJson -Path $resolvedPackagesPath -Value $resolvedFixture
        Assert-Fails "comparison package rejects $mutation drift" -ExpectedMessage 'Comparison source evidence' {
            Get-ComparisonSourceEvidence -RepoRoot $sourceFixture -ResolvedPackagesPath $resolvedPackagesPath
        }
    }

    Write-Host 'IL2CPP profile contract tests passed.'
} finally {
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
