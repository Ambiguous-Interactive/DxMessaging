#Requires -Version 5.1
<#
.SYNOPSIS
    Collect privacy-safe hardware specs for the self-hosted Windows perf runner.

.DESCRIPTION
    Emits a compact JSON object and a one-line human summary describing the
    runner's HARDWARE (CPU, cores, clock, RAM, GPU, OS) so the performance doc
    can attribute benchmark numbers to a machine profile WITHOUT revealing the
    runner's identity.

    PRIVACY (hard requirement): this script is the deliberate REPLACEMENT for
    surfacing a runner name. It collects ONLY non-identifying hardware/OS facts
    and never reads or emits any host-identity source -- machine/computer name,
    the runner-name environment variable, network adapter addresses, hardware
    serial numbers, or the logged-in user.

    ROBUSTNESS: every probe is wrapped in try/catch. A failed probe yields the
    literal "unknown" for that field and a warning annotation. The script ALWAYS
    emits valid JSON and ALWAYS exits 0, even if every probe fails, so downstream
    JSON parsing in render-perf-doc.js never crashes.

    Written to be Set-StrictMode -Version Latest safe and Windows PowerShell 5.1
    compatible: no null-coalescing (??), no ternary (?:), no pwsh-only syntax.

.PARAMETER OutputJson
    Optional path to write the compact JSON object. When omitted, JSON is not
    written to a file.

.PARAMETER OutputSummary
    Optional path to write the one-line human summary. When omitted, the summary
    is not written to a file.

.EXAMPLE
    pwsh -NoProfile -File scripts/unity/collect-machine-specs.ps1 `
        -OutputJson .artifacts/machine-specs.json `
        -OutputSummary .artifacts/machine-specs.txt
#>
[CmdletBinding()]
[OutputType([int])]
param(
    [string]$OutputJson,

    [string]$OutputSummary
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-CiWarning {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::warning::$Message"
}

# Coerce any probe result to a trimmed non-empty string, or the literal
# "unknown". Guards $null, empty strings, and whitespace without using ?? / ?:.
function Get-SpecValue {
    param($Value)

    if ($null -eq $Value) {
        return 'unknown'
    }
    $text = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return 'unknown'
    }
    return $text
}

# Map an SMBIOS/Win32_PhysicalMemory memory-type code to a human DDR string.
# SMBIOSMemoryType is the modern field (24 = DDR3, 26 = DDR4, 34 = DDR5); the
# legacy MemoryType field uses different codes (20 = DDR, 21 = DDR2, 24 = DDR3).
# Returns "unknown" for anything unrecognized rather than guessing.
function Convert-MemoryType {
    param(
        $SmbiosMemoryType,
        $MemoryType
    )

    if ($null -ne $SmbiosMemoryType) {
        $smbios = 0
        if ([int]::TryParse(([string]$SmbiosMemoryType), [ref]$smbios)) {
            switch ($smbios) {
                34 { return 'DDR5' }
                26 { return 'DDR4' }
                24 { return 'DDR3' }
                21 { return 'DDR2' }
                default { }
            }
        }
    }

    if ($null -ne $MemoryType) {
        $legacy = 0
        if ([int]::TryParse(([string]$MemoryType), [ref]$legacy)) {
            switch ($legacy) {
                24 { return 'DDR3' }
                21 { return 'DDR2' }
                20 { return 'DDR' }
                default { }
            }
        }
    }

    return 'unknown'
}

function Get-ProcessorSpecs {
    $result = [ordered]@{
        cpu          = 'unknown'
        physicalCores = 'unknown'
        logicalCores = 'unknown'
        clockMhz     = 'unknown'
    }
    try {
        $processors = @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop)
        if ($processors.Count -gt 0) {
            $processor = $processors[0]
            $result['cpu'] = Get-SpecValue $processor.Name
            $result['physicalCores'] = Get-SpecValue $processor.NumberOfCores
            $result['logicalCores'] = Get-SpecValue $processor.NumberOfLogicalProcessors
            $result['clockMhz'] = Get-SpecValue $processor.MaxClockSpeed
        }
    } catch {
        Write-CiWarning "collect-machine-specs: Win32_Processor probe failed: $($_.Exception.Message)"
    }
    return $result
}

function Get-MemorySpecs {
    $result = [ordered]@{
        ramGb       = 'unknown'
        ramSpeedMhz = 'unknown'
        ramType     = 'unknown'
    }
    try {
        $modules = @(Get-CimInstance -ClassName Win32_PhysicalMemory -ErrorAction Stop)
        if ($modules.Count -gt 0) {
            $totalBytes = [double]0
            foreach ($module in $modules) {
                if ($null -ne $module.Capacity) {
                    $capacity = [double]0
                    if ([double]::TryParse(([string]$module.Capacity), [ref]$capacity)) {
                        $totalBytes += $capacity
                    }
                }
            }
            if ($totalBytes -gt 0) {
                $result['ramGb'] = Get-SpecValue ([math]::Round($totalBytes / 1GB))
            }

            $first = $modules[0]
            $result['ramSpeedMhz'] = Get-SpecValue $first.Speed

            $smbios = $null
            if ($first.PSObject.Properties['SMBIOSMemoryType']) {
                $smbios = $first.SMBIOSMemoryType
            }
            $legacy = $null
            if ($first.PSObject.Properties['MemoryType']) {
                $legacy = $first.MemoryType
            }
            $result['ramType'] = Convert-MemoryType -SmbiosMemoryType $smbios -MemoryType $legacy
        }
    } catch {
        Write-CiWarning "collect-machine-specs: Win32_PhysicalMemory probe failed: $($_.Exception.Message)"
    }
    return $result
}

