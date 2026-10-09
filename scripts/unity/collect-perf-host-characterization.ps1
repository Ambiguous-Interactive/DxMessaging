#Requires -Version 7.0
# cspell:ignore powrprof
[CmdletBinding(DefaultParameterSetName = 'Sensors')]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'Sensors')][string]$CpuProfilePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'EventLogs')][switch]$EventLogsOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'EventLogs')]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$')]
    [string]$EventStartUtc,
    [Parameter(Mandatory = $true, ParameterSetName = 'EventLogs')]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$')]
    [string]$EventEndUtc,
    [Parameter(Mandatory = $true, ParameterSetName = 'PackageLogs')][switch]$PackageLogsOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'PackageLogs')][string]$ProjectPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'PackageLogs')][string]$CachePath,
    [Parameter(ParameterSetName = 'PackageLogs')][string]$InstalledDiagnosticsPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'RegistryDownload')][switch]$RegistryDownloadOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'RegistryDownload')]
    [ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedArchiveSha1,
    [Parameter(ParameterSetName = 'RegistryDownload')]
    [ValidateSet('Gateway', 'Cdn')][string]$RegistryArchiveRoute = 'Gateway',
    [Parameter(ParameterSetName = 'Sensors')][switch]$CpuLoad,
    [Parameter(ParameterSetName = 'Sensors')][ValidateRange(2, 3600)][int]$SampleCount = 120,
    [Parameter(ParameterSetName = 'Sensors')][string]$StopSignalPath,
    [Parameter(ParameterSetName = 'Sensors')][string]$ReadySignalPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows) {
    throw 'Performance host characterization requires Windows.'
}
function Get-HostEventEvidence {
    param(
        [Parameter(Mandatory = $true)][DateTimeOffset]$StartUtc,
        [Parameter(Mandatory = $true)][DateTimeOffset]$EndUtc
    )
    if ($EndUtc -le $StartUtc -or ($EndUtc - $StartUtc).TotalHours -gt 24) {
        throw 'Event log window must be increasing and no longer than 24 hours.'
    }
    $events = New-Object System.Collections.Generic.List[object]
    $queries = New-Object System.Collections.Generic.List[object]
    $errors = New-Object System.Collections.Generic.List[string]
    foreach ($filter in @(
        @{ ProviderName = 'Microsoft-Windows-Kernel-General'; Id = 1 },
        @{ ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 42 },
        @{ ProviderName = 'Microsoft-Windows-Power-Troubleshooter'; Id = 1 }
    )) {
        $status = 'ok'
        $count = 0
        try {
            $query = @{
                LogName = 'System'; ProviderName = $filter.ProviderName; Id = $filter.Id
                StartTime = $StartUtc.UtcDateTime; EndTime = $EndUtc.UtcDateTime
            }
            $matches = @(Get-WinEvent -FilterHashtable $query -MaxEvents 1001 -ErrorAction Stop)
            if ($matches.Count -gt 1000) {
                $status = 'truncated'
                $errors.Add("$($filter.ProviderName): more than 1000 events; narrow the window.")
            }
            foreach ($event in @($matches | Select-Object -First 1000)) {
                $events.Add([ordered]@{
                    provider = $event.ProviderName; id = $event.Id; recordId = $event.RecordId
                    timeUtc = $event.TimeCreated.ToUniversalTime().ToString('O')
                    xml = $event.ToXml()
                })
                $count++
            }
            if ($matches.Count -eq 0) { $status = 'no-matches' }
        } catch {
            if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') {
                $status = 'no-matches'
            } else {
                $status = 'error'
                $errors.Add("$($filter.ProviderName): $($_.FullyQualifiedErrorId): $($_.Exception.Message)")
            }
        }
        $queries.Add([ordered]@{
            provider = $filter.ProviderName; id = $filter.Id; status = $status; eventCount = $count
        })
    }
    return [ordered]@{
        startUtc = $StartUtc.ToUniversalTime().ToString('O')
        endUtc = $EndUtc.ToUniversalTime().ToString('O')
        queries = @($queries.ToArray()); events = @($events.ToArray()); errors = @($errors.ToArray())
    }
}

