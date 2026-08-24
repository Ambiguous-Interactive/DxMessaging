#Requires -Version 5.1
# cspell:ignore gshared
[CmdletBinding()]
param(
    [string]$ProjectPath,

    [string]$ArtifactsPath,

    [switch]$SelfTestOnly,

    [switch]$SkipNativeInventory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Set-GeneratedCppIndex {
    param([Parameter(Mandatory = $true)] [string[]]$Paths)

    # Read the tree once for candidate symbol lines, then lazily cache complete
    # files only when a candidate definition needs brace parsing. A generated
    # IL2CPP tree can contain hundreds of large files, so retaining every line
    # as a PowerShell string object wastes several times the source size.
    $script:CppCandidateLines = @(
        Select-String `
            -LiteralPath $Paths `
            -Pattern '(?:MessageBus_|Forwarded_)'
    )
    $script:CppFileLines = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
}

function Find-GeneratedLineMatches {
    param([Parameter(Mandatory = $true)] [string]$Pattern)

    foreach ($candidate in $script:CppCandidateLines) {
        if (
            [regex]::IsMatch(
                $candidate.Line,
                $Pattern,
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
        ) {
            $candidate
        }
    }
}

# SYNC: .github/workflows/perf-numbers.yml duplicates this generated-method
# parser for typed-deregistration evidence; keep brace and uniqueness handling aligned.
function Get-GeneratedMethodDefinition {
    param(
        [Parameter(Mandatory = $true)] [string]$Label,
        [Parameter(Mandatory = $true)] [string]$Pattern
    )

    $signatureMatches = @(Find-GeneratedLineMatches -Pattern $Pattern)
    $bodyCandidates = [System.Collections.Generic.List[object]]::new()
    foreach ($signature in $signatureMatches) {
        if (!$script:CppFileLines.ContainsKey($signature.Path)) {
            $script:CppFileLines.Add(
                $signature.Path,
                @(Get-Content -LiteralPath $signature.Path)
            )
        }
        $fileLines = @($script:CppFileLines[$signature.Path])
        $startIndex = $signature.LineNumber - 1
        $openingBraceIndex = $startIndex
        $openingBraceOnSignature = $fileLines[$openingBraceIndex] -match '\{'
        if (!$openingBraceOnSignature) {
            $openingBraceIndex++
            while (
                $openingBraceIndex -lt $fileLines.Count -and
                [string]::IsNullOrWhiteSpace($fileLines[$openingBraceIndex])
            ) {
                $openingBraceIndex++
            }
        }
        if (
            $openingBraceIndex -ge $fileLines.Count -or
            (!$openingBraceOnSignature -and $fileLines[$openingBraceIndex].Trim() -ne '{')
        ) {
            continue
        }

        $braceDepth = 0
        $endIndex = -1
        for ($index = $openingBraceIndex; $index -lt $fileLines.Count; $index++) {
            $braceDepth += [regex]::Matches($fileLines[$index], '\{').Count
            $braceDepth -= [regex]::Matches($fileLines[$index], '\}').Count
            if ($braceDepth -eq 0) {
                $endIndex = $index
                break
            }
            if ($braceDepth -lt 0) {
                break
            }
        }
        if ($endIndex -lt $openingBraceIndex) {
            throw "Generated definition for $Label had unbalanced braces."
        }
        $bodyCandidates.Add(
            [pscustomobject]@{
                Label      = $Label
                Path       = $signature.Path
                LineNumber = $signature.LineNumber
                Lines      = @($fileLines[$startIndex..$endIndex])
            }
        )
    }

    $uniqueBodyKeys = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($bodyCandidate in $bodyCandidates) {
        $null = $uniqueBodyKeys.Add($bodyCandidate.Lines -join "`n")
    }
    if ($uniqueBodyKeys.Count -ne 1) {
        throw (
            "Expected one unique generated body for $Label, " +
            "found $($uniqueBodyKeys.Count) unique bodies across " +
            "$($bodyCandidates.Count) definitions from " +
            "$($signatureMatches.Count) signatures."
        )
    }

    $canonicalBody = $bodyCandidates[0]
    $canonicalBody | Add-Member `
        -NotePropertyName DefinitionOccurrenceCount `
        -NotePropertyValue $bodyCandidates.Count
    $canonicalBody | Add-Member `
        -NotePropertyName DefinitionLocations `
        -NotePropertyValue @(
            $bodyCandidates |
                ForEach-Object {
                    [pscustomobject]@{
                        Path       = $_.Path
                        LineNumber = $_.LineNumber
                    }
                }
        )
    return $canonicalBody
}

function Get-GeneratedSharedImplementation {
    param(
        [Parameter(Mandatory = $true)] [string]$Label,
        [Parameter(Mandatory = $true)] [object]$Wrapper,
        [Parameter(Mandatory = $true)] [string]$WrapperSymbol
    )

    if ($WrapperSymbol -match '_gshared(?:_inline)?$') {
        return [pscustomobject]@{
            Wrapper          = $Wrapper
            Implementation   = $Wrapper
            SharedCallSymbol = $WrapperSymbol
        }
    }

    $wrapperBaseSymbol = $WrapperSymbol -replace '_inline$', ''
    $sharedCallPattern = "(?<![A-Za-z0-9_]){0}_gshared(?:_inline)?(?![A-Za-z0-9_])" -f (
        [regex]::Escape($wrapperBaseSymbol)
    )
    $sharedCallSymbols = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $Wrapper.Lines) {
        foreach (
            $symbolMatch in [regex]::Matches(
                $line,
                $sharedCallPattern,
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
        ) {
            $sharedCallSymbols.Add($symbolMatch.Value)
        }
    }
    $sharedCallSymbols = @($sharedCallSymbols | Sort-Object -Unique)
    if ($sharedCallSymbols.Count -gt 1) {
        throw (
            "Expected $Label to call at most one shared implementation, " +
            "found $($sharedCallSymbols.Count)."
        )
    }
    if ($sharedCallSymbols.Count -eq 0) {
        return [pscustomobject]@{
            Wrapper          = $Wrapper
            Implementation   = $Wrapper
            SharedCallSymbol = 'none-specialized-body'
        }
    }

    $implementation = Get-GeneratedMethodDefinition `
        -Label "$Label (shared body)" `
        -Pattern ("(?<![A-Za-z0-9_]){0}\s*\(" -f [regex]::Escape($sharedCallSymbols[0]))
    return [pscustomobject]@{
        Wrapper          = $Wrapper
        Implementation   = $implementation
        SharedCallSymbol = $sharedCallSymbols[0]
    }
}

function Get-GeneratedBodySha256 {
    param([Parameter(Mandatory = $true)] [object]$Method)

    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($Method.Lines -join "`n")
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash($bodyBytes)
        return [System.BitConverter]::ToString($hashBytes).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-UniqueGeneratedSymbol {
    param(
        [Parameter(Mandatory = $true)] [string]$Label,
        [Parameter(Mandatory = $true)] [string]$Pattern,
        [object]$Method
    )

    $lines =
        if ($null -eq $Method) {
            @((Find-GeneratedLineMatches -Pattern $Pattern) | ForEach-Object { $_.Line })
        }
        else {
            @($Method.Lines)
        }
    $symbols = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $lines) {
        foreach (
            $symbolMatch in [regex]::Matches(
                $line,
                $Pattern,
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
        ) {
            $symbols.Add($symbolMatch.Value)
        }
    }
    $symbols = @($symbols | Sort-Object -Unique)
    if ($symbols.Count -ne 1) {
        throw "Expected one exact generated symbol for $Label, found $($symbols.Count)."
    }
    return $symbols[0]
}

function Add-UniqueGeneratedMethods {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Destination,
        [Parameter(Mandatory = $true)] [object[]]$Methods
    )

    foreach ($method in $Methods) {
        $alreadyCaptured = @(
            $Destination |
                Where-Object {
                    $_.Path -eq $method.Path -and $_.LineNumber -eq $method.LineNumber
                }
        ).Count -gt 0
        if (!$alreadyCaptured) {
            $Destination.Add($method)
        }
    }
}

function Add-GeneratedMethodEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]]$Evidence,
        [Parameter(Mandatory = $true)] [object[]]$Methods,
        [Parameter(Mandatory = $true)] [string]$Root
    )

    foreach ($method in $Methods) {
        $relativePath = $method.Path.Substring($Root.Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        )
        $bodyHash = Get-GeneratedBodySha256 -Method $method
        $Evidence.Add('')
        $Evidence.Add(
            "method=$($method.Label) file=$relativePath bodySha256=$bodyHash " +
            "line=$($method.LineNumber) bodyLineCount=$($method.Lines.Count) " +
            "definitionOccurrenceCount=$($method.DefinitionOccurrenceCount)"
        )
        foreach ($definitionLocation in $method.DefinitionLocations) {
            $definitionRelativePath = $definitionLocation.Path.Substring($Root.Length).TrimStart(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar
            )
            $Evidence.Add(
                "definitionLocation=$definitionRelativePath`:$($definitionLocation.LineNumber)"
            )
        }
        foreach ($line in $method.Lines) {
            $Evidence.Add($line)
        }
    }
}

