#!/usr/bin/env pwsh
# Execute the workflow's actual cache selection against real Git input changes.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$workflow = Join-Path $repoRoot '.github/workflows/unity-tests.yml'
$readRun = 'const fs = require("node:fs"), yaml = require("yaml"); const document = yaml.parse(fs.readFileSync(process.argv[1], "utf8")); const step = Object.values(document.jobs).flatMap(job => job.steps || []).find(step => step.id === "source_cache"); if (!step) throw new Error("Missing source cache step"); process.stdout.write(step.run);'
Push-Location $repoRoot
try {
    $body = (& node -e $readRun $workflow) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the actual workflow cache-selection step.' }
} finally {
    Pop-Location
}
$cacheScript = [scriptblock]::Create($body)
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('dxm-source-cache-' + [guid]::NewGuid().ToString('N'))
$previousEnvironmentFile = $env:GITHUB_ENV

function Assert-CacheCondition {
    param([string]$Name, [bool]$Condition)
    if (-not $Condition) { throw $Name }
    Write-Host "[ok] $Name"
}

function Invoke-FixtureGit {
    param([string[]]$GitArguments)
    & git @GitArguments
    if ($LASTEXITCODE -ne 0) { throw "Fixture git command failed: $($GitArguments -join ' ')" }
}

function Read-CacheKey {
    Remove-Item -LiteralPath $env:GITHUB_ENV -Force -ErrorAction SilentlyContinue
    & $cacheScript
    $value = (Get-Content -LiteralPath $env:GITHUB_ENV -Raw).Trim()
    Assert-CacheCondition -Name 'The workflow emits an eight-character source path key' `
        -Condition ($value -cmatch '^DXM_UNITY_SOURCE_SET_KEY=[a-f0-9]{8}$')
    return $value
}

function Assert-CacheSelectionFails {
    param([string]$Name)
    Remove-Item -LiteralPath $env:GITHUB_ENV -Force -ErrorAction SilentlyContinue
    $failed = $false
    try { & $cacheScript } catch { $failed = $true }
    Assert-CacheCondition -Name $Name -Condition $failed
    Assert-CacheCondition -Name 'Rejected input publishes no reusable key' `
        -Condition (-not (Test-Path -LiteralPath $env:GITHUB_ENV))
}

try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    Push-Location $fixture
    $env:GITHUB_ENV = Join-Path $fixture 'github-env'
    Invoke-FixtureGit -GitArguments @('init', '--quiet')
    New-Item -ItemType Directory -Path 'Runtime' | Out-Null
    Set-Content -LiteralPath 'Runtime/A.cs' -Value 'class A {}'
    Invoke-FixtureGit -GitArguments @('add', 'Runtime/A.cs')
    $initial = Read-CacheKey
    Set-Content -LiteralPath 'Runtime/A.cs' -Value 'class A { int value; }'
    Invoke-FixtureGit -GitArguments @('add', 'Runtime/A.cs')
    Assert-CacheCondition -Name 'Content edits preserve the generated project' `
        -Condition ((Read-CacheKey) -ceq $initial)
    Set-Content -LiteralPath 'Runtime/B.cs' -Value 'class B {}'
    Invoke-FixtureGit -GitArguments @('add', 'Runtime/B.cs')
    $added = Read-CacheKey
    Assert-CacheCondition -Name 'An added input selects a different generated project' `
        -Condition ($added -cne $initial)
    Invoke-FixtureGit -GitArguments @('rm', '--force', '--quiet', 'Runtime/A.cs')
    $removed = Read-CacheKey
    Assert-CacheCondition -Name 'A removed input leaves its old compiler graph behind' `
        -Condition ($removed -cne $added)
    Invoke-FixtureGit -GitArguments @('mv', 'Runtime/B.cs', 'Runtime/C.cs')
    $renamed = Read-CacheKey
    Assert-CacheCondition -Name 'A renamed input selects a different generated project' `
        -Condition ($renamed -cne $removed)
    Set-Content -LiteralPath 'README.md' -Value 'Documentation change'
    Invoke-FixtureGit -GitArguments @('add', 'README.md')
    Assert-CacheCondition -Name 'Unrelated documentation preserves the generated project' `
        -Condition ((Read-CacheKey) -ceq $renamed)
    Invoke-FixtureGit -GitArguments @('rm', '--force', '--quiet', 'Runtime/C.cs')
    Assert-CacheSelectionFails -Name 'An empty source checkout fails before Unity starts'
    Remove-Item -LiteralPath '.git' -Recurse -Force
    Assert-CacheSelectionFails -Name 'A missing Git checkout fails before Unity starts'
} finally {
    Pop-Location
    $env:GITHUB_ENV = $previousEnvironmentFile
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Unity source input project cache controls passed.'
