#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$WorkspacePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{8}$')][string]$RetainedSourceSetKey,
    [ValidatePattern('^(\d+\.\d+\.\d+[abfp]\d+)?$')][string]$UnityVersion = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$workspace = [System.IO.Path]::GetFullPath($WorkspacePath)
$root = Join-Path $workspace 'dxm-u/t'

function Assert-PlainCacheDirectory {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "The managed test cache directory is missing: $Path"
    }
    if (((Get-Item -LiteralPath $Path -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing a redirected cache directory: $Path"
    }
}

Assert-PlainCacheDirectory $workspace
Assert-PlainCacheDirectory (Join-Path $workspace 'dxm-u')
Assert-PlainCacheDirectory $root
$projects = @(Get-ChildItem -LiteralPath $root -Directory)
if (-not [string]::IsNullOrEmpty($UnityVersion)) {
    $projects = @($projects | Where-Object { $_.Name.StartsWith("$UnityVersion-", [System.StringComparison]::Ordinal) })
}
$retained = @($projects | Where-Object { $_.Name.EndsWith("-$RetainedSourceSetKey", [System.StringComparison]::Ordinal) })
if ($retained.Count -eq 0) { throw 'The retained source-set key has no project in this scope; inventory is incomplete.' }
$driveRoot = [System.IO.Path]::GetPathRoot($root)
$before = ([System.IO.DriveInfo]::new($driveRoot)).AvailableFreeSpace
$ownedCurrent = 0
$projectCount = 0
$totalBytes = 0L
foreach ($project in @($projects | Sort-Object Name)) {
    if ($project.Name -cnotmatch '^\d+\.\d+\.\d+[abfp]\d+-(editmode|playmode|standalone|unitypackage)(-[0-9a-f]{8})?$') {
        continue
    }
    Assert-PlainCacheDirectory $project.FullName
    $marker = Join-Path $project.FullName '.dxmessaging-ci-project'
    $current = $project.Name.EndsWith("-$RetainedSourceSetKey", [System.StringComparison]::Ordinal)
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        (Get-Content -LiteralPath $marker -Raw).Trim() -cne 'com.wallstop-studios.dxmessaging unity ci ephemeral project') {
        if ($current) { throw 'The retained source-set project is not owned; inventory is incomplete.' }
        Write-Host "Skipping unowned project: $($project.Name)"
        continue
    }
    if ($current) { ++$ownedCurrent }
    $row = [ordered]@{
        project = $project.Name
        retained = $current
        files = 0L
        totalBytes = 0L
        beeBytes = 0L
        otherLibraryBytes = 0L
        outsideLibraryBytes = 0L
    }
    $libraryPrefix = (Join-Path $project.FullName 'Library') + [System.IO.Path]::DirectorySeparatorChar
    $beePrefix = (Join-Path $project.FullName 'Library/Bee') + [System.IO.Path]::DirectorySeparatorChar
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($project.FullName)
    while ($pending.Count -ne 0) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing redirected cache contents: $($entry.FullName)"
            }
            if ($entry.PSIsContainer) {
                $pending.Push($entry.FullName)
                continue
            }
            ++$row.files
            $row.totalBytes += $entry.Length
            if ($entry.FullName.StartsWith($beePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $row.beeBytes += $entry.Length
            } elseif ($entry.FullName.StartsWith($libraryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $row.otherLibraryBytes += $entry.Length
            } else {
                $row.outsideLibraryBytes += $entry.Length
            }
        }
    }
    ++$projectCount
    $totalBytes += $row.totalBytes
    Write-Output ('DXM_TEST_CACHE_INVENTORY ' + ($row | ConvertTo-Json -Compress))
}
if ($ownedCurrent -eq 0) { throw 'No owned current project was measured; inventory is incomplete.' }
$after = ([System.IO.DriveInfo]::new($driveRoot)).AvailableFreeSpace
$summary = [ordered]@{
    sourceSetKey = $RetainedSourceSetKey
    unityVersion = $UnityVersion
    projectCount = $projectCount
    retainedProjectCount = $ownedCurrent
    logicalBytes = $totalBytes
    freeBytesBefore = $before
    freeBytesAfter = $after
    elapsedMs = $timer.ElapsedMilliseconds
}
# File lengths do not measure unique physical storage or attribute changes in drive free space.
Write-Output ('DXM_TEST_CACHE_SUMMARY ' + ($summary | ConvertTo-Json -Compress))
