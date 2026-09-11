#Requires -Version 5.1
# Build a classic .unitypackage from the SHIPPED npm payload.
#
# Stages exactly what `npm pack` ships (the package.json files allowlist plus
# .npmignore) into an ephemeral Unity project under
# Assets/WallstopStudios/DxMessaging/, with two Assets-form adjustments:
#   - SourceGenerators/** is EXCLUDED. In UPM form those loose generator .cs
#     sources sit outside every asmdef, so Unity never compiles them; under
#     Assets/ they would land in Assembly-CSharp and fail (Microsoft.CodeAnalysis
#     is not a reference there). Assets-form consumers get the source generator
#     and analyzer from the RoslynAnalyzer-labeled DLLs shipped under
#     Runtime/Analyzers/ (their .meta files carry the label and the
#     all-platforms-disabled PluginImporter config).
#   - Samples~ is renamed to Samples so the samples import visibly.
# It then runs the provisioned Unity editor in -batchmode to
# AssetDatabase.ExportPackage the staged folder and validates the output.
#
# License discipline, the marker-file success pattern, and the Unity invocation
# idioms deliberately mirror scripts/unity/run-ci-tests.ps1 (see the comments
# there for the full rationale); keep the two scripts in sync when changing
# either. Locally, when UNITY_SERIAL/UNITY_EMAIL/UNITY_PASSWORD are absent the
# license steps are skipped (the machine is assumed already licensed).
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+f\d+$')]
    [string]$UnityVersion,

    # Final .unitypackage path. Defaults to
    # <ArtifactsPath>/com.wallstop-studios.dxmessaging-<package.json version>.unitypackage.
    [string]$OutputPath,

    [string]$RepoRoot = $(if ($env:GITHUB_WORKSPACE) { $env:GITHUB_WORKSPACE } else { (Resolve-Path ([System.IO.Path]::Combine($PSScriptRoot, '..', '..'))).Path }),

    # Uploaded as a CI artifact: keep it small (logs + the .unitypackage). The
    # staged project lives under .artifacts/unity/projects/ like the test
    # harness projects and is NOT uploaded.
    [string]$ArtifactsPath = '.artifacts/unity/release-unitypackage',

    [string]$UnityEditorPath = $env:UNITY_EDITOR_PATH,

    # Ephemeral Unity project root. Defaults to the run-ci-tests.ps1 harness
    # layout under <RepoRoot>/.artifacts/unity/projects/; the -StageOnly smoke
    # test overrides it to stage into a temp dir against the real payload.
    [string]$ProjectPath,

    # Stage the project and generate the exporter without running Unity
    # (local inspection of the staged payload).
    [switch]$StageOnly,

    # After export, install the exact Git revision, npm tarball, and classic
    # package into separate clean projects and run the shipped sample contracts.
    [switch]$VerifyConsumerInstalls,

    [string]$GitRevision,

    [ValidateSet('Local', 'Central')]
    [string]$LicenseReturnOwner = 'Local'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Mirrors run-ci-tests.ps1: keep $LASTEXITCODE-based native-exit handling
# authoritative on every PowerShell host/version.
$PSNativeCommandUseErrorActionPreference = $false

$PackageName = 'com.wallstop-studios.dxmessaging'
$ExportRootRelative = 'Assets/WallstopStudios/DxMessaging'
$ProjectOwnershipMarkerName = '.dxmessaging-unitypackage-project'
$ProjectOwnershipMarkerContent = 'com.wallstop-studios.dxmessaging unitypackage ephemeral project'
# Top-level payload entries that must NOT ship in the Assets-form package; see
# the header comment. SourceGenerators.meta pairs with the excluded folder.
$ExcludedPayloadEntries = @('SourceGenerators', 'SourceGenerators.meta')

function Write-CiError {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::error::$Message"
}

function Clear-NonFatalNativeExitCode {
    # GitHub Actions' pwsh wrapper exits with $LASTEXITCODE after a script returns.
    # Any native exit code that this script has already captured and deliberately
    # downgraded to non-fatal must be scrubbed, or cleanup noise can turn a valid
    # artifact-verified run red after the script reaches the end normally.
    param([Parameter(Mandatory = $true)][string]$Context)

    $global:LASTEXITCODE = 0
    Write-Verbose "Cleared non-fatal native exit code after $Context."
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
}

function Get-PathStringComparison {
    # This comparison gates Remove-Item -Recurse. Be conservative across default
    # Windows/macOS case-insensitive filesystems; false-positive rejections are safer
    # than missing a case-variant spelling of a protected directory.
    return [System.StringComparison]::OrdinalIgnoreCase
}

function ConvertTo-ComparableFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullPath = $fullPath.Replace(
        [System.IO.Path]::AltDirectorySeparatorChar,
        [System.IO.Path]::DirectorySeparatorChar
    )
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if (-not [string]::IsNullOrEmpty($root)) {
        $root = $root.Replace(
            [System.IO.Path]::AltDirectorySeparatorChar,
            [System.IO.Path]::DirectorySeparatorChar
        )
        $separator = [string][System.IO.Path]::DirectorySeparatorChar
        while (
            $fullPath.Length -gt $root.Length -and
            $fullPath.EndsWith($separator, [System.StringComparison]::Ordinal)
        ) {
            $fullPath = $fullPath.Substring(0, $fullPath.Length - 1)
        }
    }
    return $fullPath
}

function Test-IsPathEqual {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    return [string]::Equals(
        (ConvertTo-ComparableFullPath -Path $Left),
        (ConvertTo-ComparableFullPath -Path $Right),
        (Get-PathStringComparison)
    )
}

function Test-IsPathInsideDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $candidatePath = ConvertTo-ComparableFullPath -Path $Path
    $directoryPath = ConvertTo-ComparableFullPath -Path $Directory
    if ([string]::Equals($candidatePath, $directoryPath, (Get-PathStringComparison))) {
        return $false
    }

    $separator = [string][System.IO.Path]::DirectorySeparatorChar
    $directoryPrefix = if ($directoryPath.EndsWith($separator, [System.StringComparison]::Ordinal)) {
        $directoryPath
    } else {
        "$directoryPath$separator"
    }
    return $candidatePath.StartsWith($directoryPrefix, (Get-PathStringComparison))
}

function Test-IsFilesystemRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = ConvertTo-ComparableFullPath -Path $Path
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrEmpty($root)) {
        return $false
    }
    $root = ConvertTo-ComparableFullPath -Path $root
    return [string]::Equals($fullPath, $root, (Get-PathStringComparison))
}

