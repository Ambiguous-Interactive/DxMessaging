param([switch]$SelfTest)

Set-StrictMode -Version Latest

$MaxEvidenceBytes = 25 * 1024 * 1024
$EvidenceExtensions = @('.log', '.txt')

function Get-UnityAccountHealthEvidenceFiles {
    [CmdletBinding()]
    param([string[]]$CandidatePaths)

    $files = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new(
        [System.StringComparer]::Ordinal
    )

    function Visit-UnityEvidencePath {
        param([Parameter(Mandatory = $true)][string]$CandidatePath)

        try {
            $item = Get-Item -LiteralPath $CandidatePath -Force -ErrorAction Stop
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                return
            }
            if ($item.PSIsContainer) {
                foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop)) {
                    Visit-UnityEvidencePath -CandidatePath $child.FullName
                }
                return
            }
            if (
                $item -is [System.IO.FileInfo] -and
                $item.Length -le $MaxEvidenceBytes -and
                $EvidenceExtensions -contains $item.Extension.ToLowerInvariant()
            ) {
                $files[$item.FullName] = $item
            }
        } catch {
            Write-Host '::warning::Could not inspect a sanitized Unity evidence path; account health remains healthy.'
        }
    }

    foreach ($candidatePath in @($CandidatePaths)) {
        $trimmedPath = ([string]$candidatePath).Trim()
        if (-not [string]::IsNullOrWhiteSpace($trimmedPath)) {
            Visit-UnityEvidencePath -CandidatePath $trimmedPath
        }
    }
    return @($files.Values | Sort-Object -Property FullName)
}

function Get-UnityAccountHealthClassification {
    [CmdletBinding()]
    param(
        [System.IO.FileInfo[]]$EvidenceFiles,
        [string]$HealthyReason = 'return-missing-positive-evidence'
    )

    $digest = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256
    )
    $accountBlocked = $false
    try {
        foreach ($evidenceFile in @($EvidenceFiles)) {
            try {
                $data = [System.IO.File]::ReadAllBytes($evidenceFile.FullName)
                $digest.AppendData([System.Text.Encoding]::UTF8.GetBytes($evidenceFile.Name))
                $digest.AppendData([byte[]]@(0))
                $digest.AppendData($data)
                if ([System.Text.Encoding]::UTF8.GetString($data) -cmatch '(?<![0-9])20111(?![0-9])') {
                    $accountBlocked = $true
                }
            } catch {
                Write-Host '::warning::Could not inspect a sanitized Unity evidence file; account health remains unchanged.'
            }
        }
        $evidenceDigest = [System.Convert]::ToHexString($digest.GetHashAndReset()).ToLowerInvariant()
    } finally {
        $digest.Dispose()
    }

    if ($accountBlocked) {
        return [pscustomobject]@{
            Health = 'blocked'
            Reason = 'unity-account-limit-20111'
            Digest = $evidenceDigest
        }
    }
    return [pscustomobject]@{
        Health = 'healthy'
        Reason = $HealthyReason
        Digest = $evidenceDigest
    }
}

if ($SelfTest) {
    $fixtures = @(
        @{ Text = "Licensing failed with error code 20111`n"; Health = 'blocked'; Reason = 'unity-account-limit-20111' }
        @{ Text = "[Licensing] Error [20111]: activation limit reached`n"; Health = 'blocked'; Reason = 'unity-account-limit-20111' }
        @{ Text = "Licensing failed with error code 20113`n"; Health = 'healthy'; Reason = 'return-missing-positive-evidence' }
        @{ Text = "Licensing failed with error code 400006`n"; Health = 'healthy'; Reason = 'return-missing-positive-evidence' }
        @{ Text = "Diagnostic identifier 1201119`n"; Health = 'healthy'; Reason = 'return-missing-positive-evidence' }
    )
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dxm-unity-health-" + [guid]::NewGuid())
    try {
        New-Item -ItemType Directory -Path $tempRoot | Out-Null
        $evidencePath = Join-Path $tempRoot 'unity.log'
        foreach ($fixture in $fixtures) {
            [System.IO.File]::WriteAllText($evidencePath, $fixture.Text)
            $files = Get-UnityAccountHealthEvidenceFiles -CandidatePaths @("  $tempRoot  ")
            $classification = Get-UnityAccountHealthClassification -EvidenceFiles $files
            if (
                $classification.Health -cne $fixture.Health -or
                $classification.Reason -cne $fixture.Reason -or
                $classification.Digest -cnotmatch '^[0-9a-f]{64}$'
            ) {
                throw "Unexpected classification: $($classification.Health)/$($classification.Reason)/$($classification.Digest)"
            }
        }
        [System.IO.File]::WriteAllText((Join-Path $tempRoot 'ignored.json'), '20111')
        $classification = Get-UnityAccountHealthClassification `
            -EvidenceFiles (Get-UnityAccountHealthEvidenceFiles -CandidatePaths @($tempRoot)) `
            -HealthyReason 'cleanup-confirmed'
        if ($classification.Health -cne 'healthy' -or $classification.Reason -cne 'cleanup-confirmed') {
            throw 'Unsupported evidence extensions must be ignored.'
        }
        if (-not $IsWindows) {
            [System.IO.File]::WriteAllText((Join-Path $tempRoot 'case.log'), 'healthy')
            [System.IO.File]::WriteAllText((Join-Path $tempRoot 'CASE.log'), '20111')
            $caseFiles = Get-UnityAccountHealthEvidenceFiles -CandidatePaths @($tempRoot)
            $caseClassification = Get-UnityAccountHealthClassification -EvidenceFiles $caseFiles
            if ($caseFiles.Count -lt 3 -or $caseClassification.Health -cne 'blocked') {
                throw 'Case-distinct evidence files must remain distinct on case-sensitive filesystems.'
            }
        }
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host 'Unity account-health classifier self-test passed.'
    exit 0
}

$outputPath = $env:GITHUB_OUTPUT
if ([string]::IsNullOrWhiteSpace($outputPath)) {
    throw 'GitHub output path is required.'
}
$candidatePaths = @($env:EVIDENCE_PATHS -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$healthyReason = if ([string]::IsNullOrWhiteSpace($env:HEALTHY_REASON)) {
    'return-missing-positive-evidence'
} else {
    $env:HEALTHY_REASON
}
$classification = Get-UnityAccountHealthClassification `
    -EvidenceFiles (Get-UnityAccountHealthEvidenceFiles -CandidatePaths $candidatePaths) `
    -HealthyReason $healthyReason
"resource-health=$($classification.Health)" | Out-File -FilePath $outputPath -Append
"resource-reason=$($classification.Reason)" | Out-File -FilePath $outputPath -Append
"evidence-digest=$($classification.Digest)" | Out-File -FilePath $outputPath -Append
Write-Host "::notice::Unity account-health classification reason=$($classification.Reason) evidence-digest=$($classification.Digest)"
if ($classification.Health -ceq 'blocked') {
    Write-Host '::error title=Unity account blocked::Observed unity-account-limit-20111; central admission will stop when schema 5 is active.'
}
