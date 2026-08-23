[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselinePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    throw "Comparison baseline does not exist: $BaselinePath"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
Push-Location $repoRoot
try {
    $expected = @(& node -e "require('./scripts/unity/perf-scenarios.js').COMPARISON_SUPPORTED_SCENARIO_IDS.forEach((id) => console.log(id))")
    if ($LASTEXITCODE -ne 0) {
        throw 'Comparison capability manifest evaluation failed.'
    }

    $actual = @(Import-Csv -LiteralPath $BaselinePath |
        Where-Object { $_.scenario -like 'Comparison_*' } |
        ForEach-Object { $_.scenario })
    $expectedSorted = @($expected | Sort-Object)
    $actualSorted = @($actual | Sort-Object)

    if ($expectedSorted.Count -ne 48) {
        throw "The pinned comparison capability manifest must contain exactly 48 rows; found $($expectedSorted.Count)."
    }
    if ($actualSorted.Count -eq 0) {
        throw 'The comparison baseline contains no published comparison rows.'
    }

    $difference = @(Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actualSorted)
    if ($expectedSorted.Count -ne $actualSorted.Count -or $difference.Count -ne 0) {
        $difference | Format-Table | Out-String | Write-Host
        throw 'Published comparison rows do not match the pinned capability manifest.'
    }
}
finally {
    Pop-Location
}