function Get-RegistryArchiveEvidence {
    param(
        [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSha1,
        [ValidateSet('Gateway', 'Cdn')][string]$ArchiveRoute = 'Gateway',
        [System.Net.Http.HttpClient]$Client
    )
    $archiveUrl = if ($ArchiveRoute -eq 'Cdn') {
        'https://cdn.packages.unity.com/tarballs/com.unity.burst/com.unity.burst-1.6.6/da63315718cf3bf3d11ff958633b4b67dc8d2426.tgz'
    } else {
        'https://download.packages.unity.com/com.unity.burst/-/com.unity.burst-1.6.6.tgz'
    }
    $record = [ordered]@{
        archiveRoute = $ArchiveRoute; url = $archiveUrl
        expectedSha1 = $ExpectedSha1; maximumBytes = 512L * 1024 * 1024; deadlineSeconds = 120
        startedUtc = [DateTime]::UtcNow.ToString('O'); status = 'failed'
        httpStatus = $null; declaredLengthBytes = $null; bytesReceived = 0L
        eofReached = $false; bodyComplete = $false; sha1 = $null; sha256 = $null; digestMatches = $false
        errorType = $null; error = $null
    }
    $ownedClient = $null -eq $Client
    if ($ownedClient) {
        $handler = [Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $Client = [Net.Http.HttpClient]::new($handler)
        $Client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    }
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(120))
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $record.url)
    $response = $null; $stream = $null; $sha1 = $null; $sha256 = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = $Client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $deadline.Token).GetAwaiter().GetResult()
        $record.httpStatus = [int]$response.StatusCode
        $record.declaredLengthBytes = $response.Content.Headers.ContentLength
        if ($record.httpStatus -ne 200) { throw "HTTP status $($record.httpStatus) did not supply the archive." }
        if ($null -ne $record.declaredLengthBytes -and $record.declaredLengthBytes -gt $record.maximumBytes) {
            throw 'Declared archive length exceeds the fixed byte cap.'
        }
        $sha1 = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
        $sha256 = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buffer = [byte[]]::new(64 * 1024)
        while ($true) {
            # The same deadline covers body reads; HttpClient headers completion alone does not.
            $count = $stream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()
            if ($count -eq 0) { break }
            if ($record.bytesReceived + $count -gt $record.maximumBytes) {
                throw 'Archive body exceeds the fixed byte cap.'
            }
            $sha1.AppendData($buffer, 0, $count); $sha256.AppendData($buffer, 0, $count)
            $record.bytesReceived += $count
        }
        $record.eofReached = $true
        if ($record.bytesReceived -eq 0 -or
            ($null -ne $record.declaredLengthBytes -and $record.bytesReceived -ne $record.declaredLengthBytes)) {
            throw 'Archive body is empty or does not match its declared length.'
        }
        $record.bodyComplete = $true
    } catch {
        $record.errorType = $_.Exception.GetType().FullName
        $record.error = $_.Exception.ToString()
    } finally {
        $timer.Stop(); $record.elapsedSeconds = $timer.Elapsed.TotalSeconds
        $record.completedUtc = [DateTime]::UtcNow.ToString('O')
        if ($sha1) {
            $record.sha1 = [BitConverter]::ToString($sha1.GetHashAndReset()).Replace('-', '').ToLowerInvariant()
            $sha1.Dispose()
        }
        if ($sha256) {
            $record.sha256 = [BitConverter]::ToString($sha256.GetHashAndReset()).Replace('-', '').ToLowerInvariant()
            $sha256.Dispose()
        }
        if ($stream) { $stream.Dispose() }
        if ($response) { $response.Dispose() }
        $request.Dispose(); $deadline.Dispose()
        if ($ownedClient) { $Client.Dispose() }
    }
    $record.digestMatches = $record.bodyComplete -and $record.sha1 -ceq $ExpectedSha1
    if ($record.bodyComplete -and $record.digestMatches -and $null -eq $record.error) {
        $record.status = 'success'
    } elseif ($null -eq $record.error) {
        $record.errorType = 'DigestMismatch'
        $record.error = 'Complete archive digest does not match the registered SHA1.'
    }
    return $record
}

if ($RegistryDownloadOnly) {
    $evidence = Get-RegistryArchiveEvidence -ExpectedSha1 $ExpectedArchiveSha1 -ArchiveRoute $RegistryArchiveRoute
    $record = [ordered]@{
        schemaVersion = 1; purpose = 'fixed-unity-registry-download-diagnostic'
        capturedUtc = [DateTime]::UtcNow.ToString('O'); hostName = [Environment]::MachineName
        sourceSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        gitCommit = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
        client = 'System.Net.Http.HttpClient'; powershellVersion = $PSVersionTable.PSVersion.ToString()
        dotnetRuntime = [Environment]::Version.ToString()
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        evidence = $evidence
    }
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    Write-Host "Registry download evidence: $OutputPath"
    if ($evidence.status -cne 'success') { throw "Fixed registry download failed; see $OutputPath." }
    return
}

