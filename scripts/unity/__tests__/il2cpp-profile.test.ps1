#!/usr/bin/env pwsh
# cspell:ignore DNDEBUG
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$validatorPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'validate-il2cpp-profile.ps1'
$runnerPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'run-ci-tests.ps1'
$workflowPath = Join-Path $repoRoot '.github/workflows/perf-numbers.yml'
$testUnityVersion = '6000.3.16f1'
$sourceProfilePath = Join-Path $repoRoot '.github/perf/canonical-il2cpp-profile.v1.json'
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dxm-il2cpp-profile-{0}" -f [guid]::NewGuid().ToString('N'))

function Write-TestJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 10))
}

function Copy-JsonValue {
    param([Parameter(Mandatory = $true)]$Value)
    return $Value | ConvertTo-Json -Depth 10 | ConvertFrom-Json
}

function Assert-Fails {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [string]$ExpectedMessage
    )

    $failed = $false
    try {
        & $Action
    } catch {
        $failed = [string]::IsNullOrWhiteSpace($ExpectedMessage) -or
            $_.Exception.Message.Contains($ExpectedMessage)
    }
    if (-not $failed) {
        throw "Expected failure: $Description"
    }
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

try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $profilePath = Join-Path $fixtureRoot 'profile.json'
    Copy-Item -LiteralPath $sourceProfilePath -Destination $profilePath
    $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
    $profileSha256 = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash.ToLowerInvariant()

    $tokens = $null
    $parseErrors = $null
    $runnerAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $runnerPath,
        [ref]$tokens,
        [ref]$parseErrors
    )
    if (@($parseErrors).Count -gt 0) {
        throw "run-ci-tests.ps1 has parse errors: $($parseErrors.Message -join '; ')"
    }
    foreach ($name in @(
        'Get-ComparisonSourceEvidence',
        'Get-StandalonePlayerManifest',
        'Test-StandalonePlayerBuildOutput',
        'Write-JsonArtifact',
        'Write-NativeBuildInputEvidence',
        'New-ConfiguratorSource',
        'New-StandaloneBuildModifierSource',
        'New-ShippingFidelityBuilderSource',
        'New-StandaloneTestCallbackSource'
    )) {
        $definition = $runnerAst.FindAll(
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

    # Model session 413's partial exe/Data output with the production guard.
    # These synthetic files are never executed and are not actual player proof.
    $outputGuardRoot = Join-Path $fixtureRoot 'partial-player'
    $outputGuardExe = Join-Path $outputGuardRoot 'DxmTestPlayer.exe'
    $outputGuardData = Join-Path $outputGuardRoot 'DxmTestPlayer_Data'
    $outputGuardAssembly = Join-Path $outputGuardRoot 'GameAssembly.dll'
    $outputGuardMetadata = Join-Path $outputGuardData 'il2cpp_data/Metadata/global-metadata.dat'
    $outputGuardStarted = [datetime]::UtcNow
    $outputGuardBytes = [Text.Encoding]::UTF8.GetBytes('output guard fixture')
    New-Item -ItemType Directory -Force -Path $outputGuardData | Out-Null
    [IO.File]::WriteAllBytes($outputGuardExe, $outputGuardBytes)
    $partialOutputProblem = Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted
    Assert-That 'fresh exe/Data without native code is rejected' ($partialOutputProblem.Contains('missing IL2CPP file'))
    Assert-That 'Mono output does not require IL2CPP files' ([string]::IsNullOrEmpty((Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted -ScriptingBackend Mono)))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputGuardMetadata) | Out-Null
    [IO.File]::WriteAllBytes($outputGuardAssembly, $outputGuardBytes)
    [IO.File]::WriteAllBytes($outputGuardMetadata, $outputGuardBytes)
    Assert-That 'complete IL2CPP output passes the file guard' ([string]::IsNullOrEmpty((Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted -ScriptingBackend IL2CPP)))
    foreach ($path in @($outputGuardAssembly, $outputGuardMetadata)) {
        Remove-Item -LiteralPath $path
        Assert-That "missing native file is rejected: $path" ((Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted).Contains('missing IL2CPP file'))
        [IO.File]::WriteAllBytes($path, [byte[]]@())
        Assert-That "empty native file is rejected: $path" ((Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted).Contains('empty IL2CPP file'))
        [IO.File]::WriteAllBytes($path, $outputGuardBytes)
    }
    [IO.File]::WriteAllBytes($outputGuardExe, [byte[]]@())
    Assert-That 'empty executable is rejected' ((Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted).Contains('empty exe'))
    [IO.File]::WriteAllBytes($outputGuardExe, $outputGuardBytes)
    [IO.File]::SetLastWriteTimeUtc($outputGuardExe, $outputGuardStarted.AddMinutes(-1))
    Assert-That 'stale executable is rejected' ((Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted).Contains('stale exe'))
    [IO.File]::SetLastWriteTimeUtc($outputGuardExe, $outputGuardStarted)
    Remove-Item -LiteralPath $outputGuardData -Recurse
    Assert-That 'missing Data directory is rejected' ((Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted).Contains('missing player data directory'))
    Remove-Item -LiteralPath $outputGuardExe
    Assert-That 'missing executable is rejected' ((Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted).Contains('missing exe'))
    Assert-Fails -Description 'unknown backend is rejected' -Action {
        Test-StandalonePlayerBuildOutput -ExpectedExe $outputGuardExe -BuildStartedUtc $outputGuardStarted -ScriptingBackend unknown
    } -ExpectedMessage 'ValidateSet'

    $repositoryCatalogPath = Join-Path $repoRoot 'scripts/unity/comparison-evidence-catalog-v1.json'
    $repositoryCatalog = Get-Content -LiteralPath $repositoryCatalogPath -Raw | ConvertFrom-Json
    foreach ($entry in $repositoryCatalog.sources.PSObject.Properties) {
        $source = $entry.Value
        if ($source.origin -cne 'repository') {
            continue
        }
        $sourcePath = Join-Path $repoRoot $source.path
        $sourceBytes = [System.IO.File]::ReadAllBytes($sourcePath)
        $portableBytes = [System.Text.Encoding]::UTF8.GetBytes(
            [System.Text.Encoding]::UTF8.GetString($sourceBytes).Replace("`r`n", "`n")
        )
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        try {
            $portableHash = [BitConverter]::ToString($hasher.ComputeHash($portableBytes)).Replace('-', '').ToLowerInvariant()
        } finally {
            $hasher.Dispose()
        }
        Assert-That "repository catalog source $($entry.Name) uses portable LF bytes" ($portableHash -ceq $source.sha256)
    }

    $nativeProject = Join-Path $fixtureRoot 'native-project'
    $nativeArtifacts = Join-Path $fixtureRoot 'native-artifacts'
    $nativeLog = Join-Path $fixtureRoot 'native-build.log'
    $nativeGraph = 'Library/Bee/Player123abc.dag.json'
    $nativeInput = 'Library/Bee/Player123abc-inputdata.json'
    $nativeDag = 'Library/Bee/Player123abc.dag'
    $nativeBackend = 'Starting: C:\Editor\bee_backend.exe --ipc --dagfile="' + $nativeDag + '" --profile="Library/Bee/backend1.traceevents" Player'
    $nativeResponse = 'Library/Bee/artifacts/rsp/123456789.rsp'
    New-Item -ItemType Directory -Force -Path (Join-Path $nativeProject 'Library/Bee/artifacts/rsp') | Out-Null
    $nativeInvocation = 'Starting: C:\Editor\netcorerun.exe "C:\Editor\WinPlayerBuildProgram.exe" "C:/Editor/Bee" "' + $nativeGraph + '" "' + $nativeInput + '" "Library/Bee/buildprogram0.traceevents"'
    [IO.File]::WriteAllText((Join-Path $nativeProject $nativeGraph), '{"Nodes":[{"Action":"cl.exe @Library/Bee/artifacts/rsp/123456789.rsp"}]}')
    [IO.File]::WriteAllText((Join-Path $nativeProject $nativeDag), 'opaque graph fixture; hashed only')
    [IO.File]::WriteAllText((Join-Path $nativeProject $nativeInput), '{"BuildTarget":"StandaloneWindows64"}')
    [IO.File]::WriteAllText((Join-Path $nativeProject $nativeResponse), '/O2 /DNDEBUG')
    [IO.File]::WriteAllText($nativeLog, "$nativeInvocation`n[1/2 0s] WriteResponseFile $nativeResponse`n$nativeInvocation`n$nativeBackend`n")
    $nativeArguments = @{
        Project = $nativeProject; LogPath = $nativeLog; Artifacts = $nativeArtifacts
        ProfileId = $profile.profileId; ProfileSha256 = $profileSha256; UnityVersion = $testUnityVersion
    }
    Write-NativeBuildInputEvidence @nativeArguments
    & node (Join-Path $repoRoot 'scripts/unity/redact-unity-artifacts.js') $nativeArtifacts
    Assert-That 'native artifact scan succeeds' ($LASTEXITCODE -eq 0)
    $nativeManifest = Get-Content -LiteralPath (Join-Path $nativeArtifacts 'native-build-inputs/retained-manifest.json') -Raw | ConvertFrom-Json
    Assert-That 'native evidence retains one repeated graph pair and only observed response files' (
        @($nativeManifest.files).Count -eq 3 -and $nativeManifest.responseFileScope -ceq 'build-log-references-only'
    )
    foreach ($record in $nativeManifest.files) {
        Assert-That 'retained native bytes agree with source and retained hashes when no redaction is needed' (
            $record.sourceSha256 -ceq $record.retainedSha256 -and
            $record.retainedSha256 -ceq (Get-FileHash -LiteralPath (Join-Path $nativeArtifacts ('native-build-inputs/' + $record.retainedPath)) -Algorithm SHA256).Hash.ToLowerInvariant()
        )
    }
    Assert-That 'native evidence binds the requested editor, profile and original build log' (
        $nativeManifest.unityVersion -ceq $testUnityVersion -and $nativeManifest.profileSha256 -ceq $profileSha256 -and
        $nativeManifest.profileId -ceq $profile.profileId -and
        $nativeManifest.unredactedBuildLogSha256 -ceq (Get-FileHash -LiteralPath $nativeLog -Algorithm SHA256).Hash.ToLowerInvariant()
    )
    $responseText = [IO.File]::ReadAllText((Join-Path $nativeProject $nativeResponse))
    [IO.File]::WriteAllText((Join-Path $nativeProject $nativeResponse), $responseText + ' --access-token=fixture-secret-value')
    Write-NativeBuildInputEvidence @nativeArguments
    & node (Join-Path $repoRoot 'scripts/unity/redact-unity-artifacts.js') $nativeArtifacts
    Assert-That 'native artifact scan succeeds' ($LASTEXITCODE -eq 0)
    $redactedManifest = Get-Content -LiteralPath (Join-Path $nativeArtifacts 'native-build-inputs/retained-manifest.json') -Raw | ConvertFrom-Json
    $responseRecord = @($redactedManifest.files | Where-Object { $_.sourcePath -ceq $nativeResponse })[0]
    $retainedResponse = Join-Path $nativeArtifacts ('native-build-inputs/' + $responseRecord.retainedPath)
    Assert-That 'native response text is scanned before hashing retained bytes' (
        $responseRecord.sourceSha256 -cne $responseRecord.retainedSha256 -and
        $responseRecord.retainedSha256 -ceq (Get-FileHash -LiteralPath $retainedResponse -Algorithm SHA256).Hash.ToLowerInvariant() -and
        -not ([IO.File]::ReadAllText($retainedResponse).Contains('fixture-secret-value'))
    )
    [IO.File]::WriteAllText((Join-Path $nativeProject $nativeResponse), $responseText)
    foreach ($mutation in @('missing-graph', 'missing-response', 'no-invocation', 'ambiguous-graph', 'mismatched-pair', 'backend-conflict', 'backend-generator-conflict', 'empty-input', 'oversize-input', 'too-many-inputs')) {
        $savedLog = [IO.File]::ReadAllText($nativeLog)
        $savedGraph = [IO.File]::ReadAllText((Join-Path $nativeProject $nativeGraph))
        $savedResponse = [IO.File]::ReadAllText((Join-Path $nativeProject $nativeResponse))
        switch ($mutation) {
            'missing-graph' { Remove-Item -LiteralPath (Join-Path $nativeProject $nativeGraph) }
            'missing-response' { Remove-Item -LiteralPath (Join-Path $nativeProject $nativeResponse) }
            'no-invocation' { [IO.File]::WriteAllText($nativeLog, 'No player build graph was recorded.') }
            'ambiguous-graph' { [IO.File]::AppendAllText($nativeLog, $nativeInvocation.Replace('123abc', '456def')) }
            'mismatched-pair' { [IO.File]::WriteAllText($nativeLog, $nativeInvocation.Replace('Player123abc-inputdata', 'Player456def-inputdata')) }
            'backend-conflict' { [IO.File]::AppendAllText($nativeLog, $nativeBackend.Replace('123abc', '456def')) }
            'backend-generator-conflict' { [IO.File]::WriteAllText($nativeLog, "$nativeInvocation`n" + $nativeBackend.Replace('123abc', '456def')) }
            'empty-input' { [IO.File]::WriteAllText((Join-Path $nativeProject $nativeGraph), '') }
            'oversize-input' {
                $stream = [IO.File]::OpenWrite((Join-Path $nativeProject $nativeGraph))
                try { $stream.SetLength(64MB + 1) } finally { $stream.Dispose() }
            }
            'too-many-inputs' { [IO.File]::AppendAllLines($nativeLog, [string[]]@(1..256 | ForEach-Object { "Library/Bee/artifacts/rsp/$_.rsp" })) }
        }
        Assert-Fails "native evidence rejects $mutation" -ExpectedMessage 'Native build input' { Write-NativeBuildInputEvidence @nativeArguments }
        Assert-That 'failed native capture leaves no success manifest' (-not (Test-Path -LiteralPath (Join-Path $nativeArtifacts 'native-build-inputs/manifest.json')))
        [IO.File]::WriteAllText($nativeLog, $savedLog)
        [IO.File]::WriteAllText((Join-Path $nativeProject $nativeGraph), $savedGraph)
        [IO.File]::WriteAllText((Join-Path $nativeProject $nativeResponse), $savedResponse)
    }
    [IO.File]::WriteAllText($nativeLog, $nativeBackend.Replace('/', '\') + "`r`n")
    Write-NativeBuildInputEvidence @nativeArguments
    & node (Join-Path $repoRoot 'scripts/unity/redact-unity-artifacts.js') $nativeArtifacts
    Assert-That 'native artifact scan succeeds' ($LASTEXITCODE -eq 0)
    $noResponseManifest = Get-Content -LiteralPath (Join-Path $nativeArtifacts 'native-build-inputs/retained-manifest.json') -Raw | ConvertFrom-Json
    Assert-That 'Windows separators and zero observed response references preserve both graph inputs' (@($noResponseManifest.files).Count -eq 2 -and $noResponseManifest.graphSelection -ceq 'cached-backend-dag-companions')

    $nativeCallSites = @($runnerAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -ceq 'Write-NativeBuildInputEvidence'
    }, $true))
    Assert-That 'clean shipping, incremental shipping, and canonical standalone retain native inputs after their build' ($nativeCallSites.Count -eq 3)

    $generatedSources = @(
        New-ConfiguratorSource -CanonicalProfileId $profile.profileId -CanonicalProfileSha256 $profileSha256
        New-StandaloneTestCallbackSource -CanonicalProfileId $profile.profileId -CanonicalProfileSha256 $profileSha256
    )
    foreach ($source in $generatedSources) {
        Assert-That 'generated C# embeds the profile ID' ($source.Contains($profile.profileId))
        Assert-That 'generated C# embeds the profile SHA-256' ($source.Contains($profileSha256))
    }
    $buildModifierSource = New-StandaloneBuildModifierSource -CanonicalProfileId $profile.profileId
    $shippingBuilderSource = New-ShippingFidelityBuilderSource -CanonicalProfileId $profile.profileId -CanonicalProfileSha256 $profileSha256 -ManagedStrippingLevel High -ShippingTopology semantic -ShippingMessageTypeCount 18
    Assert-That 'standalone captures directory state before returning options for the build' (
        $buildModifierSource.IndexOf('CaptureBuildProvenance(playerOptions.locationPathName)') -gt 0 -and
        $buildModifierSource.IndexOf('CaptureBuildProvenance(playerOptions.locationPathName)') -lt $buildModifierSource.IndexOf('return playerOptions;')
    )
    Assert-That 'shipping captures directory state before BuildPipeline starts' (
        $shippingBuilderSource.IndexOf('CaptureBuildProvenance(options.locationPathName)') -gt 0 -and
        $shippingBuilderSource.IndexOf('CaptureBuildProvenance(options.locationPathName)') -lt $shippingBuilderSource.IndexOf('BuildPipeline.BuildPlayer(options)')
    )
    Assert-That 'build kind comes from effective final options' (
        $generatedSources[0].Contains('evidence.buildProvenance.playerBuildKind = evidence.values.cleanBuildCache ? "clean" : "incremental";')
    )
    # Compile and execute the real generated directory observer. Only the Unity
    # dataPath property and the test entry point are supplied by the fixture.
    $observerSources = @(foreach ($lineEnding in @("`n", "`r`n")) {
        $normalizedSource = $generatedSources[0].Replace("`r`n", "`n").Replace("`n", $lineEnding).Replace("`r`n", "`n")
        $observerStart = $normalizedSource.IndexOf("    [Serializable]`n    private sealed class BuildProvenance")
        $observerEnd = $normalizedSource.IndexOf("    [Serializable]`n    private sealed class BuildOptionsValues", $observerStart)
        Assert-That 'the generated observer can be extracted from LF and CRLF without copying its implementation' ($observerStart -ge 0 -and $observerEnd -gt $observerStart)
        $normalizedSource.Substring($observerStart, $observerEnd - $observerStart)
    })
    Assert-That 'LF and CRLF produce the same observer source' ($observerSources[0] -ceq $observerSources[1])
    $observerSource = $observerSources[0]
    Add-Type -TypeDefinition @"
using System;
using System.IO;
public static class DxmBuildProvenanceFixture
{
    private static class Application { public static string dataPath; }
$observerSource
    public static string[] Observe(string project, string output)
    {
        Application.dataPath = Path.Combine(project, "Assets");
        CaptureBuildProvenance(Path.Combine(output, "Player.exe"));
        s_BuildProvenance.playerBuildKind = "clean";
        return new[] { s_BuildProvenance.libraryStateBeforeBuild,
            s_BuildProvenance.beeStateBeforeBuild, s_BuildProvenance.il2cppCacheStateBeforeBuild,
            s_BuildProvenance.playerOutputStateBeforeBuild };
    }
}
"@
    $observerProject = Join-Path $fixtureRoot 'observer-project'
    $observerOutput = Join-Path $observerProject 'output'
    $observerLibrary = Join-Path $observerProject 'Library'
    New-Item -ItemType Directory -Force -Path (Join-Path $observerProject 'Assets') | Out-Null
    Assert-That 'the observer records missing caches and output without creating them' (
        ([DxmBuildProvenanceFixture]::Observe($observerProject, $observerOutput) -join ',') -ceq 'missing,missing,missing,missing' -and
        -not (Test-Path -LiteralPath $observerLibrary)
    )
    New-Item -ItemType Directory -Force -Path $observerLibrary, $observerOutput | Out-Null
    Assert-That 'the observer distinguishes empty directories' (
        ([DxmBuildProvenanceFixture]::Observe($observerProject, $observerOutput) -join ',') -ceq 'empty,missing,missing,empty'
    )
    foreach ($cache in @('Bee', 'Il2cppBuildCache')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $observerLibrary $cache) | Out-Null
    }
    [System.IO.File]::WriteAllText((Join-Path $observerLibrary 'Bee/input'), 'existing native input')
    foreach ($name in @('Player.exe', 'GameAssembly.dll')) {
        [System.IO.File]::WriteAllText((Join-Path $observerOutput $name), 'existing output')
    }
    Assert-That 'the observer distinguishes populated caches and stale player output' (
        ([DxmBuildProvenanceFixture]::Observe($observerProject, $observerOutput) -join ',') -ceq 'populated,populated,empty,populated'
    )
    $invalidOutput = Join-Path $observerProject 'output-file'
    [System.IO.File]::WriteAllText($invalidOutput, 'not a directory')
    Assert-Fails 'the observer fails closed on a file where a directory is required' -ExpectedMessage 'expected a directory' {
        [DxmBuildProvenanceFixture]::Observe($observerProject, $invalidOutput)
    }
    $applyStart = $generatedSources[0].IndexOf('public static void Apply()')
    $compilerPreparation = $generatedSources[0].IndexOf('DxMessaging.Editor.SetupCscRsp.PrepareCompilerInputs();', $applyStart)
    $compilationChange = $generatedSources[0].IndexOf('CompilationPipeline.codeOptimization =', $applyStart)
    $completionMarker = $generatedSources[0].IndexOf('File.WriteAllText(markerPath,', $applyStart)
    Assert-That 'configuration prepares compiler inputs before changing compilation settings and writing its success marker' (
        $compilerPreparation -gt $applyStart -and $compilerPreparation -lt $compilationChange -and
        $compilationChange -lt $completionMarker
    )
    $preparationStart = $generatedSources[0].IndexOf('public static void PrepareCompilerInputs()')
    Assert-That 'the generated configurator has a compiler-input-only entry point' ($preparationStart -ge 0 -and $preparationStart -lt $applyStart)
    $preparationMethod = $generatedSources[0].Substring($preparationStart, $applyStart - $preparationStart)
    Assert-That 'Editor preparation does not apply player or global compiler settings' (
        -not $preparationMethod.Contains('PlayerSettings') -and -not $preparationMethod.Contains('CompilationPipeline')
    )
    Add-Type -TypeDefinition @"
