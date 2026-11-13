#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$unity = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $unity)
. (Join-Path $unity 'assert-test-framework-evidence.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('dxm-framework-evidence-' + [guid]::NewGuid().ToString('N'))
$checks = 0

function Assert-EvidenceControl {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    # Add-Type loads PowerShell's existing Roslyn compiler; no package or tool download.
    Add-Type 'internal sealed class FrameworkEvidenceCompilerBootstrap504 { }'
    $modules = @()
    foreach ($name in @('UnityEditor.TestRunner', 'UnityEngine.TestRunner', 'nunit.framework')) {
        $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText('public sealed class EvidenceFixtureModule { }', [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default)
        $reference = [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile([object].Assembly.Location)
        $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
        $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($name, [Microsoft.CodeAnalysis.SyntaxTree[]]@($tree), [Microsoft.CodeAnalysis.MetadataReference[]]@($reference), $options)
        $file = Join-Path $scratch "$name.dll"
        $stream = [IO.File]::Create($file)
        try { $result = $compilation.Emit($stream); Assert-EvidenceControl $result.Success 'The metadata fixture must compile actual named assemblies.' }
        finally { $stream.Dispose() }
        $stream = [IO.File]::OpenRead($file)
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try { $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe); $mvid = $reader.GetGuid($reader.GetModuleDefinition().Mvid).ToString() }
        finally { $pe.Dispose(); $stream.Dispose() }
        $modules += @{ Name = $name; FullName = ([Reflection.AssemblyName]::GetAssemblyName($file)).FullName; ModuleVersionId = $mvid; LoadedPath = $file; SavedPath = "assemblies/$name.dll"; SymbolsPath = $null }
    }
    $cases = @('valid', 'wrong-pid', 'wrong-project', 'wrong-unity', 'missing-source', 'source-bytes', 'source-count', 'package-version', 'lock-version', 'missing-module', 'duplicate-module', 'module-mvid', 'module-name', 'wrong-assembly-payload', 'unlisted-source', 'capture-error', 'missing-snapshot')
    foreach ($case in $cases) {
        $directory = Join-Path $scratch $case
        $snapshot = Join-Path $directory 'snapshot'
        New-Item -ItemType Directory -Path (Join-Path $snapshot 'assemblies'), (Join-Path $snapshot 'package') -Force | Out-Null
        $metadata = @{
            'manifest.json' = '{"dependencies":{"com.unity.test-framework":"0.9.9"}}'
            'packages-lock.json' = '{"dependencies":{"com.unity.test-framework":{"version":"1.2.3"}}}'
            'package/package.json' = '{"name":"com.unity.test-framework","version":"1.2.3"}'
        }
        foreach ($name in $metadata.Keys) { [IO.File]::WriteAllText((Join-Path $snapshot $name), $metadata[$name]) }
        foreach ($module in $modules) { Copy-Item -LiteralPath $module.LoadedPath -Destination (Join-Path $snapshot $module.SavedPath) }
        $files = @()
        foreach ($name in @('manifest.json', 'packages-lock.json', 'package/package.json') + @($modules | ForEach-Object { $_.SavedPath })) {
            $path = Join-Path $snapshot $name
            $files += @{ Path = $name; OriginalPath = $name; Length = (Get-Item -LiteralPath $path).Length; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        $archive = [IO.Compression.ZipFile]::Open((Join-Path $snapshot 'sources.zip'), [IO.Compression.ZipArchiveMode]::Create)
        try {
            $sourceRows = @(
                @{ Path = 'package/0006.cs'; OriginalPath = 'package/Runtime/Fixture.cs'; Text = 'public sealed class FrameworkSourceFixture { }' },
                @{ Path = 'package/0007.asmdef'; OriginalPath = 'package/Runtime/Fixture.asmdef'; Text = '{"name":"Fixture"}' }
            )
            foreach ($row in $sourceRows) {
                $bytes = [Text.Encoding]::UTF8.GetBytes($row.Text)
                $entry = $archive.CreateEntry($row.Path)
                $stream = $entry.Open()
                try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
                $hash = [Security.Cryptography.SHA256]::Create()
                try { $digest = [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() } finally { $hash.Dispose() }
                $files += @{ Path = $row.Path; OriginalPath = $row.OriginalPath; Length = $bytes.Length; Sha256 = $digest }
            }
            if ($case -eq 'unlisted-source') { [void]$archive.CreateEntry('package/9999.cs') }
        } finally { $archive.Dispose() }
        $receipt = @{ SchemaVersion = 3; SourceArchive = 'sources.zip'; ProcessId = 504; DomainId = 1; UnityVersion = '6000.4.6f1'; ProjectPath = $scratch; PackageVersion = '1.2.3'; PackageSource = 'Registry'; PackagePath = 'resolved-package'; SourceCount = 1; Assemblies = @($modules | ForEach-Object { $_.Clone() }); Files = $files }
        switch ($case) {
            'wrong-pid' { $receipt.ProcessId = 505 }
            'wrong-project' { $receipt.ProjectPath = Join-Path $scratch 'another-project' }
            'wrong-unity' { $receipt.UnityVersion = '6000.3.16f1' }
            'missing-source' { $receipt.Files = @($files | Where-Object { $_.Path -cne 'package/0006.cs' }) }
            'source-bytes' { $files[-1].Sha256 = '0' * 64 }
            'source-count' { $receipt.SourceCount = 2 }
            'package-version' { $receipt.PackageVersion = '1.2.4' }
            'lock-version' { [IO.File]::WriteAllText((Join-Path $snapshot 'packages-lock.json'), '{"dependencies":{"com.unity.test-framework":{"version":"1.2.4"}}}'); $files[1].Length = (Get-Item -LiteralPath (Join-Path $snapshot 'packages-lock.json')).Length; $files[1].Sha256 = (Get-FileHash -LiteralPath (Join-Path $snapshot 'packages-lock.json') -Algorithm SHA256).Hash.ToLowerInvariant() }
            'missing-module' { $receipt.Assemblies = @($receipt.Assemblies | Select-Object -First 2) }
            'duplicate-module' { $receipt.Assemblies += $receipt.Assemblies[0] }
            'module-mvid' { $receipt.Assemblies[0].ModuleVersionId = [guid]::Empty.ToString() }
            'module-name' { $receipt.Assemblies[0].FullName = $receipt.Assemblies[1].FullName }
            'wrong-assembly-payload' {
                Copy-Item -LiteralPath $modules[1].LoadedPath -Destination (Join-Path $snapshot $modules[0].SavedPath) -Force
                $receipt.Assemblies[0].FullName = $modules[1].FullName
                $receipt.Assemblies[0].ModuleVersionId = $modules[1].ModuleVersionId
                $record = @($files | Where-Object { $_.Path -ceq $modules[0].SavedPath })[0]
                $record.Length = (Get-Item -LiteralPath (Join-Path $snapshot $record.Path)).Length
                $record.Sha256 = (Get-FileHash -LiteralPath (Join-Path $snapshot $record.Path) -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            'capture-error' { [IO.File]::WriteAllText((Join-Path $snapshot 'capture-error.txt'), 'controlled missing input') }
            'missing-snapshot' { Remove-Item -LiteralPath $snapshot -Recurse -Force }
        }
        if ($case -ne 'missing-snapshot') { $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $snapshot 'receipt.json') }
        $failure = $null
        try { Assert-UnityTestFrameworkEvidence -Directory $directory -ProcessId 504 -Project $scratch -UnityVersion '6000.4.6f1' } catch { $failure = $_.Exception.Message }
        Assert-EvidenceControl (($case -eq 'valid' -and $null -eq $failure) -or ($case -ne 'valid' -and $null -ne $failure)) "Framework evidence $case control failed: $failure"
        ++$checks
    }
    $project = Join-Path $scratch 'generated-project'
    $artifacts = Join-Path $scratch 'generation-artifacts'
    $generation = @(& pwsh -NoLogo -NoProfile -File (Join-Path $unity 'run-ci-tests.ps1') -UnityVersion '6000.4.6f1' -TestMode standalone -AssemblyNames Fixture -ArtifactsPath $artifacts -ProjectPath $project -CaptureTestFrameworkEvidence -GenerateOnly 2>&1)
    Assert-EvidenceControl ($LASTEXITCODE -eq 0) ("The actual harness must generate the enabled collector: " + ($generation -join "`n"))
    foreach ($extension in @('cs', 'asmdef')) {
        $generated = Join-Path $project "Assets/Editor/DxmCiTestFrameworkEvidence/DxmCiTestFrameworkEvidence.$extension"
        $template = Join-Path $unity "test-framework-evidence.$extension.txt"
        Assert-EvidenceControl ([IO.File]::ReadAllText($generated).TrimEnd("`r", "`n") -ceq [IO.File]::ReadAllText($template).TrimEnd("`r", "`n")) 'The actual project must receive the exact collector inputs.'
    }
    $unsupported = @(& pwsh -NoLogo -NoProfile -File (Join-Path $unity 'run-ci-tests.ps1') -UnityVersion '6000.4.6f1' -TestMode editmode -AssemblyNames Fixture -ArtifactsPath (Join-Path $scratch 'unsupported-artifacts') -ProjectPath (Join-Path $scratch 'unsupported-project') -CaptureTestFrameworkEvidence -GenerateOnly 2>&1)
    Assert-EvidenceControl ($LASTEXITCODE -ne 0 -and ($unsupported -join "`n").Contains('correctness standalone build')) 'Other modes must refuse this standalone observer option.'
    $checks += 2
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
Write-Host "$checks actual framework evidence validation controls passed."