function Get-UnityPackageLogEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Cache,
        [string]$InstalledDiagnostics
    )
    $files = New-Object System.Collections.Generic.List[object]
    $directories = New-Object System.Collections.Generic.List[object]
    $errors = New-Object System.Collections.Generic.List[string]
    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($name in @('LOCALAPPDATA', 'ALLUSERSPROFILE')) {
        $root = [Environment]::GetEnvironmentVariable($name)
        if ([string]::IsNullOrWhiteSpace($root)) {
            $files.Add([ordered]@{ kind = 'upm'; location = $name; status = 'environment-unset' })
            continue
        }
        foreach ($leaf in @('upm.log', 'upm.log.prev')) {
            $candidates.Add(@{ kind = 'upm'; path = Join-Path $root "Unity/Editor/$leaf" })
        }
    }
    foreach ($leaf in @('manifest.json', 'packages-lock.json')) {
        $candidates.Add(@{ kind = 'project'; path = Join-Path $Project "Packages/$leaf" })
    }
    if (-not [string]::IsNullOrWhiteSpace($InstalledDiagnostics)) {
        $candidates.Add(@{ kind = 'diagnostic-launcher'; path = Join-Path $InstalledDiagnostics 'RunUnityPackageManagerDiagnostics.bat' })
    }
    foreach ($candidate in $candidates) {
        $record = [ordered]@{ kind = $candidate.kind; path = $candidate.path; status = 'missing' }
        $stream = $null
        $reader = $null
        try {
            $item = Get-Item -LiteralPath $candidate.path -Force -ErrorAction Stop
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Expected a regular file, without a reparse point.'
            }
            $record.lengthBytes = $item.Length
            $record.lastWriteUtc = $item.LastWriteTimeUtc.ToString('O')
            $record.attributes = [string]$item.Attributes
            $stream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true)
            # Retain at most four million characters; detect overflow explicitly.
            $buffer = [char[]]::new(4 * 1024 * 1024)
            $count = $reader.ReadBlock($buffer, 0, $buffer.Length)
            $record.content = [string]::new($buffer, 0, $count)
            $record.status = if ($reader.Read() -eq -1) { 'ok' } else { 'truncated' }
            $bytes = [Text.Encoding]::UTF8.GetBytes($record.content)
            $hasher = [Security.Cryptography.SHA256]::Create()
            try {
                $record.capturedTextSha256BeforeRedaction = [BitConverter]::ToString(
                    $hasher.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
            } finally { $hasher.Dispose() }
            $record.lastWriteUtcAfterRead = (Get-Item -LiteralPath $candidate.path -Force).LastWriteTimeUtc.ToString('O')
            if ($record.lastWriteUtcAfterRead -cne $record.lastWriteUtc) {
                $record.status = 'changed-during-read'
            }
            if ($record.status -ne 'ok') { $errors.Add("$($candidate.path): $($record.status)") }
        } catch [System.Management.Automation.ItemNotFoundException] {
            $record.status = 'missing'
        } catch {
            $record.status = 'error'
            $record.error = $_.Exception.Message
            $errors.Add("$($candidate.path): $($_.Exception.Message)")
        } finally {
            if ($reader) { $reader.Dispose() }
            elseif ($stream) { $stream.Dispose() }
        }
        $files.Add($record)
    }
    $directoryPaths = @($Project, (Join-Path $Project 'Library/PackageCache'), $Cache,
        (Join-Path $Cache 'upm'), (Join-Path $Cache 'npm'))
    if (-not [string]::IsNullOrWhiteSpace($InstalledDiagnostics)) { $directoryPaths += $InstalledDiagnostics }
    foreach ($path in $directoryPaths) {
        $record = [ordered]@{ path = $path; status = 'missing'; entries = @() }
        try {
            $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Expected a directory, without a reparse point.'
            }
            $record.lastWriteUtc = $item.LastWriteTimeUtc.ToString('O')
            # Bound enumeration without walking package or cache contents recursively.
            $entries = @(Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop | Select-Object -First 201)
            $record.status = if ($entries.Count -gt 200) { 'truncated' } else { 'ok' }
            $record.entries = @($entries | Select-Object -First 200 | ForEach-Object {
                [ordered]@{
                    name = $_.Name; directory = $_.PSIsContainer; attributes = [string]$_.Attributes
                    lengthBytes = if ($_.PSIsContainer) { $null } else { $_.Length }
                    lastWriteUtc = $_.LastWriteTimeUtc.ToString('O')
                }
            })
            if ($record.status -ne 'ok') { $errors.Add("${path}: $($record.status)") }
        } catch [System.Management.Automation.ItemNotFoundException] {
            $record.status = 'missing'
        } catch {
            $record.status = 'error'
            $record.error = $_.Exception.Message
            $errors.Add("${path}: $($_.Exception.Message)")
        }
        $directories.Add($record)
    }
    if (@($files | Where-Object { $_.kind -eq 'upm' -and $_.status -eq 'ok' }).Count -eq 0) {
        $errors.Add('No complete UPM service log was captured from the documented account locations.')
    }
    if (-not [string]::IsNullOrWhiteSpace($InstalledDiagnostics) -and
        @($files | Where-Object { $_.kind -eq 'diagnostic-launcher' -and $_.status -eq 'ok' }).Count -eq 0) {
        $errors.Add('No complete requested installed UPM diagnostic launcher was captured.')
    }
    $result = [ordered]@{
        projectPath = $Project; cachePath = $Cache
        files = @($files.ToArray()); directories = @($directories.ToArray()); errors = @($errors.ToArray())
    }
    if (-not [string]::IsNullOrWhiteSpace($InstalledDiagnostics)) { $result.installedDiagnosticsPath = $InstalledDiagnostics }
    return $result
}