function Get-GpuSpec {
    try {
        $adapters = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop)
        foreach ($adapter in $adapters) {
            $name = Get-SpecValue $adapter.Name
            # Skip the Microsoft Basic Display Adapter / Basic Render Driver so a
            # real GPU is reported when one is present.
            if ($name -ne 'unknown' -and $name -notmatch '(?i)basic (display|render)') {
                return $name
            }
        }
        if ($adapters.Count -gt 0) {
            return Get-SpecValue $adapters[0].Name
        }
    } catch {
        Write-CiWarning "collect-machine-specs: Win32_VideoController probe failed: $($_.Exception.Message)"
    }
    return 'unknown'
}

function Get-OsSpec {
    try {
        $osInstances = @(Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop)
        if ($osInstances.Count -gt 0) {
            $os = $osInstances[0]
            $caption = Get-SpecValue $os.Caption
            $version = Get-SpecValue $os.Version
            if ($caption -eq 'unknown' -and $version -eq 'unknown') {
                return 'unknown'
            }
            if ($version -eq 'unknown') {
                return $caption
            }
            if ($caption -eq 'unknown') {
                return $version
            }
            return "$caption ($version)"
        }
    } catch {
        Write-CiWarning "collect-machine-specs: Win32_OperatingSystem probe failed: $($_.Exception.Message)"
    }
    return 'unknown'
}

function Get-MachineSpecs {
    # These return [ordered] hashtables (single objects, never collections), so the
    # captures are indexed by key below (e.g. $processor['cpu']), not as arrays. The
    # static StrictMode collection-safety gate only sees a bare capture that is later
    # indexed, so suppress it here -- @()-wrapping an [ordered] hashtable would wrap
    # it in a one-element array and break the key indexing.
    $processor = Get-ProcessorSpecs # strictmode-collection-safety: ignore
    $memory = Get-MemorySpecs # strictmode-collection-safety: ignore
    $gpu = Get-GpuSpec
    $os = Get-OsSpec

    return [ordered]@{
        cpu          = $processor['cpu']
        physicalCores = $processor['physicalCores']
        logicalCores = $processor['logicalCores']
        clockMhz     = $processor['clockMhz']
        ramGb        = $memory['ramGb']
        ramSpeedMhz  = $memory['ramSpeedMhz']
        ramType      = $memory['ramType']
        gpu          = $gpu
        os           = $os
    }
}

# One-line human summary. Mirrors render-perf-doc.js formatMachineSpecs() so the
# stdout/file summary matches the doc provenance line shape.
function Format-MachineSpecsSummary {
    param([Parameter(Mandatory = $true)]$Specs)

    return "$($Specs['cpu']), $($Specs['physicalCores'])C/$($Specs['logicalCores'])T @ $($Specs['clockMhz'])MHz; " +
        "$($Specs['ramGb'])GB $($Specs['ramType'])@$($Specs['ramSpeedMhz']); $($Specs['gpu']); $($Specs['os'])"
}

function Write-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    Set-Content -LiteralPath $Path -Value $Content -Encoding UTF8
}

function Invoke-CollectMachineSpecs {
    param(
        [string]$OutputJson,
        [string]$OutputSummary
    )

    $specs = Get-MachineSpecs
    $summary = Format-MachineSpecsSummary -Specs $specs

    if (-not [string]::IsNullOrWhiteSpace($OutputJson)) {
        try {
            $json = ($specs | ConvertTo-Json -Compress -Depth 4)
            Write-TextFile -Path $OutputJson -Content $json
        } catch {
            Write-CiWarning "collect-machine-specs: failed to write JSON to ${OutputJson}: $($_.Exception.Message)"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($OutputSummary)) {
        try {
            Write-TextFile -Path $OutputSummary -Content $summary
        } catch {
            Write-CiWarning "collect-machine-specs: failed to write summary to ${OutputSummary}: $($_.Exception.Message)"
        }
    }

    # Always echo the one-line summary to stdout so callers can capture it even
    # without an output file.
    Write-Host $summary
    return 0
}

$invokedAsScript = $MyInvocation.InvocationName -ne '' -and $MyInvocation.InvocationName -ne '.'

if ($invokedAsScript) {
    try {
        Invoke-CollectMachineSpecs -OutputJson $OutputJson -OutputSummary $OutputSummary | Out-Null
    } catch {
        # Last-resort guard: the whole point is that downstream parsing never
        # crashes, so even a catastrophic failure must still leave valid JSON and
        # exit 0. Emit a neutral fallback object + summary.
        Write-CiWarning "collect-machine-specs: unexpected failure: $($_.Exception.Message)"
        $fallback = [ordered]@{
            cpu          = 'unknown'
            physicalCores = 'unknown'
            logicalCores = 'unknown'
            clockMhz     = 'unknown'
            ramGb        = 'unknown'
            ramSpeedMhz  = 'unknown'
            ramType      = 'unknown'
            gpu          = 'unknown'
            os           = 'unknown'
        }
        $fallbackSummary = 'unknown, unknownC/unknownT @ unknownMHz; unknownGB unknown@unknown; unknown; unknown'
        if (-not [string]::IsNullOrWhiteSpace($OutputJson)) {
            try {
                Write-TextFile -Path $OutputJson -Content ($fallback | ConvertTo-Json -Compress -Depth 4)
            } catch { }
        }
        if (-not [string]::IsNullOrWhiteSpace($OutputSummary)) {
            try {
                Write-TextFile -Path $OutputSummary -Content $fallbackSummary
            } catch { }
        }
        Write-Host $fallbackSummary
    }
    exit 0
}
