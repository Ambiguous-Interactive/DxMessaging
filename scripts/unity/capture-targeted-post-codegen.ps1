#Requires -Version 5.1
# cspell:ignore gshared
[CmdletBinding()]
param(
    [string]$ProjectPath,

    [string]$ArtifactsPath,

    [switch]$SelfTestOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# SYNC: .github/workflows/perf-numbers.yml duplicates this generated-method
# parser for typed-deregistration evidence; keep brace and uniqueness handling aligned.
function Get-GeneratedMethodDefinition {
    param(
        [Parameter(Mandatory = $true)] [string]$Label,
        [Parameter(Mandatory = $true)] [string]$Pattern
    )

    $signatureMatches = @(
        Select-String `
            -LiteralPath $script:CppPaths `
            -Pattern $Pattern
    )
    $bodyCandidates = [System.Collections.Generic.List[object]]::new()
    foreach ($signature in $signatureMatches) {
        $fileLines = @(Get-Content -LiteralPath $signature.Path)
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

    if ($WrapperSymbol -match '_gshared$') {
        return [pscustomobject]@{
            Wrapper          = $Wrapper
            Implementation   = $Wrapper
            SharedCallSymbol = $WrapperSymbol
        }
    }

    $sharedCallPattern = "(?<![A-Za-z0-9_]){0}_gshared" -f [regex]::Escape(
        $WrapperSymbol
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

$extractorTestRoot = Join-Path (
    [System.IO.Path]::GetTempPath()
) ("dxm-targeted-codegen-{0}" -f [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extractorTestRoot | Out-Null
try {
    $forwardedBodyPath = Join-Path $extractorTestRoot 'forwarded.cpp'
    @(
        'inline void Forwarded_m789 ()',
        '{',
        'Forwarded_m789_gshared();',
        '}',
        'inline void Forwarded_m789_gshared ()',
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
    $forwardedWrapper = Get-GeneratedMethodDefinition `
        -Label 'extractor self-test forwarded wrapper' `
        -Pattern '(?<![A-Za-z0-9_])Forwarded_m789\s*\('
    $forwarded = Get-GeneratedSharedImplementation `
        -Label 'extractor self-test forwarded method' `
        -Wrapper $forwardedWrapper `
        -WrapperSymbol 'Forwarded_m789'
    $selfTestHash = Get-GeneratedBodySha256 -Method $forwarded.Implementation
    if (
        $forwarded.SharedCallSymbol -ne 'Forwarded_m789_gshared' -or
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
        throw 'Targeted codegen extractor self-test failed.'
    }
    Write-Host 'Targeted codegen extractor self-test passed.'
}
finally {
    Remove-Item -LiteralPath $extractorTestRoot -Recurse -Force
}

if ($SelfTestOnly) {
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

$targetedBroadcastSymbolPattern =
    '(?<![A-Za-z0-9_])MessageBus_TargetedBroadcast_TisSimpleTargetedMessage_t[0-9A-F]+_m[0-9A-F]+'
$targetedBroadcastSymbols = [System.Collections.Generic.List[string]]::new()
$targetedBroadcastSymbolMatches = @(
    Select-String `
        -LiteralPath $script:CppPaths `
        -Pattern $targetedBroadcastSymbolPattern
)
foreach ($symbolLineMatch in $targetedBroadcastSymbolMatches) {
    foreach (
        $symbolMatch in [regex]::Matches(
            $symbolLineMatch.Line,
            $targetedBroadcastSymbolPattern,
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
        )
    ) {
        $targetedBroadcastSymbols.Add($symbolMatch.Value)
    }
}
$targetedBroadcastSymbols = @($targetedBroadcastSymbols | Sort-Object -Unique)
if ($targetedBroadcastSymbols.Count -ne 1) {
    throw (
        'Expected one concrete MessageBus.TargetedBroadcast<SimpleTargetedMessage> ' +
        "symbol, found $($targetedBroadcastSymbols.Count)."
    )
}

$targetedBroadcastWrapper = Get-GeneratedMethodDefinition `
    -Label 'MessageBus.TargetedBroadcast<SimpleTargetedMessage>' `
    -Pattern ("(?<![A-Za-z0-9_]){0}\s*\(" -f [regex]::Escape($targetedBroadcastSymbols[0]))
$targetedBroadcast = Get-GeneratedSharedImplementation `
    -Label 'MessageBus.TargetedBroadcast<SimpleTargetedMessage>' `
    -Wrapper $targetedBroadcastWrapper `
    -WrapperSymbol $targetedBroadcastSymbols[0]

$targetedPostCallPattern =
    '(?<![A-Za-z0-9_])MessageBus_RunTargetedPostPhases_Tis[A-Za-z0-9_]+_m[0-9A-F]+(?:_gshared)?'
$targetedPostCallSymbols = [System.Collections.Generic.List[string]]::new()
foreach ($line in $targetedBroadcast.Implementation.Lines) {
    foreach (
        $symbolMatch in [regex]::Matches(
            $line,
            $targetedPostCallPattern,
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
        )
    ) {
        $targetedPostCallSymbols.Add($symbolMatch.Value)
    }
}
$targetedPostCallSymbols = @($targetedPostCallSymbols | Sort-Object -Unique)
if ($targetedPostCallSymbols.Count -ne 1) {
    throw (
        'Expected MessageBus.TargetedBroadcast<SimpleTargetedMessage> to call one exact ' +
        "targeted post-phase method, found $($targetedPostCallSymbols.Count)."
    )
}

$targetedPostWrapper = Get-GeneratedMethodDefinition `
    -Label 'MessageBus.RunTargetedPostPhases<SimpleTargetedMessage>' `
    -Pattern ("(?<![A-Za-z0-9_]){0}\s*\(" -f [regex]::Escape($targetedPostCallSymbols[0]))
$targetedPost = Get-GeneratedSharedImplementation `
    -Label 'MessageBus.RunTargetedPostPhases<SimpleTargetedMessage>' `
    -Wrapper $targetedPostWrapper `
    -WrapperSymbol $targetedPostCallSymbols[0]

$targetedPostMethods = [System.Collections.Generic.List[object]]::new()
foreach (
    $method in @(
        $targetedBroadcast.Wrapper,
        $targetedBroadcast.Implementation,
        $targetedPost.Wrapper,
        $targetedPost.Implementation
    )
) {
    $alreadyCaptured = @(
        $targetedPostMethods |
            Where-Object {
                $_.Path -eq $method.Path -and $_.LineNumber -eq $method.LineNumber
            }
    ).Count -gt 0
    if (!$alreadyCaptured) {
        $targetedPostMethods.Add($method)
    }
}

$evidence = [System.Collections.Generic.List[string]]::new()
$evidence.Add("il2cppOutput=$($il2cppRoot.FullName)")
$evidence.Add("cppFileCount=$($cppFiles.Count)")
$evidence.Add("capturedMethodCount=$($targetedPostMethods.Count)")
$evidence.Add("targetedBroadcastSymbol=$($targetedBroadcastSymbols[0])")
$evidence.Add("targetedBroadcastSharedCallSymbol=$($targetedBroadcast.SharedCallSymbol)")
$evidence.Add("targetedPostCallSymbol=$($targetedPostCallSymbols[0])")
$evidence.Add("targetedPostSharedCallSymbol=$($targetedPost.SharedCallSymbol)")
foreach ($method in $targetedPostMethods) {
    $relativePath = $method.Path.Substring($il2cppRoot.FullName.Length).TrimStart(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $bodyHash = Get-GeneratedBodySha256 -Method $method
    $evidence.Add('')
    $evidence.Add(
        "method=$($method.Label) file=$relativePath bodySha256=$bodyHash " +
        "line=$($method.LineNumber) bodyLineCount=$($method.Lines.Count) " +
        "definitionOccurrenceCount=$($method.DefinitionOccurrenceCount)"
    )
    foreach ($definitionLocation in $method.DefinitionLocations) {
        $definitionRelativePath = $definitionLocation.Path.Substring(
            $il2cppRoot.FullName.Length
        ).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        )
        $evidence.Add(
            "definitionLocation=$definitionRelativePath`:$($definitionLocation.LineNumber)"
        )
    }
    foreach ($line in $method.Lines) {
        $evidence.Add($line)
    }
}

$outputPath = Join-Path $resolvedArtifactsPath 'targeted-post-codegen.txt'
$evidence | Set-Content -LiteralPath $outputPath -Encoding utf8
Write-Host (
    "Captured $($targetedPostMethods.Count) exact targeted post-route C++ method bodies " +
    "to $outputPath."
)