function Test-IsReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    return (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Test-PathContainsReparsePointBeforeBoundary {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BoundaryDirectory
    )

    $current = ConvertTo-ComparableFullPath -Path $Path
    $boundary = ConvertTo-ComparableFullPath -Path $BoundaryDirectory
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-IsPathEqual -Left $current -Right $boundary) {
            return $false
        }
        if (Test-IsReparsePoint -Path $current) {
            return $true
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrEmpty($parent) -or (Test-IsPathEqual -Left $parent -Right $current)) {
            return $false
        }
        $current = $parent
    }

    return $false
}

function Test-ProjectOwnershipMarker {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $markerPath = Join-Path $ProjectPath $ProjectOwnershipMarkerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        return $false
    }

    try {
        $markerContent = Get-Content -LiteralPath $markerPath -Raw
    } catch {
        return $false
    }
    if ($null -eq $markerContent) {
        return $false
    }
    $markerContent = $markerContent.Trim()
    return [string]::Equals(
        $markerContent,
        $ProjectOwnershipMarkerContent,
        [System.StringComparison]::Ordinal
    )
}

function Write-ProjectOwnershipMarker {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $markerPath = Join-Path $ProjectPath $ProjectOwnershipMarkerName
    [System.IO.File]::WriteAllText(
        $markerPath,
        "$ProjectOwnershipMarkerContent`n",
        [System.Text.Encoding]::UTF8
    )
}

function Test-IsManagedUnityPackageProjectPath {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$ManagedProjectsRoot
    )

    if (-not (Test-IsPathInsideDirectory -Path $ProjectPath -Directory $ManagedProjectsRoot)) {
        return $false
    }

    $leafName = Split-Path -Leaf (ConvertTo-ComparableFullPath -Path $ProjectPath)
    return $leafName.EndsWith('-unitypackage', (Get-PathStringComparison))
}

function Get-UnityPackageProjectPathSafetyError {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactsPath,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    if (Test-IsFilesystemRoot -Path $ProjectPath) {
        return "Refusing to use ProjectPath '$ProjectPath' because it resolves to a filesystem root."
    }

    $managedProjectsRoot = Resolve-FullPath -Path ([System.IO.Path]::Combine($RepoRoot, '.artifacts', 'unity', 'projects'))
    $outputDirectory = Split-Path -Parent $OutputPath
    if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
        $outputDirectory = (Get-Location).Path
    }
    $outputDirectory = Resolve-FullPath -Path $outputDirectory

    $reservedPaths = @(
        @{ Path = $RepoRoot; Label = 'repository root' },
        @{ Path = $ArtifactsPath; Label = 'artifacts directory' },
        @{ Path = $outputDirectory; Label = 'unitypackage output directory' },
        @{ Path = $managedProjectsRoot; Label = 'managed Unity project parent directory' }
    )
    foreach ($reservedPath in $reservedPaths) {
        if (Test-IsPathEqual -Left $ProjectPath -Right $reservedPath.Path) {
            return "Refusing to use ProjectPath '$ProjectPath' because it resolves to the $($reservedPath.Label)."
        }
    }

    if (Test-IsPathInsideDirectory -Path $RepoRoot -Directory $ProjectPath) {
        return "Refusing to use ProjectPath '$ProjectPath' because deleting it would remove a parent of the repository root '$RepoRoot'."
    }
    if (Test-IsPathInsideDirectory -Path $ArtifactsPath -Directory $ProjectPath) {
        return "Refusing to use ProjectPath '$ProjectPath' because deleting it would remove the artifacts directory '$ArtifactsPath'."
    }
    if (Test-IsPathInsideDirectory -Path $outputDirectory -Directory $ProjectPath) {
        return "Refusing to use ProjectPath '$ProjectPath' because deleting it would remove the unitypackage output directory '$outputDirectory'."
    }
    if (Test-IsPathInsideDirectory -Path $ProjectPath -Directory $ArtifactsPath) {
        return "Refusing to use ProjectPath '$ProjectPath' because it would place the generated Unity project inside the uploaded artifacts directory '$ArtifactsPath'."
    }

    $isManagedProjectPath = Test-IsManagedUnityPackageProjectPath `
        -ProjectPath $ProjectPath `
        -ManagedProjectsRoot $managedProjectsRoot
    if (
        $isManagedProjectPath -and
        (Test-PathContainsReparsePointBeforeBoundary -Path $ProjectPath -BoundaryDirectory $RepoRoot)
    ) {
        return "Refusing to use ProjectPath '$ProjectPath' because a symlink or reparse point appears between it and the repository root '$RepoRoot'."
    }
    if (
        (Test-IsPathInsideDirectory -Path $ProjectPath -Directory $RepoRoot) -and
        -not $isManagedProjectPath
    ) {
        return "Refusing to use ProjectPath '$ProjectPath' inside the repository. Repo-contained export projects must live under '$managedProjectsRoot' and end with '-unitypackage'."
    }

    if (
        (Test-Path -LiteralPath $ProjectPath -PathType Container) -and
        -not $isManagedProjectPath -and
        (Test-IsReparsePoint -Path $ProjectPath)
    ) {
        return "Refusing to delete existing ProjectPath '$ProjectPath' because it is a symlink or reparse point."
    }

    if (
        (Test-Path -LiteralPath $ProjectPath -PathType Container) -and
        -not $isManagedProjectPath -and
        -not (Test-ProjectOwnershipMarker -ProjectPath $ProjectPath)
    ) {
        return "Refusing to delete existing ProjectPath '$ProjectPath' because it is outside the managed Unity package project area and lacks the ownership marker '$ProjectOwnershipMarkerName'. Choose a new empty ProjectPath or remove the directory manually."
    }

    return ''
}

function Test-IsWindowsHost {
    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function Resolve-NpmPackCommand {
    # On Windows, setup-node installs both npm.ps1 and npm.cmd. Calling bare
    # `npm` from pwsh can resolve through a shim that mangles arguments when
    # inherited from Git Bash CI, so choose the batch shim explicitly.
    $commandName = if (Test-IsWindowsHost) { 'npm.cmd' } else { 'npm' }
    $command = Get-Command -Name $commandName -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $command) {
        throw "Unable to find $commandName on PATH; setup Node.js before running the Unity package exporter."
    }
    if ([string]::IsNullOrWhiteSpace($command.Source)) {
        return $commandName
    }
    return $command.Source
}