using System;
using System.IO;
namespace DxMessaging.Editor {
    public static class SetupCscRsp {
        public static int Calls;
        public static bool Fail;
        public static void PrepareCompilerInputs() {
            Calls++;
            if (Fail) throw new InvalidOperationException("fixture preparation failure");
        }
    }
}
public static class DxmCompilerInputsFixture {
    $preparationMethod
}
"@
    $priorPreparationMarker = $env:DXM_CONFIGURE_MARKER_PATH
    $env:DXM_CONFIGURE_MARKER_PATH = Join-Path $fixtureRoot 'compiler-marker/nested/complete.marker'
    try {
        [DxMessaging.Editor.SetupCscRsp]::Fail = $true
        Assert-Fails 'preparation errors propagate before writing completion' -ExpectedMessage 'fixture preparation failure' {
            [DxmCompilerInputsFixture]::PrepareCompilerInputs()
        }
        Assert-That 'failed preparation cannot create a success marker' (-not (Test-Path -LiteralPath $env:DXM_CONFIGURE_MARKER_PATH))
        [DxMessaging.Editor.SetupCscRsp]::Fail = $false
        [DxmCompilerInputsFixture]::PrepareCompilerInputs()
        Assert-That 'the actual generated method completes preparation before writing its marker' (
            [DxMessaging.Editor.SetupCscRsp]::Calls -eq 2 -and
            [IO.File]::ReadAllText($env:DXM_CONFIGURE_MARKER_PATH) -ceq 'DxmCiTestConfigurator.PrepareCompilerInputs completed'
        )
    } finally {
        $env:DXM_CONFIGURE_MARKER_PATH = $priorPreparationMarker
    }
    $sdkAdmissionStart = $generatedSources[0].IndexOf('public static void PrepareNativeSdkAdmission()')
    Assert-That 'standalone configuration has an opt-in prebuild SDK admission boundary' ($sdkAdmissionStart -ge 0 -and $sdkAdmissionStart -lt $preparationStart)
    $sdkAdmissionCall = $generatedSources[0].IndexOf('PrepareNativeSdkAdmission();', $applyStart)
    Assert-That 'requested SDK admission completes after compiler preparation and before configuration success' (
        $compilerPreparation -lt $sdkAdmissionCall -and $sdkAdmissionCall -lt $completionMarker
    )
    $sdkAdmissionMethod = $generatedSources[0].Substring($sdkAdmissionStart, $preparationStart - $sdkAdmissionStart)
    Add-Type -TypeDefinition @"
