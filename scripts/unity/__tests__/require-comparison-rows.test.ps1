Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
$scriptPath = Join-Path $repoRoot 'scripts' 'unity' 'require-comparison-rows.ps1'
$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "dxm-comparison-row-gate-$([guid]::NewGuid())"
$baselinePath = Join-Path $tempDirectory 'comparison-rows.csv'
$csvHeader = 'scenario,platform,commit,runIndex,emitsPerSec,gcAllocations,wallClockMs,gcAllocatedBytes'

function Write-Baseline {
    param([string[]]$Scenarios)

    $rows = @($csvHeader) + @($Scenarios | ForEach-Object { "$_,Standalone IL2CPP,abc1234,0,1,-1,1,-1" })
    [System.IO.File]::WriteAllText($baselinePath, ($rows -join "`n") + "`n")
}

function Assert-GateFails {
    param(
        [string[]]$Scenarios,
        [string]$MessagePattern
    )

    Write-Baseline -Scenarios $Scenarios
    try {
        & $scriptPath -BaselinePath $baselinePath
        throw 'The comparison row gate accepted invalid rows.'
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw
        }
    }
}

try {
    [System.IO.Directory]::CreateDirectory($tempDirectory) | Out-Null
    $manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts' 'unity' 'comparison-supported-scenarios.json') -Raw | ConvertFrom-Json
    $expected = @($manifest.PSObject.Properties | ForEach-Object {
        $technology = $_.Name
        @($_.Value | ForEach-Object { "Comparison_${technology}_$_" })
    })

    Write-Baseline -Scenarios $expected
    & $scriptPath -BaselinePath $baselinePath

    Assert-GateFails -Scenarios @() -MessagePattern 'no published comparison rows'
    Assert-GateFails -Scenarios @($expected | Select-Object -SkipLast 1) -MessagePattern 'do not match'
    Assert-GateFails -Scenarios @($expected + 'Comparison_Unknown_GlobalToOne') -MessagePattern 'do not match'
    Assert-GateFails -Scenarios @($expected + $expected[0]) -MessagePattern 'do not match'
}
finally {
    if (Test-Path -LiteralPath $tempDirectory) {
        Remove-Item -LiteralPath $tempDirectory -Recurse -Force
    }
}