function Invoke-UnityEditor {
    # Mirrors run-ci-tests.ps1 Invoke-UnityEditor: `-logFile -` + Tee-Object so
    # PowerShell waits for the GUI-subsystem Unity.exe and $LASTEXITCODE is set;
    # RETURNS the exit code (the durable artifact is the source of truth).
    param(
        [Parameter(Mandatory = $true)][string]$EditorPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $logDir = Split-Path -Parent $LogPath
    if ($logDir -and -not (Test-Path -LiteralPath $logDir -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }

    Write-Host "::group::$Label"
    Write-Host "`"$EditorPath`" $($Arguments -join ' ')"
    & $EditorPath @Arguments 2>&1 | Tee-Object -FilePath $LogPath | Out-Host
    $exitCode = $LASTEXITCODE
    Clear-NonFatalNativeExitCode -Context $Label
    Write-Host "::endgroup::"
    return $exitCode
}

function Invoke-UnityLicenseActivate {
    # Mirrors run-ci-tests.ps1: THROWS on failure; never echoes the credential
    # arguments; the log stays under the non-uploaded temp dir.
    param(
        [Parameter(Mandatory = $true)][string]$EditorPath,
        [Parameter(Mandatory = $true)][string]$Serial,
        [Parameter(Mandatory = $true)][string]$Email,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $activateArgs = @(
        '-quit',
        '-batchmode',
        '-nographics',
        '-serial', $Serial,
        '-username', $Email,
        '-password', $Password,
        '-logFile', '-'
    )
    Write-Host "::group::Activate Unity license (serial)"
    & $EditorPath @activateArgs 2>&1 | Tee-Object -FilePath $LogPath | Out-Host
    $exitCode = $LASTEXITCODE
    Write-Host "::endgroup::"
    if ($exitCode -ne 0) {
        throw "Unity license activation failed with exit code $exitCode. See the activation log at $LogPath (not uploaded as an artifact)."
    }
    Write-Host "::notice::Activated the Unity license (serial)."
}

function Invoke-UnityLicenseReturn {
    # Mirrors run-ci-tests.ps1: best-effort, NEVER throws. The workflow
    # if: always() return-unity-license step and the next run's return-at-start
    # are the backstops for a leaked seat.
    param(
        [Parameter(Mandatory = $true)][string]$EditorPath,
        [Parameter(Mandatory = $true)][string]$Email,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    try {
        $returnArgs = @(
            '-quit',
            '-batchmode',
            '-nographics',
            '-returnlicense',
            '-username', $Email,
            '-password', $Password,
            '-logFile', '-'
        )
        Write-Host "::group::Return Unity license (serial)"
        # Return output may contain account or serial fragments. Keep it only in
        # the private evidence log consumed by central classification.
        & $EditorPath @returnArgs 2>&1 | Out-File -FilePath $LogPath -Encoding utf8
        $exitCode = $LASTEXITCODE
        Add-Content -LiteralPath $LogPath -Value "exit_return_rc=$exitCode" -Encoding utf8
        Write-Host "::endgroup::"
        if ($exitCode -ne 0) {
            Write-Host "::warning::Unity license return exited with code $exitCode; the workflow if:always() return step and the next run's return-at-start are the backstops for the leaked seat."
        } else {
            Write-Host "::notice::Returned the Unity license (serial)."
        }
    } catch {
        Write-Host "::warning::Unity license return failed: $($_.Exception.Message). The workflow if:always() return step and the next run's return-at-start are the backstops."
    } finally {
        Clear-NonFatalNativeExitCode -Context 'Unity license return cleanup'
    }
}

function New-ExporterSource {
    # Generated into Assets/Editor/ (OUTSIDE the exported payload root, so the
    # exporter itself never ships in the .unitypackage). Writes the success
    # marker as its FINAL action, mirroring DxmCiTestConfigurator.Apply in
    # run-ci-tests.ps1: a fresh marker proves Export() completed even when
    # Unity crashes in a background thread during teardown.
    return @'
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class DxmUnityPackageExporter
{
    public static void Export()
    {
        string outputPath = Environment.GetEnvironmentVariable("DXM_UNITYPACKAGE_OUTPUT");
        if (string.IsNullOrEmpty(outputPath))
        {
            throw new InvalidOperationException("DXM_UNITYPACKAGE_OUTPUT is not set.");
        }
        const string exportRoot = "Assets/WallstopStudios/DxMessaging";
        if (!AssetDatabase.IsValidFolder(exportRoot))
        {
            throw new InvalidOperationException(
                "Export root " + exportRoot + " was not imported as a folder."
            );
        }
        string outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        AssetDatabase.ExportPackage(
            new[] { exportRoot },
            outputPath,
            ExportPackageOptions.Recurse
        );
        FileInfo exported = new FileInfo(outputPath);
        if (!exported.Exists || exported.Length == 0)
        {
            throw new InvalidOperationException(
                "AssetDatabase.ExportPackage produced no package at " + outputPath + "."
            );
        }
        Debug.Log("DxmUnityPackageExporter: exported " + outputPath + ".");
        string markerPath = Environment.GetEnvironmentVariable("DXM_EXPORT_MARKER_PATH");
        if (!string.IsNullOrEmpty(markerPath))
        {
            string markerDirectory = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(markerDirectory))
            {
                Directory.CreateDirectory(markerDirectory);
            }
            File.WriteAllText(markerPath, "DxmUnityPackageExporter.Export completed");
        }
    }
}
'@
}

function New-DeterministicFolderMeta {
    # Standard Unity folderAsset .meta whose GUID is derived ONLY from the
    # project-relative folder path (MD5 of a fixed seed + the path, formatted
    # as a 32-hex GUID), so the exported .unitypackage GUIDs for these folders
    # are byte-stable across releases instead of freshly minted by Unity on
    # every import. MD5 is a stable fingerprint here, not a security boundary.
    param([Parameter(Mandatory = $true)][string]$RelativeFolderPath)

    $seed = "com.wallstop-studios.dxmessaging:folder-meta:v1:$RelativeFolderPath"
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($seed))
    } finally {
        $md5.Dispose()
    }
    $guidHex = ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    return @(
        'fileFormatVersion: 2',
        "guid: $guidHex",
        'folderAsset: yes',
        'DefaultImporter:',
        '  externalObjects: {}',
        '  userData:',
        '  assetBundleName:',
        '  assetBundleVariant:',
        ''
    ) -join "`n"
}

function New-ConsumerSampleImporterSource {
    return @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DxMessaging.Core;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.UI;
using UnityEngine;

public static class DxmConsumerSampleImporter
{
    public static void Import()
    {
        string markerPath = Environment.GetEnvironmentVariable("DXM_CONSUMER_IMPORT_MARKER");
        if (string.IsNullOrEmpty(markerPath))
        {
            throw new InvalidOperationException("DXM_CONSUMER_IMPORT_MARKER is not set.");
        }

        PackageInfo package = PackageInfo.FindForAssembly(typeof(MessageHandler).Assembly);
        if (package == null || package.name != "com.wallstop-studios.dxmessaging")
        {
            throw new InvalidOperationException("The installed DxMessaging package was not resolved.");
        }

        string[] expected =
        {
            "Mini Combat",
            "UI Buttons + Inspector",
            "Diagnostics Tooling Exerciser",
            "Dependency Injection",
        };
        Dictionary<string, Sample> samples = Sample.FindByPackage(package.name, package.version)
            .ToDictionary(sample => sample.displayName, StringComparer.Ordinal);
        if (!expected.All(samples.ContainsKey) || samples.Count != expected.Length)
        {
            throw new InvalidOperationException(
                "Expected exactly the four shipped samples; found: " + string.Join(", ", samples.Keys)
            );
        }

        string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
        Dictionary<string, string> importedPaths = new(StringComparer.Ordinal);
        foreach (string displayName in expected)
        {
            Sample sample = samples[displayName];
            if (!sample.Import(Sample.ImportOptions.OverridePreviousImports | Sample.ImportOptions.HideImportWindow))
            {
                throw new InvalidOperationException("Failed to import sample " + displayName + ".");
            }
            string importedPath = sample.importPath.Replace('\\', '/');
            if (Path.IsPathRooted(importedPath))
            {
                string projectPrefix = projectRoot + "/";
                if (!importedPath.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Imported sample is outside the consumer project: " + importedPath);
                }
                importedPath = importedPath.Substring(projectPrefix.Length);
            }
            string absoluteImportedPath = Path.Combine(projectRoot, importedPath).Replace('\\', '/');
            if (!Directory.Exists(absoluteImportedPath))
            {
                throw new InvalidOperationException("Imported sample path is missing: " + sample.importPath);
            }
            importedPaths[displayName] = importedPath;
        }

        string samplesRoot = Path.GetDirectoryName(importedPaths[expected[0]]).Replace('\\', '/');
        string markerDirectory = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(markerDirectory))
        {
            Directory.CreateDirectory(markerDirectory);
        }
        File.WriteAllLines(markerPath, new[]
        {
            "package=" + package.name,
            "version=" + package.version,
            "resolvedPath=" + package.resolvedPath.Replace('\\', '/'),
            "samplesRoot=" + samplesRoot,
            "samples=" + string.Join("|", expected),
        });
    }
}
'@
}

function New-ConsumerProject {
    param(
        [Parameter(Mandatory = $true)][string]$ConsumerPath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Dependency,
        [Parameter(Mandatory = $true)][bool]$IncludeImporter
    )

    New-Item -ItemType Directory -Force -Path ([System.IO.Path]::Combine($ConsumerPath, 'Assets', 'Editor')) | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $ConsumerPath 'Packages') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $ConsumerPath 'ProjectSettings') | Out-Null
    $dependencies = [ordered]@{ 'com.unity.test-framework' = '1.4.5' }
    if (-not [string]::IsNullOrWhiteSpace($Dependency)) {
        $dependencies[$PackageName] = $Dependency
    }
    foreach ($module in $builtinModules.PSObject.Properties) {
        $dependencies[$module.Name] = $module.Value
    }
    $manifest = [ordered]@{ dependencies = $dependencies }
    [System.IO.File]::WriteAllText(
        ([System.IO.Path]::Combine($ConsumerPath, 'Packages', 'manifest.json')),
        (($manifest | ConvertTo-Json -Depth 5) + "`n"),
        (New-Object System.Text.UTF8Encoding $false)
    )
    "m_EditorVersion: $UnityVersion`n" |
        Set-Content -LiteralPath ([System.IO.Path]::Combine($ConsumerPath, 'ProjectSettings', 'ProjectVersion.txt')) -Encoding UTF8
    @'
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!159 &1
EditorSettings:
  m_EnterPlayModeOptionsEnabled: 1
  m_EnterPlayModeOptions: 3
