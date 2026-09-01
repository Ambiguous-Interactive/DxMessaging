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
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($shippingProfile in $shippingProfiles) {
    try {
        & $RunnerPath `
            -UnityVersion $UnityVersion `
            -UnityInstallRoot $UnityInstallRoot `
            -TestMode shipping `
            -AssemblyNames '' `
            -ArtifactsPath (Join-Path $ArtifactsPath $shippingProfile.Level) `
            -RepoRoot $RepoRoot `
            -ProjectPath (Join-Path $ProjectPathRoot "$UnityVersion-shipping-$($shippingProfile.Level)") `
            -CachePath $CachePath `
            -CanonicalProfilePath (Join-Path $RepoRoot $shippingProfile.Path) `
            -LicenseReturnOwner Central `
            -ReleaseCodeOptimization `
            -ReleasePlayerBuild
    } catch {
        $failure = "{0}: {1}" -f $shippingProfile.Level, $_.Exception.Message
        $failures.Add($failure)
        Write-Warning "Shipping-fidelity profile failed; continuing to preserve later evidence. $failure"
    }
}

if ($failures.Count -gt 0) {
    throw "Shipping-fidelity profile failures: $($failures -join '; ')"
}
