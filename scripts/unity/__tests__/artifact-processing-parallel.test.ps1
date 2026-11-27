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
    param(
        [string[]]$Paths,
        [string]$Parallel = '',
        [string[]]$ParallelPaths = @(),
        [string]$ExpectedFailure = ''
    )
    $env:DXM_REDACT_PATHS = $Paths -join "`n"
    $env:DXM_REDACT_PARALLEL_PATHS = if ($Parallel -eq 'true') {
        $Paths -join "`n"
    } elseif ($ParallelPaths.Count -ne 0) {
        $ParallelPaths -join "`n"
    } else { '' }
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
    Assert-That 'the default stays sequential' ($definition.action.inputs['parallel-paths'].default -eq '')
    Assert-That 'the action exposes no separate child-scope input' (-not $definition.action.inputs.ContainsKey('parallel-child-paths'))
    $processingStep = @($definition.steps | Where-Object { $_.ContainsKey('id') -and $_.id -eq 'redact_tests' })[0]
    Assert-That 'the existing two-minute cap stays in force' ($processingStep['timeout-minutes'] -eq 2)
    Assert-That 'sequential shipping and consumer paths stay broad' ($processingStep.with.paths.Trim() -eq '.artifacts/unity')
    $concurrent = @($processingStep.with['parallel-paths'] -split "`n" | Where-Object { $_.Trim() })
    Assert-That 'three ordinary and one optional root are declared' ($concurrent.Count -eq 4)
    foreach ($mode in @('editmode', 'playmode', 'standalone')) {
        $expected = '${{ !inputs.shipping_fidelity && !inputs.consumer_install_validation && format(''@@PATH@@'', matrix.unity-version) || '''' }}'.Replace('@@PATH@@', ".artifacts/unity/{0}-$mode")
        Assert-That "ordinary path expression and special-mode guard: $mode" ($concurrent -contains $expected)
        $upload = @($definition.steps | Where-Object { $_.ContainsKey('uses') -and $_.uses.StartsWith('actions/upload-artifact@') -and $_.with.path.EndsWith("-$mode") })
        Assert-That "the ordinary root is uploaded exactly once: $mode" ($upload.Count -eq 1)
    }
    $webglExpected = '${{ inputs.webgl_fidelity && contains(fromJSON(env.WEBGL_EDITORS), matrix.unity-version) && format(''@@PATH@@'', matrix.unity-version) || '''' }}'.Replace('@@PATH@@', '.artifacts/unity/{0}-webgl')
    Assert-That 'WebGL path expression and editor guard' ($concurrent -contains $webglExpected)
    $webglUpload = @($definition.steps | Where-Object { $_.ContainsKey('uses') -and $_.uses.StartsWith('actions/upload-artifact@') -and $_.with.path.EndsWith('-webgl') })
    Assert-That 'the optional WebGL root is uploaded exactly once' ($webglUpload.Count -eq 1)
    $uploads = @($definition.steps | Where-Object { $_.ContainsKey('uses') -and $_.uses.StartsWith('actions/upload-artifact@') })
    foreach ($upload in $uploads) {
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

    $artifactRoot = Join-Path $fixtureRoot 'artifacts-unity'
    $shippingRoot = Join-Path $artifactRoot '2021.3.45f1-shipping'
    $ordinaryRoot = Join-Path $artifactRoot '2021.3.45f1-editmode'
    $shippingCells = @(
        foreach ($profile in @('minimal', 'low', 'medium', 'high')) {
            foreach ($topology in @('semantic-18', 'cardinality-1', 'cardinality-16', 'cardinality-256', 'cardinality-1000')) {
                "$profile-$topology"
            }
        }
    )
    New-Item -ItemType Directory -Path $shippingRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $ordinaryRoot -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $shippingRoot 'shipping-matrix-evidence.json'), "{`"cells`":20}`n")
    [System.IO.File]::WriteAllText((Join-Path $shippingRoot 'shipping-summary.log'), "serial SC-FAKE-FAKE-FAKE-FAKE-FAKE`n")
    [System.IO.File]::WriteAllText((Join-Path $ordinaryRoot 'unity.log'), "ordinary serial SC-FAKE-FAKE-FAKE-FAKE-FAKE`n")
    foreach ($cell in $shippingCells) {
        $cellRoot = Join-Path $shippingRoot $cell
        New-Item -ItemType Directory -Path $cellRoot | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $cellRoot 'unity.log'), "cell $cell serial SC-FAKE-FAKE-FAKE-FAKE-FAKE`n")
    }
    Assert-That 'the shipping fixture has the full 20-cell shape' ($shippingCells.Count -eq 20)
    $elapsed = Measure-Command {
        Invoke-Action -Paths @($artifactRoot)
    }
    Assert-That 'the shipping-shaped action stays under the unchanged cap' ($elapsed.TotalSeconds -lt 120)
    Assert-That 'the shipping parent summary is scrubbed' (-not [System.IO.File]::ReadAllText((Join-Path $shippingRoot 'shipping-summary.log')).Contains('SC-FAKE'))
    Assert-That 'the shipping marker remains valid after redaction' ((Get-Content -LiteralPath (Join-Path $shippingRoot 'shipping-matrix-evidence.json') -Raw | ConvertFrom-Json).cells -eq 20)
    foreach ($cell in $shippingCells) {
        $contents = [System.IO.File]::ReadAllText((Join-Path (Join-Path $shippingRoot $cell) 'unity.log'))
        Assert-That "shipping cell is scrubbed: $cell" (-not $contents.Contains('SC-FAKE'))
    }
    Assert-That 'the auto-discovered ordinary root is scrubbed' (-not [System.IO.File]::ReadAllText((Join-Path $ordinaryRoot 'unity.log')).Contains('SC-FAKE'))

    $mixedSequential = Join-Path $fixtureRoot 'mixed-sequential'
    $mixedParallel = Join-Path $fixtureRoot 'mixed-parallel'
    New-Item -ItemType Directory -Path $mixedSequential | Out-Null
    New-Item -ItemType Directory -Path $mixedParallel | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $mixedSequential 'unity.log'), "serial SC-FAKE-FAKE-FAKE-FAKE-FAKE`n")
    [System.IO.File]::WriteAllText((Join-Path $mixedParallel 'unity.log'), "serial SC-FAKE-FAKE-FAKE-FAKE-FAKE`n")
    $mixedFailure = Join-Path $fixtureRoot 'mixed-failure.log'
    [System.IO.File]::WriteAllText($mixedFailure, 'not a directory')
    Invoke-Action -Paths @($mixedSequential) -ParallelPaths @($mixedFailure) -ExpectedFailure 'failed in one or more workers'
    Assert-That 'the sequential residual completes after a parallel failure' (-not [System.IO.File]::ReadAllText((Join-Path $mixedSequential 'unity.log')).Contains('SC-FAKE'))
    [System.IO.File]::WriteAllText((Join-Path $mixedSequential 'unity.log'), "serial SC-FAKE-FAKE-FAKE-FAKE-FAKE`n")
    Invoke-Action -Paths @($mixedFailure) -ParallelPaths @($mixedParallel) -ExpectedFailure 'failed with exit code'
    Assert-That 'the parallel scope completes before a sequential failure' (-not [System.IO.File]::ReadAllText((Join-Path $mixedParallel 'unity.log')).Contains('SC-FAKE'))
    $sensitiveArtifactRoot = Join-Path $fixtureRoot 'artifacts-sensitive'
    $sensitiveShippingRoot = Join-Path $sensitiveArtifactRoot '2021.3.45f1-shipping'
    $sensitiveCell = Join-Path $sensitiveShippingRoot 'SC-FAKE-FAKE-FAKE-FAKE-FAKE-shipping'
    New-Item -ItemType Directory -Path $sensitiveCell -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $sensitiveShippingRoot 'shipping-matrix-evidence.json'), "{`"cells`":1}`n")
    [System.IO.File]::WriteAllText((Join-Path $sensitiveCell 'unity.log'), "cell output`n")
    Invoke-Action -Paths @($sensitiveArtifactRoot) -ExpectedFailure 'must not contain sensitive data'
    $overlapBroad = Join-Path $fixtureRoot 'overlap-broad'
    $overlapNarrow = Join-Path $overlapBroad 'SC-FAKE-FAKE-FAKE-FAKE-FAKE-shipping'
    $overlapCell = Join-Path $overlapNarrow 'medium-semantic-18'
    New-Item -ItemType Directory -Path $overlapCell -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $overlapNarrow 'shipping-matrix-evidence.json'), "{`"cells`":1}`n")
    [System.IO.File]::WriteAllText((Join-Path $overlapCell 'unity.log'), "cell output`n")
    Invoke-Action -Paths @($overlapNarrow, $overlapBroad) -ExpectedFailure 'must not contain sensitive data'
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
