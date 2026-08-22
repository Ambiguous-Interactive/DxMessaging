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
        [string[]]$LaunchablePaths,
        [string]$ExpectedReason,
        [bool]$ExpectedOk,
        [bool]$ExpectedAdvisory = $false
    )
    $script:Count++
    $probe = { param($path) $ExecutablePaths -contains $path }.GetNewClosure()
    $arguments = @{
        InstallRoot     = $InstallRoot
        ProbeExecutable = $probe
        RunnerName      = 'TEST-RUNNER'
    }
    if ($PSBoundParameters.ContainsKey('LaunchablePaths')) {
        $arguments['ProbeLaunch'] = { param($path) $LaunchablePaths -contains $path }.GetNewClosure()
    }
    $result = Test-MsvcToolchain @arguments
    if ($result.Advisory -ne $ExpectedAdvisory) {
        Write-Output ("FAIL  $Name`n" +
            "      expected Advisory=$ExpectedAdvisory, got $($result.Advisory)")
        $script:Failures++
        return
    }
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

    # LAUNCH PROBE (#336). Presence alone reports a `cl.exe` that cannot start as
    # healthy, and the leg then dies after taking a licence seat and the build
    # lock. The probe is injected exactly like the presence probe, so both the
    # healthy and the corrupt case run here rather than needing a Windows host.
    Assert-Verdict -Name 'a compiler that starts passes the launch probe' `
        -InstallRoot $root -ExecutablePaths @($missing) -LaunchablePaths @($missing) `
        -ExpectedOk $true -ExpectedReason 'ok'

    # A present compiler that will not start is its own verdict, distinct from
    # absent, and it BLOCKS. It was advisory until 18 licensed legs on both
    # Windows hosts showed the probe clean on healthy toolchains (#336, step 1).
    Assert-Verdict -Name 'a present compiler that will not start is compiler-unusable' `
        -InstallRoot $root -ExecutablePaths @($missing) -LaunchablePaths @() `
        -ExpectedOk $false -ExpectedReason 'compiler-unusable' -ExpectedAdvisory $false

    # An absent compiler stays `compiler-missing` even with a launch probe wired
    # in, so #333's verdict and its blocking failure are unchanged.
    Assert-Verdict -Name 'an absent compiler is still compiler-missing with a launch probe' `
        -InstallRoot $root -ExecutablePaths @() -LaunchablePaths @() `
        -ExpectedOk $false -ExpectedReason 'compiler-missing'

    # A newer broken toolset must not hide an older working one, the same way a
    # newer absent one already does not.
    Assert-Verdict -Name 'falls back past a newer toolset that will not start' `
        -InstallRoot $root -ExecutablePaths @($missing, $older) -LaunchablePaths @($older) `
        -ExpectedOk $true -ExpectedReason 'ok'

    # The three failure messages must stay distinguishable: the operator fix for
    # each one differs.
    $script:Count++
    $unusable = Test-MsvcToolchain -InstallRoot $root -RunnerName 'TEST-RUNNER' `
        -ProbeExecutable { param($p) $true } -ProbeLaunch { param($p) $false }
    if ($unusable.Message -notmatch 'did not start' -or $unusable.Message -notmatch 'TEST-RUNNER') {
        Write-Output ("FAIL  the compiler-unusable message names the symptom and the runner`n" +
            "      got $($unusable.Message)")
        $script:Failures++
    }
    elseif ($VerboseOutput) { Write-Output 'ok    the compiler-unusable message names the symptom and the runner' }

    # HOW A LAUNCH OUTCOME IS READ. `cl.exe` with no input files exits non-zero on
    # a healthy toolchain, so requiring exit 0 would call a working compiler
    # broken. Windows reports a loader failure as an NTSTATUS instead.
    $launchCases = @(
        @{ Name = 'exit 0 started';                       ExitCode = 0;            Threw = $false; Expected = $true }
        @{ Name = 'exit 2 (no input files) still started'; ExitCode = 2;           Threw = $false; Expected = $true }
        @{ Name = 'STATUS_DLL_NOT_FOUND did not start';    ExitCode = -1073741515; Threw = $false; Expected = $false }
        @{ Name = 'STATUS_INVALID_IMAGE_FORMAT did not start'; ExitCode = -1073741701; Threw = $false; Expected = $false }
        @{ Name = 'unsigned NTSTATUS did not start';       ExitCode = 3221225781;  Threw = $false; Expected = $false }
        @{ Name = 'a throw did not start';                 ExitCode = 0;           Threw = $true;  Expected = $false }
        @{ Name = 'no exit code at all did not start';     ExitCode = $null;       Threw = $false; Expected = $false }
    )
    foreach ($case in $launchCases) {
        $script:Count++
        $actual = Test-CompilerLaunchOutcome -ExitCode $case.ExitCode -Threw $case.Threw
        if ($actual -ne $case.Expected) {
            Write-Output ("FAIL  launch outcome: $($case.Name)`n" +
                "      expected $($case.Expected), got $actual")
            $script:Failures++
        }
        elseif ($VerboseOutput) { Write-Output "ok    launch outcome: $($case.Name)" }
    }

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
#   * the platform gate would exit 0 before any verdict on a non-Windows host,
#     so it is stripped;
#   * the workflow dot-sources a WRAPPER that calls the script by path -- dot-
#     sourcing the script itself trips its own dot-source guard and returns 0;
#   * a fake `vswhere` is required to reach the VERDICT path. Pointing at an
#     absent vswhere exercises a different `throw` entirely, so a guard built
#     that way cannot see `exit 1` versus `throw` at all.
#
# The fake is described as DATA -- stdout lines, stderr lines, exit code -- and
# rendered into whichever dialect the host can execute. Windows is the platform
# the gate actually runs on, so skipping it there would leave the interesting
# half untested; splatting in particular is a PowerShell behaviour worth pinning
# on the platform that matters.
function New-FakeVsWhere {
    param([string]$Root, [string[]]$Stdout, [string[]]$Stderr, [int]$ExitCode)

    $argv = Join-Path $Root 'argv'
    if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
        $path = Join-Path $Root 'vswhere.cmd'
        # Space before `>`: `%*>` lets a trailing digit in the last argument be
        # read as a file-descriptor redirect.
        $lines = @('@echo off', "echo %* > `"$argv`"")
        $lines += $Stderr | ForEach-Object { "echo $_ 1>&2" }
        $lines += $Stdout | ForEach-Object { if ($_) { "echo $_" } else { 'echo.' } }
        $lines += "exit /b $ExitCode"
        Set-Content -LiteralPath $path -Value $lines -Encoding ascii
    }
    else {
        $path = Join-Path $Root 'vswhere'
        $lines = @('#!/bin/bash', ("printf '%s\n' " + '"$@"' + " > '$argv'"))
        $lines += $Stderr | ForEach-Object { "echo '$_' >&2" }
        $lines += $Stdout | ForEach-Object { "echo '$_'" }
        $lines += "exit $ExitCode"
        Set-Content -LiteralPath $path -Value $lines
        & chmod +x $path
    }
    return $path
}

function Assert-EndToEnd {
    param(
        [string]$Name,
        [string[]]$Stdout = @(),
        [string[]]$Stderr = @(),
        [int]$ExitCode = 0,
        [scriptblock]$Setup,
        [string]$ExpectedText,
        [scriptblock]$Assert
    )
    $script:Count++
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("msvc-e2e-" + [guid]::NewGuid())
    try {
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        if ($Setup) { $Stdout = @(& $Setup $root) }

        $source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../unity/assert-msvc-toolchain.ps1')
        $gateStart = $source.IndexOf('if ([System.IO.Path]::DirectorySeparatorChar')
        $gateEnd = $source.IndexOf('}', $source.IndexOf('exit 0', $gateStart))
        $patched = Join-Path $root 'assert.ps1'
        Set-Content -LiteralPath $patched -NoNewline `
            -Value ($source.Remove($gateStart, $gateEnd - $gateStart + 1))

        $vswhere = New-FakeVsWhere -Root $root -Stdout $Stdout -Stderr $Stderr -ExitCode $ExitCode
        $wrapper = Join-Path $root 'run.ps1'
        Set-Content -LiteralPath $wrapper -NoNewline -Value "& '$patched' -VsWherePath '$vswhere'"
        $output = (pwsh -NoProfile -NonInteractive -Command ". '$wrapper'" 2>&1 | Out-String)
        $exit = $LASTEXITCODE

        # Split on ALL whitespace: the Windows fake records argv on one line via
        # `%*`, the Unix one line-per-argument. No expected token contains a space.
        $argvPath = Join-Path $root 'argv'
        $argv = if (Test-Path -LiteralPath $argvPath) {
            (Get-Content -Raw -LiteralPath $argvPath) -split '\s+' | Where-Object { $_ }
        }
        else { @() }

        if ($Assert) {
            & $Assert ([pscustomobject]@{ Exit = $exit; Output = $output; Argv = $argv })
            return
        }
        if ($exit -eq 0) {
            Write-Output ("FAIL  $Name`n      expected a non-zero exit; the gate cannot fail a step`n" +
                "      output: $output")
            $script:Failures++
        }
        elseif ($ExpectedText -and $output -notmatch [regex]::Escape($ExpectedText)) {
            Write-Output ("FAIL  $Name`n      expected the message to contain '$ExpectedText'`n" +
                "      output: $output")
            $script:Failures++
        }
        elseif ($VerboseOutput) { Write-Output "ok    $Name (exit $exit)" }
    }
    finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

# D1: a failing VERDICT must fail the process. `exit 1` here exits only the
# script, leaving the step green -- the bug this gate shipped with.
Assert-EndToEnd -Name 'a failing verdict fails the process' `
    -Setup {
        param($r)
        $vs = Join-Path $r 'vs'
        New-Item -ItemType Directory -Force -Path $vs | Out-Null
        $vs
    } `
    -ExpectedText 'no MSVC toolset'

# D3: a vswhere that FAILS is a discovery failure, not evidence the toolchain is
# absent. Telling a healthy host to install the C++ workload blocks its IL2CPP CI.
Assert-EndToEnd -Name 'a failing vswhere is reported as discovery failure' `
    -Stderr @('boom') -ExitCode 3 -ExpectedText 'vswhere exited 3'

# D4: vswhere prints usage text to stdout on a bad switch. Unvalidated, it
# reaches Test-Path, where `Usage:` parses as a drive qualifier and throws.
Assert-EndToEnd -Name 'vswhere usage text is not treated as a path' `
    -Stdout @('Usage: vswhere.exe -all') -ExpectedText 'No Visual Studio installation'

# D8: the DISCOVERY QUERY is behaviour. Two ways it has already been wrong:
# asking only for the v143 component rejects a host pinned to an older toolset
# that `Test-MsvcToolchain` accepts, and SPLATTING the argument list glob-expands
# `*` against the working directory before vswhere ever sees it.
Assert-EndToEnd -Name 'vswhere is asked for any C++ toolset, with an unexpanded glob' `
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
# EXPLICIT. The guards above run subprocesses that are SUPPOSED to fail, so
# `$LASTEXITCODE is 1 when this script ends. The built-in `shell: pwsh`
# appends `exit $LASTEXITCODE`, which failed this step in CI while every
# assertion passed. Falling off the end is not the same as succeeding.
exit 0
