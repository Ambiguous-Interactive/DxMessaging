#!/usr/bin/env pwsh
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dxm-artifact-processing-{0}" -f [guid]::NewGuid().ToString('N'))
$oldPaths = $env:DXM_REDACT_PATHS
$oldParallel = $env:DXM_REDACT_PARALLEL_PATHS

function Assert-That {
    param([string]$Description, [bool]$Condition)
    if (-not $Condition) {
        throw "Assertion failed: $Description"
    }
}

function Invoke-Action {
    param([string[]]$Paths, [string]$Parallel, [string]$ExpectedFailure = '')
    $env:DXM_REDACT_PATHS = $Paths -join "`n"
    $env:DXM_REDACT_PARALLEL_PATHS = $Parallel
    try {
        & $actionBody
    } catch {
        if ($ExpectedFailure -and $_.Exception.Message.Contains($ExpectedFailure)) {
            return
        }
        throw
    }
    if ($ExpectedFailure) {
        throw "Expected failure containing '$ExpectedFailure'."
    }
}

try {
    Push-Location $repoRoot
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    $definition = node -e 'const fs = require("fs"), yaml = require("yaml"); const action = yaml.parse(fs.readFileSync(".github/actions/redact-unity-artifacts/action.yml", "utf8")); const workflow = yaml.parse(fs.readFileSync(".github/workflows/unity-tests.yml", "utf8")); process.stdout.write(JSON.stringify({action, steps: workflow.jobs["unity-tests"].steps}));' | ConvertFrom-Json -AsHashtable
    if ($LASTEXITCODE -ne 0) { throw 'Could not load production action and workflow.' }
    $actionBody = [scriptblock]::Create($definition.action.runs.steps[0].run)
    Assert-That 'the default stays sequential' ($definition.action.inputs['parallel-paths'].default -eq 'false')
    $processingStep = @($definition.steps | Where-Object { $_.ContainsKey('id') -and $_.id -eq 'redact_tests' })
    Assert-That 'the existing two-minute cap stays in force' ($processingStep[0]['timeout-minutes'] -eq 2)
    Assert-That 'Unity tests enable parallel paths' ($processingStep[0].with['parallel-paths'] -eq 'true')
    $declaredPaths = @($processingStep[0].with.paths -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $uploads = @($definition.steps | Where-Object { $_.ContainsKey('uses') -and $_.uses.StartsWith('actions/upload-artifact@') })
    foreach ($upload in $uploads) {
        Assert-That "uploaded path is processed: $($upload.with.path)" ($declaredPaths -contains $upload.with.path)
        Assert-That 'each upload waits for all successful workers' ($upload.if.Contains("steps.redact_tests.outcome == 'success'"))
    }

    $roots = @(0..4 | ForEach-Object {
        $root = Join-Path $fixtureRoot "scope-$_"
        New-Item -ItemType Directory -Path $root | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $root 'unity.log'), "ordinary build output $_`n")
        $root
    })
    Invoke-Action -Paths $roots -Parallel 'true'
    foreach ($root in $roots) {
        Assert-That 'successful workers preserve ordinary bytes' ([System.IO.File]::ReadAllText((Join-Path $root 'unity.log')).StartsWith('ordinary build output'))
    }
    Invoke-Action -Paths @($roots[0], (Join-Path $fixtureRoot 'absent')) -Parallel 'true'
    Invoke-Action -Paths @($roots[0], $roots[1]) -Parallel ''
    Invoke-Action -Paths @() -Parallel 'true' -ExpectedFailure 'requires at least one path'
    foreach ($case in @(
        @{ Paths = @($roots[0], $roots[0]) },
        @{ Paths = @($roots[0], (Join-Path $roots[0] 'child')) },
        @{ Paths = @((Join-Path $roots[0] 'child'), $roots[0]) },
        @{ Paths = @($roots[0], (Join-Path $roots[0] '../scope-0')) }
    )) {
        Invoke-Action -Paths $case.Paths -Parallel 'true' -ExpectedFailure 'must not overlap'
    }

    $markers = @($roots | ForEach-Object {
        $directory = Join-Path $_ 'native-build-inputs'
        New-Item -ItemType Directory -Path $directory | Out-Null
        $marker = Join-Path $directory 'retained-manifest.json'
        [System.IO.File]::WriteAllText($marker, '{}')
        $marker
    })
    $invalidRoot = Join-Path $fixtureRoot 'file-instead-of-directory.log'
    [System.IO.File]::WriteAllText($invalidRoot, 'not a directory')
    Invoke-Action -Paths (@($invalidRoot) + $roots) -Parallel 'true' -ExpectedFailure 'failed in one or more workers'
    foreach ($marker in $markers) {
        Assert-That 'all other workers complete after one fails' (-not (Test-Path -LiteralPath $marker))
    }
    Invoke-Action -Paths @($invalidRoot) -Parallel '' -ExpectedFailure 'failed with exit code'
    Write-Host 'Artifact processing action regression checks passed.'
} finally {
    $env:DXM_REDACT_PATHS = $oldPaths
    $env:DXM_REDACT_PARALLEL_PATHS = $oldParallel
    Pop-Location
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}