'@ | Set-Content -LiteralPath ([System.IO.Path]::Combine($ConsumerPath, 'ProjectSettings', 'EditorSettings.asset')) -Encoding UTF8
    @('-warnaserror', '-warn:9999') |
        Set-Content -LiteralPath ([System.IO.Path]::Combine($ConsumerPath, 'Assets', 'csc.rsp')) -Encoding UTF8
    if ($IncludeImporter) {
        New-ConsumerSampleImporterSource |
            Set-Content -LiteralPath ([System.IO.Path]::Combine($ConsumerPath, 'Assets', 'Editor', 'DxmConsumerSampleImporter.cs')) -Encoding UTF8
    }
}

function Add-ConsumerSampleTests {
    param([Parameter(Mandatory = $true)][string]$ConsumerPath)

    $testRoot = [System.IO.Path]::Combine($ConsumerPath, 'Assets', 'Editor', 'ConsumerAcceptance')
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'Tests/Editor/SampleQualityContractTests.cs') -Destination $testRoot
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'Tests/Editor/OwnedEditModeScene.cs') -Destination $testRoot
    $asmdef = [ordered]@{
        name = 'Dxm.ConsumerAcceptance.Tests.Editor'
        rootNamespace = 'DxMessaging.Tests.Editor'
        references = @('UnityEditor.TestRunner', 'UnityEngine.TestRunner', 'WallstopStudios.DxMessaging')
        includePlatforms = @('Editor')
        excludePlatforms = @()
        allowUnsafeCode = $false
        overrideReferences = $true
        precompiledReferences = @('nunit.framework.dll')
        autoReferenced = $true
        defineConstraints = @('UNITY_INCLUDE_TESTS')
        versionDefines = @()
        noEngineReferences = $false
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $testRoot 'Dxm.ConsumerAcceptance.Tests.Editor.asmdef'),
        (($asmdef | ConvertTo-Json -Depth 5) + "`n"),
        (New-Object System.Text.UTF8Encoding $false)
    )
}

function Get-ConsumerMarkerValues {
    param([Parameter(Mandatory = $true)][string]$MarkerPath)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $MarkerPath) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $values[$parts[0]] = $parts[1]
        }
    }
    return $values
}