if ($PackageLogsOnly) {
    $evidence = Get-UnityPackageLogEvidence -Project $ProjectPath -Cache $CachePath -InstalledDiagnostics $InstalledDiagnosticsPath
    $record = [ordered]@{
        schemaVersion = 1; purpose = 'unity-package-manager-diagnostics'
        capturedUtc = [DateTime]::UtcNow.ToString('O'); hostName = [Environment]::MachineName
        sourceSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        gitCommit = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
        evidence = $evidence
    }
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    Write-Host "Package log evidence: $OutputPath"
    if ($evidence.errors.Count -ne 0) { throw "Package log capture is incomplete; see $OutputPath." }
    return
}

if ($EventLogsOnly) {
    $evidence = Get-HostEventEvidence `
        -StartUtc ([DateTimeOffset]::Parse($EventStartUtc, [Globalization.CultureInfo]::InvariantCulture)) `
        -EndUtc ([DateTimeOffset]::Parse($EventEndUtc, [Globalization.CultureInfo]::InvariantCulture))
    $record = [ordered]@{
        schemaVersion = 1; purpose = 'performance-host-event-logs'
        capturedUtc = [DateTime]::UtcNow.ToString('O'); hostName = [Environment]::MachineName
        sourceSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        gitCommit = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
        evidence = $evidence
    }
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    Write-Host "Host event logs: $OutputPath ($($evidence.events.Count) events)"
    if ($evidence.errors.Count -ne 0) {
        throw "Event log capture is incomplete; see $OutputPath for query errors."
    }
    return
}

