#Requires -Version 7.0
# cspell:ignore powrprof
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [ValidateRange(2, 600)][int]$SampleCount = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows) {
    throw 'Performance host characterization requires Windows.'
}

# This is a manual, outcome-free availability and cadence probe. Numerical limits are
# frozen only after inspecting this artifact and before any pilot player is launched.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DxmPowerInformation
{
    // POWER_INFORMATION_LEVEL values 11, 14, and 15 and the six-ULONG processor
    // record follow Microsoft's CallNtPowerInformation and PROCESSOR_POWER_INFORMATION APIs.
    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int informationLevel, IntPtr inputBuffer, uint inputLength,
        IntPtr outputBuffer, uint outputLength);

    public static long ReadInterruptTime(int informationLevel)
    {
        IntPtr output = Marshal.AllocHGlobal(8);
        try
        {
            uint status = CallNtPowerInformation(informationLevel, IntPtr.Zero, 0, output, 8);
            if (status != 0) throw new InvalidOperationException("CallNtPowerInformation status " + status);
            return Marshal.ReadInt64(output);
        }
        finally { Marshal.FreeHGlobal(output); }
    }

    public static uint[][] ReadProcessors(int count)
    {
        const int size = 24;
        IntPtr output = Marshal.AllocHGlobal(checked(count * size));
        try
        {
            uint status = CallNtPowerInformation(11, IntPtr.Zero, 0, output, checked((uint)(count * size)));
            if (status != 0) throw new InvalidOperationException("CallNtPowerInformation status " + status);
            uint[][] processors = new uint[count][];
            for (int index = 0; index < count; index++)
            {
                processors[index] = new uint[6];
                for (int field = 0; field < 6; field++)
                    processors[index][field] = unchecked((uint)Marshal.ReadInt32(output, index * size + field * 4));
            }
            return processors;
        }
        finally { Marshal.FreeHGlobal(output); }
    }
}
'@

function Invoke-PowerCfg {
    param([string[]]$Arguments)
    $raw = (& powercfg @Arguments 2>&1 | Out-String).Trim()
    return [ordered]@{ exitCode = $LASTEXITCODE; text = $raw }
}

function Get-PowerState {
    $active = Invoke-PowerCfg -Arguments @('/getactivescheme')
    $query = Invoke-PowerCfg -Arguments @('/query', 'SCHEME_CURRENT', 'SUB_PROCESSOR')
    $normalized = $query.text.Replace("`r`n", "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalized)
    $sha = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [ordered]@{
        active = $active
        processorQuery = $query
        processorQuerySha256 = [Convert]::ToHexString($sha).ToLowerInvariant()
    }
}

function Get-NativePowerState {
    $errors = New-Object System.Collections.Generic.List[string]
    $sleep = $null
    $wake = $null
    $processors = @()
    try { $sleep = [DxmPowerInformation]::ReadInterruptTime(15) } catch { $errors.Add("lastSleep: $($_.Exception.Message)") }
    try { $wake = [DxmPowerInformation]::ReadInterruptTime(14) } catch { $errors.Add("lastWake: $($_.Exception.Message)") }
    try {
        $raw = [DxmPowerInformation]::ReadProcessors([Environment]::ProcessorCount)
        $processors = @($raw | ForEach-Object {
            [ordered]@{
                number = $_[0]; maxMhz = $_[1]; currentMhz = $_[2]
                mhzLimit = $_[3]; maxIdleState = $_[4]; currentIdleState = $_[5]
            }
        })
    } catch { $errors.Add("processorInformation: $($_.Exception.Message)") }
    return [ordered]@{
        lastSleepInterruptTime100ns = $sleep
        lastWakeInterruptTime100ns = $wake
        processors = $processors
        errors = @($errors.ToArray())
    }
}

function Get-SleepEvidence {
    $errors = New-Object System.Collections.Generic.List[string]
    $events = New-Object System.Collections.Generic.List[object]
    $queries = New-Object System.Collections.Generic.List[object]
    $bootTimeUtc = $null
    try {
        $system = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $bootTimeUtc = $system.LastBootUpTime.ToUniversalTime().ToString('O')
    } catch { $errors.Add("lastBootUpTime: $($_.Exception.Message)") }
    foreach ($filter in @(
        @{ ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 42 },
        @{ ProviderName = 'Microsoft-Windows-Power-Troubleshooter'; Id = 1 }
    )) {
        $status = 'ok'
        try {
            $query = @{
                LogName = 'System'
                ProviderName = $filter.ProviderName
                Id = $filter.Id
                StartTime = (Get-Date).AddDays(-7)
            }
            Get-WinEvent -FilterHashtable $query -MaxEvents 100 -ErrorAction Stop | ForEach-Object {
                $events.Add([ordered]@{
                        provider = $_.ProviderName
                        id = $_.Id
                        recordId = $_.RecordId
                        timeUtc = $_.TimeCreated.ToUniversalTime().ToString('O')
                    })
            }
        } catch {
            if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') {
                $status = 'no-matches'
            } else {
                $status = 'error'
                $errors.Add("$($filter.ProviderName) event query: $($_.Exception.Message)")
            }
        }
        $queries.Add([ordered]@{ provider = $filter.ProviderName; id = $filter.Id; status = $status })
    }
    return [ordered]@{
        lastBootUpTimeUtc = $bootTimeUtc
        recentSleepWakeEvents = @($events.ToArray())
        queries = @($queries.ToArray())
        errors = @($errors.ToArray())
    }
}

