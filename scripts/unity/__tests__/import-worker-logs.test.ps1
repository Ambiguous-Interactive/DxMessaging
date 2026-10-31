#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runner = Join-Path (Split-Path -Parent $PSScriptRoot) 'run-ci-tests.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if (@($errors).Count) { throw ($errors.Message -join '; ') }
foreach ($definition in $ast.EndBlock.Statements) {
    if ($definition -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
        Invoke-Expression $definition.Extent.Text
    }
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("dxm-worker-logs-" + [guid]::NewGuid().ToString('N'))
$oldTimestamp = [DateTime]::Parse('2020-01-02T03:04:05Z').ToUniversalTime()
try {
    foreach ($count in @(0, 1, 3)) {
        $project = Join-Path $fixture "project-$count"
        $logs = Join-Path $project 'Logs'
        $artifacts = Join-Path $fixture "artifacts-$count"
        New-Item -ItemType Directory -Path $logs -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $logs 'unrelated.log'), 'outside the worker selection')
        New-Item -ItemType Directory -Path (Join-Path $logs 'AssetImportWorkerDirectory.log') | Out-Null
        for ($index = 0; $index -lt $count; $index++) {
            $path = Join-Path $logs "AssetImportWorkerHW$index.log"
            [IO.File]::WriteAllBytes($path, [byte[]]@(0, 10, 13, 65, 128, 255))
            [IO.File]::SetLastWriteTimeUtc($path, $oldTimestamp)
        }
        Copy-UnityImportWorkerLogs -Project $project -Artifacts $artifacts
        $destination = Join-Path $artifacts 'import-worker-logs'
        $manifest = Get-Content -LiteralPath (Join-Path $destination 'snapshot.json') -Raw | ConvertFrom-Json
        if ($manifest.currentInvocationMembershipEstablished -or
            $manifest.scope -ne 'post-run-reused-project-snapshot' -or
            $manifest.hashesDescribe -ne 'copied bytes before ordinary artifact processing' -or
            @($manifest.files).Count -ne $count -or
            @(Get-ChildItem -LiteralPath $destination -File).Count -ne ($count + 1)) {
            throw "Worker snapshot membership/provenance mismatch for $count files."
        }
        foreach ($record in $manifest.files) {
            $source = Join-Path $logs $record.name
            $copy = Join-Path $destination $record.name
            $expectedHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($record.preProcessingSha256 -ne $expectedHash -or
                (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash -or
                $record.preProcessingLength -ne 6 -or
                $record.sourceLengthObserved -ne 6 -or
                $record.sourceLastWriteTimeUtc -ne $oldTimestamp.ToString('o') -or
                -not $record.sourceMetadataUnchangedDuringCopy) {
                throw "Worker bytes, hash or historical timestamp lost for $($record.name)."
            }
        }
        Get-ChildItem -LiteralPath $logs -File -Filter 'AssetImportWorker*.log' | Remove-Item -Force
        Copy-UnityImportWorkerLogs -Project $project -Artifacts $artifacts
        $reused = Get-Content -LiteralPath (Join-Path $destination 'snapshot.json') -Raw | ConvertFrom-Json
        if (@($reused.files).Count -ne 0 -or
            @(Get-ChildItem -LiteralPath $destination -File).Count -ne 1) {
            throw "Reused artifacts retained stale worker logs after collecting $count files."
        }
    }

    $missingArtifacts = Join-Path $fixture 'missing-artifacts'
    Copy-UnityImportWorkerLogs -Project (Join-Path $fixture 'missing-project') -Artifacts $missingArtifacts
    $empty = Get-Content -LiteralPath (Join-Path $missingArtifacts 'import-worker-logs/snapshot.json') -Raw | ConvertFrom-Json
    if (@($empty.files).Count -ne 0) { throw 'Missing Logs directory fabricated worker evidence.' }

    # Execute the real finalization block after a deliberate failure. Collection
    # errors must neither replace it nor bypass the independent local return.
    $finalization = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [System.Management.Automation.Language.TryStatementAst]
    })[-1].Finally
    $finalBody = [scriptblock]::Create(($finalization.Statements.Extent.Text -join [Environment]::NewLine))
    function Invoke-UnityLicenseReturn { $script:returnObserved = $true }
    $hasLicenseCreds = $true
    $UnityEditorPath = 'unused-owned-fixture'
    $returnLogPath = Join-Path $fixture 'unused-return.log'
    $ProjectPath = Join-Path $fixture 'project-3'
    $ArtifactsPath = Join-Path $fixture 'blocked-output'
    [IO.File]::WriteAllText($ArtifactsPath, 'a file cannot contain the snapshot directory')
    $blockedOutput = $ArtifactsPath
    foreach ($LicenseReturnOwner in @('Local', 'Central')) {
        foreach ($blocked in @($false, $true)) {
            $ArtifactsPath = if ($blocked) { $blockedOutput } else { Join-Path $fixture "final-$LicenseReturnOwner" }
            $script:returnObserved = $false
            $caught = $null
            try {
                try { throw 'original fixture failure' }
                finally { . $finalBody }
            } catch { $caught = $_.Exception.Message }
            if ($caught -ne 'original fixture failure' -or
                $script:returnObserved -ne ($LicenseReturnOwner -eq 'Local')) {
                throw "Collection changed the original failure or $LicenseReturnOwner return ownership."
            }
            if (-not $blocked -and
                -not (Test-Path -LiteralPath (Join-Path $ArtifactsPath 'import-worker-logs/snapshot.json') -PathType Leaf)) {
                throw "The real $LicenseReturnOwner finalization did not retain the worker snapshot."
            }
        }
    }
    Write-Host 'Import worker log snapshot regression checks passed.'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
