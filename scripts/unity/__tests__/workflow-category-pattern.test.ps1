Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$workflow = Get-Content -LiteralPath (Join-Path $repo '.github/workflows/unity-tests.yml') -Raw
$patterns = [regex]::Matches($workflow, 'DXM_UNITY_TEST_CATEGORY: "(?<pattern>[^"\r\n]+)"')
if ($patterns.Count -ne 3) { throw 'Expected the three ordinary Editor, PlayMode, and standalone category inputs.' }
$legacy = @('Stress', 'Performance', 'Allocation', 'MemoryReclaim', 'PerfBench', 'PerfGate', 'PerfBaseline')
$sets = [Collections.Generic.List[string[]]]::new()
$sets.Add(@())
$sets.Add(@('Fast'))
$sets.Add(@('allocation'))
$sets.Add(@('Unrelated', 'AlsoUnrelated'))
$sets.Add(@('Fast', 'MemoryReclaim'))
$sets.Add(@('Stress', 'Performance'))
$sets.Add(@('UnityRuntime'))
foreach ($term in @($legacy) + @('UnityRuntime')) {
    $sets.Add(@($term))
    $sets.Add(@("Before${term}After"))
}
$checks = 0
for ($index = 0; $index -lt $patterns.Count; ++$index) {
    $value = $patterns[$index].Groups['pattern'].Value
    if (-not $value.StartsWith('!', [StringComparison]::Ordinal) -or $value.Contains(';')) {
        throw "Mode $index must emit one negative regex, so repeated framework discovery matches the exclusion once."
    }
    $pattern = [regex]::new($value.Substring(1))
    $terms = @($legacy)
    if ($index -eq 0) { $terms += 'UnityRuntime' }
    foreach ($categories in $sets) {
        $expected = $true
        $actual = $true
        foreach ($category in $categories) {
            foreach ($term in $terms) {
                if ([regex]::IsMatch($category, $term)) { $expected = $false }
            }
            if ($pattern.IsMatch($category)) { $actual = $false }
        }
        if ($actual -ne $expected) {
            throw "Mode $index changes the legacy exclusion for categories [$($categories -join ', ')]."
        }
        ++$checks
    }
}
# This checks the emitted regex language. Native NUnit inventories separately
# qualify inherited categories, explicit tests, platform filters, and cleanup.
Write-Host "$checks actual workflow regex selection controls passed."
