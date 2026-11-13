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
$variables = @('RUNNER_WORKSPACE', 'RUNNER_NAME', 'GITHUB_REPOSITORY', 'GITHUB_SHA', 'GITHUB_EVENT_NAME', 'DXM_UNITY_SOURCE_SET_KEY', 'DXM_REQUESTED_RUNNER', 'DXM_RETAINED_TEST_CACHE_KEY', 'DXM_TEST_CACHE_INVENTORY_ONLY')
$original = @{}
foreach ($name in $variables) { $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('dxm-cache-reclaim-' + [guid]::NewGuid().ToString('N'))
$links = [Collections.Generic.List[string]]::new()
$checks = 0

function Assert-Control {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-RestMethod {
    param([string]$Uri, [int]$TimeoutSec)
    $expected = "https://raw.githubusercontent.com/Ambiguous-Interactive/DxMessaging/$env:GITHUB_SHA/scripts/unity/inspect-test-project-caches.ps1"
    Assert-Control ($Uri -ceq $expected -and $TimeoutSec -eq 30) 'The operator must fetch the inspector from its exact workflow commit.'
    Get-Content -LiteralPath (Join-Path $repo 'scripts/unity/inspect-test-project-caches.ps1') -Raw
}

try {
    foreach ($inventoryOnly in @($false, $true)) {
        $shapes = @('valid', 'wrong-runner', 'wrong-repository', 'invalid-key', 'unknown-key', 'redirected-child', 'redirected-parent')
        if ($inventoryOnly) { $shapes += @('missing-current-marker', 'invalid-source-commit') }
        foreach ($shape in $shapes) {
            $workspace = Join-Path $scratch "$inventoryOnly-$shape"
            $root = Join-Path $workspace 'dxm-u/t'
            $current = Join-Path $root '2021.3.45f1-editmode-a1b2c3d4'
            $retired = Join-Path $root '2021.3.45f1-editmode-deadbeef'
            $unowned = Join-Path $root '2022.3.45f1-playmode-deadbeef'
            $otherVersion = Join-Path $root '6000.3.16f1-playmode-a1b2c3d4'
            $benchmark = Join-Path $workspace 'dxm-u/b/2021.3.45f1-standalone-deadbeef'
            foreach ($project in @($current, $retired, $unowned, $otherVersion, $benchmark)) {
                New-Item -ItemType Directory -Path (Join-Path $project 'Library/Bee') -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $project 'Library/Bee/sentinel') -Value 'keep'
            }
            foreach ($project in @($current, $retired, $otherVersion)) {
                Set-Content -LiteralPath (Join-Path $project '.dxmessaging-ci-project') -Value 'com.wallstop-studios.dxmessaging unity ci ephemeral project'
                New-Item -ItemType Directory -Path (Join-Path $project 'Library/Artifacts'), (Join-Path $project 'Assets') -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $project 'Library/Artifacts/sentinel') -Value 'imported'
                Set-Content -LiteralPath (Join-Path $project 'Assets/sentinel') -Value 'input'
            }
            $env:RUNNER_WORKSPACE = $workspace
            $env:RUNNER_NAME = 'DAD-MACHINE'
            $env:DXM_REQUESTED_RUNNER = 'DAD-MACHINE'
            $env:GITHUB_REPOSITORY = 'Ambiguous-Interactive/DxMessaging'
            $env:GITHUB_SHA = '0123456789012345678901234567890123456789'
            if ($shape -eq 'invalid-source-commit') { $env:GITHUB_SHA = 'not-a-commit' }
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
                Assert-Control ($rows.Count -eq 3) 'Inventory must measure owned generations across versions and exclude unowned/other-role projects.'
                foreach ($row in $rows) {
                    $project = Join-Path $root $row.project
                    $beeBytes = (Get-Item -LiteralPath (Join-Path $project 'Library/Bee/sentinel')).Length
                    $otherLibraryBytes = (Get-Item -LiteralPath (Join-Path $project 'Library/Artifacts/sentinel')).Length
                    $inputBytes = (Get-Item -LiteralPath (Join-Path $project 'Assets/sentinel')).Length + (Get-Item -LiteralPath (Join-Path $project '.dxmessaging-ci-project') -Force).Length
                    Assert-Control ($row.files -eq 4 -and $row.totalBytes -eq $beeBytes + $otherLibraryBytes + $inputBytes) 'Inventory must report exact file counts and bytes.'
                    Assert-Control ($row.beeBytes -eq $beeBytes -and $row.otherLibraryBytes -eq $otherLibraryBytes -and $row.outsideLibraryBytes -eq $inputBytes) 'Inventory must separate Bee, other Library, and input bytes.'
                    Assert-Control ($row.retained -eq ($project -in @($current, $otherVersion))) 'Inventory must label the current generation correctly.'
                }
                $inspector = Join-Path $repo 'scripts/unity/inspect-test-project-caches.ps1'
                $filtered = @(& $inspector -WorkspacePath $workspace -RetainedSourceSetKey 'a1b2c3d4' -UnityVersion '2021.3.45f1')
                $filteredRows = @($filtered | Where-Object { $_.StartsWith('DXM_TEST_CACHE_INVENTORY ') } | ForEach-Object { $_.Substring('DXM_TEST_CACHE_INVENTORY '.Length) | ConvertFrom-Json })
                Assert-Control ($filteredRows.Count -eq 2) 'The correctness job must measure only its selected Editor version.'
                Assert-Control (@($filteredRows | Where-Object { $_.project -notin @([IO.Path]::GetFileName($current), [IO.Path]::GetFileName($retired)) }).Count -eq 0) 'Version filtering must preserve exact project identities.'
                $summary = @($filtered | Where-Object { $_.StartsWith('DXM_TEST_CACHE_SUMMARY ') } | ForEach-Object { $_.Substring('DXM_TEST_CACHE_SUMMARY '.Length) | ConvertFrom-Json })
                Assert-Control ($summary.Count -eq 1 -and $summary[0].projectCount -eq 2 -and $summary[0].retainedProjectCount -eq 1) 'A complete version inventory must summarize its measured projects.'
                Assert-Control ($summary[0].logicalBytes -eq ($filteredRows | Measure-Object -Property totalBytes -Sum).Sum) 'The summary must reconcile with all measured row bytes.'
                $scopeFailure = $null
                try { & $inspector -WorkspacePath $workspace -RetainedSourceSetKey 'a1b2c3d4' -UnityVersion '6000.5.2f1' | Out-Null } catch { $scopeFailure = $_.Exception.Message }
                Assert-Control ($null -ne $scopeFailure) 'An absent requested version must not report a complete inventory.'
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
            Assert-Control (Test-Path -LiteralPath (Join-Path $otherVersion 'Library/Bee/sentinel')) 'The current key on another Editor version must remain.'
            if ($shape -eq 'redirected-child' -or $shape -eq 'redirected-parent') {
                Assert-Control (Test-Path -LiteralPath (Join-Path $foreign 'sentinel')) 'Redirected contents must remain untouched.'
            }
            ++$checks
        }
    }

    $retirement = Join-Path $repo 'scripts/unity/retire-test-project-caches.ps1'
    foreach ($shape in @('valid', 'push-event', 'trailing-paths', 'wrong-runner', 'wrong-repository', 'manual-event', 'invalid-key', 'unknown-key', 'wrong-known-key', 'missing-current-marker', 'wrong-current-marker', 'missing-current-mode', 'redirected-tree', 'redirected-current-marker', 'redirected-evidence', 'redirected-evidence-parent', 'overlapping-evidence', 'missing-evidence', 'unknown-old-entry', 'late-invalid-project')) {
        $workspace = Join-Path $scratch "routine-$shape"
        $root = Join-Path $workspace 'dxm-u/t'
        $evidence = Join-Path $workspace 'repository/.artifacts'
        $currentProjects = @()
        $retiredProjects = @()
        foreach ($mode in @('editmode', 'playmode', 'standalone')) {
            $currentProjects += Join-Path $root "2021.3.45f1-$mode-a1b2c3d4"
            foreach ($key in @('0123abcd', 'deadbeef')) { $retiredProjects += Join-Path $root "2021.3.45f1-$mode-$key" }
        }
        $otherProjects = @(
            (Join-Path $root '2022.3.45f1-standalone-deadbeef'),
            (Join-Path $root '2021.3.45f1-unitypackage-deadbeef'),
            (Join-Path $root '2021.3.45f1-editmode'),
            (Join-Path $workspace 'dxm-u/b/2021.3.45f1-standalone-deadbeef'),
            (Join-Path $workspace 'dxm-u/p/2021.3.45f1-standalone-deadbeef')
        )
        foreach ($project in @($currentProjects) + @($retiredProjects) + @($otherProjects)) {
            foreach ($child in @('Assets', 'Packages', 'ProjectSettings', 'Library/Bee', 'Library/Artifacts', 'Build/DxmTestPlayer')) {
                New-Item -ItemType Directory -Path (Join-Path $project $child) -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $project "$child/sentinel") -Value "$project/$child"
            }
            Set-Content -LiteralPath (Join-Path $project '.dxmessaging-ci-project') -Value 'com.wallstop-studios.dxmessaging unity ci ephemeral project'
        }
        $unowned = Join-Path $root '2021.3.45f1-editmode-feedface'
        New-Item -ItemType Directory -Path $unowned, $evidence, (Join-Path $workspace 'dxm-c/2021.3.45f1/upm') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $unowned 'sentinel') -Value 'unowned'
        Set-Content -LiteralPath (Join-Path $evidence 'retained.raw') -Value 'retained evidence'
        Set-Content -LiteralPath (Join-Path $workspace 'dxm-c/2021.3.45f1/upm/sentinel') -Value 'shared package cache'
        $env:RUNNER_NAME = 'DAD-MACHINE'
        $env:GITHUB_REPOSITORY = 'Ambiguous-Interactive/DxMessaging'
        $env:GITHUB_EVENT_NAME = if ($shape -eq 'push-event') { 'push' } else { 'pull_request' }
        $retainedKey = 'a1b2c3d4'
        $env:DXM_UNITY_SOURCE_SET_KEY = 'a1b2c3d4'
        if ($shape -eq 'wrong-runner') { $env:RUNNER_NAME = 'ELI-MACHINE' }
        if ($shape -eq 'wrong-repository') { $env:GITHUB_REPOSITORY = 'other/repository' }
        if ($shape -eq 'manual-event') { $env:GITHUB_EVENT_NAME = 'workflow_dispatch' }
        if ($shape -eq 'invalid-key') { $retainedKey = '../bad' }
        if ($shape -eq 'unknown-key') { $retainedKey = 'a1b2c3d5' }
        if ($shape -eq 'wrong-known-key') { $retainedKey = 'deadbeef' }
        if ($shape -eq 'missing-current-marker') { Remove-Item -LiteralPath (Join-Path $currentProjects[0] '.dxmessaging-ci-project') -Force }
        if ($shape -eq 'wrong-current-marker') { Set-Content -LiteralPath (Join-Path $currentProjects[0] '.dxmessaging-ci-project') -Value 'not owned' }
        if ($shape -eq 'missing-current-mode') { Remove-Item -LiteralPath $currentProjects[2] -Recurse -Force }
        if ($shape -eq 'unknown-old-entry') { Set-Content -LiteralPath (Join-Path $retiredProjects[0] 'retain-evidence.json') -Value 'unexpected data' }
        if ($shape -eq 'late-invalid-project') { Set-Content -LiteralPath (Join-Path $retiredProjects[-1] 'retain-evidence.json') -Value 'validate entire plan before deleting first project' }
        if ($shape -eq 'overlapping-evidence') { $evidence = Join-Path $retiredProjects[0] 'Assets' }
        if ($shape -eq 'missing-evidence') { $evidence = Join-Path $workspace 'missing-evidence' }
        if ($shape.StartsWith('redirected-', [StringComparison]::Ordinal)) {
            $foreign = Join-Path $workspace 'foreign'
            New-Item -ItemType Directory -Path $foreign -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $foreign 'sentinel') -Value 'foreign retained data'
            $link = switch ($shape) {
                'redirected-tree' { Join-Path $retiredProjects[0] 'Library/Bee/redirect' }
                'redirected-current-marker' { Join-Path $currentProjects[0] '.dxmessaging-ci-project' }
                'redirected-evidence' { $evidence }
                'redirected-evidence-parent' { Split-Path -Parent $evidence }
            }
            $target = $foreign
            if (Test-Path -LiteralPath $link) {
                if (Test-Path -LiteralPath $link -PathType Leaf) { Remove-Item -LiteralPath $link -Force }
                else { $target = Join-Path $foreign 'moved'; Move-Item -LiteralPath $link -Destination $target }
            }
            $kind = if ([IO.Path]::DirectorySeparatorChar -eq '\') { 'Junction' } else { 'SymbolicLink' }
            New-Item -ItemType $kind -Path $link -Target $target | Out-Null
            $links.Add($link)
        }
        $before = @{}
        foreach ($file in @(Get-ChildItem -LiteralPath $workspace -File -Recurse -Force)) {
            $before[$file.FullName] = @{ Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash; Length = $file.Length }
        }
        $positive = $shape -in @('valid', 'push-event', 'trailing-paths')
        if ($shape -eq 'valid') {
            $baseline = @(& (Join-Path $repo 'scripts/unity/inspect-test-project-caches.ps1') -WorkspacePath $workspace -UnityVersion '2021.3.45f1' -RetainedSourceSetKey $retainedKey)
            $old = @($baseline | Where-Object { $_.StartsWith('DXM_TEST_CACHE_INVENTORY ') } | ForEach-Object { $_.Substring('DXM_TEST_CACHE_INVENTORY '.Length) | ConvertFrom-Json } | Where-Object { -not $_.retained -and $_.project -match '^2021\.3\.45f1-(editmode|playmode|standalone)-[0-9a-f]{8}$' })
            Assert-Control ($old.Count -eq 6) 'The actual read-only baseline must retain six old standard generations.'
            Write-Host 'RED baseline: the actual inspector retains six old standard projects; the new three-project bound is unmet.'
        }
        $workspaceArgument = if ($shape -eq 'trailing-paths') { $workspace + [IO.Path]::DirectorySeparatorChar } else { $workspace }
        $evidenceArgument = if ($shape -eq 'trailing-paths') { $evidence + [IO.Path]::DirectorySeparatorChar } else { $evidence }
        $failure = $null
        $output = @()
        try { $output = @(& $retirement -WorkspacePath $workspaceArgument -EvidencePath $evidenceArgument -UnityVersion '2021.3.45f1' -RetainedSourceSetKey $retainedKey) }
        catch { $failure = $_.Exception.Message }
        if ($positive) {
            Assert-Control ($null -eq $failure) "The $shape retirement must complete: $failure"
            foreach ($project in $retiredProjects) { Assert-Control (-not (Test-Path -LiteralPath $project)) 'Every eligible retired generation must be removed.' }
            $summaries = @($output | Where-Object { $_.StartsWith('DXM_TEST_CACHE_RETIREMENT ') } | ForEach-Object { $_.Substring('DXM_TEST_CACHE_RETIREMENT '.Length) | ConvertFrom-Json })
            Assert-Control ($summaries.Count -eq 1 -and $summaries[0].retiredProjects -eq 6 -and $summaries[0].remainingOwnedStandardProjects -eq 3) 'The retirement summary must reconcile exact project counts.'
            $expectedBytes = 0L
            foreach ($name in $before.Keys) {
                if (@($retiredProjects | Where-Object { $name.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 0) { $expectedBytes += $before[$name].Length }
            }
            Assert-Control ($summaries[0].removedLogicalBytes -eq $expectedBytes) 'Removed logical bytes must reconcile with every eligible file.'
            $again = @(& $retirement -WorkspacePath $workspaceArgument -EvidencePath $evidenceArgument -UnityVersion '2021.3.45f1' -RetainedSourceSetKey $retainedKey)
            $second = @($again | Where-Object { $_.StartsWith('DXM_TEST_CACHE_RETIREMENT ') } | ForEach-Object { $_.Substring('DXM_TEST_CACHE_RETIREMENT '.Length) | ConvertFrom-Json })
            Assert-Control ($second.Count -eq 1 -and $second[0].retiredProjects -eq 0 -and $second[0].removedLogicalBytes -eq 0) 'Routine retirement must be idempotent.'
        } else { Assert-Control ($null -ne $failure) "The routine $shape guard must reject before any deletion. Last output: $($output | Select-Object -Last 1)" }
        foreach ($name in $before.Keys) {
            $eligible = @($retiredProjects | Where-Object { $name.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 0
            if ($positive -and $eligible) { continue }
            Assert-Control (Test-Path -LiteralPath $name -PathType Leaf) "The routine $shape must preserve $name"
            Assert-Control ((Get-FileHash -LiteralPath $name -Algorithm SHA256).Hash -eq $before[$name].Hash) "The routine $shape must preserve every protected file hash."
        }
        ++$checks
    }
} finally {
    foreach ($link in $links) { if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) } }
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process') }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
Write-Host "$checks production recovery/inventory/retirement controls passed; actual drive capacity remains a remote execution gate."
