#Requires -Version 7.0
<#
.SYNOPSIS
    Exercise every verdict of the MSVC toolchain assertion, on any platform.

.DESCRIPTION
    `Test-MsvcToolchain` takes an already-resolved installation root and a probe
    callback, so the whole decision runs on Linux with fake directory trees. Only
    the `vswhere` invocation is Windows-only, and it does nothing but produce the
    root this function consumes.

    The case that matters most is `compiler-missing`: a resolved toolset whose
    `cl.exe` is absent. That is what #333 actually was, and it is distinct from
    having no Visual Studio at all -- Unity discovers the version directory,
    puts it on PATH, and fails twenty minutes later with a message that names
    neither the host nor the cause.
#>
[CmdletBinding()]
param([switch]$VerboseOutput)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/../unity/assert-msvc-toolchain.ps1"

$script:Failures = 0
$script:Count = 0

function Assert-Verdict {
    param(
        [string]$Name,
        [string]$InstallRoot,
        [string[]]$ExecutablePaths = @(),
        [string]$ExpectedReason,
        [bool]$ExpectedOk
    )
    $script:Count++
    $probe = { param($path) $ExecutablePaths -contains $path }.GetNewClosure()
    $result = Test-MsvcToolchain -InstallRoot $InstallRoot -ProbeExecutable $probe -RunnerName 'TEST-RUNNER'
    if ($result.Ok -ne $ExpectedOk -or $result.Reason -ne $ExpectedReason) {
        Write-Output ("FAIL  $Name`n" +
            "      expected Ok=$ExpectedOk Reason=$ExpectedReason`n" +
            "      got      Ok=$($result.Ok) Reason=$($result.Reason)`n" +
            "      message  $($result.Message)")
        $script:Failures++
        return
    }
    if (-not $result.Ok -and $result.Message -notmatch 'TEST-RUNNER') {
        Write-Output "FAIL  $Name`n      failure message does not name the runner: $($result.Message)"
        $script:Failures++
        return
    }
    if ($VerboseOutput) { Write-Output "ok    $Name" }
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("msvc-" + [guid]::NewGuid())
function New-Toolset {
    param([string]$Version)
    $dir = Join-Path $root "VC/Tools/MSVC/$Version"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    # The path the production code builds uses backslashes; mirror it exactly so
    # the probe comparison is the real one and not a normalized approximation.
    return (Join-Path (Join-Path $root "VC\Tools\MSVC\$Version") 'bin\Hostx64\x64\cl.exe')
}

try {
    # No Visual Studio at all -- vswhere resolved nothing.
    Assert-Verdict -Name 'empty install root is no-visual-studio' `
        -InstallRoot '' -ExpectedOk $false -ExpectedReason 'no-visual-studio'
    Assert-Verdict -Name 'whitespace install root is no-visual-studio' `
        -InstallRoot '   ' -ExpectedOk $false -ExpectedReason 'no-visual-studio'

    # Visual Studio present, but no MSVC toolset directory at all.
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    Assert-Verdict -Name 'install without a toolset directory is no-toolset' `
        -InstallRoot $root -ExpectedOk $false -ExpectedReason 'no-toolset'

    # THE #333 CASE: the toolset exists, cl.exe does not.
    $missing = New-Toolset -Version '14.51.36231'
    Assert-Verdict -Name 'toolset present but cl.exe absent is compiler-missing' `
        -InstallRoot $root -ExpectedOk $false -ExpectedReason 'compiler-missing'

    # The same tree, with the compiler restored.
    Assert-Verdict -Name 'toolset with an executable cl.exe passes' `
        -InstallRoot $root -ExecutablePaths @($missing) -ExpectedOk $true -ExpectedReason 'ok'

    # Two toolsets, only the OLDER usable: the newest is preferred but a working
    # older one still satisfies the requirement, so the leg should not fail.
    $older = New-Toolset -Version '14.29.30133'
    Assert-Verdict -Name 'falls back to an older usable toolset' `
        -InstallRoot $root -ExecutablePaths @($older) -ExpectedOk $true -ExpectedReason 'ok'

    # Version ordering must be numeric, not lexical: 14.9 must not outrank 14.51.
    $null = New-Toolset -Version '14.9.00000'
    $result = Test-MsvcToolchain -InstallRoot $root -RunnerName 'TEST-RUNNER' -ProbeExecutable { param($p) $false }
    $script:Count++
    if ($result.Path -notmatch [regex]::Escape('14.51.36231')) {
        Write-Output ("FAIL  newest toolset is chosen by version, not by name`n" +
            "      expected the reported path to name 14.51.36231, got $($result.Path)")
        $script:Failures++
    }
    elseif ($VerboseOutput) { Write-Output 'ok    newest toolset is chosen by version, not by name' }

    # A non-version directory must not crash the comparison.
    $null = New-Toolset -Version 'not-a-version'
    Assert-Verdict -Name 'a non-version toolset directory does not break sorting' `
        -InstallRoot $root -ExecutablePaths @($missing) -ExpectedOk $true -ExpectedReason 'ok'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:Failures -gt 0) {
    Write-Output "`n$script:Failures of $script:Count MSVC toolchain assertions failed."
    exit 1
}
Write-Output "All $script:Count MSVC toolchain assertions passed."
