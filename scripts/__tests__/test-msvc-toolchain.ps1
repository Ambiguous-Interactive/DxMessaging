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
    # NOTE: PowerShell normalizes `\` to `/` inside `Join-Path` on Linux, so both
    # this path and the production one are normalized and the comparison IS an
    # approximation. Nothing in this suite can catch a Windows separator bug; that
    # is a real limit of testing Windows-only path logic off Windows, not a
    # property this fixture establishes.
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

# END-TO-END GUARDS. Everything above tests `Test-MsvcToolchain` in isolation;
# these drive the whole script the way the workflow does, because three of the
# defects that shipped lived entirely outside that function.
#
# Modelling notes, each learned by getting it wrong first:
#   * the platform gate would exit 0 before any verdict here, so it is stripped;
#   * the workflow dot-sources a WRAPPER that calls the script by path -- dot-
#     sourcing the script itself trips its own dot-source guard and returns 0;
#   * a fake `vswhere` is required to reach the VERDICT path. Pointing at an
#     absent vswhere exercises a different `throw` entirely, so a guard built
#     that way cannot see `exit 1` versus `throw` at all.
function Invoke-GateEndToEnd {
    param([string]$Root, [string]$VsWhereBody)
    $source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../unity/assert-msvc-toolchain.ps1')
    $gateStart = $source.IndexOf('if ([System.IO.Path]::DirectorySeparatorChar')
    $gateEnd = $source.IndexOf('}', $source.IndexOf('exit 0', $gateStart))
    $patched = Join-Path $Root 'assert.ps1'
    Set-Content -LiteralPath $patched -NoNewline -Value ($source.Remove($gateStart, $gateEnd - $gateStart + 1))

    # The fake records its own argv: the QUERY is behaviour too. Asking for the
    # v143 component alone rejects an install pinned to an older toolset, which
    # `Test-MsvcToolchain` is explicitly written to accept.
    $vswhere = Join-Path $Root 'vswhere'
    $recorder = "printf '%s\n' " + '"$@"' + " > '$Root/argv'"
    Set-Content -LiteralPath $vswhere -Value @('#!/bin/bash', $recorder, $VsWhereBody)
    & chmod +x $vswhere

    $wrapper = Join-Path $Root 'run.ps1'
    Set-Content -LiteralPath $wrapper -NoNewline -Value "& '$patched' -VsWherePath '$vswhere'"
    $output = pwsh -NoProfile -NonInteractive -Command ". '$wrapper'" 2>&1
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Output = ($output | Out-String) }
}

function Assert-EndToEnd {
    param([string]$Name, [string]$VsWhereBody, [scriptblock]$Setup, [string]$ExpectedText,
        [scriptblock]$Assert)
    $script:Count++
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("msvc-e2e-" + [guid]::NewGuid())
    try {
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        if ($Setup) { & $Setup $root }
        $r = Invoke-GateEndToEnd -Root $root -VsWhereBody $VsWhereBody.Replace('{ROOT}', $root)
        $argvPath = Join-Path $root 'argv'
        $r | Add-Member -NotePropertyName Argv -NotePropertyValue @(
            if (Test-Path -LiteralPath $argvPath) { Get-Content -LiteralPath $argvPath } )
        if ($Assert) { & $Assert $r; return }
        if ($r.Exit -eq 0) {
            Write-Output ("FAIL  $Name`n      expected a non-zero exit; the gate cannot fail a step`n" +
                "      output: $($r.Output)")
            $script:Failures++
        }
        elseif ($ExpectedText -and $r.Output -notmatch [regex]::Escape($ExpectedText)) {
            Write-Output ("FAIL  $Name`n      expected the message to contain '$ExpectedText'`n" +
                "      output: $($r.Output)")
            $script:Failures++
        }
        elseif ($VerboseOutput) { Write-Output "ok    $Name (exit $($r.Exit))" }
    }
    finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

# D1: a failing VERDICT must fail the process. `exit 1` here exits only the
# script, leaving the step green -- the bug this gate shipped with.
Assert-EndToEnd -Name 'a failing verdict fails the process' `
    -VsWhereBody 'echo "{ROOT}/vs"; exit 0' `
    -Setup { param($r) New-Item -ItemType Directory -Force -Path (Join-Path $r 'vs') | Out-Null } `
    -ExpectedText 'no MSVC toolset'

# D3: a vswhere that FAILS is a discovery failure, not evidence the toolchain is
# absent. Telling a healthy host to install the C++ workload blocks its IL2CPP CI.
Assert-EndToEnd -Name 'a failing vswhere is reported as discovery failure' `
    -VsWhereBody 'echo "boom" >&2; exit 3' `
    -ExpectedText 'vswhere exited 3'

# D4: vswhere prints usage text to stdout on a bad switch. Unvalidated, it
# reaches Test-Path, where `Usage:` parses as a drive qualifier and throws.
Assert-EndToEnd -Name 'vswhere usage text is not treated as a path' `
    -VsWhereBody 'echo "Usage: vswhere.exe [-all]"; exit 0' `
    -ExpectedText 'No Visual Studio installation'

# D8: the DISCOVERY QUERY is behaviour. Two ways it has already been wrong:
# asking only for the v143 component rejects a host pinned to an older toolset
# that `Test-MsvcToolchain` accepts, and an unquoted `*` glob-expands against the
# working directory before vswhere ever sees it.
Assert-EndToEnd -Name 'vswhere is asked for any C++ toolset, with an unexpanded glob' `
    -VsWhereBody 'echo ""; exit 0' `
    -Assert {
        param($r)
        $required = @(
            '-requiresAny',
            'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
            'Microsoft.VisualStudio.Workload.VCTools',
            '*')
        $absent = @($required | Where-Object { $r.Argv -notcontains $_ })
        if ($absent.Count -gt 0) {
            Write-Output ("FAIL  vswhere is asked for any C++ toolset, with an unexpanded glob`n" +
                "      missing from the query: $($absent -join ', ')`n" +
                "      argv: $($r.Argv -join ' ')")
            $script:Failures++
        }
        elseif ($VerboseOutput) {
            Write-Output 'ok    vswhere is asked for any C++ toolset, with an unexpanded glob'
        }
    }

if ($script:Failures -gt 0) {
    Write-Output "`n$script:Failures of $script:Count MSVC toolchain assertions failed."
    exit 1
}
Write-Output "All $script:Count MSVC toolchain assertions passed."