function Assert-ConsumerResults {
    param(
        [Parameter(Mandatory = $true)][string]$ResultPath,
        [Parameter(Mandatory = $true)][string]$SourceKind
    )

    if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
        throw "$SourceKind consumer wrote no NUnit result at $ResultPath."
    }
    [xml]$result = Get-Content -LiteralPath $ResultPath -Raw
    $run = $result.'test-run'
    $total = [int]$run.total
    $passed = [int]$run.passed
    $failed = [int]$run.failed
    $skipped = [int]$run.skipped
    if ($total -ne 8 -or $passed -ne 8 -or $failed -ne 0 -or $skipped -ne 0) {
        throw "$SourceKind consumer expected 8 passed / 0 failed / 0 skipped; got total=$total passed=$passed failed=$failed skipped=$skipped."
    }
    return [ordered]@{ total = $total; passed = $passed; failed = $failed; skipped = $skipped }
}

function Invoke-ConsumerInstallVerification {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('git', 'tarball', 'classic')]
        [string]$SourceKind,
        [Parameter(Mandatory = $true)][string]$ConsumerPath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Dependency,
        [Parameter(Mandatory = $true)][string]$EvidenceRoot,
        [Parameter(Mandatory = $true)][string]$TarballPayloadRoot
    )

    $sourceEvidence = Join-Path $EvidenceRoot $SourceKind
    if (Test-Path -LiteralPath $sourceEvidence -PathType Container) {
        Remove-Item -LiteralPath $sourceEvidence -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $sourceEvidence | Out-Null
    New-ConsumerProject -ConsumerPath $ConsumerPath -Dependency $Dependency -IncludeImporter ($SourceKind -ne 'classic')
    $samplesRoot = 'Assets/WallstopStudios/DxMessaging/Samples'
    $importLog = Join-Path $sourceEvidence 'install-and-import.log'
    if ($SourceKind -eq 'classic') {
        $importArgs = @('-quit', '-batchmode', '-nographics', '-projectPath', $ConsumerPath, '-importPackage', $OutputPath, '-logFile', '-')
        $importExit = Invoke-UnityEditor -EditorPath $UnityEditorPath -Arguments $importArgs -Label "Import classic package ($UnityVersion)" -LogPath $importLog
        if ($importExit -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $ConsumerPath $samplesRoot) -PathType Container)) {
            throw "Classic consumer import failed with exit code $importExit."
        }
        $markerValues = @{ package = $PackageName; version = $packageVersion; resolvedPath = $OutputPath; samplesRoot = $samplesRoot; samples = (($packageJson.samples.displayName) -join '|') }
    } else {
        $markerPath = Join-Path $sourceEvidence 'import-complete.marker'
        if (Test-Path -LiteralPath $markerPath) { Remove-Item -LiteralPath $markerPath -Force }
        $env:DXM_CONSUMER_IMPORT_MARKER = $markerPath
        try {
            $importArgs = @('-quit', '-batchmode', '-nographics', '-projectPath', $ConsumerPath, '-executeMethod', 'DxmConsumerSampleImporter.Import', '-logFile', '-')
            $importExit = Invoke-UnityEditor -EditorPath $UnityEditorPath -Arguments $importArgs -Label "Install and import $SourceKind package ($UnityVersion)" -LogPath $importLog
        } finally {
            Remove-Item -LiteralPath Env:\DXM_CONSUMER_IMPORT_MARKER -ErrorAction SilentlyContinue
        }
        if ($importExit -ne 0 -or -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "$SourceKind consumer import failed with exit code $importExit or no completion marker."
        }
        $markerValues = Get-ConsumerMarkerValues -MarkerPath $markerPath
        $samplesRoot = $markerValues.samplesRoot
        if ($markerValues.package -ne $PackageName -or $markerValues.version -ne $packageVersion) {
            throw "$SourceKind consumer resolved $($markerValues.package)@$($markerValues.version), expected $PackageName@$packageVersion."
        }
        $lock = Get-Content -LiteralPath (Join-Path $ConsumerPath 'Packages/packages-lock.json') -Raw | ConvertFrom-Json
        $lockEntry = $lock.dependencies.$PackageName
        if ($SourceKind -eq 'git') {
            if ($lockEntry.source -ne 'git' -or $lockEntry.hash -ne $GitRevision) {
                throw "Git consumer lock provenance mismatch: source=$($lockEntry.source), hash=$($lockEntry.hash), expected=$GitRevision."
            }
        } elseif ($SourceKind -eq 'tarball') {
            $contentMismatches = @()
            foreach ($relativePath in @('Runtime/Core/MessageHandler.cs', 'Samples~/Mini Combat/MiniCombat.unity')) {
                $expectedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $TarballPayloadRoot $relativePath)).Hash
                $resolvedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $markerValues.resolvedPath $relativePath)).Hash
                if ($resolvedHash -ne $expectedHash) {
                    $contentMismatches += "$relativePath resolved=$resolvedHash expected=$expectedHash"
                }
            }
            if ($lockEntry.source -ne 'local-tarball' -or $contentMismatches.Count -gt 0) {
                throw "Tarball consumer provenance mismatch: source=$($lockEntry.source), immutable content=$($contentMismatches -join '; ')."
            }
        }
    }

    $expectedSamples = ($packageJson.samples.displayName) -join '|'
    if ($markerValues.samples -ne $expectedSamples) {
        throw "$SourceKind consumer sample roster mismatch: $($markerValues.samples)."
    }
    Add-ConsumerSampleTests -ConsumerPath $ConsumerPath
    $resultPath = Join-Path $sourceEvidence 'test-results.xml'
    $env:DXM_IMPORTED_SAMPLES_ROOT = $samplesRoot
    try {
        $testArgs = @('-batchmode', '-nographics', '-projectPath', $ConsumerPath, '-runTests', '-testPlatform', 'EditMode', '-testResults', $resultPath, '-assemblyNames', 'Dxm.ConsumerAcceptance.Tests.Editor', '-testCategory', 'CiImportedSampleFixture', '-releaseCodeOptimization', '-logFile', '-')
        $testExit = Invoke-UnityEditor -EditorPath $UnityEditorPath -Arguments $testArgs -Label "Run $SourceKind consumer sample contracts ($UnityVersion)" -LogPath (Join-Path $sourceEvidence 'tests.log')
    } finally {
        Remove-Item -LiteralPath Env:\DXM_IMPORTED_SAMPLES_ROOT -ErrorAction SilentlyContinue
    }
    $counts = Assert-ConsumerResults -ResultPath $resultPath -SourceKind $SourceKind
    $artifactHash = if ($SourceKind -eq 'classic') { (Get-FileHash -Algorithm SHA256 -LiteralPath $OutputPath).Hash } elseif ($SourceKind -eq 'tarball') { (Get-FileHash -Algorithm SHA256 -LiteralPath $tarballPath).Hash } else { $GitRevision }
    [ordered]@{
        source = $SourceKind
        unityVersion = $UnityVersion
        package = "$PackageName@$packageVersion"
        provenance = $artifactHash
        samplesRoot = $samplesRoot
        samples = @($packageJson.samples.displayName)
        results = $counts
        unityExitCode = $testExit
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $sourceEvidence 'evidence.json') -Encoding UTF8
}