function Add-NativeLayoutInventory {
    param(
        [Parameter(Mandatory = $true)] [string]$ProjectRoot,
        [Parameter(Mandatory = $true)] [string]$ArtifactsRoot,
        [Parameter(Mandatory = $true)] [string[]]$Symbols,
        [Parameter(Mandatory = $true)] [object[]]$Methods,
        [string[]]$DumpbinPaths
    )

    $playerDir = Join-Path $ProjectRoot 'Build\DxmTestPlayer'
    if (!(Test-Path -LiteralPath $playerDir -PathType Container)) {
        throw "Validated standalone player directory was not found at $playerDir."
    }

    $gameAssemblies = @(
        Get-ChildItem -LiteralPath $playerDir -File -Recurse -Filter 'GameAssembly.dll'
    )
    $symbolMaps = @(
        Get-ChildItem -LiteralPath $playerDir -File -Recurse -Filter 'SymbolMap'
    )
    if ($gameAssemblies.Count -ne 1) {
        throw "Expected one GameAssembly.dll under $playerDir; found $($gameAssemblies.Count)."
    }
    if ($symbolMaps.Count -ne 1) {
        throw "Expected one SymbolMap under $playerDir; found $($symbolMaps.Count)."
    }

    $inventory = [System.Collections.Generic.List[string]]::new()
    $inventory.Add("gameAssembly=$($gameAssemblies[0].FullName)")
    $inventory.Add("gameAssemblyBytes=$($gameAssemblies[0].Length)")
    $inventory.Add("symbolMap=$($symbolMaps[0].FullName)")
    $inventory.Add("symbolMapBytes=$($symbolMaps[0].Length)")

    $pdbFiles = @(
        Get-ChildItem -LiteralPath $playerDir -File -Recurse -Filter '*.pdb' |
            Sort-Object -Property FullName
    )
    $inventory.Add("pdbFileCount=$($pdbFiles.Count)")
    foreach ($pdbFile in $pdbFiles) {
        $inventory.Add("pdb=$($pdbFile.FullName) bytes=$($pdbFile.Length)")
    }

    $inventory.Add('')
    $inventory.Add('symbolMapHead:')
    foreach ($line in @(Get-Content -LiteralPath $symbolMaps[0].FullName -TotalCount 12)) {
        $inventory.Add($line)
    }
    $inventory.Add('')
    $inventory.Add('symbolMapMatches:')
    $symbolMatches = @(
        Select-String -LiteralPath $symbolMaps[0].FullName -SimpleMatch -Pattern $Symbols
    )
    $inventory.Add("symbolMapMatchCount=$($symbolMatches.Count)")
    foreach ($match in $symbolMatches) {
        $inventory.Add("$($match.LineNumber):$($match.Line)")
    }

    $beeArtifacts = Join-Path $ProjectRoot 'Library\Bee\artifacts'
    $sourceBaseNames = @(
        $Methods |
            ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Path) } |
            Sort-Object -Unique
    )
    $objectFiles = @(
        Get-ChildItem -LiteralPath $beeArtifacts -File -Recurse -Filter '*.obj' `
            -ErrorAction SilentlyContinue
    )
    $matchingObjectFiles = @(
        $objectFiles |
            Where-Object {
                $objectName = $_.Name
                @(
                    $sourceBaseNames |
                        Where-Object {
                            $objectName.IndexOf(
                                $_,
                                [System.StringComparison]::OrdinalIgnoreCase
                            ) -ge 0
                        }
                ).Count -gt 0
            } |
            Sort-Object -Property FullName
    )
    $inventory.Add('')
    $inventory.Add("objectFileCount=$($objectFiles.Count)")
    $inventory.Add("matchingObjectFileCount=$($matchingObjectFiles.Count)")
    foreach ($objectFile in $matchingObjectFiles) {
        $inventory.Add("object=$($objectFile.FullName) bytes=$($objectFile.Length)")
    }

    $resolvedDumpbins = @()
    if ($null -ne $DumpbinPaths -and $DumpbinPaths.Count -gt 0) {
        $resolvedDumpbins = @(
            $DumpbinPaths |
                ForEach-Object { Get-Item -LiteralPath $_ }
        )
    }
    else {
        $vsWherePath = Join-Path ${env:ProgramFiles(x86)} (
            'Microsoft Visual Studio\Installer\vswhere.exe'
        )
        if (!(Test-Path -LiteralPath $vsWherePath -PathType Leaf)) {
            throw "vswhere.exe was not found at $vsWherePath."
        }
        $vsWhereOutput = @(
            & $vsWherePath -latest -products '*' -requiresAny `
                -requires 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' `
                -requires 'Microsoft.VisualStudio.Workload.VCTools' `
                -property 'installationPath' 2>$null
        )
        if ($LASTEXITCODE -ne 0) {
            throw "vswhere exited $LASTEXITCODE while locating dumpbin.exe."
        }
        $installRoots = @(
            $vsWhereOutput |
                ForEach-Object { "$($_)".Trim() } |
                Where-Object {
                    ![string]::IsNullOrWhiteSpace($_) -and
                    [System.IO.Path]::IsPathRooted($_)
                }
        )
        if ($installRoots.Count -ne 1) {
            throw "Expected vswhere to return one Visual Studio root; found $($installRoots.Count)."
        }
        $toolsetRoot = Join-Path $installRoots[0] 'VC\Tools\MSVC'
        $toolsets = @(
            Get-ChildItem -LiteralPath $toolsetRoot -Directory -ErrorAction SilentlyContinue |
                Sort-Object -Property @{ Expression = {
                    $parsed = $null
                    if ([version]::TryParse($_.Name, [ref]$parsed)) {
                        $parsed
                    }
                    else {
                        [version]'0.0'
                    }
                } }, Name -Descending
        )
        $resolvedDumpbins = @(
            $toolsets |
                ForEach-Object {
                    Get-Item `
                        -LiteralPath (Join-Path $_.FullName 'bin\Hostx64\x64\dumpbin.exe') `
                        -ErrorAction SilentlyContinue
                }
        )
    }
    $inventory.Add('')
    $inventory.Add("dumpbinCount=$($resolvedDumpbins.Count)")
    foreach ($dumpbin in $resolvedDumpbins) {
        $inventory.Add("dumpbin=$($dumpbin.FullName)")
    }
    if ($resolvedDumpbins.Count -eq 0) {
        throw 'No x64 dumpbin.exe was found under the installed Visual Studio toolsets.'
    }

    $headers = @()
    $selectedDumpbin = $null
    foreach ($dumpbin in $resolvedDumpbins) {
        try {
            $candidateHeaders = @(& $dumpbin.FullName /headers $gameAssemblies[0].FullName 2>&1)
        }
        catch {
            $inventory.Add(
                "dumpbinFailure=$($dumpbin.FullName) exception=$($_.Exception.GetType().Name)"
            )
            continue
        }
        $commandSucceeded = $?
        $exitCodeVariable = Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue
        $candidateExitCode =
            if ($null -ne $exitCodeVariable) {
                $exitCodeVariable.Value
            }
            elseif ($commandSucceeded) {
                0
            }
            else {
                1
            }
        if ($commandSucceeded -and $candidateExitCode -eq 0) {
            $selectedDumpbin = $dumpbin
            $headers = $candidateHeaders
            break
        }
        $inventory.Add("dumpbinFailure=$($dumpbin.FullName) exitCode=$candidateExitCode")
    }
    if ($null -eq $selectedDumpbin) {
        throw "No discovered dumpbin.exe could read $($gameAssemblies[0].FullName)."
    }
    $inventory.Add("selectedDumpbin=$($selectedDumpbin.FullName)")
    $inventory.Add('')
    $inventory.Add('gameAssemblyHeaders:')
    foreach ($line in $headers) {
        $inventory.Add("$line")
    }

    $outputPath = Join-Path $ArtifactsRoot 'native-layout-inventory.txt'
    $inventory | Set-Content -LiteralPath $outputPath -Encoding utf8
    Write-Host "Captured native layout inventory to $outputPath."
}

$targetedPostCallPattern =
    '(?<![A-Za-z0-9_])MessageBus_RunTargetedPostPhases_Tis[A-Za-z0-9_]+_m[0-9A-F]+(?:_gshared)?(?:_inline)?(?![A-Za-z0-9_])'
$untargetedHelperSpecs = @(
    [pscustomobject]@{
        Label   = 'MessageBus.RunUntargetedInterceptors<SimpleUntargetedMessage>'
        Key     = 'untargetedInterceptor'
        Pattern = '(?<![A-Za-z0-9_])MessageBus_RunUntargetedInterceptors_Tis[A-Za-z0-9_]+_m[0-9A-F]+(?:_gshared)?(?:_inline)?(?![A-Za-z0-9_])'
    },
    [pscustomobject]@{
        Label   = 'MessageBus.RunUntargetedPostPhase<SimpleUntargetedMessage>'
        Key     = 'untargetedPost'
        Pattern = '(?<![A-Za-z0-9_])MessageBus_RunUntargetedPostPhase_Tis[A-Za-z0-9_]+_m[0-9A-F]+(?:_gshared)?(?:_inline)?(?![A-Za-z0-9_])'
    },
    [pscustomobject]@{
        Label   = 'MessageBus.DispatchUntargetedHandlePhase<SimpleUntargetedMessage>'
        Key     = 'untargetedHandle'
        Pattern = '(?<![A-Za-z0-9_])MessageBus_DispatchUntargetedHandlePhase_Tis[A-Za-z0-9_]+_m[0-9A-F]+(?:_gshared)?(?:_inline)?(?![A-Za-z0-9_])'
    },
    [pscustomobject]@{
        Label   = 'MessageBus.AcquireDispatchSnapshotFast<SimpleUntargetedMessage>'
        Key     = 'untargetedSnapshot'
        Pattern = '(?<![A-Za-z0-9_])MessageBus_AcquireDispatchSnapshotFast_Tis[A-Za-z0-9_]+_m[0-9A-F]+(?:_gshared)?(?:_inline)?(?![A-Za-z0-9_])'
    }
)
$extractorTestRoot = Join-Path (
    [System.IO.Path]::GetTempPath()
) ("dxm-dispatch-codegen-{0}" -f [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extractorTestRoot | Out-Null
try {
    $forwardedBodyPath = Join-Path $extractorTestRoot 'forwarded.cpp'
    @(
        'inline void Forwarded_m789_inline ()',
        '{',
        'Forwarded_m789_gshared_inline();',
        'MessageBus_RunTargetedPostPhases_TisShared_t123_mABCDEF_gshared_inline();',
        'MessageBus_RunUntargetedInterceptors_TisShared_t123_m111AAA_gshared();',
        'MessageBus_RunUntargetedPostPhase_TisShared_t123_m222BBB_inline();',
        'MessageBus_DispatchUntargetedHandlePhase_TisShared_t123_m333CCC_gshared_inline();',
        'MessageBus_AcquireDispatchSnapshotFast_TisShared_t123_m444DDD_inline();',
        '}',
        'inline void Forwarded_m789_gshared_inline ()',
        '{',
        'if (true)',
        '{',
        '// actual shared implementation',
        '}',
        '}',
        'inline void Neighbor_m987 ()',
        '{',
        '}'
    ) | Set-Content -LiteralPath $forwardedBodyPath -Encoding utf8
    $script:CppPaths = @($forwardedBodyPath)
    Set-GeneratedCppIndex -Paths $script:CppPaths
    $forwardedWrapper = Get-GeneratedMethodDefinition `
        -Label 'extractor self-test forwarded wrapper' `
        -Pattern '(?<![A-Za-z0-9_])Forwarded_m789_inline\s*\('
    $discoveredPostSymbols = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $forwardedWrapper.Lines) {
        foreach (
            $symbolMatch in [regex]::Matches(
                $line,
                $targetedPostCallPattern,
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
        ) {
            $discoveredPostSymbols.Add($symbolMatch.Value)
        }
    }
    if (
        $discoveredPostSymbols.Count -ne 1 -or
        $discoveredPostSymbols[0] -ne
            'MessageBus_RunTargetedPostPhases_TisShared_t123_mABCDEF_gshared_inline'
    ) {
        throw 'Dispatch codegen extractor truncated the targeted post-phase call symbol.'
    }
    foreach ($helperSpec in $untargetedHelperSpecs) {
        $discoveredHelperSymbols = [System.Collections.Generic.List[string]]::new()
        foreach ($line in $forwardedWrapper.Lines) {
            foreach (
                $symbolMatch in [regex]::Matches(
                    $line,
                    $helperSpec.Pattern,
                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
                )
            ) {
                $discoveredHelperSymbols.Add($symbolMatch.Value)
            }
        }
        $discoveredHelperSymbols = @($discoveredHelperSymbols | Sort-Object -Unique)
        if ($discoveredHelperSymbols.Count -ne 1) {
            throw (
                "Dispatch codegen extractor truncated or missed $($helperSpec.Label); " +
                "found $($discoveredHelperSymbols.Count) symbols."
            )
        }
    }
    $forwarded = Get-GeneratedSharedImplementation `
        -Label 'extractor self-test forwarded method' `
        -Wrapper $forwardedWrapper `
        -WrapperSymbol 'Forwarded_m789_inline'
    $selfTestHash = Get-GeneratedBodySha256 -Method $forwarded.Implementation
    if (
        $forwarded.SharedCallSymbol -ne 'Forwarded_m789_gshared_inline' -or
        @(
            $forwarded.Implementation.Lines |
                Where-Object { $_ -match 'actual shared implementation' }
        ).Count -ne 1 -or
        @(
            $forwarded.Implementation.Lines |
                Where-Object { $_ -match 'Neighbor_m987' }
        ).Count -ne 0 -or
        $selfTestHash -notmatch '^[0-9A-F]{64}$'
    ) {
        throw 'Dispatch codegen extractor self-test failed.'
    }
    Write-Host 'Dispatch codegen extractor self-test passed.'
}
finally {
    Remove-Item -LiteralPath $extractorTestRoot -Recurse -Force
}

