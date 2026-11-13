#Requires -Version 7.0
# MIT License - Copyright (c) wallstop studios

function Get-TestFrameworkPathFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateSet('Ordinal', 'OrdinalIgnoreCase')][string]$Comparison = 'Ordinal'
    )
    $uri = [Uri]::new($Path, [UriKind]::Absolute)
    if (-not $uri.IsFile) { throw 'Framework evidence requires an absolute filesystem path.' }
    $canonical = $uri.AbsoluteUri.TrimEnd('/')
    if ($Comparison -ceq 'OrdinalIgnoreCase') { $canonical = $canonical.ToLowerInvariant() }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Assert-UnityTestFrameworkEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$UnityVersion
    )
    $snapshots = @(Get-ChildItem -LiteralPath $Directory -Directory -ErrorAction Stop)
    if ($snapshots.Count -lt 1) { throw 'No loaded Test Framework snapshot was captured.' }
    foreach ($snapshot in $snapshots) {
        if (Test-Path -LiteralPath (Join-Path $snapshot.FullName 'capture-error.txt')) { throw 'Test Framework source capture was incomplete.' }
        $receipt = Get-Content -LiteralPath (Join-Path $snapshot.FullName 'receipt.json') -Raw | ConvertFrom-Json -AsHashtable
        $expectedProject = Get-TestFrameworkPathFingerprint -Path $Project -Comparison $receipt.PathComparison
        if ($receipt.SchemaVersion -ne 4 -or $receipt.UnityVersion -cne $UnityVersion -or $receipt.ProcessId -ne $ProcessId -or $ProcessId -le 0 -or
            $receipt.ProjectPathSha256 -cne $expectedProject -or $receipt.PackagePathSha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Test Framework snapshot does not match this Unity process/project.'
        }
        $archive = $null
        try {
            if ($receipt.SchemaVersion -eq 4) {
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
                $version = @($identity.Version.Major, $identity.Version.Minor, $identity.Version.Build, $identity.Version.Revision)
                $signingIdentity = @($identity.GetPublicKeyToken() | ForEach-Object { [int]$_ })
                if (@($assembly.Version).Count -ne 4 -or $null -eq $assembly.SigningIdentity) { throw 'Malformed loaded Test Framework assembly identity.' }
                $expectedIdentity = [Reflection.AssemblyName]::new()
                $expectedIdentity.Name = $assembly.Name
                $expectedIdentity.Version = [version]::new($assembly.Version[0], $assembly.Version[1], $assembly.Version[2], $assembly.Version[3])
                $expectedIdentity.CultureName = $assembly.Culture
                $expectedIdentity.SetPublicKeyToken([byte[]]@($assembly.SigningIdentity))
                $expectedIdentity.Flags = [Reflection.AssemblyNameFlags]$assembly.Flags
                $expectedIdentity.ContentType = [Enum]::Parse([Reflection.AssemblyContentType], $assembly.ContentType)
                $stream = [IO.File]::OpenRead($assemblyPath)
                $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
                try {
                    $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                    $mvid = $reader.GetGuid($reader.GetModuleDefinition().Mvid).ToString()
                    if ($identity.Name -cne $assembly.Name -or @($assembly.Version).Count -ne 4 -or
                        [string]::Join('.', $version) -cne [string]::Join('.', @($assembly.Version)) -or
                        [string]$identity.CultureName -cne $assembly.Culture -or $null -eq $assembly.SigningIdentity -or
                        @($assembly.SigningIdentity).Count -ne $signingIdentity.Count -or
                        [string]::Join(',', $signingIdentity) -cne [string]::Join(',', @($assembly.SigningIdentity)) -or
                        $identity.FullName -cne $expectedIdentity.FullName -or $identity.ContentType.ToString() -cne $assembly.ContentType -or
                        $mvid -cne $assembly.ModuleVersionId -or $assembly.LoadedPathSha256 -cnotmatch '^[0-9a-f]{64}$') {
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
