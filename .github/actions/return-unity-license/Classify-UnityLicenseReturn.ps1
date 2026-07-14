param([switch]$SelfTest)

Set-StrictMode -Version Latest

function Test-UnityLicenseReturnResourceSafe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    if ($ExitCode -in @(137, 143, -1073741510, -1073740791)) {
        return $false
    }

    try {
        if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
            return $false
        }

        $entitlementReturned = $false
        $ulfReturned = $false
        foreach ($line in (Get-Content -LiteralPath $LogPath -ErrorAction Stop)) {
            $normalized = ([string]$line).Trim()
            if (
                $normalized -ceq 'Successfully returned the entitlement license' -or
                $normalized -ceq '[Licensing::Module] Successfully returned the entitlement license'
            ) {
                $entitlementReturned = $true
            }
            if (
                $normalized -ceq 'Serial number unavailable for ULF return' -or
                $normalized -cmatch '^\[Licensing::Client\] Successfully returned ULF license with serial number\s*:\s*\S+$'
            ) {
                $ulfReturned = $true
            }
        }

        return $entitlementReturned -and $ulfReturned
    } catch {
        return $false
    }
}

if ($SelfTest) {
    $exact = "Successfully returned the entitlement license`nSerial number unavailable for ULF return`n"
    $explicit = "[Licensing::Module] Successfully returned the entitlement license`n[Licensing::Client] Successfully returned ULF license with serial number: REDACTED`n"
    $fixtures = @(
        @{ Name = 'entitlement and legacy absence'; ExitCode = 0; Log = $exact; Expected = $true }
        @{ Name = 'entitlement and explicit ULF return'; ExitCode = 0; Log = $explicit; Expected = $true }
        @{ Name = 'exit zero alone'; ExitCode = 0; Log = "Exiting batchmode successfully now!`n"; Expected = $false }
        @{ Name = 'serial unavailable alone'; ExitCode = 0; Log = "Serial number unavailable for ULF return`n"; Expected = $false }
        @{ Name = 'entitlement alone'; ExitCode = 0; Log = "Successfully returned the entitlement license`n"; Expected = $false }
        @{ Name = 'terminated process'; ExitCode = 143; Log = $exact; Expected = $false }
    )
    $logPath = [System.IO.Path]::GetTempFileName()
    try {
        foreach ($fixture in $fixtures) {
            [System.IO.File]::WriteAllText($logPath, $fixture.Log)
            $actual = Test-UnityLicenseReturnResourceSafe -ExitCode $fixture.ExitCode -LogPath $logPath
            if ($actual -ne $fixture.Expected) {
                throw "Unexpected cleanup classification for $($fixture.Name): $actual"
            }
        }
    } finally {
        Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
    }
    Write-Host 'Unity cleanup classifier self-test passed.'
}