if ($SelfTestOnly) {
    $integrationTestRoot = Join-Path (
        [System.IO.Path]::GetTempPath()
    ) ("dxm-dispatch-codegen-integration-{0}" -f [guid]::NewGuid().ToString('N'))
    try {
        $integrationCppRoot = Join-Path (
            $integrationTestRoot
        ) 'Library\Bee\artifacts\Player\il2cppOutput'
        $integrationArtifacts = Join-Path $integrationTestRoot 'artifacts'
        New-Item -ItemType Directory -Path $integrationCppRoot | Out-Null
        New-Item -ItemType Directory -Path $integrationArtifacts | Out-Null
        $integrationCppPath = Join-Path $integrationCppRoot 'Generated.cpp'
        $integrationLines = [System.Collections.Generic.List[string]]::new()
        function Add-IntegrationMethod {
            param(
                [Parameter(Mandatory = $true)] [string]$Name,
                [Parameter(Mandatory = $true)] [string[]]$Body
            )

            $integrationLines.Add("inline void $Name ()")
            $integrationLines.Add('{')
            foreach ($line in $Body) {
                $integrationLines.Add($line)
            }
            $integrationLines.Add('}')
        }
        Add-IntegrationMethod `
            'MessageBus_TargetedBroadcast_TisSimpleTargetedMessage_tAAAA_mA001' `
            @('MessageBus_TargetedBroadcast_TisSimpleTargetedMessage_tAAAA_mA001_gshared();')
        Add-IntegrationMethod `
            'MessageBus_TargetedBroadcast_TisSimpleTargetedMessage_tAAAA_mA001_gshared' `
            @('MessageBus_RunTargetedPostPhases_TisSimpleTargetedMessage_tAAAA_mA002_inline();')
        Add-IntegrationMethod `
            'MessageBus_RunTargetedPostPhases_TisSimpleTargetedMessage_tAAAA_mA002_inline' `
            @('MessageBus_RunTargetedPostPhases_TisSimpleTargetedMessage_tAAAA_mA002_gshared_inline();')
        Add-IntegrationMethod `
            'MessageBus_RunTargetedPostPhases_TisSimpleTargetedMessage_tAAAA_mA002_gshared_inline' `
            @('// targeted post shared body')
        Add-IntegrationMethod `
            'MessageBus_UntargetedBroadcast_TisSimpleUntargetedMessage_tBBBB_mB001' `
            @('MessageBus_UntargetedBroadcast_TisSimpleUntargetedMessage_tBBBB_mB001_gshared();')
        Add-IntegrationMethod `
            'MessageBus_UntargetedBroadcast_TisSimpleUntargetedMessage_tBBBB_mB001_gshared' `
            @(
                'MessageBus_RunUntargetedInterceptors_TisSimpleUntargetedMessage_tBBBB_mB002();',
                'MessageBus_RunUntargetedPostPhase_TisSimpleUntargetedMessage_tBBBB_mB003_inline();',
                'MessageBus_DispatchUntargetedHandlePhase_TisSimpleUntargetedMessage_tBBBB_mB004_inline();',
                'MessageBus_AcquireDispatchSnapshotFast_TisSimpleUntargetedMessage_tBBBB_mB005_inline();'
            )
        foreach (
            $method in @(
                @('RunUntargetedInterceptors', 'B002', ''),
                @('RunUntargetedPostPhase', 'B003', '_inline'),
                @('DispatchUntargetedHandlePhase', 'B004', '_inline'),
                @('AcquireDispatchSnapshotFast', 'B005', '_inline')
            )
        ) {
            $wrapperName = "MessageBus_$($method[0])_TisSimpleUntargetedMessage_tBBBB_m$($method[1])$($method[2])"
            $sharedName = $wrapperName -replace '_inline$', ''
            $sharedName += '_gshared' + $method[2]
            Add-IntegrationMethod $wrapperName @("$sharedName();")
            Add-IntegrationMethod $sharedName @("// $($method[0]) shared body")
        }
        $integrationLines | Set-Content -LiteralPath $integrationCppPath -Encoding utf8
        @(
            'inline void MessageBus_UntargetedBroadcast_TisSimpleUntargetedMessage_tBBBB_mB001 ()',
            '{',
            'MessageBus_UntargetedBroadcast_TisSimpleUntargetedMessage_tBBBB_mB001_gshared();',
            '}'
        ) | Set-Content `
            -LiteralPath (Join-Path $integrationCppRoot 'Duplicate.cpp') `
            -Encoding utf8

        & $PSCommandPath `
            -ProjectPath $integrationTestRoot `
            -ArtifactsPath $integrationArtifacts `
            -SkipNativeInventory
        if (!$?) {
            throw 'Dispatch codegen integration self-test child invocation failed.'
        }

        $targetedEvidencePath = Join-Path $integrationArtifacts 'targeted-post-codegen.txt'
        $untargetedEvidencePath = Join-Path $integrationArtifacts 'untargeted-hook-codegen.txt'
        if (
            !(Test-Path -LiteralPath $targetedEvidencePath -PathType Leaf) -or
            !(Test-Path -LiteralPath $untargetedEvidencePath -PathType Leaf)
        ) {
            throw 'Dispatch codegen integration self-test did not write both evidence files.'
        }
        $untargetedIntegrationEvidence = Get-Content `
            -LiteralPath $untargetedEvidencePath `
            -Raw
        foreach (
            $expectedEvidence in @(
                'capturedMethodCount=10',
                'untargetedInterceptorSharedCallSymbol=',
                'untargetedPostSharedCallSymbol=',
                'untargetedHandleSharedCallSymbol=',
                'untargetedSnapshotSharedCallSymbol=',
                'definitionOccurrenceCount=2',
                'AcquireDispatchSnapshotFast shared body'
            )
        ) {
            if (!$untargetedIntegrationEvidence.Contains($expectedEvidence)) {
                throw "Dispatch codegen integration evidence omitted '$expectedEvidence'."
            }
        }

        $playerDir = Join-Path $integrationTestRoot 'Build\DxmTestPlayer'
        $backupDir = Join-Path $playerDir 'DxmTestPlayer_BackUpThisFolder_ButDontShipItWithYourGame'
        New-Item -ItemType Directory -Path $backupDir | Out-Null
        'fake native image' | Set-Content -LiteralPath (Join-Path $playerDir 'GameAssembly.dll')
        @(
            '0000000000001000 16 OtherSymbol',
            '0000000000002000 32 InventoryProbeSymbol'
        ) | Set-Content -LiteralPath (Join-Path $backupDir 'SymbolMap')
        'fake pdb' | Set-Content -LiteralPath (Join-Path $backupDir 'GameAssembly.pdb')
        'fake object' | Set-Content -LiteralPath (Join-Path $integrationCppRoot 'gEnErAtEd.cpp.obj')
        $fakeDumpbin = Join-Path $integrationTestRoot 'fake-dumpbin.ps1'
        $failedDumpbin = Join-Path $integrationTestRoot 'failed-dumpbin.ps1'
        $throwingDumpbin = Join-Path $integrationTestRoot 'throwing-dumpbin.ps1'
        'exit 7' | Set-Content -LiteralPath $failedDumpbin -Encoding utf8
        'throw "synthetic dumpbin launch failure"' |
            Set-Content -LiteralPath $throwingDumpbin -Encoding utf8
        @(
            'param([string]$Option, [string]$Image)',
            'if ($Option -ne "/headers" -or !(Test-Path -LiteralPath $Image)) { exit 1 }',
            'Write-Output "PE header proof"',
            'exit 0'
        ) | Set-Content -LiteralPath $fakeDumpbin -Encoding utf8
        Add-NativeLayoutInventory `
            -ProjectRoot $integrationTestRoot `
            -ArtifactsRoot $integrationArtifacts `
            -Symbols @('InventoryProbeSymbol') `
            -Methods @([pscustomobject]@{ Path = $integrationCppPath }) `
            -DumpbinPaths @($failedDumpbin, $throwingDumpbin, $fakeDumpbin)
        $nativeInventory = Get-Content `
            -LiteralPath (Join-Path $integrationArtifacts 'native-layout-inventory.txt') `
            -Raw
        foreach (
            $expectedInventoryEvidence in @(
                'symbolMapMatchCount=1',
                'InventoryProbeSymbol',
                'pdbFileCount=1',
                'matchingObjectFileCount=1',
                'exitCode=7',
                'dumpbinFailure=',
                'selectedDumpbin=',
                'PE header proof'
            )
        ) {
            if (!$nativeInventory.Contains($expectedInventoryEvidence)) {
                throw "Native layout inventory omitted '$expectedInventoryEvidence'."
            }
        }
        Write-Host 'Native layout inventory self-test passed.'
        Write-Host 'Dispatch codegen integration self-test passed.'
    }
    finally {
        Remove-Item -LiteralPath $integrationTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit 0
}
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    throw 'ProjectPath is required unless SelfTestOnly is set.'
}
if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    throw 'ArtifactsPath is required unless SelfTestOnly is set.'
}

$resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$resolvedArtifactsPath = (Resolve-Path -LiteralPath $ArtifactsPath).Path
$beeArtifacts = Join-Path $resolvedProjectPath 'Library\Bee\artifacts'
$il2cppRoots = @(
    Get-ChildItem `
        -LiteralPath $beeArtifacts `
        -Directory `
        -Recurse `
        -Filter 'il2cppOutput' `
        -ErrorAction SilentlyContinue
)
if ($il2cppRoots.Count -eq 0) {
    throw "No il2cppOutput directory was found under $beeArtifacts."
}
$il2cppRoot = $il2cppRoots |
    Sort-Object -Property LastWriteTimeUtc -Descending |
    Select-Object -First 1
$cppFiles = @(
    Get-ChildItem -LiteralPath $il2cppRoot.FullName -File -Recurse -Filter '*.cpp' |
        Sort-Object -Property FullName
)
if ($cppFiles.Count -eq 0) {
    throw "No generated C++ files were found under $($il2cppRoot.FullName)."
}
$script:CppPaths = @($cppFiles | ForEach-Object { $_.FullName })
Set-GeneratedCppIndex -Paths $script:CppPaths

$targetedBroadcastSymbolPattern =
    '(?<![A-Za-z0-9_])MessageBus_TargetedBroadcast_TisSimpleTargetedMessage_t[0-9A-F]+_m[0-9A-F]+'
$targetedBroadcastSymbol = Get-UniqueGeneratedSymbol `
    -Label 'MessageBus.TargetedBroadcast<SimpleTargetedMessage>' `
    -Pattern $targetedBroadcastSymbolPattern

$targetedBroadcastWrapper = Get-GeneratedMethodDefinition `
    -Label 'MessageBus.TargetedBroadcast<SimpleTargetedMessage>' `
    -Pattern ("(?<![A-Za-z0-9_]){0}\s*\(" -f [regex]::Escape($targetedBroadcastSymbol))
$targetedBroadcast = Get-GeneratedSharedImplementation `
    -Label 'MessageBus.TargetedBroadcast<SimpleTargetedMessage>' `
    -Wrapper $targetedBroadcastWrapper `
    -WrapperSymbol $targetedBroadcastSymbol
Write-Host "Targeted broadcast generated symbol: $targetedBroadcastSymbol"
Write-Host "Targeted broadcast shared symbol: $($targetedBroadcast.SharedCallSymbol)"

$targetedPostCallSymbol = Get-UniqueGeneratedSymbol `
    -Label 'MessageBus.RunTargetedPostPhases<SimpleTargetedMessage> call' `
    -Pattern $targetedPostCallPattern `
    -Method $targetedBroadcast.Implementation
Write-Host "Targeted post generated call symbol: $targetedPostCallSymbol"

$targetedPostWrapper = Get-GeneratedMethodDefinition `
    -Label 'MessageBus.RunTargetedPostPhases<SimpleTargetedMessage>' `
    -Pattern ("(?<![A-Za-z0-9_]){0}\s*\(" -f [regex]::Escape($targetedPostCallSymbol))
$targetedPost = Get-GeneratedSharedImplementation `
    -Label 'MessageBus.RunTargetedPostPhases<SimpleTargetedMessage>' `
    -Wrapper $targetedPostWrapper `
    -WrapperSymbol $targetedPostCallSymbol

$targetedPostMethods = [System.Collections.Generic.List[object]]::new()
Add-UniqueGeneratedMethods `
    -Destination $targetedPostMethods `
    -Methods @(
        $targetedBroadcast.Wrapper,
        $targetedBroadcast.Implementation,
        $targetedPost.Wrapper,
        $targetedPost.Implementation
    )

$evidence = [System.Collections.Generic.List[string]]::new()
$evidence.Add("il2cppOutput=$($il2cppRoot.FullName)")
$evidence.Add("cppFileCount=$($cppFiles.Count)")
$evidence.Add("capturedMethodCount=$($targetedPostMethods.Count)")
$evidence.Add("targetedBroadcastSymbol=$targetedBroadcastSymbol")
$evidence.Add("targetedBroadcastSharedCallSymbol=$($targetedBroadcast.SharedCallSymbol)")
$evidence.Add("targetedPostCallSymbol=$targetedPostCallSymbol")
$evidence.Add("targetedPostSharedCallSymbol=$($targetedPost.SharedCallSymbol)")
Add-GeneratedMethodEvidence `
    -Evidence $evidence `
    -Methods @($targetedPostMethods) `
    -Root $il2cppRoot.FullName

$outputPath = Join-Path $resolvedArtifactsPath 'targeted-post-codegen.txt'
$evidence | Set-Content -LiteralPath $outputPath -Encoding utf8
Write-Host (
    "Captured $($targetedPostMethods.Count) exact targeted post-route C++ method bodies " +
    "to $outputPath."
)

$untargetedBroadcastSymbolPattern =
    '(?<![A-Za-z0-9_])MessageBus_UntargetedBroadcast_TisSimpleUntargetedMessage_t[0-9A-F]+_m[0-9A-F]+'
$untargetedBroadcastSymbol = Get-UniqueGeneratedSymbol `
    -Label 'MessageBus.UntargetedBroadcast<SimpleUntargetedMessage>' `
    -Pattern $untargetedBroadcastSymbolPattern

$untargetedBroadcastWrapper = Get-GeneratedMethodDefinition `
    -Label 'MessageBus.UntargetedBroadcast<SimpleUntargetedMessage>' `
    -Pattern ("(?<![A-Za-z0-9_]){0}\s*\(" -f [regex]::Escape($untargetedBroadcastSymbol))
$untargetedBroadcast = Get-GeneratedSharedImplementation `
    -Label 'MessageBus.UntargetedBroadcast<SimpleUntargetedMessage>' `
    -Wrapper $untargetedBroadcastWrapper `
    -WrapperSymbol $untargetedBroadcastSymbol
Write-Host "Untargeted broadcast generated symbol: $untargetedBroadcastSymbol"
Write-Host "Untargeted broadcast shared symbol: $($untargetedBroadcast.SharedCallSymbol)"

$untargetedRouteMethods = [System.Collections.Generic.List[object]]::new()
Add-UniqueGeneratedMethods `
    -Destination $untargetedRouteMethods `
    -Methods @($untargetedBroadcast.Wrapper, $untargetedBroadcast.Implementation)
$untargetedHelperEvidence = [System.Collections.Generic.List[object]]::new()
foreach ($helperSpec in $untargetedHelperSpecs) {
    $helperCallSymbol = Get-UniqueGeneratedSymbol `
        -Label "$($helperSpec.Label) call" `
        -Pattern $helperSpec.Pattern `
        -Method $untargetedBroadcast.Implementation

    $helperWrapper = Get-GeneratedMethodDefinition `
        -Label $helperSpec.Label `
        -Pattern ("(?<![A-Za-z0-9_]){0}\s*\(" -f [regex]::Escape($helperCallSymbol))
    $helper = Get-GeneratedSharedImplementation `
        -Label $helperSpec.Label `
        -Wrapper $helperWrapper `
        -WrapperSymbol $helperCallSymbol
    $untargetedHelperEvidence.Add(
        [pscustomobject]@{
            Key              = $helperSpec.Key
            CallSymbol       = $helperCallSymbol
            SharedCallSymbol = $helper.SharedCallSymbol
        }
    )
    Add-UniqueGeneratedMethods `
        -Destination $untargetedRouteMethods `
        -Methods @($helper.Wrapper, $helper.Implementation)
}

$untargetedEvidence = [System.Collections.Generic.List[string]]::new()
$untargetedEvidence.Add("il2cppOutput=$($il2cppRoot.FullName)")
$untargetedEvidence.Add("cppFileCount=$($cppFiles.Count)")
$untargetedEvidence.Add("capturedMethodCount=$($untargetedRouteMethods.Count)")
$untargetedEvidence.Add(
    'comparisonScenarios=GlobalToOne,Filtered,PostProcess,FilteredPostProcess'
)
$untargetedEvidence.Add('featuredComparisonScenarios=Filtered,PostProcess,FilteredPostProcess')
$untargetedEvidence.Add("untargetedBroadcastSymbol=$untargetedBroadcastSymbol")
$untargetedEvidence.Add(
    "untargetedBroadcastSharedCallSymbol=$($untargetedBroadcast.SharedCallSymbol)"
)
foreach ($helper in $untargetedHelperEvidence) {
    $untargetedEvidence.Add("$($helper.Key)CallSymbol=$($helper.CallSymbol)")
    $untargetedEvidence.Add("$($helper.Key)SharedCallSymbol=$($helper.SharedCallSymbol)")
}
Add-GeneratedMethodEvidence `
    -Evidence $untargetedEvidence `
    -Methods @($untargetedRouteMethods) `
    -Root $il2cppRoot.FullName

$untargetedOutputPath = Join-Path $resolvedArtifactsPath 'untargeted-hook-codegen.txt'
$untargetedEvidence | Set-Content -LiteralPath $untargetedOutputPath -Encoding utf8
Write-Host (
    "Captured $($untargetedRouteMethods.Count) exact untargeted hook-route C++ method bodies " +
    "to $untargetedOutputPath."
)

$nativeSymbols = [System.Collections.Generic.List[string]]::new()
$nativeSymbols.Add($untargetedBroadcastSymbol)
$nativeSymbols.Add($untargetedBroadcast.SharedCallSymbol)
foreach ($helper in $untargetedHelperEvidence) {
    $nativeSymbols.Add($helper.CallSymbol)
    $nativeSymbols.Add($helper.SharedCallSymbol)
}
if (!$SkipNativeInventory) {
    Add-NativeLayoutInventory `
        -ProjectRoot $resolvedProjectPath `
        -ArtifactsRoot $resolvedArtifactsPath `
        -Symbols @($nativeSymbols) `
        -Methods @($untargetedRouteMethods)
}
