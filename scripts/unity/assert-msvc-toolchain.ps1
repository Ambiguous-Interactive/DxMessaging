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
        [string]$RunnerName = 'unknown'
    )

    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        return [pscustomobject]@{
            Ok      = $false
            Reason  = 'no-visual-studio'
            Path    = ''
            Message = ("No Visual Studio installation with the C++ toolset was found on runner " +
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
            Ok      = $false
            Reason  = 'no-toolset'
            Path    = $toolsRoot
            Message = ("Visual Studio is installed on runner '$RunnerName' but carries no MSVC " +
                "toolset under '$toolsRoot'. IL2CPP cannot compile a player. A runner " +
                "administrator must add the 'Desktop development with C++' workload.")
        }
    }

    foreach ($version in $versions) {
        $compiler = Join-Path $version.FullName 'bin\Hostx64\x64\cl.exe'
        if (& $ProbeExecutable $compiler) {
            return [pscustomobject]@{
                Ok      = $true
                Reason  = 'ok'
                Path    = $compiler
                Message = "MSVC toolset $($version.Name) is usable: $compiler"
            }
        }
    }

    $newest = $versions[0]
    return [pscustomobject]@{
        Ok      = $false
        Reason  = 'compiler-missing'
        Path    = (Join-Path $newest.FullName 'bin\Hostx64\x64\cl.exe')
        Message = ("MSVC toolset $($newest.Name) is present on runner '$RunnerName' but its " +
            "compiler is not executable at " +
            "'$(Join-Path $newest.FullName 'bin\Hostx64\x64\cl.exe')'. This is a broken or " +
            "half-completed Visual Studio update, not a missing install -- Unity will still " +
            "discover the version directory and put it on PATH, then fail the IL2CPP link " +
            "with `"cl.exe is not recognized`" after a full build. A runner administrator " +
            "must repair the 'Desktop development with C++' workload.")
    }
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
    # `-requiresAny` with the version-agnostic component AND the workload one:
    # the v143-only filter rejects an install pinned to an older toolset, which
    # would report `no-visual-studio` for a host whose older toolset
    # `Test-MsvcToolchain` is explicitly written to accept.
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

$result = Test-MsvcToolchain -InstallRoot $installRoot -RunnerName $runner -ProbeExecutable {
    param($path)
    Test-Path -LiteralPath $path -PathType Leaf
}

if ($result.Ok) {
    Write-Output $result.Message
    exit 0
}

if ($DetectOnly) {
    Write-Output "::warning::$($result.Message)"
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
