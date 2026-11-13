#Requires -Version 7.0
# MIT License - Copyright (c) wallstop studios

function Assert-UnityTestFrameworkEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$UnityVersion
    )
    $pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $snapshots = @(Get-ChildItem -LiteralPath $Directory -Directory -ErrorAction Stop)
    if ($snapshots.Count -lt 1) { throw 'No loaded Test Framework snapshot was captured.' }
    foreach ($snapshot in $snapshots) {
        if (Test-Path -LiteralPath (Join-Path $snapshot.FullName 'capture-error.txt')) { throw 'Test Framework source capture was incomplete.' }
        $receipt = Get-Content -LiteralPath (Join-Path $snapshot.FullName 'receipt.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($receipt.SchemaVersion -ne 3 -or $receipt.UnityVersion -cne $UnityVersion -or $receipt.ProcessId -ne $ProcessId -or $ProcessId -le 0 -or
            -not [string]::Equals([IO.Path]::GetFullPath($receipt.ProjectPath), [IO.Path]::GetFullPath($Project), $pathComparison)) {
            throw 'Test Framework snapshot does not match this Unity process/project.'
        }
        $archive = $null
        try {
            if ($receipt.SchemaVersion -eq 3) {
                if ($receipt.SourceArchive -cne 'sources.zip') { throw 'Malformed Test Framework source archive path.' }
                $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $snapshot.FullName 'sources.zip'))
                $archivePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                foreach ($entry in $archive.Entries) {
                    if ($entry.FullName -cnotmatch '^package/[0-9]{4}\.(cs|asmdef)$' -or
                        $entry.Length -gt 16777216 -or -not $archivePaths.Add($entry.FullName)) {
                        throw 'Malformed or duplicate archived Test Framework source.'
                    }
                }
            }
            $packedCount = 0
            $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $originalPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($file in $receipt.Files) {
                if ($file.Path -isnot [string] -or [string]::IsNullOrWhiteSpace($file.Path) -or
                    [IO.Path]::IsPathRooted($file.Path) -or $file.Path.Contains('\') -or $file.Path -match '[\x00-\x1f\x7f:]' -or
                    $file.Path -match '(?:^|/)(?:\.{1,2})?(?:/|$)' -or -not $paths.Add($file.Path)) {
                    throw 'Malformed or duplicate Test Framework evidence path.'
                }
                if ($file.OriginalPath -isnot [string] -or [string]::IsNullOrWhiteSpace($file.OriginalPath) -or
                    [IO.Path]::IsPathRooted($file.OriginalPath) -or $file.OriginalPath.Contains('\') -or $file.OriginalPath -match '[\x00-\x1f\x7f:]' -or
                    $file.OriginalPath -match '(?:^|/)(?:\.{1,2})?(?:/|$)' -or -not $originalPaths.Add($file.OriginalPath)) {
                    throw 'Malformed or duplicate original Test Framework evidence path.'
                }
                if (($file.Path -ceq 'package/package.json' -or -not $file.Path.StartsWith('package/', [StringComparison]::Ordinal)) -and $file.OriginalPath -cne $file.Path) { throw 'Framework metadata/module mapping differs.' }
                if ($file.Path.StartsWith('package/', [StringComparison]::Ordinal) -and $file.Path -cne 'package/package.json' -and
                    ($file.Path -cnotmatch '^package/[0-9]{4}\.(cs|asmdef)$' -or -not $file.OriginalPath.StartsWith('package/', [StringComparison]::Ordinal) -or
                    [IO.Path]::GetExtension($file.Path) -cne [IO.Path]::GetExtension($file.OriginalPath))) { throw 'Malformed compact framework source mapping.' }
                $packed = $null -ne $archive -and $file.Path.StartsWith('package/', [StringComparison]::Ordinal) -and $file.Path -cne 'package/package.json'
                if ($packed) {
                    $entry = $archive.GetEntry($file.Path)
                    if ($null -eq $entry) { throw "Missing archived Test Framework source: $($file.Path)" }
                    $length = $entry.Length
                    $stream = $entry.Open()
                    $hash = [Security.Cryptography.SHA256]::Create()
                    try { $digest = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
                    finally { $hash.Dispose(); $stream.Dispose() }
                    $packedCount++
                } else {
                    $path = Join-Path $snapshot.FullName $file.Path
                    $item = Get-Item -LiteralPath $path -ErrorAction Stop
                    $length = $item.Length
                    $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                }
                if ($length -ne $file.Length -or $digest -cne $file.Sha256) {
                    throw "Test Framework evidence bytes differ: $($file.Path)"
                }
            }
            if ($null -ne $archive -and $packedCount -ne $archive.Entries.Count) { throw 'Unlisted archived Test Framework source.' }
            foreach ($required in @('manifest.json', 'packages-lock.json', 'package/package.json')) {
                if (-not $paths.Contains($required)) { throw "Missing Test Framework metadata: $required" }
            }
            $sourceCount = @($paths | Where-Object { $_.StartsWith('package/', [StringComparison]::Ordinal) -and $_.EndsWith('.cs', [StringComparison]::Ordinal) }).Count
            if ($sourceCount -lt 1 -or $sourceCount -ne $receipt.SourceCount) { throw 'Test Framework source inventory is empty or incomplete.' }
            $package = Get-Content -LiteralPath (Join-Path $snapshot.FullName 'package/package.json') -Raw | ConvertFrom-Json -AsHashtable
            $lock = Get-Content -LiteralPath (Join-Path $snapshot.FullName 'packages-lock.json') -Raw | ConvertFrom-Json -AsHashtable
            if ($package.name -cne 'com.unity.test-framework' -or $package.version -cne $receipt.PackageVersion -or
                $lock.dependencies.'com.unity.test-framework'.version -cne $receipt.PackageVersion) {
                throw 'Resolved Test Framework package identity differs from captured metadata.'
            }
            $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($name in @('UnityEditor.TestRunner', 'UnityEngine.TestRunner', 'nunit.framework')) { [void]$expected.Add($name) }
            foreach ($assembly in $receipt.Assemblies) {
                if (-not $expected.Remove($assembly.Name) -or $assembly.SavedPath -cne "assemblies/$($assembly.Name).dll" -or -not $paths.Contains($assembly.SavedPath)) {
                    throw 'Missing or duplicate loaded Test Framework assembly identity.'
                }
                $assemblyPath = Join-Path $snapshot.FullName $assembly.SavedPath
                $identity = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath)
                $stream = [IO.File]::OpenRead($assemblyPath)
                $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
                try {
                    $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                    $mvid = $reader.GetGuid($reader.GetModuleDefinition().Mvid).ToString()
                    if ($identity.Name -cne $assembly.Name -or $identity.FullName -cne $assembly.FullName -or $mvid -cne $assembly.ModuleVersionId) {
                        throw 'Loaded Test Framework module differs from captured DLL bytes.'
                    }
                } finally { $pe.Dispose(); $stream.Dispose() }
            }
            if ($expected.Count -ne 0) { throw 'Missing loaded Test Framework assembly identity.' }
            Write-Host "[test-framework] Unity $($receipt.UnityVersion), resolved $($receipt.PackageVersion) ($($receipt.PackageSource)); $sourceCount source files, three bound modules, PID $ProcessId."
        } finally {
            if ($null -ne $archive) { $archive.Dispose() }
        }
    }
}
