[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EditorPath,
    [Parameter(Mandatory)][string]$OwnedRoot,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$OwnedRoot = [IO.Path]::GetFullPath($OwnedRoot)
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
if (Test-Path -LiteralPath $OwnedRoot) { throw 'The owned copy root must be new.' }
if (Test-Path -LiteralPath $ArtifactsPath) { throw 'The artifact root must be new.' }
New-Item -ItemType Directory -Path $OwnedRoot, $ArtifactsPath | Out-Null
$head = (git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot bind the source commit.' }
$paths = @(git -C $repo ls-files)
if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw 'Cannot enumerate source files.' }
$projectFiles = @('Packages/manifest.json', 'Packages/packages-lock.json',
    'ProjectSettings/ProjectSettings.asset', 'ProjectSettings/ProjectVersion.txt')

function Get-FileRecord([string]$Path) {
    [ordered]@{ bytes = (Get-Item -LiteralPath $Path -Force).Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Write-Json([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

$copies = @{}
$sourceHashes = @{}
foreach ($name in $paths) {
    $sourceHashes[$name] = Get-FileRecord (Join-Path $repo $name)
}
foreach ($variant in @('baseline', 'candidate')) {
    $copy = Join-Path $OwnedRoot $variant
    New-Item -ItemType Directory -Path $copy | Out-Null
    foreach ($name in $paths) {
        $target = Join-Path $copy $name
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        [IO.File]::Copy((Join-Path $repo $name), $target)
        if ((Get-FileRecord $target).sha256 -ne $sourceHashes[$name].sha256) {
            throw 'Owned copy differs from its source input.'
        }
    }
    $project = Join-Path $copy '.unity-test-project'
    if ($variant -eq 'baseline') {
        foreach ($name in $projectFiles) {
            [IO.File]::Copy((Join-Path $PSScriptRoot ("local-project-baseline/" + [IO.Path]::GetFileName($name))), (Join-Path $project $name), $true)
        }
    }
    if (Test-Path -LiteralPath (Join-Path $project 'Library')) { throw 'Fresh project already has a Library.' }
    $output = Join-Path $ArtifactsPath $variant
    New-Item -ItemType Directory -Path $output | Out-Null
    foreach ($name in $projectFiles) {
        $target = Join-Path $output "before/$name"
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        [IO.File]::Copy((Join-Path $project $name), $target)
    }
    $probe = Join-Path $project 'Assets/Editor/LocalProjectImportProbe.cs'
    New-Item -ItemType Directory -Force -Path (Split-Path $probe -Parent) | Out-Null
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LocalProjectImportProbe.cs.txt'))
    [IO.File]::WriteAllText($probe, ($text.Replace("`r`n", "`n").Replace("`n", "`r`n")), [Text.UTF8Encoding]::new($false))
    $copies[$variant] = [ordered]@{ root = $copy; project = $project; output = $output }
}
Write-Json (Join-Path $ArtifactsPath 'preparation.json') ([ordered]@{
    sourceCommit = $head; ownedRoot = $OwnedRoot; sourceFiles = $sourceHashes; copies = $copies
    probeSha256 = (Get-FileRecord (Join-Path $PSScriptRoot 'LocalProjectImportProbe.cs.txt')).sha256
    prepareOnly = [bool]$PrepareOnly; editorInvocations = 0
})
if ($PrepareOnly) { return }
if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) { throw 'Gated Editor is missing.' }

# Import actual production license functions. Do not execute runner setup or copy its logic.
$tokens = $null; $errors = $null
$runner = Join-Path $repo 'scripts/unity/run-ci-tests.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Runner source does not parse.' }
$functions = @{}
foreach ($node in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    if ($functions.ContainsKey($node.Name)) { throw 'Ambiguous runner function definition.' }
    $functions[$node.Name] = $node
}
$pending = [Collections.Generic.Queue[string]]::new()
$selected = [Collections.Generic.HashSet[string]]::new()
$pending.Enqueue('Invoke-UnityLicenseActivate'); $pending.Enqueue('Invoke-UnityLicenseReturn')
while ($pending.Count) {
    $name = $pending.Dequeue()
    if (-not $selected.Add($name)) { continue }
    $node = $functions[$name]
    $globals = @($node.FindAll({ param($n) $n -is [Management.Automation.Language.VariableExpressionAst] }, $true) |
        Where-Object { $_.VariablePath.UserPath -eq 'PSScriptRoot' -or $_.VariablePath.UserPath -like 'script:*' })
    if ($globals.Count) { throw 'License import depends on runner script globals.' }
    foreach ($command in $node.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] }, $true)) {
        $called = $command.GetCommandName()
        if ($called -and $functions.ContainsKey($called)) { $pending.Enqueue($called) }
    }
}
if ($selected.Count -ne 8) { throw 'The frozen eight-function license closure changed.' }
$functionHashes = @{}
foreach ($name in $selected) {
    $text = $functions[$name].Extent.Text
    $functionHashes[$name] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
    . ([scriptblock]::Create($text))
}
Write-Json (Join-Path $ArtifactsPath 'license-function-source.json') ([ordered]@{
    runnerSha256 = (Get-FileRecord $runner).sha256; functions = $functionHashes
})
$licensePrefix = Join-Path $env:RUNNER_TEMP "local-project-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$returnBefore = "$licensePrefix-return-before.log"
$returnAfter = "$licensePrefix-return-finally.log"
$activation = "$licensePrefix-activate.log"
Invoke-UnityLicenseReturn -EditorPath $EditorPath -Email $env:UNITY_EMAIL -Password $env:UNITY_PASSWORD -LogPath $returnBefore
$results = @()
try {
    Invoke-UnityLicenseActivate -EditorPath $EditorPath -Serial $env:UNITY_SERIAL -Email $env:UNITY_EMAIL -Password $env:UNITY_PASSWORD -LogPath $activation
    foreach ($variant in @('baseline', 'candidate')) {
        $cell = $copies[$variant]
        $env:DXM_LOCAL_PROJECT_RECEIPT = Join-Path $cell.output 'import-receipt.json'
        $env:DXM_LOCAL_PROJECT_PACKAGE_ROOT = $cell.root
        $env:DXM_LOCAL_PROJECT_VARIANT = $variant
        $env:DXM_LOCAL_PROJECT_SOURCE_HEAD = $head
        $info = [Diagnostics.ProcessStartInfo]::new()
        $info.FileName = $EditorPath
        $info.WorkingDirectory = $cell.project
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($argument in @('-batchmode', '-nographics', '-quit', '-projectPath', $cell.project,
            '-executeMethod', 'DxmLocalProjectImport.Probe.Run', '-logFile', (Join-Path $cell.output 'unity.log'))) {
            $info.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        if (-not $process.Start()) { throw 'Owned Editor process did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit(600000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $cell.output 'stdout.log'), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $cell.output 'stderr.log'), $stderr.GetAwaiter().GetResult())
        $exitCode = $process.ExitCode
        $process.Dispose()
        $missingAfter = @()
        foreach ($name in $projectFiles) {
            $target = Join-Path $cell.output "after/$name"
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            $actualPath = Join-Path $cell.project $name
            if (Test-Path -LiteralPath $actualPath -PathType Leaf) {
                [IO.File]::Copy($actualPath, $target)
            } else {
                $missingAfter += $name
            }
        }
        $changed = @()
        foreach ($name in $paths | Where-Object { -not $_.StartsWith('.unity-test-project/') }) {
            $actual = Get-FileRecord (Join-Path $cell.root $name)
            if ($actual.sha256 -ne $sourceHashes[$name].sha256) { $changed += $name }
        }
        $receiptExists = Test-Path -LiteralPath $env:DXM_LOCAL_PROJECT_RECEIPT -PathType Leaf
        $compiledFiles = @()
        if ($receiptExists) {
            $receipt = Get-Content -LiteralPath $env:DXM_LOCAL_PROJECT_RECEIPT -Raw | ConvertFrom-Json
            $compiledRoot = Join-Path $cell.output 'compiled-assemblies'
            New-Item -ItemType Directory -Path $compiledRoot | Out-Null
            foreach ($assembly in $receipt.assemblies) {
                $assemblyPath = if ([IO.Path]::IsPathRooted($assembly.path)) {
                    [IO.Path]::GetFullPath($assembly.path)
                } else { [IO.Path]::GetFullPath((Join-Path $cell.project $assembly.path)) }
                $library = [IO.Path]::GetFullPath((Join-Path $cell.project 'Library')) + [IO.Path]::DirectorySeparatorChar
                if (-not $assemblyPath.StartsWith($library, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Reported package assembly is outside the owned Library.'
                }
                $record = Get-FileRecord $assemblyPath
                if ($record.sha256 -ne $assembly.sha256) { throw 'Reported assembly hash differs from actual bytes.' }
                $destination = Join-Path $compiledRoot ([IO.Path]::GetFileName($assemblyPath))
                [IO.File]::Copy($assemblyPath, $destination)
                $compiledFiles += [ordered]@{ name = [IO.Path]::GetFileName($assemblyPath)
                    bytes = $record.bytes; sha256 = $record.sha256 }
            }
        }
        $result = [ordered]@{ variant = $variant; exitCode = $exitCode; timedOut = $timedOut
            receiptExists = $receiptExists; packageSourceChanges = $changed; missingProjectFilesAfter = $missingAfter
            compiledFiles = $compiledFiles }
        Write-Json (Join-Path $cell.output 'process.json') $result
        $results += $result
        if ($changed.Count -or $timedOut) { throw 'Owned source changed or Editor timed out; stop this admission.' }
        if ($variant -eq 'candidate' -and ($exitCode -ne 0 -or -not $receiptExists)) {
            throw 'Candidate import/compilation did not complete normally.'
        }
    }
} finally {
    Write-Json (Join-Path $ArtifactsPath 'results.json') $results
    Invoke-UnityLicenseReturn -EditorPath $EditorPath -Email $env:UNITY_EMAIL -Password $env:UNITY_PASSWORD -LogPath $returnAfter
}