function Test-ExportMarker {
    # '' when the marker exists and is fresh for this run; else the problem text.
    param(
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)][DateTime]$StartedUtc
    )
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) {
        return 'export marker was not written (DxmUnityPackageExporter.Export did not run to completion)'
    }
    $marker = Get-Item -LiteralPath $MarkerPath
    if ($marker.LastWriteTimeUtc -lt $StartedUtc.AddSeconds(-5)) {
        return "stale export marker; LastWriteTimeUtc=$($marker.LastWriteTimeUtc.ToString('o'))"
    }
    return ''
}

function Test-UnityPackageOutput {
    # '' when the .unitypackage is a fresh, nonempty gzip; else the problem text.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][DateTime]$StartedUtc
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return "no .unitypackage was written at $Path"
    }
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -le 0) {
        return "the .unitypackage at $Path is empty"
    }
    if ($file.LastWriteTimeUtc -lt $StartedUtc.AddSeconds(-5)) {
        return "stale .unitypackage at $Path; LastWriteTimeUtc=$($file.LastWriteTimeUtc.ToString('o'))"
    }
    # A .unitypackage is a gzipped tar; validate the gzip magic bytes.
    $stream = [System.IO.File]::OpenRead($file.FullName)
    try {
        $header = New-Object byte[] 2
        $read = $stream.Read($header, 0, 2)
        if ($read -ne 2 -or $header[0] -ne 0x1F -or $header[1] -ne 0x8B) {
            return "the file at $Path is not a gzip archive (bad magic bytes)"
        }
    } finally {
        $stream.Dispose()
    }
    return ''
}

$RepoRoot = Resolve-FullPath -Path $RepoRoot
$ArtifactsPath = Resolve-FullPath -Path $ArtifactsPath
New-Item -ItemType Directory -Force -Path $ArtifactsPath | Out-Null

$packageJsonPath = Join-Path $RepoRoot 'package.json'
$packageVersion = (Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json).version
if ($VerifyConsumerInstalls -and $GitRevision -notmatch '^[0-9a-fA-F]{40}$') {
    Write-CiError 'GitRevision must be the full 40-character commit SHA when VerifyConsumerInstalls is enabled.'
    exit 1
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $ArtifactsPath "$PackageName-$packageVersion.unitypackage"
}
$OutputPath = Resolve-FullPath -Path $OutputPath

if (-not $StageOnly) {
    if ([string]::IsNullOrWhiteSpace($UnityEditorPath)) {
        Write-CiError 'UnityEditorPath is required (set UNITY_EDITOR_PATH or pass -UnityEditorPath); CI must validate a manually installed editor before export.'
        exit 1
    }
    if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
        Write-CiError "Unity editor not found at $UnityEditorPath."
        exit 1
    }
}

# Ephemeral project location mirrors the run-ci-tests.ps1 harness layout and is
# NOT uploaded as an artifact (only $ArtifactsPath is).
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = [System.IO.Path]::Combine($RepoRoot, '.artifacts', 'unity', 'projects', "$UnityVersion-unitypackage")
}
$projectPath = Resolve-FullPath -Path $ProjectPath
$projectPathSafetyError = Get-UnityPackageProjectPathSafetyError `
    -ProjectPath $projectPath `
    -RepoRoot $RepoRoot `
    -ArtifactsPath $ArtifactsPath `
    -OutputPath $OutputPath
if (-not [string]::IsNullOrWhiteSpace($projectPathSafetyError)) {
    Write-CiError $projectPathSafetyError
    exit 1
}
if (Test-Path -LiteralPath $projectPath -PathType Container) {
    Remove-Item -LiteralPath $projectPath -Recurse -Force
}
$stagingPath = Join-Path $projectPath 'staging'
$extractPath = Join-Path $stagingPath 'extract'
New-Item -ItemType Directory -Force -Path $projectPath | Out-Null
Write-ProjectOwnershipMarker -ProjectPath $projectPath
New-Item -ItemType Directory -Force -Path $extractPath | Out-Null

