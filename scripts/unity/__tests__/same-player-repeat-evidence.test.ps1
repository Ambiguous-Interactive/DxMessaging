#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Exercises the real same-player manifest, process-affinity, and host-probe helpers.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runnerPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'run-ci-tests.ps1'
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$workflowPath = Join-Path $repoRoot '.github/workflows/perf-numbers.yml'
$stabilityScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'require-same-player-stability.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $runnerPath,
    [ref]$tokens,
    [ref]$parseErrors
)
if ($parseErrors -and $parseErrors.Count -gt 0) {
    throw "run-ci-tests.ps1 has parse errors: $($parseErrors.Message -join '; ')"
}

foreach ($name in @(
    'Get-StandaloneHostConditionSnapshot',
    'Get-StandalonePlayerManifest',
    'Write-JsonArtifact',
    'ConvertTo-ProcessArgumentLine',
    'Invoke-ProcessWithTreeKillTimeout'
)) {
    $definition = $ast.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $name
        },
        $true
    ) | Select-Object -First 1
    if (-not $definition) {
        throw "Function '$name' was not found in run-ci-tests.ps1."
    }
    Invoke-Expression $definition.Extent.Text
}

function Assert-That {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][bool]$Condition
    )
    if (-not $Condition) {
        throw "Assertion failed: $Description"
    }
}

function Write-TestJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Assert-StabilityGateFails {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$ArtifactsPath,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    $failedAsExpected = $false
    try {
        & $stabilityScriptPath -ArtifactsPath $ArtifactsPath
    } catch {
        $failedAsExpected = $_.Exception.Message.Contains($ExpectedMessage)
    }
    Assert-That $Description $failedAsExpected
}

