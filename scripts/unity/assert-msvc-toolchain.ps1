#Requires -Version 7.0
<#
.SYNOPSIS
    Fail fast when the MSVC C++ compiler an IL2CPP player build needs is absent.

.DESCRIPTION
    IL2CPP compiles generated C++ with `cl.exe` from a Visual Studio MSVC
    toolset. Mono legs (editmode, playmode) never touch it, which is why a
    missing toolchain shows up as a standalone-only failure.

    `assert-unity-host-prereqs` checks the VC++ *runtime* redistributables, which
    are what the Editor needs in order to LAUNCH. Nothing checked the VC++ *build
    tools*, which are what IL2CPP needs in order to COMPILE. The gap cost three
    Unity licence seats and roughly two hours on 2026-07-31 (#333): three
    standalone legs each took the organization build lock, consumed a seat, built
    for about twenty minutes, and only then died -- two reporting
    `Player build failed` and the third a player crashing with
    `CreateDirectory '' failed`, neither of which names the real cause. The
    per-host nature was only visible by correlating `runner_name` across four
    runs.

    This script answers the question in seconds, before the lock is taken, and
    names the runner in the failure.

    The decision is separated from the discovery on purpose. `Test-MsvcToolchain`
    is pure: it takes an already-resolved installation root and a probe callback,
    so every verdict is exercised on Linux by
    `scripts/__tests__/test-msvc-toolchain.ps1`.

    That suite also drives this file end to end against a fake `vswhere`, because
    three defects lived outside the pure function entirely: `exit 1` could not
    fail a step under this repository's custom `shell:` template, a failing
    vswhere was reported as a missing toolchain, and splatting the argument list
    glob-expanded `-products *` against the working directory. Only the Windows
    path separators remain genuinely untestable here.

.PARAMETER VsWherePath
    Location of `vswhere.exe`. Defaults to the fixed path the Visual Studio
    Installer guarantees, which is why Microsoft documents it as the supported
    discovery mechanism rather than probing `Program Files` by hand.

.PARAMETER DetectOnly
    Report the verdict without failing, for diagnostics.
#>
[CmdletBinding()]
param(
    [string]$VsWherePath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
    [switch]$DetectOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Without this, `$ErrorActionPreference = 'Stop'` turns ANY non-zero exit from
# `vswhere` into a thrown NativeCommandExitException -- an unhandled stack trace
# instead of a diagnosis. Every other Windows-facing script in this directory
# pins it; this one did not.
$PSNativeCommandUseErrorActionPreference = $false

function Test-MsvcToolchain {
    <#
    .SYNOPSIS
        Decide whether an MSVC installation can actually compile.

    .DESCRIPTION
        Returns a result object rather than throwing, so the caller owns the
        failure mode and the tests can assert on every branch.

        `InstallRoot` is what `vswhere` resolved, or empty when it found nothing.
        `ProbeExecutable` is a callback taking a path and returning $true when
        that path is an executable file; injecting it is what makes this testable
        off Windows, where no such file exists.

        The distinction that matters: a resolved root whose `cl.exe` is missing
        is NOT the same as no Visual Studio at all. The first is a broken or
        half-updated install -- exactly what #333 was, since the version
        directory existed and Unity put it on PATH -- and the operator fix
        differs.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$InstallRoot,
        [scriptblock]$ProbeExecutable,
        [scriptblock]$ProbeLaunch,
        [string]$RunnerName = 'unknown'
    )

    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        return [pscustomobject]@{
            Ok       = $false
            Advisory = $false
            Reason   = 'no-visual-studio'
            Path     = ''
            Message  = ("No Visual Studio installation with the C++ toolset was found on runner " +
                "'$RunnerName'. IL2CPP cannot compile a player without it. A runner administrator " +
                "must install the 'Desktop development with C++' workload.")
        }
    }

    # The toolset version directory is not fixed, so the newest one wins. Sorting
    # by name is wrong for versions (14.9 would beat 14.51), so compare the
    # parsed version and fall back to the raw name only when it does not parse.
    $toolsRoot = Join-Path $InstallRoot 'VC\Tools\MSVC'
    $versions = @()
    if (Test-Path -LiteralPath $toolsRoot -PathType Container) {
        $versions = @(
            Get-ChildItem -LiteralPath $toolsRoot -Directory -ErrorAction SilentlyContinue |
                Sort-Object -Property @{ Expression = {
                    $parsed = $null
                    if ([version]::TryParse($_.Name, [ref]$parsed)) { $parsed } else { [version]'0.0' }
                } }, Name -Descending
        )
    }

    if ($versions.Count -eq 0) {
        return [pscustomobject]@{
            Ok       = $false
            Advisory = $false
            Reason   = 'no-toolset'
            Path     = $toolsRoot
            Message  = ("Visual Studio is installed on runner '$RunnerName' but carries no MSVC " +
                "toolset under '$toolsRoot'. IL2CPP cannot compile a player. A runner " +
                "administrator must add the 'Desktop development with C++' workload.")
        }
    }

    # A compiler that is present but cannot start is tracked separately from one
    # that is absent, because the operator fix differs and because a newer broken
    # toolset must not hide an older working one.
    $unusable = $null
    foreach ($version in $versions) {
        $compiler = Join-Path $version.FullName 'bin\Hostx64\x64\cl.exe'
        if (-not (& $ProbeExecutable $compiler)) { continue }
        if ($ProbeLaunch -and -not (& $ProbeLaunch $compiler)) {
            if ($null -eq $unusable) { $unusable = [pscustomobject]@{ Name = $version.Name; Path = $compiler } }
            continue
        }
        return [pscustomobject]@{
            Ok       = $true
            Advisory = $false
            Reason   = 'ok'
            Path     = $compiler
            Message  = "MSVC toolset $($version.Name) is usable: $compiler"
        }
    }

    if ($null -ne $unusable) {
        return [pscustomobject]@{
            Ok       = $false
            # ADVISORY, not blocking. This gate runs before the organization build
            # lock, so a false failure would block every IL2CPP leg on the runner.
            # No Windows host has yet demonstrated that the launch probe exits
            # cleanly on a HEALTHY toolchain invoked by full path outside vcvars
            # (#336, step 1), so until it does, a failed launch is reported and the
            # leg continues. Flip this to $false in the same change that records
            # that demonstration.
            Advisory = $true
            Reason   = 'compiler-unusable'
            Path     = $unusable.Path
            Message  = ("MSVC toolset $($unusable.Name) is present on runner '$RunnerName' but " +
                "its compiler did not start at '$($unusable.Path)'. A compiler that cannot " +
                "start is normally a missing sibling DLL, a truncated file, or security " +
                "tooling blocking it, and IL2CPP will fail the player build after taking a " +
                "Unity licence seat and the build lock. A runner administrator must repair the " +
                "'Desktop development with C++' workload. This is reported, not enforced: see " +
                "issue #336.")
        }
    }

    $newest = $versions[0]
    return [pscustomobject]@{
        Ok       = $false
        Advisory = $false
        Reason   = 'compiler-missing'
        Path     = (Join-Path $newest.FullName 'bin\Hostx64\x64\cl.exe')
        Message  = ("MSVC toolset $($newest.Name) is present on runner '$RunnerName' but its " +
            "compiler is not executable at " +
            "'$(Join-Path $newest.FullName 'bin\Hostx64\x64\cl.exe')'. This is a broken or " +
            "half-completed Visual Studio update, not a missing install -- Unity will still " +
            "discover the version directory and put it on PATH, then fail the IL2CPP link " +
            "with `"cl.exe is not recognized`" after a full build. A runner administrator " +
            "must repair the 'Desktop development with C++' workload.")
    }
}

function Test-CompilerLaunchOutcome {
    <#
    .SYNOPSIS
        Decide whether a compiler process actually started, from how it ended.

    .DESCRIPTION
        Pure, so every branch runs on Linux. The signal is that the process
        STARTED, not that it liked its arguments: `cl.exe` invoked with no input
        files still exits non-zero on a healthy toolchain, so requiring exit 0
        would call a working compiler broken.

        Windows reports a loader failure -- a missing sibling DLL, a truncated
        image, an execution block -- as an NTSTATUS in the exit code rather than
        as a compiler diagnostic. `0xC0000135` is STATUS_DLL_NOT_FOUND and
        `0xC000007B` is STATUS_INVALID_IMAGE_FORMAT. PowerShell surfaces those as
        negative signed integers, so both forms are checked.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][object]$ExitCode,
        [bool]$Threw
    )

    if ($Threw) { return $false }
    if ($null -eq $ExitCode) { return $false }

    # `0xC0000000L`, with the L. PowerShell parses a hex literal with no type suffix as a
    # SIGNED int, so a bare `0xC0000000` is -1073741824 and `0 -ge -1073741824`
    # would call every healthy exit code a loader failure.
    $code = [int64]$ExitCode
    if ($code -lt 0 -or $code -ge 0xC0000000L) { return $false }
    return $true
}

# Dot-sourced by the tests, which supply their own inputs.
if ($MyInvocation.InvocationName -eq '.') { return }

$runner = if ($env:RUNNER_NAME) { $env:RUNNER_NAME } else { 'unknown' }

if ([System.IO.Path]::DirectorySeparatorChar -ne '\') {
    Write-Output "::notice::Skipping the MSVC toolchain assertion: not a Windows runner."
    exit 0
}

function Invoke-VsWhere {
    <#
        Returns the newest installation root carrying a C++ toolset, or throws
        with a reason of its own. A vswhere that FAILS is not the same as a
        vswhere that finds nothing: the first is a transient (a locked
        `_Instances` state file, a concurrent VS Installer, an older vswhere that
        rejects `-products`) on a possibly healthy host, and telling that admin
        to "install the C++ workload" is both wrong and blocks all IL2CPP CI on
        the machine.
    #>
    param([string]$Path)

    # The argument list is written out literally rather than splatted from an
    # array. SPLATTING a native command glob-expands `*` against the working
    # directory -- source-level quoting does NOT prevent it, which is what an
    # earlier version of this file claimed. Measured on 7.6.4:
    #   & $p -products '*'      ->  -products *
    #   $a=@('-products','*'); & $p @a  ->  -products __pycache__ _site ...
    # vswhere would then have been asked for a product named after whatever
    # happened to sit in the checkout, matched nothing, and reported a healthy
    # host as having no Visual Studio at all.
    #
    # `-products *` so Build Tools installs count, not just full Visual Studio.
    #
    # `-requiresAny` makes the two `-requires` an OR: an install satisfying
    # EITHER the version-agnostic C++ component or the Build Tools workload
    # counts. Requiring both would reject an install pinned to an older toolset
    # -- reporting `no-visual-studio` for a host whose older toolset
    # `Test-MsvcToolchain` is explicitly written to accept. (GitHub Copilot
    # flagged the earlier wording, which said "AND" and read as the opposite.)
    $stdout = & $Path -latest -products '*' -requiresAny `
        -requires 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' `
        -requires 'Microsoft.VisualStudio.Workload.VCTools' `
        -property 'installationPath' 2>$null
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        throw ("vswhere exited $code while enumerating Visual Studio installations. " +
            "This is a discovery failure, NOT evidence that the C++ toolchain is missing; " +
            "the leg is failed closed rather than guess. Re-run, and if it persists check " +
            "the Visual Studio Installer state on this runner.")
    }
    foreach ($line in @($stdout)) {
        $candidate = "$line".Trim()
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        # vswhere prints usage text to stdout on a bad switch, and an unvalidated
        # line reaches `Join-Path`/`Test-Path`, where `Usage:` parses as a drive
        # qualifier and raises DriveNotFoundException.
        if (-not [System.IO.Path]::IsPathRooted($candidate)) { continue }
        return $candidate
    }
    return ''
}

$installRoot = ''
if (Test-Path -LiteralPath $VsWherePath -PathType Leaf) {
    $installRoot = Invoke-VsWhere -Path $VsWherePath
}
else {
    throw ("vswhere.exe was not found at '$VsWherePath' on runner '$runner'. It ships with " +
        "the Visual Studio Installer, so its absence means no Visual Studio tooling is " +
        "installed at all. A runner administrator must install the 'Desktop development " +
        "with C++' workload.")
}

# Presence AND a launch probe. Presence is what #333 needed and is the blocking
# check. The launch probe catches a `cl.exe` that is there but cannot start -- a
# failed update, a missing sibling DLL, security tooling -- which presence cannot
# see and which today reports the host healthy and fails the leg twenty minutes
# later, after taking a licence seat and the build lock.
#
# The launch verdict is ADVISORY. This gate runs before the organization build
# lock, so a launch probe that is wrong about a HEALTHY host would block every
# IL2CPP leg on that runner. That risk is real: `cl.exe` resolves sibling DLLs
# from its own directory and the toolchain normally runs under `vcvars`, and no
# Windows host has yet demonstrated the healthy case (#336, step 1). Reporting it
# costs nothing and names the cause up front; enforcing it needs that
# demonstration first.
$result = Test-MsvcToolchain -InstallRoot $installRoot -RunnerName $runner -ProbeExecutable {
    param($path)
    Test-Path -LiteralPath $path -PathType Leaf
} -ProbeLaunch {
    param($path)
    # EVERYTHING is inside the try, including reading the outcome. This verdict is
    # advisory, so an unexpected throw here must become "did not start" and a
    # warning, never a terminating error that fails the leg this gate exists to
    # protect.
    try {
        # By full path, with no input files, output discarded: the question is
        # whether the image loads, not what it says.
        $null = & $path '/?' 2>&1
        return (Test-CompilerLaunchOutcome -ExitCode $LASTEXITCODE -Threw $false)
    }
    catch {
        return (Test-CompilerLaunchOutcome -ExitCode $null -Threw $true)
    }
}

if ($result.Ok) {
    Write-Output $result.Message
    exit 0
}

if ($DetectOnly -or $result.Advisory) {
    Write-Output "::warning title=MSVC C++ toolchain ($($result.Reason))::$($result.Message)"
    exit 0
}

Write-Output "::error title=MSVC C++ toolchain unusable ($($result.Reason))::$($result.Message)"
# `throw`, NOT `exit 1`. Under this repository's custom `shell:` template
# (`pwsh ... -Command ". '{0}'"`), `exit N` inside a script invoked by path sets
# $LASTEXITCODE but does NOT exit the host pwsh, and the template -- unlike the
# built-in `shell: pwsh` -- appends no `exit $LASTEXITCODE`. So `exit 1` here
# made this entire gate a no-op that printed an error and passed. A terminating
# error is what every other step on this shell uses.
throw $result.Message