# (1) PACK: npm pack applies the package.json files allowlist + .npmignore, so
# the staged payload is byte-identical to what npm/UPM consumers install.
Write-Host "::group::npm pack the shipped payload"
Push-Location $RepoRoot
try {
    $npmPackStdoutPath = Join-Path $ArtifactsPath 'npm-pack.stdout.json'
    $npmPackStderrPath = Join-Path $ArtifactsPath 'npm-pack.stderr.log'
    $npmCommand = Resolve-NpmPackCommand
    $npmPackArgs = @('pack', '--json', '--pack-destination', $stagingPath)
    Write-Host "Using npm command: $npmCommand"
    $packJsonText = (& $npmCommand @npmPackArgs 2> $npmPackStderrPath | Out-String)
    $packExitCode = $LASTEXITCODE
    Set-Content -LiteralPath $npmPackStdoutPath -Encoding UTF8 -NoNewline -Value $packJsonText
    if ($packExitCode -ne 0) {
        $stderrTail = ''
        if (Test-Path -LiteralPath $npmPackStderrPath -PathType Leaf) {
            $stderrTail = (Get-Content -LiteralPath $npmPackStderrPath -Tail 40 | Out-String).Trim()
        }
        $stdoutTail = ($packJsonText -split "`r?`n" | Select-Object -Last 40 | Out-String).Trim()
        $detail = @()
        if (-not [string]::IsNullOrWhiteSpace($stderrTail)) {
            $detail += "stderr tail:`n$stderrTail"
        }
        if (-not [string]::IsNullOrWhiteSpace($stdoutTail)) {
            $detail += "stdout tail:`n$stdoutTail"
        }
        $detail += "command: $npmCommand $($npmPackArgs -join ' ')"
        $detailText = if ($detail.Count -gt 0) { "`n$($detail -join "`n")" } else { '' }
        throw "npm pack failed with exit code $packExitCode. See $npmPackStdoutPath and $npmPackStderrPath.$detailText"
    }
} finally {
    Pop-Location
}
$packInfo = $packJsonText | ConvertFrom-Json
$tarballName = $packInfo[0].filename
$tarballPath = Join-Path $stagingPath $tarballName
if (-not (Test-Path -LiteralPath $tarballPath -PathType Leaf)) {
    throw "npm pack reported $tarballName but no tarball exists at $tarballPath."
}
Write-Host "Packed $tarballName"
Write-Host "::endgroup::"

# (2) EXTRACT: relative operands for tar (Windows drive-letter absolute paths
# are parsed as remote archive specs by GNU tar; same rule as
# buildLocalTarArchiveSpec in scripts/validate-npm-meta.js).
Push-Location $stagingPath
try {
    & tar -xzf "./$tarballName" -C './extract'
    if ($LASTEXITCODE -ne 0) {
        throw "tar extraction of $tarballName failed with exit code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}
$payloadRoot = Join-Path $extractPath 'package'
if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) {
    throw "Extracted tarball is missing the package/ root at $payloadRoot."
}

# (3) STAGE the Assets-form payload (exclusions + Samples~ rename; see header).
$exportRoot = [System.IO.Path]::Combine($projectPath, 'Assets', 'WallstopStudios', 'DxMessaging')
New-Item -ItemType Directory -Force -Path $exportRoot | Out-Null
$skipped = @()
foreach ($entry in Get-ChildItem -LiteralPath $payloadRoot -Force) {
    if ($ExcludedPayloadEntries -contains $entry.Name) {
        $skipped += $entry.Name
        continue
    }
    Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $exportRoot $entry.Name) -Recurse -Force
}
Write-Host "Staged payload at $exportRoot (excluded: $($skipped -join ', '))"
$samplesTilde = Join-Path $exportRoot 'Samples~'
if (Test-Path -LiteralPath $samplesTilde -PathType Container) {
    Move-Item -LiteralPath $samplesTilde -Destination (Join-Path $exportRoot 'Samples')
    Write-Host 'Renamed Samples~ to Samples so the samples import visibly.'
}

# (3b) Pre-write stable .meta files for the staged folders the payload cannot
# supply: the two script-created ancestors and the renamed Samples folder
# (Samples~ ships meta-less because Unity ignores ~ folders). Deterministic
# GUIDs keep the exported .unitypackage byte-stable across releases.
$folderMetaRelativePaths = @('Assets/WallstopStudios', $ExportRootRelative)
if (Test-Path -LiteralPath (Join-Path $exportRoot 'Samples') -PathType Container) {
    $folderMetaRelativePaths += "$ExportRootRelative/Samples"
}
foreach ($relativeFolder in $folderMetaRelativePaths) {
    $metaPath = [System.IO.Path]::Combine([string[]](@($projectPath) + (($relativeFolder + '.meta') -split '/')))
    if (Test-Path -LiteralPath $metaPath -PathType Leaf) {
        continue # A payload-supplied .meta wins; never overwrite its GUID.
    }
    New-DeterministicFolderMeta -RelativeFolderPath $relativeFolder |
        Set-Content -LiteralPath $metaPath -Encoding UTF8 -NoNewline
    Write-Host "Wrote deterministic folder meta $relativeFolder.meta"
}

# (4) PROJECT SKELETON: a manifest that ENABLES Unity's built-in modules, the
# pinned editor version, and the generated exporter OUTSIDE the exported root.
# An empty `dependencies: {}` does NOT enable the built-in modules -- it omits
# UnityEngine.IMGUIModule (and the rest), so the staged payload fails to compile
# the instant it touches an IMGUI type (e.g. EditorGUIUtility, whose base class
# GUIUtility lives there), Unity aborts batchmode, and ExportPackage never runs.
# That is exactly what broke the v3.1.0 export. The release-editor-compatible
# built-in package set is the single source of truth in
# scripts/unity/unity-builtin-modules.json. Do not copy newer editor default
# manifests wholesale; every listed package must resolve in the pinned release
# editor without network access. The package's own UPM dependencies merge on top.
# See .llm/skills/github-workflow-consistency/references/release-asset-and-notes-invariants.md.
New-Item -ItemType Directory -Force -Path (Join-Path $projectPath 'Packages') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $projectPath 'ProjectSettings') | Out-Null
New-Item -ItemType Directory -Force -Path ([System.IO.Path]::Combine($projectPath, 'Assets', 'Editor')) | Out-Null
$manifestDependencies = [ordered]@{}
$builtinModulesPath = Join-Path $PSScriptRoot 'unity-builtin-modules.json'
$builtinModules = (Get-Content -LiteralPath $builtinModulesPath -Raw | ConvertFrom-Json).dependencies
foreach ($module in $builtinModules.PSObject.Properties) {
    $manifestDependencies[$module.Name] = $module.Value
}
# StrictMode-safe probe: package.json omits `dependencies` today, and direct
# `.dependencies` access throws under Set-StrictMode -Version Latest.
$packageJson = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
$packageDependencies = $packageJson.PSObject.Properties['dependencies']
if ($null -ne $packageDependencies -and $null -ne $packageDependencies.Value) {
    foreach ($dependency in $packageDependencies.Value.PSObject.Properties) {
        $manifestDependencies[$dependency.Name] = $dependency.Value
    }
}
# Write BOM-less UTF-8: Windows PowerShell 5.1's `Set-Content -Encoding UTF8`
# prepends a BOM, and a BOM at the head of manifest.json can make Unity's
# package manager fail to parse it -- silently reintroducing the empty-manifest
# failure this block exists to prevent. WriteAllText is BOM-less on 5.1 and 7.
$manifestJson = (@{ dependencies = $manifestDependencies } | ConvertTo-Json -Depth 5) + "`n"
[System.IO.File]::WriteAllText(
    ([System.IO.Path]::Combine($projectPath, 'Packages', 'manifest.json')),
    $manifestJson,
    (New-Object System.Text.UTF8Encoding $false)
)
"m_EditorVersion: $UnityVersion`n" |
    Set-Content -LiteralPath ([System.IO.Path]::Combine($projectPath, 'ProjectSettings', 'ProjectVersion.txt')) -Encoding UTF8
New-ExporterSource |
    Set-Content -LiteralPath ([System.IO.Path]::Combine($projectPath, 'Assets', 'Editor', 'DxmUnityPackageExporter.cs')) -Encoding UTF8
@('-warnaserror', '-warn:9999') |
    Set-Content -LiteralPath ([System.IO.Path]::Combine($projectPath, 'Assets', 'csc.rsp')) -Encoding UTF8

$consumerRoot = Join-Path $projectPath 'consumers'
if ($VerifyConsumerInstalls) {
    $tarballDependency = 'file:' + ($tarballPath -replace '\\', '/')
    if ($StageOnly) {
        New-ConsumerProject -ConsumerPath (Join-Path $consumerRoot 'git') -Dependency "https://github.com/Ambiguous-Interactive/DxMessaging.git#$GitRevision" -IncludeImporter $true
        New-ConsumerProject -ConsumerPath (Join-Path $consumerRoot 'tarball') -Dependency $tarballDependency -IncludeImporter $true
        New-ConsumerProject -ConsumerPath (Join-Path $consumerRoot 'classic') -Dependency '' -IncludeImporter $false
        foreach ($consumer in @('git', 'tarball', 'classic')) {
            Add-ConsumerSampleTests -ConsumerPath (Join-Path $consumerRoot $consumer)
        }
    }
}

if ($StageOnly) {
    Write-Host "StageOnly: staged project at $projectPath; skipping the Unity export run."
    exit 0
}

# (5) EXPORT under the full license discipline (mirrors run-ci-tests.ps1).
$logPath = Join-Path $ArtifactsPath 'unity.log'
$markerPath = Join-Path $ArtifactsPath 'export-complete.marker'
if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
    Remove-Item -LiteralPath $markerPath -Force
}
if (Test-Path -LiteralPath $OutputPath -PathType Leaf) {
    Remove-Item -LiteralPath $OutputPath -Force
}

$hasLicenseCreds = -not [string]::IsNullOrWhiteSpace($env:UNITY_SERIAL) -and
    -not [string]::IsNullOrWhiteSpace($env:UNITY_EMAIL) -and
    -not [string]::IsNullOrWhiteSpace($env:UNITY_PASSWORD)

# License logs may carry account fragments: keep them OUT of $ArtifactsPath
# (which is uploaded); RUNNER_TEMP / system temp is never uploaded.
$licenseLogDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$activateLogPath = Join-Path $licenseLogDir "unity-activate-$UnityVersion-unitypackage.log"
$preflightReturnLogPath = Join-Path $licenseLogDir "unity-return-preflight-$UnityVersion-unitypackage.log"
$returnLogPath = Join-Path $licenseLogDir "unity-return-$UnityVersion-unitypackage.log"

# The workflow may treat only this run's post-activation return as cleanup proof.
# Delete any prior-run evidence before activation, and keep return-at-start output
# in a separate file so it can never confirm cleanup for the activation below.
if (Test-Path -LiteralPath $returnLogPath) {
    Remove-Item -LiteralPath $returnLogPath -Force
}

# Return-at-start: reclaim a seat a prior force-killed run may have leaked.
if ($hasLicenseCreds) {
    Invoke-UnityLicenseReturn -EditorPath $UnityEditorPath -Email $env:UNITY_EMAIL -Password $env:UNITY_PASSWORD -LogPath $preflightReturnLogPath
}

try {
    if ($hasLicenseCreds) {
        Invoke-UnityLicenseActivate -EditorPath $UnityEditorPath -Serial $env:UNITY_SERIAL -Email $env:UNITY_EMAIL -Password $env:UNITY_PASSWORD -LogPath $activateLogPath
    }

    $env:DXM_UNITYPACKAGE_OUTPUT = $OutputPath -replace '\\', '/'
    $env:DXM_EXPORT_MARKER_PATH = $markerPath
    $startedUtc = [DateTime]::UtcNow
    $exportArgs = @(
        '-quit',
        '-batchmode',
        '-nographics',
        '-projectPath', $projectPath,
        '-executeMethod', 'DxmUnityPackageExporter.Export',
        '-logFile', '-'
    )
    $exportExit = Invoke-UnityEditor `
        -EditorPath $UnityEditorPath `
        -Arguments $exportArgs `
        -Label "Export $PackageName-$packageVersion.unitypackage (Unity $UnityVersion)" `
        -LogPath $logPath
    Remove-Item -LiteralPath Env:\DXM_UNITYPACKAGE_OUTPUT -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Env:\DXM_EXPORT_MARKER_PATH -ErrorAction SilentlyContinue

    # The durable artifacts (fresh marker + valid package) are the source of
    # truth; a non-zero exit with both valid is a benign teardown crash.
    # @(...) around the pipeline keeps $problems an array under StrictMode
    # even when Where-Object yields a single string.
    $problems = @(
        @(
            (Test-ExportMarker -MarkerPath $markerPath -StartedUtc $startedUtc),
            (Test-UnityPackageOutput -Path $OutputPath -StartedUtc $startedUtc)
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($problems.Count -gt 0) {
        $detail = $problems -join '; '
        Write-CiError "Unity package export failed ($detail; Unity exit code $exportExit). See the export log at $logPath."
        throw "Unity package export failed: $detail"
    }
    if ($exportExit -ne 0) {
        Write-Host "::warning::Unity exited with code $exportExit after writing a valid .unitypackage and marker; treating the export as successful (benign teardown crash)."
    }

    $size = (Get-Item -LiteralPath $OutputPath).Length
    Write-Host "::notice::Exported $OutputPath ($size bytes)."

    if ($VerifyConsumerInstalls) {
        $evidenceRoot = Join-Path $ArtifactsPath 'consumer-installs'
        Invoke-ConsumerInstallVerification -SourceKind 'git' -ConsumerPath (Join-Path $consumerRoot 'git') -Dependency "https://github.com/Ambiguous-Interactive/DxMessaging.git#$GitRevision" -EvidenceRoot $evidenceRoot -TarballPayloadRoot $payloadRoot
        Invoke-ConsumerInstallVerification -SourceKind 'tarball' -ConsumerPath (Join-Path $consumerRoot 'tarball') -Dependency $tarballDependency -EvidenceRoot $evidenceRoot -TarballPayloadRoot $payloadRoot
        Invoke-ConsumerInstallVerification -SourceKind 'classic' -ConsumerPath (Join-Path $consumerRoot 'classic') -Dependency '' -EvidenceRoot $evidenceRoot -TarballPayloadRoot $payloadRoot
        Write-Host "::notice::Verified Git, tarball, and classic consumer installs on Unity $UnityVersion."
    }
} finally {
    if ($hasLicenseCreds -and $LicenseReturnOwner -eq 'Local') {
        Invoke-UnityLicenseReturn -EditorPath $UnityEditorPath -Email $env:UNITY_EMAIL -Password $env:UNITY_PASSWORD -LogPath $returnLogPath
    }
}
