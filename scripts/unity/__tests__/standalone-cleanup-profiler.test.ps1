#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$unity = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('dxm-cleanup-profiler-' + [guid]::NewGuid().ToString('N'))
$originalMode = [Environment]::GetEnvironmentVariable('DXM_CLEANUP_PROFILER_MODE')
$originalDirectory = [Environment]::GetEnvironmentVariable('DXM_CLEANUP_PROFILER_DIRECTORY')
$checks = 0
function Assert-Control([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Assert-ControlFailure([scriptblock]$Action, [string]$Expected) {
    $failure = ''
    try { & $Action } catch { $failure = $_.Exception.ToString() }
    Assert-Control ($failure.Contains($Expected)) "Expected failure containing '$Expected'; actual: $failure"
}
# Only external Unity APIs are stubbed. Every observer, serializer, scope and
# hierarchy operation below executes the actual emitted C# template.
$stubs = @'
namespace UnityEngine {
 public static class Application { public static string unityVersion => "6000.4.6f1"; public static string dataPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DxmFixture", "Assets"); }
}
namespace UnityEngine.Profiling {
 public static class Profiler {
  public static bool enabled;
  public static bool enableBinaryLog;
  public static bool FailLogStop;
  private static string log = "";
  public static string logFile { get => log; set { if (value == "" && FailLogStop) { FailLogStop = false; throw new System.IO.IOException("controlled log stop failure"); } log = value; } }
 }
}
namespace UnityEditor { public sealed class CleanupProfilerCompileNamespace { } }
namespace UnityEditorInternal {
 public static class ProfilerDriver {
  public static bool enabled;
  public static bool profileEditor;
  public static int connectedProfiler = -1;
  public static int firstFrameIndex = -1;
  public static int lastFrameIndex = -1;
  public static bool MissingMarkers;
  public static bool RawLoadFails;
  public static int ClearCount;
  public static void ClearAllFrames() { ++ClearCount; firstFrameIndex = -1; lastFrameIndex = -1; }
  public static bool LoadProfile(string file, bool append) {
   if (RawLoadFails || !System.IO.File.Exists(file)) return false;
   firstFrameIndex = 0; lastFrameIndex = 0; return true;
  }
  public static UnityEditor.Profiling.RawFrameDataView GetRawFrameDataView(int frame, int thread) => new UnityEditor.Profiling.RawFrameDataView();
 }
}
namespace UnityEditor.Profiling {
 public sealed class RawFrameDataView : System.IDisposable {
  public bool valid => true;
  public string threadName => "Main Thread";
  public int sampleCount => 4;
  public string GetSampleName(int index) => index == 0 ? "Root" : (UnityEditorInternal.ProfilerDriver.MissingMarkers ? "Other" : "ExecutePostBuildCleanupMethods");
  public int GetSampleChildrenCountRecursive(int index) => index == 0 ? 3 : (index == 1 ? 2 : 0);
  public int GetSampleChildrenCount(int index) => index == 0 ? 1 : (index == 1 ? 2 : 0);
  public ulong GetSampleStartTimeNs(int index) => new ulong[] { 0, 10, 15, 30 }[index];
  public ulong GetSampleTimeNs(int index) => new ulong[] { 100, 50, 10, 10 }[index];
  public void Dispose() { }
 }
}
'@
try {
    Add-Type -TypeDefinition ($stubs + [IO.File]::ReadAllText((Join-Path $unity 'standalone-cleanup-profiler.cs.txt'))) -CompilerOptions '/langversion:9.0'
    New-Item -ItemType Directory -Path $scratch | Out-Null
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_MODE', $null)
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_DIRECTORY', $null)
    [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Begin()
    [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Finish()
    Assert-Control (@(Get-ChildItem -LiteralPath $scratch).Count -eq 0) 'Disabled observation must not write anything.'
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_MODE', 'Unexpected')
    Assert-ControlFailure { [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Begin() } 'Unknown cleanup observer mode'
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_MODE', 'Capture')
    Assert-ControlFailure { [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Begin() } 'absolute path'
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_DIRECTORY', 'relative')
    Assert-ControlFailure { [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Begin() } 'absolute path'
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_DIRECTORY', $scratch)
    Assert-ControlFailure { [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Begin() } 'already exists'
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_DIRECTORY', (Join-Path $scratch 'busy'))
    [UnityEngine.Profiling.Profiler]::enabled = $true
    Assert-ControlFailure { [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Begin() } 'idle profiler'
    Assert-Control ([UnityEngine.Profiling.Profiler]::enabled) 'Admission must not stop another active profiler.'
    [UnityEngine.Profiling.Profiler]::enabled = $false
    foreach ($mode in @('Control', 'Capture')) {
        $directory = Join-Path $scratch $mode
        [UnityEngine.Profiling.Profiler]::logFile = 'original.log'
        [UnityEditorInternal.ProfilerDriver]::connectedProfiler = 17
        [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_MODE', $mode)
        [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_DIRECTORY', $directory)
        [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Begin()
        Assert-ControlFailure { [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Begin() } 'already active'
        Assert-Control ([UnityEngine.Profiling.Profiler]::enabled -eq ($mode -ceq 'Capture')) 'Only capture enables profiling.'
        if ($mode -ceq 'Capture') { [IO.File]::WriteAllBytes((Join-Path $directory 'cleanup.raw'), [byte[]]@(1, 2, 3, 4)) }
        [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Finish()
        $packetPath = Join-Path $directory 'observation.json'
        $packet = Get-Content -LiteralPath $packetPath -Raw | ConvertFrom-Json
        Assert-Control $packet.Completed 'Completed control must be explicit.'
        Assert-Control ($packet.BuildProcessId -eq $PID) 'Producer process identity must survive serialization.'
        Assert-Control ($packet.StopwatchFrequency -gt 0 -and $packet.PostprocessTimestamp -le $packet.ArmedTimestamp -and $packet.ArmedTimestamp -le $packet.ExitCallbackTimestamp) 'Native clock boundaries must remain ordered.'
        Assert-Control ($null -ne $packet.Original -and $null -ne $packet.Restored) 'Nested restoration records must survive serialization.'
        Assert-Control (($packet.Original | ConvertTo-Json -Compress) -ceq ($packet.Restored | ConvertTo-Json -Compress)) 'Every original field must match its restoration.'
        Assert-Control ($packet.Restored.LogFile -ceq 'original.log' -and $packet.Restored.Connection -eq 17) 'Non-default admitted settings must be retained.'
        $unchanged = [IO.File]::ReadAllText($packetPath)
        [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Finish()
        Assert-Control ([IO.File]::ReadAllText($packetPath) -ceq $unchanged) 'A second finish must not rewrite the completed observation.'
    }
    $captureDirectory = Join-Path $scratch 'Capture'
    [UnityEngine.Profiling.Profiler]::logFile = 'reader-original.log'
    [UnityEditorInternal.ProfilerDriver]::connectedProfiler = 23
    [UnityEditorInternal.ProfilerDriver]::profileEditor = $true
    [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Export($captureDirectory)
    $readerPacket = Get-Content -LiteralPath (Join-Path $captureDirectory 'samples.json') -Raw | ConvertFrom-Json
    Assert-Control ($null -ne $readerPacket.Original -and $readerPacket.Original.LogFile -ceq 'reader-original.log' -and $readerPacket.Original.Connection -eq 23 -and $readerPacket.Original.ProfileEditor) 'The separate reader must retain its own starting settings.'
    Assert-Control (($readerPacket.Original | ConvertTo-Json -Compress) -ceq ($readerPacket.Restored | ConvertTo-Json -Compress)) 'Reader restoration must match reader admission, independently of the builder.'
    $samples = Get-Content -LiteralPath (Join-Path $captureDirectory 'samples.json') -Raw | ConvertFrom-Json
    Assert-Control ($samples.Completed -and @($samples.Samples).Count -eq 3) 'Keep each same-name raw sample separately.'
    Assert-Control (($samples.Samples.Index -join ',') -ceq '1,2,3') 'Raw execution order must be preserved.'
    Assert-Control (($samples.Samples[0].AncestorIndices -join ',') -ceq '0' -and ($samples.Samples[1].AncestorIndices -join ',') -ceq '0,1') 'Parents must come from raw ancestry.'
    Assert-Control (($samples.Samples.InclusiveNs -join ',') -ceq '50,10,10') 'Do not replace individual inclusive times with a nested sum.'
    Assert-Control ($samples.ReaderProcessId -eq $PID -and $samples.Restored.FirstFrame -eq -1 -and $samples.Restored.LastFrame -eq -1) 'The reader must identify itself and restore empty history.'
    foreach ($failure in @('missing-markers', 'load-failure')) {
        $directory = Join-Path $scratch $failure
        New-Item -ItemType Directory -Path $directory | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $directory 'cleanup.raw'), [byte[]]@(1))
        [UnityEditorInternal.ProfilerDriver]::MissingMarkers = $failure -ceq 'missing-markers'
        [UnityEditorInternal.ProfilerDriver]::RawLoadFails = $failure -ceq 'load-failure'
        $expected = if ($failure -ceq 'missing-markers') { 'no main-thread cleanup scopes' } else { 'could not be loaded' }
        Assert-ControlFailure { [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Export($directory) } $expected
        $packet = Get-Content -LiteralPath (Join-Path $directory 'samples.json') -Raw | ConvertFrom-Json
        Assert-Control (-not $packet.Completed -and $packet.Error.Contains($expected)) 'A reader error must remain an error in its packet.'
        Assert-Control ($packet.Restored.FirstFrame -eq -1 -and $packet.Restored.LastFrame -eq -1) 'Failed readers must release owned history.'
    }
    [UnityEditorInternal.ProfilerDriver]::MissingMarkers = $false
    [UnityEditorInternal.ProfilerDriver]::RawLoadFails = $false
    [UnityEngine.Profiling.Profiler]::logFile = 'original.log'
    [UnityEditorInternal.ProfilerDriver]::connectedProfiler = 17
    [UnityEditorInternal.ProfilerDriver]::profileEditor = $false
    $directory = Join-Path $scratch 'restore-failure'
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_MODE', 'Capture')
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_DIRECTORY', $directory)
    [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Begin()
    [UnityEngine.Profiling.Profiler]::FailLogStop = $true
    Assert-ControlFailure { [DxMessaging.Ci.DxmStandaloneCleanupProfiler]::Finish() } 'controlled log stop failure'
    $packet = Get-Content -LiteralPath (Join-Path $directory 'observation.json') -Raw | ConvertFrom-Json
    Assert-Control (-not $packet.Completed -and $packet.Error.Contains('controlled log stop failure')) 'Restoration failure cannot produce a successful observation.'
    Assert-Control (-not [UnityEngine.Profiling.Profiler]::enabled -and -not [UnityEditorInternal.ProfilerDriver]::enabled -and [UnityEngine.Profiling.Profiler]::logFile -ceq 'original.log' -and [UnityEditorInternal.ProfilerDriver]::connectedProfiler -eq 17) 'All later restoration steps must run after one fails.'
    $runnerAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $unity 'run-ci-tests.ps1'), [ref]$null, [ref]$null)
    $runnerText = [IO.File]::ReadAllText((Join-Path $unity 'run-ci-tests.ps1'))
    $pathStart = $runnerText.IndexOf('            $observationProject =', [StringComparison]::Ordinal)
    $pathEnd = $runnerText.IndexOf('            if ($observation.SchemaVersion', $pathStart, [StringComparison]::Ordinal)
    $pathBlock = $runnerText.Substring($pathStart, $pathEnd - $pathStart)
    $pathHelper = $runnerAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertTo-ComparableFullPath' }, $true).Extent.Text
    # The empty session has no PowerShell 7 platform variables, as on Windows PowerShell 5.1.
    $session = [System.Management.Automation.Runspaces.InitialSessionState]::Create()
    $session.LanguageMode = 'FullLanguage'
    $session.Commands.Add([System.Management.Automation.Runspaces.SessionStateCmdletEntry]::new('Set-StrictMode', [Microsoft.PowerShell.Commands.SetStrictModeCommand], $null))
    $runspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($session)
    $runspace.Open()
    $shell = [powershell]::Create()
    try {
        $shell.Runspace = $runspace
        $runspace.SessionStateProxy.SetVariable('ProjectPath', $scratch)
        foreach ($case in @('trailing-separator', 'different-path', 'case-variant')) {
            $actualPath = switch ($case) {
                'trailing-separator' { $scratch + [IO.Path]::DirectorySeparatorChar }
                'different-path' { $scratch + '-other' }
                'case-variant' { $scratch.ToUpperInvariant() }
            }
            $expectedEqual = $case -ceq 'trailing-separator' -or ($case -ceq 'case-variant' -and [IO.Path]::DirectorySeparatorChar -eq '\')
            $runspace.SessionStateProxy.SetVariable('observation', [pscustomobject]@{ ProjectPath = $actualPath })
            $shell.Commands.Clear()
            $shell.Streams.Error.Clear()
            $checkPath = "Set-StrictMode -Version Latest; $pathHelper`n$pathBlock`n[string]::Equals(`$observationProject, `$expectedProject, `$pathComparison)"
            $null = $shell.AddScript($checkPath)
            $pathResult = @($shell.Invoke())
            Assert-Control ($shell.Streams.Error.Count -eq 0 -and $pathResult.Count -eq 1 -and $pathResult[0] -eq $expectedEqual) ("Actual observer path binding must work without PowerShell 7 platform variables: " + ($shell.Streams.Error -join '; '))
            if ([IO.Path]::DirectorySeparatorChar -eq '\') {
                $legacyScript = "`$ErrorActionPreference = 'Stop'; `$observation = @{ ProjectPath = '" + $actualPath.Replace("'", "''") + "' }; `$ProjectPath = '" + $scratch.Replace("'", "''") + "'; $checkPath"
                $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($legacyScript))
                $legacyResult = @(& powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded 2>&1)
                Assert-Control ($LASTEXITCODE -eq 0 -and ($legacyResult -join '').Trim() -ceq $expectedEqual.ToString()) ("Native Windows PowerShell 5.1 path binding failed: " + ($legacyResult -join '; '))
            }
        }
    } finally { $shell.Dispose(); $runspace.Dispose() }
    $hierarchyFunction = $runnerAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Assert-StandaloneCleanupHierarchy' }, $true)
    . ([scriptblock]::Create($hierarchyFunction.Extent.Text))
    Assert-StandaloneCleanupHierarchy -SamplePacket $samples -ExpectedScopes 3
    Assert-Control $true 'The actual hierarchy qualifier accepts the native-reader fixture.'
    foreach ($mutation in @('missing-child', 'flat-markers', 'outside-parent', 'wrong-frame', 'reversed', 'overlap')) {
        $changed = $samples | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        switch ($mutation) {
            'missing-child' { $changed.Samples = @($changed.Samples[0], $changed.Samples[1]) }
            'flat-markers' { $changed.Samples[1].Ancestors = @('Root') }
            'outside-parent' { $changed.Samples[2].StartNs = 1000 }
            'wrong-frame' { $changed.Samples[1].Frame = 1 }
            'reversed' { $changed.Samples = @($changed.Samples[0], $changed.Samples[2], $changed.Samples[1]) }
            'overlap' { $changed.Samples[2].StartNs = $changed.Samples[1].StartNs }
        }
        Assert-ControlFailure { Assert-StandaloneCleanupHierarchy -SamplePacket $changed -ExpectedScopes 3 } 'Standalone cleanup hierarchy'
    }
    $twoScopes = $samples | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $twoScopes.Samples = @($twoScopes.Samples[0], $twoScopes.Samples[1])
    $twoScopes.Samples[0].Children = 1
    Assert-StandaloneCleanupHierarchy -SamplePacket $twoScopes -ExpectedScopes 2
    Assert-Control $true 'The declared older framework layout accepts one nested child.'
    $generatedSources = @{}
    foreach ($mode in @('Control', 'Capture')) {
        $project = Join-Path $scratch ("project-" + $mode)
        $output = @(& pwsh -NoLogo -NoProfile -File (Join-Path $unity 'run-ci-tests.ps1') -UnityVersion '6000.4.6f1' -TestMode standalone -AssemblyNames Fixture -ArtifactsPath (Join-Path $scratch ("artifacts-" + $mode)) -ProjectPath $project -CaptureTestFrameworkEvidence -CleanupProfilerMode $mode -GenerateOnly -SkipCiAnalyzers 2>&1)
        Assert-Control ($LASTEXITCODE -eq 0) ("Actual observer projection must generate: " + ($output -join "`n"))
        $generatedSources[$mode] = @{}
        foreach ($name in @('DxmStandaloneCleanupProfiler.cs', 'DxmCiStandaloneBuildModifier.cs')) {
            $generatedSources[$mode][$name] = [IO.File]::ReadAllText((Join-Path $project ("Assets/Editor/" + $name))).Replace("`r`n", "`n")
        }
        Assert-Control ($generatedSources[$mode]['DxmStandaloneCleanupProfiler.cs'].TrimEnd("`n") -ceq [IO.File]::ReadAllText((Join-Path $unity 'standalone-cleanup-profiler.cs.txt')).Replace("`r`n", "`n").TrimEnd("`n")) 'The emitted observer must be the actual tested template.'
    }
    foreach ($name in @('DxmStandaloneCleanupProfiler.cs', 'DxmCiStandaloneBuildModifier.cs')) {
        Assert-Control ($generatedSources.Control[$name] -ceq $generatedSources.Capture[$name]) 'Capture and control must compile identical helper and callback source.'
    }
    $unsupported = @(& pwsh -NoLogo -NoProfile -File (Join-Path $unity 'run-ci-tests.ps1') -UnityVersion '6000.4.6f1' -TestMode editmode -AssemblyNames Fixture -ArtifactsPath (Join-Path $scratch 'unsupported') -CaptureTestFrameworkEvidence -CleanupProfilerMode Capture -GenerateOnly -SkipCiAnalyzers 2>&1)
    Assert-Control ($LASTEXITCODE -ne 0 -and ($unsupported -join "`n").Contains('ordinary correctness standalone')) 'The observer must reject unsupported modes before generation.'
    # Execute the workflow's actual PowerShell body with a controlled CLI producer.
    $repoRoot = Split-Path -Parent (Split-Path -Parent $unity)
    $readStep = 'const fs=require("node:fs"),yaml=require("yaml");const w=yaml.parse(fs.readFileSync(process.argv[1],"utf8"));process.stdout.write(w.jobs["unity-tests"].steps.find(s=>s.id==="run_standalone").run);'
    $workflowBody = (& node -e $readStep (Join-Path $repoRoot '.github/workflows/unity-tests.yml')) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot load the actual standalone workflow body.' }
    $environmentNames = @('RUNNER_WORKSPACE', 'RUNNER_TOOL_CACHE', 'DXM_UNITY_SOURCE_SET_KEY', 'DXM_TEST_ASSEMBLIES', 'DXM_OBSERVER_FIXTURE_LOG', 'DXM_OBSERVER_FIXTURE_DRIFT')
    $savedEnvironment = @{}
    foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
    try {
        foreach ($case in @('disabled', 'stable', 'name', 'outcome', 'skip-reason', 'duplicate')) {
            $fixture = Join-Path $scratch ("workflow-" + $case)
            $runner = Join-Path $fixture 'scripts/unity/run-ci-tests.ps1'
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $runner) | Out-Null
            [IO.File]::WriteAllText($runner, @'
param([string]$UnityVersion,[string]$UnityInstallRoot,[string]$TestMode,[switch]$CaptureTestFrameworkEvidence,[string]$AssemblyNames,[string]$ArtifactsPath,[string]$ProjectPath,[string]$CachePath,[string]$LicenseReturnOwner,[string]$CleanupProfilerMode,[switch]$ReleaseCodeOptimization,[switch]$ReleasePlayerBuild)
[ordered]@{mode=$CleanupProfilerMode;project=$ProjectPath;cache=$CachePath;assemblies=$AssemblyNames;testMode=$TestMode;licenseOwner=$LicenseReturnOwner;release=$ReleasePlayerBuild.IsPresent;optimization=$ReleaseCodeOptimization.IsPresent;framework=$CaptureTestFrameworkEvidence.IsPresent} | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:DXM_OBSERVER_FIXTURE_LOG
$name='Fixture.Pass';$outcome='Passed';$reason='retained skip';$duplicate=''
if($CleanupProfilerMode -ceq 'Capture') {
 switch($env:DXM_OBSERVER_FIXTURE_DRIFT) {
  'name' { $name='Fixture.Changed' }
  'outcome' { $outcome='Failed' }
  'skip-reason' { $reason='changed skip' }
  'duplicate' { $duplicate='<test-case fullname="Fixture.Pass" result="Passed" />' }
 }
}
New-Item -ItemType Directory -Path $ArtifactsPath -Force | Out-Null
[IO.File]::WriteAllText((Join-Path (Resolve-Path -LiteralPath $ArtifactsPath).Path 'results.xml'),"<test-run><test-case fullname='$name' result='$outcome'/><test-case fullname='Fixture.Skip' result='Skipped'><reason><message>$reason</message></reason></test-case>$duplicate</test-run>")
'@)
            $env:RUNNER_WORKSPACE = Join-Path $fixture 'runner'
            $env:RUNNER_TOOL_CACHE = Join-Path $fixture 'tools'
            $env:DXM_UNITY_SOURCE_SET_KEY = '0123abcd'
            $env:DXM_TEST_ASSEMBLIES = 'Fixture'
            $env:DXM_OBSERVER_FIXTURE_LOG = Join-Path $fixture 'calls.jsonl'
            $env:DXM_OBSERVER_FIXTURE_DRIFT = $case
            $selected = if ($case -ceq 'disabled') { 'Disabled' } else { '2021.3.45f1' }
            $body = $workflowBody.Replace('${{ matrix.unity-version }}', '2021.3.45f1').Replace('${{ inputs.cleanup_profiler_editor }}', $selected)
            Push-Location $fixture
            try {
                if ($case -cin @('disabled', 'stable')) { & ([scriptblock]::Create($body)) }
                else { Assert-ControlFailure { & ([scriptblock]::Create($body)) } 'Cleanup profiling changed native case' }
            } finally { Pop-Location }
            $calls = @(Get-Content -LiteralPath $env:DXM_OBSERVER_FIXTURE_LOG | ForEach-Object { $_ | ConvertFrom-Json })
            $expected = if ($case -ceq 'disabled') { 'Disabled' } elseif ($case -ceq 'stable') { 'Disabled,Control,Capture,Capture,Control' } else { 'Disabled,Control,Capture' }
            Assert-Control (($calls.mode -join ',') -ceq $expected) 'The actual workflow must preserve its fixed sequence and stop on the first inventory drift.'
            Assert-Control (@($calls.project | Select-Object -Unique).Count -eq 1 -and @($calls.cache | Select-Object -Unique).Count -eq 1) 'Every observation must use the same project and cache.'
            Assert-Control (@($calls | Where-Object { $_.assemblies -cne 'Fixture' -or $_.testMode -cne 'standalone' -or $_.licenseOwner -cne 'Central' -or -not $_.release -or -not $_.optimization -or -not $_.framework }).Count -eq 0) 'Every observation must retain assemblies, backend settings, framework capture and central cleanup.'
        }
    } finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
    }
    Write-Host "Standalone cleanup observer: $checks controls passed; actual source, C# 9; external Unity APIs stubbed."
} finally {
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_MODE', $originalMode)
    [Environment]::SetEnvironmentVariable('DXM_CLEANUP_PROFILER_DIRECTORY', $originalDirectory)
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