function Get-SensorSample {
    $errors = New-Object System.Collections.Generic.List[string]
    $processorCounters = @()
    $thermalZones = @()
    try {
        $members = @(
            'ProcessorFrequency', 'PercentMaximumFrequency', 'PercentProcessorPerformance',
            'PercentPerformanceLimit', 'PerformanceLimitFlags', 'PercentProcessorTime'
        )
        $processorCounters = @(Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ProcessorInformation -ErrorAction Stop | ForEach-Object {
            $item = [ordered]@{ name = [string]$_.Name }
            foreach ($member in $members) {
                $property = $_.PSObject.Properties[$member]
                if ($null -ne $property) { $item[$member] = $property.Value }
            }
            $item
        })
    } catch { $errors.Add("processorCounters: $($_.Exception.Message)") }
    try {
        $thermalZones = @(Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction Stop | ForEach-Object {
            [ordered]@{ instanceName = [string]$_.InstanceName; rawTenthsKelvin = $_.CurrentTemperature }
        })
    } catch { $errors.Add("acpiThermalZones: $($_.Exception.Message)") }
    return [ordered]@{
        timestampUtc = [DateTime]::UtcNow.ToString('O')
        processorCounters = $processorCounters
        acpiThermalZones = $thermalZones
        nativePower = Get-NativePowerState
        errors = @($errors.ToArray())
    }
}

$startedUtc = [DateTime]::UtcNow.ToString('O')
$before = Get-PowerState
$sleepBefore = Get-SleepEvidence
$samples = New-Object System.Collections.Generic.List[object]
$clock = [System.Diagnostics.Stopwatch]::StartNew()
for ($index = 0; $index -lt $SampleCount; $index++) {
    $samples.Add((Get-SensorSample))
    $remaining = ($index + 1) - $clock.Elapsed.TotalSeconds
    if ($index + 1 -lt $SampleCount -and $remaining -gt 0) {
        Start-Sleep -Milliseconds ([int][Math]::Ceiling($remaining * 1000))
    }
}
$after = Get-PowerState
$sleepAfter = Get-SleepEvidence
$nativeAfter = Get-NativePowerState
$record = [ordered]@{
    schemaVersion = 1
    purpose = 'outcome-blind-host-characterization'
    sourceSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    gitCommit = $env:GITHUB_SHA
    runId = $env:GITHUB_RUN_ID
    runAttempt = $env:GITHUB_RUN_ATTEMPT
    startedUtc = $startedUtc
    endedUtc = [DateTime]::UtcNow.ToString('O')
    hostName = [Environment]::MachineName
    osVersion = [Environment]::OSVersion.VersionString
    powerShellVersion = $PSVersionTable.PSVersion.ToString()
    processorCount = [Environment]::ProcessorCount
    requestedSampleCount = $SampleCount
    requestedCadenceSeconds = 1
    powerBefore = $before
    powerAfter = $after
    sleepBefore = $sleepBefore
    sleepAfter = $sleepAfter
    nativeAfter = $nativeAfter
    samples = @($samples.ToArray())
}
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Host characterization: $OutputPath ($SampleCount samples)"
