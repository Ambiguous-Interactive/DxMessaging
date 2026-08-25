#Requires -Version 5.1
<#
.SYNOPSIS
    Resolves the fastest homogeneous Windows CPU partition for perf players.

.DESCRIPTION
    Reads SYSTEM_CPU_SET_INFORMATION, selects every logical processor in the
    highest numerical EfficiencyClass, derives one group-0 affinity mask, and
    writes the complete topology and selection to JSON. Microsoft documents a
    higher EfficiencyClass as faster and less power-efficient.

    TopologyFixturePath exists only so non-Windows script tests can exercise the
    exact selection and rejection logic without inventing a second implementation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputJson,
    [string]$TopologyFixturePath,
    [string]$CpuModelFixture = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WindowsCpuSetTopology {
    if ($env:OS -cne 'Windows_NT') {
        throw 'GetSystemCpuSetInformation is available only on Windows.'
    }

    if ($null -eq ('DxmCpuSetNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class DxmCpuSetNative
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);

    public static byte[] Query()
    {
        uint requiredLength;
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out requiredLength, new IntPtr(-1), 0);
        if (requiredLength == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "GetSystemCpuSetInformation returned no CPU sets.");
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
        try
        {
            uint returnedLength;
            if (!GetSystemCpuSetInformation(
                buffer, requiredLength, out returnedLength, new IntPtr(-1), 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "GetSystemCpuSetInformation failed.");
            }

            byte[] result = new byte[returnedLength];
            Marshal.Copy(buffer, result, 0, checked((int)returnedLength));
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
'@
    }

    $bytes = [DxmCpuSetNative]::Query()
    $records = [System.Collections.Generic.List[object]]::new()
    $offset = 0
    while ($offset -lt $bytes.Length) {
        if ($offset + 8 -gt $bytes.Length) {
            throw 'CPU-set topology ended before a complete record header.'
        }
        $size = [BitConverter]::ToUInt32($bytes, $offset)
        $type = [BitConverter]::ToUInt32($bytes, $offset + 4)
        if ($size -lt 8 -or $offset + $size -gt $bytes.Length) {
            throw "CPU-set topology contains invalid record size $size at offset $offset."
        }
        if ($type -eq 0) {
            if ($size -lt 20) {
                throw "CPU-set record at offset $offset is too small ($size bytes)."
            }
            $flags = $bytes[$offset + 19]
            $records.Add([ordered]@{
                id = [BitConverter]::ToUInt32($bytes, $offset + 8)
                group = [BitConverter]::ToUInt16($bytes, $offset + 12)
                logicalProcessorIndex = $bytes[$offset + 14]
                coreIndex = $bytes[$offset + 15]
                lastLevelCacheIndex = $bytes[$offset + 16]
                numaNodeIndex = $bytes[$offset + 17]
                efficiencyClass = $bytes[$offset + 18]
                parked = ($flags -band 0x01) -ne 0
                allocated = ($flags -band 0x02) -ne 0
                allocatedToTargetProcess = ($flags -band 0x04) -ne 0
            })
        }
        $offset += $size
    }
    return @($records.ToArray())
}

function Resolve-PerformanceCpuProfile {
    param(
        [Parameter(Mandatory = $true)][object[]]$CpuSets,
        [Parameter(Mandatory = $true)][string]$CpuModel
    )

    $records = @($CpuSets)
    if ($records.Count -eq 0) {
        throw 'CPU-set topology contains no CPU sets.'
    }
    $groups = @($records | ForEach-Object { [int]$_.group } | Sort-Object -Unique)
    if ($groups.Count -ne 1 -or $groups[0] -ne 0) {
        throw "The process-affinity profile requires one processor group 0; found '$($groups -join ',')'."
    }
    $logicalIndices = @($records | ForEach-Object { [int]$_.logicalProcessorIndex })
    if (@($logicalIndices | Sort-Object -Unique).Count -ne $records.Count) {
        throw 'CPU-set topology contains duplicate logical processor indices.'
    }
    if (@($logicalIndices | Where-Object { $_ -lt 0 -or $_ -gt 62 }).Count -ne 0) {
        throw 'The process-affinity profile supports logical processor indices 0 through 62.'
    }

    $highestEfficiencyClass = [int](
        ($records | Measure-Object -Property efficiencyClass -Maximum).Maximum
    )
    $selected = @($records | Where-Object {
        [int]$_.efficiencyClass -eq $highestEfficiencyClass
    })
    $allocatedElsewhere = @($selected | Where-Object {
        [bool]$_.allocated -and -not [bool]$_.allocatedToTargetProcess
    })
    if ($allocatedElsewhere.Count -ne 0) {
        throw 'A selected high-performance CPU set is allocated to another process.'
    }

    [uint64]$affinityMask = 0
    foreach ($record in $selected) {
        $affinityMask = $affinityMask -bor ([uint64]1 -shl [int]$record.logicalProcessorIndex)
    }
    if ($affinityMask -eq 0 -or $affinityMask -gt [uint64][long]::MaxValue) {
        throw 'The selected high-performance CPU sets did not produce a valid Int64 affinity mask.'
    }

    $efficiencyClasses = @($records |
        Group-Object -Property efficiencyClass |
        Sort-Object { [int]$_.Name } |
        ForEach-Object {
            [ordered]@{ value = [int]$_.Name; logicalProcessorCount = $_.Count }
        })
    $selectedCoreCount = @(
        $selected | ForEach-Object { [int]$_.coreIndex } | Sort-Object -Unique
    ).Count
    return [ordered]@{
        schemaVersion = 1
        executionProfileId = 'highest-efficiency-class-affinity-normal-v1'
        source = 'GetSystemCpuSetInformation'
        selectionPolicy = 'maximum EfficiencyClass'
        cpuModel = $CpuModel
        processorGroup = 0
        logicalProcessorCount = $records.Count
        efficiencyClasses = $efficiencyClasses
        selectedEfficiencyClass = $highestEfficiencyClass
        selectedLogicalProcessorCount = $selected.Count
        selectedCoreCount = $selectedCoreCount
        selectedLogicalProcessorIndices = @(
            $selected | ForEach-Object { [int]$_.logicalProcessorIndex } | Sort-Object
        )
        affinityMask = '0x{0:X}' -f $affinityMask
        priorityClass = 'Normal'
        cpuSets = $records
    }
}

if (-not [string]::IsNullOrWhiteSpace($TopologyFixturePath)) {
    $cpuSets = @(
        Get-Content -LiteralPath $TopologyFixturePath -Raw | ConvertFrom-Json
    )
    $cpuModel = $CpuModelFixture
} else {
    $cpuSets = @(Get-WindowsCpuSetTopology)
    $processors = @(Get-CimInstance Win32_Processor)
    $cpuModel = @($processors | ForEach-Object { ([string]$_.Name).Trim() }) -join '; '
}
if ([string]::IsNullOrWhiteSpace($cpuModel)) {
    throw 'CPU model evidence is empty.'
}

$profile = Resolve-PerformanceCpuProfile -CpuSets $cpuSets -CpuModel $cpuModel
$outputDirectory = Split-Path -Parent $OutputJson
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
$json = $profile | ConvertTo-Json -Depth 8
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($OutputJson, $json + [Environment]::NewLine, $utf8NoBom)
Write-Host (
    "Resolved performance CPU profile '$($profile.executionProfileId)': " +
    "class=$($profile.selectedEfficiencyClass), logical=$($profile.selectedLogicalProcessorCount), " +
    "cores=$($profile.selectedCoreCount), mask=$($profile.affinityMask), priority=$($profile.priorityClass)."
)
