#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$WorkspacePath,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$EvidencePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+[abfp]\d+$')][string]$UnityVersion,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{8}$')][string]$RetainedSourceSetKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$timer = [System.Diagnostics.Stopwatch]::StartNew()
if ($env:RUNNER_NAME -cne 'DAD-MACHINE' -or $env:GITHUB_REPOSITORY -cne 'Ambiguous-Interactive/DxMessaging' -or
    -not ($env:GITHUB_EVENT_NAME -cin @('pull_request', 'push'))) {
    throw 'Routine test-cache retirement requires a DAD correctness push or pull request; no cache was changed.'
}
if ($RetainedSourceSetKey -cne $env:DXM_UNITY_SOURCE_SET_KEY) {
    throw 'The retained key must match the workflow source-set key; no cache was changed.'
}
$workspace = [System.IO.Path]::GetFullPath($WorkspacePath)
if ($workspace.Equals([System.IO.Path]::GetPathRoot($workspace), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The runner workspace cannot be a filesystem root.'
}
$workspace = $workspace.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$root = Join-Path $workspace 'dxm-u/t'
$evidence = [System.IO.Path]::GetFullPath($EvidencePath).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$comparison = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
$separator = [System.IO.Path]::DirectorySeparatorChar
if (-not $evidence.StartsWith($workspace.TrimEnd($separator) + $separator, $comparison) -or
    $evidence.Equals($root, $comparison) -or $evidence.StartsWith($root + $separator, $comparison) -or
    $root.StartsWith($evidence.TrimEnd($separator) + $separator, $comparison)) {
    throw 'Retained evidence must be separate from the managed test-project tree.'
}
$ancestor = $evidence
while (-not $ancestor.Equals($workspace, $comparison)) {
    if (-not (Test-Path -LiteralPath $ancestor -PathType Container) -or
        ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Retained evidence must have an existing, plain path inside the workspace.'
    }
    $ancestor = [System.IO.Path]::GetDirectoryName($ancestor)
}

$inspector = Join-Path $PSScriptRoot 'inspect-test-project-caches.ps1'
$inventory = @(& $inspector -WorkspacePath $workspace -UnityVersion $UnityVersion -RetainedSourceSetKey $RetainedSourceSetKey)
$inventory | Write-Output
$rows = @($inventory | Where-Object { $_.StartsWith('DXM_TEST_CACHE_INVENTORY ') } | ForEach-Object {
    $_.Substring('DXM_TEST_CACHE_INVENTORY '.Length) | ConvertFrom-Json
})
$currentNames = @('editmode', 'playmode', 'standalone') | ForEach-Object { "$UnityVersion-$_-$RetainedSourceSetKey" }
$current = @($rows | Where-Object { $_.project -cin $currentNames -and $_.retained })
if ($current.Count -ne 3) { throw 'All three owned current correctness projects are required before retirement.' }
$standardPattern = '^' + [regex]::Escape($UnityVersion) + '-(editmode|playmode|standalone)-[0-9a-f]{8}$'
$retired = @($rows | Where-Object { $_.project -cmatch $standardPattern -and -not $_.retained } | Sort-Object project)
$expectedDirectories = @('Assets', 'Packages', 'ProjectSettings', 'Library', 'Build', 'Temp', 'Logs', 'UserSettings', 'obj')

# Validate every candidate before the first removal; unexpected data remains available for review.
foreach ($row in $retired) {
    $project = Join-Path $root $row.project
    foreach ($entry in @(Get-ChildItem -LiteralPath $project -Force)) {
        if (($entry.PSIsContainer -and $entry.Name -cin $expectedDirectories) -or
            (-not $entry.PSIsContainer -and $entry.Name -ceq '.dxmessaging-ci-project')) { continue }
        throw "Unexpected contents in retired test project; no project was removed: $($entry.FullName)"
    }
}
$driveRoot = [System.IO.Path]::GetPathRoot($root)
$before = ([System.IO.DriveInfo]::new($driveRoot)).AvailableFreeSpace
$removedBytes = 0L
foreach ($row in $retired) {
    $project = Join-Path $root $row.project
    Remove-Item -LiteralPath $project -Recurse -Force
    if (Test-Path -LiteralPath $project) { throw "Retired project still exists after removal: $project" }
    $removedBytes += $row.totalBytes
    Write-Output ('DXM_TEST_CACHE_RETIRED ' + ($row | ConvertTo-Json -Compress))
}

$post = @(& $inspector -WorkspacePath $workspace -UnityVersion $UnityVersion -RetainedSourceSetKey $RetainedSourceSetKey)
$remaining = @($post | Where-Object { $_.StartsWith('DXM_TEST_CACHE_INVENTORY ') } | ForEach-Object {
    $_.Substring('DXM_TEST_CACHE_INVENTORY '.Length) | ConvertFrom-Json
} | Where-Object { $_.project -cmatch $standardPattern })
if ($remaining.Count -ne 3 -or @($remaining | Where-Object { -not $_.retained }).Count -ne 0) {
    throw 'Routine retirement did not leave exactly the three owned current correctness projects.'
}
foreach ($line in $post) { Write-Output ($line -replace '^DXM_TEST_CACHE_', 'DXM_TEST_CACHE_POST_') }
$summary = [ordered]@{
    sourceSetKey = $RetainedSourceSetKey
    unityVersion = $UnityVersion
    retiredProjects = $retired.Count
    removedLogicalBytes = $removedBytes
    remainingOwnedStandardProjects = $remaining.Count
    freeBytesBefore = $before
    freeBytesAfter = ([System.IO.DriveInfo]::new($driveRoot)).AvailableFreeSpace
    elapsedMs = $timer.ElapsedMilliseconds
}
# Logical file lengths and drive observations do not establish unique physical bytes reclaimed.
Write-Output ('DXM_TEST_CACHE_RETIREMENT ' + ($summary | ConvertTo-Json -Compress))
