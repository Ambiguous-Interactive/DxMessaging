Param(
    [string]$Root = ".",
    [switch]$VerboseOutput
)

$ErrorActionPreference = 'Stop'

function Write-VerboseLine {
    param([string]$Message)
    if ($VerboseOutput) { Write-Host $Message }
}

function Test-IsGitHookPath {
    param([string]$PathValue)

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $false
    }

    $normalizedPath = $PathValue -replace '\\', '/'
    if (-not $normalizedPath.StartsWith('scripts/hooks/')) {
        return $false
    }

    return [string]::IsNullOrEmpty([System.IO.Path]::GetExtension($normalizedPath))
}

function Get-GitIndexEolIssues {
    param([string[]]$Extensions)

    $issues = New-Object System.Collections.Generic.List[string]
    try {
        $gitRoot = git rev-parse --show-toplevel 2>$null
    }
    catch {
        return $issues
    }

    if (-not $gitRoot) {
        return $issues
    }

    $gitRoot = $gitRoot.Trim()
    try {
        $lines = git -C $gitRoot ls-files --eol 2>$null
    }
    catch {
        return $issues
    }

    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split "`t", 2
        if ($parts.Count -lt 2) { continue }

        $meta = $parts[0].Trim()
        $relPath = $parts[1].Trim()
        $ext = [System.IO.Path]::GetExtension($relPath).ToLowerInvariant()
        $isGitHook = Test-IsGitHookPath -PathValue $relPath
        if ($Extensions -notcontains $ext -and -not $isGitHook) { continue }

        # git ls-files --eol output format: i/[eol] w/[eol] attr/[attrs] [path]
        $tokens = $meta -split '\s+'
        $attrToken = $tokens | Where-Object { $_ -like 'attr/*' } | Select-Object -First 1
        if ($attrToken -eq 'attr/-text') { continue }
        
        $indexToken = $tokens | Where-Object { $_ -like 'i/*' } | Select-Object -First 1
        if (-not $indexToken) { continue }
        # MIRROR scripts/check-eol.js isIndexEolViolation(): only i/crlf and
        # i/mixed are genuine non-normalized index endings. i/lf and i/none are
        # already correct; i/-text means git classified the indexed blob as
        # BINARY (e.g. it contains NUL bytes) and a binary blob is never
        # line-ending converted, so flagging it is a false positive that NO
        # line-ending fixer (fix-eol) can ever clear. Whitelist semantics keep
        # any future/unknown token a non-violation by default.
        if ($indexToken -eq 'i/crlf' -or $indexToken -eq 'i/mixed') {
            $issues.Add("$relPath ($indexToken)")
        }
    }

    return $issues
}

# Directories to exclude from scanning
$excludePatterns = @(
    "[\\/]\.git[\\/]",
    "[\\/]node_modules[\\/]",
    "[\\/]Library[\\/]",
    "[\\/]Obj[\\/]|[\\/]obj[\\/]",
    "[\\/]Temp[\\/]",
    "[\\/]Samples~[\\/]",
    "[\\/]\.vs[\\/]",
    "[\\/]\.venv[\\/]",
    "[\\/]\.artifacts[\\/]",
    "[\\/]site[\\/]"
)

# SYNC (bidirectional): Keep in sync with scripts/lib/eol-policy.js crlfExts/lfExts
# (consumed by scripts/check-eol.js and scripts/fix-eol.js), and .gitattributes EOL policy.
# File extensions that require CRLF (C# and .NET files only)
$crlfExtensions = @(
    '.cs', '.csproj', '.sln', '.props'
)

# File extensions that require LF (all other text files)
$lfExtensions = @(
    '.js', '.cjs', '.mjs',
    '.json', '.jsonc', '.toml',
    '.yaml', '.yml',
    '.md', '.markdown',
    '.xml', '.uxml', '.uss',
    '.shader', '.hlsl', '.compute', '.cginc',
    '.asmdef', '.asmref', '.meta',
    '.ps1',
    '.txt',
    '.sh',
    '.bash',
    '.zsh',
    '.ksh',
    '.fish'
)

# All extensions to scan (CRLF + LF types)
$allExtensions = $crlfExtensions + $lfExtensions

$rootPath = Resolve-Path -LiteralPath $Root
Write-VerboseLine "Scanning for EOL/BOM issues under: $rootPath"

$bomFiles = New-Object System.Collections.Generic.List[string]
$crlfViolations = New-Object System.Collections.Generic.List[string]
$lfViolations = New-Object System.Collections.Generic.List[string]
$indexIssues = Get-GitIndexEolIssues -Extensions $allExtensions

Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force |
    ForEach-Object {
        $path = $_.FullName
        # Skip excluded directories
        foreach ($pat in $excludePatterns) { if ($path -match $pat) { return } }

        $ext = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
        $relativePath = [System.IO.Path]::GetRelativePath($rootPath, $path)
        $isGitHook = Test-IsGitHookPath -PathValue $relativePath
        if ($allExtensions -notcontains $ext -and -not $isGitHook) { return }

        try {
            $bytes = [System.IO.File]::ReadAllBytes($path)
        }
        catch {
            Write-VerboseLine "Skipping unreadable file: $path"
            return
        }

        # Check for UTF-8 BOM (EF BB BF)
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $bomFiles.Add($path)
        }

        # Check for line endings based on file type
        $text = [System.Text.Encoding]::UTF8.GetString($bytes)
        
        if ($crlfExtensions -contains $ext) {
            # C# and .NET files should have CRLF (no bare LF or bare CR)
            $lfOnly = [regex]::Matches($text, '(?<!\r)\n').Count
            $crOnly = [regex]::Matches($text, '\r(?!\n)').Count
            if ($lfOnly -gt 0 -or $crOnly -gt 0) {
                $crlfViolations.Add($path)
            }
        }
        elseif ($lfExtensions -contains $ext -or $isGitHook) {
            # All other text files should have LF only (no CRLF or bare CR)
            $crlfCount = [regex]::Matches($text, '\r\n').Count
            $crOnly = [regex]::Matches($text, '\r(?!\n)').Count
            if ($crlfCount -gt 0 -or $crOnly -gt 0) {
                $lfViolations.Add($path)
            }
        }
    }

if ($bomFiles.Count -eq 0 -and $crlfViolations.Count -eq 0 -and $lfViolations.Count -eq 0 -and $indexIssues.Count -eq 0) {
    Write-Host "EOL check passed: line endings correct and no BOMs detected."
    exit 0
}

if ($bomFiles.Count -gt 0) {
    Write-Host "Files contain a UTF-8 BOM (should be no BOM):"
    $bomFiles | ForEach-Object { Write-Host "  $_" }
}

if ($lfViolations.Count -gt 0) {
    Write-Host "Files require LF line endings but contain CRLF or bare CR:"
    $lfViolations | ForEach-Object { Write-Host "  $_" }
}

if ($crlfViolations.Count -gt 0) {
    Write-Host "C# and .NET files require CRLF line endings but contain LF or bare CR:"
    $crlfViolations | ForEach-Object { Write-Host "  $_" }
}

if ($indexIssues.Count -gt 0) {
    Write-Host "Git index contains non-normalized line endings (expected LF in repo for text files):"
    $indexIssues | ForEach-Object { Write-Host "  $_" }
    Write-Host "Fix: Run 'node scripts/fix-eol.js' then re-stage affected files."
}

Write-Error "EOL/BOM policy violations detected. See lists above."
exit 1

