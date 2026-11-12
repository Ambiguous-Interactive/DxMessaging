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
$variables = @('RUNNER_WORKSPACE', 'RUNNER_NAME', 'GITHUB_REPOSITORY', 'DXM_REQUESTED_RUNNER', 'DXM_RETAINED_TEST_CACHE_KEY', 'DXM_TEST_CACHE_INVENTORY_ONLY')
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
    foreach ($inventoryOnly in @($false, $true)) {
        $shapes = @('valid', 'wrong-runner', 'wrong-repository', 'invalid-key', 'unknown-key', 'redirected-child', 'redirected-parent')
        if ($inventoryOnly) { $shapes += 'missing-current-marker' }
        foreach ($shape in $shapes) {
            $workspace = Join-Path $scratch "$inventoryOnly-$shape"
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
                New-Item -ItemType Directory -Path (Join-Path $project 'Library/Artifacts'), (Join-Path $project 'Assets') -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $project 'Library/Artifacts/sentinel') -Value 'imported'
                Set-Content -LiteralPath (Join-Path $project 'Assets/sentinel') -Value 'input'
            }
            $env:RUNNER_WORKSPACE = $workspace
            $env:RUNNER_NAME = 'DAD-MACHINE'
            $env:DXM_REQUESTED_RUNNER = 'DAD-MACHINE'
            $env:GITHUB_REPOSITORY = 'Ambiguous-Interactive/DxMessaging'
            $env:DXM_RETAINED_TEST_CACHE_KEY = 'a1b2c3d4'
            $env:DXM_TEST_CACHE_INVENTORY_ONLY = $inventoryOnly.ToString().ToLowerInvariant()
            if ($shape -eq 'wrong-runner') { $env:RUNNER_NAME = 'ELI-MACHINE' }
            if ($shape -eq 'wrong-repository') { $env:GITHUB_REPOSITORY = 'other/repository' }
            if ($shape -eq 'invalid-key') { $env:DXM_RETAINED_TEST_CACHE_KEY = '../bad' }
            if ($shape -eq 'unknown-key') { $env:DXM_RETAINED_TEST_CACHE_KEY = 'a1b2c3d5' }
            if ($shape -eq 'missing-current-marker') { Remove-Item -LiteralPath (Join-Path $current '.dxmessaging-ci-project') -Force }
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
            $before = @{}
            if ($inventoryOnly -and $shape -eq 'valid') {
                foreach ($file in @(Get-ChildItem -LiteralPath $workspace -Recurse -File -Force)) {
                    $before[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                }
            }
            $failure = $null
            $output = @()
            try { $output = @(& $reclaim 6>&1) } catch { $failure = $_.Exception.Message }
            if ($shape -eq 'valid' -and $inventoryOnly) {
                Assert-Control ($null -eq $failure) "Read-only inventory must complete: $failure"
                $afterFiles = @(Get-ChildItem -LiteralPath $workspace -Recurse -File -Force)
                Assert-Control ($afterFiles.Count -eq $before.Count) 'Inventory must preserve every file.'
                foreach ($file in $afterFiles) {
                    Assert-Control ($before.ContainsKey($file.FullName) -and $before[$file.FullName] -eq (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) 'Inventory must preserve every file path and byte.'
                }
                $rows = @($output | ForEach-Object { $_.ToString() } | Where-Object { $_.StartsWith('DXM_TEST_CACHE_INVENTORY ') } | ForEach-Object { $_.Substring('DXM_TEST_CACHE_INVENTORY '.Length) | ConvertFrom-Json })
                Assert-Control ($rows.Count -eq 2) 'Inventory must measure both owned generations and exclude unowned/other-role projects.'
                foreach ($row in $rows) {
                    $project = Join-Path $root $row.project
                    $beeBytes = (Get-Item -LiteralPath (Join-Path $project 'Library/Bee/sentinel')).Length
                    $otherLibraryBytes = (Get-Item -LiteralPath (Join-Path $project 'Library/Artifacts/sentinel')).Length
                    $inputBytes = (Get-Item -LiteralPath (Join-Path $project 'Assets/sentinel')).Length + (Get-Item -LiteralPath (Join-Path $project '.dxmessaging-ci-project') -Force).Length
                    Assert-Control ($row.files -eq 4 -and $row.totalBytes -eq $beeBytes + $otherLibraryBytes + $inputBytes) 'Inventory must report exact file counts and bytes.'
                    Assert-Control ($row.beeBytes -eq $beeBytes -and $row.otherLibraryBytes -eq $otherLibraryBytes -and $row.outsideLibraryBytes -eq $inputBytes) 'Inventory must separate Bee, other Library, and input bytes.'
                    Assert-Control ($row.retained -eq ($project -eq $current)) 'Inventory must label the current generation correctly.'
                }
            } elseif ($shape -eq 'valid') {
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
    }
} finally {
    foreach ($link in $links) { if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) } }
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process') }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
Write-Host "$checks production recovery/inventory controls passed; actual drive capacity remains a remote execution gate."