using System;
public static class DxmNativeSdkAdmissionFixture {
    $sdkAdmissionMethod
}
"@
    $priorSdkAdmissionPath = $env:DXM_NATIVE_SDK_FLOOR_EVIDENCE
    $env:DXM_NATIVE_SDK_FLOOR_EVIDENCE = $null
    try {
        [DxmNativeSdkAdmissionFixture]::PrepareNativeSdkAdmission()
        $env:DXM_NATIVE_SDK_FLOOR_EVIDENCE = Join-Path $fixtureRoot 'sdk-admission.json'
        Assert-Fails 'requested admission rejects a missing optional fixture' -ExpectedMessage 'exactly one optional SDK admission fixture' {
            [DxmNativeSdkAdmissionFixture]::PrepareNativeSdkAdmission()
        }
        Assert-That 'missing fixture cannot create admission evidence' (-not (Test-Path -LiteralPath $env:DXM_NATIVE_SDK_FLOOR_EVIDENCE))
        Add-Type -TypeDefinition @'
using System;
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch {
    public sealed class NativeSdkFloorAdmission {
        public static bool Fail;
        public static bool OmitEvidence;
        public static int Calls;
        public void RequireActualFloorPackagesBeforeCandidates() {
            Calls++;
            if (Fail) throw new InvalidOperationException("fixture SDK admission failure");
            if (OmitEvidence) return;
            System.IO.File.WriteAllText(Environment.GetEnvironmentVariable("DXM_NATIVE_SDK_FLOOR_EVIDENCE"), "fixture SDK admitted");
        }
    }
}
'@
        [DxMessaging.Tests.Runtime.NativeCollectionsResearch.NativeSdkFloorAdmission]::Fail = $true
        Assert-Fails 'the public admission failure propagates' -ExpectedMessage 'fixture SDK admission failure' {
            [DxmNativeSdkAdmissionFixture]::PrepareNativeSdkAdmission()
        }
        Assert-That 'failed admission cannot produce success evidence' (-not (Test-Path -LiteralPath $env:DXM_NATIVE_SDK_FLOOR_EVIDENCE))
        [DxMessaging.Tests.Runtime.NativeCollectionsResearch.NativeSdkFloorAdmission]::Fail = $false
        [DxmNativeSdkAdmissionFixture]::PrepareNativeSdkAdmission()
        Assert-That 'the actual generated boundary invokes the public instance admission method' (
            [DxMessaging.Tests.Runtime.NativeCollectionsResearch.NativeSdkFloorAdmission]::Calls -eq 2 -and
            [IO.File]::ReadAllText($env:DXM_NATIVE_SDK_FLOOR_EVIDENCE) -ceq 'fixture SDK admitted'
        )
        Assert-Fails 'stale evidence cannot satisfy a new configuration' -ExpectedMessage 'must be absent before configuration' {
            [DxmNativeSdkAdmissionFixture]::PrepareNativeSdkAdmission()
        }
        [IO.File]::Delete($env:DXM_NATIVE_SDK_FLOOR_EVIDENCE)
        [DxMessaging.Tests.Runtime.NativeCollectionsResearch.NativeSdkFloorAdmission]::OmitEvidence = $true
        Assert-Fails 'returning without admission evidence fails before success' -ExpectedMessage 'did not produce evidence' {
            [DxmNativeSdkAdmissionFixture]::PrepareNativeSdkAdmission()
        }
        [DxMessaging.Tests.Runtime.NativeCollectionsResearch.NativeSdkFloorAdmission]::OmitEvidence = $false
        [DxmNativeSdkAdmissionFixture]::PrepareNativeSdkAdmission()
        $duplicateAssembly = [System.Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly(
            [System.Reflection.AssemblyName]::new('DxmDuplicateSdkAdmissionFixture'),
            [System.Reflection.Emit.AssemblyBuilderAccess]::Run
        )
        $duplicateType = $duplicateAssembly.DefineDynamicModule('fixture').DefineType(
            'DxMessaging.Tests.Runtime.NativeCollectionsResearch.NativeSdkFloorAdmission',
            [System.Reflection.TypeAttributes]::Public
        )
        $null = $duplicateType.CreateType()
        Assert-Fails 'ambiguous optional fixture types fail before invoking admission' -ExpectedMessage 'exactly one optional SDK admission fixture' {
            [DxmNativeSdkAdmissionFixture]::PrepareNativeSdkAdmission()
        }
        Assert-That 'ambiguity cannot invoke the previously admitted fixture' (
            [DxMessaging.Tests.Runtime.NativeCollectionsResearch.NativeSdkFloorAdmission]::Calls -eq 4
        )
        $missingMethodSource = @"
using System;
public static class DxmMissingAdmissionMethodFixture { $sdkAdmissionMethod }
namespace DxMessaging.Tests.Runtime.NativeCollectionsResearch { public sealed class NativeSdkFloorAdmission {} }
"@
        $missingMethodCommand = @'
$ErrorActionPreference = 'Stop'
$source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{SOURCE}'))
Add-Type -TypeDefinition $source
try {
    [DxmMissingAdmissionMethodFixture]::PrepareNativeSdkAdmission()
    throw 'Missing public admission method was accepted.'
} catch {
    if (-not $_.Exception.Message.Contains('public SDK admission method')) { throw }
}
exit 0
'@.Replace('{SOURCE}', [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($missingMethodSource)))
        & (Get-Process -Id $PID).Path -NoProfile -NonInteractive -Command $missingMethodCommand
        Assert-That 'the isolated actual boundary rejects a missing public method' ($LASTEXITCODE -eq 0)
        $env:DXM_NATIVE_SDK_FLOOR_EVIDENCE = $null
        [DxmNativeSdkAdmissionFixture]::PrepareNativeSdkAdmission()
        Assert-That 'ordinary configuration does not invoke or inspect optional admission fixtures' (
            [DxMessaging.Tests.Runtime.NativeCollectionsResearch.NativeSdkFloorAdmission]::Calls -eq 4
        )
    } finally {
        $env:DXM_NATIVE_SDK_FLOOR_EVIDENCE = $priorSdkAdmissionPath
    }
    Assert-That 'the configurator pins OptimizeSpeed' (
        $generatedSources[0].Contains('Il2CppCodeGeneration.OptimizeSpeed')
    )
    Assert-That 'configure and build observe Unity registered package resolution' (
        $generatedSources[0].Contains('PackageInfo.GetAllRegisteredPackages()') -and
        $buildModifierSource.Contains('DxmCiTestConfigurator.WriteComparisonPackageResolution();')
    )
    Assert-That 'the build evidence reads final BuildReport options' (
        $buildModifierSource.Contains('report.summary.options')
    )
    Assert-That 'the build process records prebuild and postbuild configuration' (
        $buildModifierSource.Contains('DXM_PREBUILD_CONFIG_PROFILE_PATH') -and
        $buildModifierSource.Contains('DXM_POSTBUILD_CONFIG_PROFILE_PATH')
    )
    Assert-That 'the build evidence uses Unity ForceEnableAssertions flag' (
        $generatedSources[0].Contains('BuildOptions.ForceEnableAssertions') -and
        -not $generatedSources[0].Contains('BuildOptions.EnableAssertions')
    )
    Assert-That 'the player records Debug.isDebugBuild' (
        $generatedSources[1].Contains('Debug.isDebugBuild')
    )
    $sdkRuntimeStart = $generatedSources[1].IndexOf('    [Serializable]' + "`n" + '    private sealed class NativeSdkAssembly')
    $sdkRuntimeEnd = $generatedSources[1].IndexOf('    public void RunStarted(', $sdkRuntimeStart)
    Assert-That 'the actual callback retains optional public SDK runtime observation' ($sdkRuntimeStart -ge 0 -and $sdkRuntimeEnd -gt $sdkRuntimeStart)
    $sdkRuntimeSource = $generatedSources[1].Substring($sdkRuntimeStart, $sdkRuntimeEnd - $sdkRuntimeStart)
    Assert-That 'the callback preserves the package-owned generic SDK type through PowerShell generation' ($sdkRuntimeSource.Contains('Unity.Collections.NativeList`1'))
    Add-Type -CompilerOptions '/define:ENABLE_IL2CPP' -TypeDefinition @"
using System;
using System.IO;
public static class DxmRuntimeSdkMetadataFixture {
    private static class Application {
        public static string unityVersion = "2021.3.45f1";
        public static string platform = "WindowsPlayer";
        public static bool isEditor = false;
    }
    private static class Debug { public static bool isDebugBuild = false; }
    private static class JsonUtility {
        public static string ToJson(object evidence, bool pretty) { Last = evidence; return "fixture SDK metadata"; }
    }
    public static object Last;
    public static bool BurstEnabled = true;
    public static bool LegacyMissing;
    public static bool LegacyValid;
    public static bool LegacyEnabled;
    public static int LegacyReads;
    public static int LegacyGets;
    public static int ForcedCalls = 1;
    public static string RecorderFault = "";
    public static int ProfilerStarts;
    public static int ProfilerDisposals;
    public static bool ProfilerValid;
    private static class UnityEngine {
        public static class Profiling {
            public sealed class Recorder {
                public static Recorder Get(string name) {
                    LegacyGets++;
                    if (RecorderFault == "get") { throw new InvalidOperationException("fixture get"); }
                    return LegacyMissing ? null : new Recorder();
                }
                public bool isValid { get { return LegacyValid; } }
                public bool enabled { get { return LegacyEnabled; } set { LegacyEnabled = value; } }
                public int sampleBlockCount {
                    get {
                        if (RecorderFault == "count") { throw new InvalidOperationException("fixture count"); }
                        return LegacyReads++ == 0 ? ForcedCalls : 0;
                    }
                }
            }
        }
    }
    private static class Unity {
        public static class Profiling {
            public readonly struct ProfilerCategory { public static ProfilerCategory Memory { get { return default; } } }
            public readonly struct ProfilerRecorder : IDisposable {
                public static ProfilerRecorder StartNew(ProfilerCategory category, string name) {
                    ProfilerStarts++;
                    if (RecorderFault == "start") { throw new InvalidOperationException("fixture start"); }
                    return default;
                }
                public bool Valid {
                    get {
                        if (RecorderFault == "valid") { throw new InvalidOperationException("fixture valid"); }
                        return ProfilerValid;
                    }
                }
                public void Dispose() { ProfilerDisposals++; }
            }
        }
    }
    $sdkRuntimeSource
    public static void Capture() { WriteNativeSdkRuntimeEvidence(); }
}
"@
    $priorRuntimeSdkPath = $env:DXM_NATIVE_SDK_RUNTIME_EVIDENCE
    try {
        $env:DXM_NATIVE_SDK_RUNTIME_EVIDENCE = $null
        [DxmRuntimeSdkMetadataFixture]::Capture()
        Assert-That 'no runtime request does not create an observation' ($null -eq [DxmRuntimeSdkMetadataFixture]::Last)
        Assert-That 'no runtime request performs no recorder work' (
            [DxmRuntimeSdkMetadataFixture]::ProfilerStarts -eq 0 -and [DxmRuntimeSdkMetadataFixture]::LegacyGets -eq 0
        )
        $env:DXM_NATIVE_SDK_RUNTIME_EVIDENCE = Join-Path $fixtureRoot 'sdk/runtime.json'
        [DxmRuntimeSdkMetadataFixture]::Capture()
        Assert-That 'missing SDK assemblies remain explicit errors' (@([DxmRuntimeSdkMetadataFixture]::Last.errors).Count -eq 4)
        foreach ($entry in @(
            @{ assembly = 'Unity.Burst'; type = 'Unity.Burst.BurstCompiler' },
            @{ assembly = 'Unity.Collections'; type = 'Unity.Collections.NativeList`1' },
            @{ assembly = 'UnityEngine.CoreModule'; type = 'Unity.Collections.NativeArray`1' },
            @{ assembly = 'Unity.Mathematics'; type = 'Unity.Mathematics.math' },
            @{ assembly = 'Unity.Jobs'; type = 'Unity.Jobs.IJobParallelForBatch' }
        )) {
            $assemblyName = [System.Reflection.AssemblyName]::new($entry.assembly)
            $assemblyName.Version = [Version]::new(1, 2, 3, 4)
            $assembly = [System.Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly(
                $assemblyName, [System.Reflection.Emit.AssemblyBuilderAccess]::Run)
            $type = $assembly.DefineDynamicModule('fixture').DefineType($entry.type, [System.Reflection.TypeAttributes]::Public)
            if ($entry.assembly -ceq 'Unity.Collections' -or $entry.assembly -ceq 'UnityEngine.CoreModule') {
                $null = $type.DefineGenericParameters(@('T'))
            }
            if ($entry.assembly -ceq 'Unity.Burst') {
                $getter = $type.DefineMethod('get_IsEnabled',
                    [System.Reflection.MethodAttributes]::Public -bor [System.Reflection.MethodAttributes]::Static -bor
                    [System.Reflection.MethodAttributes]::SpecialName, [bool], [Type[]]@())
                $il = $getter.GetILGenerator()
                $il.Emit([System.Reflection.Emit.OpCodes]::Ldsfld, [DxmRuntimeSdkMetadataFixture].GetField('BurstEnabled'))
                $il.Emit([System.Reflection.Emit.OpCodes]::Ret)
                $property = $type.DefineProperty('IsEnabled', [System.Reflection.PropertyAttributes]::None, [bool], [Type[]]@())
                $property.SetGetMethod($getter)
            }
            $null = $type.CreateType()
        }
        [DxmRuntimeSdkMetadataFixture]::Capture()
        $observed = [DxmRuntimeSdkMetadataFixture]::Last
        Assert-That 'the actual generated callback captures public allocation recorder evidence' (
            $null -ne $observed.GetType().GetField('allocationRecorders')
        )
        Assert-That 'the actual managed observer captures exact public SDK metadata without compiler internals' (
            $observed.schemaVersion -eq 2 -and @($observed.errors).Count -eq 0 -and @($observed.assemblies).Count -eq 4 -and
            @($observed.assemblies | Where-Object { -not $_.typeObserved -or -not $_.assemblyVersion }).Count -eq 0 -and
            $observed.burstEnabledObserved -and $observed.burstEnabled -and $observed.il2cpp -and
            -not $observed.isEditor -and -not $observed.debugBuild -and $observed.pointerBytes -eq [IntPtr]::Size
        )
        $collectionsAssembly = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -ceq 'Unity.Collections' })[0]
        Assert-That 'NativeArray belongs to the engine module and cannot admit the Collections package' (
            $null -eq $collectionsAssembly.GetType('Unity.Collections.NativeArray`1', $false) -and
            @($observed.assemblies | Where-Object { $_.name -ceq 'Unity.Collections' })[0].typeName -ceq 'Unity.Collections.NativeList`1'
        )
        foreach ($record in $observed.assemblies) {
            Assert-That 'public assembly version components retain the exact observed version' (
                @($record.assemblyVersionComponents).Count -eq 4 -and
                ($record.assemblyVersionComponents -join '.') -ceq $record.assemblyVersion
            )
        }
        $metadataRoot = Join-Path $fixtureRoot 'sdk-metadata-redaction'
        $null = New-Item -ItemType Directory -Path $metadataRoot
        $metadataPath = Join-Path $metadataRoot 'sdk-runtime.json'
        Write-TestJson -Path $metadataPath -Value $observed
        & node (Join-Path $repoRoot 'scripts/unity/redact-unity-artifacts.js') $metadataRoot
        Assert-That 'public metadata remains safe under unchanged production redaction' ($LASTEXITCODE -eq 0)
        $redactedMetadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        foreach ($record in $redactedMetadata.assemblies) {
            Assert-That 'typed public version components survive redaction without an exemption' (
                $record.assemblyVersion -ceq '[redacted:ipv4-address]' -and
                ($record.assemblyVersionComponents -join '.') -ceq '1.2.3.4'
            )
        }
        [DxmRuntimeSdkMetadataFixture]::BurstEnabled = $false
        [DxmRuntimeSdkMetadataFixture]::Capture()
        Assert-That 'disabled Burst is an observed state rather than fabricated enabled evidence' (
            [DxmRuntimeSdkMetadataFixture]::Last.burstEnabledObserved -and -not [DxmRuntimeSdkMetadataFixture]::Last.burstEnabled
        )
        foreach ($variant in @('missing', 'invalid', 'valid', 'zero', 'get', 'count', 'start', 'valid-read')) {
            [DxmRuntimeSdkMetadataFixture]::LegacyMissing = $variant -ceq 'missing'
            [DxmRuntimeSdkMetadataFixture]::LegacyValid = $variant -cne 'invalid'
            [DxmRuntimeSdkMetadataFixture]::LegacyReads = 0
            [DxmRuntimeSdkMetadataFixture]::LegacyEnabled = $false
            [DxmRuntimeSdkMetadataFixture]::ForcedCalls = $(if ($variant -ceq 'zero') { 0 } else { 1 })
            [DxmRuntimeSdkMetadataFixture]::ProfilerValid = $variant -ceq 'valid'
            [DxmRuntimeSdkMetadataFixture]::ProfilerStarts = 0
            [DxmRuntimeSdkMetadataFixture]::ProfilerDisposals = 0
            [DxmRuntimeSdkMetadataFixture]::RecorderFault = $(if ($variant -ceq 'valid-read') { 'valid' } elseif ($variant -cin @('get', 'count', 'start')) { $variant } else { '' })
            [DxmRuntimeSdkMetadataFixture]::Capture()
            $diagnostic = [DxmRuntimeSdkMetadataFixture]::Last.allocationRecorders
            Assert-That "recorder diagnostic variant=$variant always disables legacy recording" (-not [DxmRuntimeSdkMetadataFixture]::LegacyEnabled)
            Assert-That "recorder diagnostic variant=$variant observes all three metric names" (
                @($diagnostic.profilerMetrics).Count -eq 3 -and [DxmRuntimeSdkMetadataFixture]::ProfilerStarts -eq 3
            )
            Assert-That "recorder diagnostic variant=$variant preserves observed metric validity" (
                @($diagnostic.profilerMetrics | Where-Object { $_.valid }).Count -eq $(if ($variant -ceq 'valid') { 3 } else { 0 }) -and
                @($diagnostic.profilerMetrics | Where-Object { $_.observed }).Count -eq $(if ($variant -cin @('start', 'valid-read')) { 0 } else { 3 })
            )
            Assert-That "recorder diagnostic variant=$variant disposes every constructed metric recorder" (
                [DxmRuntimeSdkMetadataFixture]::ProfilerDisposals -eq $(if ($variant -ceq 'start') { 0 } else { 3 })
            )
            if ($variant -cin @('missing', 'invalid', 'get', 'count')) {
                Assert-That "recorder diagnostic variant=$variant retains unmeasured sentinels" (
                    $diagnostic.forcedAllocationCalls -eq -1 -and $diagnostic.emptyOperationCalls -eq -1
                )
            }
            if ($variant -cin @('valid', 'zero')) {
                Assert-That "recorder diagnostic variant=$variant retains actual forced and empty counts" (
                    $diagnostic.legacyObserved -and $diagnostic.legacyPresent -and $diagnostic.legacyValid -and
                    $diagnostic.forcedAllocationCalls -eq $(if ($variant -ceq 'zero') { 0 } else { 1 }) -and
                    $diagnostic.emptyOperationCalls -eq 0
                )
            }
            Assert-That "recorder diagnostic variant=$variant preserves public API exceptions" (
                @($diagnostic.errors).Count -eq $(if ($variant -cin @('start', 'valid-read')) { 3 } elseif ($variant -cin @('get', 'count')) { 1 } else { 0 })
            )
        }
    } finally {
        $env:DXM_NATIVE_SDK_RUNTIME_EVIDENCE = $priorRuntimeSdkPath
    }

    $runnerText = Get-Content -LiteralPath $runnerPath -Raw
    $workflowText = Get-Content -LiteralPath $workflowPath -Raw
    Assert-That 'the runner archives the exact profile file' (
        $runnerText.Contains('Copy-Item -LiteralPath $resolvedCanonicalProfilePath')
    )
    foreach ($kind in @('configuration', 'buildOptions', 'runtime')) {
        Assert-That "the runner validates $kind evidence" ($runnerText.Contains("-EvidenceKind $kind"))
    }
    $evidenceCommands = @($runnerAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.CommandElements[0].Extent.Text -eq '$profileValidatorPath' -and
            @($node.CommandElements | Where-Object {
                $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                    $_.ParameterName -eq 'EvidenceKind'
            }).Count -gt 0
    }, $true))
    Assert-That 'the runner contains profile evidence validation calls' ($evidenceCommands.Count -gt 0)
    foreach ($command in $evidenceCommands) {
        $elements = @($command.CommandElements)
        $versionArguments = @(for ($index = 1; $index -lt $elements.Count - 1; ++$index) {
            if ($elements[$index] -is [System.Management.Automation.Language.CommandParameterAst] -and
                $elements[$index].ParameterName -eq 'ExpectedUnityVersion') {
                $elements[$index + 1].Extent.Text
            }
        })
        Assert-That "profile evidence validation at line $($command.Extent.StartLineNumber) binds the requested editor" (
            $versionArguments.Count -eq 1 -and $versionArguments[0] -ceq '$UnityVersion'
        )
    }
    Assert-That 'the performance standalone leg passes the canonical profile' (
        $workflowText.Contains("CanonicalProfilePath = '.github/perf/canonical-il2cpp-profile.v1.json'")
    )
    Assert-That 'canonical profile changes invalidate historical benchmark comparison' (
        $workflowText.Contains("|\.github/perf/canonical-il2cpp-profile\.v1\.json`$'")
    )
    Assert-That 'profile validator changes invalidate historical benchmark comparison' (
        $workflowText.Contains('|validate-il2cpp-profile)')
    )

    & $validatorPath -ProfilePath $profilePath -ProfileOnly -ExpectedSha256 $profileSha256

    $nativeProfilePath = Join-Path $repoRoot '.github/perf/native-sdk-il2cpp-qualification-profile.v1.json'
    $nativeProfile = Copy-JsonValue -Value $profile
    $nativeProfile.profileId = 'native-sdk-il2cpp-qualification-player-v1'
    $nativeFixtureProfilePath = Join-Path $fixtureRoot 'native-profile.json'
    Write-TestJson -Path $nativeFixtureProfilePath -Value $nativeProfile
    & $validatorPath -ProfilePath $nativeFixtureProfilePath -ProfileOnly
    $committedNativeProfile = Get-Content -LiteralPath $nativeProfilePath -Raw | ConvertFrom-Json
    Assert-That 'the qualification profile retains every reviewed Release value' (
        ($committedNativeProfile | ConvertTo-Json -Depth 10 -Compress) -ceq ($nativeProfile | ConvertTo-Json -Depth 10 -Compress)
    )
    foreach ($variant in @('complete', 'assembly', 'category', 'version', 'repeat', 'packages')) {
        $nativeArgs = @{
            UnityVersion = '2021.3.45f1'; TestMode = 'standalone';
            AssemblyNames = 'WallstopStudios.DxMessaging.Tests.00.Runtime.NativeCollectionsResearch';
            TestCategory = 'NativeSdkCpu'; CanonicalProfilePath = $nativeProfilePath;
            ArtifactsPath = (Join-Path $fixtureRoot "native-$variant-artifacts");
            ProjectPath = (Join-Path $fixtureRoot "native-$variant-project");
            CachePath = (Join-Path $fixtureRoot "native-$variant-cache");
            IncludeComparisons = $true; StandalonePlayerRunCount = 1; GenerateOnly = $true
        }
        if ($variant -ceq 'assembly') { $nativeArgs.AssemblyNames = 'WallstopStudios.DxMessaging.Tests.Runtime' }
        if ($variant -ceq 'category') { $nativeArgs.TestCategory = '!Allocation' }
        if ($variant -ceq 'version') { $nativeArgs.UnityVersion = '6000.3.16f1' }
        if ($variant -ceq 'repeat') { $nativeArgs.StandalonePlayerRunCount = 2 }
        if ($variant -ceq 'packages') { $nativeArgs.IncludeComparisons = $false }
        $generationOutput = & (Get-Process -Id $PID).Path -NoProfile -NonInteractive -File $runnerPath @nativeArgs 2>&1
        if ($variant -ceq 'complete') {
            Assert-That 'the actual complete native qualification project generates without Unity execution' ($LASTEXITCODE -eq 0)
            $callback = Get-Content -LiteralPath (Join-Path $nativeArgs.ProjectPath 'Assets/DxmCiStandaloneTestCallback/DxmCiStandaloneTestCallback.cs') -Raw
            Assert-That 'the actual native callback embeds its distinct qualification identity' ($callback.Contains($nativeProfile.profileId))
        } else {
            Assert-That "the actual runner rejects qualification scope drift $variant" (
                $LASTEXITCODE -ne 0 -and ($generationOutput | Out-String).Contains('complete old-floor scope')
            )
        }
    }

    $badProfilePath = Join-Path $fixtureRoot 'bad-profile.json'
    foreach ($semanticMutation in @(
        @{ Group = 'configuration'; Property = 'buildTarget'; Value = 'StandaloneLinux64' },
        @{ Group = 'configuration'; Property = 'il2cppCodeGeneration'; Value = 'OptimizeSize' },
        @{ Group = 'buildOptions'; Property = 'developmentBuild'; Value = $true },
        @{ Group = 'runtime'; Property = 'debugBuild'; Value = $true }
    )) {
        $mutatedProfile = Copy-JsonValue -Value $profile
        $mutatedProfile.($semanticMutation.Group).($semanticMutation.Property) = $semanticMutation.Value
        Write-TestJson -Path $badProfilePath -Value $mutatedProfile
        Assert-Fails "$($semanticMutation.Group).$($semanticMutation.Property) fixed profile value" {
            & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
        }
    }
    foreach ($kind in @('configuration', 'buildOptions', 'runtime')) {
        foreach ($property in $profile.$kind.PSObject.Properties) {
            $wrongTypeProfile = Copy-JsonValue -Value $profile
            $wrongTypeProfile.$kind.($property.Name) = if ($property.Value -is [bool]) {
                $property.Value.ToString().ToLowerInvariant()
            } else {
                $false
            }
            Write-TestJson -Path $badProfilePath -Value $wrongTypeProfile
            Assert-Fails "$kind.$($property.Name) profile type" {
                & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
            }

            $missingProfileValue = Copy-JsonValue -Value $profile
            $missingProfileValue.$kind.PSObject.Properties.Remove($property.Name)
            Write-TestJson -Path $badProfilePath -Value $missingProfileValue
            Assert-Fails "$kind.$($property.Name) missing from profile" {
                & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
            }
        }
    }

    foreach ($kind in @('configuration', 'buildOptions', 'runtime')) {
        $evidencePath = Join-Path $fixtureRoot "$kind.json"
        $evidence = [ordered]@{
            schemaVersion = $profile.schemaVersion
            profileId = $profile.profileId
            profileSha256 = $profileSha256
            evidenceKind = $kind
            unityVersion = $testUnityVersion
            values = Copy-JsonValue -Value $profile.$kind
        }
        if ($kind -ceq 'buildOptions') {
            $evidence.schemaVersion = 2
            Write-TestJson -Path $evidencePath -Value $evidence
            Assert-Fails 'clean build requires observed cache provenance' -ExpectedMessage 'missing=buildProvenance' {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind buildOptions -ExpectedUnityVersion $testUnityVersion
            }
            $evidence.buildProvenance = [ordered]@{
                playerBuildKind = 'clean'
                libraryStateBeforeBuild = 'populated'
                beeStateBeforeBuild = 'populated'
                il2cppCacheStateBeforeBuild = 'missing'
                playerOutputStateBeforeBuild = 'empty'
            }
            $changed = Copy-JsonValue -Value $evidence
            $changed.schemaVersion = 1
            Write-TestJson -Path $evidencePath -Value $changed
            Assert-Fails 'build provenance requires evidence schema 2' -ExpectedMessage 'buildOptions.schemaVersion differs' {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind buildOptions -ExpectedUnityVersion $testUnityVersion
            }
            $changed = Copy-JsonValue -Value $evidence
            $changed.buildProvenance | Add-Member -NotePropertyName unverified -NotePropertyValue 'clean'
            Write-TestJson -Path $evidencePath -Value $changed
            Assert-Fails 'build provenance rejects extra declarations' -ExpectedMessage 'extra=unverified' {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind buildOptions -ExpectedUnityVersion $testUnityVersion
            }
            foreach ($mutation in @(
                @{ Field = 'playerBuildKind'; Value = 'incremental' },
                @{ Field = 'playerBuildKind'; Value = 'Clean' },
                @{ Field = 'playerOutputStateBeforeBuild'; Value = 'populated' },
                @{ Field = 'libraryStateBeforeBuild'; Value = 'missing' },
                @{ Field = 'libraryStateBeforeBuild'; Value = 'empty' }
            )) {
                $changed = Copy-JsonValue -Value $evidence
                $changed.buildProvenance.($mutation.Field) = $mutation.Value
                Write-TestJson -Path $evidencePath -Value $changed
                Assert-Fails "build provenance rejects $($mutation.Field)=$($mutation.Value)" -ExpectedMessage 'buildProvenance' {
                    & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind buildOptions -ExpectedUnityVersion $testUnityVersion
                }
            }
            foreach ($field in $evidence.buildProvenance.Keys) {
                foreach ($invalid in @($null, $false, 1, @(), 'unknown', 'empty ')) {
                    $changed = Copy-JsonValue -Value $evidence
                    $changed.buildProvenance.$field = $invalid
                    Write-TestJson -Path $evidencePath -Value $changed
                    Assert-Fails "build provenance rejects invalid $field" {
                        & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind buildOptions -ExpectedUnityVersion $testUnityVersion
                    }
                }
                $changed = Copy-JsonValue -Value $evidence
                $changed.buildProvenance.PSObject.Properties.Remove($field)
                Write-TestJson -Path $evidencePath -Value $changed
                Assert-Fails "build provenance requires $field" -ExpectedMessage "missing=$field" {
                    & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind buildOptions -ExpectedUnityVersion $testUnityVersion
                }
            }
            foreach ($state in @('missing', 'empty', 'populated')) {
                $changed = Copy-JsonValue -Value $evidence
                $changed.buildProvenance.libraryStateBeforeBuild = $state
                $changed.buildProvenance.beeStateBeforeBuild = 'missing'
                $changed.buildProvenance.il2cppCacheStateBeforeBuild = 'missing'
                $changed.buildProvenance.playerOutputStateBeforeBuild = 'missing'
                Write-TestJson -Path $evidencePath -Value $changed
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind buildOptions -ExpectedUnityVersion $testUnityVersion
            }
            foreach ($claimedKind in @('clean', 'incremental')) {
                $changed = Copy-JsonValue -Value $evidence
                $changed.values.cleanBuildCache = $false
                $changed.buildProvenance.playerBuildKind = $claimedKind
                Write-TestJson -Path $evidencePath -Value $changed
                Assert-Fails "incremental output cannot pass clean profile when labeled $claimedKind" -ExpectedMessage 'buildOptions.cleanBuildCache differs' {
                    & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind buildOptions -ExpectedUnityVersion $testUnityVersion
                }
            }
            $incrementalProfilePath = Join-Path $repoRoot '.github/perf/shipping-fidelity-il2cpp-profile.v1.json'
            $incrementalProfile = Get-Content -LiteralPath $incrementalProfilePath -Raw | ConvertFrom-Json
            $incrementalProfileSha256 = (
                Get-FileHash -LiteralPath $incrementalProfilePath -Algorithm SHA256
            ).Hash.ToLowerInvariant()
            $incrementalEvidence = Copy-JsonValue -Value $evidence
            $incrementalEvidence.profileId = $incrementalProfile.profileId
            $incrementalEvidence.profileSha256 = $incrementalProfileSha256
            $incrementalEvidence.values = Copy-JsonValue -Value $incrementalProfile.buildOptions
            $incrementalEvidence.values.cleanBuildCache = $false
            $incrementalEvidence.buildProvenance.playerBuildKind = 'incremental'
            $incrementalEvidence.buildProvenance.libraryStateBeforeBuild = 'populated'
            $incrementalEvidence.buildProvenance.beeStateBeforeBuild = 'populated'
            $incrementalEvidence.buildProvenance.il2cppCacheStateBeforeBuild = 'populated'
            $incrementalEvidence.buildProvenance.playerOutputStateBeforeBuild = 'populated'
            Write-TestJson -Path $evidencePath -Value $incrementalEvidence
            & $validatorPath `
                -ProfilePath $incrementalProfilePath `
                -EvidencePath $evidencePath `
                -EvidenceKind buildOptions `
                -ExpectedUnityVersion $testUnityVersion `
                -ExpectedBuildFactor incremental
            foreach ($requiredPopulatedField in @(
                'libraryStateBeforeBuild',
                'beeStateBeforeBuild',
                'playerOutputStateBeforeBuild'
            )) {
                $changed = Copy-JsonValue -Value $incrementalEvidence
                $changed.buildProvenance.$requiredPopulatedField = 'empty'
                Write-TestJson -Path $evidencePath -Value $changed
                Assert-Fails "incremental build requires populated $requiredPopulatedField" -ExpectedMessage 'buildProvenance' {
                    & $validatorPath `
                        -ProfilePath $incrementalProfilePath `
                        -EvidencePath $evidencePath `
                        -EvidenceKind buildOptions `
                        -ExpectedUnityVersion $testUnityVersion `
                        -ExpectedBuildFactor incremental
                }
            }
            Write-TestJson -Path $evidencePath -Value $incrementalEvidence
            Assert-Fails 'incremental evidence cannot pass as the profile build factor' -ExpectedMessage 'buildOptions.cleanBuildCache differs' {
                & $validatorPath `
                    -ProfilePath $incrementalProfilePath `
                    -EvidencePath $evidencePath `
                    -EvidenceKind buildOptions `
                    -ExpectedUnityVersion $testUnityVersion
            }
            Assert-Fails 'build factor override is rejected for runtime evidence' -ExpectedMessage 'valid only for buildOptions evidence' {
                & $validatorPath `
                    -ProfilePath $incrementalProfilePath `
                    -EvidencePath $evidencePath `
                    -EvidenceKind runtime `
                    -ExpectedUnityVersion $testUnityVersion `
                    -ExpectedBuildFactor incremental
            }
        }
        Write-TestJson -Path $evidencePath -Value $evidence
        & $validatorPath `
            -ProfilePath $profilePath `
            -EvidencePath $evidencePath `
            -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion `
            -ExpectedSha256 $profileSha256

        foreach ($wrongVersion in @('2021.3.45f1', '6000.3.16F1', '6000.3.16f1 ')) {
            $wrongVersionEvidence = Copy-JsonValue -Value $evidence
            $wrongVersionEvidence.unityVersion = $wrongVersion
            Write-TestJson -Path $evidencePath -Value $wrongVersionEvidence
            Assert-Fails "$kind evidence from a different editor ($wrongVersion)" -ExpectedMessage "$kind.unityVersion differs" {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion
            }
        }
        Write-TestJson -Path $evidencePath -Value $evidence
        foreach ($missingExpectedVersion in @($null, '', ' ')) {
            Assert-Fails "$kind missing expected editor version" -ExpectedMessage 'ExpectedUnityVersion is required' {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $missingExpectedVersion
            }
        }

        foreach ($property in $profile.$kind.PSObject.Properties) {
            $mutated = Copy-JsonValue -Value $evidence
            $mutated.values.($property.Name) = if ($property.Value -is [bool]) {
                -not $property.Value
            } else {
                "$($property.Value)-drift"
            }
            Write-TestJson -Path $evidencePath -Value $mutated
            Assert-Fails "$kind.$($property.Name) drift" {
                & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion
            }
        }

        $missing = Copy-JsonValue -Value $evidence
        $firstPropertyName = @($profile.$kind.PSObject.Properties)[0].Name
        $missing.values.PSObject.Properties.Remove($firstPropertyName)
        Write-TestJson -Path $evidencePath -Value $missing
        Assert-Fails "$kind missing value" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion
        }

        $extra = Copy-JsonValue -Value $evidence
        $extra.values | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
        Write-TestJson -Path $evidencePath -Value $extra
        Assert-Fails "$kind extra value" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $evidencePath -EvidenceKind $kind -ExpectedUnityVersion $testUnityVersion
        }
    }

    $runtimeEvidencePath = Join-Path $fixtureRoot 'runtime.json'
    $runtimeEvidence = [ordered]@{
        schemaVersion = 1
        profileId = $profile.profileId
        profileSha256 = $profileSha256
        evidenceKind = 'runtime'
        unityVersion = $testUnityVersion
        values = [ordered]@{ debugBuild = $false }
    }
    $runtimeEvidence.unexpected = $true
    Write-TestJson -Path $runtimeEvidencePath -Value $runtimeEvidence
    Assert-Fails 'extra evidence property' {
        & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
    }

    $runtimeEvidence.Remove('unexpected')
    $runtimeEvidence.schemaVersion = '1'
    Write-TestJson -Path $runtimeEvidencePath -Value $runtimeEvidence
    Assert-Fails 'string evidence schema version' {
        & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
    }

    $runtimeEvidence.schemaVersion = 1
    foreach ($metadataField in @('profileId', 'profileSha256', 'evidenceKind', 'unityVersion')) {
        $missingMetadata = Copy-JsonValue -Value $runtimeEvidence
        $missingMetadata.PSObject.Properties.Remove($metadataField)
        Write-TestJson -Path $runtimeEvidencePath -Value $missingMetadata
        Assert-Fails "missing evidence $metadataField" -ExpectedMessage "missing=$metadataField" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
        }
    }
    $wrongMetadataValues = [ordered]@{
        profileId = 'different-profile-v1'
        profileSha256 = '0' * 64
        evidenceKind = 'configuration'
        unityVersion = $false
    }
    foreach ($metadataField in $wrongMetadataValues.Keys) {
        $wrongMetadata = Copy-JsonValue -Value $runtimeEvidence
        $wrongMetadata.schemaVersion = 1
        $wrongMetadata.$metadataField = $wrongMetadataValues[$metadataField]
        Write-TestJson -Path $runtimeEvidencePath -Value $wrongMetadata
        Assert-Fails "wrong evidence $metadataField" {
            & $validatorPath -ProfilePath $profilePath -EvidencePath $runtimeEvidencePath -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
        }
    }
    Assert-Fails 'missing evidence file' -ExpectedMessage 'does not exist' {
        & $validatorPath `
            -ProfilePath $profilePath `
            -EvidencePath (Join-Path $fixtureRoot 'missing-evidence.json') `
            -EvidenceKind runtime -ExpectedUnityVersion $testUnityVersion
    }

    Assert-Fails 'wrong expected profile hash' {
        & $validatorPath -ProfilePath $profilePath -ProfileOnly -ExpectedSha256 ('0' * 64)
    }

    foreach ($topLevelField in @('schemaVersion', 'profileId', 'configuration', 'buildOptions', 'runtime')) {
        $missingTopLevel = Copy-JsonValue -Value $profile
        $missingTopLevel.PSObject.Properties.Remove($topLevelField)
        Write-TestJson -Path $badProfilePath -Value $missingTopLevel
        Assert-Fails "missing profile $topLevelField" {
            & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
        }
    }

    $badProfile = Copy-JsonValue -Value $profile
    $badProfile.configuration | Add-Member -NotePropertyName unexpected -NotePropertyValue 'value'
    Write-TestJson -Path $badProfilePath -Value $badProfile
    Assert-Fails 'extra canonical profile property' {
        & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
    }

    $badProfile = Copy-JsonValue -Value $profile
    $badProfile.profileId = 'unsupported-il2cpp-profile-v1'
    Write-TestJson -Path $badProfilePath -Value $badProfile
    Assert-Fails 'unsupported profile ID lists every accepted profile' -ExpectedMessage (
        "Supported profileIds: 'canonical-il2cpp-verdict-player-v1', " +
        "'native-sdk-il2cpp-qualification-player-v1', " +
        "'shipping-fidelity-il2cpp-minimal-player-v1', " +
        "'shipping-fidelity-il2cpp-low-player-v1', " +
        "'shipping-fidelity-il2cpp-medium-player-v1', " +
        "'shipping-fidelity-il2cpp-player-v1'."
    ) {
        & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
    }

    $badProfile = Copy-JsonValue -Value $profile
    $badProfile.schemaVersion = '1'
    Write-TestJson -Path $badProfilePath -Value $badProfile
    Assert-Fails 'string canonical profile schema version' {
        & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
    }

    [System.IO.File]::WriteAllText($badProfilePath, '{')
    Assert-Fails 'invalid canonical profile JSON' {
        & $validatorPath -ProfilePath $badProfilePath -ProfileOnly
    }

    $sourceFixture = Join-Path $fixtureRoot 'comparison-sources'
    $packageFixture = Join-Path $fixtureRoot 'resolved-messagepipe'
    foreach ($directory in @('scripts/unity', '.github', 'Runtime')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $sourceFixture $directory) | Out-Null
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $packageFixture 'Runtime') | Out-Null
    $resolvedPackagesPath = Join-Path $fixtureRoot 'resolved-packages.json'
    $ledgerFixturePath = Join-Path $sourceFixture 'scripts/unity/comparison-semantic-ledger-v1.json'
    $catalogFixturePath = Join-Path $sourceFixture 'scripts/unity/comparison-evidence-catalog-v1.json'
    $packageSourcePath = Join-Path $packageFixture 'Runtime/Broker.cs'
    $repositorySourcePath = Join-Path $sourceFixture 'Runtime/Contract.cs'
    $contractBytes = [System.Text.Encoding]::UTF8.GetBytes("source contract`n")
    [System.IO.File]::WriteAllBytes($packageSourcePath, $contractBytes)
    $contractSha256 = (Get-FileHash -LiteralPath $packageSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($repositorySourcePath, "source contract`r`n")
    [System.IO.File]::WriteAllText((Join-Path $sourceFixture 'scripts/unity/comparison-contract-v1.schema.json'), '{}')
    Write-TestJson -Path (Join-Path $packageFixture 'package.json') -Value @{ name = 'com.cysharp.messagepipe'; version = '1.8.2' }
    Write-TestJson -Path (Join-Path $sourceFixture '.github/comparison-packages.json') -Value @{ packages = @{ 'com.cysharp.messagepipe' = '1.8.2' } }
    Write-TestJson -Path $ledgerFixturePath -Value @{ identity = @{ messagePipe = @{ version = '1.8.2' }; byteDomain = 'test bytes' } }
    $catalogFixture = @{ sources = @{
        dxToken = @{ origin = 'repository'; path = 'Runtime/Contract.cs'; sha256 = $contractSha256 }
        package = @{ origin = 'messagepipe-unity'; packagePath = 'Runtime/Broker.cs'; sha256 = $contractSha256 }
    } }
    Write-TestJson -Path $catalogFixturePath -Value $catalogFixture
    $resolvedFixture = @{ packages = @(@{ name = 'com.cysharp.messagepipe'; version = '1.8.2'; resolvedPath = $packageFixture }) }
    Write-TestJson -Path $resolvedPackagesPath -Value $resolvedFixture
    $sourceEvidence = Get-ComparisonSourceEvidence -RepoRoot $sourceFixture -ResolvedPackagesPath $resolvedPackagesPath
    Assert-That 'actual resolved package bytes and portable repository CRLF hash pass' ($sourceEvidence.sources.Count -eq 2)
    foreach ($expected in @(
        @('dxToken', 'Runtime/Contract.cs', $repositorySourcePath),
        @('package', 'Runtime/Broker.cs', $packageSourcePath)
    )) {
        $sourceRecords = @($sourceEvidence.sources | Where-Object { $_.sourceRef -ceq $expected[0] })
        Assert-That "source reference $($expected[0]) survives exactly once" ($sourceRecords.Count -eq 1)
        Assert-That "source reference $($expected[0]) retains path and both byte domains" (
            $sourceRecords[0].path -ceq $expected[1] -and
            $sourceRecords[0].sha256 -ceq $contractSha256 -and
            $sourceRecords[0].compilerInputSha256 -ceq (Get-FileHash -LiteralPath $expected[2] -Algorithm SHA256).Hash.ToLowerInvariant()
        )
    }
    Assert-That 'source evidence binds its schema and catalog' ($sourceEvidence.schemaSha256.Length -eq 64 -and $sourceEvidence.catalogSha256.Length -eq 64)
    $artifactFixture = Join-Path $fixtureRoot 'comparison-artifacts'
    $playerFixture = Join-Path $fixtureRoot 'player/DxmTestPlayer_Data/il2cpp_data/Metadata'
    New-Item -ItemType Directory -Force -Path $artifactFixture, $playerFixture | Out-Null
    $playerExecutable = Join-Path $fixtureRoot 'player/DxmTestPlayer.exe'
    # These files are hashed by the real manifest producer, never executed.
    foreach ($file in @($playerExecutable, (Join-Path $fixtureRoot 'player/GameAssembly.dll'), (Join-Path $playerFixture 'global-metadata.dat'))) {
        [System.IO.File]::WriteAllBytes($file, $contractBytes)
    }
    $sourceEvidence['playerDirectoryManifest'] = Get-StandalonePlayerManifest -ExecutablePath $playerExecutable
    $resultFixturePath = Join-Path $artifactFixture 'results.xml'
    $logFixturePath = Join-Path $artifactFixture 'player.log'
    [System.IO.File]::WriteAllText($resultFixturePath, '<test-run total="1" passed="1" failed="0" skipped="0"><test-case name="comparison" result="Passed" /></test-run>')
    [System.IO.File]::WriteAllText($logFixturePath, "Comparison fixture completed.`n")
    $sourceEvidence['runs'] = @(@{
        runIndex = 1
        unredactedResultsSha256 = (Get-FileHash -LiteralPath $resultFixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
        unredactedPlayerLogSha256 = (Get-FileHash -LiteralPath $logFixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    })
    $sourceEvidencePath = Join-Path $artifactFixture 'comparison-source-evidence.json'
    Write-JsonArtifact -Path $sourceEvidencePath -Value $sourceEvidence
    $evidenceBefore = (Get-FileHash -LiteralPath $sourceEvidencePath -Algorithm SHA256).Hash
    $redactorPath = Join-Path $repoRoot 'scripts/unity/redact-unity-artifacts.js'
    & node $redactorPath $artifactFixture
    Assert-That 'complete produced comparison evidence passes production redaction' ($LASTEXITCODE -eq 0)
    Assert-That 'public source, player and run identities survive byte-for-byte' (
        (Get-FileHash -LiteralPath $sourceEvidencePath -Algorithm SHA256).Hash -ceq $evidenceBefore
    )
    $sourceEvidence['accessToken'] = @{ nested = 'must not be accepted as a credential container' }
    Write-JsonArtifact -Path $sourceEvidencePath -Value $sourceEvidence
    & node $redactorPath $artifactFixture
    Assert-That 'true sensitive containers remain rejected' ($LASTEXITCODE -eq 2)
    foreach ($mutation in @('byte', 'missing', 'case', 'version', 'duplicate', 'hash')) {
        [System.IO.File]::WriteAllBytes($packageSourcePath, $contractBytes)
        $catalogFixture.sources.package.packagePath = 'Runtime/Broker.cs'
        $catalogFixture.sources.package.sha256 = $contractSha256
        $resolvedFixture.packages = @(@{ name = 'com.cysharp.messagepipe'; version = '1.8.2'; resolvedPath = $packageFixture })
        switch ($mutation) {
            'byte' { [System.IO.File]::AppendAllText($packageSourcePath, 'x') }
            'missing' { Remove-Item -LiteralPath $packageSourcePath }
            'case' { $catalogFixture.sources.package.packagePath = 'Runtime/broker.cs' }
            'version' { $resolvedFixture.packages[0].version = '1.8.1' }
            'duplicate' { $resolvedFixture.packages += $resolvedFixture.packages[0] }
            'hash' { $catalogFixture.sources.package.sha256 = '0' * 64 }
        }
        Write-TestJson -Path $catalogFixturePath -Value $catalogFixture
        Write-TestJson -Path $resolvedPackagesPath -Value $resolvedFixture
        Assert-Fails "comparison package rejects $mutation drift" -ExpectedMessage 'Comparison source evidence' {
            Get-ComparisonSourceEvidence -RepoRoot $sourceFixture -ResolvedPackagesPath $resolvedPackagesPath
        }
    }

    Write-Host 'IL2CPP profile contract tests passed.'
} finally {
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
