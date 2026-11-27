#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$unityScriptsRoot = Split-Path -Parent $PSScriptRoot
$runnerPath = Join-Path $unityScriptsRoot 'run-ci-tests.ps1'
$verifierPath = Join-Path $unityScriptsRoot 'verify-webgl-build.ps1'
$fixtureRoot = Join-Path $repoRoot ('.artifacts/u/webgl-smoke-' + [guid]::NewGuid().ToString('N'))
$artifactsRoot = Join-Path $repoRoot ('.artifacts/unity/webgl-smoke-' + [guid]::NewGuid().ToString('N'))
$verifierFixture = Join-Path ([System.IO.Path]::GetTempPath()) ('dxm-webgl-verifier-' + [guid]::NewGuid().ToString('N'))

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

try {
    $runnerOutput = & pwsh -NoLogo -NoProfile -File $runnerPath `
        -UnityVersion '2022.3.45f1' `
        -TestMode 'webgl' `
        -AssemblyNames '' `
        -ArtifactsPath $artifactsRoot `
        -ProjectPath $fixtureRoot `
        -GenerateOnly `
        -SkipCiAnalyzers 2>&1
    $runnerExit = $LASTEXITCODE
    Assert-That 'WebGL GenerateOnly exits zero' ($runnerExit -eq 0) 
    $runnerText = ($runnerOutput | Out-String)
    Assert-That 'WebGL generation reports the WebGL project path' ($runnerText.Contains('Generated ephemeral Unity project only'))

    $manifestPath = Join-Path $fixtureRoot 'Packages/manifest.json'
    $builderPath = Join-Path $fixtureRoot 'Assets/Editor/DxmWebGlBuilder.cs'
    $componentPath = Join-Path $fixtureRoot 'Assets/DxmWebGlSmoke.cs'
    Assert-That 'WebGL manifest exists' (Test-Path -LiteralPath $manifestPath -PathType Leaf)
    Assert-That 'WebGL builder source exists' (Test-Path -LiteralPath $builderPath -PathType Leaf)
    Assert-That 'WebGL smoke component exists' (Test-Path -LiteralPath $componentPath -PathType Leaf)

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-That 'WebGL manifest omits testables' ($null -eq $manifest.PSObject.Properties['testables'])
    Assert-That 'WebGL manifest keeps the package' ($manifest.dependencies.'com.wallstop-studios.dxmessaging' -match '^file:')
    Assert-That 'WebGL manifest keeps integration packages' (
        $manifest.dependencies.'com.gustavopsantos.reflex' -eq '14.4.0' -and
        $manifest.dependencies.'com.svermeulen.extenject' -eq '9.2.0-stcf3' -and
        $manifest.dependencies.'jp.hadashikick.vcontainer' -eq '1.19.0'
    )
    Assert-That 'WebGL manifest omits the test framework' ($null -eq $manifest.dependencies.PSObject.Properties['com.unity.test-framework'])

    $builderText = Get-Content -LiteralPath $builderPath -Raw
    Assert-That 'WebGL builder selects BuildTarget.WebGL' ($builderText.Contains('target = BuildTarget.WebGL'))
    Assert-That 'WebGL builder calls BuildPipeline.BuildPlayer' ($builderText.Contains('BuildPipeline.BuildPlayer(options)'))
    Assert-That 'WebGL builder reads the output path contract' ($builderText.Contains('DXM_WEBGL_BUILD_PATH'))
    Assert-That 'WebGL builder writes the report contract' ($builderText.Contains('DXM_WEBGL_BUILD_REPORT_PATH'))
    $componentText = Get-Content -LiteralPath $componentPath -Raw
    Assert-That 'WebGL smoke component links MessageBus' ($componentText.Contains('MessageBus bus = new MessageBus();'))

    New-Item -ItemType Directory -Force -Path (Join-Path $verifierFixture 'build/Build') | Out-Null
    foreach ($file in @(
            @{ Path = 'build/index.html'; Text = 'html' },
            @{ Path = 'build/Build/test.loader.js'; Text = 'loader' },
            @{ Path = 'build/Build/test.wasm'; Text = 'wasm' },
            @{ Path = 'build/Build/test.data'; Text = 'data' },
            @{ Path = 'build/Build/test.framework.js'; Text = 'framework' }
        )) {
        [System.IO.File]::WriteAllText((Join-Path $verifierFixture $file.Path), $file.Text)
    }
    $declaredFiles = @(
        'Build/test.data',
        'Build/test.framework.js',
        'Build/test.loader.js',
        'Build/test.wasm',
        'index.html'
    )
    $report = [ordered]@{
        schemaVersion = 1
        unityVersion = '2022.3.45f1'
        target = 'WebGL'
        result = 'Succeeded'
        totalSizeBytes = 27
        files = $declaredFiles
    }
    $reportPath = Join-Path $verifierFixture 'report.json'
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    $verificationPath = Join-Path $verifierFixture 'verification.json'
    & pwsh -NoLogo -NoProfile -File $verifierPath `
        -ReportPath $reportPath `
        -BuildPath (Join-Path $verifierFixture 'build') `
        -UnityVersion '2022.3.45f1' `
        -VerificationPath $verificationPath 2>&1 | Out-Null
    Assert-That 'WebGL verifier accepts a complete inventory' ($LASTEXITCODE -eq 0)
    $verification = Get-Content -LiteralPath $verificationPath -Raw | ConvertFrom-Json
    Assert-That 'WebGL verification records five files' ($verification.actualFileCount -eq 5)
    Assert-That 'WebGL verification records the wasm artifact' ($verification.requiredArtifacts.wasm -eq $true)
    Assert-That 'WebGL verification records no problem' (@($verification.problems).Count -eq 0)

    Remove-Item -LiteralPath (Join-Path $verifierFixture 'build/Build/test.wasm') -Force
    $failedVerificationPath = Join-Path $verifierFixture 'failed-verification.json'
    $failedOutput = & pwsh -NoLogo -NoProfile -File $verifierPath `
        -ReportPath $reportPath `
        -BuildPath (Join-Path $verifierFixture 'build') `
        -UnityVersion '2022.3.45f1' `
        -VerificationPath $failedVerificationPath 2>&1
    Assert-That 'WebGL verifier rejects a missing wasm artifact' ($LASTEXITCODE -ne 0)
    $failedText = ($failedOutput | Out-String)
    Assert-That 'WebGL verifier names the missing wasm artifact' ($failedText.Contains('missing the required wasm artifact'))
    $failedVerification = Get-Content -LiteralPath $failedVerificationPath -Raw | ConvertFrom-Json
    Assert-That 'WebGL failed verification records the wasm problem' (@($failedVerification.problems).Count -ge 1)

    Write-Host 'WebGL smoke regression suite passed.'
} finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $artifactsRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $verifierFixture -Recurse -Force -ErrorAction SilentlyContinue
}
