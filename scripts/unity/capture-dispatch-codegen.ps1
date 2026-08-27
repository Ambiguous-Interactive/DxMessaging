#Requires -Version 5.1
# cspell:ignore dbghelp disasm gshared nobytes pdbpath
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

function Get-GeneratedSymbolMatches {
    param(
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
    return @($symbols)
}

function Get-GeneratedSymbols {
    param(
        [Parameter(Mandatory = $true)] [string]$Pattern,
        [object]$Method
    )

    return @(Get-GeneratedSymbolMatches -Pattern $Pattern -Method $Method | Sort-Object -Unique)
}

function Get-UniqueGeneratedSymbol {
    param(
        [Parameter(Mandatory = $true)] [string]$Label,
        [Parameter(Mandatory = $true)] [string]$Pattern,
        [object]$Method
    )

    $symbols = @(Get-GeneratedSymbols -Pattern $Pattern -Method $Method)
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

function Invoke-NativeCommandOutput {
    param(
        [Parameter(Mandatory = $true)] [string]$FilePath,
        [Parameter(Mandatory = $true)] [string[]]$Arguments,
        [Parameter(Mandatory = $true)] [ref]$ExitCode
    )

    & $FilePath @Arguments 2>&1
    # Capture this before returning control to any downstream pipeline cmdlet.
    # $LASTEXITCODE is the native program's status; `$?` at the caller describes
    # the complete PowerShell pipeline instead.
    $ExitCode.Value = $LASTEXITCODE
}

function Initialize-NativeSourceLineReader {
    if ($null -eq ('DxMessagingNativeSourceLineReader' -as [type])) {
        $readerSourcePath = Join-Path $PSScriptRoot 'lib/native-source-line-reader.cs.txt'
        if (!(Test-Path -LiteralPath $readerSourcePath -PathType Leaf)) {
            throw "Native source-line reader source was not found at $readerSourcePath."
        }
        $readerSource = Get-Content -LiteralPath $readerSourcePath -Raw
        Add-Type -TypeDefinition $readerSource
    }
    [DxMessagingNativeSourceLineReader]::ValidateInteropLayout()
}

function Get-NativeSourceLineMap {
    param(
        [Parameter(Mandatory = $true)] [string]$ImagePath,
        [Parameter(Mandatory = $true)] [string]$PdbPath,
        [Parameter(Mandatory = $true)] [object[]]$Methods,
        [Parameter(Mandatory = $true)] [string[]]$Symbols,
        [Parameter(Mandatory = $true)] [object[]]$RequiredMethods,
        [scriptblock]$LineReader
    )

    $requestedRequiredMethods = @($RequiredMethods)
    if ($requestedRequiredMethods.Count -eq 0) {
        throw 'At least one required native method must be provided.'
    }
    $duplicateRequiredLabels = @(
        $requestedRequiredMethods |
            Group-Object -Property Label |
            Where-Object { $_.Count -gt 1 }
    )
    if ($duplicateRequiredLabels.Count -gt 0) {
        throw (
            'Required native method labels must be unique; duplicates: ' +
            (@($duplicateRequiredLabels | ForEach-Object { $_.Name }) -join ', ')
        )
    }

    $uniqueSymbols = [System.Collections.Generic.List[string]]::new()
    $seenSymbols = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($symbol in $Symbols) {
        if ($seenSymbols.Add($symbol)) {
            $uniqueSymbols.Add($symbol)
        }
    }

    $capturedRequiredMethods = [System.Collections.Generic.List[object]]::new()
    foreach ($requiredMethod in $requestedRequiredMethods) {
        $capturedMatches = @(
            $Methods |
                Where-Object {
                    [string]::Equals(
                        "$($_.Label)",
                        "$($requiredMethod.Label)",
                        [System.StringComparison]::Ordinal
                    )
                }
        )
        if ($capturedMatches.Count -ne 1) {
            throw (
                "Expected one captured required native method '$($requiredMethod.Label)'; " +
                "found $($capturedMatches.Count)."
            )
        }
        $capturedRequiredMethods.Add($capturedMatches[0])
    }

    $targets = [System.Collections.Generic.List[object]]::new()
    foreach ($method in $capturedRequiredMethods) {
        $methodTargetCountBefore = $targets.Count
        $definitionLocations =
            if ($null -ne $method.PSObject.Properties['DefinitionLocations']) {
                @($method.DefinitionLocations)
            }
            else {
                @(
                    [pscustomobject]@{
                        Path       = $method.Path
                        LineNumber = $method.LineNumber
                    }
                )
            }
        foreach ($definitionLocation in $definitionLocations) {
            $rangeStartLine = [uint32]$definitionLocation.LineNumber
            $rangeEndLine = [uint32](
                $definitionLocation.LineNumber + $method.Lines.Count - 1
            )
            $targetPrefix = '{0}@{1}:{2}' -f
                $method.Label,
                $definitionLocation.Path,
                $definitionLocation.LineNumber
            for ($lineIndex = 1; $lineIndex -lt $method.Lines.Count; $lineIndex++) {
                $sourceLine = $method.Lines[$lineIndex]
                foreach ($symbol in $uniqueSymbols) {
                    if (
                        [regex]::IsMatch(
                            $sourceLine,
                            '(?<![A-Za-z0-9_]){0}(?![A-Za-z0-9_])' -f
                                [regex]::Escape($symbol)
                        )
                    ) {
                        $requestedLine = [uint32]($rangeStartLine + $lineIndex)
                        $targets.Add(
                            [pscustomobject]@{
                                Id             = ('{0}:call:{1}:line{2}' -f
                                    $targetPrefix,
                                    $symbol,
                                    $requestedLine)
                                LogicalId      = ('{0}:call:{1}:offset{2}' -f
                                    $method.Label,
                                    $symbol,
                                    $lineIndex)
                                Path           = $definitionLocation.Path
                                RequestedLine  = $requestedLine
                                RangeStartLine = $rangeStartLine
                                RangeEndLine   = $rangeEndLine
                                Kind           = 'call'
                                Symbol         = $symbol
                            }
                        )
                    }
                }
            }
        }
        if ($targets.Count -eq $methodTargetCountBefore) {
            throw "Required native method '$($method.Label)' had no captured call targets."
        }
    }

    if ($targets.Count -eq 0) {
        throw 'No generated source targets were declared for native mapping.'
    }
    $requiredLogicalTargetIds = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($target in $targets) {
        [void]$requiredLogicalTargetIds.Add($target.LogicalId)
    }
    if ($null -eq $LineReader) {
        if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
            throw 'Native PDB source-line mapping requires Windows DbgHelp.'
        }
        Initialize-NativeSourceLineReader
        $nativeTargets =
            [System.Collections.Generic.List[DxMessagingNativeSourceTarget]]::new()
        foreach ($target in $targets) {
            $nativeTargets.Add(
                [DxMessagingNativeSourceTarget]@{
                    Id             = $target.Id
                    Path           = $target.Path
                    RequestedLine  = $target.RequestedLine
                    RangeStartLine = $target.RangeStartLine
                    RangeEndLine   = $target.RangeEndLine
                }
            )
        }
        $readResult = [DxMessagingNativeSourceLineReader]::Read(
            $ImagePath,
            $PdbPath,
            @($nativeTargets)
        )
    }
    else {
        $readResult = & $LineReader $ImagePath $PdbPath @($targets)
    }

    $lineRecords = @($readResult.Lines)
    if ($lineRecords.Count -gt (2 * $targets.Count)) {
        throw (
            "Native source-line reader returned $($lineRecords.Count) records for " +
            "$($targets.Count) targets; the maximum is two records per target."
        )
    }
    $targetIds = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($target in $targets) {
        [void]$targetIds.Add($target.Id)
    }
    foreach ($lineRecord in $lineRecords) {
        if (!$targetIds.Contains($lineRecord.Target)) {
            throw "Native source-line reader returned unknown target '$($lineRecord.Target)'."
        }
    }

    $evidence = [System.Collections.Generic.List[string]]::new()
    $selectedAddresses = [System.Collections.Generic.List[object]]::new()
    $resolvedRequiredLogicalTargetIds = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($target in $targets) {
        $rawTargetLines = @($lineRecords | Where-Object { $_.Target -eq $target.Id })
        if ($rawTargetLines.Count -gt 2) {
            throw "Native source target '$($target.Id)' returned more than two records."
        }
        foreach ($targetLine in $rawTargetLines) {
            $relationIsValid =
                switch ($targetLine.Relation) {
                    'exact' {
                        $targetLine.SelectedLine -eq $target.RequestedLine
                        break
                    }
                    'preceding' {
                        $targetLine.SelectedLine -lt $target.RequestedLine
                        break
                    }
                    'following' {
                        $targetLine.SelectedLine -gt $target.RequestedLine
                        break
                    }
                    default { $false }
                }
            if (
                $targetLine.RequestedLine -ne $target.RequestedLine -or
                $targetLine.SelectedLine -lt $target.RangeStartLine -or
                $targetLine.SelectedLine -gt $target.RangeEndLine -or
                !$relationIsValid
            ) {
                throw "Native source target '$($target.Id)' returned an invalid line relation."
            }
            $address = [uint64]$targetLine.Address
            $nativeSymbolName = "$($targetLine.NativeSymbolName)"
            $nativeSymbolAddress = [uint64]$targetLine.NativeSymbolAddress
            $nativeSymbolEndAddress = [uint64]$targetLine.NativeSymbolEndAddress
            $moduleBase = [uint64]$readResult.ModuleBase
            if (
                $address -lt $moduleBase -or
                [string]::IsNullOrWhiteSpace($nativeSymbolName) -or
                $nativeSymbolAddress -gt $address -or
                $nativeSymbolEndAddress -le $address -or
                [uint64]$targetLine.RelativeVirtualAddress -ne ($address - $moduleBase)
            ) {
                throw (
                    "Native source target '$($target.Id)' returned an invalid native " +
                    'address or containing-symbol extent.'
                )
            }
        }
        $targetLines = @(
            $rawTargetLines |
                Sort-Object -Property Relation, SelectedLine, Address -Unique
        )
        $exactLines = @($targetLines | Where-Object { $_.Relation -eq 'exact' })
        $precedingLines = @($targetLines | Where-Object { $_.Relation -eq 'preceding' })
        $followingLines = @($targetLines | Where-Object { $_.Relation -eq 'following' })
        $recognizedLineCount =
            $exactLines.Count + $precedingLines.Count + $followingLines.Count
        if ($recognizedLineCount -ne $targetLines.Count) {
            throw "Native source target '$($target.Id)' returned an unknown relation."
        }

        $status = 'unmapped'
        if (
            $exactLines.Count -eq 1 -and
            $precedingLines.Count -eq 0 -and
            $followingLines.Count -eq 0
        ) {
            $status = 'exact'
        }
        elseif (
            $exactLines.Count -eq 0 -and
            $precedingLines.Count -eq 1 -and
            $followingLines.Count -eq 1
        ) {
            $status = 'bracket'
        }
        elseif ($targetLines.Count -gt 0) {
            $status = 'partial'
        }

        if ($status -eq 'exact' -or $status -eq 'bracket') {
            [void]$resolvedRequiredLogicalTargetIds.Add($target.LogicalId)
        }

        if ($targetLines.Count -eq 0) {
            $evidence.Add(
                "target=$($target.Id) kind=$($target.Kind) symbol=$($target.Symbol) " +
                "status=$status requestedLine=$($target.RequestedLine) " +
                'mappedAddressCount=0'
            )
            continue
        }
        foreach ($line in $targetLines) {
            $lineDelta = [int64]$line.SelectedLine - [int64]$target.RequestedLine
            $selectedAddresses.Add(
                [pscustomobject]@{
                    Target                 = $target.Id
                    Kind                   = $target.Kind
                    Symbol                 = $target.Symbol
                    Status                 = $status
                    Relation               = $line.Relation
                    RequestedLine          = $target.RequestedLine
                    SelectedLine           = $line.SelectedLine
                    LineDelta              = $lineDelta
                    Address                = [uint64]$line.Address
                    ExtentEndAddress       = [uint64]$line.ExtentEndAddress
                    NativeSymbolName       = "$($line.NativeSymbolName)"
                    NativeSymbolAddress    = [uint64]$line.NativeSymbolAddress
                    NativeSymbolEndAddress = [uint64]$line.NativeSymbolEndAddress
                    RelativeVirtualAddress = [uint64]$line.RelativeVirtualAddress
                }
            )
            $evidence.Add(
                (('target={0} kind={1} symbol={2} status={3} relation={4} ' +
                    'requestedLine={5} selectedLine={6} lineDelta={7} ' +
                    'address=0x{8:X16} extentEndAddress=0x{9:X16} ' +
                    'nativeSymbol={10} nativeSymbolAddress=0x{11:X16} ' +
                    'nativeSymbolEndAddress=0x{12:X16} rva=0x{13:X8}') -f
                    $target.Id,
                    $target.Kind,
                    $target.Symbol,
                    $status,
                    $line.Relation,
                    $target.RequestedLine,
                    $line.SelectedLine,
                    $lineDelta,
                    [uint64]$line.Address,
                    [uint64]$line.ExtentEndAddress,
                    "$($line.NativeSymbolName)",
                    [uint64]$line.NativeSymbolAddress,
                    [uint64]$line.NativeSymbolEndAddress,
                    [uint64]$line.RelativeVirtualAddress)
            )
        }
    }

    $uniqueAddressCount = @(
        $selectedAddresses | Select-Object -ExpandProperty Address -Unique
    ).Count
    $missingRequiredLogicalTargets = @(
        $requiredLogicalTargetIds |
            Where-Object { !$resolvedRequiredLogicalTargetIds.Contains($_) }
    )
    if ($missingRequiredLogicalTargets.Count -gt 0) {
        throw (
            'Required native source targets were unresolved in every definition occurrence: ' +
            ($missingRequiredLogicalTargets -join ', ')
        )
    }
    $requiredTargetsResolved =
        $resolvedRequiredLogicalTargetIds.Count -eq $requiredLogicalTargetIds.Count
    $summary = [System.Collections.Generic.List[string]]::new()
    $summary.Add("gameAssembly=$ImagePath")
    $summary.Add("matchedGameAssemblyPdb=$([System.IO.Path]::GetFullPath($PdbPath))")
    $summary.Add("dbgHelpPath=$($readResult.DbgHelpPath)")
    $summary.Add("dbgHelpVersion=$($readResult.DbgHelpVersion)")
    $summary.Add(('moduleBase=0x{0:X16}' -f [uint64]$readResult.ModuleBase))
    $summary.Add("targetCount=$($targets.Count)")
    $summary.Add("requiredTargetCount=$($requiredLogicalTargetIds.Count)")
    $summary.Add("resolvedRequiredTargetCount=$($resolvedRequiredLogicalTargetIds.Count)")
    $summary.Add("requiredTargetsResolved=$requiredTargetsResolved")
    $summary.Add("mappedLineRecordCount=$($lineRecords.Count)")
    $summary.Add("selectedAddressRecordCount=$($selectedAddresses.Count)")
    $summary.Add("uniqueAddressCount=$uniqueAddressCount")
    $summary.Add('')
    foreach ($line in $evidence) {
        $summary.Add($line)
    }

    return [pscustomobject]@{
        Evidence                    = @($summary)
        AddressRecords              = @($selectedAddresses)
        SourceLineCount             = $lineRecords.Count
        TargetCount                 = $targets.Count
        RequiredTargetCount         = $requiredLogicalTargetIds.Count
        ResolvedRequiredTargetCount = $resolvedRequiredLogicalTargetIds.Count
        UniqueAddressCount          = $uniqueAddressCount
    }
}

function Add-NativeLayoutInventory {
    param(
        [Parameter(Mandatory = $true)] [string]$ProjectRoot,
        [Parameter(Mandatory = $true)] [string]$ArtifactsRoot,
        [Parameter(Mandatory = $true)] [string[]]$Symbols,
        [Parameter(Mandatory = $true)] [object[]]$Methods,
        [object[]]$RequiredMethods,
        [object]$NativeInlineProbeMethod,
        [string]$NativeInlineProbeCallSymbol,
        [string]$NativeInlineProbeExpectedSymbolPrefix,
        [string[]]$DumpbinPaths,
        [scriptblock]$NativeLineReader
    )

    if ($null -eq $RequiredMethods) {
        $RequiredMethods = @(@($Methods)[0])
    }

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
    # Investigation (2026-08-24): Unity 6000.5 Windows Standalone emits
    # GameAssembly.pdb without a SymbolMap. Both layouts are valid; the PDB remains mandatory below.
    if ($symbolMaps.Count -gt 1) {
        throw "Expected at most one SymbolMap under $playerDir; found $($symbolMaps.Count)."
    }

    $inventory = [System.Collections.Generic.List[string]]::new()
    $inventory.Add("gameAssembly=$($gameAssemblies[0].FullName)")
    $inventory.Add("gameAssemblyBytes=$($gameAssemblies[0].Length)")
    $inventory.Add("symbolMapCount=$($symbolMaps.Count)")
    if ($symbolMaps.Count -eq 1) {
        $inventory.Add("symbolMap=$($symbolMaps[0].FullName)")
        $inventory.Add("symbolMapBytes=$($symbolMaps[0].Length)")
    }
    else {
        $inventory.Add('symbolMap=absent')
    }

    $pdbFiles = @(
        Get-ChildItem -LiteralPath $playerDir -File -Recurse -Filter '*.pdb' |
            Sort-Object -Property FullName
    )
    $gameAssemblyPdbFiles = @(
        $pdbFiles |
            Where-Object {
                [string]::Equals(
                    $_.Name,
                    'GameAssembly.pdb',
                    [System.StringComparison]::OrdinalIgnoreCase
                )
            }
    )
    if ($gameAssemblyPdbFiles.Count -ne 1) {
        throw "Expected one GameAssembly.pdb under $playerDir; found $($gameAssemblyPdbFiles.Count)."
    }
    if ($gameAssemblyPdbFiles[0].Length -le 0) {
        throw "Expected a non-empty GameAssembly.pdb at $($gameAssemblyPdbFiles[0].FullName)."
    }
    $inventory.Add("pdbFileCount=$($pdbFiles.Count)")
    $inventory.Add("gameAssemblyPdb=$($gameAssemblyPdbFiles[0].FullName)")
    $inventory.Add("gameAssemblyPdbBytes=$($gameAssemblyPdbFiles[0].Length)")
    foreach ($pdbFile in $pdbFiles) {
        $inventory.Add("pdb=$($pdbFile.FullName) bytes=$($pdbFile.Length)")
    }

    $inventory.Add('')
    $inventory.Add('symbolMapHead:')
    if ($symbolMaps.Count -eq 1) {
        foreach ($line in @(Get-Content -LiteralPath $symbolMaps[0].FullName -TotalCount 12)) {
            $inventory.Add($line)
        }
    }
    else {
        $inventory.Add('(absent; use GameAssembly.pdb for native symbols)')
    }
    $inventory.Add('')
    $inventory.Add('symbolMapMatches:')
    $symbolMatches = @(
        if ($symbolMaps.Count -eq 1) {
            Select-String -LiteralPath $symbolMaps[0].FullName -SimpleMatch -Pattern $Symbols
        }
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

    $pdbPathOutput = @(
        & $selectedDumpbin.FullName /pdbpath:verbose $gameAssemblies[0].FullName 2>&1
    )
    $pdbPathSucceeded = $?
    $pdbPathExitCodeVariable = Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue
    $pdbPathExitCode =
        if ($null -ne $pdbPathExitCodeVariable) {
            $pdbPathExitCodeVariable.Value
        }
        elseif ($pdbPathSucceeded) {
            0
        }
        else {
            1
        }
    if (!$pdbPathSucceeded -or $pdbPathExitCode -ne 0) {
        throw "dumpbin /pdbpath:verbose exited $pdbPathExitCode for $($gameAssemblies[0].FullName)."
    }
    $matchedPdbPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $pdbPathOutput) {
        $pdbMatch = [regex]::Match(
            "$line",
            'PDB file found at [''"](?<path>.+?\.pdb)[''"]',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
        )
        if (
            $pdbMatch.Success -and
            [string]::Equals(
                [System.IO.Path]::GetFileName($pdbMatch.Groups['path'].Value),
                'GameAssembly.pdb',
                [System.StringComparison]::OrdinalIgnoreCase
            )
        ) {
            $matchedPdbPaths.Add($pdbMatch.Groups['path'].Value)
        }
    }
    if ($matchedPdbPaths.Count -ne 1) {
        throw 'dumpbin /pdbpath:verbose did not report a matching GameAssembly.pdb.'
    }
    $matchedPdb = Get-Item -LiteralPath $matchedPdbPaths[0] -ErrorAction SilentlyContinue
    if ($null -eq $matchedPdb -or $matchedPdb.Length -le 0) {
        throw "dumpbin reported a missing or empty matching PDB at $($matchedPdbPaths[0])."
    }
    $projectRootWithSeparator = [System.IO.Path]::GetFullPath($ProjectRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    $matchedPdbFullName = [System.IO.Path]::GetFullPath($matchedPdb.FullName)
    if (
        !$matchedPdbFullName.StartsWith(
            $projectRootWithSeparator,
            [System.StringComparison]::OrdinalIgnoreCase
        )
    ) {
        throw "dumpbin loaded a matching PDB outside the project root: $matchedPdbFullName."
    }
    $inventory.Add('')
    $inventory.Add('gameAssemblyPdbPath:')
    $inventory.Add("matchedGameAssemblyPdb=$matchedPdbFullName")
    $inventory.Add("matchedGameAssemblyPdbBytes=$($matchedPdb.Length)")
    foreach ($line in $pdbPathOutput) {
        $inventory.Add("$line")
    }

    $nativeLineMap = Get-NativeSourceLineMap `
        -ImagePath $gameAssemblies[0].FullName `
        -PdbPath $matchedPdbFullName `
        -Methods $Methods `
        -Symbols $Symbols `
        -RequiredMethods $RequiredMethods `
        -LineReader $NativeLineReader
    $nativeLineMapPath = Join-Path $ArtifactsRoot 'native-line-map.txt'
    $nativeLineMap.Evidence | Set-Content -LiteralPath $nativeLineMapPath -Encoding utf8
    $inventory.Add('')
    $inventory.Add('nativeSourceLineMap:')
    $inventory.Add("mappedLineRecordCount=$($nativeLineMap.SourceLineCount)")
    $inventory.Add("nativeTargetCount=$($nativeLineMap.TargetCount)")
    $inventory.Add("requiredNativeTargetCount=$($nativeLineMap.RequiredTargetCount)")
    $inventory.Add("selectedAddressRecordCount=$($nativeLineMap.AddressRecords.Count)")
    $inventory.Add("uniqueNativeAddressCount=$($nativeLineMap.UniqueAddressCount)")
    $inventory.Add("nativeLineMap=$nativeLineMapPath")
    Write-Host (
        "Mapped $($nativeLineMap.SourceLineCount) generated source-line records " +
        "to $nativeLineMapPath."
    )

    $outputPath = Join-Path $ArtifactsRoot 'native-layout-inventory.txt'
    $inventory | Set-Content -LiteralPath $outputPath -Encoding utf8
    Write-Host "Captured native layout inventory to $outputPath."

    $addressTargetsByHex = [System.Collections.Generic.Dictionary[
        string,
        System.Collections.Generic.List[string]
    ]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($addressGroup in @($nativeLineMap.AddressRecords | Group-Object -Property Address)) {
        $address = [uint64]$addressGroup.Name
        $addressHex = '{0:X16}' -f $address
        $targets = [System.Collections.Generic.List[string]]::new()
        foreach ($addressRecord in $addressGroup.Group) {
            if (!$targets.Contains($addressRecord.Target)) {
                $targets.Add($addressRecord.Target)
            }
        }
        $addressTargetsByHex.Add($addressHex, $targets)
    }
    if ($addressTargetsByHex.Count -eq 0) {
        throw 'Native source-line mapping selected no addresses for disassembly.'
    }
    $nativeInlineProbeAddress = $null
    if ($null -ne $NativeInlineProbeMethod) {
        if ([string]::IsNullOrWhiteSpace($NativeInlineProbeCallSymbol)) {
            throw 'A native inline probe call symbol is required with its method.'
        }
        if ([string]::IsNullOrWhiteSpace($NativeInlineProbeExpectedSymbolPrefix)) {
            throw 'An expected containing native symbol prefix is required with the probe.'
        }
        $nativeInlineProbeTargetPrefix = "$($NativeInlineProbeMethod.Label)@"
        $nativeInlineProbeRecords = @(
            $nativeLineMap.AddressRecords |
                Where-Object {
                    $_.Target.StartsWith(
                        $nativeInlineProbeTargetPrefix,
                        [System.StringComparison]::Ordinal
                    ) -and
                    [string]::Equals(
                        $_.Symbol,
                        $NativeInlineProbeCallSymbol,
                        [System.StringComparison]::Ordinal
                    )
                }
        )
        if (
            $nativeInlineProbeRecords.Count -ne 1 -or
            $nativeInlineProbeRecords[0].Status -ne 'exact'
        ) {
            throw (
                "Expected one exact native inline probe address for " +
                "'$($NativeInlineProbeMethod.Label)' calling " +
                "'$NativeInlineProbeCallSymbol'; found $($nativeInlineProbeRecords.Count)."
            )
        }
        $nativeInlineProbeAddress = [uint64]$nativeInlineProbeRecords[0].Address
        $nativeInlineProbeExtentEndAddress =
            [uint64]$nativeInlineProbeRecords[0].ExtentEndAddress
        $nativeInlineProbeSymbolName = "$($nativeInlineProbeRecords[0].NativeSymbolName)"
        $nativeInlineProbeSymbolAddress =
            [uint64]$nativeInlineProbeRecords[0].NativeSymbolAddress
        $nativeInlineProbeSymbolEndAddress =
            [uint64]$nativeInlineProbeRecords[0].NativeSymbolEndAddress
        if (
            !$nativeInlineProbeSymbolName.StartsWith(
                $NativeInlineProbeExpectedSymbolPrefix,
                [System.StringComparison]::Ordinal
            )
        ) {
            throw (
                "Native inline probe resolved containing symbol " +
                "'$nativeInlineProbeSymbolName'; expected prefix " +
                "'$NativeInlineProbeExpectedSymbolPrefix'."
            )
        }
    }
    $maximumAddressMatchRecords = $addressTargetsByHex.Count
    $capturedNativeAddressMatches = [System.Collections.Generic.List[object]]::new()
    $nativeInlineProbeEvidence = $null
    $disassemblyLineCount = 0
    $nativeAddressLineMatchCount = 0
    foreach ($addressHex in @($addressTargetsByHex.Keys | Sort-Object)) {
        $address = [uint64]::Parse(
            $addressHex,
            [System.Globalization.NumberStyles]::HexNumber
        )
        $rangeStart = $address
        $rangeEnd = $address + 0x400
        if ($null -ne $nativeInlineProbeAddress -and $address -eq $nativeInlineProbeAddress) {
            $rangeEnd = [Math]::Min(
                $rangeEnd,
                $nativeInlineProbeSymbolEndAddress - 1
            )
        }
        $rangeArgument = '/range:0x{0:X16},0x{1:X16}' -f $rangeStart, $rangeEnd
        $rangeExitCode = $null
        $nativeCommand = @{
            FilePath  = $selectedDumpbin.FullName
            Arguments = @(
                '/disasm:nobytes',
                $rangeArgument,
                $gameAssemblies[0].FullName
            )
            ExitCode  = [ref]$rangeExitCode
        }
        $rangeOutput = @(Invoke-NativeCommandOutput @nativeCommand)
        if ($null -eq $rangeExitCode) {
            throw (
                "dumpbin /disasm:nobytes $rangeArgument did not report an exit code for " +
                "$($gameAssemblies[0].FullName)."
            )
        }
        if ($rangeExitCode -ne 0) {
            throw (
                "dumpbin /disasm:nobytes exited $rangeExitCode for " +
                "$($gameAssemblies[0].FullName) in $rangeArgument."
            )
        }
        if ($rangeOutput.Count -eq 0) {
            throw (
                "dumpbin wrote no native disassembly for $($gameAssemblies[0].FullName) " +
                "in $rangeArgument."
            )
        }
        if ($null -ne $nativeInlineProbeAddress -and $address -eq $nativeInlineProbeAddress) {
            $nativeCallPrefix = '(?im)^[ \t]*[0-9A-F]+:[ \t]+call[ \t]+.*'
            $ensureFlatNativeCallCount = [regex]::Matches(
                $rangeOutput -join "`n",
                $nativeCallPrefix +
                    '(?<![A-Za-z0-9_])InterceptorCache_1_EnsureFlat_m[0-9A-F]+' +
                    '(?:_gshared)?(?:_inline)?(?![A-Za-z0-9_])'
            ).Count
            $rebuildFlatNativeCallCount = [regex]::Matches(
                $rangeOutput -join "`n",
                $nativeCallPrefix +
                    '(?<![A-Za-z0-9_])InterceptorCache_1_RebuildFlat_m[0-9A-F]+' +
                    '(?:_gshared)?(?:_inline)?(?![A-Za-z0-9_])'
            ).Count
            if (($ensureFlatNativeCallCount + $rebuildFlatNativeCallCount) -ne 1) {
                throw (
                    "Native inline probe for '$($NativeInlineProbeMethod.Label)' found " +
                    "$ensureFlatNativeCallCount EnsureFlat calls and " +
                    "$rebuildFlatNativeCallCount RebuildFlat calls; expected exactly one."
                )
            }
            $nativeInlineProbeEvidence = [pscustomobject]@{
                Address              = $address
                EnsureFlatCallCount  = $ensureFlatNativeCallCount
                RebuildFlatCallCount = $rebuildFlatNativeCallCount
            }
        }
        $disassemblyLineCount += $rangeOutput.Count
        $addressPattern =
            '^[ \t]*(?i:{0}):' -f [regex]::Escape($addressHex)
        $addressMatches = @(
            $rangeOutput |
                Select-String -Pattern $addressPattern -CaseSensitive -Context 2, 80
        )
        if ($addressMatches.Count -ne 1) {
            throw (
                "Native source address 0x$addressHex matched $($addressMatches.Count) " +
                "disassembly lines; expected exactly one in $rangeArgument."
            )
        }
        $nativeAddressLineMatchCount++
        $capturedNativeAddressMatches.Add(
            [pscustomobject]@{
                Match = $addressMatches[0]
                Label = $addressTargetsByHex[$addressHex] -join ','
                Range = $rangeArgument
            }
        )
    }


    $nativeEvidence = [System.Collections.Generic.List[string]]::new()
    $nativeEvidence.Add("gameAssembly=$($gameAssemblies[0].FullName)")
    $nativeEvidence.Add("matchedGameAssemblyPdb=$matchedPdbFullName")
    $nativeEvidence.Add("selectedDumpbin=$($selectedDumpbin.FullName)")
    $nativeEvidence.Add("disassemblyLineCount=$disassemblyLineCount")
    $nativeEvidence.Add("rangeInvocationCount=$($addressTargetsByHex.Count)")
    $nativeEvidence.Add('maximumRangeBytesAfterAddress=1024')
    $nativeEvidence.Add("nativeAddressLineMatchCount=$nativeAddressLineMatchCount")
    $nativeEvidence.Add("capturedMatchRecordCount=$($capturedNativeAddressMatches.Count)")
    $nativeEvidence.Add(
        "capturedAddressMatchRecordCount=$($capturedNativeAddressMatches.Count)"
    )
    $nativeEvidence.Add("maximumAddressMatchRecords=$maximumAddressMatchRecords")
    if ($null -ne $nativeInlineProbeAddress) {
        if ($null -eq $nativeInlineProbeEvidence) {
            throw 'Native inline probe address was not disassembled.'
        }
        $nativeEvidence.Add("nativeInlineProbeMethod=$($NativeInlineProbeMethod.Label)")
        $nativeEvidence.Add(
            'nativeInlineProbeAddress=0x{0:X16}' -f $nativeInlineProbeEvidence.Address
        )
        $nativeEvidence.Add(
            'nativeInlineProbeExtentEndAddress=0x{0:X16}' -f
                $nativeInlineProbeExtentEndAddress
        )
        $nativeEvidence.Add("nativeInlineProbeSymbol=$nativeInlineProbeSymbolName")
        $nativeEvidence.Add(
            'nativeInlineProbeSymbolAddress=0x{0:X16}' -f
                $nativeInlineProbeSymbolAddress
        )
        $nativeEvidence.Add(
            'nativeInlineProbeSymbolEndAddress=0x{0:X16}' -f
                $nativeInlineProbeSymbolEndAddress
        )
        $nativeEvidence.Add(
            "ensureFlatNativeCallCount=$($nativeInlineProbeEvidence.EnsureFlatCallCount)"
        )
        $nativeEvidence.Add(
            "rebuildFlatNativeCallCount=$($nativeInlineProbeEvidence.RebuildFlatCallCount)"
        )
    }
    foreach ($addressHex in @($addressTargetsByHex.Keys | Sort-Object)) {
        $nativeEvidence.Add(
            "address=0x$addressHex " +
            "addressTargets=$($addressTargetsByHex[$addressHex] -join ',') " +
            'lineMatchCount=1'
        )
    }
    foreach ($capturedMatch in $capturedNativeAddressMatches) {
        $match = $capturedMatch.Match
        $nativeEvidence.Add('')
        $nativeEvidence.Add(
            "matchKind=address matchLabel=$($capturedMatch.Label) " +
            "range=$($capturedMatch.Range) line=$($match.LineNumber) path=$($match.Path)"
        )
        foreach ($contextLine in @($match.Context.PreContext)) {
            $nativeEvidence.Add("  $contextLine")
        }
        $nativeEvidence.Add("> $($match.Line)")
        foreach ($contextLine in @($match.Context.PostContext)) {
            $nativeEvidence.Add("  $contextLine")
        }
    }
    $nativeDisassemblyPath = Join-Path $ArtifactsRoot 'native-disassembly.txt'
    $nativeEvidence | Set-Content -LiteralPath $nativeDisassemblyPath -Encoding utf8
    Write-Host (
        "Captured $nativeAddressLineMatchCount source-address matches " +
        "to $nativeDisassemblyPath."
    )
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
    Initialize-NativeSourceLineReader
    Write-Host 'Native source-line reader compile and ABI layout self-test passed.'

    $nativeProbeCommand = 'Write-Output "NativePipelineProbeSymbol"; exit 9'
    $nativeProbeEncodedCommand = [System.Convert]::ToBase64String(
        [System.Text.Encoding]::Unicode.GetBytes($nativeProbeCommand)
    )
    $nativeProbeExecutable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $nativeSuccessCommand = [System.Convert]::ToBase64String(
        [System.Text.Encoding]::Unicode.GetBytes('exit 0')
    )
    & $nativeProbeExecutable -NoLogo -NoProfile -EncodedCommand $nativeSuccessCommand
    if ($LASTEXITCODE -ne 0) {
        throw 'Native exit-code self-test could not establish the stale-success precondition.'
    }
    $nativeProbeExitCode = $null
    $nativeProbeMatches = @(
        Invoke-NativeCommandOutput `
            -FilePath $nativeProbeExecutable `
            -Arguments @('-NoLogo', '-NoProfile', '-EncodedCommand', $nativeProbeEncodedCommand) `
            -ExitCode ([ref]$nativeProbeExitCode) |
            Select-String `
                -SimpleMatch `
                -CaseSensitive `
                -Pattern 'NativePipelineProbeSymbol'
    )
    if ($nativeProbeExitCode -ne 9 -or $nativeProbeMatches.Count -ne 1) {
        throw (
            'Native exit-code self-test failed: expected exit 9 and one streamed match, ' +
            "found exit $nativeProbeExitCode and $($nativeProbeMatches.Count) matches."
        )
    }
    Write-Host 'Native exit-code capture self-test passed.'

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
            $sharedBody =
                if ($method[0] -eq 'RunUntargetedInterceptors') {
                    @('InterceptorCache_1_EnsureFlat_mC006_gshared_inline();')
                }
                else {
                    @("// $($method[0]) shared body")
                }
            Add-IntegrationMethod $sharedName $sharedBody
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
                'untargetedInterceptorEnsureFlatCallCount=1',
                'untargetedBroadcastEnsureFlatCallCount=0',
                'untargetedSteadyStateEnsureFlatCallCount=1',
                'untargetedInterceptorEnsureFlatCallSymbol=InterceptorCache_1_EnsureFlat_mC006_gshared_inline',
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

        $integrationSource = [System.IO.File]::ReadAllText($integrationCppPath)
        $ensureFlatCall = 'InterceptorCache_1_EnsureFlat_mC006_gshared_inline();'
        if (
            [regex]::Matches(
                $integrationSource,
                [regex]::Escape($ensureFlatCall)
            ).Count -ne 1
        ) {
            throw 'Dispatch codegen integration fixture must contain one EnsureFlat call.'
        }
        $borrowedIntegrationArtifacts = Join-Path $integrationTestRoot 'borrowed-artifacts'
        New-Item -ItemType Directory -Path $borrowedIntegrationArtifacts | Out-Null
        try {
            [System.IO.File]::WriteAllText(
                $integrationCppPath,
                $integrationSource.Replace($ensureFlatCall, '// borrowed interceptor view')
            )
            & $PSCommandPath `
                -ProjectPath $integrationTestRoot `
                -ArtifactsPath $borrowedIntegrationArtifacts `
                -SkipNativeInventory
            if (!$?) {
                throw 'Borrowed-view dispatch codegen integration child invocation failed.'
            }
            $borrowedEvidence = Get-Content `
                -LiteralPath (
                    Join-Path $borrowedIntegrationArtifacts 'untargeted-hook-codegen.txt'
                ) `
                -Raw
            foreach (
                $expectedEvidence in @(
                    'untargetedInterceptorEnsureFlatCallCount=0',
                    'untargetedBroadcastEnsureFlatCallCount=0',
                    'untargetedSteadyStateEnsureFlatCallCount=0',
                    'untargetedInterceptorEnsureFlatCallSymbol=absent'
                )
            ) {
                if (!$borrowedEvidence.Contains($expectedEvidence)) {
                    throw "Borrowed-view integration evidence omitted '$expectedEvidence'."
                }
            }

            $outerInterceptorCall =
                'MessageBus_RunUntargetedInterceptors_TisSimpleUntargetedMessage_tBBBB_mB002();'
            if (
                [regex]::Matches(
                    $integrationSource,
                    [regex]::Escape($outerInterceptorCall)
                ).Count -ne 1
            ) {
                throw 'Dispatch codegen integration fixture must contain one outer helper call.'
            }
            $relocatedIntegrationArtifacts = Join-Path `
                $integrationTestRoot `
                'relocated-artifacts'
            New-Item -ItemType Directory -Path $relocatedIntegrationArtifacts | Out-Null
            $relocatedSource = $integrationSource.Replace(
                $ensureFlatCall,
                '// EnsureFlat moved out of the helper'
            ).Replace(
                $outerInterceptorCall,
                "$ensureFlatCall`n$outerInterceptorCall"
            )
            [System.IO.File]::WriteAllText($integrationCppPath, $relocatedSource)
            & $PSCommandPath `
                -ProjectPath $integrationTestRoot `
                -ArtifactsPath $relocatedIntegrationArtifacts `
                -SkipNativeInventory
            if (!$?) {
                throw 'Relocated-call dispatch codegen integration child invocation failed.'
            }
            $relocatedEvidence = Get-Content `
                -LiteralPath (
                    Join-Path $relocatedIntegrationArtifacts 'untargeted-hook-codegen.txt'
                ) `
                -Raw
            foreach (
                $expectedEvidence in @(
                    'untargetedInterceptorEnsureFlatCallCount=0',
                    'untargetedBroadcastEnsureFlatCallCount=1',
                    'untargetedSteadyStateEnsureFlatCallCount=1',
                    'untargetedInterceptorEnsureFlatCallSymbol=InterceptorCache_1_EnsureFlat_mC006_gshared_inline'
                )
            ) {
                if (!$relocatedEvidence.Contains($expectedEvidence)) {
                    throw "Relocated-call integration evidence omitted '$expectedEvidence'."
                }
            }
        }
        finally {
            [System.IO.File]::WriteAllText($integrationCppPath, $integrationSource)
        }

        $playerDir = Join-Path $integrationTestRoot 'Build\DxmTestPlayer'
        $backupDir = Join-Path $playerDir 'DxmTestPlayer_BackUpThisFolder_ButDontShipItWithYourGame'
        New-Item -ItemType Directory -Path $backupDir | Out-Null
        'fake native image' | Set-Content -LiteralPath (Join-Path $playerDir 'GameAssembly.dll')
        $primarySymbolMapPath = Join-Path $backupDir 'SymbolMap'
        @(
            '0000000000001000 16 OtherSymbol',
            '0000000000002000 32 InventoryProbeSymbol'
        ) | Set-Content -LiteralPath $primarySymbolMapPath
        $gameAssemblyPdbPath = Join-Path $backupDir 'GameAssembly.pdb'
        'fake pdb' | Set-Content -LiteralPath $gameAssemblyPdbPath
        'fake object' | Set-Content -LiteralPath (Join-Path $integrationCppRoot 'gEnErAtEd.cpp.obj')
        $nativeProbeMethod = [pscustomobject]@{
            Label               = 'Native source-line probe'
            Path                = $integrationCppPath
            LineNumber          = 1
            DefinitionLocations = @(
                [pscustomobject]@{
                    Path       = $integrationCppPath
                    LineNumber = 1
                },
                [pscustomobject]@{
                    Path       = (Join-Path $integrationCppRoot 'Duplicate.cpp')
                    LineNumber = 1
                }
            )
            Lines               = @(
                'inline void NativeSourceLineProbe ()',
                '{',
                'InventoryProbeSymbol();',
                '}'
            )
        }
        $nestedNativeProbeMethod = [pscustomobject]@{
            Label      = 'Nested native source-line probe'
            Path       = $integrationCppPath
            LineNumber = 10
            Lines      = @(
                'inline void NestedNativeSourceLineProbe ()',
                '{',
                'InterceptorCache_1_EnsureFlat_mC006_gshared_inline();',
                '}'
            )
        }
        $fakeNativeLineReader = {
            param($ImagePath, $PdbPath, $Targets)

            $lines = [System.Collections.Generic.List[object]]::new()
            $nextAddress = [uint64]0x180001000
            $targetIndex = 0
            foreach ($target in @($Targets)) {
                if (
                    $target.Symbol -eq
                    'InterceptorCache_1_EnsureFlat_mC006_gshared_inline'
                ) {
                    $lines.Add(
                        [pscustomobject]@{
                            Target                  = $target.Id
                            SourcePath              = $target.Path
                            Relation                = 'exact'
                            RequestedLine           = $target.RequestedLine
                            SelectedLine            = $target.RequestedLine
                            Address                 = $nextAddress
                            ExtentEndAddress        = $nextAddress + 0x10
                            NativeSymbolName        = 'SyntheticNestedNativeProbe'
                            NativeSymbolAddress     = [uint64]0x180001020
                            NativeSymbolEndAddress  = [uint64]0x180001080
                            RelativeVirtualAddress  = $nextAddress - [uint64]0x180000000
                        }
                    )
                    $nextAddress += 0x10
                }
                elseif ($targetIndex -eq 0) {
                    $lines.Add(
                        [pscustomobject]@{
                            Target                  = $target.Id
                            SourcePath              = $target.Path
                            Relation                = 'exact'
                            RequestedLine           = $target.RequestedLine
                            SelectedLine            = $target.RequestedLine
                            Address                 = $nextAddress
                            ExtentEndAddress        = $nextAddress + 0x10
                            NativeSymbolName        = 'SyntheticOuterNativeProbe'
                            NativeSymbolAddress     = [uint64]0x180001000
                            NativeSymbolEndAddress  = [uint64]0x180001020
                            RelativeVirtualAddress  = $nextAddress - [uint64]0x180000000
                        }
                    )
                }
                else {
                    foreach ($relation in @('preceding', 'following')) {
                        $selectedLine =
                            if ($relation -eq 'preceding') {
                                [uint32]($target.RequestedLine - 1)
                            }
                            else {
                                [uint32]($target.RequestedLine + 1)
                            }
                        $lines.Add(
                            [pscustomobject]@{
                                Target                  = $target.Id
                                SourcePath              = $target.Path
                                Relation                = $relation
                                RequestedLine           = $target.RequestedLine
                                SelectedLine            = $selectedLine
                                Address                 = $nextAddress
                                ExtentEndAddress        = $nextAddress + 0x10
                                NativeSymbolName        = 'SyntheticOuterNativeProbe'
                                NativeSymbolAddress     = [uint64]0x180001000
                                NativeSymbolEndAddress  = [uint64]0x180001020
                                RelativeVirtualAddress  = (
                                    $nextAddress - [uint64]0x180000000
                                )
                            }
                        )
                        $nextAddress += 0x10
                    }
                }
                $targetIndex++
            }
            [pscustomobject]@{
                DbgHelpPath    = 'synthetic-dbghelp.dll'
                DbgHelpVersion = '1.2.3.4'
                ModuleBase     = [uint64]0x180000000
                Lines          = @($lines)
            }
        }

        $fakeDumpbin = Join-Path $integrationTestRoot 'fake-dumpbin.ps1'
        $failedDumpbin = Join-Path $integrationTestRoot 'failed-dumpbin.ps1'
        $throwingDumpbin = Join-Path $integrationTestRoot 'throwing-dumpbin.ps1'
        'exit 7' | Set-Content -LiteralPath $failedDumpbin -Encoding utf8
        'throw "synthetic dumpbin launch failure"' |
            Set-Content -LiteralPath $throwingDumpbin -Encoding utf8
        @(
            'param([Parameter(ValueFromRemainingArguments = $true)] [string[]]$CommandArguments)',
            '$image = $CommandArguments[-1]',
            'if (!(Test-Path -LiteralPath $image)) { exit 1 }',
            '$option = $CommandArguments[0]',
            '$behavior = [System.IO.Path]::GetFileNameWithoutExtension($PSCommandPath)',
            'if ($option -eq "/headers") { Write-Output "PE header proof"; exit 0 }',
            'if ($option -eq "/pdbpath:verbose") {',
            '    if ($behavior -eq "failed-pdb-dumpbin") { exit 8 }',
            '    if ($behavior -eq "prefix-collision-pdb-dumpbin") {',
            '        $prefixPdb = Join-Path (Split-Path -Parent $image) "NotGameAssembly.pdb"',
            '        Set-Content -LiteralPath $prefixPdb -Value "fake prefix pdb"',
            '        Write-Output "PDB file found at ''$prefixPdb''"',
            '        exit 0',
            '    }',
            '    if ($behavior -eq "no-matching-pdb-dumpbin") {',
            '        Write-Output "PDB file ''C:\synthetic\GameAssembly.pdb'' checked. (File not found)"',
            '        exit 0',
            '    }',
            '    $pdb = @(Get-ChildItem -LiteralPath (Split-Path -Parent $image) -File -Recurse -Filter "GameAssembly.pdb")',
            '    if ($pdb.Count -ne 1) { exit 1 }',
            '    Write-Output "PDB file found at ''$($pdb[0].FullName)''"',
            '    exit 0',
            '}',
            'if ($option -eq "/disasm:nobytes") {',
            '    if ($CommandArguments.Count -ne 3) { exit 10 }',
            '    $rangeMatch = [regex]::Match($CommandArguments[1], "^/range:0x(?<min>[0-9A-F]{16}),0x(?<max>[0-9A-F]{16})$")',
            '    if (!$rangeMatch.Success) { exit 10 }',
            '    $rangeStart = [Convert]::ToUInt64($rangeMatch.Groups["min"].Value, 16)',
            '    $rangeEnd = [Convert]::ToUInt64($rangeMatch.Groups["max"].Value, 16)',
            '    if ($rangeEnd -lt $rangeStart -or $rangeEnd -gt $rangeStart + 0x400) { exit 10 }',
            '    if ($behavior -eq "failed-disassembly-dumpbin") { exit 9 }',
            '    if ($behavior -eq "empty-disassembly-dumpbin") {',
            '        exit 0',
            '    }',
            '    if ($behavior -eq "missing-address-dumpbin") {',
            '        Write-Output "InventoryProbeSymbol:"',
            '        $address = [uint64]0x180001000',
            '        if ($address -ge $rangeStart -and $address -le $rangeEnd) {',
            '            Write-Output ("{0:X16}:" -f $address)',
            '        }',
            '        exit 0',
            '    }',
            '    function Write-SyntheticAddress {',
            '        param([uint64]$Address, [string]$Instruction)',
            '        if ($Address -ge $rangeStart -and $Address -le $rangeEnd) {',
            '            Write-Output ("{0:X16}: $Instruction" -f $Address)',
            '        }',
            '    }',
            '    Write-Output "Synthetic disassembly"',
            '    Write-Output "InventoryProbeSymbol:"',
            '    Write-SyntheticAddress ([uint64]0x180001000) "mov eax, 3"',
            '    Write-SyntheticAddress ([uint64]0x180001010) "mov eax, 4"',
            '    if ($behavior -eq "rebuild-flat-dumpbin") {',
            '        Write-SyntheticAddress ([uint64]0x180001020) "mov rcx, rax"',
            '        Write-SyntheticAddress ([uint64]0x180001040) "call InterceptorCache_1_RebuildFlat_mD007_gshared"',
            '    }',
            '    elseif ($behavior -eq "missing-inline-proof-dumpbin") {',
            '        Write-SyntheticAddress ([uint64]0x180001020) "call synthetic_helper"',
            '    }',
            '    elseif ($behavior -eq "adjacent-inline-proof-dumpbin") {',
            '        Write-SyntheticAddress ([uint64]0x180001020) "call synthetic_helper"',
            '        Write-SyntheticAddress ([uint64]0x180001080) "call InterceptorCache_1_EnsureFlat_mC006_gshared_inline"',
            '    }',
            '    else {',
            '        Write-SyntheticAddress ([uint64]0x180001020) "call InterceptorCache_1_EnsureFlat_mC006_gshared_inline"',
            '    }',
            '    if ($behavior -eq "ambiguous-inline-proof-dumpbin") {',
            '        Write-SyntheticAddress ([uint64]0x180001040) "call InterceptorCache_1_RebuildFlat_mD007_gshared"',
            '    }',
            '    exit 0',
            '}',
            'exit 0'
        ) | Set-Content -LiteralPath $fakeDumpbin -Encoding utf8
        Add-NativeLayoutInventory `
            -ProjectRoot $integrationTestRoot `
            -ArtifactsRoot $integrationArtifacts `
            -Symbols @(
                'InventoryProbeSymbol',
                'InventoryProbeSymbol',
                'InterceptorCache_1_EnsureFlat_mC006_gshared_inline'
            ) `
            -Methods @($nativeProbeMethod, $nestedNativeProbeMethod) `
            -RequiredMethods @($nativeProbeMethod, $nestedNativeProbeMethod) `
            -NativeInlineProbeMethod $nestedNativeProbeMethod `
            -NativeInlineProbeCallSymbol `
                'InterceptorCache_1_EnsureFlat_mC006_gshared_inline' `
            -NativeInlineProbeExpectedSymbolPrefix 'SyntheticNestedNativeProbe' `
            -DumpbinPaths @($failedDumpbin, $throwingDumpbin, $fakeDumpbin) `
            -NativeLineReader $fakeNativeLineReader
        $nativeInventory = Get-Content `
            -LiteralPath (Join-Path $integrationArtifacts 'native-layout-inventory.txt') `
            -Raw
        foreach (
            $expectedInventoryEvidence in @(
                'symbolMapCount=1',
                'symbolMapMatchCount=1',
                'InventoryProbeSymbol',
                'pdbFileCount=1',
                'gameAssemblyPdb=',
                'matchingObjectFileCount=1',
                'exitCode=7',
                'dumpbinFailure=',
                'selectedDumpbin=',
                'PE header proof',
                'gameAssemblyPdbPath:',
                'nativeSourceLineMap:',
                'mappedLineRecordCount=4',
                'nativeTargetCount=3',
                'requiredNativeTargetCount=2',
                'selectedAddressRecordCount=4',
                'uniqueNativeAddressCount=3'
            )
        ) {
            if (!$nativeInventory.Contains($expectedInventoryEvidence)) {
                throw "Native layout inventory omitted '$expectedInventoryEvidence'."
            }
        }
        $nativeDisassembly = Get-Content `
            -LiteralPath (Join-Path $integrationArtifacts 'native-disassembly.txt') `
            -Raw
        foreach (
            $expectedDisassemblyEvidence in @(
                'nativeAddressLineMatchCount=3',
                'rangeInvocationCount=3',
                'maximumRangeBytesAfterAddress=1024',
                'capturedMatchRecordCount=3',
                'capturedAddressMatchRecordCount=3',
                'maximumAddressMatchRecords=3',
                'address=0x0000000180001000',
                'address=0x0000000180001010',
                'address=0x0000000180001020',
                'nativeInlineProbeMethod=Nested native source-line probe',
                'nativeInlineProbeAddress=0x0000000180001020',
                'nativeInlineProbeExtentEndAddress=0x0000000180001030',
                'nativeInlineProbeSymbol=SyntheticNestedNativeProbe',
                'nativeInlineProbeSymbolAddress=0x0000000180001020',
                'nativeInlineProbeSymbolEndAddress=0x0000000180001080',
                'ensureFlatNativeCallCount=1',
                'rebuildFlatNativeCallCount=0',
                '> 0000000180001000:',
                '> 0000000180001010:',
                '> 0000000180001020:',
                'range=/range:0x0000000180001000,0x0000000180001400',
                'mov eax, 3'
            )
        ) {
            if (!$nativeDisassembly.Contains($expectedDisassemblyEvidence)) {
                throw "Native disassembly omitted '$expectedDisassemblyEvidence'."
            }
        }
        $sharedAddressEvidence = @(
            $nativeDisassembly -split '\r?\n' |
                Where-Object {
                    $_ -match '^address=0x0000000180001000 ' -and
                    $_ -match 'Generated\.cpp' -and
                    $_ -match 'Duplicate\.cpp'
                }
        )
        if ($sharedAddressEvidence.Count -ne 1) {
            throw 'Native disassembly did not deduplicate a VA shared by two source targets.'
        }
        $nativeLineMapEvidence = Get-Content `
            -LiteralPath (Join-Path $integrationArtifacts 'native-line-map.txt') `
            -Raw
        foreach (
            $expectedLineMapEvidence in @(
                'dbgHelpPath=synthetic-dbghelp.dll',
                'dbgHelpVersion=1.2.3.4',
                'moduleBase=0x0000000180000000',
                'targetCount=3',
                'requiredTargetCount=2',
                'resolvedRequiredTargetCount=2',
                'requiredTargetsResolved=True',
                'mappedLineRecordCount=4',
                'selectedAddressRecordCount=4',
                'uniqueAddressCount=3',
                'status=exact relation=exact requestedLine=3 selectedLine=3 lineDelta=0',
                'status=bracket relation=preceding requestedLine=3 selectedLine=2 lineDelta=-1',
                'status=bracket relation=following requestedLine=3 selectedLine=4 lineDelta=1'
            )
        ) {
            if (!$nativeLineMapEvidence.Contains($expectedLineMapEvidence)) {
                throw "Native source-line map omitted '$expectedLineMapEvidence'."
            }
        }

        function Assert-NativeInventoryFailure {
            param(
                [Parameter(Mandatory = $true)] [scriptblock]$Operation,
                [Parameter(Mandatory = $true)] [string]$ExpectedMessage
            )

            $failureMessage = $null
            try {
                & $Operation
            }
            catch {
                $failureMessage = $_.Exception.Message
            }
            if ([string]::IsNullOrWhiteSpace($failureMessage)) {
                throw "Native layout inventory unexpectedly accepted '$ExpectedMessage'."
            }
            if (!$failureMessage.Contains($ExpectedMessage)) {
                throw (
                    "Native layout inventory failure '$failureMessage' did not contain " +
                    "'$ExpectedMessage'."
                )
            }
        }

        $rebuildFlatDumpbin = Join-Path $integrationTestRoot 'rebuild-flat-dumpbin.ps1'
        Copy-Item -LiteralPath $fakeDumpbin -Destination $rebuildFlatDumpbin
        Add-NativeLayoutInventory `
            -ProjectRoot $integrationTestRoot `
            -ArtifactsRoot $integrationArtifacts `
            -Symbols @(
                'InventoryProbeSymbol',
                'InterceptorCache_1_EnsureFlat_mC006_gshared_inline'
            ) `
            -Methods @($nativeProbeMethod, $nestedNativeProbeMethod) `
            -RequiredMethods @($nativeProbeMethod, $nestedNativeProbeMethod) `
            -NativeInlineProbeMethod $nestedNativeProbeMethod `
            -NativeInlineProbeCallSymbol `
                'InterceptorCache_1_EnsureFlat_mC006_gshared_inline' `
            -NativeInlineProbeExpectedSymbolPrefix 'SyntheticNestedNativeProbe' `
            -DumpbinPaths @($rebuildFlatDumpbin) `
            -NativeLineReader $fakeNativeLineReader
        $rebuildFlatEvidence = Get-Content `
            -LiteralPath (Join-Path $integrationArtifacts 'native-disassembly.txt') `
            -Raw
        foreach (
            $expectedRebuildFlatEvidence in @(
                'ensureFlatNativeCallCount=0',
                'rebuildFlatNativeCallCount=1'
            )
        ) {
            if (!$rebuildFlatEvidence.Contains($expectedRebuildFlatEvidence)) {
                throw "Native rebuild evidence omitted '$expectedRebuildFlatEvidence'."
            }
        }

        foreach ($invalidInlineProofBehavior in @('missing', 'adjacent', 'ambiguous')) {
            $invalidInlineProofDumpbin = Join-Path `
                $integrationTestRoot `
                "$invalidInlineProofBehavior-inline-proof-dumpbin.ps1"
            Copy-Item -LiteralPath $fakeDumpbin -Destination $invalidInlineProofDumpbin
            Assert-NativeInventoryFailure `
                -ExpectedMessage 'expected exactly one' `
                -Operation {
                    Add-NativeLayoutInventory `
                        -ProjectRoot $integrationTestRoot `
                        -ArtifactsRoot $integrationArtifacts `
                        -Symbols @(
                            'InventoryProbeSymbol',
                            'InterceptorCache_1_EnsureFlat_mC006_gshared_inline'
                        ) `
                        -Methods @($nativeProbeMethod, $nestedNativeProbeMethod) `
                        -RequiredMethods @(
                            $nativeProbeMethod,
                            $nestedNativeProbeMethod
                        ) `
                        -NativeInlineProbeMethod $nestedNativeProbeMethod `
                        -NativeInlineProbeCallSymbol `
                            'InterceptorCache_1_EnsureFlat_mC006_gshared_inline' `
                        -NativeInlineProbeExpectedSymbolPrefix `
                            'SyntheticNestedNativeProbe' `
                        -DumpbinPaths @($invalidInlineProofDumpbin) `
                        -NativeLineReader $fakeNativeLineReader
                }
        }

        Remove-Item -LiteralPath $primarySymbolMapPath
        Add-NativeLayoutInventory `
            -ProjectRoot $integrationTestRoot `
            -ArtifactsRoot $integrationArtifacts `
            -Symbols @('InventoryProbeSymbol', 'InventoryProbeSymbol_gshared') `
            -Methods @($nativeProbeMethod) `
            -DumpbinPaths @($fakeDumpbin) `
            -NativeLineReader $fakeNativeLineReader
        $nativeInventoryWithoutSymbolMap = Get-Content `
            -LiteralPath (Join-Path $integrationArtifacts 'native-layout-inventory.txt') `
            -Raw
        foreach (
            $expectedNoSymbolMapEvidence in @(
                'symbolMapCount=0',
                'symbolMap=absent',
                'symbolMapMatchCount=0',
                'gameAssemblyPdb=',
                'PE header proof',
                'gameAssemblyPdbPath:'
            )
        ) {
            if (!$nativeInventoryWithoutSymbolMap.Contains($expectedNoSymbolMapEvidence)) {
                throw (
                    'Native layout inventory without SymbolMap omitted ' +
                    "'$expectedNoSymbolMapEvidence'."
                )
            }
        }

        $missingNestedNativeProbeMethod = [pscustomobject]@{
            Label      = 'Missing nested native source-line probe'
            Path       = $integrationCppPath
            LineNumber = 20
            Lines      = @(
                'inline void MissingNestedNativeSourceLineProbe ()',
                '{',
                'MissingNestedProbeSymbol();',
                '}'
            )
        }
        Assert-NativeInventoryFailure `
            -ExpectedMessage (
                "Required native method '$($missingNestedNativeProbeMethod.Label)' " +
                'had no captured call targets.'
            ) `
            -Operation {
                Get-NativeSourceLineMap `
                    -ImagePath (Join-Path $playerDir 'GameAssembly.dll') `
                    -PdbPath $gameAssemblyPdbPath `
                    -Methods @($nativeProbeMethod, $missingNestedNativeProbeMethod) `
                    -Symbols @('InventoryProbeSymbol') `
                    -RequiredMethods @(
                        $nativeProbeMethod,
                        $missingNestedNativeProbeMethod
                    ) `
                    -LineReader $fakeNativeLineReader
            }

        $singleDefinitionNativeLineReader = {
            param($ImagePath, $PdbPath, $Targets)

            $result = & $fakeNativeLineReader $ImagePath $PdbPath $Targets
            $result.Lines = @(
                $result.Lines |
                    Where-Object { $_.SourcePath -eq $integrationCppPath }
            )
            $result
        }
        $singleDefinitionMap = Get-NativeSourceLineMap `
            -ImagePath (Join-Path $playerDir 'GameAssembly.dll') `
            -PdbPath $gameAssemblyPdbPath `
            -Methods @($nativeProbeMethod) `
            -Symbols @('InventoryProbeSymbol') `
            -RequiredMethods @($nativeProbeMethod) `
            -LineReader $singleDefinitionNativeLineReader
        if (
            $singleDefinitionMap.ResolvedRequiredTargetCount -ne 1 -or
            @(
                $singleDefinitionMap.Evidence |
                    Where-Object { $_ -match 'Duplicate\.cpp.*status=unmapped' }
            ).Count -ne 1
        ) {
            throw 'Native source-line map did not account for a discarded duplicate definition.'
        }

        $emptyNativeLineReader = {
            param($ImagePath, $PdbPath, $Targets)

            $result = & $fakeNativeLineReader $ImagePath $PdbPath $Targets
            $result.Lines = @()
            $result
        }
        Assert-NativeInventoryFailure `
            -ExpectedMessage 'Required native source targets were unresolved' `
            -Operation {
                Get-NativeSourceLineMap `
                    -ImagePath (Join-Path $playerDir 'GameAssembly.dll') `
                    -PdbPath $gameAssemblyPdbPath `
                    -Methods @($nativeProbeMethod) `
                    -Symbols @('InventoryProbeSymbol') `
                    -RequiredMethods @($nativeProbeMethod) `
                    -LineReader $emptyNativeLineReader
        }
        $partialNativeLineReader = {
            param($ImagePath, $PdbPath, $Targets)

            $result = & $fakeNativeLineReader $ImagePath $PdbPath $Targets
            $result.Lines = @(
                $result.Lines | Where-Object { $_.Relation -eq 'preceding' }
            )
            $result
        }
        Assert-NativeInventoryFailure `
            -ExpectedMessage 'Required native source targets were unresolved' `
            -Operation {
                Get-NativeSourceLineMap `
                    -ImagePath (Join-Path $playerDir 'GameAssembly.dll') `
                    -PdbPath $gameAssemblyPdbPath `
                    -Methods @($nativeProbeMethod) `
                    -Symbols @('InventoryProbeSymbol') `
                    -RequiredMethods @($nativeProbeMethod) `
                    -LineReader $partialNativeLineReader
        }
        $mislabeledNativeLineReader = {
            param($ImagePath, $PdbPath, $Targets)

            $result = & $fakeNativeLineReader $ImagePath $PdbPath $Targets
            $result.Lines[0].Relation = 'preceding'
            $result
        }
        Assert-NativeInventoryFailure `
            -ExpectedMessage 'returned an invalid line relation' `
            -Operation {
                Get-NativeSourceLineMap `
                    -ImagePath (Join-Path $playerDir 'GameAssembly.dll') `
                    -PdbPath $gameAssemblyPdbPath `
                    -Methods @($nativeProbeMethod) `
                    -Symbols @('InventoryProbeSymbol') `
                    -RequiredMethods @($nativeProbeMethod) `
                    -LineReader $mislabeledNativeLineReader
        }
        $outOfRangeNativeLineReader = {
            param($ImagePath, $PdbPath, $Targets)

            $result = & $fakeNativeLineReader $ImagePath $PdbPath $Targets
            $result.Lines[0].Relation = 'following'
            $result.Lines[0].SelectedLine = [uint32]5
            $result
        }
        Assert-NativeInventoryFailure `
            -ExpectedMessage 'returned an invalid line relation' `
            -Operation {
                Get-NativeSourceLineMap `
                    -ImagePath (Join-Path $playerDir 'GameAssembly.dll') `
                    -PdbPath $gameAssemblyPdbPath `
                    -Methods @($nativeProbeMethod) `
                    -Symbols @('InventoryProbeSymbol') `
                    -RequiredMethods @($nativeProbeMethod) `
                    -LineReader $outOfRangeNativeLineReader
        }
        $unboundedNativeLineReader = {
            param($ImagePath, $PdbPath, $Targets)

            $result = & $fakeNativeLineReader $ImagePath $PdbPath $Targets
            $result.Lines = @($result.Lines) + @($result.Lines)
            $result
        }
        Assert-NativeInventoryFailure `
            -ExpectedMessage 'maximum is two records per target' `
            -Operation {
                Get-NativeSourceLineMap `
                    -ImagePath (Join-Path $playerDir 'GameAssembly.dll') `
                    -PdbPath $gameAssemblyPdbPath `
                    -Methods @($nativeProbeMethod) `
                    -Symbols @('InventoryProbeSymbol') `
                    -RequiredMethods @($nativeProbeMethod) `
                    -LineReader $unboundedNativeLineReader
        }

        foreach (
            $nativeCommandFailure in @(
                [pscustomobject]@{
                    Name            = 'no-matching-pdb-dumpbin.ps1'
                    ExpectedMessage = 'did not report a matching GameAssembly.pdb'
                },
                [pscustomobject]@{
                    Name            = 'prefix-collision-pdb-dumpbin.ps1'
                    ExpectedMessage = 'did not report a matching GameAssembly.pdb'
                },
                [pscustomobject]@{
                    Name            = 'failed-pdb-dumpbin.ps1'
                    ExpectedMessage = 'dumpbin /pdbpath:verbose exited 8'
                },
                [pscustomobject]@{
                    Name            = 'failed-disassembly-dumpbin.ps1'
                    ExpectedMessage = 'dumpbin /disasm:nobytes exited 9'
                },
                [pscustomobject]@{
                    Name            = 'empty-disassembly-dumpbin.ps1'
                    ExpectedMessage = 'dumpbin wrote no native disassembly'
                },
                [pscustomobject]@{
                    Name            = 'missing-address-dumpbin.ps1'
                    ExpectedMessage = 'matched 0 disassembly lines; expected exactly one'
                }
            )
        ) {
            $failureDumpbin = Join-Path $integrationTestRoot $nativeCommandFailure.Name
            Copy-Item -LiteralPath $fakeDumpbin -Destination $failureDumpbin
            Assert-NativeInventoryFailure `
                -ExpectedMessage $nativeCommandFailure.ExpectedMessage `
                -Operation {
                    Add-NativeLayoutInventory `
                        -ProjectRoot $integrationTestRoot `
                        -ArtifactsRoot $integrationArtifacts `
                        -Symbols @('InventoryProbeSymbol') `
                        -Methods @($nativeProbeMethod) `
                        -DumpbinPaths @($failureDumpbin) `
                        -NativeLineReader $fakeNativeLineReader
                }
        }

        [System.IO.File]::WriteAllBytes($gameAssemblyPdbPath, [byte[]]@())
        Assert-NativeInventoryFailure `
            -ExpectedMessage 'Expected a non-empty GameAssembly.pdb' `
            -Operation {
                Add-NativeLayoutInventory `
                    -ProjectRoot $integrationTestRoot `
                    -ArtifactsRoot $integrationArtifacts `
                    -Symbols @('InventoryProbeSymbol') `
                    -Methods @($nativeProbeMethod) `
                    -DumpbinPaths @($fakeDumpbin) `
                    -NativeLineReader $fakeNativeLineReader
            }
        'fake pdb' | Set-Content -LiteralPath $gameAssemblyPdbPath

        Remove-Item -LiteralPath $gameAssemblyPdbPath
        Assert-NativeInventoryFailure `
            -ExpectedMessage 'Expected one GameAssembly.pdb under' `
            -Operation {
                Add-NativeLayoutInventory `
                    -ProjectRoot $integrationTestRoot `
                    -ArtifactsRoot $integrationArtifacts `
                    -Symbols @('InventoryProbeSymbol') `
                    -Methods @($nativeProbeMethod) `
                    -DumpbinPaths @($fakeDumpbin) `
                    -NativeLineReader $fakeNativeLineReader
            }
        'fake pdb' | Set-Content -LiteralPath $gameAssemblyPdbPath

        $alternateEvidenceDir = Join-Path $playerDir 'AlternateEvidence'
        New-Item -ItemType Directory -Path $alternateEvidenceDir | Out-Null
        $alternatePdbPath = Join-Path $alternateEvidenceDir 'GameAssembly.pdb'
        'second fake pdb' | Set-Content -LiteralPath $alternatePdbPath
        Assert-NativeInventoryFailure `
            -ExpectedMessage 'Expected one GameAssembly.pdb under' `
            -Operation {
                Add-NativeLayoutInventory `
                    -ProjectRoot $integrationTestRoot `
                    -ArtifactsRoot $integrationArtifacts `
                    -Symbols @('InventoryProbeSymbol') `
                    -Methods @($nativeProbeMethod) `
                    -DumpbinPaths @($fakeDumpbin) `
                    -NativeLineReader $fakeNativeLineReader
            }
        Remove-Item -LiteralPath $alternatePdbPath

        'first map' | Set-Content -LiteralPath $primarySymbolMapPath
        'second map' | Set-Content -LiteralPath (Join-Path $alternateEvidenceDir 'SymbolMap')
        Assert-NativeInventoryFailure `
            -ExpectedMessage 'Expected at most one SymbolMap under' `
            -Operation {
                Add-NativeLayoutInventory `
                    -ProjectRoot $integrationTestRoot `
                    -ArtifactsRoot $integrationArtifacts `
                    -Symbols @('InventoryProbeSymbol') `
                    -Methods @($nativeProbeMethod) `
                    -DumpbinPaths @($fakeDumpbin) `
                    -NativeLineReader $fakeNativeLineReader
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
            Implementation   = $helper.Implementation
        }
    )
    Add-UniqueGeneratedMethods `
        -Destination $untargetedRouteMethods `
        -Methods @($helper.Wrapper, $helper.Implementation)
}
$untargetedInterceptorHelpers = @(
    $untargetedHelperEvidence |
        Where-Object { $_.Key -eq 'untargetedInterceptor' }
)
if ($untargetedInterceptorHelpers.Count -ne 1) {
    throw (
        'Expected one captured untargeted interceptor helper; found ' +
        "$($untargetedInterceptorHelpers.Count)."
    )
}
$untargetedInterceptor = $untargetedInterceptorHelpers[0]
$untargetedInterceptorEnsureFlatCallPattern =
    '(?<![A-Za-z0-9_])InterceptorCache_1_EnsureFlat_m[0-9A-F]+' +
    '(?:_gshared)?(?:_inline)?(?![A-Za-z0-9_])'
$untargetedBroadcastEnsureFlatCallSymbols = @(
    Get-GeneratedSymbolMatches `
        -Pattern $untargetedInterceptorEnsureFlatCallPattern `
        -Method $untargetedBroadcast.Implementation
)
$untargetedInterceptorEnsureFlatCallSymbols = @(
    Get-GeneratedSymbolMatches `
        -Pattern $untargetedInterceptorEnsureFlatCallPattern `
        -Method $untargetedInterceptor.Implementation
)
$untargetedSteadyStateEnsureFlatCallSymbols = @(
    $untargetedBroadcastEnsureFlatCallSymbols
    $untargetedInterceptorEnsureFlatCallSymbols
)
if ($untargetedSteadyStateEnsureFlatCallSymbols.Count -gt 1) {
    throw (
        'Expected at most one exact InterceptorCache.EnsureFlat call across the ' +
        'steady untargeted broadcast and interceptor bodies; found ' +
        "$($untargetedSteadyStateEnsureFlatCallSymbols.Count)."
    )
}
$untargetedInterceptorEnsureFlatCallSymbol =
    if ($untargetedSteadyStateEnsureFlatCallSymbols.Count -eq 1) {
        $untargetedSteadyStateEnsureFlatCallSymbols[0]
    }
    else {
        $null
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
$untargetedEvidence.Add(
    "untargetedInterceptorEnsureFlatCallCount=$($untargetedInterceptorEnsureFlatCallSymbols.Count)"
)
$untargetedEvidence.Add(
    "untargetedBroadcastEnsureFlatCallCount=$($untargetedBroadcastEnsureFlatCallSymbols.Count)"
)
$untargetedEvidence.Add(
    "untargetedSteadyStateEnsureFlatCallCount=$($untargetedSteadyStateEnsureFlatCallSymbols.Count)"
)
$untargetedInterceptorEnsureFlatCallDisplay =
    if ($null -eq $untargetedInterceptorEnsureFlatCallSymbol) { 'absent' }
    else { $untargetedInterceptorEnsureFlatCallSymbol }
$untargetedEvidence.Add(
    "untargetedInterceptorEnsureFlatCallSymbol=$untargetedInterceptorEnsureFlatCallDisplay"
)
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
if ($null -ne $untargetedInterceptorEnsureFlatCallSymbol) {
    $nativeSymbols.Add($untargetedInterceptorEnsureFlatCallSymbol)
}
if (!$SkipNativeInventory) {
    $nativeInventoryArguments = @{
        ProjectRoot    = $resolvedProjectPath
        ArtifactsRoot  = $resolvedArtifactsPath
        Symbols        = @($nativeSymbols)
        Methods        = @($untargetedRouteMethods)
        RequiredMethods = @(
            $untargetedBroadcast.Implementation,
            $untargetedInterceptor.Implementation
        )
    }
    if ($null -ne $untargetedInterceptorEnsureFlatCallSymbol) {
        $ensureFlatProbeIsInterceptor =
            $untargetedInterceptorEnsureFlatCallSymbols.Count -eq 1
        $nativeInventoryArguments.NativeInlineProbeMethod =
            if ($ensureFlatProbeIsInterceptor) { $untargetedInterceptor.Implementation }
            else { $untargetedBroadcast.Implementation }
        $nativeInventoryArguments.NativeInlineProbeCallSymbol =
            $untargetedInterceptorEnsureFlatCallSymbol
        $nativeInventoryArguments.NativeInlineProbeExpectedSymbolPrefix =
            if ($ensureFlatProbeIsInterceptor) {
                'MessageBus_RunUntargetedInterceptors_Tis'
            }
            else {
                'MessageBus_UntargetedBroadcast_Tis'
            }
    }
    Add-NativeLayoutInventory @nativeInventoryArguments
}
