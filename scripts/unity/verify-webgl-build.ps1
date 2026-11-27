#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportPath,

    [Parameter(Mandatory = $true)]
    [string]$BuildPath,

    [Parameter(Mandatory = $true)]
    [string]$UnityVersion,

    [Parameter(Mandatory = $true)]
    [string]$VerificationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# FullName is absolute; resolve a relative -BuildPath once so prefix removal
# compares like with like for every inventory entry.
$BuildPath = [System.IO.Path]::GetFullPath($BuildPath)

$problems = New-Object System.Collections.Generic.List[string]
$report = $null
if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
    $problems.Add("WebGL build report is missing: $ReportPath")
} else {
    try {
        $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
    } catch {
        $problems.Add("WebGL build report is not valid JSON: $($_.Exception.Message)")
    }
}

$declaredFiles = @()
$actualFiles = @()
$required = [ordered]@{
    'index.html'   = $false
    'loader.js'    = $false
    'wasm'         = $false
    'data'         = $false
    'framework.js' = $false
}
if ($null -ne $report) {
    if ([int]$report.schemaVersion -ne 1) {
        $problems.Add("WebGL build report schemaVersion is $($report.schemaVersion), expected 1.")
    }
    if ([string]$report.target -cne 'WebGL') {
        $problems.Add("WebGL build report target is '$($report.target)', expected 'WebGL'.")
    }
    if ([string]$report.result -cne 'Succeeded') {
        $problems.Add("WebGL build report result is '$($report.result)', expected 'Succeeded'.")
    }
    if ([string]$report.unityVersion -cne $UnityVersion) {
        $problems.Add("WebGL build report unityVersion is '$($report.unityVersion)', expected '$UnityVersion'.")
    }
    $declaredFiles = @($report.files | ForEach-Object { [string]$_ })
}

if (Test-Path -LiteralPath $BuildPath -PathType Container) {
    $actualFiles = @(
        Get-ChildItem -LiteralPath $BuildPath -File -Recurse |
            ForEach-Object {
                $_.FullName.Substring($BuildPath.Length).TrimStart(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar
                ).Replace('\', '/')
            } |
            Sort-Object
    )
} else {
    $problems.Add("WebGL build output directory is missing: $BuildPath")
}

$declaredSorted = @($declaredFiles | Sort-Object)
if ($declaredSorted.Count -eq 0) {
    $problems.Add('WebGL build report declares no output files.')
}
if (($declaredSorted -join "`n") -cne ($actualFiles -join "`n")) {
    $problems.Add(
        "WebGL build output inventory does not match the report (declared=$($declaredSorted.Count), actual=$($actualFiles.Count))."
    )
}
foreach ($file in $actualFiles) {
    if ($file -match '(^|/)\.\.(/|$)' -or $file.StartsWith('/')) {
        $problems.Add("WebGL build output contains an unsafe relative path: $file")
    }
    if ($file -eq 'index.html') { $required['index.html'] = $true }
    if ($file -match '^Build/.*\.loader\.js$') { $required['loader.js'] = $true }
    if ($file -match '^Build/.*\.wasm(\..+)?$') { $required['wasm'] = $true }
    if ($file -match '^Build/.*\.data(\..+)?$') { $required['data'] = $true }
    if ($file -match '^Build/.*\.framework\.js(\..+)?$') { $required['framework.js'] = $true }
}
foreach ($key in $required.Keys) {
    if (-not $required[$key]) {
        $problems.Add("WebGL build output is missing the required $key artifact.")
    }
}

$actualBytes = [long]0
foreach ($file in $actualFiles) {
    $fullPath = [System.IO.Path]::Combine($BuildPath, ($file -replace '/', '\'))
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        $actualBytes += (Get-Item -LiteralPath $fullPath).Length
    }
}
if ($actualBytes -le 0) {
    $problems.Add('WebGL build output is empty.')
}

$verificationDirectory = Split-Path -Parent $VerificationPath
if ($verificationDirectory -and -not (Test-Path -LiteralPath $verificationDirectory -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $verificationDirectory | Out-Null
}
$verification = [ordered]@{
    schemaVersion = 1
    reportPath = (Split-Path -Leaf $ReportPath)
    buildPath = (Split-Path -Leaf $BuildPath)
    unityVersion = $UnityVersion
    target = 'WebGL'
    declaredFileCount = $declaredSorted.Count
    actualFileCount = $actualFiles.Count
    actualBytes = $actualBytes
    requiredArtifacts = $required
    problems = @($problems.ToArray())
}
$verification | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $VerificationPath -Encoding UTF8

if ($problems.Count -gt 0) {
    Write-Host "::error::WebGL build verification failed: $($problems -join ' ')"
    exit 1
}

Write-Host "::notice::WebGL build verification passed: $($actualFiles.Count) files, $actualBytes bytes."
exit 0
