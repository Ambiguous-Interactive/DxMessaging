#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$validatorPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'validate-il2cpp-profile.ps1'
$runnerPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'run-ci-tests.ps1'
$workflowPath = Join-Path $repoRoot '.github/workflows/perf-numbers.yml'
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
    Assert-That 'the configurator pins OptimizeSpeed' (
        $generatedSources[0].Contains('Il2CppCodeGeneration.OptimizeSpeed')
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
            unityVersion = '6000.3.16f1'
            values = Copy-JsonValue -Value $profile.$kind
        }
        Write-TestJson -Path $evidencePath -Value $evidence
        & $validatorPath `
            -ProfilePath $profilePath `
            -EvidencePath $evidencePath `
            -EvidenceKind $kind `
            -ExpectedSha256 $profileSha256

        foreach ($property in $profile.$kind.PSObject.Properties) {
            $mutated = Copy-JsonValue -Value $evidence
            $mutated.values.($property.Name) = if ($property.Value -is [bool]) {
                -not $property.Value
            } else {
                "$($property.Value)-drift"
            }
            Write-TestJson -Path $evidencePath -Value $mutated
            Assert-Fails "$kind.$($property.Name) drift" {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind
            }
        }

        $missing = Copy-JsonValue -Value $evidence
        $firstPropertyName = @($profile.$kind.PSObject.Properties)[0].Name
        $missing.values.PSObject.Properties.Remove($firstPropertyName)
        Write-TestJson -Path $evidencePath -Value $missing
        Assert-Fails "$kind missing value" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind
        }

        $extra = Copy-JsonValue -Value $evidence
        $extra.values | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
        Write-TestJson -Path $evidencePath -Value $extra
        Assert-Fails "$kind extra value" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind
        }
    }

    $runtimeEvidencePath = Join-Path $fixtureRoot 'runtime.json'
    $runtimeEvidence = [ordered]@{
        schemaVersion = 1
        profileId = $profile.profileId
        profileSha256 = $profileSha256
        evidenceKind = 'runtime'
        unityVersion = '6000.3.16f1'
        values = [ordered]@{ debugBuild = $false }
    }
    $runtimeEvidence.unexpected = $true
    Write-TestJson -Path $runtimeEvidencePath -Value $runtimeEvidence
    Assert-Fails 'extra evidence property' {
        & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime
    }

    $runtimeEvidence.Remove('unexpected')
    $runtimeEvidence.schemaVersion = '1'
    Write-TestJson -Path $runtimeEvidencePath -Value $runtimeEvidence
    Assert-Fails 'string evidence schema version' {
        & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime
    }

    $runtimeEvidence.schemaVersion = 1
    foreach ($metadataField in @('profileId', 'profileSha256', 'evidenceKind', 'unityVersion')) {
        $missingMetadata = Copy-JsonValue -Value $runtimeEvidence
        $missingMetadata.PSObject.Properties.Remove($metadataField)
        Write-TestJson -Path $runtimeEvidencePath -Value $missingMetadata
        Assert-Fails "missing evidence $metadataField" -ExpectedMessage "missing=$metadataField" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime
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
            & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime
        }
    }
    Assert-Fails 'missing evidence file' -ExpectedMessage 'does not exist' {
        & $validatorPath `
            -ProfilePath $profilePath `
            -EvidencePath (Join-Path $fixtureRoot 'missing-evidence.json') `
            -EvidenceKind runtime
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

    Write-Host 'IL2CPP profile contract tests passed.'
} finally {
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
