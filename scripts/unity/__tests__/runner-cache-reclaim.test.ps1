Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$workflow = Get-Content -LiteralPath (Join-Path $repo '.github/workflows/runner-bootstrap.yml') -Raw
$start = $workflow.IndexOf('      - name: Reclaim retired test Bee caches', [System.StringComparison]::Ordinal)
if ($start -lt 0) { throw 'The production recovery step is missing.' }
$match = [regex]::Match($workflow.Substring($start), '(?m)^        run: \|\r?\n(?<body>(?: {10}[^\r\n]*(?:\r?\n|$))+)')
if (-not $match.Success) { throw 'The complete production recovery script could not be extracted.' }
$body = (($match.Groups['body'].Value -split '\r?\n') | ForEach-Object { $_ -replace '^ {10}', '' }) -join "`n"
$reclaim = [scriptblock]::Create($body)
$variables = @('RUNNER_WORKSPACE', 'RUNNER_NAME', 'GITHUB_REPOSITORY', 'DXM_REQUESTED_RUNNER', 'DXM_RETAINED_TEST_CACHE_KEY')
$original = @{}
foreach ($name in $variables) { $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('dxm-cache-reclaim-' + [guid]::NewGuid().ToString('N'))
$links = [Collections.Generic.List[string]]::new()
$checks = 0

function Assert-Control {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    foreach ($shape in @('valid', 'wrong-runner', 'wrong-repository', 'invalid-key', 'unknown-key', 'redirected-child', 'redirected-parent')) {
        $workspace = Join-Path $scratch $shape
        $root = Join-Path $workspace 'dxm-u/t'
        $current = Join-Path $root '2021.3.45f1-editmode-a1b2c3d4'
        $retired = Join-Path $root '2021.3.45f1-editmode-deadbeef'
        $unowned = Join-Path $root '2022.3.45f1-playmode-deadbeef'
        $benchmark = Join-Path $workspace 'dxm-u/b/2021.3.45f1-standalone-deadbeef'
        foreach ($project in @($current, $retired, $unowned, $benchmark)) {
            New-Item -ItemType Directory -Path (Join-Path $project 'Library/Bee') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $project 'Library/Bee/sentinel') -Value 'keep'
        }
        foreach ($project in @($current, $retired)) {
            Set-Content -LiteralPath (Join-Path $project '.dxmessaging-ci-project') -Value 'com.wallstop-studios.dxmessaging unity ci ephemeral project'
        }
        $env:RUNNER_WORKSPACE = $workspace
        $env:RUNNER_NAME = 'DAD-MACHINE'
        $env:DXM_REQUESTED_RUNNER = 'DAD-MACHINE'
        $env:GITHUB_REPOSITORY = 'Ambiguous-Interactive/DxMessaging'
        $env:DXM_RETAINED_TEST_CACHE_KEY = 'a1b2c3d4'
        if ($shape -eq 'wrong-runner') { $env:RUNNER_NAME = 'ELI-MACHINE' }
        if ($shape -eq 'wrong-repository') { $env:GITHUB_REPOSITORY = 'other/repository' }
        if ($shape -eq 'invalid-key') { $env:DXM_RETAINED_TEST_CACHE_KEY = '../bad' }
        if ($shape -eq 'unknown-key') { $env:DXM_RETAINED_TEST_CACHE_KEY = 'a1b2c3d5' }
        $foreign = Join-Path $workspace 'foreign'
        if ($shape -eq 'redirected-child' -or $shape -eq 'redirected-parent') {
            New-Item -ItemType Directory -Path $foreign -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $foreign 'sentinel') -Value 'foreign'
            $link = if ($shape -eq 'redirected-child') { Join-Path $retired 'Library/Bee/linked' } else { Join-Path $workspace 'dxm-u' }
            $target = $foreign
            if ($shape -eq 'redirected-parent') {
                $target = Join-Path $foreign 'redirected'
                Move-Item -LiteralPath $link -Destination $target
            }
            $kind = if ([IO.Path]::DirectorySeparatorChar -eq '\') { 'Junction' } else { 'SymbolicLink' }
            New-Item -ItemType $kind -Path $link -Target $target | Out-Null
            $links.Add($link)
        }
        $failure = $null
        try { & $reclaim } catch { $failure = $_.Exception.Message }
        if ($shape -eq 'valid') {
            Assert-Control (-not (Test-Path -LiteralPath (Join-Path $retired 'Library/Bee'))) 'A marked retired Bee directory must be reclaimed.'
            Assert-Control ($null -eq $failure -or $failure -eq 'Cache recovery did not establish the required 64 GiB free space; administrator action remains necessary.') 'Only the unchanged real-capacity gate may fail on a small test drive.'
            Assert-Control (Test-Path -LiteralPath (Join-Path $retired '.dxmessaging-ci-project')) 'Project inputs must remain.'
        } else {
            Assert-Control ($null -ne $failure) "The $shape guard must reject the operation."
            if ($shape -ne 'redirected-parent') {
                Assert-Control (Test-Path -LiteralPath (Join-Path $retired 'Library/Bee/sentinel')) "The $shape guard must preserve retired cache bytes."
            }
        }
        if ($shape -ne 'redirected-parent') {
            Assert-Control (Test-Path -LiteralPath (Join-Path $current 'Library/Bee/sentinel')) 'The current source-set cache must remain.'
            Assert-Control (Test-Path -LiteralPath (Join-Path $unowned 'Library/Bee/sentinel')) 'Unowned projects must remain.'
        }
        Assert-Control (Test-Path -LiteralPath (Join-Path $benchmark 'Library/Bee/sentinel')) 'Other project roles must remain.'
        if ($shape -eq 'redirected-child' -or $shape -eq 'redirected-parent') {
            Assert-Control (Test-Path -LiteralPath (Join-Path $foreign 'sentinel')) 'Redirected contents must remain untouched.'
        }
        ++$checks
    }
} finally {
    foreach ($link in $links) { if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) } }
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process') }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
Write-Host "$checks production recovery selection and refusal controls passed; actual drive capacity remains a remote execution gate."
