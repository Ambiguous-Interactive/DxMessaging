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
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($shippingProfile in $shippingProfiles) {
    foreach ($shippingTopology in $shippingTopologies) {
        $shippingCaseId = "$($shippingProfile.Level)-$($shippingTopology.Id)"
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
        } catch {
            $failure = "{0}: {1}" -f $shippingCaseId, $_.Exception.Message
            $failures.Add($failure)
            Write-Warning "Shipping-fidelity cell failed; continuing to preserve later evidence. $failure"
        }
    }
}

if ($failures.Count -gt 0) {
    throw "Shipping-fidelity cell failures: $($failures -join '; ')"
}