if ($ReadySignalPath -and -not $StopSignalPath) {
    throw '-ReadySignalPath requires -StopSignalPath.'
}
$cpuProfile = Get-Content -LiteralPath $CpuProfilePath -Raw | ConvertFrom-Json
# The player-time sampler is pinned outside the selected player CPUs. .NET's
# ProcessorCount follows that process affinity, so use the host topology here.
$hostLogicalProcessorCount = [int](
    (Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
)
if (
    $cpuProfile.executionProfileId -cne 'highest-efficiency-class-affinity-normal-v1' -or
    $cpuProfile.cpuModel -notmatch 'i9-13900KF' -or
    $cpuProfile.logicalProcessorCount -ne $hostLogicalProcessorCount -or
    $cpuProfile.selectedLogicalProcessorCount -ne 16 -or
    $cpuProfile.selectedCoreCount -ne 8 -or
    @($cpuProfile.selectedLogicalProcessorIndices).Count -ne $cpuProfile.selectedLogicalProcessorCount -or
    $cpuProfile.affinityMask -cnotmatch '^0x[0-9A-F]+$'
) {
    throw 'Performance host characterization requires a valid pinned CPU profile.'
}

# This is a manual, outcome-free availability and cadence probe. Numerical limits are
# frozen only after inspecting this artifact and before any pilot player is launched.
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

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

public static class DxmSelectedCpuLoad
{
    private static volatile bool stopping;
    private static Thread[] workers;
    private static long[] sinks;
    private static IntPtr originalAffinity;

    public static void Start(int count, long affinityMask)
    {
        if (workers != null) throw new InvalidOperationException("CPU load is already active.");
        Process process = Process.GetCurrentProcess();
        originalAffinity = process.ProcessorAffinity;
        process.ProcessorAffinity = new IntPtr(affinityMask);
        stopping = false;
        sinks = new long[count];
        workers = new Thread[count];
        try
        {
            for (int index = 0; index < count; index++)
            {
                int slot = index;
                workers[index] = new Thread(() =>
                {
                    ulong value = (ulong)(slot + 1);
                    while (!stopping)
                    {
                        for (int step = 0; step < 10000; step++)
                            value ^= value << 13 ^ value >> 7 ^ value << 17;
                        Interlocked.Exchange(ref sinks[slot], unchecked((long)value));
                    }
                });
                workers[index].IsBackground = true;
                workers[index].Start();
            }
        }
        catch { Stop(); throw; }
    }

    public static void Stop()
    {
        if (workers == null) return;
        stopping = true;
        try
        {
            foreach (Thread worker in workers)
                if (worker != null && !worker.Join(10000))
                    throw new TimeoutException("A CPU load worker did not stop.");
        }
        finally
        {
            Process.GetCurrentProcess().ProcessorAffinity = originalAffinity;
            workers = null;
            sinks = null;
        }
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
        $raw = [DxmPowerInformation]::ReadProcessors($hostLogicalProcessorCount)
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
    $readStartTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $errors = New-Object System.Collections.Generic.List[string]
    $processorCounters = @()
    $thermalZones = @()
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $counterNames = [ordered]@{
            'Processor Frequency' = 'ProcessorFrequency'
            '% of Maximum Frequency' = 'PercentMaximumFrequency'
            '% Processor Performance' = 'PercentProcessorPerformance'
            '% Performance Limit' = 'PercentPerformanceLimit'
            'Performance Limit Flags' = 'PerformanceLimitFlags'
            '% Processor Time' = 'PercentProcessorTime'
        }
        $paths = @($counterNames.Keys | ForEach-Object { "\Processor Information(*)\$_" })
        $byInstance = @{}
        foreach ($counter in (Get-Counter -Counter $paths -ErrorAction Stop).CounterSamples) {
            $instance = [string]$counter.InstanceName
            if (-not $byInstance.ContainsKey($instance)) {
                $byInstance[$instance] = [ordered]@{ name = $instance }
            }
            $counterName = ($counter.Path -split '\\')[-1]
            $member = $counterNames[$counterName]
            if (-not $member) { throw "Unexpected processor counter path: $($counter.Path)" }
            $byInstance[$instance][$member] = $counter.CookedValue
        }
        $processorCounters = @($byInstance.Values)
    } catch { $errors.Add("processorCounters: $($_.Exception.Message)") }
    $processorCounterReadMilliseconds = $clock.Elapsed.TotalMilliseconds
    # The available ACPI zone has no verified CPU-package identity. Probe it
    # during host characterization, but keep this uninformative WMI read out
    # of each time-sensitive player sample.
    if (-not $StopSignalPath) {
        try {
            $thermalZones = @(Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction Stop | ForEach-Object {
                [ordered]@{ instanceName = [string]$_.InstanceName; rawTenthsKelvin = $_.CurrentTemperature }
            })
        } catch { $errors.Add("acpiThermalZones: $($_.Exception.Message)") }
    }
    $beforeNativeMilliseconds = $clock.Elapsed.TotalMilliseconds
    $nativePower = Get-NativePowerState
    # Bracket the UTC read so scheduling inside the capture remains observable.
    $utcBeforeTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $timestampUtc = [DateTime]::UtcNow.ToString('O')
    $utcAfterTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
    return [ordered]@{
        timestampUtc = $timestampUtc
        monotonicClock = [ordered]@{
            frequencyTicksPerSecond = [System.Diagnostics.Stopwatch]::Frequency.ToString([Globalization.CultureInfo]::InvariantCulture)
            isHighResolution = [System.Diagnostics.Stopwatch]::IsHighResolution
            readStartTicks = $readStartTicks.ToString([Globalization.CultureInfo]::InvariantCulture)
            utcBeforeTicks = $utcBeforeTicks.ToString([Globalization.CultureInfo]::InvariantCulture)
            utcAfterTicks = $utcAfterTicks.ToString([Globalization.CultureInfo]::InvariantCulture)
        }
        processorCounters = $processorCounters
        acpiThermalZones = $thermalZones
        acpiThermalZoneStatus = if ($StopSignalPath) { 'unmeasured-player-time' } else { 'probed' }
        nativePower = $nativePower
        sensorReadMilliseconds = [ordered]@{
            processorCounters = $processorCounterReadMilliseconds
            acpiThermalZoneStep = $beforeNativeMilliseconds - $processorCounterReadMilliseconds
            nativePower = $clock.Elapsed.TotalMilliseconds - $beforeNativeMilliseconds
            total = $clock.Elapsed.TotalMilliseconds
        }
        errors = @($errors.ToArray())
    }
}

$startedUtc = [DateTime]::UtcNow.ToString('O')
$before = Get-PowerState
$sleepBefore = Get-SleepEvidence
$samples = New-Object System.Collections.Generic.List[object]
$loadMode = if ($CpuLoad) {
    'selected-cpu-spin-v1'
} elseif ($StopSignalPath) {
    'player-workload-v1'
} else {
    'idle-observation-v1'
}
$stopReason = 'sample-count-limit'
try {
    if ($CpuLoad) {
        $mask = [Convert]::ToInt64($cpuProfile.affinityMask.Substring(2), 16)
        [DxmSelectedCpuLoad]::Start($cpuProfile.selectedLogicalProcessorCount, $mask)
    }
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    for ($index = 0; $index -lt $SampleCount; $index++) {
        if ($index -gt 0 -and $StopSignalPath -and (Test-Path -LiteralPath $StopSignalPath -PathType Leaf)) {
            $stopReason = 'player-finished'
            break
        }
        $samples.Add((Get-SensorSample))
        if ($index -eq 0 -and $ReadySignalPath) {
            Set-Content -LiteralPath $ReadySignalPath -Value ([DateTime]::UtcNow.ToString('O')) -Encoding utf8
        }
        if ($StopSignalPath -and (Test-Path -LiteralPath $StopSignalPath -PathType Leaf)) {
            $stopUtc = ([DateTime](Get-Content -LiteralPath $StopSignalPath -Raw)).ToUniversalTime()
            $sampleUtc = ([DateTime]$samples[$samples.Count - 1].timestampUtc).ToUniversalTime()
            if ($sampleUtc -gt $stopUtc) { $samples.RemoveAt($samples.Count - 1) }
            $stopReason = 'player-finished'
            break
        }
        $remaining = ($index + 1) - $clock.Elapsed.TotalSeconds
        if ($index + 1 -lt $SampleCount -and $remaining -gt 0) {
            Start-Sleep -Milliseconds ([int][Math]::Ceiling($remaining * 1000))
        }
    }
} finally {
    if ($CpuLoad) { [DxmSelectedCpuLoad]::Stop() }
}
$after = Get-PowerState
$sleepAfter = Get-SleepEvidence
$nativeAfter = Get-NativePowerState
$record = [ordered]@{
    schemaVersion = 1
    purpose = if ($StopSignalPath) { 'player-time-host-telemetry' } else { 'outcome-blind-host-characterization' }
    sourceSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    gitCommit = $env:GITHUB_SHA
    runId = $env:GITHUB_RUN_ID
    runAttempt = $env:GITHUB_RUN_ATTEMPT
    startedUtc = $startedUtc
    endedUtc = [DateTime]::UtcNow.ToString('O')
    hostName = [Environment]::MachineName
    osVersion = [Environment]::OSVersion.VersionString
    powerShellVersion = $PSVersionTable.PSVersion.ToString()
    processorCount = $hostLogicalProcessorCount
    cpuProfile = [ordered]@{
        sha256 = (Get-FileHash -LiteralPath $CpuProfilePath -Algorithm SHA256).Hash.ToLowerInvariant()
        executionProfileId = $cpuProfile.executionProfileId
        affinityMask = $cpuProfile.affinityMask
        selectedLogicalProcessorIndices = @($cpuProfile.selectedLogicalProcessorIndices)
    }
    requestedSampleCount = $SampleCount
    actualSampleCount = $samples.Count
    requestedCadenceSeconds = 1
    stopReason = $stopReason
    loadMode = $loadMode
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