function New-StabilityFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][double[]]$RunValues,
        [Parameter(Mandatory = $true)][string[]]$Scenarios
    )

    $repeatRoot = Join-Path $Path 'same-player-repeats'
    New-Item -ItemType Directory -Force -Path $repeatRoot | Out-Null
    $records = New-Object System.Collections.Generic.List[object]
    for ($index = 0; $index -lt $RunValues.Count; $index++) {
        $runIndex = $index + 1
        $runNumber = '{0:D2}' -f $runIndex
        if ($runIndex -eq 1) {
            $relativeResultsPath = 'results.xml'
            $relativeLogPath = 'player.log'
        } else {
            $relativeResultsPath = "same-player-repeats/run-$runNumber/repeat-$runNumber-results.xml"
            $relativeLogPath = "same-player-repeats/run-$runNumber/repeat-$runNumber-player.log"
            New-Item `
                -ItemType Directory `
                -Force `
                -Path (Split-Path -Parent (Join-Path $Path $relativeResultsPath)) |
                Out-Null
        }
        $rows = foreach ($scenario in $Scenarios) {
            "Comparison_DxMessaging_$scenario,Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1),aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa,0,$($RunValues[$index]),0,5000,0"
        }
        [System.IO.File]::WriteAllLines((Join-Path $Path $relativeResultsPath), $rows)
        [System.IO.File]::WriteAllText((Join-Path $Path $relativeLogPath), 'fixture')

        $hostConditionsFile = "run-$runNumber-host-conditions.json"
        $beforeTimestamp = [DateTime]::UtcNow
        $snapshot = [ordered]@{
            phase = 'before'
            timestampUtc = $beforeTimestamp.ToString('O')
            logicalProcessors = @([ordered]@{ frequencyMhz = 4000; loadPercent = 20 })
            processors = @()
            totalCpuLoadPercent = 20
            acpiThermalZones = [ordered]@{ available = $false; zones = @() }
        }
        $afterSnapshot = [ordered]@{} + $snapshot
        $afterSnapshot.phase = 'after'
        $afterSnapshot.timestampUtc = $beforeTimestamp.AddSeconds(1).ToString('O')
        Write-TestJson `
            -Path (Join-Path $repeatRoot $hostConditionsFile) `
            -Value ([ordered]@{
                schemaVersion = 1
                runIndex = $runIndex
                runCount = $RunValues.Count
                playerProcessId = 1000 + $runIndex
                playerProcessorAffinityMask = '0x3'
                timedOut = $false
                before = $snapshot
                after = $afterSnapshot
            })
        $records.Add([ordered]@{
                runIndex = $runIndex
                resultsPath = $relativeResultsPath
                playerLogPath = $relativeLogPath
                hostConditionsFile = $hostConditionsFile
                processId = 1000 + $runIndex
                processorAffinityMask = '0x3'
                timedOut = $false
            })
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        fileCount = 1
        files = @([ordered]@{
                path = 'DxmTestPlayer.exe'
                length = 3
                sha256 = 'A' * 64
            })
    }
    Write-TestJson `
        -Path (Join-Path $repeatRoot 'same-player-evidence.json') `
        -Value ([ordered]@{
            schemaVersion = 1
            runCount = $RunValues.Count
            canonicalPublishedRunIndex = 1
            playerDirectoryManifestMatches = $true
            playerDirectoryManifestBefore = $manifest
            playerDirectoryManifestAfter = $manifest
            runs = @($records.ToArray())
        })
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dxm-same-player-{0}" -f [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $executablePath = Join-Path $fixtureRoot 'DxmTestPlayer.exe'
    $gameAssemblyPath = Join-Path $fixtureRoot 'GameAssembly.dll'
    $metadataPath = Join-Path $fixtureRoot 'global-metadata.dat'
    $unityPlayerPath = Join-Path $fixtureRoot 'UnityPlayer.dll'
    $nestedDirectory = Join-Path $fixtureRoot 'DxmTestPlayer_Data/Managed'
    New-Item -ItemType Directory -Force -Path $nestedDirectory | Out-Null
    $arbitraryPlayerFilePath = Join-Path $nestedDirectory 'arbitrary-player-file.bin'
    [System.IO.File]::WriteAllText($executablePath, 'exe-v1')
    [System.IO.File]::WriteAllText($gameAssemblyPath, 'game-v1')
    [System.IO.File]::WriteAllText($metadataPath, 'metadata-v1')
    [System.IO.File]::WriteAllText($unityPlayerPath, 'unity-v1')
    [System.IO.File]::WriteAllText($arbitraryPlayerFilePath, 'arbitrary-v1')

    $manifestBefore = Get-StandalonePlayerManifest -ExecutablePath $executablePath
    [System.IO.File]::WriteAllText($arbitraryPlayerFilePath, 'arbitrary-v2')
    $manifestAfter = Get-StandalonePlayerManifest -ExecutablePath $executablePath
    Assert-That 'the manifest includes every nested player file in ordinal path order' (
        $manifestBefore.fileCount -eq 5 -and
        (@($manifestBefore.files.path) -join ',') -ceq (
            'DxmTestPlayer.exe,' +
            'DxmTestPlayer_Data/Managed/arbitrary-player-file.bin,' +
            'GameAssembly.dll,UnityPlayer.dll,global-metadata.dat'
        ) -and
        @($manifestBefore.files | Where-Object { $_.path -eq 'DxmTestPlayer_Data/Managed/arbitrary-player-file.bin' }).Count -eq 1
    )
    $arbitraryBefore = @(
        $manifestBefore.files |
            Where-Object { $_.path -eq 'DxmTestPlayer_Data/Managed/arbitrary-player-file.bin' }
    )[0]
    $arbitraryAfter = @(
        $manifestAfter.files |
            Where-Object { $_.path -eq 'DxmTestPlayer_Data/Managed/arbitrary-player-file.bin' }
    )[0]
    Assert-That 'a mutation to an arbitrary player file changes the complete manifest' (
        $arbitraryBefore.sha256 -ne $arbitraryAfter.sha256
    )
    Assert-That 'unchanged player files keep their manifest entries' (
        @($manifestBefore.files | Where-Object { $_.path -eq 'GameAssembly.dll' })[0].sha256 -eq
        @($manifestAfter.files | Where-Object { $_.path -eq 'GameAssembly.dll' })[0].sha256
    )

    $snapshot = Get-StandaloneHostConditionSnapshot -Phase 'test'
    Assert-That 'the snapshot records its phase and timestamp' (
        $snapshot.phase -eq 'test' -and -not [string]::IsNullOrWhiteSpace($snapshot.timestampUtc)
    )
    Assert-That 'the snapshot distinguishes logical, package, and ACPI thermal probes' (
        $null -ne $snapshot.logicalProcessors -and
        $null -ne $snapshot.processors -and
        $null -ne $snapshot.acpiThermalZones.available
    )

    $jsonPath = Join-Path $fixtureRoot 'evidence.json'
    Write-JsonArtifact -Path $jsonPath -Value $snapshot
    $bytes = [System.IO.File]::ReadAllBytes($jsonPath)
    Assert-That 'JSON evidence is UTF-8 without a BOM' (
        $bytes.Length -ge 3 -and
        -not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    )
    $parsed = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    Assert-That 'JSON evidence round-trips' ($parsed.phase -eq 'test')

    $processLog = Join-Path $fixtureRoot 'process.log'
    $pwshPath = (Get-Process -Id $PID).Path
    $processArguments = @{
        FilePath = $pwshPath
        Arguments = @('-NoLogo', '-NoProfile', '-Command', 'Wait-Event -Timeout 1 | Out-Null; exit 0')
        TimeoutSeconds = 30
        LogPath = $processLog
        Label = 'same-player process evidence test'
    }
    $processResult = Invoke-ProcessWithTreeKillTimeout @processArguments
    Assert-That 'the process helper returns success and a process id' (
        $processResult.ExitCode -eq 0 -and $processResult.ProcessId -gt 0
    )
    $hasProcessorAffinity = -not [string]::IsNullOrWhiteSpace(
        [string]$processResult.ProcessorAffinityMask
    )
    $hasProcessorAffinityError = -not [string]::IsNullOrWhiteSpace(
        [string]$processResult.ProcessorAffinityError
    )
    if ($IsMacOS) {
        # Investigation 2026-08-25: macOS does not implement Process.ProcessorAffinity.
        # Require its explicit probe error here; the Windows-only evidence gate below still
        # rejects a missing affinity value, and Windows/Linux test hosts require the real value.
        Assert-That 'the macOS process helper captures its platform affinity probe error' (
            -not $hasProcessorAffinity -and $hasProcessorAffinityError
        )
    } else {
        Assert-That 'the process helper captures actual child affinity' (
            $hasProcessorAffinity -and -not $hasProcessorAffinityError
        )
    }

    $runnerText = Get-Content -LiteralPath $runnerPath -Raw
    $buildIndex = $runnerText.IndexOf('$buildResult = Invoke-ProcessWithTreeKillTimeout')
    $repeatIndex = $runnerText.IndexOf('for ($playerRunIndex = 1;')
    Assert-That 'one editor build remains outside and before the repeat loop' (
        $buildIndex -ge 0 -and $repeatIndex -gt $buildIndex
    )
    Assert-That 'run 1 stays canonical while repeat filenames are noncanonical' (
        $runnerText.Contains('$currentResultsPath = $resultsPath') -and
        $runnerText.Contains('"repeat-$runNumber-results.xml"') -and
        $runnerText.Contains('"repeat-$runNumber-player.log"')
    )
    Assert-That 'the managed repeat root is cleared before reuse' (
        $runnerText.Contains('Remove-Item -LiteralPath $samePlayerEvidenceRoot -Recurse -Force')
    )
    Assert-That 'the runner captures and compares complete player manifests' (
        $runnerText.Contains('Get-StandalonePlayerManifest') -and
        $runnerText.Contains('playerDirectoryManifestMatches')
    )

    $workflowText = Get-Content -LiteralPath $workflowPath -Raw
    Assert-That 'only the comparison player receives three launches' (
        $workflowText -match "StandalonePlayerRunCount = if .*benchmark-suite.*comparisons.* 3 .* 1"
    )
    Assert-That 'the workflow invokes the tested same-player evidence gate' (
        $workflowText.Contains('./scripts/unity/require-same-player-stability.ps1')
    )

    $scenarioModulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'perf-scenarios.js'
    $scenarios = @(
        & node -e 'require(process.argv[1]).COMPARISON_SCENARIO_ORDER.forEach((scenario) => console.log(scenario))' `
            $scenarioModulePath
    )
    Assert-That 'the fixture uses all comparison scenarios' (
        $LASTEXITCODE -eq 0 -and $scenarios.Count -eq 9
    )

    $stableFixturePath = Join-Path $fixtureRoot 'stable-artifacts'
    New-Item -ItemType Directory -Force -Path $stableFixturePath | Out-Null
    New-StabilityFixture `
        -Path $stableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    & $stabilityScriptPath -ArtifactsPath $stableFixturePath
    $stableReportPath = Join-Path $stableFixturePath 'same-player-repeats/same-player-stability.json'
    $stableReport = Get-Content -LiteralPath $stableReportPath -Raw | ConvertFrom-Json
    Assert-That 'three stable samples pass without median or outlier removal' (
        $stableReport.allRowsStable -eq $true -and
        $stableReport.calculation -eq '(maximum / minimum - 1) * 100' -and
        $stableReport.platform -eq 'Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1)' -and
        $stableReport.commit -eq 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' -and
        @($stableReport.rows).Count -eq 9
    )

    $unstableFixturePath = Join-Path $fixtureRoot 'unstable-artifacts'
    New-Item -ItemType Directory -Force -Path $unstableFixturePath | Out-Null
    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1050000) `
        -Scenarios $scenarios
    & $stabilityScriptPath -ArtifactsPath $unstableFixturePath
    $unstableReportPath = Join-Path $unstableFixturePath 'same-player-repeats/same-player-stability.json'
    $unstableReport = Get-Content -LiteralPath $unstableReportPath -Raw | ConvertFrom-Json
    Assert-That 'an unstable spread remains a successful evidence verdict' (
        $unstableReport.allRowsStable -eq $false -and
        @($unstableReport.rows | Where-Object { $_.withinMaterialityBand -eq $false }).Count -eq 9
    )

    $invalidEvidencePath = Join-Path $unstableFixturePath 'same-player-repeats/same-player-evidence.json'
    $invalidEvidence = Get-Content -LiteralPath $invalidEvidencePath -Raw | ConvertFrom-Json
    $invalidEvidence.runs[1].processorAffinityMask = ''
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    $invalidFailed = $false
    try {
        & $stabilityScriptPath -ArtifactsPath $unstableFixturePath
    } catch {
        $invalidFailed = $_.Exception.Message.Contains('did not record its actual processor affinity')
    }
    Assert-That 'malformed process evidence fails closed' $invalidFailed

    $invalidEvidence.runs[1].processorAffinityMask = '0x3'
    $invalidEvidence.playerDirectoryManifestAfter.files[0].sha256 = 'E' * 64
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    $identityMismatchFailed = $false
    try {
        & $stabilityScriptPath -ArtifactsPath $unstableFixturePath
    } catch {
        $identityMismatchFailed = $_.Exception.Message.Contains('manifest entry 0 is missing, invalid, or changed')
    }
    Assert-That 'the gate independently rejects a changed player manifest entry' $identityMismatchFailed

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $invalidEvidence = Get-Content -LiteralPath $invalidEvidencePath -Raw | ConvertFrom-Json
    $invalidEvidence.schemaVersion = 2
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    Assert-StabilityGateFails `
        -Description 'the aggregate evidence schema fails closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'Expected same-player evidence schemaVersion 1'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $invalidEvidence = Get-Content -LiteralPath $invalidEvidencePath -Raw | ConvertFrom-Json
    $invalidEvidence.runs[1].resultsPath = 'same-player-repeats/run-02/wrong.xml'
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    Assert-StabilityGateFails `
        -Description 'noncanonical managed repeat paths fail closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'did not use its exact managed evidence paths'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $invalidEvidence = Get-Content -LiteralPath $invalidEvidencePath -Raw | ConvertFrom-Json
    $invalidEvidence.runs[1].timedOut = $true
    Write-TestJson -Path $invalidEvidencePath -Value $invalidEvidence
    Assert-StabilityGateFails `
        -Description 'a timed-out player remains valid NUnit output but fails stability evidence' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'timed out and cannot support stability evidence'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $hostPath = Join-Path $unstableFixturePath 'same-player-repeats/run-02-host-conditions.json'
    $invalidHost = Get-Content -LiteralPath $hostPath -Raw | ConvertFrom-Json
    $invalidHost.schemaVersion = 2
    Write-TestJson -Path $hostPath -Value $invalidHost
    Assert-StabilityGateFails `
        -Description 'the per-run host schema fails closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'host evidence does not match its run record'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $invalidHost = Get-Content -LiteralPath $hostPath -Raw | ConvertFrom-Json
    $invalidHost.after.timestampUtc = (
        [DateTimeOffset]::Parse([string]$invalidHost.before.timestampUtc).AddMinutes(-1).ToString('O')
    )
    Write-TestJson -Path $hostPath -Value $invalidHost
    Assert-StabilityGateFails `
        -Description 'reversed before and after timestamps fail closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'recorded its before snapshot after its after snapshot'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $thirdRunPath = Join-Path $unstableFixturePath 'same-player-repeats/run-03/repeat-03-results.xml'
    $thirdRunText = Get-Content -LiteralPath $thirdRunPath -Raw
    $thirdRunText = $thirdRunText.Replace(
        'Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1)',
        'Standalone IL2CPP x64 Debug (WindowsPlayer; Unity 6000.3.16f1)'
    )
    [System.IO.File]::WriteAllText($thirdRunPath, $thirdRunText)
    Assert-StabilityGateFails `
        -Description 'a non-Release repeat platform fails closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'recorded a non-published platform'

    New-StabilityFixture `
        -Path $unstableFixturePath `
        -RunValues @(1000000, 1010000, 1020000) `
        -Scenarios $scenarios
    $thirdRunText = Get-Content -LiteralPath $thirdRunPath -Raw
    $thirdRunText = $thirdRunText.Replace(
        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    )
    [System.IO.File]::WriteAllText($thirdRunPath, $thirdRunText)
    Assert-StabilityGateFails `
        -Description 'a different measured commit in one repeat fails closed' `
        -ArtifactsPath $unstableFixturePath `
        -ExpectedMessage 'did not preserve one platform and measured commit'
} finally {
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

Write-Host 'same-player repeat evidence tests passed'
