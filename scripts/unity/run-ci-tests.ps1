#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+f\d+$')]
    [string]$UnityVersion,

    [Parameter(Mandatory = $true)]
    [ValidateSet('editmode', 'playmode', 'standalone', 'shipping')]
    [string]$TestMode,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$AssemblyNames,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactsPath,

    [string]$RepoRoot = $(if ($env:GITHUB_WORKSPACE) { $env:GITHUB_WORKSPACE } else { (Resolve-Path ([System.IO.Path]::Combine($PSScriptRoot, '..', '..'))).Path }),

    [string]$ProjectPath,

    [string]$CachePath,

    [string]$UnityEditorPath = $env:UNITY_EDITOR_PATH,

    [string]$UnityInstallRoot = $(if ($env:UNITY_EDITOR_INSTALL_ROOT) { $env:UNITY_EDITOR_INSTALL_ROOT } else { 'C:\Unity\Editors' }),

    [string]$TestCategory = $(if ($env:DXM_UNITY_TEST_CATEGORY) { $env:DXM_UNITY_TEST_CATEGORY } else { '' }),

    [switch]$IncludeComparisons,

    [switch]$ReleaseCodeOptimization,

    [ValidateSet('IL2CPP', 'Mono2x')]
    [string]$StandaloneScriptingBackend = 'IL2CPP',

    [switch]$ReleasePlayerBuild,

    [string]$CanonicalProfilePath,

    [ValidateSet('semantic', 'cardinality')]
    [string]$ShippingTopology = 'semantic',

    [ValidateSet(1, 16, 18, 256, 1000)]
    [int]$ShippingMessageTypeCount = 18,

    [ValidateRange(1, 10)]
    [int]$StandalonePlayerRunCount = 1,

    [ValidateRange(0, [long]::MaxValue)]
    [long]$StandalonePlayerProcessorAffinityMask = 0,

    [ValidateSet('Normal', 'AboveNormal', 'High')]
    [string]$StandalonePlayerPriorityClass = 'Normal',

    [ValidateSet('Local', 'Central')]
    [string]$LicenseReturnOwner = 'Local',

    [switch]$GenerateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PowerShell 7.4 introduced $PSNativeCommandUseErrorActionPreference (stabilizing
# the native-error experimental feature). Its default is $false on current builds,
# so `& <native>` does NOT throw on a non-zero exit and our explicit checks run as
# written. However, a host profile or a future/different build could enable it,
# which would make `& <native>` THROW on a non-zero exit BEFORE our explicit
# `$LASTEXITCODE` check runs -- short-circuiting Invoke-UnityEditor's exit-code
# diagnostic and making the best-effort license return rely on its catch block
# instead of finishing. Pinning it $false makes LASTEXITCODE-based handling
# authoritative and identical across hosts/versions. (PS 5.1 lacks this variable;
# assigning it there is harmless, and the assignment is StrictMode-safe.)
$PSNativeCommandUseErrorActionPreference = $false

$PackageName = 'com.wallstop-studios.dxmessaging'
$TestFrameworkVersion = '1.4.5'
$PerformanceFrameworkVersion = '3.4.2'
# DxMessaging's own analyzer + source-generator assemblies. These MUST be present
# in the package's Runtime/Analyzers/; the harness just sanity-checks they ship
# there (see Assert-DxMessagingAnalyzerDllsPresent). The generator + analyzer apply
# NATIVELY: Unity scopes the Runtime/Analyzers/ RoslynAnalyzer-labeled DLLs to the
# runtime assembly and EVERYTHING that references it (the test assemblies + the
# predefined Assembly-CSharp), so the generator runs at the first compile with no
# in-project analyzer copy or -a:csc.rsp entry. Roslyn and System.Collections.Immutable
# are PrivateAssets build references; every supported Unity compiler host supplies their
# runtime assemblies, so the package ships no private compiler-support DLLs.
$RequiredDxMessagingAnalyzerDllNames = @(
    'WallstopStudios.DxMessaging.SourceGenerators.dll',
    'WallstopStudios.DxMessaging.Analyzer.dll'
)
# Typed emissions the shipping player repeats after its correctness phases to
# record a diagnostic ns/op. The generated player embeds these constants and
# Test-ShippingStartupTimings requires the exact delivered count. The warm-up
# batch is discarded before the clock is sampled.
$ShippingDispatchLoopIterations = 1000000
$ShippingDispatchLoopWarmupIterations = 10000

function Get-ShippingDispatchLoopShape {
    # The one message type the timed loop emits. Both the generated player and
    # the result validator read it from here, so neither can drift from the
    # other or from the ordering of the expected-shape inventory.
    param(
        [Parameter(Mandatory = $true)][ValidateSet('semantic', 'cardinality')][string]$Topology,
        [Parameter(Mandatory = $true)][ValidateSet(1, 16, 18, 256, 1000)][int]$MessageTypeCount
    )

    if ($Topology -ceq 'cardinality') {
        return 'DxmShippingCardinalityMessage0001'
    }
    if ($MessageTypeCount -ne 18) {
        throw 'The semantic shipping topology requires exactly 18 message types.'
    }
    return 'DxmShippingPublicUntargetedClass'
}
$CiRoslynatorAnalyzerFiles = @(
    @{ Name = 'Roslynator.CSharp.Analyzers.dll'; Sha256 = '3f104ae829826e063b36ea4c11df2fd595ae482ddf76c58c09530486e1ebf853'; Guid = '3661e954d1b7490b944b35cdb72a3665' }
    @{ Name = 'Roslynator_Analyzers_Roslynator.Common.dll'; Sha256 = '4b3133ce1d4f52e17e6b488a1b7e7eb3d768e4d705c50d3482f8ca65e91cc834'; Guid = '8ccb09443b614abbb68b8e4bc48fed63' }
    @{ Name = 'Roslynator_Analyzers_Roslynator.Core.dll'; Sha256 = 'bab462206bdb9653cc61f39b13b47042d82b8fcc189ab73eaf76452f2f369424'; Guid = 'f4fdc9dd29fa4da897893d2be89437d6' }
    @{ Name = 'Roslynator_Analyzers_Roslynator.CSharp.dll'; Sha256 = 'c69267920234e720e5c93f0eec218d522547edd1e67ec2e295f42c5a2b89de70'; Guid = 'bb49eda285bf4387a171485058f6ae80' }
)
$ProjectOwnershipMarkerName = '.dxmessaging-ci-project'
$ProjectOwnershipMarkerContent = 'com.wallstop-studios.dxmessaging unity ci ephemeral project'
$CacheOwnershipMarkerName = '.dxmessaging-ci-cache'
$CacheOwnershipMarkerContent = 'com.wallstop-studios.dxmessaging unity ci cache'
$script:UnityCacheRoot = ''

function Write-CiError {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::error::$Message"
}

function Write-CiNotice {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::notice::$Message"
}

function Write-CiWarning {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "::warning::$Message"
}

function Clear-NonFatalNativeExitCode {
    # GitHub Actions' pwsh wrapper exits with $LASTEXITCODE after a script returns.
    # Any native exit code that this script has already captured and deliberately
    # downgraded to non-fatal must be scrubbed, or cleanup noise can turn a valid
    # artifact-verified run red after the script reaches the end normally.
    param([Parameter(Mandatory = $true)][string]$Context)

    $global:LASTEXITCODE = 0
    Write-Verbose "Cleared non-fatal native exit code after $Context."
}

# SINGLE SOURCE OF TRUTH for the catastrophic-pattern list that both
# Write-UnityCatastrophicErrorAnnotations (new ::error:: annotation surface)
# AND Write-UnityResultFailureDiagnostics (older line-numbered selected-line
# printer) scan for. Each entry has:
#   Label    : human-readable label written into the GitHub group/error line
#   Pattern  : the Select-String pattern (regex when UseSimple=false, literal
#              substring when UseSimple=true)
#   UseSimple: whether to invoke Select-String -SimpleMatch (literal substring,
#              cheaper) or as a regex
# Keeping this at $script: scope keeps the array deterministic and shared
# even when callers run from inside a try/finally or a child function.
#
# Patterns covered:
#   - PrecompiledAssemblyException -- "Multiple precompiled assemblies with
#     the same name" (the analyzer-DLL duplicate that motivated this
#     diagnostic; the runtime auto-copy that caused it has been removed).
#   - CompilationFailedException -- generic compile-failure path.
#   - error CS\d+ -- compiler errors (CS0246, CS0103, CS0117, etc).
#   - warning CS8032 -- "An instance of analyzer cannot be created" (analyzer
#     failed to instantiate; same class of issue).
#   - error RCS/ROS -- Roslynator diagnostics promoted by -warnaserror.
#   - "forwarded to assembly 'UnityEngine.<X>Module'" (CS1069) -- a test/source
#     references an OPTIONAL engine module the minimal CI test project omits;
#     carries a remediation Hint (kept in sync with the copy in
#     .github/actions/verify-unity-results/action.yml).
$script:CatastrophicPatterns = @(
    @{ Label = 'PrecompiledAssemblyException'; Pattern = 'PrecompiledAssemblyException'; UseSimple = $true }
    @{ Label = 'CompilationFailedException'; Pattern = 'CompilationFailedException'; UseSimple = $true }
    @{ Label = 'Multiple precompiled assemblies with the same name'; Pattern = 'Multiple precompiled assemblies with the same name'; UseSimple = $true }
    @{ Label = 'error CS\d+'; Pattern = 'error CS\d+'; UseSimple = $false }
    @{ Label = 'warning CS8032'; Pattern = 'warning CS8032'; UseSimple = $false }
    @{ Label = 'error Roslynator diagnostic'; Pattern = 'error (?:RCS|ROS)\d+'; UseSimple = $false }
    @{ Label = 'Roslyn analyzer failure'; Pattern = '(?:error|warning) AD0001'; UseSimple = $false }
    @{
        Label = 'Optional engine module not in the minimal CI project (CS1069 forwarded type)'
        Pattern = 'forwarded to assembly .UnityEngine\.\w+Module'
        UseSimple = $false
        Hint = 'A Tests.* assembly references a type from an optional Unity engine module ' +
            'the minimal CI test project does not include. PREFER making the test module-free ' +
            '(e.g. use Transform, the always-present UnityEngine.CoreModule Component) instead ' +
            'of adding the module. Only declare the module in .github/comparison-packages.json ' +
            '(and .unity-test-project for local parity) if that module is itself under test.'
    }
)

# CLASS-OF-ISSUE DIAGNOSTIC: when Unity exits non-zero, the operator's next
# question is "WHY did Unity fail?". The most common silent-killer answers are
# catastrophic compile-time errors -- the editor exits before running tests at
# all, leaving no NUnit XML. Surface these patterns as `::error::` annotations
# directly from the runner script so they ALWAYS show up in both the runner log
# and GitHub's error summary, independent of whether the workflow-level verify
# step also runs. Reusable at top-level so additional call sites can adopt it.
# Patterns come from the single-source-of-truth $script:CatastrophicPatterns
# array above; see Write-UnityResultFailureDiagnostics for the second consumer.
function Write-UnityCatastrophicErrorAnnotations {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LogPath,
        [int]$MaxPerPattern = 5
    )

    if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        return
    }

    foreach ($entry in $script:CatastrophicPatterns) {
        try {
            if ($entry.UseSimple) {
                $hits = @(
                    Select-String -LiteralPath $LogPath -SimpleMatch -Pattern $entry.Pattern -ErrorAction SilentlyContinue |
                        Select-Object -First $MaxPerPattern
                )
            } else {
                $hits = @(
                    Select-String -LiteralPath $LogPath -Pattern $entry.Pattern -ErrorAction SilentlyContinue |
                        Select-Object -First $MaxPerPattern
                )
            }
        } catch {
            # Best-effort; never throw from a diagnostic helper -- the caller is
            # already in the middle of a throw path.
            continue
        }

        if ($hits.Count -lt 1) {
            continue
        }

        Write-Host "::group::Catastrophic pattern: $($entry.Label)"
        foreach ($hit in $hits) {
            $line = $hit.Line.Trim()
            Write-Host "::error::Pattern detected -- $($entry.Label):: $line"
            Write-Host "  $($hit.Path):$($hit.LineNumber): $line"
        }
        if ($entry.ContainsKey('Hint') -and $entry.Hint) {
            Write-Host "::error::Remediation -- $($entry.Hint)"
        }
        Write-Host "::endgroup::"
    }
}

function Test-UnityPackageManagerTransientFailure {
    param([string]$LogPath)

    if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        return $false
    }

    try {
        $logText = Get-Content -LiteralPath $LogPath -Raw
    } catch {
        return $false
    }

    if (-not $logText) {
        return $false
    }

    return (
        $logText -match 'Cancelled resolving packages' -or
        $logText -match 'Failed to resolve packages:\s+operation cancelled' -or
        $logText -match 'IPCStream \(Upm-[^)]+\): IPC stream failed to read'
    )
}

function Write-UnityPackageManagerTransientFailureWarnings {
    param(
        [string]$LogPath,
        [int]$MaxLines = 12
    )

    if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        return
    }

    $patterns = @(
        'Cancelled resolving packages',
        'Failed to resolve packages:\s+operation cancelled',
        'IPCStream \(Upm-[^)]+\): IPC stream failed to read'
    )

    try {
        $matches = @(
            Select-String -LiteralPath $LogPath -Pattern $patterns -ErrorAction SilentlyContinue |
                Select-Object -First $MaxLines
        )
    } catch {
        return
    }

    foreach ($match in $matches) {
        $line = ConvertTo-SingleLineDiagnostic -Text $match.Line
        Write-Host "::warning::Unity Package Manager transient package-resolution signal: $line"
    }
}

function Clear-UnityPackageManagerRetryState {
    param([Parameter(Mandatory = $true)][string]$Project)

    $packageCachePath = [System.IO.Path]::Combine($Project, 'Library', 'PackageCache')
    $packageManagerPath = [System.IO.Path]::Combine($Project, 'Library', 'PackageManager')
    $tempPath = Join-Path $Project 'Temp'
    $projectRetryPaths = @(
        $packageCachePath,
        $packageManagerPath,
        $tempPath
    )
    foreach ($projectRetryPath in $projectRetryPaths) {
        Assert-UnityProjectRetryPathSafe -Path $projectRetryPath -Project $Project
    }

    $retryCachePaths = @()
    foreach ($envName in @('UPM_CACHE_ROOT', 'UPM_NPM_CACHE_PATH')) {
        $value = [Environment]::GetEnvironmentVariable($envName)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $retryCachePaths += $value
        }
    }
    foreach ($retryCachePath in $retryCachePaths) {
        Assert-UnityCacheRetryPathSafe -Path $retryCachePath -CacheRoot $script:UnityCacheRoot
    }
    $paths = $projectRetryPaths + $retryCachePaths

    Write-Host "::group::Unity Package Manager retry cleanup"
    foreach ($path in ($paths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        try {
            if (Test-Path -LiteralPath $path) {
                Write-Host "Removing $path"
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            } else {
                Write-Host "Already absent: $path"
            }
            New-Item -ItemType Directory -Force -Path $path -ErrorAction Stop | Out-Null
        } catch {
            Write-Host "::warning::Could not clear Unity Package Manager retry path '${path}': $($_.Exception.Message)"
        }
    }
    Write-Host "::endgroup::"
}

function Write-UnityPackageManagerDiagnostics {
    param(
        [string]$Project,
        [string]$LogPath
    )

    Write-Host "::group::Unity Package Manager diagnostics"
    try {
        foreach ($envName in @('UPM_CACHE_ROOT', 'UPM_NPM_CACHE_PATH', 'UPM_GIT_LFS_CACHE_PATH')) {
            Write-Host "${envName}: $([Environment]::GetEnvironmentVariable($envName))"
        }

        if ($Project) {
            foreach ($relativePath in @('Packages/manifest.json', 'Packages/packages-lock.json')) {
                $file = [System.IO.Path]::Combine([string[]](@($Project) + ($relativePath -split '/')))
                if (Test-Path -LiteralPath $file -PathType Leaf) {
                    Write-Host "${relativePath}:"
                    Get-Content -LiteralPath $file -ErrorAction SilentlyContinue |
                        ForEach-Object { Write-Host "  $_" }
                } else {
                    Write-Host "${relativePath}: (missing)"
                }
            }

            $packageCache = [System.IO.Path]::Combine($Project, 'Library', 'PackageCache')
            Write-Host "Library PackageCache: $packageCache"
            if (Test-Path -LiteralPath $packageCache -PathType Container) {
                Get-ChildItem -LiteralPath $packageCache -Force -ErrorAction SilentlyContinue |
                    Sort-Object Name |
                    Select-Object -First 80 |
                    ForEach-Object {
                        $kind = if ($_.PSIsContainer) { 'dir ' } else { 'file' }
                        Write-Host ("  [{0}] {1}" -f $kind, $_.Name)
                    }
            } else {
                Write-Host "  (missing)"
            }
        }

        if ($LogPath -and (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
            Write-Host "Package Manager failure log hits:"
            Select-String -LiteralPath $LogPath -Pattern @(
                'IPCStream \(Upm-[^)]+\): IPC stream failed to read',
                'Failed to resolve packages',
                'Cancelled resolving packages'
            ) -ErrorAction SilentlyContinue |
                Select-Object -First 40 |
                ForEach-Object {
                    Write-Host ("  line {0}: {1}" -f $_.LineNumber, $_.Line.Trim())
                }
        }
    } catch {
        Write-Host "::warning::Could not collect Unity Package Manager diagnostics: $($_.Exception.Message)"
    }
    Write-Host "::endgroup::"
}

function Invoke-UnityEditorTestsWithPackageManagerRetry {
    param(
        [Parameter(Mandatory = $true)][string]$EditorPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$ResultsPath,
        [Parameter(Mandatory = $true)][string]$Project
    )

    $runExit = Invoke-UnityEditor `
        -EditorPath $EditorPath `
        -Arguments $Arguments `
        -Label $Label `
        -LogPath $LogPath

    if ((Test-Path -LiteralPath $ResultsPath -PathType Leaf) -or
        -not (Test-UnityPackageManagerTransientFailure -LogPath $LogPath)) {
        return $runExit
    }

    Write-CiWarning "Unity Package Manager canceled package resolution before NUnit results existed; clearing UPM state and retrying once."
    Write-UnityPackageManagerTransientFailureWarnings -LogPath $LogPath
    $firstAttemptLogPath = Join-Path (Split-Path -Parent $LogPath) ("{0}.first-attempt.log" -f [System.IO.Path]::GetFileNameWithoutExtension($LogPath))
    try {
        Copy-Item -LiteralPath $LogPath -Destination $firstAttemptLogPath -Force -ErrorAction Stop
        Write-CiNotice "Saved first failed Unity log before retry: $firstAttemptLogPath"
    } catch {
        Write-CiWarning "Could not preserve first failed Unity log before retry: $($_.Exception.Message)"
    }
    Clear-UnityPackageManagerRetryState -Project $Project

    if (Test-Path -LiteralPath $ResultsPath -PathType Leaf) {
        Remove-Item -LiteralPath $ResultsPath -Force
    }

    return Invoke-UnityEditor `
        -EditorPath $EditorPath `
        -Arguments $Arguments `
        -Label "$Label (retry 1 after UPM cancellation)" `
        -LogPath $LogPath
}

# Collapse any run of whitespace (including CR/LF) to a single space and trim, so
# a multi-line NUnit <failure>/<message> renders as ONE line. GitHub `::error::`
# annotations are single-line: an embedded newline silently truncates the
# annotation at the first line break, so the whole message must be flattened
# before it is emitted. Mirrors the `.Trim()` collapse the catastrophic-pattern
# scanner applies to each matched log line.
function ConvertTo-SingleLineDiagnostic {
    param([string]$Text)
    if (-not $Text) {
        return ''
    }
    return (($Text -replace '\s+', ' ').Trim())
}

# Holder for the ::stop-commands::<token> ... ::<token>:: fence token that wraps
# caller-controlled raw multi-line dumps (NUnit <message>/<stack-trace>). GitHub
# parses every stdout line for `::command::` directives; fencing the raw body
# disables that processing so an assertion message containing a line like
# `::error file=...::` or `::set-output name=x::` cannot inject a spurious
# workflow command. The token is NOT a fixed literal: a crafted message
# containing the exact `::<literal>::` close line could otherwise end the fence
# early and re-enable injection. Instead a FRESH random token is generated per
# enumeration via New-WorkflowCommandStopToken (mirroring GitHub's own
# @actions/core, which uses a random per-invocation delimiter) and the SAME
# value is used for the opening and closing fence lines. The matching fence in
# .github/actions/verify-unity-results/action.yml uses the same scheme.
$script:WorkflowCommandStopToken = $null

# Generate a fresh, unpredictable stop-commands fence token. A GUID 'N' form is
# 32 hex chars with no separators, so it can never collide with caller text and
# is regenerated each call so it is neither predictable nor committed.
function New-WorkflowCommandStopToken {
    return ('dxm-stop-commands-{0}' -f [guid]::NewGuid().ToString('N'))
}

# Resolve an NUnit test-case / test-suite node's display name using
# XmlElement.GetAttribute, which returns '' for an ABSENT attribute instead of
# THROWING under Set-StrictMode -Version Latest (the dynamic `$node.fullname`
# property accessor throws "The property 'fullname' cannot be found" when the
# attribute is missing, which would degrade the whole failed-test enumeration to
# a generic warning for any NUnit XML lacking a fullname). Prefers fullname, then
# name, then a final '(unnamed test)' fallback.
function Get-NUnitNodeFullName {
    param([Parameter(Mandatory = $true)]$Node)

    $fullName = $Node.GetAttribute('fullname')
    if (-not $fullName) {
        $fullName = $Node.GetAttribute('name')
    }
    if (-not $fullName) {
        $fullName = '(unnamed test)'
    }
    return $fullName
}

# DIAGNOSTIC: when a Unity test run reports failures, the operator's next question
# is "WHICH tests failed and WHY?". The aggregate `failed=N` count alone is not
# actionable -- a real 2021.3 PlayMode run failed 1 of 697 tests and the logs
# never named it. This best-effort helper enumerates each failed test from the
# NUnit3 results XML and emits BOTH:
#   - a single-line `::error::` GitHub annotation per failed test (label +
#     fullname + first line of the failure message), and
#   - a `::group::Failed test: <fullname>` ... `::endgroup::` console block with
#     the full multi-line message and stack trace.
# It NEVER throws (the caller is already on a throw path; a diagnostic error must
# not mask the real test failure) and follows the structure of the other
# best-effort scanners (Write-UnityCatastrophicErrorAnnotations /
# Write-UnityResultFailureDiagnostics).
#
# Two classes of failed node are enumerated:
#   (1) Failed leaf cases: //test-case[@result='Failed'] -- the ordinary
#       assertion failure.
#   (2) Failed suites that carry their OWN direct <failure> child:
#       //test-suite[@result='Failed'] with a direct <failure> element. This is
#       the OneTimeSetUp / OneTimeTearDown failure shape (e.g.
#       SuiteWallClockBudgetTest's [OneTimeTearDown] Assert.Fail) -- a suite can
#       carry its OWN teardown failure message EVEN WHEN it also has a failed
#       child case, so we report on the direct <failure> regardless of failed
#       descendants. The fullname de-dup keeps a suite distinct from its child
#       cases (suite fullname differs from case fullname), so this never
#       double-prints; an aggregate-only suite (no direct <failure>) is still
#       skipped because its failure is just the roll-up of the child cases.
# De-duplicated by fullname so the same logical node is never printed twice, and
# capped at the first $MaxFailures (a truncation notice is printed -- no silent
# cap). Attribute reads use XmlElement.GetAttribute (returns '' when absent,
# never throws) so a results.xml lacking a fullname/name attribute does NOT
# degrade the whole enumeration to a generic warning under Set-StrictMode.
function Write-SuiteWallClockSummary {
    <#
    .SYNOPSIS
        Lift the suite's own wall-clock line out of the Unity log and into the job summary.

    .DESCRIPTION
        Issue #410: a change added 78 seconds to the EditMode step on every editor
        leg and stayed green for two days, because nothing in CI looks at how long
        a step takes. `SuiteWallClockBudgetTest` already measures the suite and
        already warns past its soft budget, but only into the Unity log, which
        nobody reads on a green run.

        Printing the number it already has costs one regex per leg and needs no
        new script and no new workflow, which is what issue #410 asks for. It does
        not compare against history: that is the option the issue calls most at
        odds with the repository's tooling philosophy, and it is not taken here.
    #>
    [CmdletBinding()]
    param(
        [string]$LogPath,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        return
    }

    try {
        # The producing side formats with the invariant culture, so the decimal
        # separator here is always '.'.
        $pattern = 'DxMessaging suite wall clock:\s*([0-9.]+)s\s*\(soft budget\s*([0-9.]+)s,\s*hard budget\s*([0-9.]+)s'
        $match = @(
            Select-String -LiteralPath $LogPath -Pattern $pattern -ErrorAction SilentlyContinue |
                Select-Object -Last 1
        )
        if ($match.Count -lt 1) {
            return
        }

        $groups = $match[0].Matches[0].Groups
        $invariant = [System.Globalization.CultureInfo]::InvariantCulture
        $elapsed = 0.0
        $soft = 0.0
        if (-not [double]::TryParse($groups[1].Value, 'Float', $invariant, [ref]$elapsed)) { return }
        if (-not [double]::TryParse($groups[2].Value, 'Float', $invariant, [ref]$soft)) { return }

        $summaryPath = $env:GITHUB_STEP_SUMMARY
        if ($summaryPath) {
            # The header is looked up in the FILE, not held in a variable: the
            # workflow runs this script once per test mode, so each leg is its own
            # pwsh process and an in-process flag would print the header three
            # times per job.
            $header = '### Suite wall clock'
            $alreadyOpen = (Test-Path -LiteralPath $summaryPath -PathType Leaf) -and
                @(Select-String -LiteralPath $summaryPath -Pattern ([regex]::Escape($header)) `
                    -SimpleMatch:$false -ErrorAction SilentlyContinue).Count -gt 0
            if (-not $alreadyOpen) {
                Add-Content -LiteralPath $summaryPath -Value @(
                    $header,
                    '',
                    '| Leg | Elapsed | Soft budget | Hard budget |',
                    '| --- | ---: | ---: | ---: |'
                )
            }
            Add-Content -LiteralPath $summaryPath -Value ("| $Label | $($groups[1].Value)s | " +
                "$($groups[2].Value)s | $($groups[3].Value)s |")
        }

        if ($elapsed -gt $soft) {
            Write-Host ("::warning::${Label} suite wall clock $($groups[1].Value)s is over its " +
                "$($groups[2].Value)s soft budget (hard budget $($groups[3].Value)s). A step that " +
                "grew without breaching its ceiling is what issue #410 was raised for.")
        }
    } catch {
        # Best-effort reporting must never mask a real result.
        Write-Host "::warning::Could not read the suite wall clock for ${Label}: $($_.Exception.Message)"
    }
}

function Write-UnityFailedTestAnnotations {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Xml,
        [Parameter(Mandatory = $true)][string]$Label,
        [int]$MaxFailures = 50
    )

    try {
        $failedCases = @($Xml.SelectNodes("//test-case[@result='Failed']"))
        $failedSuites = @($Xml.SelectNodes("//test-suite[@result='Failed']"))

        # A failed suite is reported on its OWN merits whenever it carries a
        # direct <failure> child element. This captures the OneTimeSetUp /
        # OneTimeTearDown failure message even when the suite ALSO has a failed
        # descendant case (the teardown's own message would otherwise be lost).
        # An aggregate-only suite (no direct <failure>, just a roll-up of failed
        # children) is skipped. The fullname de-dup below keeps the suite
        # distinct from its child cases, so this never double-prints.
        $ownFailureSuites = @(
            foreach ($suite in $failedSuites) {
                $directFailure = $suite.SelectSingleNode('failure')
                if ($directFailure) {
                    $suite
                }
            }
        )

        $failedNodes = @($failedCases) + @($ownFailureSuites)
        if ($failedNodes.Count -lt 1) {
            return
        }

        # De-duplicate by fullname (fallback name) so the same logical test is
        # never printed twice.
        $seen = New-Object 'System.Collections.Generic.HashSet[string]'
        $uniqueNodes = New-Object 'System.Collections.Generic.List[object]'
        foreach ($node in $failedNodes) {
            $fullName = Get-NUnitNodeFullName -Node $node
            if ($seen.Add($fullName)) {
                $uniqueNodes.Add($node)
            }
        }

        $totalFailed = $uniqueNodes.Count
        $shown = @($uniqueNodes | Select-Object -First $MaxFailures)
        foreach ($node in $shown) {
            $fullName = Get-NUnitNodeFullName -Node $node

            $failureNode = $node.SelectSingleNode('failure')
            $message = ''
            $stackTrace = ''
            if ($failureNode) {
                $messageNode = $failureNode.SelectSingleNode('message')
                if ($messageNode) {
                    $message = $messageNode.InnerText
                }
                $stackNode = $failureNode.SelectSingleNode('stack-trace')
                if ($stackNode) {
                    $stackTrace = $stackNode.InnerText
                }
            }

            $firstMessageLine = ConvertTo-SingleLineDiagnostic -Text $message
            # The single-line ::error:: annotation stays OUTSIDE the fence so it
            # is still processed as a GitHub annotation. ConvertTo-SingleLineDiagnostic
            # already flattens it to one line, so an embedded `::error::`/`::set-output::`
            # token cannot start a NEW directive on its own line here.
            Write-Host "::error::${Label} failed test: $fullName -- $firstMessageLine"

            Write-Host "::group::Failed test: $fullName"
            # SECURITY: the raw NUnit <message>/<stack-trace> are caller-controlled
            # (an assertion message can contain ANY text). GitHub parses every
            # stdout line for `::command::` directives, so a message line like
            # `::error file=...::` or `::set-output name=x::` would inject a
            # spurious workflow command. Fence the raw multi-line dump with
            # ::stop-commands::<token> ... ::<token>:: so command processing is
            # disabled for the enclosed lines. The token is a FRESH random GUID
            # per dump (never a fixed literal) so a crafted message containing
            # the exact `::<literal>::` close line cannot end the fence early and
            # re-enable injection. The ::group::/::endgroup:: markers stay OUTSIDE
            # the fence so they are still processed.
            $script:WorkflowCommandStopToken = New-WorkflowCommandStopToken
            Write-Host "::stop-commands::$script:WorkflowCommandStopToken"
            if ($message) {
                Write-Host "Message:"
                Write-Host $message
            } else {
                Write-Host "Message: (none recorded)"
            }
            if ($stackTrace) {
                Write-Host "Stack trace:"
                Write-Host $stackTrace
            }
            Write-Host "::$script:WorkflowCommandStopToken::"
            Write-Host "::endgroup::"
        }

        if ($totalFailed -gt $shown.Count) {
            $omitted = $totalFailed - $shown.Count
            Write-CiNotice "${Label}: $omitted additional failed test(s) not shown (showing first $($shown.Count) of $totalFailed)."
        }
    } catch {
        # Best-effort; a diagnostic must never mask the real test failure.
        Write-Host "::warning::Could not enumerate failed tests for ${Label}: $($_.Exception.Message)"
    }
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $executionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Get-PathStringComparison {
    # This comparison gates recursive cleanup. Be conservative across default
    # Windows/macOS case-insensitive filesystems; false-positive rejections are safer
    # than missing a case-variant spelling of a protected directory.
    return [System.StringComparison]::OrdinalIgnoreCase
}

function ConvertTo-ComparableFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullPath = $fullPath.Replace(
        [System.IO.Path]::AltDirectorySeparatorChar,
        [System.IO.Path]::DirectorySeparatorChar
    )
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if (-not [string]::IsNullOrEmpty($root)) {
        $root = $root.Replace(
            [System.IO.Path]::AltDirectorySeparatorChar,
            [System.IO.Path]::DirectorySeparatorChar
        )
        $separator = [string][System.IO.Path]::DirectorySeparatorChar
        while (
            $fullPath.Length -gt $root.Length -and
            $fullPath.EndsWith($separator, [System.StringComparison]::Ordinal)
        ) {
            $fullPath = $fullPath.Substring(0, $fullPath.Length - 1)
        }
    }
    return $fullPath
}

function Test-IsPathEqual {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    return [string]::Equals(
        (ConvertTo-ComparableFullPath -Path $Left),
        (ConvertTo-ComparableFullPath -Path $Right),
        (Get-PathStringComparison)
    )
}

function Test-IsPathInsideDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $candidatePath = ConvertTo-ComparableFullPath -Path $Path
    $directoryPath = ConvertTo-ComparableFullPath -Path $Directory
    if ([string]::Equals($candidatePath, $directoryPath, (Get-PathStringComparison))) {
        return $false
    }

    $separator = [string][System.IO.Path]::DirectorySeparatorChar
    $directoryPrefix = if ($directoryPath.EndsWith($separator, [System.StringComparison]::Ordinal)) {
        $directoryPath
    } else {
        "$directoryPath$separator"
    }
    return $candidatePath.StartsWith($directoryPrefix, (Get-PathStringComparison))
}

function Test-IsFilesystemRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = ConvertTo-ComparableFullPath -Path $Path
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrEmpty($root)) {
        return $false
    }
    $root = ConvertTo-ComparableFullPath -Path $root
    return [string]::Equals($fullPath, $root, (Get-PathStringComparison))
}

function Test-IsReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    } catch [System.Management.Automation.ItemNotFoundException] {
        return $false
    }
    return (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Remove-OwnedUnityInputEntry {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        $isDirectoryReparsePoint = (
            $item.PSIsContainer -or
            (($item.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0)
        )
        if ($isDirectoryReparsePoint) {
            [System.IO.Directory]::Delete($item.FullName, $false)
        } else {
            [System.IO.File]::Delete($item.FullName)
        }
        return
    }
    if ($item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force)) {
            Remove-OwnedUnityInputEntry -Path $child.FullName
        }
        [System.IO.Directory]::Delete($item.FullName, $false)
        return
    }
    [System.IO.File]::Delete($item.FullName)
}

function Reset-OwnedUnityInputRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    Remove-OwnedUnityInputEntry -Path $Path
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    if (Test-IsReparsePoint -Path $Path) {
        throw "Owned Unity input root remained a reparse point after reset: '$Path'."
    }
}

function Test-PathContainsReparsePointBeforeBoundary {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BoundaryDirectory
    )

    $current = ConvertTo-ComparableFullPath -Path $Path
    $boundary = ConvertTo-ComparableFullPath -Path $BoundaryDirectory
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-IsPathEqual -Left $current -Right $boundary) {
            return $false
        }
        if (Test-IsReparsePoint -Path $current) {
            return $true
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrEmpty($parent) -or (Test-IsPathEqual -Left $parent -Right $current)) {
            return $false
        }
        $current = $parent
    }

    return $false
}

function Test-ProjectOwnershipMarker {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $markerPath = Join-Path $ProjectPath $ProjectOwnershipMarkerName
    if (
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
        (Test-IsReparsePoint -Path $markerPath)
    ) {
        return $false
    }

    try {
        $markerContent = Get-Content -LiteralPath $markerPath -Raw
    } catch {
        return $false
    }
    if ($null -eq $markerContent) {
        return $false
    }
    $markerContent = $markerContent.Trim()
    return [string]::Equals(
        $markerContent,
        $ProjectOwnershipMarkerContent,
        [System.StringComparison]::Ordinal
    )
}

function Write-ProjectOwnershipMarker {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $markerPath = Join-Path $ProjectPath $ProjectOwnershipMarkerName
    if (Test-IsReparsePoint -Path $markerPath) {
        throw "Refusing to write the Unity project ownership marker through a reparse point: '$markerPath'."
    }
    [System.IO.File]::WriteAllText(
        $markerPath,
        "$ProjectOwnershipMarkerContent`n",
        [System.Text.Encoding]::UTF8
    )
}

function Test-CacheOwnershipMarker {
    param([Parameter(Mandatory = $true)][string]$CachePath)

    $markerPath = Join-Path $CachePath $CacheOwnershipMarkerName
    if (
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
        (Test-IsReparsePoint -Path $markerPath)
    ) {
        return $false
    }

    try {
        $markerContent = Get-Content -LiteralPath $markerPath -Raw
    } catch {
        return $false
    }
    return $null -ne $markerContent -and [string]::Equals(
        $markerContent.Trim(),
        $CacheOwnershipMarkerContent,
        [System.StringComparison]::Ordinal
    )
}

function Write-CacheOwnershipMarker {
    param([Parameter(Mandatory = $true)][string]$CachePath)

    $markerPath = Join-Path $CachePath $CacheOwnershipMarkerName
    if (Test-IsReparsePoint -Path $markerPath) {
        throw "Refusing to write the Unity cache ownership marker through a reparse point: '$markerPath'."
    }
    [System.IO.File]::WriteAllText(
        $markerPath,
        "$CacheOwnershipMarkerContent`n",
        [System.Text.Encoding]::UTF8
    )
}

function Test-IsManagedUnityCiProjectPath {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string[]]$ManagedProjectRoots
    )

    foreach ($managedProjectRoot in $ManagedProjectRoots) {
        if (Test-IsPathInsideDirectory -Path $ProjectPath -Directory $managedProjectRoot) {
            return $true
        }
    }

    return $false
}

function Get-UnityCiProjectPathSafetyError {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactsPath,
        [Parameter(Mandatory = $true)][string[]]$ManagedProjectRoots
    )

    if (Test-IsFilesystemRoot -Path $ProjectPath) {
        return "Refusing to use ProjectPath '$ProjectPath' because it resolves to a filesystem root."
    }

    $reservedPaths = @(
        @{ Path = $RepoRoot; Label = 'repository root' },
        @{ Path = $ArtifactsPath; Label = 'artifacts directory' }
    ) + @(
        foreach ($managedProjectRoot in $ManagedProjectRoots) {
            @{ Path = $managedProjectRoot; Label = 'managed Unity project parent directory' }
        }
    )
    foreach ($reservedPath in $reservedPaths) {
        if (Test-IsPathEqual -Left $ProjectPath -Right $reservedPath.Path) {
            return "Refusing to use ProjectPath '$ProjectPath' because it resolves to the $($reservedPath.Label)."
        }
    }

    if (Test-IsPathInsideDirectory -Path $RepoRoot -Directory $ProjectPath) {
        return "Refusing to use ProjectPath '$ProjectPath' because it would write into a parent of the repository root '$RepoRoot'."
    }
    if (Test-IsPathInsideDirectory -Path $ArtifactsPath -Directory $ProjectPath) {
        return "Refusing to use ProjectPath '$ProjectPath' because it would write into a parent of the artifacts directory '$ArtifactsPath'."
    }
    if (Test-IsPathInsideDirectory -Path $ProjectPath -Directory $ArtifactsPath) {
        return "Refusing to use ProjectPath '$ProjectPath' because it would place the generated Unity project inside the uploaded artifacts directory '$ArtifactsPath'."
    }

    $isManagedProjectPath = Test-IsManagedUnityCiProjectPath `
        -ProjectPath $ProjectPath `
        -ManagedProjectRoots $ManagedProjectRoots
    if (
        $isManagedProjectPath -and
        (Test-PathContainsReparsePointBeforeBoundary -Path $ProjectPath -BoundaryDirectory $RepoRoot)
    ) {
        return "Refusing to use ProjectPath '$ProjectPath' because a symlink or reparse point appears between it and the repository root '$RepoRoot'."
    }
    if (
        (Test-IsPathInsideDirectory -Path $ProjectPath -Directory $RepoRoot) -and
        -not $isManagedProjectPath
    ) {
        $managedRoots = $ManagedProjectRoots -join "', '"
        return "Refusing to use ProjectPath '$ProjectPath' inside the repository. Repo-contained CI projects must live under '$managedRoots'."
    }

    if (
        (Test-Path -LiteralPath $ProjectPath -PathType Container) -and
        -not $isManagedProjectPath -and
        (Test-IsReparsePoint -Path $ProjectPath)
    ) {
        return "Refusing to use existing ProjectPath '$ProjectPath' because it is a symlink or reparse point."
    }

    if (
        (Test-Path -LiteralPath $ProjectPath -PathType Container) -and
        -not $isManagedProjectPath -and
        -not (Test-ProjectOwnershipMarker -ProjectPath $ProjectPath)
    ) {
        return "Refusing to use existing ProjectPath '$ProjectPath' because it is outside the managed Unity CI project area and lacks the ownership marker '$ProjectOwnershipMarkerName'. Choose a new empty ProjectPath or remove the directory manually."
    }

    return ''
}

function Get-UnityCiCachePathSafetyError {
    param(
        [Parameter(Mandatory = $true)][string]$CachePath,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactsPath,
        [Parameter(Mandatory = $true)][string[]]$ManagedCacheRoots
    )

    if (Test-IsFilesystemRoot -Path $CachePath) {
        return "Refusing to use CachePath '$CachePath' because it resolves to a filesystem root."
    }
    foreach ($reservedPath in @($RepoRoot, $ArtifactsPath) + $ManagedCacheRoots) {
        if (Test-IsPathEqual -Left $CachePath -Right $reservedPath) {
            return "Refusing to use CachePath '$CachePath' because it resolves to a protected parent directory."
        }
    }
    if (
        (Test-IsPathInsideDirectory -Path $RepoRoot -Directory $CachePath) -or
        (Test-IsPathInsideDirectory -Path $ArtifactsPath -Directory $CachePath)
    ) {
        return "Refusing to use CachePath '$CachePath' because it contains a protected repository or artifacts directory."
    }
    if (Test-IsPathInsideDirectory -Path $CachePath -Directory $ArtifactsPath) {
        return "Refusing to use CachePath '$CachePath' inside the uploaded artifacts directory."
    }

    $managedRoot = $null
    foreach ($candidateRoot in $ManagedCacheRoots) {
        if (Test-IsPathInsideDirectory -Path $CachePath -Directory $candidateRoot) {
            $managedRoot = $candidateRoot
            break
        }
    }
    if (
        (Test-IsPathInsideDirectory -Path $CachePath -Directory $RepoRoot) -and
        $null -eq $managedRoot
    ) {
        return "Refusing to use CachePath '$CachePath' outside the managed Unity cache area."
    }
    if (
        $null -ne $managedRoot -and
        (
            (Test-IsReparsePoint -Path $managedRoot) -or
            (Test-PathContainsReparsePointBeforeBoundary -Path $CachePath -BoundaryDirectory $managedRoot)
        )
    ) {
        return "Refusing to use CachePath '$CachePath' because a symlink or reparse point appears inside the managed cache area."
    }
    if (
        (Test-Path -LiteralPath $CachePath -PathType Container) -and
        $null -eq $managedRoot -and
        ((Test-IsReparsePoint -Path $CachePath) -or -not (Test-CacheOwnershipMarker -CachePath $CachePath))
    ) {
        return "Refusing to use existing CachePath '$CachePath' because it is not an owned DxMessaging CI cache. Choose a new empty CachePath or remove the directory manually."
    }
    return ''
}

function Assert-UnityCacheRetryPathSafe {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$CacheRoot
    )

    if (
        [string]::IsNullOrWhiteSpace($CacheRoot) -or
        -not (Test-CacheOwnershipMarker -CachePath $CacheRoot) -or
        (Test-IsReparsePoint -Path $CacheRoot)
    ) {
        throw "Refusing Unity package-cache cleanup because the cache root is not an owned directory."
    }

    $isExpectedChild = $false
    foreach ($childName in @('upm', 'npm')) {
        if (Test-IsPathEqual -Left $Path -Right (Join-Path $CacheRoot $childName)) {
            $isExpectedChild = $true
            break
        }
    }
    if (
        -not $isExpectedChild -or
        -not (Test-IsPathInsideDirectory -Path $Path -Directory $CacheRoot) -or
        (Test-PathContainsReparsePointBeforeBoundary -Path $Path -BoundaryDirectory $CacheRoot)
    ) {
        throw "Refusing Unity package-cache cleanup outside the owned cache root '$CacheRoot': '$Path'."
    }
}

function Assert-UnityProjectRetryPathSafe {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Project
    )

    if (
        -not (Test-ProjectOwnershipMarker -ProjectPath $Project) -or
        (Test-IsReparsePoint -Path $Project)
    ) {
        throw "Refusing Unity project retry cleanup because the project is not an owned directory."
    }

    $isExpectedChild = $false
    foreach ($expectedPath in @(
        [System.IO.Path]::Combine($Project, 'Library', 'PackageCache'),
        [System.IO.Path]::Combine($Project, 'Library', 'PackageManager'),
        (Join-Path $Project 'Temp')
    )) {
        if (Test-IsPathEqual -Left $Path -Right $expectedPath) {
            $isExpectedChild = $true
            break
        }
    }
    if (
        -not $isExpectedChild -or
        -not (Test-IsPathInsideDirectory -Path $Path -Directory $Project) -or
        (Test-PathContainsReparsePointBeforeBoundary -Path $Path -BoundaryDirectory $Project)
    ) {
        throw "Refusing Unity project retry cleanup outside the owned project '$Project': '$Path'."
    }
}

function Assert-RepoRoot {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath (Join-Path $Path 'package.json') -PathType Leaf)) {
        throw "Repo root '$Path' does not contain package.json."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Path 'Runtime') -PathType Container)) {
        throw "Repo root '$Path' does not contain Runtime/."
    }
}

function ConvertTo-UnityFileUriPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return ($Path -replace '\\', '/')
}

function Initialize-UnityCacheEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$Path
    )

    $cacheRoot = if ([string]::IsNullOrWhiteSpace($Path)) {
        [System.IO.Path]::Combine($Root, '.artifacts', 'unity', 'cache', $Version)
    } else {
        Resolve-FullPath -Path $Path
    }
    $managedCacheRoots = @([System.IO.Path]::Combine($Root, '.artifacts', 'unity', 'cache'))
    if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_WORKSPACE)) {
        $managedCacheRoots += [System.IO.Path]::Combine($env:RUNNER_WORKSPACE, 'dxm-c')
    }
    $cachePathSafetyError = Get-UnityCiCachePathSafetyError `
        -CachePath $cacheRoot `
        -RepoRoot $Root `
        -ArtifactsPath $ArtifactsPath `
        -ManagedCacheRoots $managedCacheRoots
    if (-not [string]::IsNullOrWhiteSpace($cachePathSafetyError)) {
        throw $cachePathSafetyError
    }

    $upmRoot = Join-Path $cacheRoot 'upm'
    $npmRoot = Join-Path $cacheRoot 'npm'
    $gitLfsRoot = Join-Path $cacheRoot 'git-lfs'
    $localUnityCaches = if ($env:LOCALAPPDATA) {
        [System.IO.Path]::Combine($env:LOCALAPPDATA, 'Unity', 'Caches')
    } else {
        [System.IO.Path]::Combine($cacheRoot, 'localappdata', 'Unity', 'Caches')
    }

    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
    Write-CacheOwnershipMarker -CachePath $cacheRoot
    $script:UnityCacheRoot = $cacheRoot
    foreach ($path in @($upmRoot, $npmRoot, $gitLfsRoot, $localUnityCaches)) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }

    $env:UPM_CACHE_ROOT = $upmRoot
    $env:UPM_NPM_CACHE_PATH = $npmRoot
    $env:UPM_GIT_LFS_CACHE_PATH = $gitLfsRoot
    $env:UPM_ENABLE_GIT_LFS_CACHE = 'true'

    Write-Host "::group::Unity cache environment"
    Write-Host "LOCALAPPDATA Unity caches: $localUnityCaches"
    Write-Host "UPM_CACHE_ROOT: $env:UPM_CACHE_ROOT"
    Write-Host "UPM_NPM_CACHE_PATH: $env:UPM_NPM_CACHE_PATH"
    Write-Host "UPM_GIT_LFS_CACHE_PATH: $env:UPM_GIT_LFS_CACHE_PATH"
    Write-Host "::endgroup::"
}

function Get-ComparisonPackages {
    param([Parameter(Mandatory = $true)][string]$Root)
    $path = Join-Path $Root '.github/comparison-packages.json'
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Comparison packages single source not found: $path"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function New-ManifestJson {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$IncludeComparisons,
        [switch]$IncludeIntegrations,
        [switch]$ShippingFidelity,
        [string]$RepoRoot
    )

    $packagePath = ConvertTo-UnityFileUriPath -Path $Root
    if ($ShippingFidelity) {
        if ($IncludeComparisons -or $IncludeIntegrations) {
            throw 'A shipping-fidelity project cannot include comparison or integration test packages.'
        }
        $dependencies = [ordered]@{
            $PackageName = "file:$packagePath"
        }
        $manifest = [ordered]@{
            dependencies = $dependencies
        }
    } else {
        $dependencies = [ordered]@{
            'com.unity.test-framework' = $TestFrameworkVersion
            'com.unity.test-framework.performance' = $PerformanceFrameworkVersion
            $PackageName = "file:$packagePath"
        }
        $manifest = [ordered]@{
            dependencies = $dependencies
            testables = @($PackageName)
        }
    }

    # Comparison legs install the benchmark dependencies and their required Unity
    # modules. EditMode correctness legs install the three optional DI providers so
    # the copied conditional sample bodies compile against real pinned packages.
    # Both sets come from the single source .github/comparison-packages.json.
    if ($IncludeComparisons -or $IncludeIntegrations) {
        if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
            throw "New-ManifestJson package inclusion requires -RepoRoot (the comparison-packages.json single source)."
        }
        $comparisons = Get-ComparisonPackages -Root $RepoRoot
        if ($IncludeIntegrations) {
            $integrationPackages = $comparisons.PSObject.Properties['integrationPackages']
            if (-not $integrationPackages) {
                throw "comparison-packages.json is missing integrationPackages; cannot compile conditional DI samples."
            }
            foreach ($pkg in $integrationPackages.Value.PSObject.Properties) {
                $dependencies[$pkg.Name] = $pkg.Value
            }
            $integrationBuiltInPackages = $comparisons.PSObject.Properties['integrationUnityBuiltInPackages']
            if (-not $integrationBuiltInPackages) {
                throw "comparison-packages.json is missing integrationUnityBuiltInPackages; cannot compile conditional DI samples."
            }
            foreach ($pkg in $integrationBuiltInPackages.Value.PSObject.Properties) {
                $dependencies[$pkg.Name] = $pkg.Value
            }
        }
        if ($IncludeComparisons) {
            foreach ($pkg in $comparisons.packages.PSObject.Properties) {
                $dependencies[$pkg.Name] = $pkg.Value
            }
            $builtInPackages = $comparisons.PSObject.Properties['unityBuiltInPackages']
            if (-not $builtInPackages) {
                throw "comparison-packages.json is missing unityBuiltInPackages; cannot generate the comparison manifest."
            }
            foreach ($pkg in $builtInPackages.Value.PSObject.Properties) {
                $dependencies[$pkg.Name] = $pkg.Value
            }
        }
        $reg = $comparisons.registry
        # Ordered so ConvertTo-Json emits name/url/scopes deterministically (matches
        # the committed local-parity manifest field order and keeps the CI-log diff
        # of the generated manifest stable run-to-run).
        $manifest['scopedRegistries'] = @(
            [ordered]@{
                name = $reg.name
                url = $reg.url
                scopes = @($reg.scopes)
            }
        )
    }

    return ($manifest | ConvertTo-Json -Depth 8)
}

function New-DiSampleAsmdef {
    @'
{
  "name": "DxmCi.Samples.DI",
  "rootNamespace": "DxMessaging.Samples.DI",
  "references": [
    "WallstopStudios.DxMessaging",
    "WallstopStudios.DxMessaging.Reflex",
    "WallstopStudios.DxMessaging.VContainer",
    "WallstopStudios.DxMessaging.Zenject",
    "Reflex",
    "VContainer",
    "VContainer.Unity",
    "Zenject"
  ],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [
    {
      "name": "com.gustavopsantos.reflex",
      "expression": "14.3.1",
      "define": "REFLEX_PRESENT"
    },
    {
      "name": "com.svermeulen.extenject",
      "expression": "9.2.0-stcf3",
      "define": "ZENJECT_PRESENT"
    },
    {
      "name": "jp.hadashikick.vcontainer",
      "expression": "1.19.0",
      "define": "VCONTAINER_PRESENT"
    }
  ],
  "noEngineReferences": false
}
'@
}

function Install-CiRoslynatorAnalyzer {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Project
    )

    $destinationDirectory = Join-Path $Project 'Assets'
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    $destinationPaths = New-Object System.Collections.Generic.List[string]
    foreach ($file in $CiRoslynatorAnalyzerFiles) {
        $sourcePath = [System.IO.Path]::Combine($Root, '.github', 'analyzers', $file.Name)
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Missing vendored Unity CI analyzer dependency: $sourcePath"
        }
        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sourceHash -ne $file.Sha256) {
            throw "Vendored Unity CI analyzer hash mismatch for $($file.Name): expected $($file.Sha256), got $sourceHash."
        }

        $destinationPath = Join-Path $destinationDirectory $file.Name
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
        $destinationPaths.Add($destinationPath)
        @"
fileFormatVersion: 2
guid: $($file.Guid)
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      : Any
    second:
      enabled: 0
      settings: {}
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  userData:
  assetBundleName:
  assetBundleVariant:
"@ | Set-Content -LiteralPath "$destinationPath.meta" -Encoding UTF8
    }

    # csc.rsp registers every assembly in the dependency closure because Roslyn's
    # command-line loader does not probe sibling DLLs. The DLLs deliberately have
    # no RoslynAnalyzer label: folder labels are assembly-scoped and would also
    # double-register predefined code.
    return @($destinationPaths)
}

function New-ConfiguratorSource {
    param(
        [string]$Backend = 'IL2CPP',
        [ValidateSet('Disabled', 'Minimal', 'Low', 'Medium', 'High')]
        [string]$ManagedStrippingLevel = 'Disabled',
        [string]$CanonicalProfileId = '',
        [string]$CanonicalProfileSha256 = ''
    )

    $stripEngineCodeDuringConfigure = if ($ManagedStrippingLevel -ceq 'Disabled') {
        'true'
    } else {
        'false'
    }

    # NOTE: this is a DOUBLE-quoted here-string so $Backend interpolates into the
    # generated C#. Every LITERAL C# dollar sign (the Debug.Log interpolated
    # string) is therefore backtick-escaped (`$). The LIVE code uses the
    # parameterized scripting backend (ScriptingImplementation.<Backend>), the
    # non-deprecated ApiCompatibilityLevel.NET_Standard (which targets .NET Standard
    # 2.1), CompilationPipeline.codeOptimization = Release, applies the selected
    # managed stripping level, and pins the IL2CPP C++ compiler configuration to
    # Release. This is an invariant of the generated configurator.
    @"
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public static class DxmCiTestConfigurator
{
    [Serializable]
    private sealed class ConfigurationEvidence
    {
        public int schemaVersion = 1;
        public string profileId = "$CanonicalProfileId";
        public string profileSha256 = "$CanonicalProfileSha256";
        public string evidenceKind = "configuration";
        public string unityVersion = Application.unityVersion;
        public ConfigurationValues values = new ConfigurationValues();
    }

    [Serializable]
    private sealed class ConfigurationValues
    {
        public string buildTarget;
        public string scriptingBackend;
        public string apiCompatibilityLevel;
        public string codeOptimization;
        public string il2cppCompilerConfiguration;
        public string il2cppCodeGeneration;
        public string managedStrippingLevel;
        public bool incrementalGc;
        public bool stripEngineCode;
    }

    [Serializable]
    private sealed class BuildOptionsEvidence
    {
        public int schemaVersion = 1;
        public string profileId = "$CanonicalProfileId";
        public string profileSha256 = "$CanonicalProfileSha256";
        public string evidenceKind = "buildOptions";
        public string unityVersion = Application.unityVersion;
        public BuildOptionsValues values = new BuildOptionsValues();
    }

    [Serializable]
    private sealed class BuildOptionsValues
    {
        public bool developmentBuild;
        public bool allowDebugging;
        public bool deepProfiling;
        public bool enableAssertions;
        public bool includeTestAssemblies;
        public bool autoRunPlayer;
        public bool connectToHost;
        public bool connectWithProfiler;
        public bool cleanBuildCache;
        public bool detailedBuildReport;
    }

    public static void Apply()
    {
        // Prove Release editor code optimization for every Unity CI leg. Set FIRST
        // so the effective value is logged below.
        UnityEditor.Compilation.CompilationPipeline.codeOptimization = UnityEditor.Compilation.CodeOptimization.Release;

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
        NamedBuildTarget standalone = NamedBuildTarget.Standalone;
        // The scripting backend is parameterized: the runner passes the IL2CPP or
        // the Mono backend for the Mono perf leg via -Backend.
        PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.$Backend);
        // Use the non-deprecated ApiCompatibilityLevel.NET_Standard (targets .NET
        // Standard 2.1). The deprecated 2.0 form and the non-existent 2.1 enum
        // member are intentionally NOT used.
        PlayerSettings.SetApiCompatibilityLevel(standalone, ApiCompatibilityLevel.NET_Standard);
        // Apply the reviewed profile's stripping level. Test players select
        // Disabled so their callbacks survive; shipping-fidelity cells select a
        // non-Disabled level.
        PlayerSettings.SetManagedStrippingLevel(standalone, ManagedStrippingLevel.$ManagedStrippingLevel);
        // Pin the IL2CPP C++ compiler configuration to Release explicitly. An
        // ephemeral CI project has no committed default for this setting, and
        // measured CI runs showed Debug's faster native compile is outweighed by a
        // much slower standalone test player. The correctness and published perf
        // legs both keep Release native code: it is faster end-to-end here and
        // matches shipped-player behavior. Harmless under Mono.
        PlayerSettings.SetIl2CppCompilerConfiguration(standalone, Il2CppCompilerConfiguration.Release);

        if (!string.IsNullOrEmpty("$CanonicalProfileId"))
        {
            PlayerSettings.gcIncremental = true;
            // Unity 2021.3 can omit UnityEngine.IMGUIModule while recompiling
            // editor-only package code when engine stripping is already enabled.
            // Keep it disabled while the shipping builder assembly loads; that
            // builder applies the reviewed value immediately before BuildPlayer.
            PlayerSettings.stripEngineCode = $stripEngineCodeDuringConfigure;
#if UNITY_2022_1_OR_NEWER
            PlayerSettings.SetIl2CppCodeGeneration(standalone, Il2CppCodeGeneration.OptimizeSpeed);
#else
            EditorUserBuildSettings.il2CppCodeGeneration = Il2CppCodeGeneration.OptimizeSpeed;
#endif
        }
#if UNITY_2022_1_OR_NEWER
        string il2CppCodeGeneration = PlayerSettings.GetIl2CppCodeGeneration(standalone).ToString();
#else
        string il2CppCodeGeneration = EditorUserBuildSettings.il2CppCodeGeneration.ToString();
#endif

        // Print the EFFECTIVE Unity config so the artifact log PROVES Mono/IL2CPP
        // + .NET Standard 2.1 + Release for this run.
        Debug.Log(`$"DXM perf config: backend={PlayerSettings.GetScriptingBackend(standalone)}, api={PlayerSettings.GetApiCompatibilityLevel(standalone)}, codeOpt={UnityEditor.Compilation.CompilationPipeline.codeOptimization}, il2cppConfig={PlayerSettings.GetIl2CppCompilerConfiguration(standalone)}, il2cppCodeGeneration={il2CppCodeGeneration}");

        string profilePath = Environment.GetEnvironmentVariable("DXM_CONFIGURED_PROFILE_PATH");
        WriteConfigurationEvidence(profilePath);

        // Write a success marker as the FINAL action so the runner can treat the
        // CONFIGURED PROJECT -- not Unity's process exit code -- as the source of
        // truth. Unity can crash in a BACKGROUND thread (for example the
        // DirectoryMonitor file-watcher's teardown) DURING shutdown, AFTER Apply()
        // has fully completed and the editor logged "Batchmode quit successfully
        // invoked"; that returns a crash exit code (0xC0000005 STATUS_ACCESS_VIOLATION)
        // for a run whose configuration work actually succeeded. A fresh marker
        // proves Apply() ran to completion regardless of the shutdown exit code. The
        // marker path is handed in via DXM_CONFIGURE_MARKER_PATH (mirrors how the
        // standalone build modifier receives DXM_PLAYER_BUILD_PATH).
        string markerPath = Environment.GetEnvironmentVariable("DXM_CONFIGURE_MARKER_PATH");
        if (!string.IsNullOrEmpty(markerPath))
        {
            string dir = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(markerPath, "DxmCiTestConfigurator.Apply completed");
        }
    }

    internal static void WriteConfigurationEvidence(string profilePath)
    {
        if (string.IsNullOrEmpty(profilePath))
        {
            return;
        }
        NamedBuildTarget standalone = NamedBuildTarget.Standalone;
#if UNITY_2022_1_OR_NEWER
        string il2CppCodeGeneration = PlayerSettings.GetIl2CppCodeGeneration(standalone).ToString();
#else
        string il2CppCodeGeneration = EditorUserBuildSettings.il2CppCodeGeneration.ToString();
#endif
        ConfigurationEvidence evidence = new ConfigurationEvidence();
        evidence.values.buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
        evidence.values.scriptingBackend = PlayerSettings.GetScriptingBackend(standalone).ToString();
        evidence.values.apiCompatibilityLevel = PlayerSettings.GetApiCompatibilityLevel(standalone).ToString();
        evidence.values.codeOptimization = UnityEditor.Compilation.CompilationPipeline.codeOptimization.ToString();
        evidence.values.il2cppCompilerConfiguration = PlayerSettings.GetIl2CppCompilerConfiguration(standalone).ToString();
        evidence.values.il2cppCodeGeneration = il2CppCodeGeneration;
        evidence.values.managedStrippingLevel = PlayerSettings.GetManagedStrippingLevel(standalone).ToString();
        evidence.values.incrementalGc = PlayerSettings.gcIncremental;
        evidence.values.stripEngineCode = PlayerSettings.stripEngineCode;
        WriteJson(profilePath, evidence);
    }

    internal static void WriteBuildOptionsEvidence(string profilePath, BuildOptions options)
    {
        if (string.IsNullOrEmpty(profilePath))
        {
            return;
        }
        BuildOptionsEvidence evidence = new BuildOptionsEvidence();
        evidence.values.developmentBuild = Has(options, BuildOptions.Development);
        evidence.values.allowDebugging = Has(options, BuildOptions.AllowDebugging);
        evidence.values.deepProfiling = Has(options, BuildOptions.EnableDeepProfilingSupport);
        evidence.values.enableAssertions = Has(options, BuildOptions.ForceEnableAssertions);
        evidence.values.includeTestAssemblies = Has(options, BuildOptions.IncludeTestAssemblies);
        evidence.values.autoRunPlayer = Has(options, BuildOptions.AutoRunPlayer);
        evidence.values.connectToHost = Has(options, BuildOptions.ConnectToHost);
        evidence.values.connectWithProfiler = Has(options, BuildOptions.ConnectWithProfiler);
        evidence.values.cleanBuildCache = Has(options, BuildOptions.CleanBuildCache);
        evidence.values.detailedBuildReport = Has(options, BuildOptions.DetailedBuildReport);
        WriteJson(profilePath, evidence);
    }

    private static bool Has(BuildOptions options, BuildOptions flag)
    {
        return (options & flag) == flag;
    }

    private static void WriteJson(string path, object value)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonUtility.ToJson(value, true));
    }
}
"@
}

# STANDALONE ONLY. The Editor-side type that severs the test player's outbound
# PlayerConnection/Profiler TCP dependency at build time AND makes the editor's
# `-runTests` build step terminate. Emitted into Assets/Editor/ of the standalone
# CI project by Initialize-EphemeralProject. It mirrors Unity's documented
# "Split build and run" example (vendored com.unity.test-framework
# TestPlayerBuildModifierAttribute.cs): ITestPlayerBuildModifier rewrites the
# BuildPlayerOptions, IPostBuildCleanup exits the editor after the build.
#
# CRITICAL: clearing BuildOptions.AutoRunPlayer ALONE is NOT enough. The CLI
# `-runTests` path registers Executer.ExitIfRunIsCompleted on
# EditorApplication.update, which returns early while TestRunnerApi.IsRunActive()
# is true; for a player run that flag clears only on the PlayerConnection
# runFinished message. With the player never launched the message never arrives,
# so the editor idles forever. The PostBuildCleanup exit (run AFTER the build via
# ExecutePostBuildCleanupMethods) is mandatory.
function New-StandaloneBuildModifierSource {
    param(
        [bool]$DevelopmentBuild = $false,
        [string]$CanonicalProfileId = ''
    )

    # The Development BuildOptions flag is opt-in only. Unity CI defaults to a true
    # Release/non-development player; the compatibility -ReleasePlayerBuild switch is
    # retained at the script boundary but Release is the unconditional contract.
    # CRITICAL: the Unity Test Framework's PlayerLauncher hands ModifyOptions a
    # BuildPlayerOptions that ALREADY carries BuildOptions.Development, so the
    # Release path must actively CLEAR the flag -- merely not adding it leaves the
    # player a development build (Debug.isDebugBuild=true; published runs reported
    # "x64 Debug" platform strings until this strip landed). Every OTHER option (clearing
    # AutoRunPlayer/ConnectToHost/ConnectWithProfiler, |= IncludeTestAssemblies, the
    # DXM_PLAYER_BUILD_PATH redirect, and the PostBuildCleanup exit) is REQUIRED for
    # the split-build test execution and is emitted unconditionally. This is a
    # DOUBLE-quoted here-string so $developmentOption interpolates; the generated C#
    # contains no other dollar signs or backticks, so nothing else needs escaping.
    $developmentOption = if ($DevelopmentBuild) { '        playerOptions.options |= BuildOptions.Development;' } else { '        playerOptions.options &= ~BuildOptions.Development;' }
    @"
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.TestTools;
using UnityEngine;
using UnityEngine.TestTools;

[assembly: TestPlayerBuildModifier(typeof(DxmCiStandaloneBuildModifier))]
[assembly: PostBuildCleanup(typeof(DxmCiStandaloneBuildModifier))]

// Mirrors the documented Unity "Split build and run" example. Clearing
// AutoRunPlayer alone is NOT enough: the CLI -runTests path registers
// Executer.ExitIfRunIsCompleted on EditorApplication.update, which returns early
// while TestRunnerApi.IsRunActive() is true; for a player run that flag only
// clears on the PlayerConnection runFinished message, which never arrives when
// the player is not launched. PostBuildCleanup is the framework's hook (run after
// the build) to exit the editor cleanly.
public sealed class DxmCiStandaloneBuildModifier : ITestPlayerBuildModifier, IPostBuildCleanup, IPostprocessBuildWithReport
{
    private static bool s_Armed;
    private static readonly EditorApplication.CallbackFunction s_Exit = () => EditorApplication.Exit(0);
    public int callbackOrder => 0;

    public BuildPlayerOptions ModifyOptions(BuildPlayerOptions playerOptions)
    {
        playerOptions.options &= ~BuildOptions.AutoRunPlayer;
        playerOptions.options &= ~BuildOptions.ConnectToHost;
        playerOptions.options &= ~BuildOptions.ConnectWithProfiler;
        playerOptions.options &= ~BuildOptions.AllowDebugging;
        playerOptions.options &= ~BuildOptions.EnableDeepProfilingSupport;
        playerOptions.options &= ~BuildOptions.ForceEnableAssertions;
        playerOptions.options |= BuildOptions.IncludeTestAssemblies;
        if (!string.IsNullOrEmpty("$CanonicalProfileId"))
        {
            playerOptions.options |= BuildOptions.CleanBuildCache;
            playerOptions.options |= BuildOptions.DetailedBuildReport;
        }
$developmentOption
        string outPath = Environment.GetEnvironmentVariable("DXM_PLAYER_BUILD_PATH");
        if (!string.IsNullOrEmpty(outPath))
        {
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            playerOptions.locationPathName = outPath;
        }
        DxmCiTestConfigurator.WriteConfigurationEvidence(
            Environment.GetEnvironmentVariable("DXM_PREBUILD_CONFIG_PROFILE_PATH"));
        return playerOptions;
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        DxmCiTestConfigurator.WriteConfigurationEvidence(
            Environment.GetEnvironmentVariable("DXM_POSTBUILD_CONFIG_PROFILE_PATH"));
        DxmCiTestConfigurator.WriteBuildOptionsEvidence(
            Environment.GetEnvironmentVariable("DXM_BUILD_OPTIONS_PROFILE_PATH"),
            report.summary.options);
    }

    public void Cleanup()
    {
        if (s_Armed)
        {
            return;
        }
        s_Armed = true;
        if (Environment.GetCommandLineArgs().Any(a => a == "-runTests"))
        {
            EditorApplication.update += s_Exit;
        }
    }
}
"@
}

# STANDALONE ONLY. The player-side [assembly:TestRunCallback] that REPLACES the
# editor's need to receive results over PlayerConnection/TCP. On RunFinished it
# serializes the NUnit result to NUnit-compatible XML (mirroring Unity's
# ResultsWriter.WriteResultsToXml) at the path from the -dxmTestResults <path>
# command-line arg, then Application.Quit(0 pass / 1 fail / 2 no-path / 3 write
# error). Emitted into Assets/DxmCiStandaloneTestCallback/ with its own .asmdef.
# [Preserve] keeps the type for IL2CPP.
#
# On the PLAYER, ITestResult.ResultState is a NUnit.Framework.Interfaces.ResultState
# OBJECT, so we call .ToString() (the editor adaptor does the same). The single
# results channel is -dxmTestResults; there is NO environment-variable fallback and
# NO per-user-data-folder silent-loss fallback.
function New-StandaloneTestCallbackSource {
    param(
        [string]$CanonicalProfileId = '',
        [string]$CanonicalProfileSha256 = ''
    )

    @"
using System;
using System.IO;
using System.Xml;
using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.TestRunner;

[assembly: TestRunCallback(typeof(DxmCiStandaloneTestCallback))]

[Preserve]
internal sealed class DxmCiStandaloneTestCallback : ITestRunCallback
{
    [Serializable]
    private sealed class RuntimeEvidence
    {
        public int schemaVersion = 1;
        public string profileId = "$CanonicalProfileId";
        public string profileSha256 = "$CanonicalProfileSha256";
        public string evidenceKind = "runtime";
        public string unityVersion = Application.unityVersion;
        public RuntimeValues values = new RuntimeValues();
    }

    [Serializable]
    private sealed class RuntimeValues
    {
        public bool debugBuild;
    }

    public void RunStarted(ITest testsToRun)
    {
    }

    public void TestStarted(ITest test)
    {
    }

    public void TestFinished(ITestResult result)
    {
    }

    public void RunFinished(ITestResult result)
    {
        string path = ResolveResultsPath();
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogError("DXM: standalone test player received no -dxmTestResults <path>; not writing results.");
            Application.Quit(2);
            return;
        }
        int exitCode;
        try
        {
            WriteRuntimeEvidence();
            WriteNUnitXml(result, path);
            exitCode = result.FailCount > 0 ? 1 : 0;
            int total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
            Debug.LogFormat(
                LogType.Log,
                LogOption.NoStacktrace,
                null,
                "DXM: wrote standalone results to {0} (total={1} passed={2} failed={3} skipped={4})",
                path,
                total,
                result.PassCount,
                result.FailCount,
                result.SkipCount);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            exitCode = 3;
        }
        Application.Quit(exitCode);
    }

    private static string ResolveArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string ResolveResultsPath()
    {
        return ResolveArgument("-dxmTestResults");
    }

    private static void WriteRuntimeEvidence()
    {
        string path = ResolveArgument("-dxmRuntimeProfile");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        RuntimeEvidence evidence = new RuntimeEvidence();
        evidence.values.debugBuild = Debug.isDebugBuild;
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonUtility.ToJson(evidence, true));
    }

    private static void WriteNUnitXml(ITestResult result, string filePath)
    {
        string dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        XmlWriterSettings settings = new XmlWriterSettings
        {
            Indent = true,
            NewLineOnAttributes = false
        };
        using (StreamWriter sw = File.CreateText(filePath))
        using (XmlWriter xw = XmlWriter.Create(sw, settings))
        {
            int total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
            TNode run = new TNode("test-run");
            run.AddAttribute("id", "2");
            run.AddAttribute("testcasecount", total.ToString());
            run.AddAttribute("result", result.ResultState.ToString());
            run.AddAttribute("total", total.ToString());
            run.AddAttribute("passed", result.PassCount.ToString());
            run.AddAttribute("failed", result.FailCount.ToString());
            run.AddAttribute("inconclusive", result.InconclusiveCount.ToString());
            run.AddAttribute("skipped", result.SkipCount.ToString());
            run.AddAttribute("asserts", result.AssertCount.ToString());
            run.AddAttribute("engine-version", "3.5.0.0");
            run.AddAttribute("clr-version", Environment.Version.ToString());
            run.AddAttribute("start-time", result.StartTime.ToString("u"));
            run.AddAttribute("end-time", result.EndTime.ToString("u"));
            run.AddAttribute("duration", result.Duration.ToString());
            run.ChildNodes.Add(result.ToXml(true));
            run.WriteTo(xw);
        }
    }
}
"@
}

# STANDALONE ONLY. The asmdef for the player-side test callback above. Referencing
# UnityEngine.TestRunner is MANDATORY: TestRunCallbackListener.GetAllCallbacks only
# scans assemblies that reference UnityEngine.TestRunner. overrideReferences +
# precompiledReferences=nunit.framework.dll gives the callback the NUnit types;
# defineConstraints UNITY_INCLUDE_TESTS keeps it out of non-test builds. This must
# be a PLAYER assembly (NOT under Assets/Editor/), so includePlatforms is empty.
function New-StandaloneTestCallbackAsmdef {
    @'
{
    "name": "DxmCiStandaloneTestCallback",
    "references": [
        "UnityEngine.TestRunner"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": true,
    "defineConstraints": [
        "UNITY_INCLUDE_TESTS"
    ]
}
'@
}

# SHIPPING ONLY. This is a real Assembly-CSharp consumer with no NUnit or Unity
# Test Framework references. It proves the package survives High managed
# stripping and that generated AOT roots support typed and untyped dispatch for
# public, private nested, class, and readonly-struct message shapes. A private
# manual message intentionally has no generated bridge and supplies the RED
# missing-root control from the same built binary.
function New-ShippingFidelityPlayerSource {
    param(
        [Parameter(Mandatory = $true)][string]$CanonicalProfileId,
        [Parameter(Mandatory = $true)][string]$CanonicalProfileSha256,
        [Parameter(Mandatory = $true)][ValidateSet('semantic', 'cardinality')][string]$ShippingTopology,
        [Parameter(Mandatory = $true)][ValidateSet(1, 16, 18, 256, 1000)][int]$ShippingMessageTypeCount
    )

    $shippingTopologyId = "$ShippingTopology-$ShippingMessageTypeCount-v1"
    # The block guarded by DXM_SHIPPING_SEMANTIC_TOPOLOGY only ever compiles
    # for the semantic topology, so it always names the semantic loop shape.
    $dispatchLoopShape = Get-ShippingDispatchLoopShape -Topology semantic -MessageTypeCount 18

    @"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DxMessaging.Core;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.MessageBus;
using DxMessaging.Core.Messages;
using UnityEngine;

#if DXM_SHIPPING_SEMANTIC_TOPOLOGY
[DxUntargetedMessage]
public sealed partial class DxmShippingPublicUntargetedClass
{
}

[DxUntargetedMessage]
public readonly partial struct DxmShippingPublicUntargetedStruct
{
}

[DxTargetedMessage]
public sealed partial class DxmShippingPublicTargetedClass
{
}

[DxTargetedMessage]
public readonly partial struct DxmShippingPublicTargetedStruct
{
}

[DxBroadcastMessage]
public sealed partial class DxmShippingPublicBroadcastClass
{
}

[DxBroadcastMessage]
public readonly partial struct DxmShippingPublicBroadcastStruct
{
}
#endif

public sealed partial class DxmShippingFidelityPlayer
{
    private const string PositiveMode = "positive";
    private const string MissingRootMode = "missing-root-mutant";
    private const string MissingRootMessageFragment = "no rooted dispatch bridge was registered";

#if DXM_SHIPPING_SEMANTIC_TOPOLOGY
    [DxUntargetedMessage]
    private readonly partial struct NestedUntargetedStruct
    {
    }

    [DxUntargetedMessage]
    private sealed partial class NestedUntargetedClass
    {
    }

    [DxTargetedMessage]
    private sealed partial class NestedTargetedClass
    {
    }

    [DxTargetedMessage]
    private readonly partial struct NestedTargetedStruct
    {
    }

    [DxBroadcastMessage]
    private readonly partial struct NestedBroadcastStruct
    {
    }

    [DxBroadcastMessage]
    private sealed partial class NestedBroadcastClass
    {
    }

    [DxUntargetedMessage]
    public sealed partial class PublicNestedUntargetedClass
    {
    }

    [DxUntargetedMessage]
    public readonly partial struct PublicNestedUntargetedStruct
    {
    }

    [DxTargetedMessage]
    public sealed partial class PublicNestedTargetedClass
    {
    }

    [DxTargetedMessage]
    public readonly partial struct PublicNestedTargetedStruct
    {
    }

    [DxBroadcastMessage]
    public sealed partial class PublicNestedBroadcastClass
    {
    }

    [DxBroadcastMessage]
    public readonly partial struct PublicNestedBroadcastStruct
    {
    }
#endif

    private sealed class MissingRootUntargetedMessage : IUntargetedMessage
    {
        public Type MessageType => typeof(MissingRootUntargetedMessage);
    }

    // Cold-start and first-touch diagnostics for one fresh clean-build player
    // launch. These are characterization values for the #506 protocol and never
    // enter the published benchmark baseline. Microsecond phases come from
    // Stopwatch.GetTimestamp deltas; engineStartToRunMs is the engine clock at
    // the first script entry point.
    //
    // Every phase starts at NotMeasured. The missing-root mutant runs none of
    // them, and reporting a real zero for work that never happened would be a
    // fabricated measurement. A measured phase is never negative.
    [Serializable]
    private sealed class StartupTimings
    {
        public double engineStartToRunMs = NotMeasured;
        public long stopwatchFrequency;
        public bool stopwatchIsHighResolution;
        public double busConstructionUs = NotMeasured;
        public double rootProbePhaseUs = NotMeasured;
        public double registrationPhaseUs = NotMeasured;
        public double firstTypedDispatchUs = NotMeasured;
        public int firstTypedDispatchCount = NotMeasuredCount;
        public double typedPhaseUs = NotMeasured;
        public double untypedPhaseUs = NotMeasured;
        public string dispatchLoopShape = string.Empty;
        public int dispatchLoopCount = NotMeasuredCount;
        public double dispatchLoopNsPerOp = NotMeasured;
        public double trimUs = NotMeasured;
        public double teardownUs = NotMeasured;
    }

    [Serializable]
    private sealed class ShippingResult
    {
        public int schemaVersion = 3;
        public string profileId = "$CanonicalProfileId";
        public string profileSha256 = "$CanonicalProfileSha256";
        public string topologyId = "$shippingTopologyId";
        public int messageTypeCount = $ShippingMessageTypeCount;
        public string unityVersion = Application.unityVersion;
        public string mode = string.Empty;
        public bool success;
        public bool unityIncludeTests;
        public int rootedUntypedProbeCount;
        public int typedDispatchCount;
        public int untypedDispatchCount;
        public string[] rootedUntypedShapes = new string[0];
        public string[] typedDispatchShapes = new string[0];
        public string[] untypedDispatchShapes = new string[0];
        public bool missingRootFailureObserved;
        public string failureType = string.Empty;
        public string failureMessage = string.Empty;
        public string[] loadedAssemblies = new string[0];
        public StartupTimings timings = new StartupTimings();
    }

    [Serializable]
    private sealed class RuntimeEvidence
    {
        public int schemaVersion = 1;
        public string profileId = "$CanonicalProfileId";
        public string profileSha256 = "$CanonicalProfileSha256";
        public string evidenceKind = "runtime";
        public string unityVersion = Application.unityVersion;
        public RuntimeValues values = new RuntimeValues();
    }

    [Serializable]
    private sealed class RuntimeValues
    {
        public bool debugBuild;
    }

    private const double NotMeasured = -1.0;
    private const int NotMeasuredCount = -1;
    private const int PhaseTyped = 0;
    private const int PhaseUntyped = 1;
    private const int PhaseFirstTyped = 2;
    private const int PhaseLoop = 3;
    private const int DispatchLoopIterations = $ShippingDispatchLoopIterations;
    private const int DispatchLoopWarmupIterations = $ShippingDispatchLoopWarmupIterations;

    private static int s_TypedDispatchCount;
    private static int s_UntypedDispatchCount;
    private static int s_FirstTypedDispatchCount;
    private static int s_LoopDispatchCount;
    private static int s_Phase;
    private static readonly List<string> TypedDispatchShapes = new List<string>($ShippingMessageTypeCount);
    private static readonly List<string> UntypedDispatchShapes = new List<string>($ShippingMessageTypeCount);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Run()
    {
        double engineStartToRunMs = Time.realtimeSinceStartupAsDouble * 1000.0;
        string resultPath = ResolveArgument("-dxmShippingResult");
        string runtimeProfilePath = ResolveArgument("-dxmRuntimeProfile");
        string mode = ResolveArgument("-dxmShippingMode") ?? PositiveMode;
        if (string.IsNullOrEmpty(resultPath) || string.IsNullOrEmpty(runtimeProfilePath))
        {
            Debug.LogError("DXM shipping player requires result and runtime-profile paths.");
            Application.Quit(2);
            return;
        }

        ShippingResult result = new ShippingResult { mode = mode };
        result.timings.engineStartToRunMs = engineStartToRunMs;
        result.timings.stopwatchFrequency = System.Diagnostics.Stopwatch.Frequency;
        result.timings.stopwatchIsHighResolution = System.Diagnostics.Stopwatch.IsHighResolution;
        try
        {
#if UNITY_INCLUDE_TESTS
            result.unityIncludeTests = true;
#endif
            if (result.unityIncludeTests)
            {
                throw new InvalidOperationException(
                    "Shipping player compiled with the forbidden UNITY_INCLUDE_TESTS define.");
            }
            result.loadedAssemblies = GetLoadedAssemblyNames();
            AssertNoTestAssemblies(result.loadedAssemblies);
            if (string.Equals(mode, PositiveMode, StringComparison.Ordinal))
            {
                RunPositiveSmoke(result);
            }
            else if (string.Equals(mode, MissingRootMode, StringComparison.Ordinal))
            {
                RunMissingRootMutant(result);
            }
            else
            {
                throw new InvalidOperationException("Unknown shipping-fidelity mode: " + mode);
            }
            result.success = true;
        }
        catch (Exception ex)
        {
            result.success = false;
            result.failureType = ex.GetType().FullName;
            result.failureMessage = ex.Message;
            Debug.LogException(ex);
        }

        try
        {
            WriteRuntimeEvidence(runtimeProfilePath);
            WriteJson(resultPath, result);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            Application.Quit(3);
            return;
        }

        Application.Quit(result.success ? 0 : 1);
    }

#if DXM_SHIPPING_SEMANTIC_TOPOLOGY
    private static void RunPositiveSmoke(ShippingResult result)
    {
        StartupTimings timings = result.timings;
        long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        MessageBus bus = new MessageBus();
        timings.busConstructionUs = ElapsedMicroseconds(phaseStart);
        InstanceId route = new InstanceId(0x5348_4950);

        DxmShippingPublicUntargetedClass publicUntargetedClass = new DxmShippingPublicUntargetedClass();
        DxmShippingPublicUntargetedStruct publicUntargetedStruct = default;
        DxmShippingPublicTargetedClass publicTargetedClass = new DxmShippingPublicTargetedClass();
        DxmShippingPublicTargetedStruct publicTargetedStruct = default;
        DxmShippingPublicBroadcastClass publicBroadcastClass = new DxmShippingPublicBroadcastClass();
        DxmShippingPublicBroadcastStruct publicBroadcastStruct = default;
        NestedUntargetedClass nestedUntargetedClass = new NestedUntargetedClass();
        NestedUntargetedStruct nestedUntargetedStruct = default;
        NestedTargetedClass nestedTargetedClass = new NestedTargetedClass();
        NestedTargetedStruct nestedTargetedStruct = default;
        NestedBroadcastClass nestedBroadcastClass = new NestedBroadcastClass();
        NestedBroadcastStruct nestedBroadcastStruct = default;
        PublicNestedUntargetedClass publicNestedUntargetedClass = new PublicNestedUntargetedClass();
        PublicNestedUntargetedStruct publicNestedUntargetedStruct = default;
        PublicNestedTargetedClass publicNestedTargetedClass = new PublicNestedTargetedClass();
        PublicNestedTargetedStruct publicNestedTargetedStruct = default;
        PublicNestedBroadcastClass publicNestedBroadcastClass = new PublicNestedBroadcastClass();
        PublicNestedBroadcastStruct publicNestedBroadcastStruct = default;

        // First dispatch each shape only through its interface. No typed register
        // or emit has had a chance to seed a bridge, so success proves the
        // generated RuntimeInitializeOnLoadMethod root survived stripping.
        long emissionIdBeforeRootProbe = bus.EmissionId;
        List<string> rootedUntypedShapes = new List<string>(18);
        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        ProbeUntargetedRoot(bus, publicUntargetedClass, rootedUntypedShapes);
        ProbeUntargetedRoot(bus, publicUntargetedStruct, rootedUntypedShapes);
        ProbeTargetedRoot(bus, route, publicTargetedClass, rootedUntypedShapes);
        ProbeTargetedRoot(bus, route, publicTargetedStruct, rootedUntypedShapes);
        ProbeBroadcastRoot(bus, route, publicBroadcastClass, rootedUntypedShapes);
        ProbeBroadcastRoot(bus, route, publicBroadcastStruct, rootedUntypedShapes);
        ProbeUntargetedRoot(bus, nestedUntargetedClass, rootedUntypedShapes);
        ProbeUntargetedRoot(bus, nestedUntargetedStruct, rootedUntypedShapes);
        ProbeTargetedRoot(bus, route, nestedTargetedClass, rootedUntypedShapes);
        ProbeTargetedRoot(bus, route, nestedTargetedStruct, rootedUntypedShapes);
        ProbeBroadcastRoot(bus, route, nestedBroadcastClass, rootedUntypedShapes);
        ProbeBroadcastRoot(bus, route, nestedBroadcastStruct, rootedUntypedShapes);
        ProbeUntargetedRoot(bus, publicNestedUntargetedClass, rootedUntypedShapes);
        ProbeUntargetedRoot(bus, publicNestedUntargetedStruct, rootedUntypedShapes);
        ProbeTargetedRoot(bus, route, publicNestedTargetedClass, rootedUntypedShapes);
        ProbeTargetedRoot(bus, route, publicNestedTargetedStruct, rootedUntypedShapes);
        ProbeBroadcastRoot(bus, route, publicNestedBroadcastClass, rootedUntypedShapes);
        ProbeBroadcastRoot(bus, route, publicNestedBroadcastStruct, rootedUntypedShapes);
        timings.rootProbePhaseUs = ElapsedMicroseconds(phaseStart);
        long rootedUntypedProbeCount = bus.EmissionId - emissionIdBeforeRootProbe;
        if (rootedUntypedProbeCount != 18 || rootedUntypedShapes.Count != 18)
        {
            throw new InvalidOperationException(
                "Shipping root probe did not execute all 18 first-untyped dispatches.");
        }
        result.rootedUntypedProbeCount = (int)rootedUntypedProbeCount;
        result.rootedUntypedShapes = rootedUntypedShapes.ToArray();

        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        MessageHandler handler = new MessageHandler(route, bus) { active = true };
        MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
        try
        {
            _ = token.RegisterUntargeted<DxmShippingPublicUntargetedClass>(HandlePublicUntargetedClass);
            _ = token.RegisterUntargeted<DxmShippingPublicUntargetedStruct>(HandlePublicUntargetedStruct);
            _ = token.RegisterTargeted<DxmShippingPublicTargetedClass>(route, HandlePublicTargetedClass);
            _ = token.RegisterTargeted<DxmShippingPublicTargetedStruct>(route, HandlePublicTargetedStruct);
            _ = token.RegisterBroadcast<DxmShippingPublicBroadcastClass>(route, HandlePublicBroadcastClass);
            _ = token.RegisterBroadcast<DxmShippingPublicBroadcastStruct>(route, HandlePublicBroadcastStruct);
            _ = token.RegisterUntargeted<NestedUntargetedClass>(HandleNestedUntargetedClass);
            _ = token.RegisterUntargeted<NestedUntargetedStruct>(HandleNestedUntargetedStruct);
            _ = token.RegisterTargeted<NestedTargetedClass>(route, HandleNestedTargetedClass);
            _ = token.RegisterTargeted<NestedTargetedStruct>(route, HandleNestedTargetedStruct);
            _ = token.RegisterBroadcast<NestedBroadcastClass>(route, HandleNestedBroadcastClass);
            _ = token.RegisterBroadcast<NestedBroadcastStruct>(route, HandleNestedBroadcastStruct);
            _ = token.RegisterUntargeted<PublicNestedUntargetedClass>(HandlePublicNestedUntargetedClass);
            _ = token.RegisterUntargeted<PublicNestedUntargetedStruct>(HandlePublicNestedUntargetedStruct);
            _ = token.RegisterTargeted<PublicNestedTargetedClass>(route, HandlePublicNestedTargetedClass);
            _ = token.RegisterTargeted<PublicNestedTargetedStruct>(route, HandlePublicNestedTargetedStruct);
            _ = token.RegisterBroadcast<PublicNestedBroadcastClass>(route, HandlePublicNestedBroadcastClass);
            _ = token.RegisterBroadcast<PublicNestedBroadcastStruct>(route, HandlePublicNestedBroadcastStruct);
            token.Enable();
            timings.registrationPhaseUs = ElapsedMicroseconds(phaseStart);

            s_TypedDispatchCount = 0;
            s_UntypedDispatchCount = 0;
            s_FirstTypedDispatchCount = 0;
            s_LoopDispatchCount = 0;
            TypedDispatchShapes.Clear();
            UntypedDispatchShapes.Clear();
            s_Phase = PhaseFirstTyped;
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bus.UntargetedBroadcast(ref publicUntargetedClass);
            timings.firstTypedDispatchUs = ElapsedMicroseconds(phaseStart);
            s_Phase = PhaseTyped;
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bus.UntargetedBroadcast(ref publicUntargetedClass);
            bus.UntargetedBroadcast(ref publicUntargetedStruct);
            bus.TargetedBroadcast(ref route, ref publicTargetedClass);
            bus.TargetedBroadcast(ref route, ref publicTargetedStruct);
            bus.SourcedBroadcast(ref route, ref publicBroadcastClass);
            bus.SourcedBroadcast(ref route, ref publicBroadcastStruct);
            bus.UntargetedBroadcast(ref nestedUntargetedClass);
            bus.UntargetedBroadcast(ref nestedUntargetedStruct);
            bus.TargetedBroadcast(ref route, ref nestedTargetedClass);
            bus.TargetedBroadcast(ref route, ref nestedTargetedStruct);
            bus.SourcedBroadcast(ref route, ref nestedBroadcastClass);
            bus.SourcedBroadcast(ref route, ref nestedBroadcastStruct);
            bus.UntargetedBroadcast(ref publicNestedUntargetedClass);
            bus.UntargetedBroadcast(ref publicNestedUntargetedStruct);
            bus.TargetedBroadcast(ref route, ref publicNestedTargetedClass);
            bus.TargetedBroadcast(ref route, ref publicNestedTargetedStruct);
            bus.SourcedBroadcast(ref route, ref publicNestedBroadcastClass);
            bus.SourcedBroadcast(ref route, ref publicNestedBroadcastStruct);
            timings.typedPhaseUs = ElapsedMicroseconds(phaseStart);

            s_Phase = PhaseUntyped;
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bus.UntypedUntargetedBroadcast(publicUntargetedClass);
            bus.UntypedUntargetedBroadcast(publicUntargetedStruct);
            bus.UntypedTargetedBroadcast(route, publicTargetedClass);
            bus.UntypedTargetedBroadcast(route, publicTargetedStruct);
            bus.UntypedSourcedBroadcast(route, publicBroadcastClass);
            bus.UntypedSourcedBroadcast(route, publicBroadcastStruct);
            bus.UntypedUntargetedBroadcast(nestedUntargetedClass);
            bus.UntypedUntargetedBroadcast(nestedUntargetedStruct);
            bus.UntypedTargetedBroadcast(route, nestedTargetedClass);
            bus.UntypedTargetedBroadcast(route, nestedTargetedStruct);
            bus.UntypedSourcedBroadcast(route, nestedBroadcastClass);
            bus.UntypedSourcedBroadcast(route, nestedBroadcastStruct);
            bus.UntypedUntargetedBroadcast(publicNestedUntargetedClass);
            bus.UntypedUntargetedBroadcast(publicNestedUntargetedStruct);
            bus.UntypedTargetedBroadcast(route, publicNestedTargetedClass);
            bus.UntypedTargetedBroadcast(route, publicNestedTargetedStruct);
            bus.UntypedSourcedBroadcast(route, publicNestedBroadcastClass);
            bus.UntypedSourcedBroadcast(route, publicNestedBroadcastStruct);
            timings.untypedPhaseUs = ElapsedMicroseconds(phaseStart);

            result.typedDispatchCount = s_TypedDispatchCount;
            result.untypedDispatchCount = s_UntypedDispatchCount;
            result.typedDispatchShapes = TypedDispatchShapes.ToArray();
            result.untypedDispatchShapes = UntypedDispatchShapes.ToArray();
            if (s_FirstTypedDispatchCount != 1 || s_TypedDispatchCount != 18 || s_UntypedDispatchCount != 18)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Shipping dispatch counts differ (firstTyped={0}, typed={1}, untyped={2}).",
                        s_FirstTypedDispatchCount,
                        s_TypedDispatchCount,
                        s_UntypedDispatchCount));
            }

            s_Phase = PhaseLoop;
            for (int i = 0; i < DispatchLoopWarmupIterations; i++)
            {
                bus.UntargetedBroadcast(ref publicUntargetedClass);
            }
            s_LoopDispatchCount = 0;
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < DispatchLoopIterations; i++)
            {
                bus.UntargetedBroadcast(ref publicUntargetedClass);
            }
            double loopMicroseconds = ElapsedMicroseconds(phaseStart);
            s_Phase = PhaseTyped;
            RecordDispatchLoop(timings, "$dispatchLoopShape", loopMicroseconds);
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = bus.Trim(true);
            timings.trimUs = ElapsedMicroseconds(phaseStart);
        }
        finally
        {
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            token.UnregisterAll();
            token.Dispose();
            handler.active = false;
            timings.teardownUs = ElapsedMicroseconds(phaseStart);
        }
    }
#endif

    private static void ProbeUntargetedRoot(
        MessageBus bus,
        IUntargetedMessage message,
        List<string> observedShapes)
    {
        bus.UntypedUntargetedBroadcast(message);
        observedShapes.Add(message.MessageType.Name);
    }

    private static void ProbeTargetedRoot(
        MessageBus bus,
        InstanceId target,
        ITargetedMessage message,
        List<string> observedShapes)
    {
        bus.UntypedTargetedBroadcast(target, message);
        observedShapes.Add(message.MessageType.Name);
    }

    private static void ProbeBroadcastRoot(
        MessageBus bus,
        InstanceId source,
        IBroadcastMessage message,
        List<string> observedShapes)
    {
        bus.UntypedSourcedBroadcast(source, message);
        observedShapes.Add(message.MessageType.Name);
    }

    private static void RunMissingRootMutant(ShippingResult result)
    {
        MessageBus bus = new MessageBus();
        try
        {
            bus.UntypedUntargetedBroadcast(new MissingRootUntargetedMessage());
        }
        catch (InvalidOperationException ex)
        {
            if (ex.Message.IndexOf(MissingRootMessageFragment, StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "The missing-root mutant threw the wrong InvalidOperationException.",
                    ex);
            }
            result.missingRootFailureObserved = true;
            return;
        }
        throw new InvalidOperationException(
            "The missing-root mutant dispatched without the required AOT bridge failure.");
    }

#if DXM_SHIPPING_SEMANTIC_TOPOLOGY
    private static void HandlePublicUntargetedClass(in DxmShippingPublicUntargetedClass message) => Count("DxmShippingPublicUntargetedClass");
    private static void HandlePublicUntargetedStruct(in DxmShippingPublicUntargetedStruct message) => Count("DxmShippingPublicUntargetedStruct");
    private static void HandlePublicTargetedClass(in DxmShippingPublicTargetedClass message) => Count("DxmShippingPublicTargetedClass");
    private static void HandlePublicTargetedStruct(in DxmShippingPublicTargetedStruct message) => Count("DxmShippingPublicTargetedStruct");
    private static void HandlePublicBroadcastClass(in DxmShippingPublicBroadcastClass message) => Count("DxmShippingPublicBroadcastClass");
    private static void HandlePublicBroadcastStruct(in DxmShippingPublicBroadcastStruct message) => Count("DxmShippingPublicBroadcastStruct");
    private static void HandleNestedUntargetedClass(in NestedUntargetedClass message) => Count("NestedUntargetedClass");
    private static void HandleNestedUntargetedStruct(in NestedUntargetedStruct message) => Count("NestedUntargetedStruct");
    private static void HandleNestedTargetedClass(in NestedTargetedClass message) => Count("NestedTargetedClass");
    private static void HandleNestedTargetedStruct(in NestedTargetedStruct message) => Count("NestedTargetedStruct");
    private static void HandleNestedBroadcastClass(in NestedBroadcastClass message) => Count("NestedBroadcastClass");
    private static void HandleNestedBroadcastStruct(in NestedBroadcastStruct message) => Count("NestedBroadcastStruct");
    private static void HandlePublicNestedUntargetedClass(in PublicNestedUntargetedClass message) => Count("PublicNestedUntargetedClass");
    private static void HandlePublicNestedUntargetedStruct(in PublicNestedUntargetedStruct message) => Count("PublicNestedUntargetedStruct");
    private static void HandlePublicNestedTargetedClass(in PublicNestedTargetedClass message) => Count("PublicNestedTargetedClass");
    private static void HandlePublicNestedTargetedStruct(in PublicNestedTargetedStruct message) => Count("PublicNestedTargetedStruct");
    private static void HandlePublicNestedBroadcastClass(in PublicNestedBroadcastClass message) => Count("PublicNestedBroadcastClass");
    private static void HandlePublicNestedBroadcastStruct(in PublicNestedBroadcastStruct message) => Count("PublicNestedBroadcastStruct");
#endif

    private static void Count(string shape)
    {
        switch (s_Phase)
        {
            case PhaseUntyped:
                s_UntypedDispatchCount++;
                UntypedDispatchShapes.Add(shape);
                break;
            case PhaseFirstTyped:
                s_FirstTypedDispatchCount++;
                break;
            case PhaseLoop:
                s_LoopDispatchCount++;
                break;
            default:
                s_TypedDispatchCount++;
                TypedDispatchShapes.Add(shape);
                break;
        }
    }

    private static double ElapsedMicroseconds(long startTimestamp)
    {
        long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp;
        return elapsedTicks * 1000000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static void RecordDispatchLoop(StartupTimings timings, string shape, double loopMicroseconds)
    {
        if (s_LoopDispatchCount != DispatchLoopIterations)
        {
            throw new InvalidOperationException(
                string.Format(
                    "Shipping warm dispatch delivered {0} of {1} emissions.",
                    s_LoopDispatchCount,
                    DispatchLoopIterations));
        }
        timings.firstTypedDispatchCount = s_FirstTypedDispatchCount;
        timings.dispatchLoopShape = shape;
        timings.dispatchLoopCount = s_LoopDispatchCount;
        timings.dispatchLoopNsPerOp = loopMicroseconds * 1000.0 / DispatchLoopIterations;
    }

    private static string[] GetLoadedAssemblyNames()
    {
        System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        List<string> names = new List<string>(assemblies.Length);
        for (int i = 0; i < assemblies.Length; i++)
        {
            names.Add(assemblies[i].GetName().Name);
        }
        string[] result = names.ToArray();
        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    private static void AssertNoTestAssemblies(string[] assemblyNames)
    {
        for (int i = 0; i < assemblyNames.Length; i++)
        {
            string name = assemblyNames[i];
            if (
                string.Equals(name, "nunit.framework", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("PerformanceTesting", StringComparison.OrdinalIgnoreCase) >= 0
                || name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf(".Tests.", StringComparison.OrdinalIgnoreCase) >= 0
                || name.StartsWith("DxmCiStandalone", StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    "Shipping player loaded forbidden test assembly: " + name);
            }
        }
    }

    private static string ResolveArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void WriteRuntimeEvidence(string path)
    {
        RuntimeEvidence evidence = new RuntimeEvidence();
        evidence.values.debugBuild = Debug.isDebugBuild;
        WriteJson(path, evidence);
    }

    private static void WriteJson(string path, ShippingResult value)
    {
        StringBuilder json = new StringBuilder(2048);
        json.Append('{');
        AppendProperty(json, "schemaVersion", value.schemaVersion.ToString(CultureInfo.InvariantCulture), false);
        AppendProperty(json, "profileId", QuoteJson(value.profileId), true);
        AppendProperty(json, "profileSha256", QuoteJson(value.profileSha256), true);
        AppendProperty(json, "topologyId", QuoteJson(value.topologyId), true);
        AppendProperty(json, "messageTypeCount", value.messageTypeCount.ToString(CultureInfo.InvariantCulture), true);
        AppendProperty(json, "unityVersion", QuoteJson(value.unityVersion), true);
        AppendProperty(json, "mode", QuoteJson(value.mode), true);
        AppendProperty(json, "success", JsonBoolean(value.success), true);
        AppendProperty(json, "unityIncludeTests", JsonBoolean(value.unityIncludeTests), true);
        AppendProperty(json, "rootedUntypedProbeCount", value.rootedUntypedProbeCount.ToString(CultureInfo.InvariantCulture), true);
        AppendProperty(json, "typedDispatchCount", value.typedDispatchCount.ToString(CultureInfo.InvariantCulture), true);
        AppendProperty(json, "untypedDispatchCount", value.untypedDispatchCount.ToString(CultureInfo.InvariantCulture), true);
        AppendProperty(json, "rootedUntypedShapes", SerializeStringArray(value.rootedUntypedShapes), true);
        AppendProperty(json, "typedDispatchShapes", SerializeStringArray(value.typedDispatchShapes), true);
        AppendProperty(json, "untypedDispatchShapes", SerializeStringArray(value.untypedDispatchShapes), true);
        AppendProperty(json, "missingRootFailureObserved", JsonBoolean(value.missingRootFailureObserved), true);
        AppendProperty(json, "failureType", QuoteJson(value.failureType), true);
        AppendProperty(json, "failureMessage", QuoteJson(value.failureMessage), true);
        AppendProperty(json, "loadedAssemblies", SerializeStringArray(value.loadedAssemblies), true);
        StartupTimings timings = value.timings;
        json.Append(',').Append(QuoteJson("timings")).Append(':').Append('{');
        AppendProperty(json, "engineStartToRunMs", JsonNumber(timings.engineStartToRunMs), false);
        AppendProperty(json, "stopwatchFrequency", timings.stopwatchFrequency.ToString(CultureInfo.InvariantCulture), true);
        AppendProperty(json, "stopwatchIsHighResolution", JsonBoolean(timings.stopwatchIsHighResolution), true);
        AppendProperty(json, "busConstructionUs", JsonNumber(timings.busConstructionUs), true);
        AppendProperty(json, "rootProbePhaseUs", JsonNumber(timings.rootProbePhaseUs), true);
        AppendProperty(json, "registrationPhaseUs", JsonNumber(timings.registrationPhaseUs), true);
        AppendProperty(json, "firstTypedDispatchUs", JsonNumber(timings.firstTypedDispatchUs), true);
        AppendProperty(json, "firstTypedDispatchCount", timings.firstTypedDispatchCount.ToString(CultureInfo.InvariantCulture), true);
        AppendProperty(json, "typedPhaseUs", JsonNumber(timings.typedPhaseUs), true);
        AppendProperty(json, "untypedPhaseUs", JsonNumber(timings.untypedPhaseUs), true);
        AppendProperty(json, "dispatchLoopShape", QuoteJson(timings.dispatchLoopShape), true);
        AppendProperty(json, "dispatchLoopCount", timings.dispatchLoopCount.ToString(CultureInfo.InvariantCulture), true);
        AppendProperty(json, "dispatchLoopNsPerOp", JsonNumber(timings.dispatchLoopNsPerOp), true);
        AppendProperty(json, "trimUs", JsonNumber(timings.trimUs), true);
        AppendProperty(json, "teardownUs", JsonNumber(timings.teardownUs), true);
        json.Append('}');
        json.Append('}');
        WriteJsonText(path, json.ToString());
    }

    private static string JsonNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidOperationException("Shipping timing values must be finite numbers.");
        }
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void WriteJson(string path, RuntimeEvidence value)
    {
        StringBuilder json = new StringBuilder(384);
        json.Append('{');
        AppendProperty(json, "schemaVersion", value.schemaVersion.ToString(CultureInfo.InvariantCulture), false);
        AppendProperty(json, "profileId", QuoteJson(value.profileId), true);
        AppendProperty(json, "profileSha256", QuoteJson(value.profileSha256), true);
        AppendProperty(json, "evidenceKind", QuoteJson(value.evidenceKind), true);
        AppendProperty(json, "unityVersion", QuoteJson(value.unityVersion), true);
        json.Append(',').Append(QuoteJson("values")).Append(':').Append('{');
        AppendProperty(json, "debugBuild", JsonBoolean(value.values.debugBuild), false);
        json.Append('}').Append('}');
        WriteJsonText(path, json.ToString());
    }

    private static void AppendProperty(
        StringBuilder json,
        string name,
        string encodedValue,
        bool prependComma)
    {
        if (prependComma)
        {
            json.Append(',');
        }
        json.Append(QuoteJson(name)).Append(':').Append(encodedValue);
    }

    private static string SerializeStringArray(string[] values)
    {
        StringBuilder json = new StringBuilder();
        json.Append('[');
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }
            json.Append(QuoteJson(values[i]));
        }
        json.Append(']');
        return json.ToString();
    }

    private static string QuoteJson(string value)
    {
        if (value == null)
        {
            return "null";
        }
        StringBuilder json = new StringBuilder(value.Length + 2);
        json.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            switch (character)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (character < 0x20)
                    {
                        json.Append("\\u").Append(
                            ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        json.Append(character);
                    }
                    break;
            }
        }
        return json.Append('"').ToString();
    }

    private static string JsonBoolean(bool value)
    {
        return value ? "true" : "false";
    }

    private static void WriteJsonText(string path, string json)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, json);
    }
}
"@
}

# SHIPPING CARDINALITY ONLY. Generates exact closed public readonly-struct
# message inventories without changing the semantic 18-shape proof. Methods are
# split into deterministic batches so the 1,000-type cell does not depend on one
# oversized C# or IL2CPP method body.
function New-ShippingCardinalityTopologySource {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet(1, 16, 256, 1000)]
        [int]$MessageTypeCount
    )

    $dispatchLoopShape = Get-ShippingDispatchLoopShape `
        -Topology cardinality `
        -MessageTypeCount $MessageTypeCount
    $batchSize = 64
    $declarations = [System.Text.StringBuilder]::new()
    $probeCalls = [System.Collections.Generic.List[string]]::new()
    $registrationCalls = [System.Collections.Generic.List[string]]::new()
    $typedCalls = [System.Collections.Generic.List[string]]::new()
    $untypedCalls = [System.Collections.Generic.List[string]]::new()
    $batchMethods = [System.Text.StringBuilder]::new()
    $handlers = [System.Text.StringBuilder]::new()

    for ($messageIndex = 1; $messageIndex -le $MessageTypeCount; $messageIndex++) {
        $typeName = 'DxmShippingCardinalityMessage{0:D4}' -f $messageIndex
        $null = $declarations.AppendLine('[DxUntargetedMessage]')
        $null = $declarations.AppendLine("public readonly partial struct $typeName")
        $null = $declarations.AppendLine('{')
        $null = $declarations.AppendLine('}')
        $null = $declarations.AppendLine()
        $null = $handlers.AppendLine(
            "    private static void Handle$typeName(in $typeName message) => Count(`"$typeName`");"
        )
    }

    $batchCount = [int][Math]::Ceiling($MessageTypeCount / [double]$batchSize)
    for ($batchIndex = 0; $batchIndex -lt $batchCount; $batchIndex++) {
        $batchSuffix = '{0:D3}' -f $batchIndex
        $batchStart = ($batchIndex * $batchSize) + 1
        $batchEnd = [Math]::Min($batchStart + $batchSize - 1, $MessageTypeCount)
        $probeCalls.Add("        ProbeBatch$batchSuffix(bus, rootedUntypedShapes);")
        $registrationCalls.Add("        RegisterBatch$batchSuffix(token);")
        $typedCalls.Add("        TypedBatch$batchSuffix(bus);")
        $untypedCalls.Add("        UntypedBatch$batchSuffix(bus);")

        $null = $batchMethods.AppendLine(
            "    private static void ProbeBatch$batchSuffix(MessageBus bus, List<string> observedShapes)"
        )
        $null = $batchMethods.AppendLine('    {')
        for ($messageIndex = $batchStart; $messageIndex -le $batchEnd; $messageIndex++) {
            $typeName = 'DxmShippingCardinalityMessage{0:D4}' -f $messageIndex
            $variableName = 'message{0:D4}' -f $messageIndex
            $null = $batchMethods.AppendLine("        $typeName $variableName = default;")
            $null = $batchMethods.AppendLine(
                "        ProbeUntargetedRoot(bus, $variableName, observedShapes);"
            )
        }
        $null = $batchMethods.AppendLine('    }')
        $null = $batchMethods.AppendLine()

        $null = $batchMethods.AppendLine(
            "    private static void RegisterBatch$batchSuffix(MessageRegistrationToken token)"
        )
        $null = $batchMethods.AppendLine('    {')
        for ($messageIndex = $batchStart; $messageIndex -le $batchEnd; $messageIndex++) {
            $typeName = 'DxmShippingCardinalityMessage{0:D4}' -f $messageIndex
            $null = $batchMethods.AppendLine(
                "        _ = token.RegisterUntargeted<$typeName>(Handle$typeName);"
            )
        }
        $null = $batchMethods.AppendLine('    }')
        $null = $batchMethods.AppendLine()

        $null = $batchMethods.AppendLine("    private static void TypedBatch$batchSuffix(MessageBus bus)")
        $null = $batchMethods.AppendLine('    {')
        for ($messageIndex = $batchStart; $messageIndex -le $batchEnd; $messageIndex++) {
            $typeName = 'DxmShippingCardinalityMessage{0:D4}' -f $messageIndex
            $variableName = 'message{0:D4}' -f $messageIndex
            $null = $batchMethods.AppendLine("        $typeName $variableName = default;")
            $null = $batchMethods.AppendLine("        bus.UntargetedBroadcast(ref $variableName);")
        }
        $null = $batchMethods.AppendLine('    }')
        $null = $batchMethods.AppendLine()

        $null = $batchMethods.AppendLine("    private static void UntypedBatch$batchSuffix(MessageBus bus)")
        $null = $batchMethods.AppendLine('    {')
        for ($messageIndex = $batchStart; $messageIndex -le $batchEnd; $messageIndex++) {
            $typeName = 'DxmShippingCardinalityMessage{0:D4}' -f $messageIndex
            $variableName = 'message{0:D4}' -f $messageIndex
            $null = $batchMethods.AppendLine("        $typeName $variableName = default;")
            $null = $batchMethods.AppendLine("        bus.UntypedUntargetedBroadcast($variableName);")
        }
        $null = $batchMethods.AppendLine('    }')
        $null = $batchMethods.AppendLine()
    }

    @"
using System;
using System.Collections.Generic;
using DxMessaging.Core;
using DxMessaging.Core.Attributes;
using DxMessaging.Core.MessageBus;

$($declarations.ToString())public sealed partial class DxmShippingFidelityPlayer
{
    private static void RunPositiveSmoke(ShippingResult result)
    {
        StartupTimings timings = result.timings;
        long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        MessageBus bus = new MessageBus();
        timings.busConstructionUs = ElapsedMicroseconds(phaseStart);
        long emissionIdBeforeRootProbe = bus.EmissionId;
        List<string> rootedUntypedShapes = new List<string>($MessageTypeCount);
        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
$($probeCalls -join "`n")
        timings.rootProbePhaseUs = ElapsedMicroseconds(phaseStart);
        long rootedUntypedProbeCount = bus.EmissionId - emissionIdBeforeRootProbe;
        if (rootedUntypedProbeCount != $MessageTypeCount || rootedUntypedShapes.Count != $MessageTypeCount)
        {
            throw new InvalidOperationException(
                "Shipping cardinality root probe did not execute all $MessageTypeCount first-untyped dispatches.");
        }
        result.rootedUntypedProbeCount = (int)rootedUntypedProbeCount;
        result.rootedUntypedShapes = rootedUntypedShapes.ToArray();

        InstanceId route = new InstanceId(0x5348_4950);
        phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        MessageHandler handler = new MessageHandler(route, bus) { active = true };
        MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
        try
        {
$($registrationCalls -join "`n")
            token.Enable();
            timings.registrationPhaseUs = ElapsedMicroseconds(phaseStart);
            s_TypedDispatchCount = 0;
            s_UntypedDispatchCount = 0;
            s_FirstTypedDispatchCount = 0;
            s_LoopDispatchCount = 0;
            TypedDispatchShapes.Clear();
            UntypedDispatchShapes.Clear();
            DxmShippingCardinalityMessage0001 firstMessage = default;
            s_Phase = PhaseFirstTyped;
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bus.UntargetedBroadcast(ref firstMessage);
            timings.firstTypedDispatchUs = ElapsedMicroseconds(phaseStart);
            s_Phase = PhaseTyped;
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
$($typedCalls -join "`n")
            timings.typedPhaseUs = ElapsedMicroseconds(phaseStart);
            s_Phase = PhaseUntyped;
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
$($untypedCalls -join "`n")
            timings.untypedPhaseUs = ElapsedMicroseconds(phaseStart);
            result.typedDispatchCount = s_TypedDispatchCount;
            result.untypedDispatchCount = s_UntypedDispatchCount;
            result.typedDispatchShapes = TypedDispatchShapes.ToArray();
            result.untypedDispatchShapes = UntypedDispatchShapes.ToArray();
            if (s_FirstTypedDispatchCount != 1 || s_TypedDispatchCount != $MessageTypeCount || s_UntypedDispatchCount != $MessageTypeCount)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Shipping cardinality dispatch counts differ (firstTyped={0}, typed={1}, untyped={2}).",
                        s_FirstTypedDispatchCount,
                        s_TypedDispatchCount,
                        s_UntypedDispatchCount));
            }
            s_Phase = PhaseLoop;
            for (int i = 0; i < DispatchLoopWarmupIterations; i++)
            {
                bus.UntargetedBroadcast(ref firstMessage);
            }
            s_LoopDispatchCount = 0;
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < DispatchLoopIterations; i++)
            {
                bus.UntargetedBroadcast(ref firstMessage);
            }
            double loopMicroseconds = ElapsedMicroseconds(phaseStart);
            s_Phase = PhaseTyped;
            RecordDispatchLoop(timings, "$dispatchLoopShape", loopMicroseconds);
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = bus.Trim(true);
            timings.trimUs = ElapsedMicroseconds(phaseStart);
        }
        finally
        {
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            token.UnregisterAll();
            token.Dispose();
            handler.active = false;
            timings.teardownUs = ElapsedMicroseconds(phaseStart);
        }
    }

$($batchMethods.ToString())$($handlers.ToString())}
"@
}

# SHIPPING ONLY. Builds the Assembly-CSharp smoke consumer directly through
# BuildPipeline, without -runTests, IncludeTestAssemblies, TestRunCallback, or
# PlayerConnection. It records the exact final BuildOptions and the player
# assembly inventory used by Unity before writing a fresh completion marker.
function New-ShippingFidelityBuilderSource {
    param(
        [Parameter(Mandatory = $true)][string]$CanonicalProfileId,
        [Parameter(Mandatory = $true)][string]$CanonicalProfileSha256,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Minimal', 'Low', 'Medium', 'High')]
        [string]$ManagedStrippingLevel,
        [Parameter(Mandatory = $true)][ValidateSet('semantic', 'cardinality')][string]$ShippingTopology,
        [Parameter(Mandatory = $true)][ValidateSet(1, 16, 18, 256, 1000)][int]$ShippingMessageTypeCount
    )

    $shippingTopologyId = "$ShippingTopology-$ShippingMessageTypeCount-v1"

    @"
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class DxmShippingFidelityBuilder
{
    [Serializable]
    private sealed class AssemblyEvidence
    {
        public int schemaVersion = 1;
        public string profileId = "$CanonicalProfileId";
        public string profileSha256 = "$CanonicalProfileSha256";
        public string unityVersion = Application.unityVersion;
        public bool includeTestAssemblies;
        public string[] playerAssemblies = new string[0];
    }

    // Build-time and size facts from the same BuildReport that proves the final
    // options. buildDurationMs is a Stopwatch around BuildPipeline.BuildPlayer;
    // reportedTotalTimeMs and the step list come from Unity's own report. The
    // start and end stamps are Unix epoch milliseconds rather than ISO 8601
    // text. ConvertFrom-Json rehydrates an ISO 8601 string into a DateTime, so
    // the reader cannot validate a text stamp as a string, and the conversion
    // is not consistent across PowerShell versions. An integer reads back as an
    // integer on every host.
    [Serializable]
    private sealed class BuildStepEvidence
    {
        public string name = string.Empty;
        public int depth;
        public double durationMs;
    }

    [Serializable]
    private sealed class BuildReportEvidence
    {
        public int schemaVersion = 1;
        public string profileId = "$CanonicalProfileId";
        public string profileSha256 = "$CanonicalProfileSha256";
        public string topologyId = "$shippingTopologyId";
        public int messageTypeCount = $ShippingMessageTypeCount;
        public string unityVersion = Application.unityVersion;
        public string buildResult = string.Empty;
        public long buildStartedUnixMs;
        public long buildEndedUnixMs;
        public double buildDurationMs;
        public double reportedTotalTimeMs;
        public long reportedTotalSizeBytes;
        public BuildStepEvidence[] steps = new BuildStepEvidence[0];
    }

    public static void Build()
    {
        string outputPath = RequireEnvironmentVariable("DXM_PLAYER_BUILD_PATH");
        string markerPath = RequireEnvironmentVariable("DXM_SHIPPING_BUILD_MARKER_PATH");
        string buildReportPath = RequireEnvironmentVariable("DXM_SHIPPING_BUILD_REPORT_PATH");
        string scenePath = "Assets/DxmShippingFidelity.unity";
        string outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (!EditorSceneManager.SaveScene(scene, scenePath))
        {
            throw new InvalidOperationException("Could not save the shipping-fidelity scene.");
        }

        UnityEditor.Compilation.Assembly[] playerAssemblies =
            CompilationPipeline.GetAssemblies(AssembliesType.Player);
        string[] assemblyNames = GetAssemblyNames(playerAssemblies);
        AssertNoTestAssemblies(assemblyNames);

        ApplyShippingProfile();

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.CleanBuildCache | BuildOptions.DetailedBuildReport
        };
        options.options &= ~BuildOptions.Development;
        options.options &= ~BuildOptions.AllowDebugging;
        options.options &= ~BuildOptions.EnableDeepProfilingSupport;
        options.options &= ~BuildOptions.ForceEnableAssertions;
        options.options &= ~BuildOptions.IncludeTestAssemblies;
        options.options &= ~BuildOptions.AutoRunPlayer;
        options.options &= ~BuildOptions.ConnectToHost;
        options.options &= ~BuildOptions.ConnectWithProfiler;

        DxmCiTestConfigurator.WriteConfigurationEvidence(
            Environment.GetEnvironmentVariable("DXM_PREBUILD_CONFIG_PROFILE_PATH"));
        DateTime buildStartedUtc = DateTime.UtcNow;
        System.Diagnostics.Stopwatch buildStopwatch = System.Diagnostics.Stopwatch.StartNew();
        BuildReport report = BuildPipeline.BuildPlayer(options);
        buildStopwatch.Stop();
        DateTime buildEndedUtc = DateTime.UtcNow;
        DxmCiTestConfigurator.WriteConfigurationEvidence(
            Environment.GetEnvironmentVariable("DXM_POSTBUILD_CONFIG_PROFILE_PATH"));
        DxmCiTestConfigurator.WriteBuildOptionsEvidence(
            Environment.GetEnvironmentVariable("DXM_BUILD_OPTIONS_PROFILE_PATH"),
            report.summary.options);
        WriteBuildReportEvidence(
            buildReportPath,
            report,
            buildStartedUtc,
            buildEndedUtc,
            buildStopwatch.Elapsed.TotalMilliseconds);
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Shipping-fidelity build failed: " + report.summary.result);
        }

        WriteAssemblyEvidence(
            RequireEnvironmentVariable("DXM_SHIPPING_ASSEMBLY_EVIDENCE_PATH"),
            assemblyNames,
            (report.summary.options & BuildOptions.IncludeTestAssemblies)
                == BuildOptions.IncludeTestAssemblies);
        WriteMarker(markerPath);
    }

    private static void WriteBuildReportEvidence(
        string path,
        BuildReport report,
        DateTime buildStartedUtc,
        DateTime buildEndedUtc,
        double buildDurationMs)
    {
        BuildStep[] steps = report.steps ?? new BuildStep[0];
        BuildStepEvidence[] stepEvidence = new BuildStepEvidence[steps.Length];
        for (int i = 0; i < steps.Length; i++)
        {
            stepEvidence[i] = new BuildStepEvidence
            {
                name = steps[i].name ?? string.Empty,
                depth = steps[i].depth,
                durationMs = steps[i].duration.TotalMilliseconds
            };
        }
        BuildReportEvidence evidence = new BuildReportEvidence
        {
            buildResult = report.summary.result.ToString(),
            buildStartedUnixMs = ToUnixMilliseconds(buildStartedUtc),
            buildEndedUnixMs = ToUnixMilliseconds(buildEndedUtc),
            buildDurationMs = buildDurationMs,
            reportedTotalTimeMs = report.summary.totalTime.TotalMilliseconds,
            reportedTotalSizeBytes = (long)report.summary.totalSize,
            steps = stepEvidence
        };
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, JsonUtility.ToJson(evidence, true));
    }

    private static void ApplyShippingProfile()
    {
        NamedBuildTarget standalone = NamedBuildTarget.Standalone;
        UnityEditor.Compilation.CompilationPipeline.codeOptimization =
            UnityEditor.Compilation.CodeOptimization.Release;
        PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetApiCompatibilityLevel(standalone, ApiCompatibilityLevel.NET_Standard);
        PlayerSettings.SetManagedStrippingLevel(standalone, ManagedStrippingLevel.$ManagedStrippingLevel);
        PlayerSettings.SetIl2CppCompilerConfiguration(
            standalone,
            Il2CppCompilerConfiguration.Release);
        PlayerSettings.gcIncremental = true;
        PlayerSettings.stripEngineCode = true;
#if UNITY_2022_1_OR_NEWER
        PlayerSettings.SetIl2CppCodeGeneration(
            standalone,
            Il2CppCodeGeneration.OptimizeSpeed);
#else
        EditorUserBuildSettings.il2CppCodeGeneration = Il2CppCodeGeneration.OptimizeSpeed;
#endif
    }

    private static string[] GetAssemblyNames(UnityEditor.Compilation.Assembly[] assemblies)
    {
        List<string> names = new List<string>(assemblies.Length);
        for (int i = 0; i < assemblies.Length; i++)
        {
            names.Add(assemblies[i].name);
        }
        string[] result = names.ToArray();
        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    private static void AssertNoTestAssemblies(string[] assemblyNames)
    {
        for (int i = 0; i < assemblyNames.Length; i++)
        {
            string name = assemblyNames[i];
            if (
                string.Equals(name, "nunit.framework", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("PerformanceTesting", StringComparison.OrdinalIgnoreCase) >= 0
                || name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf(".Tests.", StringComparison.OrdinalIgnoreCase) >= 0
                || name.StartsWith("DxmCiStandalone", StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    "Shipping build selected forbidden test assembly: " + name);
            }
        }
        if (
            assemblyNames.Length != 2
            || !string.Equals(assemblyNames[0], "Assembly-CSharp", StringComparison.Ordinal)
            || !string.Equals(
                assemblyNames[1],
                "WallstopStudios.DxMessaging",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Shipping build selected an unexpected player assembly inventory: "
                + string.Join(",", assemblyNames));
        }
    }

    private static void WriteAssemblyEvidence(
        string path,
        string[] assemblyNames,
        bool includeTestAssemblies)
    {
        AssemblyEvidence evidence = new AssemblyEvidence
        {
            includeTestAssemblies = includeTestAssemblies,
            playerAssemblies = assemblyNames
        };
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, JsonUtility.ToJson(evidence, true));
    }

    private static long ToUnixMilliseconds(DateTime utc)
    {
        DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (long)(utc - epoch).TotalMilliseconds;
    }

    private static void WriteMarker(string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, "DxmShippingFidelityBuilder.Build completed");
    }

    private static string RequireEnvironmentVariable(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException("Missing required environment variable: " + name);
        }
        return value;
    }
}
"@
}

function Assert-DxMessagingAnalyzerDllsPresent {
    param([Parameter(Mandatory = $true)][string]$Root)

    $missingRequired = New-Object System.Collections.Generic.List[string]
    foreach ($dllName in $RequiredDxMessagingAnalyzerDllNames) {
        $sourcePath = [System.IO.Path]::Combine($Root, 'Runtime', 'Analyzers', $dllName)
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            $missingRequired.Add($sourcePath)
        }
    }

    if ($missingRequired.Count -gt 0) {
        throw "Missing required DxMessaging analyzer DLL(s) in Runtime/Analyzers:`n$($missingRequired.ToArray() -join "`n")"
    }
}

function Write-AnalyzerSetupDiagnostics {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [string]$LogPath,
        [Parameter(Mandatory = $true)][string]$Label
    )

    # The generator + analyzer now apply NATIVELY from the package's Runtime/Analyzers/
    # (Unity scopes the RoslynAnalyzer-labeled DLLs to the runtime assembly and
    # everything referencing it). There is no in-project analyzer copy or -a:csc.rsp
    # entry to inspect, so this is a best-effort log scan only: confirm the Unity compile log
    # mentions both analyzer DLLs, proving Unity passed them to csc from
    # Runtime/Analyzers/. No hard throw -- if the generator did NOT apply, the tests
    # themselves fail loudly with CS0315/CS0452. $Project is unused (kept so the 3 call
    # sites are unchanged).
    $logHasSourceGeneratorArg = $false
    $logHasAnalyzerArg = $false
    if ($LogPath -and (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        $logText = Get-Content -LiteralPath $LogPath -Raw
        $logHasSourceGeneratorArg = $logText -match 'WallstopStudios\.DxMessaging\.SourceGenerators\.dll'
        $logHasAnalyzerArg = $logText -match 'WallstopStudios\.DxMessaging\.Analyzer\.dll'
    }

    Write-Host "::group::DxMessaging analyzer setup diagnostics ($Label)"
    Write-Host "Unity compile log mentioned DxMessaging source-generator arg: $logHasSourceGeneratorArg"
    Write-Host "Unity compile log mentioned DxMessaging analyzer arg: $logHasAnalyzerArg"
    Write-Host "::endgroup::"
}

function Copy-SamplesForCompilation {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Project,
        [switch]$IncludeIntegrations
    )

    # Unity does not import package Samples~ until a consumer explicitly installs a
    # sample. Copy every sample compile input and runnable scene into the disposable
    # CI host so every supported editor version imports the exact shipped assets.
    # Preserve their metas because the scenes reference sample scripts by GUID. EditMode adds a
    # generated parent asmdef whose package version defines activate every conditional
    # DI sample against real provider assemblies; other legs leave those bodies off.
    $sampleSource = [System.IO.Path]::Combine($Root, 'Samples~')
    if (-not (Test-Path -LiteralPath $sampleSource -PathType Container)) {
        throw "Missing package samples source directory: $sampleSource"
    }
    # FileInfo.FullName expands Windows 8.3 path segments (for example RUNNER~1).
    # Normalize both roots through the provider before using length-based relative
    # paths, otherwise the expanded child path no longer shares the raw root prefix.
    $sampleSource = (Get-Item -LiteralPath $sampleSource).FullName
    $sampleDestination = (
        New-Item -ItemType Directory -Force -Path (
            [System.IO.Path]::Combine($Project, 'Assets', 'DxmCiSamples')
        )
    ).FullName
    $csharpInputs = @(Get-ChildItem -LiteralPath $sampleSource -Filter '*.cs' -File -Recurse)
    $asmdefInputs = @(Get-ChildItem -LiteralPath $sampleSource -Filter '*.asmdef' -File -Recurse)
    $sceneInputs = @(Get-ChildItem -LiteralPath $sampleSource -Filter '*.unity' -File -Recurse)
    if ($csharpInputs.Count -eq 0 -or $asmdefInputs.Count -eq 0 -or $sceneInputs.Count -eq 0) {
        throw "Package samples must contain at least one C# file, asmdef, and Unity scene."
    }
    $assetInputs = @($csharpInputs) + @($asmdefInputs) + @($sceneInputs)
    $metaInputs = @($assetInputs | ForEach-Object {
        $metaPath = "$($_.FullName).meta"
        if (-not (Test-Path -LiteralPath $metaPath -PathType Leaf)) {
            throw "Package sample input lacks its required meta file: $($_.FullName)"
        }
        Get-Item -LiteralPath $metaPath
    })
    $copiedInputs = $assetInputs + $metaInputs
    $copiedInputRelativePaths = @($copiedInputs | ForEach-Object {
        $_.FullName.Substring($sampleSource.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    })
    $generatedDiAsmdefRelativePath = [System.IO.Path]::Combine('DI', 'DxmCi.Samples.DI.asmdef')
    if ($IncludeIntegrations) {
        $copiedInputRelativePaths += $generatedDiAsmdefRelativePath
        $copiedInputRelativePaths += "$generatedDiAsmdefRelativePath.meta"
    }

    foreach ($existingFile in @(Get-ChildItem -LiteralPath $sampleDestination -File -Recurse)) {
        $existingRelativePath = $existingFile.FullName.Substring($sampleDestination.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        $isManagedSampleInput =
            $existingFile.Extension -eq '.cs' -or
            $existingFile.Extension -eq '.asmdef' -or
            $existingFile.Extension -eq '.unity' -or
            $existingFile.Name.EndsWith('.cs.meta', [System.StringComparison]::OrdinalIgnoreCase) -or
            $existingFile.Name.EndsWith('.asmdef.meta', [System.StringComparison]::OrdinalIgnoreCase) -or
            $existingFile.Name.EndsWith('.unity.meta', [System.StringComparison]::OrdinalIgnoreCase)
        if (
            $isManagedSampleInput -and
            $copiedInputRelativePaths -notcontains $existingRelativePath
        ) {
            Remove-Item -LiteralPath $existingFile.FullName -Force
        }
    }

    foreach ($sourceFile in $copiedInputs) {
        $relativePath = $sourceFile.FullName.Substring($sampleSource.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        $destinationPath = Join-Path $sampleDestination $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        $sourceContent = Get-Content -LiteralPath $sourceFile.FullName -Raw
        $needsWrite = -not (Test-Path -LiteralPath $destinationPath -PathType Leaf)
        if (-not $needsWrite) {
            $destinationContent = Get-Content -LiteralPath $destinationPath -Raw
            $needsWrite = $destinationContent -ne $sourceContent
        }
        if ($needsWrite) {
            [System.IO.File]::WriteAllText($destinationPath, $sourceContent)
        }
        if ((Get-Content -LiteralPath $destinationPath -Raw) -ne $sourceContent) {
            throw "Generated sample input differs from its source: $relativePath"
        }
    }
    if ($IncludeIntegrations) {
        $generatedDiAsmdefPath = Join-Path $sampleDestination $generatedDiAsmdefRelativePath
        $generatedDiAsmdef = New-DiSampleAsmdef
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $generatedDiAsmdefPath) | Out-Null
        if (
            -not (Test-Path -LiteralPath $generatedDiAsmdefPath -PathType Leaf) -or
            (Get-Content -LiteralPath $generatedDiAsmdefPath -Raw) -ne $generatedDiAsmdef
        ) {
            [System.IO.File]::WriteAllText($generatedDiAsmdefPath, $generatedDiAsmdef)
        }
    }
}

function Initialize-EphemeralProject {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Mode,
        [string]$Path,
        [switch]$IncludeComparisons,
        [string]$Backend = 'IL2CPP',
        [ValidateSet('Disabled', 'Minimal', 'Low', 'Medium', 'High')]
        [string]$ManagedStrippingLevel = 'Disabled',
        [bool]$DevelopmentBuild = $false,
        [string]$CanonicalProfileId = '',
        [string]$CanonicalProfileSha256 = '',
        [ValidateSet('semantic', 'cardinality')]
        [string]$ShippingTopology = 'semantic',
        [ValidateSet(1, 16, 18, 256, 1000)]
        [int]$ShippingMessageTypeCount = 18,
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactsPath
    )

    # The comparison-packages single source lives at the repo root. Default to
    # -Root when no explicit -RepoRoot is threaded (the package source root is the
    # repo root in this harness), so New-ManifestJson -IncludeComparisons can read it.
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = $Root
    }

    # `.artifacts/u` rather than the longer `.artifacts/unity/projects`, because this prefix is
    # charged against the Windows MAX_PATH budget for every file Unity resolves under
    # `<project>/Library/PackageCache`. A comparison package (Extenject) produced a 267-character
    # path -- seven over the 260 limit -- and Mono's System.IO enforces that limit regardless of
    # the OS long-path policy, so asset import died with a DirectoryNotFoundException before any
    # test ran. Shortening the one segment CI controls buys 14 characters. See issue #357.
    #
    # The two historical roots stay accepted so an existing generated project, or a caller
    # passing an explicit -ProjectPath under them, is still treated as managed rather than
    # rejected as an unmanaged repo-contained path.
    $managedProjectRoots = @(
        [System.IO.Path]::Combine($Root, '.artifacts', 'u'),
        [System.IO.Path]::Combine($Root, '.artifacts', 'unity', 'projects'),
        [System.IO.Path]::Combine($Root, '.artifacts', 'unity', 'game-ci-projects')
    )

    $project = if ($Path) {
        Resolve-FullPath -Path $Path
    } else {
        [System.IO.Path]::Combine($Root, '.artifacts', 'u', "$Version-$Mode")
    }
    $projectPathSafetyError = Get-UnityCiProjectPathSafetyError `
        -ProjectPath $project `
        -RepoRoot $RepoRoot `
        -ArtifactsPath $ArtifactsPath `
        -ManagedProjectRoots $managedProjectRoots
    if (-not [string]::IsNullOrWhiteSpace($projectPathSafetyError)) {
        throw $projectPathSafetyError
    }

    # MAX_PATH headroom, checked against the real path on the real runner rather than a
    # simulation. Unity resolves package files under
    # <project>/Library/PackageCache/<pkg>@<hash>/..., and Mono's System.IO enforces the
    # 260-character Windows limit regardless of the OS long-path policy, so a project path with
    # too little headroom kills asset import with a DirectoryNotFoundException raised from a
    # third-party asset postprocessor -- before a single test runs, and naming a file nobody
    # here wrote. Failing loudly at generation time is the difference between a five-minute
    # diagnosis and an afternoon. The constant is the longest path any current comparison
    # package contributes below the project root (Extenject's DeclareSignal binder chain,
    # measured at 171 characters). See issue #357.
    #
    # Scoped to -IncludeComparisons on purpose: that 171-character path belongs to Extenject,
    # which only exists in the project when the comparison packages are installed. A plain test
    # project has nothing remotely that deep, and applying the same budget to it would reject
    # legitimate short-lived projects under a long temp directory -- which is exactly what it did
    # to this harness's own generate-only tests on windows-latest.
    # `DirectorySeparatorChar`, not `$IsWindows`: this script declares `#Requires -Version 5.1`
    # and runs under `Set-StrictMode -Version Latest`, where touching the PowerShell 6+
    # `$IsWindows` automatic throws instead of returning false. `bootstrap-windows-runner.ps1`
    # documents the same choice for the same reason.
    $onWindowsHost = [System.IO.Path]::DirectorySeparatorChar -eq '\'
    $deepestKnownPackageRelativeLength = 171
    if ($IncludeComparisons -and $onWindowsHost -and (($project.Length + $deepestKnownPackageRelativeLength) -ge 260)) {
        throw ("Refusing to generate the Unity project at '$project' ($($project.Length) characters): " +
            "the deepest known package path would reach $($project.Length + $deepestKnownPackageRelativeLength) " +
            "characters, at or over the 260-character Windows MAX_PATH limit. Unity asset import would fail " +
            "with a DirectoryNotFoundException before any test ran. Shorten the project path (see issue #357).")
    }

    $includeIntegrations = $Mode -eq 'editmode'
    $isShippingFidelity = $Mode -eq 'shipping'
    New-Item -ItemType Directory -Force -Path $project | Out-Null
    Write-ProjectOwnershipMarker -ProjectPath $project
    if ($isShippingFidelity) {
        # Reset every authored root before creating any child path. The helper
        # unlinks reparse points without traversal and recursively checks real
        # directories, so a cached junction or symlink cannot redirect cleanup
        # outside the owned generated project.
        foreach ($shippingInputRootName in @('Assets', 'Packages', 'ProjectSettings', 'UserSettings')) {
            Reset-OwnedUnityInputRoot -Path (Join-Path $project $shippingInputRootName)
        }
        New-Item -ItemType Directory -Force -Path ([System.IO.Path]::Combine($project, 'Assets', 'Editor')) | Out-Null
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $project 'Packages') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $project 'ProjectSettings') | Out-Null
        New-Item -ItemType Directory -Force -Path ([System.IO.Path]::Combine($project, 'Assets', 'Editor')) | Out-Null
    }

    $shippingProjectInputRelativePaths = @()
    if ($isShippingFidelity) {
        $shippingProjectInputRelativePaths = @(
            'Assets/csc.rsp',
            'Assets/DxmShippingFidelityPlayer.cs',
            'Assets/Editor/DxmCiTestConfigurator.cs',
            'Assets/Editor/DxmShippingFidelityBuilder.cs',
            'Packages/manifest.json',
            'ProjectSettings/EditorSettings.asset',
            'ProjectSettings/ProjectVersion.txt'
        )
        if ($ShippingTopology -ceq 'cardinality') {
            $shippingProjectInputRelativePaths += 'Assets/DxmShippingCardinalityTopology.cs'
        }
    }
    New-ManifestJson -Root $Root -IncludeComparisons:$IncludeComparisons -IncludeIntegrations:$includeIntegrations -ShippingFidelity:$isShippingFidelity -RepoRoot $RepoRoot |
        Set-Content -LiteralPath ([System.IO.Path]::Combine($project, 'Packages', 'manifest.json')) -Encoding UTF8
    "m_EditorVersion: $Version`n" |
        Set-Content -LiteralPath ([System.IO.Path]::Combine($project, 'ProjectSettings', 'ProjectVersion.txt')) -Encoding UTF8
    # Disable enter-play-mode domain + scene reload (DisableDomainReload=1 |
    # DisableSceneReload=2 = 3) so the PlayMode test leg skips the per-entry
    # reload. The ephemeral project is generated minimal, so without this emit
    # Unity falls back to the slow default (both reloads ON). Production resets
    # its statics on play-mode entry via five [RuntimeInitializeOnLoadMethod(
    # SubsystemRegistration)] hooks and the tests reset via
    # DxMessagingStaticState.Reset() per test, so disabling the reload is safe
    # (verified red-green via the MCP loop). Inert for the editmode + standalone
    # legs (no in-editor play-mode entry). Written as a PARTIAL
    # EditorSettings.asset: Unity fills every unset field with its built-in
    # default, and carrying no serializedVersion means no pinned version can
    # mismatch across the 2021.3 / 2022.3 / 6000.x matrix (the matrix legs
    # validate it; the local MCP loop only exercises 6000.x). Written
    # unconditionally, like ProjectVersion.txt above: EditorSettings.asset lives
    # under ProjectSettings/, not Assets/, so a same-content rewrite does not
    # invalidate the AssetDatabase import cache.
    @'
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!159 &1
EditorSettings:
  m_EnterPlayModeOptionsEnabled: 1
  m_EnterPlayModeOptions: 3
'@ | Set-Content -LiteralPath ([System.IO.Path]::Combine($project, 'ProjectSettings', 'EditorSettings.asset')) -Encoding UTF8
    $cscOptions = @('-warnaserror', '-warn:9999')
    if ($isShippingFidelity) {
        $shippingDefine = if ($ShippingTopology -ceq 'semantic') {
            'DXM_SHIPPING_SEMANTIC_TOPOLOGY'
        } else {
            'DXM_SHIPPING_CARDINALITY_TOPOLOGY'
        }
        $cscOptions += "-define:$shippingDefine"
    }
    if ($includeIntegrations) {
        $ciAnalyzerPaths = @(Install-CiRoslynatorAnalyzer -Root $Root -Project $project)
        foreach ($ciAnalyzerPath in $ciAnalyzerPaths) {
            $cscOptions += "-analyzer:`"$ciAnalyzerPath`""
        }
    }
    $cscOptions | Set-Content -LiteralPath ([System.IO.Path]::Combine($project, 'Assets', 'csc.rsp')) -Encoding UTF8
    New-ConfiguratorSource -Backend $Backend -ManagedStrippingLevel $ManagedStrippingLevel -CanonicalProfileId $CanonicalProfileId -CanonicalProfileSha256 $CanonicalProfileSha256 |
        Set-Content -LiteralPath ([System.IO.Path]::Combine($project, 'Assets', 'Editor', 'DxmCiTestConfigurator.cs')) -Encoding UTF8
    if (-not $isShippingFidelity) {
        Copy-SamplesForCompilation -Root $Root -Project $project -IncludeIntegrations:$includeIntegrations
    }

    # The generator + analyzer ship under the package's Runtime/Analyzers/
    # (RoslynAnalyzer-labeled, every platform disabled), so Unity scopes them to the
    # test assemblies + the predefined Assembly-CSharp NATIVELY -- registered at the
    # first compile with no in-project analyzer copy and no -a:csc.rsp entry. This call only
    # sanity-checks that the package actually ships those two DLLs where Unity expects
    # them; the generator applies on its own once the file: package mounts.
    Assert-DxMessagingAnalyzerDllsPresent -Root $Root

    # STANDALONE ONLY: generate the split-build helpers that sever the test
    # player's PlayerConnection/TCP result streaming (the 10060 hang on multi-NIC
    # self-hosted runners). The Editor-side build modifier clears the player's
    # outbound-connection BuildOptions and exits the editor after the build; the
    # player-side TestRunCallback writes NUnit XML to -dxmTestResults and quits.
    # Written idempotently (only when missing or changed) so reruns against the
    # cached project do not needlessly invalidate Unity's import cache.
    # editmode/playmode never emit these files (the local single -runTests path is
    # untouched).
    if ($Mode -eq 'standalone') {
        $standaloneFiles = @(
            @{ Path = ([System.IO.Path]::Combine($project, 'Assets', 'Editor', 'DxmCiStandaloneBuildModifier.cs')); Content = (New-StandaloneBuildModifierSource -DevelopmentBuild $DevelopmentBuild -CanonicalProfileId $CanonicalProfileId) },
            @{ Path = ([System.IO.Path]::Combine($project, 'Assets', 'DxmCiStandaloneTestCallback', 'DxmCiStandaloneTestCallback.cs')); Content = (New-StandaloneTestCallbackSource -CanonicalProfileId $CanonicalProfileId -CanonicalProfileSha256 $CanonicalProfileSha256) },
            @{ Path = ([System.IO.Path]::Combine($project, 'Assets', 'DxmCiStandaloneTestCallback', 'DxmCiStandaloneTestCallback.asmdef')); Content = (New-StandaloneTestCallbackAsmdef) }
        )
        foreach ($file in $standaloneFiles) {
            $dir = Split-Path -Parent $file.Path
            if ($dir -and -not (Test-Path -LiteralPath $dir -PathType Container)) {
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
            }
            $needsWrite = -not (Test-Path -LiteralPath $file.Path -PathType Leaf)
            if (-not $needsWrite) {
                # Compare EOL-trailing-tolerantly: Set-Content appends a trailing
                # newline that the here-string content lacks, so a naive `-ne` would
                # rewrite on every run and needlessly bust Unity's import cache.
                $existing = Get-Content -LiteralPath $file.Path -Raw
                $needsWrite = ($existing.TrimEnd("`r", "`n") -ne $file.Content.TrimEnd("`r", "`n"))
            }
            if ($needsWrite) {
                Set-Content -LiteralPath $file.Path -Value $file.Content -Encoding UTF8
            }
        }
        Write-Host "::group::DxMessaging standalone split-build helpers"
        Write-Host "Generated the standalone build modifier + player TestRunCallback under $project (file-based results; no PlayerConnection)."
        foreach ($file in $standaloneFiles) {
            Write-Host "  $($file.Path)"
        }
        Write-Host "::endgroup::"
    } elseif ($isShippingFidelity) {
        if (
            [string]::IsNullOrWhiteSpace($CanonicalProfileId) -or
            [string]::IsNullOrWhiteSpace($CanonicalProfileSha256)
        ) {
            throw 'Shipping-fidelity generation requires a validated IL2CPP profile identity.'
        }
        $shippingFiles = @(
            @{ Path = ([System.IO.Path]::Combine($project, 'Assets', 'Editor', 'DxmShippingFidelityBuilder.cs')); Content = (New-ShippingFidelityBuilderSource -CanonicalProfileId $CanonicalProfileId -CanonicalProfileSha256 $CanonicalProfileSha256 -ManagedStrippingLevel $ManagedStrippingLevel -ShippingTopology $ShippingTopology -ShippingMessageTypeCount $ShippingMessageTypeCount) },
            @{ Path = ([System.IO.Path]::Combine($project, 'Assets', 'DxmShippingFidelityPlayer.cs')); Content = (New-ShippingFidelityPlayerSource -CanonicalProfileId $CanonicalProfileId -CanonicalProfileSha256 $CanonicalProfileSha256 -ShippingTopology $ShippingTopology -ShippingMessageTypeCount $ShippingMessageTypeCount) }
        )
        if ($ShippingTopology -ceq 'cardinality') {
            $shippingFiles += @{
                Path = [System.IO.Path]::Combine($project, 'Assets', 'DxmShippingCardinalityTopology.cs')
                Content = New-ShippingCardinalityTopologySource -MessageTypeCount $ShippingMessageTypeCount
            }
        }
        foreach ($file in $shippingFiles) {
            $dir = Split-Path -Parent $file.Path
            if ($dir -and -not (Test-Path -LiteralPath $dir -PathType Container)) {
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
            }
            $needsWrite = -not (Test-Path -LiteralPath $file.Path -PathType Leaf)
            if (-not $needsWrite) {
                $existing = Get-Content -LiteralPath $file.Path -Raw
                $needsWrite = ($existing.TrimEnd("`r", "`n") -ne $file.Content.TrimEnd("`r", "`n"))
            }
            if ($needsWrite) {
                Set-Content -LiteralPath $file.Path -Value $file.Content -Encoding UTF8
            }
        }
        Write-Host "::group::DxMessaging shipping-fidelity player"
        Write-Host "Generated the stripped Assembly-CSharp consumer and direct player builder under $project."
        foreach ($file in $shippingFiles) {
            Write-Host "  $($file.Path)"
        }
        Write-Host "::endgroup::"

        $observedProjectInputs = @(
            foreach ($shippingInputRootName in @('Assets', 'Packages', 'ProjectSettings', 'UserSettings')) {
                $shippingInputRoot = Join-Path $project $shippingInputRootName
                if (-not (Test-Path -LiteralPath $shippingInputRoot -PathType Container)) {
                    continue
                }
                Get-ChildItem -LiteralPath $shippingInputRoot -File -Recurse -Force |
                    Where-Object { -not $_.Name.EndsWith('.meta', [StringComparison]::Ordinal) } |
                    ForEach-Object {
                        $_.FullName.Substring($project.Length).TrimStart(
                            [System.IO.Path]::DirectorySeparatorChar,
                            [System.IO.Path]::AltDirectorySeparatorChar
                        ).Replace('\', '/')
                    }
            }
        )
        $unexpectedProjectInputs = @(
            $observedProjectInputs |
                Where-Object { $shippingProjectInputRelativePaths -cnotcontains $_ }
        )
        $missingProjectInputs = @(
            $shippingProjectInputRelativePaths |
                Where-Object { $observedProjectInputs -cnotcontains $_ }
        )
        if ($unexpectedProjectInputs.Count -gt 0 -or $missingProjectInputs.Count -gt 0) {
            throw "Shipping project inputs differ (missing=$($missingProjectInputs -join ','), unexpected=$($unexpectedProjectInputs -join ','))."
        }
        $projectInputEntries = New-Object System.Collections.Generic.List[object]
        foreach ($relativeInputPath in $shippingProjectInputRelativePaths) {
            $fullInputPath = Join-Path $project $relativeInputPath
            $projectInputEntries.Add([ordered]@{
                    path = $relativeInputPath
                    length = [long](Get-Item -LiteralPath $fullInputPath).Length
                    sha256 = (Get-FileHash -LiteralPath $fullInputPath -Algorithm SHA256).Hash.ToLowerInvariant()
                })
        }
        Write-JsonArtifact `
            -Path (Join-Path $ArtifactsPath 'shipping-project-inputs.json') `
            -Value ([ordered]@{
                schemaVersion = 2
                topologyId = "$ShippingTopology-$ShippingMessageTypeCount-v1"
                topologyKind = $ShippingTopology
                messageTypeCount = $ShippingMessageTypeCount
                expectedShapes = @(
                    Get-ExpectedShippingShapeNames `
                        -Topology $ShippingTopology `
                        -MessageTypeCount $ShippingMessageTypeCount
                )
                files = @($projectInputEntries.ToArray())
            })
    }

    return $project
}

function ConvertTo-NormalizedAcceleratorEndpoint {
    param([string]$Endpoint)

    # Pure: returns $null for empty input or a non-empty 'host:port' string;
    # THROWS with form-only diagnostics (never echoes the input value -- the
    # raw form is sensitive even if it just looks like a URL, and a future
    # secret-masking lapse must not exfiltrate it through our error text).
    if (-not $Endpoint -or $Endpoint.Trim().Length -eq 0) {
        return $null
    }

    $trimmed = $Endpoint.Trim()
    $hostPart = $null
    $portPart = 0

    # URL form: a scheme is present. [System.Uri]::TryCreate handles userinfo
    # stripping, path/query/fragment stripping, bracketed IPv6 hosts, and
    # explicit port extraction in one call. PS 5.1 compatible.
    if ($trimmed -match '^[a-zA-Z][a-zA-Z0-9+.\-]*://') {
        [System.Uri]$uri = $null
        # NOTE (leak-guard): the throw text below is form-only and intentionally
        # interpolates NO part of `$Endpoint`/`$trimmed`. The fourth normalizer
        # throw path (URL TryCreate failure) is therefore statically safe even
        # though it cannot be deterministically triggered from a unit test --
        # [System.Uri]::TryCreate is too permissive about most malformed URLs.
        if (-not [System.Uri]::TryCreate($trimmed, [System.UriKind]::Absolute, [ref]$uri)) {
            throw 'UNITY_ACCELERATOR_ENDPOINT could not be parsed as a URL form (scheme present, but not RFC 3986 well-formed). Expected host:port or scheme://host:port.'
        }
        # IsDefaultPort=TRUE means the URL OMITTED :port and the scheme's
        # default (e.g. 80/443 for http/https) was substituted -- both cases
        # are wrong for a Unity cache server, which needs an EXPLICIT port.
        # The `$uri.Port -lt 0` clause is belt-and-suspenders: on pwsh 7+ a
        # missing port yields Port == -1 AND IsDefaultPort == True, so the
        # -lt 0 check is subsumed -- it stays here as defense against a future
        # .NET runtime change that decouples the two flags.
        if ($uri.Port -lt 0 -or $uri.IsDefaultPort) {
            throw 'UNITY_ACCELERATOR_ENDPOINT URL is missing an explicit :port. Provide host:port or scheme://host:port.'
        }
        # `Uri.Host` returns `[::1]` (with brackets) on pwsh 7+ / .NET Core (the
        # CI runtime), and historically returned `::1` (no brackets) on PS 5.1 /
        # .NET Framework. The `StartsWith('[')` guard makes the assembled
        # 'host:port' string unambiguous on both runtimes; the production target
        # is pwsh 7+, so this is defense-in-depth against a future PS 5.1
        # backport.
        $hostPart = $uri.Host
        if ($uri.HostNameType -eq [System.UriHostNameType]::IPv6 -and -not $hostPart.StartsWith('[')) {
            $hostPart = "[$hostPart]"
        }
        $portPart = $uri.Port
    }
    else {
        # Bare host:port (canonical). Bracketed IPv6 first because the v4 /
        # hostname regex would mis-anchor on the closing bracket.
        #
        # LEAK GUARD: pre-validate the port digit length BEFORE the `[int]` cast.
        # The .NET Int32 overflow exception text echoes the offending value
        # verbatim ("Cannot convert value "99999999999" to type ...") which would
        # contradict the function's "never echoes the input" invariant. 5 digits
        # is the max legal port (65535); anything longer is automatically out of
        # range, so reject with the existing form-only message before the cast.
        if ($trimmed -match '^\[([0-9A-Fa-f:]+)\]:(\d+)$') {
            if ($matches[2].Length -gt 5) {
                throw 'UNITY_ACCELERATOR_ENDPOINT port is out of range (must be 1-65535).'
            }
            $hostPart = "[$($matches[1])]"
            $portPart = [int]$matches[2]
        }
        elseif ($trimmed -match '^([^:\s/?#]+):(\d+)$') {
            if ($matches[2].Length -gt 5) {
                throw 'UNITY_ACCELERATOR_ENDPOINT port is out of range (must be 1-65535).'
            }
            $hostPart = $matches[1]
            $portPart = [int]$matches[2]
        }
        else {
            throw 'UNITY_ACCELERATOR_ENDPOINT could not be parsed: expected host:port (e.g. 127.0.0.1:10080), [ipv6]:port, or scheme://host:port[/path].'
        }
    }

    if ($portPart -le 0 -or $portPart -gt 65535) {
        throw 'UNITY_ACCELERATOR_ENDPOINT port is out of range (must be 1-65535).'
    }

    return ('{0}:{1}' -f $hostPart, $portPart)
}

function Get-AcceleratorArguments {
    param(
        [string]$Endpoint,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Mode
    )

    $normalized = ConvertTo-NormalizedAcceleratorEndpoint -Endpoint $Endpoint
    if (-not $normalized) {
        return @()
    }

    # SECURITY: defense-in-depth masking. GitHub Actions masks the original
    # secret value, but here we extract a NEW substring (the normalized
    # host:port form) -- masking a parent string does NOT propagate to derived
    # substrings. Register BOTH the raw trimmed input (defense-in-depth, in
    # case the secret was passed via non-secret env in some other call path)
    # AND the normalized form BEFORE any downstream log line could echo them:
    # Invoke-UnityEditor prints "$EditorPath $($Arguments -join ' ')" later in
    # this same script (search for `Write-Host "`"$EditorPath`"`) which WOULD
    # leak the host:port unmasked without these directives.
    #
    # `::add-mask::` is a no-op outside GitHub Actions, so local runs are
    # unaffected. Done at the top of the success path so all callers benefit.
    Write-Host "::add-mask::$($Endpoint.Trim())"
    Write-Host "::add-mask::$normalized"

    return @(
        '-EnableCacheServer',
        '-cacheServerEndpoint', $normalized,
        '-cacheServerNamespacePrefix', "dxmessaging-$Version-$Mode",
        '-cacheServerEnableDownload', 'true',
        '-cacheServerEnableUpload', 'true'
    )
}

function Get-UnityActivationRetryBudgetSeconds {
    # Total wall-clock budget for RETRYING transient Unity serial-activation
    # failures. This exists for issue #57: the organization build lock's release
    # cooldown is deliberately near-zero, so when this run acquires a seat the
    # PRIOR holder's activation may not have propagated as returned yet and Unity
    # answers 20111 "maximum number of activations". That is transient seat
    # contention, so activation retries (bounded) until the seat frees instead of
    # the lock blindly holding a slot for minutes. Honors
    # DXM_ACTIVATION_RETRY_BUDGET_SECONDS. Default 360s matches the previously
    # observed ~5-minute Unity activation handoff plus a margin. A NON-INTEGER or
    # NEGATIVE override is ignored with a ::warning:: and the default is used. 0 is
    # the explicit OPT-OUT: a single attempt with legacy fail-fast behavior (no
    # retry) -- note this differs from the *-TIMEOUT_SECONDS helpers where 0 means
    # "unbounded", because an unbounded activation retry could pin a lock slot
    # forever on a real capacity leak. Mirrors Get-StandaloneTestPlayerTimeoutSeconds's
    # env-parse idiom. StrictMode-safe: no collection reads.
    param([int]$Default = 360)

    if ($env:DXM_ACTIVATION_RETRY_BUDGET_SECONDS) {
        $parsed = 0
        if (
            [int]::TryParse($env:DXM_ACTIVATION_RETRY_BUDGET_SECONDS, [ref]$parsed) -and
            $parsed -ge 0
        ) {
            return $parsed
        }
        Write-Host "::warning::Ignoring invalid DXM_ACTIVATION_RETRY_BUDGET_SECONDS='$env:DXM_ACTIVATION_RETRY_BUDGET_SECONDS'; using $Default second(s)."
    }
    return $Default
}

function Get-UnityActivationRetryDelaySeconds {
    # Deterministic (pre-jitter) exponential backoff CEILING for activation retry
    # attempt N (1-based): min(CapSeconds, BaseSeconds * 2^(N-1)). The caller adds
    # bounded random jitter and additionally clamps the sleep so it never overruns
    # the retry-budget deadline. Pure + StrictMode-safe so the backoff policy is
    # unit-tested without launching Unity.
    param(
        [Parameter(Mandatory = $true)][int]$Attempt,
        [int]$BaseSeconds = 5,
        [int]$CapSeconds = 30
    )

    if ($Attempt -lt 1) { $Attempt = 1 }
    # Clamp the exponent so 2^(N-1) cannot overflow on a pathologically high attempt
    # count; the min() against CapSeconds makes anything past the cap moot anyway.
    $exponent = [Math]::Min($Attempt - 1, 30)
    $scaled = [double]$BaseSeconds * [Math]::Pow(2, $exponent)
    $ceiling = [Math]::Min([double]$CapSeconds, $scaled)
    return [int][Math]::Ceiling($ceiling)
}

function Get-UnityActivationFailureClass {
    # PURE classifier for a Unity SERIAL-activation attempt: given the editor exit
    # code and the (possibly empty) activation log text, decide whether the run
    # should proceed, retry, or fail fast. Returns a StrictMode-safe object with
    #   Class  = 'success' | 'retryable' | 'hard'
    #   Reason = a stable, credential-free tag for diagnostics
    #
    # Contract (see the org lock's RESOURCE_REASON_CODES + secure-two-seat-rollout):
    #   * exit 0                                 -> success / activated
    #   * 20111 "maximum number of activations"  -> retryable / account-limit-20111
    #       The SEAT-CONTENTION signal. The lock caps concurrent holders, so a 20111
    #       here is either a transient handoff (previous seat still freeing -> clears
    #       within the budget) or a real capacity leak (persists). We retry it ONLY
    #       within the bounded budget; a 20111 that survives the deadline is re-thrown
    #       with the 20111 evidence still in the (OVERWRITTEN, final-attempt)
    #       activation log, so the EXISTING return-side classifier still raises the
    #       account-blocked incident. We never treat 20111 as success and never reset
    #       the incident path.
    #   * 20113 "serial expired"                 -> hard / serial-expired-20113
    #       Calendar expiry; retrying is pointless until the serial is rotated.
    #   * any other non-zero exit                -> retryable / unknown
    #       Covers process kills (124/137/143), transient network/token licensing
    #       errors (20105/20120/...), and unforeseen transients. Bounded by the
    #       budget, so a genuine misconfiguration costs at most one budget then
    #       surfaces the real error.
    # The \b-style non-digit guards keep 201110 / 120111 from matching 20111.
    # StrictMode-safe: no collection reads.
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [string]$LogText = ''
    )

    if ($ExitCode -eq 0) {
        return [pscustomobject]@{ Class = 'success'; Reason = 'activated' }
    }

    $text = if ($null -eq $LogText) { '' } else { [string]$LogText }

    if (
        $text -match '(?i)maximum number of activations' -or
        $text -match '(?<![0-9])20111(?![0-9])'
    ) {
        return [pscustomobject]@{ Class = 'retryable'; Reason = 'account-limit-20111' }
    }

    if (
        $text -match '(?i)serial expired' -or
        $text -match '(?<![0-9])20113(?![0-9])'
    ) {
        return [pscustomobject]@{ Class = 'hard'; Reason = 'serial-expired-20113' }
    }

    return [pscustomobject]@{ Class = 'retryable'; Reason = 'unknown' }
}

function Invoke-UnityLicenseActivate {
    param(
        [Parameter(Mandatory = $true)][string]$EditorPath,
        [Parameter(Mandatory = $true)][string]$Serial,
        [Parameter(Mandatory = $true)][string]$Email,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [int]$RetryBudgetSeconds = -1,
        # TESTABILITY SEAM (never passed in production): an optional invoker
        # `{ param($activateArgs, $logPath) ... return [int]$exitCode }` that stands
        # in for the real editor launch so the retry/deadline/backoff loop can be
        # exercised cross-platform without Unity. When $null (the default) the real
        # `& $EditorPath ... | Tee-Object` path runs and production behavior is
        # byte-for-byte unchanged.
        [scriptblock]$ActivationInvoker = $null
    )

    # Classic SERIAL activation: an editor invocation that activates the paid Unity
    # seat and immediately quits. This MUST succeed before the test run, so unlike
    # the best-effort return path a terminal failure THROWS -- an unlicensed test
    # editor fails opaquely.
    #
    # RETRY (issue #57): the organization lock's release cooldown is intentionally
    # near-zero, so when this run acquires a seat the PRIOR holder's activation may
    # not have propagated as returned yet and Unity answers 20111 "maximum number of
    # activations". That is transient seat contention, not a hard error, so we retry
    # within a bounded wall-clock budget (Get-UnityActivationRetryBudgetSeconds) with
    # jittered exponential backoff (Get-UnityActivationRetryDelaySeconds). A 20111
    # that SURVIVES the budget is a real capacity leak: we re-throw with the 20111
    # evidence still in the activation log so the existing return-side classifier
    # raises the account incident. Permanent failures (serial expiry) fail fast.
    $logDir = Split-Path -Parent $LogPath
    if ($logDir -and -not (Test-Path -LiteralPath $logDir -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }

    # SECURITY: the serial/email/password ride in the argument array, so this site
    # must NEVER echo the args (no "...$activateArgs..." Write-Host). The caller
    # passes a $LogPath under a NON-uploaded temp dir (RUNNER_TEMP / system temp),
    # never under $ArtifactsPath. Tee-Object OVERWRITES $LogPath each attempt, so the
    # log reflects only the FINAL attempt: a success after a transient 20111 leaves a
    # clean log (no stale 20111 for the return classifier to mis-read as an incident),
    # while a persistent 20111 leaves the 20111 intact for the incident path.
    $activateArgs = @(
        '-quit',
        '-batchmode',
        '-nographics',
        '-serial', $Serial,
        '-username', $Email,
        '-password', $Password,
        '-logFile', '-'
    )

    if ($RetryBudgetSeconds -lt 0) {
        $RetryBudgetSeconds = Get-UnityActivationRetryBudgetSeconds
    }
    $deadline = (Get-Date).AddSeconds($RetryBudgetSeconds)
    $attempt = 0

    while ($true) {
        $attempt++

        if ($ActivationInvoker) {
            $exitCode = [int](& $ActivationInvoker $activateArgs $LogPath)
        } else {
            Write-Host "::group::Activate Unity license (serial) attempt $attempt"
            # Unity.exe is a Windows GUI-subsystem binary: `&` does NOT wait for it or
            # set $LASTEXITCODE unless its stdout is consumed. `-logFile -` + Tee-Object
            # forces the wait, sets $LASTEXITCODE, and (over)writes the non-uploaded log.
            & $EditorPath @activateArgs 2>&1 | Tee-Object -FilePath $LogPath
            $exitCode = $LASTEXITCODE
            Write-Host "::endgroup::"
        }

        $logText = ''
        try {
            if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
                $rawLog = Get-Content -LiteralPath $LogPath -Raw -ErrorAction Stop
                if ($null -ne $rawLog) { $logText = $rawLog }
            }
        } catch {
            $logText = ''
        }

        $decision = Get-UnityActivationFailureClass -ExitCode $exitCode -LogText $logText

        if ($decision.Class -eq 'success') {
            if ($attempt -gt 1) {
                Write-CiNotice "Activated the Unity license (serial) on attempt $attempt."
            } else {
                Write-CiNotice 'Activated the Unity license (serial).'
            }
            return
        }

        # Every throw names the failure class + reason + attempt count + the
        # (non-uploaded) log path ONLY -- never the serial/email/password values.
        if ($decision.Class -eq 'hard') {
            throw "Unity license activation failed with exit code $exitCode (reason=$($decision.Reason)) after $attempt attempt(s); not retryable. See the activation log at $LogPath (not uploaded as an artifact)."
        }

        $remainingSeconds = ($deadline - (Get-Date)).TotalSeconds
        if ($RetryBudgetSeconds -le 0 -or $remainingSeconds -le 0) {
            throw "Unity license activation failed with exit code $exitCode (reason=$($decision.Reason)) after $attempt attempt(s) within a $RetryBudgetSeconds s retry budget. See the activation log at $LogPath (not uploaded as an artifact)."
        }

        $delaySeconds = Get-UnityActivationRetryDelaySeconds -Attempt $attempt
        # Full jitter in [1, delay], then clamp so we never sleep past the deadline
        # (min 1s so a tiny remaining budget still makes one more attempt).
        $jittered = Get-Random -Minimum 1 -Maximum ($delaySeconds + 1)
        $sleepSeconds = [int][Math]::Max(1, [Math]::Min([double]$jittered, $remainingSeconds))
        Write-Host "::warning::Unity license activation attempt $attempt failed (exit code $exitCode, reason=$($decision.Reason)); retrying in ${sleepSeconds}s (~$([int]$remainingSeconds)s of retry budget left). Expected transient seat contention under the organization lock's near-zero release cooldown."
        Start-Sleep -Seconds $sleepSeconds
    }
}

function Test-UnityLicenseReturnLogShowsEntitlementReturned {
    param(
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    try {
        if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
            return $false
        }

        $returnedEntitlement = Select-String `
            -LiteralPath $LogPath `
            -Pattern 'Successfully returned the entitlement license' `
            -SimpleMatch `
            -Quiet
        $legacyFileUnavailable = Select-String `
            -LiteralPath $LogPath `
            -Pattern 'Serial number unavailable for ULF return' `
            -SimpleMatch `
            -Quiet
        return $returnedEntitlement -and $legacyFileUnavailable
    } catch {
        return $false
    }
}

function Invoke-UnityLicenseReturn {
    param(
        [Parameter(Mandatory = $true)][string]$EditorPath,
        [Parameter(Mandatory = $true)][string]$Email,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    # Best-effort, defense-in-depth: this MUST NEVER throw. The license is also
    # returned by the workflow if:always() step (a backstop for a hard-killed
    # editor that never reaches this finally) and by the NEXT run's
    # return-at-start (which reclaims a seat leaked by a prior force-killed run on
    # this persistent self-hosted runner).
    try {
        $logDir = Split-Path -Parent $LogPath
        if ($logDir -and -not (Test-Path -LiteralPath $logDir -PathType Container)) {
            New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        }

        # SECURITY: email/password ride in the argument array; never echo the args
        # and keep the return log in the NON-uploaded temp dir, never under
        # $ArtifactsPath.
        $returnArgs = @(
            '-quit',
            '-batchmode',
            '-nographics',
            '-returnlicense',
            '-username', $Email,
            '-password', $Password,
            '-logFile', '-'
        )

        Write-Host "::group::Return Unity license (serial)"
        # Consume the GUI-subsystem process through a pipeline so PowerShell waits
        # and records LASTEXITCODE, but write only to the private log. Unity return
        # output can contain account or serial fragments and must not reach the
        # public workflow log.
        & $EditorPath @returnArgs 2>&1 | Out-File -FilePath $LogPath -Encoding utf8
        $exitCode = $LASTEXITCODE
        Add-Content -LiteralPath $LogPath -Value "exit_return_rc=$exitCode" -Encoding utf8
        Write-Host "::endgroup::"

        if ($exitCode -ne 0) {
            if (Test-UnityLicenseReturnLogShowsEntitlementReturned -LogPath $LogPath) {
                Write-CiNotice "Unity returned the entitlement license, then exited with code $exitCode while skipping legacy ULF return; treating the seat return as successful."
            } else {
                Write-Host "::warning::Unity license return exited with code $exitCode; the workflow if:always() return step and the next run's return-at-start are the backstops for the leaked seat."
            }
        } else {
            Write-CiNotice 'Returned the Unity license (serial).'
        }
    } catch {
        Write-Host "::warning::Unity license return failed: $($_.Exception.Message). The workflow if:always() return step and the next run's return-at-start are the backstops."
    } finally {
        Clear-NonFatalNativeExitCode -Context 'Unity license return cleanup'
    }
}

function Get-StandaloneTestPlayerTimeoutSeconds {
    # Single source of truth for the TOTAL wall-clock timeout applied to the
    # DIRECTLY-LAUNCHED standalone test player (Invoke-StandaloneTestPlayer). The
    # player runs ~700 runtime tests headless in single-digit minutes; the 30 min
    # default is a generous backstop so a player that hangs (e.g. a residual
    # connection dial-out or a deadlocked test) is tree-killed instead of running
    # until the 120-minute GitHub step is cancelled. Mirrors ensure-editor.ps1
    # Get-EnsureEditorInstallTimeoutSeconds EXACTLY: honors
    # DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS; a non-integer or NEGATIVE override is
    # ignored with a ::warning:: and the default is used; 0 is the explicit OPT-OUT
    # (unbounded wait). StrictMode-safe: no collection reads.
    param([int]$Default = 1800)

    if ($env:DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS) {
        $parsed = 0
        if (
            [int]::TryParse($env:DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS, [ref]$parsed) -and
            $parsed -ge 0
        ) {
            return $parsed
        }
        Write-Host "::warning::Ignoring invalid DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS='$env:DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS'; using $Default second(s)."
    }
    return $Default
}

function Get-StandaloneBuildTimeoutSeconds {
    # Single source of truth for the TOTAL wall-clock timeout applied to the editor
    # BUILD step that produces the standalone IL2CPP test player. The IL2CPP build
    # is the long pole; the 45 min default matches the install default and comfortably
    # exceeds a slow-but-progressing build, so a build that idles forever (e.g. the
    # PostBuildCleanup exit never fired because the modifier failed to compile and
    # AutoRunPlayer stayed set) is tree-killed instead of consuming the 120-minute
    # GitHub step. Mirrors ensure-editor.ps1 Get-EnsureEditorInstallTimeoutSeconds
    # EXACTLY: honors DXM_STANDALONE_BUILD_TIMEOUT_SECONDS; a non-integer or NEGATIVE
    # override is ignored with a ::warning:: and the default is used; 0 is the
    # explicit OPT-OUT (unbounded wait). StrictMode-safe: no collection reads.
    param([int]$Default = 2700)

    if ($env:DXM_STANDALONE_BUILD_TIMEOUT_SECONDS) {
        $parsed = 0
        if (
            [int]::TryParse($env:DXM_STANDALONE_BUILD_TIMEOUT_SECONDS, [ref]$parsed) -and
            $parsed -ge 0
        ) {
            return $parsed
        }
        Write-Host "::warning::Ignoring invalid DXM_STANDALONE_BUILD_TIMEOUT_SECONDS='$env:DXM_STANDALONE_BUILD_TIMEOUT_SECONDS'; using $Default second(s)."
    }
    return $Default
}

function Get-StandaloneHostConditionSnapshot {
    # Capture host conditions OUTSIDE the benchmark player so the probes never
    # execute inside a scenario's warmed five-second window. Every probe is
    # best-effort: missing thermal firmware or a rejected CIM query is evidence,
    # not a reason to discard an otherwise valid player run.
    param([Parameter(Mandatory = $true)][string]$Phase)

    $errors = New-Object System.Collections.Generic.List[string]
    $logicalProcessors = New-Object System.Collections.Generic.List[object]
    try {
        $processorInformation = @(
            Get-CimInstance `
                -ClassName Win32_PerfFormattedData_Counters_ProcessorInformation `
                -ErrorAction Stop
        )
        foreach ($logicalProcessor in $processorInformation) {
            if ([string]$logicalProcessor.Name -notlike '*_Total') {
                $logicalProcessors.Add([ordered]@{
                        name = [string]$logicalProcessor.Name
                        frequencyMhz = $logicalProcessor.ProcessorFrequency
                        percentProcessorPerformance = $logicalProcessor.PercentProcessorPerformance
                        loadPercent = $logicalProcessor.PercentProcessorTime
                    })
            }
        }
    } catch {
        $errors.Add("Win32_PerfFormattedData_Counters_ProcessorInformation: $($_.Exception.Message)")
    }

    $processors = New-Object System.Collections.Generic.List[object]
    try {
        $processorInstances = @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop)
        for ($index = 0; $index -lt $processorInstances.Count; $index++) {
            $processor = $processorInstances[$index]
            $processors.Add([ordered]@{
                    index = $index
                    currentClockMhz = $processor.CurrentClockSpeed
                    maxClockMhz = $processor.MaxClockSpeed
                    loadPercent = $processor.LoadPercentage
                    logicalProcessors = $processor.NumberOfLogicalProcessors
                })
        }
    } catch {
        $errors.Add("Win32_Processor: $($_.Exception.Message)")
    }

    $totalCpuLoadPercent = $null
    try {
        $cpuTotals = @(
            Get-CimInstance `
                -ClassName Win32_PerfFormattedData_PerfOS_Processor `
                -Filter "Name='_Total'" `
                -ErrorAction Stop
        )
        if ($cpuTotals.Count -gt 0) {
            $totalCpuLoadPercent = $cpuTotals[0].PercentProcessorTime
        }
    } catch {
        $errors.Add("Win32_PerfFormattedData_PerfOS_Processor: $($_.Exception.Message)")
    }

    $thermalRecords = New-Object System.Collections.Generic.List[object]
    try {
        $thermalZones = @(
            Get-CimInstance `
                -Namespace 'root/wmi' `
                -ClassName MSAcpi_ThermalZoneTemperature `
                -ErrorAction Stop
        )
        foreach ($thermalZone in $thermalZones) {
            if ($null -ne $thermalZone.CurrentTemperature) {
                $rawTemperature = [double]$thermalZone.CurrentTemperature
                $temperature = ($rawTemperature / 10.0) - 273.15
                $thermalRecords.Add([ordered]@{
                        instanceName = [string]$thermalZone.InstanceName
                        rawTenthsKelvin = $rawTemperature
                        celsius = [math]::Round($temperature, 2)
                    })
            }
        }
    } catch {
        $errors.Add("MSAcpi_ThermalZoneTemperature: $($_.Exception.Message)")
    }

    $harnessAffinity = $null
    try {
        $currentProcess = [System.Diagnostics.Process]::GetCurrentProcess()
        $harnessAffinity = '0x{0:X}' -f $currentProcess.ProcessorAffinity.ToInt64()
        $currentProcess.Dispose()
    } catch {
        $errors.Add("harness processor affinity: $($_.Exception.Message)")
    }

    return [ordered]@{
        phase = $Phase
        timestampUtc = [DateTime]::UtcNow.ToString('O')
        processorCount = [Environment]::ProcessorCount
        harnessProcessorAffinityMask = $harnessAffinity
        logicalProcessors = @($logicalProcessors.ToArray())
        processors = @($processors.ToArray())
        totalCpuLoadPercent = $totalCpuLoadPercent
        acpiThermalZones = [ordered]@{
            available = $thermalRecords.Count -gt 0
            zones = @($thermalRecords.ToArray())
        }
        probeErrors = @($errors.ToArray())
    }
}

function Get-StandalonePlayerManifest {
    # Hash every file present under the built player directory. Capturing the
    # complete manifest before the first launch and after the last detects any
    # changed, added, or removed player file without claiming which subset alone
    # defines the launched IL2CPP program.
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Standalone player executable was not found at $ExecutablePath."
    }
    $playerDirectory = Split-Path -Parent $ExecutablePath
    $files = @(Get-ChildItem -LiteralPath $playerDirectory -File -Recurse -Force)
    if ($files.Count -eq 0) {
        throw "Standalone player directory contains no files at $playerDirectory."
    }

    $filesByRelativePath = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($playerDirectory.Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        ).Replace('\', '/')
        $filesByRelativePath.Add($relativePath, $file)
    }
    $relativePaths = [string[]]@($filesByRelativePath.Keys)
    [Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($relativePath in $relativePaths) {
        $file = $filesByRelativePath[$relativePath]
        $entries.Add([ordered]@{
                path = $relativePath
                length = [long]$file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            })
    }
    return [ordered]@{
        schemaVersion = 1
        fileCount = $entries.Count
        files = @($entries.ToArray())
    }
}

function Write-JsonArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    $json = $Value | ConvertTo-Json -Depth 10
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json + "`n", $utf8NoBom)
}

function ConvertTo-ProcessArgumentLine {
    # MIRROR of scripts/unity/ensure-editor.ps1 ConvertTo-ProcessArgumentLine
    # (run-ci-tests.ps1 does not import that script, so the helper is copied here
    # verbatim). Builds a single Windows command-line argument string from an array,
    # quoting any argument containing whitespace or a quote and escaping embedded
    # backslashes/quotes per the CommandLineToArgvW rules. Used by
    # Invoke-ProcessWithTreeKillTimeout (it assigns ProcessStartInfo.Arguments, the
    # single command-line string form, NOT the per-element argument-list property
    # the contract forbids).
    param([string[]]$Arguments)

    $quoted = foreach ($arg in @($Arguments)) {
        if ($null -eq $arg) {
            '""'
            continue
        }

        $value = [string]$arg
        if ($value.Length -gt 0 -and $value -notmatch '[\s"]') {
            $value
            continue
        }

        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append('"')
        $backslashes = 0
        foreach ($ch in $value.ToCharArray()) {
            if ($ch -eq '\') {
                $backslashes++
                continue
            }

            if ($ch -eq '"') {
                if ($backslashes -gt 0) {
                    [void]$builder.Append('\' * ($backslashes * 2))
                }
                [void]$builder.Append('\"')
                $backslashes = 0
                continue
            }

            if ($backslashes -gt 0) {
                [void]$builder.Append('\' * $backslashes)
                $backslashes = 0
            }
            [void]$builder.Append($ch)
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append('\' * ($backslashes * 2))
        }
        [void]$builder.Append('"')
        $builder.ToString()
    }

    return ($quoted -join ' ')
}

function Invoke-ProcessWithTreeKillTimeout {
    # GENERALIZED hard tree-kill watchdog, STRUCTURALLY IDENTICAL to
    # scripts/unity/ensure-editor.ps1 Invoke-UnityCliCaptureWithTimeout (the proven
    # resilience core). It launches $FilePath with $Arguments via
    # System.Diagnostics.Process + ProcessStartInfo, drains BOTH stdout and stderr
    # from a MAIN-THREAD ReadLineAsync poll loop (live echo via Write-Host + Tee to
    # $LogPath), enforces an absolute UTC deadline, and on a breach $proc.Kill($true)
    # tree-kills the whole process tree (the Unity editor build spawns child
    # processes -- IL2CPP/bee -- and the player may too, so a bare Kill() would orphan
    # them). The process is held in a try/finally that kills it on ANY throw between
    # launch and reap, so a pwsh cancellation cannot leave an orphaned editor/player.
    #
    # WHY a Process and NOT `& <exe>`: the call operator cannot be interrupted -- a
    # hung child runs until the whole job is killed. WHY the main-thread poll loop:
    # every line is echoed LIVE the instant it arrives (no silent multi-minute build
    # console) AND both pipes are continuously drained so neither can fill and
    # back-pressure the child (the classic full-pipe-buffer deadlock is impossible).
    # A Process.Start() launch is NOT an `&`/`.` call, so it does not trip the
    # powershell-unity-process-wait-safety parser rule; the contract test additionally
    # forbids a bare empty-parens WaitForExit and the per-element argument-list
    # property here, both of which this implementation avoids.
    #
    # Returns a StrictMode-safe hashtable @{ ExitCode; TimedOut }. The caller throws
    # on $TimedOut or a non-zero $ExitCode; the FILE written by the player is the
    # source of truth for pass/fail.
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 1800,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$Label,
        [ValidateRange(0, [long]::MaxValue)][long]$ProcessorAffinityMask = 0,
        [ValidateSet('Normal', 'AboveNormal', 'High')][string]$PriorityClass = 'Normal'
    )

    $logDir = Split-Path -Parent $LogPath
    if ($logDir -and -not (Test-Path -LiteralPath $logDir -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }

    # Sentinel exit code for a wall-clock timeout kill. 124 mirrors GNU coreutils
    # `timeout`; it is non-zero so the caller's "exit != 0 -> fail" path applies.
    $timeoutExitCode = 124
    $PriorityClass = [System.Enum]::Parse(
        [System.Diagnostics.ProcessPriorityClass],
        $PriorityClass,
        $true
    ).ToString()

    Write-Host "::group::$Label"
    Write-Host "`"$FilePath`" $($Arguments -join ' ')"

    $buffer = New-Object System.Collections.Generic.List[string]

    if ($TimeoutSeconds -le 0) {
        $hasDeadline = $false
        $timeoutMs = -1
    } else {
        $hasDeadline = $true
        $timeoutMsLong = [int64]$TimeoutSeconds * 1000
        if ($timeoutMsLong -gt [int64]::MaxValue - 1) {
            $timeoutMs = [int64]::MaxValue - 1
        } else {
            $timeoutMs = $timeoutMsLong
        }
    }

    $proc = $null
    $exit = -1
    $timedOut = $false
    $reaped = $false
    $processId = $null
    $observedProcessorAffinityMask = $null
    $processorAffinityError = $null
    $observedProcessorPriorityClass = $null
    $processorPriorityError = $null
    $processSettingsVerified = $false
    $processSettingsError = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $FilePath
        $psi.Arguments = ConvertTo-ProcessArgumentLine -Arguments $Arguments
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true

        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi

        [void]$proc.Start()
        $processId = $proc.Id
        if ($ProcessorAffinityMask -gt 0) {
            $availableAffinityMask = $proc.ProcessorAffinity.ToInt64()
            if (($availableAffinityMask -band $ProcessorAffinityMask) -ne $ProcessorAffinityMask) {
                throw (
                    "Requested processor affinity 0x{0:X} is not a subset of available mask 0x{1:X}." -f
                    $ProcessorAffinityMask,
                    $availableAffinityMask
                )
            }
            $proc.ProcessorAffinity = [IntPtr]::new($ProcessorAffinityMask)
        }
        $requestedPriorityClass = [System.Enum]::Parse(
            [System.Diagnostics.ProcessPriorityClass],
            $PriorityClass,
            $true
        )
        $proc.PriorityClass = $requestedPriorityClass
        $proc.Refresh()
        try {
            $observedProcessorAffinityMask = '0x{0:X}' -f $proc.ProcessorAffinity.ToInt64()
        } catch {
            $processorAffinityError = $_.Exception.Message
        }
        try {
            $observedProcessorPriorityClass = $proc.PriorityClass.ToString()
        } catch {
            $processorPriorityError = $_.Exception.Message
        }
        if ($ProcessorAffinityMask -gt 0) {
            if ($observedProcessorAffinityMask -cne ('0x{0:X}' -f $ProcessorAffinityMask)) {
                throw "Process affinity verification failed: requested 0x$('{0:X}' -f $ProcessorAffinityMask), observed '$observedProcessorAffinityMask'."
            }
        }
        if ($observedProcessorPriorityClass -cne $PriorityClass) {
            throw "Process priority verification failed: requested '$PriorityClass', observed '$observedProcessorPriorityClass'."
        }
        $processSettingsVerified = $true
        Write-Host (
            "::notice::$Label process settings: pid=$processId, affinity=$observedProcessorAffinityMask, priority=$observedProcessorPriorityClass"
        )

        $outReader = $proc.StandardOutput
        $errReader = $proc.StandardError
        $oTask = $outReader.ReadLineAsync()
        $eTask = $errReader.ReadLineAsync()

        if ($hasDeadline) {
            $deadline = [DateTime]::UtcNow.AddMilliseconds([double]$timeoutMs)
        } else {
            $deadline = [DateTime]::MaxValue
        }

        $oDone = $false
        $eDone = $false
        while (-not ($oDone -and $eDone)) {
            $progressed = $false

            if (-not $oDone -and $oTask.Wait(0)) {
                $line = $oTask.Result
                if ($null -eq $line) {
                    $oDone = $true
                } else {
                    Write-Host $line
                    $buffer.Add([string]$line)
                    $oTask = $outReader.ReadLineAsync()
                }
                $progressed = $true
            }

            if (-not $eDone -and $eTask.Wait(0)) {
                $line = $eTask.Result
                if ($null -eq $line) {
                    $eDone = $true
                } else {
                    Write-Host $line
                    $buffer.Add([string]$line)
                    $eTask = $errReader.ReadLineAsync()
                }
                $progressed = $true
            }

            if ([DateTime]::UtcNow -ge $deadline) {
                # HUNG (or a quick-exit child whose grandchild still holds the pipe
                # open, so EOF never arrives): tree-kill the WHOLE process tree.
                $timedOut = $true
                try {
                    $proc.Kill($true)
                } catch {
                    try { $proc.Kill() } catch { }
                }
                break
            }

            if (-not $progressed) {
                Start-Sleep -Milliseconds 50
            }
        }

        # Reap so ExitCode is valid; bounded so a stuck reap cannot hang the harness.
        $reaped = $proc.WaitForExit(5000)

        # Drain any reads that completed during/after the kill so no pre-kill output
        # is dropped.
        foreach ($pending in @($oTask, $eTask)) {
            try {
                if ($pending.Wait(2000) -and $null -ne $pending.Result) {
                    $line = $pending.Result
                    Write-Host $line
                    $buffer.Add([string]$line)
                }
            } catch {
                # A faulted/cancelled read on a killed pipe carries nothing to add.
            }
        }

        if ($timedOut) {
            $exit = $timeoutExitCode
        } elseif ($reaped -and $proc.HasExited) {
            $exit = $proc.ExitCode
        } else {
            $exit = $timeoutExitCode
            $timedOut = $true
        }
    } catch {
        $message = "Process watchdog '$Label' threw: $($_.Exception.Message)"
        if (-not $processSettingsVerified) {
            $processSettingsError = $_.Exception.Message
        }
        Write-Host "::warning::$message"
        $buffer.Add($message)
        $exit = -1
    } finally {
        # If we are unwinding on a throw/cancellation and the process is still alive,
        # tree-kill it so a cancelled step never orphans the editor/player.
        if ($proc -and -not $proc.HasExited) {
            try { $proc.Kill($true) } catch { }
        }
        if ($proc) { $proc.Dispose() }
    }

    Write-Host "::endgroup::"

    # Persist the captured (already-streamed) output to $LogPath for diagnostics.
    try {
        Set-Content -LiteralPath $LogPath -Value (@($buffer.ToArray()) -join "`n") -Encoding UTF8
    } catch {
        Write-Host "::warning::Could not persist '$Label' log to ${LogPath}: $($_.Exception.Message)"
    }

    return @{
        ExitCode = $exit
        TimedOut = [bool]$timedOut
        ProcessId = $processId
        ProcessorAffinityMask = $observedProcessorAffinityMask
        ProcessorAffinityError = $processorAffinityError
        ProcessorPriorityClass = $observedProcessorPriorityClass
        ProcessorPriorityError = $processorPriorityError
        ProcessSettingsVerified = [bool]$processSettingsVerified
        ProcessSettingsError = $processSettingsError
    }
}

function Invoke-StandaloneTestPlayer {
    # RUN the editor-built standalone IL2CPP test player DIRECTLY (no
    # PlayerConnection): the player-side TestRunCallback writes NUnit XML to the
    # -dxmTestResults path and quits 0/1/2/3. The exe is launched under the hard
    # tree-kill watchdog so a hung player is killed long before the GitHub step is
    # cancelled. Returns @{ ExitCode; TimedOut }. The FILE is the source of truth: the
    # caller validates results.xml and treats a watchdog timeout as fatal ONLY when no
    # usable results file was written (a player can finish writing results in
    # RunFinished and then have Application.Quit deferred in -batchmode IL2CPP, which
    # the watchdog would otherwise turn into a spurious failure). Exit 2 (the player got
    # no -dxmTestResults arg -- a harness-contract violation) is still thrown here.
    #
    # ONE results channel: -dxmTestResults. There is NO environment-variable handoff
    # and NO per-user-data-folder fallback.
    param(
        [Parameter(Mandatory = $true)][string]$EditorBuiltExePath,
        [Parameter(Mandatory = $true)][string]$ResultsPath,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [string]$RuntimeProfilePath,
        [int]$TimeoutSeconds = 1800,
        [string]$HostConditionEvidencePath,
        [string]$ProcessEvidencePath,
        [ValidateRange(0, [long]::MaxValue)][long]$ProcessorAffinityMask = 0,
        [ValidateSet('Normal', 'AboveNormal', 'High')][string]$PriorityClass = 'Normal',
        [int]$RunIndex = 1,
        [int]$RunCount = 1
    )

    $playerArgs = @(
        '-batchmode',
        '-nographics',
        '-logFile', '-',
        '-dxmTestResults', $ResultsPath
    )
    if (-not [string]::IsNullOrWhiteSpace($RuntimeProfilePath)) {
        $playerArgs += @('-dxmRuntimeProfile', $RuntimeProfilePath)
    }
    $PriorityClass = [System.Enum]::Parse(
        [System.Diagnostics.ProcessPriorityClass],
        $PriorityClass,
        $true
    ).ToString()

    $before = $null
    if (-not [string]::IsNullOrWhiteSpace($HostConditionEvidencePath)) {
        $before = Get-StandaloneHostConditionSnapshot -Phase 'before'
    }

    $result = Invoke-ProcessWithTreeKillTimeout `
        -FilePath $EditorBuiltExePath `
        -Arguments $playerArgs `
        -TimeoutSeconds $TimeoutSeconds `
        -LogPath $LogPath `
        -Label 'Run standalone test player' `
        -ProcessorAffinityMask $ProcessorAffinityMask `
        -PriorityClass $PriorityClass

    if (-not [string]::IsNullOrWhiteSpace($ProcessEvidencePath)) {
        $requestedAffinityMask = if ($ProcessorAffinityMask -gt 0) {
            '0x{0:X}' -f $ProcessorAffinityMask
        } else {
            'unrestricted'
        }
        $processEvidence = [ordered]@{
            schemaVersion = 1
            processId = $result.ProcessId
            requestedProcessorAffinityMask = $requestedAffinityMask
            actualProcessorAffinityMask = $result.ProcessorAffinityMask
            processorAffinityError = $result.ProcessorAffinityError
            requestedPriorityClass = $PriorityClass
            actualPriorityClass = $result.ProcessorPriorityClass
            processorPriorityError = $result.ProcessorPriorityError
            processSettingsVerified = $result.ProcessSettingsVerified
            processSettingsError = $result.ProcessSettingsError
            exitCode = $result.ExitCode
            timedOut = $result.TimedOut
        }
        Write-JsonArtifact -Path $ProcessEvidencePath -Value $processEvidence
    }

    if (-not $result.ProcessSettingsVerified) {
        throw "Standalone player process settings were not applied: $($result.ProcessSettingsError)"
    }

    if (-not [string]::IsNullOrWhiteSpace($HostConditionEvidencePath)) {
        $after = Get-StandaloneHostConditionSnapshot -Phase 'after'
        $evidence = [ordered]@{
            schemaVersion = 1
            runIndex = $RunIndex
            runCount = $RunCount
            playerProcessId = $result.ProcessId
            playerProcessorAffinityMask = $result.ProcessorAffinityMask
            playerProcessorAffinityError = $result.ProcessorAffinityError
            exitCode = $result.ExitCode
            timedOut = $result.TimedOut
            before = $before
            after = $after
        }
        Write-JsonArtifact -Path $HostConditionEvidencePath -Value $evidence
    }

    # Exit 2 means the player received no -dxmTestResults arg (a harness-contract
    # violation -- the harness always passes it), so no file can exist: fail fast.
    if ($result.ExitCode -eq 2) {
        throw "Standalone test player reported no -dxmTestResults path (exit 2); no results were written. See the player log at $LogPath."
    }

    # Do NOT throw on a watchdog timeout here. A player can write a complete results
    # file in its RunFinished callback and then have Application.Quit deferred/ignored
    # in -batchmode -nographics IL2CPP; the watchdog then tree-kills it (TimedOut) even
    # though the results are valid. The caller validates the FILE (the source of truth)
    # and decides, so a deferred-quit run is not turned into a spurious failure.
    return @{
        ExitCode = $result.ExitCode
        TimedOut = $result.TimedOut
        ProcessId = $result.ProcessId
        ProcessorAffinityMask = $result.ProcessorAffinityMask
        ProcessorAffinityError = $result.ProcessorAffinityError
        ProcessorPriorityClass = $result.ProcessorPriorityClass
        ProcessorPriorityError = $result.ProcessorPriorityError
        ProcessSettingsVerified = $result.ProcessSettingsVerified
        ProcessSettingsError = $result.ProcessSettingsError
    }
}

function Invoke-UnityEditor {
    param(
        [Parameter(Mandatory = $true)][string]$EditorPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    # Unity.exe is a Windows GUI-subsystem binary. PowerShell's `&` launches such
    # executables ASYNCHRONOUSLY: it does NOT wait for them and does NOT set
    # $LASTEXITCODE. Callers therefore pass `-logFile -` (Unity logs to stdout) so
    # that consuming the process's stdout via the pipeline forces PowerShell to
    # BLOCK until the process exits AND reliably sets $LASTEXITCODE. Tee-Object both
    # streams the log live to the CI console and persists it to $LogPath.
    $logDir = Split-Path -Parent $LogPath
    if ($logDir -and -not (Test-Path -LiteralPath $logDir -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }

    Write-Host "::group::$Label"
    Write-Host "`"$EditorPath`" $($Arguments -join ' ')"
    # Stream Unity's output LIVE to the console AND persist it to $LogPath, but route
    # it to the HOST (Out-Host) so it never enters this function's success stream:
    # the function RETURNS the exit code, and a bare `| Tee-Object` would otherwise
    # collect every streamed log line into the caller's `$x = Invoke-UnityEditor`
    # capture (turning the return value into an Object[] of log lines + the code).
    # Consuming the process's stdout via the pipeline still forces PowerShell to
    # BLOCK until the GUI-subsystem Unity.exe exits and to set $LASTEXITCODE.
    & $EditorPath @Arguments 2>&1 | Tee-Object -FilePath $LogPath | Out-Host
    $exitCode = $LASTEXITCODE
    Clear-NonFatalNativeExitCode -Context $Label
    Write-Host "::endgroup::"
    if ($exitCode -ne 0) {
        # Proactively surface catastrophic compile-time failure patterns
        # (PrecompiledAssemblyException, CompilationFailedException, CS####,
        # CS8032) as ::error:: annotations so the operator sees the root cause
        # in BOTH the runner log AND GitHub's error summary, independent of
        # whether the workflow-level verify step also fires. On a benign
        # shutdown-race crash the log matches no catastrophic pattern, so this
        # is a no-op; on a real compile failure it names the root cause.
        Write-UnityCatastrophicErrorAnnotations -LogPath $LogPath
    }
    # RETURN the exit code; do NOT throw on a non-zero value. The DURABLE ARTIFACT
    # the invocation produces (the configure marker / the built player exe / the
    # NUnit results.xml) is the source of truth, validated by the caller. Unity
    # can crash in a BACKGROUND thread (for example the DirectoryMonitor file
    # watcher) DURING shutdown AFTER the artifact is fully written, returning a
    # crash exit code for an otherwise-successful run; gating on the artifact (not
    # the exit code) makes those benign shutdown-race crashes non-fatal while a
    # missing/invalid artifact still fails loudly.
    return $exitCode
}

# The Windows NTSTATUS codes a Unity batch process most commonly exits WITH when
# it crashes or aborts. Keyed by the canonical 8-char uppercase hex of the
# UNSIGNED exit code. This is the single source of truth for both the human
# description (Get-NativeExitCodeDescription) and the "is this a native crash
# code" classifier (Test-NativeCrashExitCode). Crash codes (the 0xC000xxxx
# family) are EXACTLY the benign post-work shutdown-race exits the
# artifact-is-source-of-truth gate tolerates when the durable artifact is valid.
$script:NativeExitCodeDescriptions = [ordered]@{
    'C0000005' = 'STATUS_ACCESS_VIOLATION'
    'C000001D' = 'STATUS_ILLEGAL_INSTRUCTION'
    'C0000017' = 'STATUS_NO_MEMORY'
    'C00000FD' = 'STATUS_STACK_OVERFLOW'
    'C0000135' = 'STATUS_DLL_NOT_FOUND'
    'C0000139' = 'STATUS_ENTRYPOINT_NOT_FOUND'
    'C0000374' = 'STATUS_HEAP_CORRUPTION'
    'C0000409' = 'STATUS_STACK_BUFFER_OVERRUN'
    'C0000420' = 'STATUS_ASSERTION_FAILURE'
}

function ConvertTo-UnsignedExitHex {
    # Canonical 8-char uppercase hex of an exit code, normalizing the negative
    # Int32 form PowerShell yields for a high-bit NTSTATUS (for example -1073741819
    # -> 'C0000005'). Compare against this STRING form, never the 0xC0000005 token:
    # PowerShell parses `0xC0000005` as a NEGATIVE Int32, so a numeric -eq against
    # the unsigned value silently fails (the int/uint conflation this whole helper
    # exists to avoid).
    param([Parameter(Mandatory = $true)][int]$ExitCode)
    $normalized = if ($ExitCode -lt 0) {
        [uint32]($ExitCode + 4294967296)
    } else {
        [uint32]$ExitCode
    }
    return $normalized.ToString('X8')
}

function Test-NativeCrashExitCode {
    # True when the exit code is a native Windows CRASH/abort NTSTATUS (the
    # 0xC000xxxx severity-error family), i.e. a process the OS terminated rather
    # than a value the app returned (0..255). Used ONLY to phrase the benign-exit
    # ::warning:: accurately; the pass/fail decision is gated on the durable
    # artifact, never on this classifier.
    param([Parameter(Mandatory = $true)][int]$ExitCode)
    $hexBare = ConvertTo-UnsignedExitHex -ExitCode $ExitCode
    if ($script:NativeExitCodeDescriptions.Contains($hexBare)) {
        return $true
    }
    # The 0xC000xxxx NTSTATUS family (STATUS_SEVERITY_ERROR + facility 0) covers
    # the native crash/abort statuses a Unity batch process exits with. This is a
    # best-effort classifier for the warning text ONLY; pass/fail is gated on the
    # durable artifact, so a status outside this prefix is at worst a missing
    # "(a native crash code)" note, never a wrong verdict.
    return ($hexBare -like 'C0*')
}

function Get-NativeExitCodeDescription {
    param([Parameter(Mandatory = $true)][int]$ExitCode)

    $hexBare = ConvertTo-UnsignedExitHex -ExitCode $ExitCode
    $hex = "0x$hexBare"
    if ($script:NativeExitCodeDescriptions.Contains($hexBare)) {
        return "$hex / $($script:NativeExitCodeDescriptions[$hexBare])"
    }

    return $hex
}

function Get-UnityCrashSignature {
    # Best-effort: scan a captured Unity log for the signature of a BACKGROUND-thread
    # crash that fired DURING shutdown, AFTER the batch work completed. Returns a
    # short human description (for the benign-exit ::warning::) or '' when no crash
    # signature is present. NEVER throws -- a diagnostic must not mask the real
    # decision (which is gated on the durable artifact, not on this scan).
    param([string]$LogPath)

    if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        return ''
    }
    try {
        $logText = Get-Content -LiteralPath $LogPath -Raw
    } catch {
        return ''
    }
    if (-not $logText) {
        return ''
    }

    # The editor reached the end of batch execution before the crash -> the crash
    # is in teardown, not in the work. (-quit prints this; -runTests prints the
    # "Exiting batchmode successfully" variant.)
    $cleanShutdown = ($logText -match 'Batchmode quit successfully invoked' -or
        $logText -match 'Exiting batchmode successfully')

    # A known benign Windows shutdown-race: the DirectoryMonitor file-watcher
    # thread faulting while the editor tears down. This is the crash observed on
    # the 6000.3 standalone configure pass.
    if ($logText -match 'DirectoryMonitor') {
        $suffix = if ($cleanShutdown) { ' after a clean batch shutdown' } else { '' }
        return "Unity DirectoryMonitor file-watcher thread crash during shutdown$suffix"
    }
    if ($logText -match 'Crash!!!') {
        $suffix = if ($cleanShutdown) { ' after a clean batch shutdown' } else { '' }
        return "Unity native crash during shutdown$suffix"
    }
    if ($cleanShutdown) {
        return 'Unity completed its batch work (clean shutdown logged) before exiting non-zero'
    }
    return ''
}

function Write-UnityBenignExitWarning {
    # Emit a single ::warning:: when a Unity batch invocation produced a VALID
    # durable artifact but still exited non-zero or was tree-killed by the
    # watchdog. Decodes the exit code (for example 0xC0000005 /
    # STATUS_ACCESS_VIOLATION) and names any crash signature found in the log, so
    # the benign post-work shutdown crash stays VISIBLE and trackable in CI without
    # failing the job. The artifact -- already validated by the caller -- is the
    # source of truth; this only narrates why a non-zero exit was tolerated.
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [int]$ExitCode = 0,
        [switch]$TimedOut,
        [string]$LogPath
    )

    $cause = if ($TimedOut) {
        'was tree-killed by the watchdog (likely a deferred Application.Quit)'
    } else {
        $description = Get-NativeExitCodeDescription -ExitCode $ExitCode
        $crashNote = if (Test-NativeCrashExitCode -ExitCode $ExitCode) { ' (a native crash code)' } else { '' }
        "exited with code $ExitCode / $description$crashNote"
    }
    $signature = Get-UnityCrashSignature -LogPath $LogPath
    $signatureNote = if ($signature) { " Crash signature: $signature." } else { '' }
    Write-Host "::warning::${Label}: Unity $cause AFTER producing a valid result artifact; honoring the artifact as the source of truth and treating this as a benign post-work shutdown crash.$signatureNote"
}

function Test-UnityConfigureMarker {
    # Validate the standalone-configure SUCCESS MARKER as the source of truth for
    # the configure pass (DxmCiTestConfigurator.Apply writes it as its final
    # action). Returns '' when the marker exists and is FRESH for this run, else a
    # short reason string (mirrors Test-StandalonePlayerBuildOutput's contract).
    # A fresh marker proves Apply() ran to completion even if Unity then crashed in
    # a background thread during shutdown and returned a crash exit code.
    param(
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)][datetime]$StartedUtc
    )

    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) {
        return 'configure marker was not written (DxmCiTestConfigurator.Apply did not run to completion)'
    }
    $marker = Get-Item -LiteralPath $MarkerPath
    if ($marker.LastWriteTimeUtc -lt $StartedUtc.AddSeconds(-5)) {
        return "stale configure marker; LastWriteTimeUtc=$($marker.LastWriteTimeUtc.ToString('o'))"
    }
    return ''
}

function Invoke-UnityNativeStartupProbe {
    param(
        [Parameter(Mandatory = $true)][string]$EditorPath,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $logDir = Split-Path -Parent $LogPath
    if ($logDir -and -not (Test-Path -LiteralPath $logDir -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }

    Write-Host "::group::Unity native startup diagnostics"
    Write-Host "Runner name: $env:RUNNER_NAME"
    Write-Host "Runner OS: $env:RUNNER_OS"
    Write-Host "Runner architecture: $env:RUNNER_ARCH"
    Write-Host "Unity editor path: $EditorPath"
    try {
        $editorItem = Get-Item -LiteralPath $EditorPath
        Write-Host "Unity editor file version: $($editorItem.VersionInfo.FileVersion)"
        Write-Host "Unity editor product version: $($editorItem.VersionInfo.ProductVersion)"
    } catch {
        Write-Host "::notice::Could not read Unity editor version info: $($_.Exception.Message)"
    }

    Write-Host "Unity licensing client inventory:"
    $licensingClientCandidates = New-Object System.Collections.Generic.List[string]
    foreach ($root in @(${env:ProgramFiles}, ${env:ProgramFiles(x86)})) {
        if ($root -and $root.Trim().Length -gt 0) {
            $licensingClientCandidates.Add(
                (Join-Path $root 'Common Files\Unity\UnityLicensingClient\Unity.Licensing.Client.exe')
            )
        }
    }
    if ($env:LOCALAPPDATA -and $env:LOCALAPPDATA.Trim().Length -gt 0) {
        $licensingClientCandidates.Add(
            (Join-Path $env:LOCALAPPDATA 'Unity\Unity.Licensing.Client\Unity.Licensing.Client.exe')
        )
    }
    foreach ($candidate in $licensingClientCandidates) {
        $exists = Test-Path -LiteralPath $candidate -PathType Leaf
        Write-Host "  [$exists] $candidate"
    }

    $probeArgs = @(
        '-version',
        '-batchmode',
        '-nographics',
        '-quit',
        '-logFile', '-'
    )

    Write-Host "`"$EditorPath`" $($probeArgs -join ' ')"
    & $EditorPath @probeArgs 2>&1 | Tee-Object -FilePath $LogPath
    $exitCode = $LASTEXITCODE
    $description = Get-NativeExitCodeDescription -ExitCode $exitCode
    Write-Host "Unity native startup probe exit code: $exitCode ($description)"
    Write-Host "::endgroup::"

    if ($exitCode -ne 0) {
        throw "Unity native startup probe failed with exit code $exitCode ($description) after the pre-lock healthy-existing editor check. CI never repairs editors. A runner administrator must repair the host or editor manually, then retry. See the streamed probe log above (also saved to $LogPath)."
    }
}

# CLASS-OF-ISSUE GUARD: the defect this whole change fixes is a single analyzer
# DLL handed to the compiler from MORE THAN ONE path. That is invisible in a raw
# csc command line, so this
# best-effort scanner reads the Unity compile log, collects every analyzer the
# compiler was given (-a:/-analyzer:, quoted or not), and -- when the SAME DLL file
# name came from more than one distinct path -- names the offending DLL and every
# path. It catches a regression of the project-generation fix loudly. NEVER throws
# (the caller is already on a throw path).
function Write-DuplicateAnalyzerDiagnostics {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$LogPath)

    if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        return
    }

    try {
        # -a:"path" / -a:path / -analyzer:"path" / -analyzer:path. Captured lazily
        # up to the first '.dll' so an unquoted, space-separated token does not
        # swallow the next argument.
        $pattern = '-(?:a|analyzer):"?([^"\r\n]+?\.dll)"?(?:"|\s|$)'
        $pathsByName = @{}
        $hits = @(
            Select-String -LiteralPath $LogPath -Pattern $pattern -AllMatches -ErrorAction SilentlyContinue
        )
        foreach ($hit in $hits) {
            foreach ($match in $hit.Matches) {
                $fullPath = $match.Groups[1].Value.Trim() -replace '\\', '/'
                if (-not $fullPath) {
                    continue
                }
                $name = Split-Path -Leaf $fullPath
                if (-not $pathsByName.ContainsKey($name)) {
                    $pathsByName[$name] = New-Object 'System.Collections.Generic.HashSet[string]'
                }
                [void]$pathsByName[$name].Add($fullPath)
            }
        }

        $duplicates = @($pathsByName.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 })
        if ($duplicates.Count -lt 1) {
            return
        }

        Write-Host "::group::Duplicate analyzer registration"
        foreach ($entry in $duplicates) {
            $joinedPaths = (@($entry.Value) | Sort-Object) -join '; '
            Write-CiError ("Analyzer/source-generator '$($entry.Key)' was handed to the compiler from " +
                "$($entry.Value.Count) distinct paths: $joinedPaths. A source generator that runs more than " +
                "once emits each member twice (CS0102) and duplicate precompiled assemblies are rejected " +
                "outright. Each DLL must arrive from exactly one intended path: DxMessaging analyzers " +
                "through Runtime/Analyzers RoslynAnalyzer metadata, and CI-only Roslynator assemblies " +
                "through Assets/csc.rsp. Do not copy or register the same DLL through another path.")
        }
        Write-Host "::endgroup::"
    } catch {
        Write-Host "::warning::Could not scan for duplicate analyzer registration: $($_.Exception.Message)"
    }
}

function Write-UnityResultFailureDiagnostics {
    param(
        [string]$LogPath,
        [string]$Project,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Write-Host "::group::Unity result failure diagnostics ($Label)"
    try {
        if ($LogPath -and (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
            Write-Host "Unity log path: $LogPath"
            # Compose this function's scan list as:
            #   (catastrophic patterns from the shared $script:CatastrophicPatterns
            #    array; ONLY the regex-form entries, since Select-String's
            #    -Pattern overload is regex when -SimpleMatch is absent)
            # plus this function's local additions (Aborting/Exiting/No tests/
            # TestRunner/results.xml/assemblyNames) -- the latter are NOT
            # catastrophic-class patterns and are intentionally NOT in the
            # shared array. This keeps the "single source of truth" rule for
            # the overlapping patterns (error CS\d+, warning CS8032) without
            # changing the function's overall scan behavior.
            $catastrophicRegexes = @(
                foreach ($entry in $script:CatastrophicPatterns) {
                    if (-not $entry.UseSimple) {
                        $entry.Pattern
                    }
                }
            )
            $localDiagnosticPatterns = @(
                'Aborting batchmode',
                'Exiting batchmode successfully',
                'No tests',
                'TestRunner',
                'IPCStream \(Upm-[^)]+\): IPC stream failed to read',
                'Failed to resolve packages',
                'Cancelled resolving packages',
                'results\.xml',
                'assemblyNames'
            )
            $diagnosticPatterns = @($catastrophicRegexes) + @($localDiagnosticPatterns)
            $matches = @(
                Select-String -LiteralPath $LogPath -Pattern $diagnosticPatterns -ErrorAction SilentlyContinue |
                    Select-Object -First 80
            )
            if ($matches.Count -gt 0) {
                Write-Host "Selected Unity log lines:"
                foreach ($match in $matches) {
                    Write-Host ("  line {0}: {1}" -f $match.LineNumber, $match.Line.Trim())
                }
            } else {
                Write-Host "No targeted diagnostic lines matched in the Unity log."
            }

            $logText = Get-Content -LiteralPath $LogPath -Raw
            if ($logText -match 'warning CS8032') {
                Write-CiError "Unity could not instantiate one or more DxMessaging analyzers/source generators (CS8032). Check that Runtime/Analyzers DLLs target the Roslyn version supported by this Unity editor."
            }
            if ($logText -match 'error CS0315' -and $logText -match 'Simple(?:Untargeted|Targeted|Broadcast)Message') {
                Write-CiError "Message fixture compile errors followed missing generated interfaces. This usually means the DxMessaging source generator did not load."
            }
            if ($logText -match 'Exiting batchmode successfully') {
                Write-CiError "Unity exited with code 0 but did not write NUnit results. Check the selected assembly list, test platform, and TestRunner log lines above."
            }
            if (Test-UnityPackageManagerTransientFailure -LogPath $LogPath) {
                Write-CiError "Unity Package Manager canceled package resolution before tests started. This is a CI/Unity package-resolution failure, not a DxMessaging test assertion."
                Write-UnityPackageManagerDiagnostics -Project $Project -LogPath $LogPath
            }

            # Name a duplicate analyzer registration (the same generator/analyzer
            # DLL fed to csc from two paths) -- the precise root cause of the
            # "Multiple precompiled assemblies" / CS0102 duplicate-'MessageType'
            # failures this harness change fixes.
            Write-DuplicateAnalyzerDiagnostics -LogPath $LogPath
        } else {
            Write-Host "Unity log path unavailable or missing: $LogPath"
        }

        if ($Project) {
            $scriptAssemblies = [System.IO.Path]::Combine($Project, 'Library', 'ScriptAssemblies')
            if (Test-Path -LiteralPath $scriptAssemblies -PathType Container) {
                Write-Host "Script assemblies present:"
                Get-ChildItem -LiteralPath $scriptAssemblies -Filter '*.dll' -ErrorAction SilentlyContinue |
                    Select-Object -ExpandProperty Name |
                    Sort-Object |
                    ForEach-Object { Write-Host "  $_" }
            } else {
                Write-Host "Script assemblies directory missing: $scriptAssemblies"
            }
        }
    } catch {
        Write-Host "::warning::Could not collect Unity result failure diagnostics: $($_.Exception.Message)"
    }
    Write-Host "::endgroup::"
}

function Write-UnityRunFailureDiagnostics {
    # Emit the combined analyzer-setup + result-failure diagnostics for a Unity
    # batch invocation whose DURABLE ARTIFACT validation failed (missing configure
    # marker / invalid player exe / missing-or-invalid results.xml). This is the
    # failure-path diagnostics bundle the retired Invoke-UnityEditorWithFailureDiagnostics
    # wrapper used to emit on a thrown non-zero exit; it now fires from the
    # artifact-validation failure branch (the exit code is no longer the trigger).
    # Two callers: the configure marker-validation failure and the standalone build
    # exe-validation failure (the latter then also emits Write-StandaloneBuildOutputDiagnostics).
    # The editmode/playmode + standalone-player paths get the result-failure half
    # directly from Test-NUnitResults, which calls Write-UnityResultFailureDiagnostics.
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$CscLabel,
        [Parameter(Mandatory = $true)][string]$DiagnosticsLabel
    )

    Write-AnalyzerSetupDiagnostics -Project $Project -LogPath $LogPath -Label $CscLabel
    Write-UnityResultFailureDiagnostics -LogPath $LogPath -Project $Project -Label $DiagnosticsLabel
}

function Write-StandaloneDirectorySnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$MaxEntries = 60
    )

    try {
        Write-Host "${Label}: $Path"
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            Write-Host "  (missing)"
            return
        }

        $entries = @(
            Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue |
                Sort-Object FullName |
                Select-Object -First $MaxEntries
        )
        if ($entries.Count -lt 1) {
            Write-Host "  (empty)"
            return
        }

        foreach ($entry in $entries) {
            $kind = if ($entry.PSIsContainer) { 'dir ' } else { 'file' }
            $length = if ($entry.PSIsContainer) { '' } else { " $($entry.Length) bytes" }
            Write-Host "  [$kind] $($entry.FullName)$length"
        }
    } catch {
        Write-Host "::warning::Could not snapshot ${Label}: $($_.Exception.Message)"
    }
}

function Write-StandaloneBuildOutputDiagnostics {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$ExpectedExe,
        [string]$LogPath,
        [datetime]$BuildStartedUtc
    )

    Write-Host "::group::Standalone player build output diagnostics"
    try {
        Write-Host "Expected exe: $ExpectedExe"
        Write-Host "DXM_PLAYER_BUILD_PATH: $env:DXM_PLAYER_BUILD_PATH"
        Write-Host "Build started UTC: $($BuildStartedUtc.ToString('o'))"

        $expectedDir = Split-Path -Parent $ExpectedExe
        Write-StandaloneDirectorySnapshot -Label 'Expected output directory' -Path $expectedDir
        Write-StandaloneDirectorySnapshot -Label 'Project Build directory' -Path (Join-Path $Project 'Build')
        Write-StandaloneDirectorySnapshot -Label 'Project Temp/DxmTestPlayer directory' -Path ([System.IO.Path]::Combine($Project, 'Temp', 'DxmTestPlayer'))
        Write-StandaloneDirectorySnapshot -Label 'Project Temp/PlayerWithTests directory' -Path ([System.IO.Path]::Combine($Project, 'Temp', 'PlayerWithTests'))

        Write-Host "Discovered executable candidates under Build/Temp:"
        $candidateRoots = @(
            Join-Path $Project 'Build',
            Join-Path $Project 'Temp'
        )
        $candidates = @(
            foreach ($root in $candidateRoots) {
                if (Test-Path -LiteralPath $root -PathType Container) {
                    Get-ChildItem -LiteralPath $root -Recurse -Filter '*.exe' -File -ErrorAction SilentlyContinue
                }
            }
        )
        if ($candidates.Count -lt 1) {
            Write-Host "  (none)"
        } else {
            foreach ($candidate in ($candidates | Sort-Object FullName | Select-Object -First 40)) {
                Write-Host ("  {0} ({1} bytes, LastWriteTimeUtc={2:o})" -f $candidate.FullName, $candidate.Length, $candidate.LastWriteTimeUtc)
            }
        }

        if ($LogPath -and (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
            $logText = Get-Content -LiteralPath $LogPath -Raw
            Write-Host "Build log markers:"
            foreach ($marker in @(
                    'DxmCiStandaloneBuildModifier',
                    'DXM_PLAYER_BUILD_PATH',
                    'DxmTestPlayer',
                    'PlayerWithTests',
                    'AutoRunPlayer',
                    'CopyFiles'
                )) {
                Write-Host "  ${marker}: $($logText.Contains($marker))"
            }
            Write-Host "Build log tail:"
            Get-Content -LiteralPath $LogPath -Tail 80 -ErrorAction SilentlyContinue |
                ForEach-Object { Write-Host "  $_" }
        } else {
            Write-Host "Build log missing: $LogPath"
        }
    } catch {
        Write-Host "::warning::Could not collect standalone player build diagnostics: $($_.Exception.Message)"
    }
    Write-Host "::endgroup::"
}

function Test-StandalonePlayerBuildOutput {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedExe,
        [Parameter(Mandatory = $true)][datetime]$BuildStartedUtc
    )

    if (-not (Test-Path -LiteralPath $ExpectedExe -PathType Leaf)) {
        return "missing exe"
    }

    $exe = Get-Item -LiteralPath $ExpectedExe
    if ($exe.LastWriteTimeUtc -lt $BuildStartedUtc.AddSeconds(-5)) {
        return "stale exe; LastWriteTimeUtc=$($exe.LastWriteTimeUtc.ToString('o'))"
    }

    $dataDir = Join-Path (Split-Path -Parent $ExpectedExe) ("{0}_Data" -f [System.IO.Path]::GetFileNameWithoutExtension($ExpectedExe))
    if (-not (Test-Path -LiteralPath $dataDir -PathType Container)) {
        return "missing player data directory: $dataDir"
    }

    return ''
}

function Assert-ExactJsonPropertyNames {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actualNames = [string[]]@($Value.PSObject.Properties.Name)
    [Array]::Sort($actualNames, [System.StringComparer]::Ordinal)
    $expectedNames = [string[]]@($Expected)
    [Array]::Sort($expectedNames, [System.StringComparer]::Ordinal)
    if (($actualNames -join "`n") -cne ($expectedNames -join "`n")) {
        throw "$Label has unexpected JSON properties. Expected [$($expectedNames -join ', ')], observed [$($actualNames -join ', ')]."
    }
}

function Assert-JsonValueType {
    param(
        [Parameter(Mandatory = $true)][AllowNull()][AllowEmptyString()][object]$Value,
        [Parameter(Mandatory = $true)][ValidateSet('string', 'bool', 'integer', 'number', 'array')][string]$ExpectedKind,
        [Parameter(Mandatory = $true)][string]$Path
    )

    # 'number' accepts any JSON numeric token. ConvertFrom-Json yields [int] or
    # [long] for integral literals and [double] or [decimal] for fractional ones,
    # so a duration written as 12 and one written as 12.5 are both numbers.
    $matches = switch ($ExpectedKind) {
        'string' { $Value -is [string] }
        'bool' { $Value -is [bool] }
        'integer' { $Value -is [int] -or $Value -is [long] }
        'number' {
            $Value -is [int] -or $Value -is [long] -or $Value -is [double] -or
            $Value -is [single] -or $Value -is [decimal]
        }
        'array' { $Value -is [System.Array] }
    }
    if (-not $matches) {
        $actualType = if ($null -eq $Value) { 'null' } else { $Value.GetType().FullName }
        throw "$Path must be a JSON $ExpectedKind value; observed $actualType."
    }
}

function Get-ExpectedShippingShapeNames {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('semantic', 'cardinality')][string]$Topology,
        [Parameter(Mandatory = $true)][ValidateSet(1, 16, 18, 256, 1000)][int]$MessageTypeCount
    )

    if ($Topology -ceq 'cardinality') {
        return [string[]]@(
            for ($messageIndex = 1; $messageIndex -le $MessageTypeCount; $messageIndex++) {
                'DxmShippingCardinalityMessage{0:D4}' -f $messageIndex
            }
        )
    }
    if ($MessageTypeCount -ne 18) {
        throw 'The semantic shipping topology requires exactly 18 message types.'
    }
    return [string[]]@(
        'DxmShippingPublicUntargetedClass',
        'DxmShippingPublicUntargetedStruct',
        'DxmShippingPublicTargetedClass',
        'DxmShippingPublicTargetedStruct',
        'DxmShippingPublicBroadcastClass',
        'DxmShippingPublicBroadcastStruct',
        'NestedUntargetedClass',
        'NestedUntargetedStruct',
        'NestedTargetedClass',
        'NestedTargetedStruct',
        'NestedBroadcastClass',
        'NestedBroadcastStruct',
        'PublicNestedUntargetedClass',
        'PublicNestedUntargetedStruct',
        'PublicNestedTargetedClass',
        'PublicNestedTargetedStruct',
        'PublicNestedBroadcastClass',
        'PublicNestedBroadcastStruct'
    )
}

function Assert-ExactJsonStringArray {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Value,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Path
    )

    foreach ($element in @($Value)) {
        Assert-JsonValueType -Value $element -ExpectedKind string -Path "$Path[]"
    }
    $actual = [string[]]@($Value)
    if (
        $actual.Count -ne $Expected.Count -or
        ($actual -join "`n") -cne ($Expected -join "`n")
    ) {
        throw "$Path does not contain the exact expected ordered shape inventory."
    }
}

function Assert-NoShippingTestAssemblies {
    param(
        [Parameter(Mandatory = $true)][string[]]$AssemblyNames,
        [Parameter(Mandatory = $true)][string]$Label
    )

    foreach ($assemblyName in $AssemblyNames) {
        if (
            [string]::IsNullOrWhiteSpace($assemblyName) -or
            $assemblyName -ieq 'nunit.framework' -or
            $assemblyName -imatch 'TestRunner' -or
            $assemblyName -imatch 'PerformanceTesting' -or
            $assemblyName -imatch '(?:^|\.)Tests(?:\.|$)' -or
            $assemblyName -clike 'DxmCiStandalone*'
        ) {
            throw "$Label contains a forbidden or empty assembly name: '$assemblyName'."
        }
    }
    $uniqueNames = @($AssemblyNames | Sort-Object -Unique)
    if ($uniqueNames.Count -ne $AssemblyNames.Count) {
        throw "$Label contains duplicate assembly names."
    }
}

function Test-ShippingAssemblyEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedProfileId,
        [Parameter(Mandatory = $true)][string]$ExpectedProfileSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedUnityVersion
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Shipping build did not write assembly evidence at $Path."
    }
    $evidence = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-ExactJsonPropertyNames -Value $evidence -Expected @(
        'schemaVersion',
        'profileId',
        'profileSha256',
        'unityVersion',
        'includeTestAssemblies',
        'playerAssemblies'
    ) -Label 'Shipping assembly evidence'
    Assert-JsonValueType -Value $evidence.schemaVersion -ExpectedKind integer -Path 'shippingAssemblyEvidence.schemaVersion'
    Assert-JsonValueType -Value $evidence.profileId -ExpectedKind string -Path 'shippingAssemblyEvidence.profileId'
    Assert-JsonValueType -Value $evidence.profileSha256 -ExpectedKind string -Path 'shippingAssemblyEvidence.profileSha256'
    Assert-JsonValueType -Value $evidence.unityVersion -ExpectedKind string -Path 'shippingAssemblyEvidence.unityVersion'
    Assert-JsonValueType -Value $evidence.includeTestAssemblies -ExpectedKind bool -Path 'shippingAssemblyEvidence.includeTestAssemblies'
    Assert-JsonValueType -Value $evidence.playerAssemblies -ExpectedKind array -Path 'shippingAssemblyEvidence.playerAssemblies'
    if (
        [int]$evidence.schemaVersion -ne 1 -or
        [string]$evidence.profileId -cne $ExpectedProfileId -or
        [string]$evidence.profileSha256 -cne $ExpectedProfileSha256 -or
        [string]$evidence.unityVersion -cne $ExpectedUnityVersion -or
        [bool]$evidence.includeTestAssemblies
    ) {
        throw 'Shipping assembly evidence does not match the selected profile or Unity version.'
    }
    foreach ($rawAssemblyName in @($evidence.playerAssemblies)) {
        Assert-JsonValueType `
            -Value $rawAssemblyName `
            -ExpectedKind string `
            -Path 'shippingAssemblyEvidence.playerAssemblies[]'
    }
    $assemblyNames = [string[]]@($evidence.playerAssemblies)
    Assert-NoShippingTestAssemblies -AssemblyNames $assemblyNames -Label 'Shipping build assembly inventory'
    $expectedAssemblyNames = @('Assembly-CSharp', 'WallstopStudios.DxMessaging')
    if (($assemblyNames -join "`n") -cne ($expectedAssemblyNames -join "`n")) {
        throw 'Shipping build assembly inventory differs from the exact two expected consumer assemblies.'
    }
}

function Test-ShippingBuildReport {
    # The generated builder writes this from the same BuildReport that proves the
    # final options. Identity and success are exact; Unity-reported time and size
    # are recorded as observations and only required to be non-negative, while
    # the builder's own Stopwatch duration and UTC stamps must be positive, in
    # order, and fresh for this runner launch.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedProfileId,
        [Parameter(Mandatory = $true)][string]$ExpectedProfileSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedUnityVersion,
        [Parameter(Mandatory = $true)][ValidateSet('semantic', 'cardinality')][string]$ExpectedTopology,
        [Parameter(Mandatory = $true)][ValidateSet(1, 16, 18, 256, 1000)][int]$ExpectedMessageTypeCount,
        [Parameter(Mandatory = $true)][datetime]$BuildStartedUtc
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Shipping build did not write a build report at $Path."
    }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report -isnot [pscustomobject]) {
        throw 'Shipping build report must be a JSON object.'
    }
    Assert-ExactJsonPropertyNames -Value $report -Expected @(
        'schemaVersion',
        'profileId',
        'profileSha256',
        'topologyId',
        'messageTypeCount',
        'unityVersion',
        'buildResult',
        'buildStartedUnixMs',
        'buildEndedUnixMs',
        'buildDurationMs',
        'reportedTotalTimeMs',
        'reportedTotalSizeBytes',
        'steps'
    ) -Label 'Shipping build report'
    foreach ($stringProperty in @(
        'profileId',
        'profileSha256',
        'topologyId',
        'unityVersion',
        'buildResult'
    )) {
        Assert-JsonValueType -Value $report.$stringProperty -ExpectedKind string -Path "shippingBuildReport.$stringProperty"
    }
    foreach ($integerProperty in @(
        'schemaVersion',
        'messageTypeCount',
        'buildStartedUnixMs',
        'buildEndedUnixMs',
        'reportedTotalSizeBytes'
    )) {
        Assert-JsonValueType -Value $report.$integerProperty -ExpectedKind integer -Path "shippingBuildReport.$integerProperty"
    }
    foreach ($numberProperty in @('buildDurationMs', 'reportedTotalTimeMs')) {
        Assert-JsonValueType -Value $report.$numberProperty -ExpectedKind number -Path "shippingBuildReport.$numberProperty"
    }
    Assert-JsonValueType -Value $report.steps -ExpectedKind array -Path 'shippingBuildReport.steps'
    if (
        [int]$report.schemaVersion -ne 1 -or
        [string]$report.profileId -cne $ExpectedProfileId -or
        [string]$report.profileSha256 -cne $ExpectedProfileSha256 -or
        [string]$report.topologyId -cne "$ExpectedTopology-$ExpectedMessageTypeCount-v1" -or
        [int]$report.messageTypeCount -ne $ExpectedMessageTypeCount -or
        [string]$report.unityVersion -cne $ExpectedUnityVersion -or
        [string]$report.buildResult -cne 'Succeeded'
    ) {
        throw 'Shipping build report does not match the selected profile, topology, Unity version, or a succeeded build.'
    }
    $unixEpochUtc = [datetime]::new(1970, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)
    $earliestAllowedUnixMs = [long](
        ($BuildStartedUtc.ToUniversalTime().AddSeconds(-5)) - $unixEpochUtc
    ).TotalMilliseconds
    if (
        [long]$report.buildStartedUnixMs -lt $earliestAllowedUnixMs -or
        [long]$report.buildEndedUnixMs -lt [long]$report.buildStartedUnixMs -or
        [double]$report.buildDurationMs -le 0 -or
        [double]$report.reportedTotalTimeMs -lt 0 -or
        [long]$report.reportedTotalSizeBytes -lt 0
    ) {
        throw 'Shipping build report timing or size values are stale, inverted, or negative.'
    }
    $steps = @($report.steps)
    if ($steps.Count -eq 0) {
        throw 'Shipping build report must contain at least one build step.'
    }
    foreach ($step in $steps) {
        if ($step -isnot [pscustomobject]) {
            throw 'Shipping build report steps[] must be a JSON object.'
        }
        Assert-ExactJsonPropertyNames `
            -Value $step `
            -Expected @('name', 'depth', 'durationMs') `
            -Label 'Shipping build report steps[]'
        Assert-JsonValueType -Value $step.name -ExpectedKind string -Path 'shippingBuildReport.steps[].name'
        Assert-JsonValueType -Value $step.depth -ExpectedKind integer -Path 'shippingBuildReport.steps[].depth'
        Assert-JsonValueType -Value $step.durationMs -ExpectedKind number -Path 'shippingBuildReport.steps[].durationMs'
        if ([int]$step.depth -lt 0 -or [double]$step.durationMs -lt 0) {
            throw 'Shipping build report steps[] contains a negative depth or duration.'
        }
    }
}

function Test-ShippingStartupTimings {
    # SYNC: $cellTimingPropertyNames does not exist; the matrix wrapper copies
    # whatever the player wrote, so this list is the only declaration of the
    # timing contract.
    #
    # Cold-start diagnostics written by the shipping player. A measured phase is
    # a non-negative number; -1 means the mode never ran that phase. The
    # missing-root mutant measures nothing, so all of its phases must be exactly
    # -1. Accepting 0 there would let a fabricated zero pass for work that did
    # happen, such as constructing the bus.
    param(
        [Parameter(Mandatory = $true)][AllowNull()][object]$Value,
        [Parameter(Mandatory = $true)][ValidateSet('positive', 'missing-root-mutant')][string]$ExpectedMode,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$ExpectedDispatchLoopShape,
        [Parameter(Mandatory = $true)][ValidateRange(1, [int]::MaxValue)][int]$ExpectedDispatchLoopCount,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ($Value -isnot [pscustomobject]) {
        throw "$Path must be a JSON object."
    }
    $phaseProperties = @(
        'busConstructionUs',
        'rootProbePhaseUs',
        'registrationPhaseUs',
        'firstTypedDispatchUs',
        'typedPhaseUs',
        'untypedPhaseUs',
        'dispatchLoopNsPerOp',
        'trimUs',
        'teardownUs'
    )
    Assert-ExactJsonPropertyNames -Value $Value -Expected (@(
        'engineStartToRunMs',
        'stopwatchFrequency',
        'stopwatchIsHighResolution',
        'firstTypedDispatchCount',
        'dispatchLoopShape',
        'dispatchLoopCount'
    ) + $phaseProperties) -Label $Path
    foreach ($numberProperty in @('engineStartToRunMs') + $phaseProperties) {
        Assert-JsonValueType -Value $Value.$numberProperty -ExpectedKind number -Path "$Path.$numberProperty"
        if ([double]$Value.$numberProperty -lt 0 -and [double]$Value.$numberProperty -ne -1) {
            throw "$Path.$numberProperty must be a measured value or the -1 not-measured marker."
        }
    }
    foreach ($integerProperty in @('stopwatchFrequency', 'firstTypedDispatchCount', 'dispatchLoopCount')) {
        Assert-JsonValueType -Value $Value.$integerProperty -ExpectedKind integer -Path "$Path.$integerProperty"
    }
    Assert-JsonValueType -Value $Value.stopwatchIsHighResolution -ExpectedKind bool -Path "$Path.stopwatchIsHighResolution"
    Assert-JsonValueType -Value $Value.dispatchLoopShape -ExpectedKind string -Path "$Path.dispatchLoopShape"
    # engineStartToRunMs only has to be a real reading. A batchmode player that
    # reaches the first script before the engine clock advances would report 0,
    # and failing 40 cells over that would cost far more than it proves. The
    # Stopwatch frequency is a platform constant, so it must be positive.
    if ([double]$Value.engineStartToRunMs -lt 0 -or [long]$Value.stopwatchFrequency -le 0) {
        throw "$Path must record a non-negative engine start time and a positive Stopwatch frequency."
    }
    if ($ExpectedMode -ceq 'positive') {
        $unmeasuredPhases = @($phaseProperties | Where-Object { [double]$Value.$_ -lt 0 })
        if ($unmeasuredPhases.Count -gt 0) {
            throw "$Path is missing measurements for: $($unmeasuredPhases -join ', ')."
        }
        if (
            [int]$Value.firstTypedDispatchCount -ne 1 -or
            [int]$Value.dispatchLoopCount -ne $ExpectedDispatchLoopCount -or
            [double]$Value.dispatchLoopNsPerOp -le 0 -or
            [string]$Value.dispatchLoopShape -cne $ExpectedDispatchLoopShape
        ) {
            throw "$Path must record one first typed dispatch and $ExpectedDispatchLoopCount timed dispatches of $ExpectedDispatchLoopShape with a positive ns/op."
        }
        return
    }
    $measuredPhases = @($phaseProperties | Where-Object { [double]$Value.$_ -ne -1 })
    if (
        $measuredPhases.Count -gt 0 -or
        [int]$Value.firstTypedDispatchCount -ne -1 -or
        [int]$Value.dispatchLoopCount -ne -1 -or
        [string]$Value.dispatchLoopShape -cne ''
    ) {
        throw "$Path must mark every phase not measured for the missing-root mutant."
    }
}

function Write-ShippingCellEvidence {
    # One row of #506 build-time, size, and cold-start evidence for one clean
    # IL2CPP build and its player. Every input was validated before this call;
    # the manifest entries are the in-process ordered dictionaries produced by
    # Get-StandalonePlayerManifest. run-shipping-fidelity-matrix.ps1 copies
    # whatever this writes, so there is no second copy of the shape to keep in
    # step.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BuildReportPath,
        [Parameter(Mandatory = $true)][string]$PositiveResultPath,
        [Parameter(Mandatory = $true)][object]$PlayerDirectoryManifest,
        [Parameter(Mandatory = $true)][string]$PlayerExecutableName,
        [Parameter(Mandatory = $true)][string]$ProfileId,
        [Parameter(Mandatory = $true)][string]$ProfileSha256,
        [Parameter(Mandatory = $true)][string]$ManagedStrippingLevel,
        [Parameter(Mandatory = $true)][ValidateSet('semantic', 'cardinality')][string]$Topology,
        [Parameter(Mandatory = $true)][ValidateSet(1, 16, 18, 256, 1000)][int]$MessageTypeCount,
        [Parameter(Mandatory = $true)][string]$UnityVersion,
        [Parameter(Mandatory = $true)][ValidateSet('cold', 'warm')][string]$LibraryState,
        [Parameter(Mandatory = $true)][double]$EditorBuildWallClockMs,
        [Parameter(Mandatory = $true)][double]$PositivePlayerWallClockMs,
        [Parameter(Mandatory = $true)][double]$MutantPlayerWallClockMs
    )

    $buildReport = Get-Content -LiteralPath $BuildReportPath -Raw | ConvertFrom-Json
    $positiveResult = Get-Content -LiteralPath $PositiveResultPath -Raw | ConvertFrom-Json
    $playerFiles = @($PlayerDirectoryManifest['files'])
    $playerTotalBytes = [long]0
    $playerExecutableBytes = [long]-1
    $gameAssemblyBytes = [long]-1
    $gameAssemblyMatches = 0
    foreach ($playerFile in $playerFiles) {
        $playerFilePath = [string]$playerFile['path']
        $playerTotalBytes += [long]$playerFile['length']
        if ($playerFilePath -ieq $PlayerExecutableName) {
            $playerExecutableBytes = [long]$playerFile['length']
        }
        # Locate the IL2CPP native output the way capture-dispatch-codegen.ps1
        # already does: by leaf name, at any depth, ignoring case. Requiring one
        # exact root-relative spelling would fail every cell over a layout
        # detail that carries no evidence.
        if ([System.IO.Path]::GetFileName($playerFilePath) -ieq 'GameAssembly.dll') {
            $gameAssemblyBytes = [long]$playerFile['length']
            $gameAssemblyMatches++
        }
    }
    if ($playerExecutableBytes -lt 0 -or $gameAssemblyMatches -ne 1) {
        throw "Shipping player directory must contain $PlayerExecutableName and exactly one IL2CPP GameAssembly.dll."
    }
    Write-JsonArtifact -Path $Path -Value ([ordered]@{
            schemaVersion = 1
            measurementClass = 'characterization'
            profileId = $ProfileId
            profileSha256 = $ProfileSha256
            managedStrippingLevel = $ManagedStrippingLevel
            topologyId = "$Topology-$MessageTypeCount-v1"
            messageTypeCount = $MessageTypeCount
            unityVersion = $UnityVersion
            libraryState = $LibraryState
            editorBuildWallClockMs = $EditorBuildWallClockMs
            buildDurationMs = [double]$buildReport.buildDurationMs
            reportedTotalTimeMs = [double]$buildReport.reportedTotalTimeMs
            reportedTotalSizeBytes = [long]$buildReport.reportedTotalSizeBytes
            buildStepCount = @($buildReport.steps).Count
            playerFileCount = $playerFiles.Count
            playerTotalBytes = $playerTotalBytes
            playerExecutableBytes = $playerExecutableBytes
            gameAssemblyBytes = $gameAssemblyBytes
            positivePlayerWallClockMs = $PositivePlayerWallClockMs
            mutantPlayerWallClockMs = $MutantPlayerWallClockMs
            timings = $positiveResult.timings
        })
    Write-CiNotice (
        "Characterization (not a benchmark row) for shipping cell {0} x {1}: build {2:F0} ms in BuildPipeline ({3:F0} ms editor wall clock), player {4} bytes, GameAssembly {5} bytes, engine start to script {6:F0} ms, first typed dispatch {7:F1} us, dispatch loop {8:F1} ns/op over {9}." -f
        $ManagedStrippingLevel,
        "$Topology-$MessageTypeCount",
        [double]$buildReport.buildDurationMs,
        $EditorBuildWallClockMs,
        $playerTotalBytes,
        $gameAssemblyBytes,
        [double]$positiveResult.timings.engineStartToRunMs,
        [double]$positiveResult.timings.firstTypedDispatchUs,
        [double]$positiveResult.timings.dispatchLoopNsPerOp,
        [string]$positiveResult.timings.dispatchLoopShape
    )
}

function Assert-ShippingPlayerDirectoryManifest {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Value -isnot [pscustomobject]) {
        throw "$Label must be a JSON object."
    }
    Assert-ExactJsonPropertyNames `
        -Value $Value `
        -Expected @('schemaVersion', 'fileCount', 'files') `
        -Label $Label
    Assert-JsonValueType -Value $Value.schemaVersion -ExpectedKind integer -Path "$Label.schemaVersion"
    Assert-JsonValueType -Value $Value.fileCount -ExpectedKind integer -Path "$Label.fileCount"
    Assert-JsonValueType -Value $Value.files -ExpectedKind array -Path "$Label.files"
    $files = @($Value.files)
    if ([int]$Value.schemaVersion -ne 1 -or [int]$Value.fileCount -ne $files.Count -or $files.Count -eq 0) {
        throw "$Label does not contain a non-empty schema-version-1 file inventory."
    }
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        if ($file -isnot [pscustomobject]) {
            throw "$Label.files[] must be a JSON object."
        }
        Assert-ExactJsonPropertyNames `
            -Value $file `
            -Expected @('path', 'length', 'sha256') `
            -Label "$Label.files[]"
        Assert-JsonValueType -Value $file.path -ExpectedKind string -Path "$Label.files[].path"
        Assert-JsonValueType -Value $file.length -ExpectedKind integer -Path "$Label.files[].length"
        Assert-JsonValueType -Value $file.sha256 -ExpectedKind string -Path "$Label.files[].sha256"
        if (
            [string]::IsNullOrWhiteSpace([string]$file.path) -or
            [long]$file.length -lt 0 -or
            [string]$file.sha256 -cnotmatch '^[0-9A-F]{64}$'
        ) {
            throw "$Label.files[] contains an invalid path, length, or SHA-256."
        }
        $paths.Add([string]$file.path)
    }
    $sortedPaths = [string[]]@($paths.ToArray())
    [Array]::Sort($sortedPaths, [System.StringComparer]::Ordinal)
    $uniquePaths = @($paths | Sort-Object -Unique)
    if (
        ($paths -join "`n") -cne ($sortedPaths -join "`n") -or
        $uniquePaths.Count -ne $paths.Count
    ) {
        throw "$Label file paths must be unique and ordinally sorted."
    }
}

function Test-ShippingPlayerManifestEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateSet('semantic', 'cardinality')][string]$ExpectedTopology,
        [Parameter(Mandatory = $true)][ValidateSet(1, 16, 18, 256, 1000)][int]$ExpectedMessageTypeCount
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Shipping player manifest was not written at $Path."
    }
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($manifest -isnot [pscustomobject]) {
        throw 'Shipping player manifest must be a JSON object.'
    }
    Assert-ExactJsonPropertyNames -Value $manifest -Expected @(
        'schemaVersion',
        'topologyId',
        'messageTypeCount',
        'playerDirectoryManifestMatches',
        'playerDirectoryManifestBefore',
        'playerDirectoryManifestAfter',
        'runs'
    ) -Label 'Shipping player manifest'
    Assert-JsonValueType -Value $manifest.schemaVersion -ExpectedKind integer -Path 'shippingPlayerManifest.schemaVersion'
    Assert-JsonValueType -Value $manifest.topologyId -ExpectedKind string -Path 'shippingPlayerManifest.topologyId'
    Assert-JsonValueType -Value $manifest.messageTypeCount -ExpectedKind integer -Path 'shippingPlayerManifest.messageTypeCount'
    Assert-JsonValueType -Value $manifest.playerDirectoryManifestMatches -ExpectedKind bool -Path 'shippingPlayerManifest.playerDirectoryManifestMatches'
    Assert-JsonValueType -Value $manifest.runs -ExpectedKind array -Path 'shippingPlayerManifest.runs'
    Assert-ShippingPlayerDirectoryManifest `
        -Value $manifest.playerDirectoryManifestBefore `
        -Label 'shippingPlayerManifest.playerDirectoryManifestBefore'
    Assert-ShippingPlayerDirectoryManifest `
        -Value $manifest.playerDirectoryManifestAfter `
        -Label 'shippingPlayerManifest.playerDirectoryManifestAfter'
    foreach ($run in @($manifest.runs)) {
        Assert-JsonValueType -Value $run -ExpectedKind string -Path 'shippingPlayerManifest.runs[]'
    }
    $expectedRuns = @('positive', 'missing-root-mutant')
    $manifestsMatch = (
        ($manifest.playerDirectoryManifestBefore | ConvertTo-Json -Depth 10 -Compress) -ceq
        ($manifest.playerDirectoryManifestAfter | ConvertTo-Json -Depth 10 -Compress)
    )
    if (
        [int]$manifest.schemaVersion -ne 2 -or
        [string]$manifest.topologyId -cne "$ExpectedTopology-$ExpectedMessageTypeCount-v1" -or
        [int]$manifest.messageTypeCount -ne $ExpectedMessageTypeCount -or
        -not [bool]$manifest.playerDirectoryManifestMatches -or
        -not $manifestsMatch -or
        (@($manifest.runs) -join "`n") -cne ($expectedRuns -join "`n")
    ) {
        throw 'Shipping player manifest does not satisfy the exact unchanged-binary contract.'
    }
}

function Write-ShippingPackageResolutionEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$ArtifactsPath,
        [Parameter(Mandatory = $true)][string]$ExpectedRepoRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedManifestSha256
    )

    $packagesPath = Join-Path $ProjectPath 'Packages'
    $packageEntries = @(Get-ChildItem -LiteralPath $packagesPath -Force)
    $actualEntryNames = [string[]]@($packageEntries | ForEach-Object { $_.Name })
    [Array]::Sort($actualEntryNames, [System.StringComparer]::Ordinal)
    $expectedEntryNames = [string[]]@('manifest.json', 'packages-lock.json')
    if (($actualEntryNames -join "`n") -cne ($expectedEntryNames -join "`n")) {
        throw 'Shipping Packages contains an unexpected entry after package resolution.'
    }
    foreach ($packageEntry in $packageEntries) {
        if ($packageEntry.PSIsContainer -or (Test-IsReparsePoint -Path $packageEntry.FullName)) {
            throw "Shipping Packages entry '$($packageEntry.Name)' must be a regular file."
        }
    }

    $manifestPath = Join-Path $packagesPath 'manifest.json'
    $lockPath = Join-Path $packagesPath 'packages-lock.json'
    $resolvedManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($resolvedManifestSha256 -cne $ExpectedManifestSha256) {
        throw 'Shipping package manifest hash changed during package resolution.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest -isnot [pscustomobject] -or $manifest.dependencies -isnot [pscustomobject]) {
        throw 'Shipping package manifest and dependencies must be JSON objects.'
    }
    Assert-ExactJsonPropertyNames `
        -Value $manifest `
        -Expected @('dependencies') `
        -Label 'Shipping package manifest'
    $manifestDependencyNames = @($manifest.dependencies.PSObject.Properties.Name)
    if (
        $manifestDependencyNames.Count -ne 1 -or
        $manifestDependencyNames[0] -cne 'com.wallstop-studios.dxmessaging'
    ) {
        throw 'Shipping package manifest differs from the single reviewed file dependency.'
    }
    $manifestDependency = $manifest.dependencies.'com.wallstop-studios.dxmessaging'
    Assert-JsonValueType `
        -Value $manifestDependency `
        -ExpectedKind string `
        -Path 'shippingPackageManifest.dependencies.com.wallstop-studios.dxmessaging'
    $expectedManifestDependency = "file:$(ConvertTo-UnityFileUriPath -Path (Resolve-FullPath -Path $ExpectedRepoRoot))"
    if ([string]$manifestDependency -cne $expectedManifestDependency) {
        throw 'Shipping package manifest does not reference the reviewed repository root.'
    }

    $packageLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    if ($packageLock -isnot [pscustomobject] -or $packageLock.dependencies -isnot [pscustomobject]) {
        throw 'Shipping packages-lock and dependencies must be JSON objects.'
    }
    Assert-ExactJsonPropertyNames `
        -Value $packageLock `
        -Expected @('dependencies') `
        -Label 'Shipping packages-lock'
    $lockDependencyNames = @($packageLock.dependencies.PSObject.Properties.Name)
    if (
        $lockDependencyNames.Count -ne 1 -or
        $lockDependencyNames[0] -cne 'com.wallstop-studios.dxmessaging'
    ) {
        throw 'Shipping packages-lock differs from the exact one-package graph.'
    }
    $lockDependencyProperty = $packageLock.dependencies.PSObject.Properties[
        'com.wallstop-studios.dxmessaging'
    ]
    $lockDependency = if ($null -eq $lockDependencyProperty) {
        $null
    } else {
        $lockDependencyProperty.Value
    }
    if ($null -ne $lockDependency) {
        Assert-ExactJsonPropertyNames `
            -Value $lockDependency `
            -Expected @('version', 'depth', 'source', 'dependencies') `
            -Label 'Shipping packages-lock dependency'
        Assert-JsonValueType -Value $lockDependency.version -ExpectedKind string -Path 'shippingPackagesLock.version'
        Assert-JsonValueType -Value $lockDependency.depth -ExpectedKind integer -Path 'shippingPackagesLock.depth'
        Assert-JsonValueType -Value $lockDependency.source -ExpectedKind string -Path 'shippingPackagesLock.source'
        if ($lockDependency.dependencies -isnot [pscustomobject]) {
            throw 'Shipping packages-lock transitive dependencies must be a JSON object.'
        }
    }
    $transitiveDependencyNames = @()
    if ($null -ne $lockDependency) {
        $transitiveDependencyNames = @(
            $lockDependency.dependencies.PSObject.Properties |
                ForEach-Object { $_.Name }
        )
    }
    if (
        $null -eq $lockDependency -or
        [string]$lockDependency.source -cne 'local' -or
        [int]$lockDependency.depth -ne 0 -or
        [string]$lockDependency.version -cne $expectedManifestDependency -or
        $transitiveDependencyNames.Count -ne 0
    ) {
        throw 'Shipping packages-lock does not exactly resolve the reviewed checkout as one direct local dependency.'
    }

    $resolvedInputEntries = New-Object System.Collections.Generic.List[object]
    foreach ($resolvedInputPath in @($manifestPath, $lockPath)) {
        $resolvedInputEntries.Add([ordered]@{
                path = "Packages/$([System.IO.Path]::GetFileName($resolvedInputPath))"
                length = [long](Get-Item -LiteralPath $resolvedInputPath).Length
                sha256 = (Get-FileHash -LiteralPath $resolvedInputPath -Algorithm SHA256).Hash.ToLowerInvariant()
            })
    }
    Write-JsonArtifact `
        -Path (Join-Path $ArtifactsPath 'shipping-resolved-package-inputs.json') `
        -Value ([ordered]@{
            schemaVersion = 1
            resolvedPackage = [ordered]@{
                packageId = 'com.wallstop-studios.dxmessaging'
                source = 'local'
                depth = 0
                versionScheme = 'file'
            }
            files = @($resolvedInputEntries.ToArray())
        })
}

function Test-ShippingFidelityResult {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateSet('positive', 'missing-root-mutant')][string]$ExpectedMode,
        [Parameter(Mandatory = $true)][string]$ExpectedProfileId,
        [Parameter(Mandatory = $true)][string]$ExpectedProfileSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedUnityVersion,
        [Parameter(Mandatory = $true)][ValidateSet('semantic', 'cardinality')][string]$ExpectedTopology,
        [Parameter(Mandatory = $true)][ValidateSet(1, 16, 18, 256, 1000)][int]$ExpectedMessageTypeCount,
        [Parameter(Mandatory = $true)][ValidateRange(1, [int]::MaxValue)][int]$ExpectedDispatchLoopCount
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Shipping player did not write $ExpectedMode evidence at $Path."
    }
    $result = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-ExactJsonPropertyNames -Value $result -Expected @(
        'schemaVersion',
        'profileId',
        'profileSha256',
        'topologyId',
        'messageTypeCount',
        'unityVersion',
        'mode',
        'success',
        'unityIncludeTests',
        'rootedUntypedProbeCount',
        'typedDispatchCount',
        'untypedDispatchCount',
        'rootedUntypedShapes',
        'typedDispatchShapes',
        'untypedDispatchShapes',
        'missingRootFailureObserved',
        'failureType',
        'failureMessage',
        'loadedAssemblies',
        'timings'
    ) -Label "Shipping $ExpectedMode result"
    foreach ($stringProperty in @(
        'profileId',
        'profileSha256',
        'topologyId',
        'unityVersion',
        'mode',
        'failureType',
        'failureMessage'
    )) {
        Assert-JsonValueType `
            -Value $result.$stringProperty `
            -ExpectedKind string `
            -Path "shippingResult.$stringProperty"
    }
    foreach ($booleanProperty in @('success', 'unityIncludeTests', 'missingRootFailureObserved')) {
        Assert-JsonValueType `
            -Value $result.$booleanProperty `
            -ExpectedKind bool `
            -Path "shippingResult.$booleanProperty"
    }
    foreach ($integerProperty in @(
        'schemaVersion',
        'messageTypeCount',
        'rootedUntypedProbeCount',
        'typedDispatchCount',
        'untypedDispatchCount'
    )) {
        Assert-JsonValueType `
            -Value $result.$integerProperty `
            -ExpectedKind integer `
            -Path "shippingResult.$integerProperty"
    }
    foreach ($arrayProperty in @(
        'rootedUntypedShapes',
        'typedDispatchShapes',
        'untypedDispatchShapes',
        'loadedAssemblies'
    )) {
        Assert-JsonValueType -Value $result.$arrayProperty -ExpectedKind array -Path "shippingResult.$arrayProperty"
    }
    if (
        [int]$result.schemaVersion -ne 3 -or
        [string]$result.profileId -cne $ExpectedProfileId -or
        [string]$result.profileSha256 -cne $ExpectedProfileSha256 -or
        [string]$result.topologyId -cne "$ExpectedTopology-$ExpectedMessageTypeCount-v1" -or
        [int]$result.messageTypeCount -ne $ExpectedMessageTypeCount -or
        [string]$result.unityVersion -cne $ExpectedUnityVersion -or
        [string]$result.mode -cne $ExpectedMode -or
        -not [bool]$result.success -or
        [bool]$result.unityIncludeTests -or
        -not [string]::IsNullOrEmpty([string]$result.failureType) -or
        -not [string]::IsNullOrEmpty([string]$result.failureMessage)
    ) {
        throw "Shipping $ExpectedMode result does not satisfy the success contract."
    }
    [string[]]$expectedShapes = @()
    if ($ExpectedMode -ceq 'positive') {
        $expectedShapes = @(
            Get-ExpectedShippingShapeNames `
                -Topology $ExpectedTopology `
                -MessageTypeCount $ExpectedMessageTypeCount
        )
    }
    # Read the loop shape from the shared helper, not from the ordering of the
    # expected-shape inventory. Those two lists answer different questions and
    # coupling them would fail the leg if the probe order ever changed.
    $expectedDispatchLoopShape = if ($ExpectedMode -ceq 'positive') {
        Get-ShippingDispatchLoopShape -Topology $ExpectedTopology -MessageTypeCount $ExpectedMessageTypeCount
    } else {
        ''
    }
    Test-ShippingStartupTimings `
        -Value $result.timings `
        -ExpectedMode $ExpectedMode `
        -ExpectedDispatchLoopShape $expectedDispatchLoopShape `
        -ExpectedDispatchLoopCount $ExpectedDispatchLoopCount `
        -Path 'shippingResult.timings'
    foreach ($shapeProperty in @(
        'rootedUntypedShapes',
        'typedDispatchShapes',
        'untypedDispatchShapes'
    )) {
        Assert-ExactJsonStringArray `
            -Value @($result.$shapeProperty) `
            -Expected $expectedShapes `
            -Path "shippingResult.$shapeProperty"
    }
    if ($ExpectedMode -ceq 'positive') {
        if (
            [int]$result.rootedUntypedProbeCount -ne $ExpectedMessageTypeCount -or
            [int]$result.typedDispatchCount -ne $ExpectedMessageTypeCount -or
            [int]$result.untypedDispatchCount -ne $ExpectedMessageTypeCount -or
            [bool]$result.missingRootFailureObserved
        ) {
            throw "Shipping positive result does not contain the required $ExpectedMessageTypeCount rooted, typed, and untyped dispatches."
        }
    } elseif (
        [int]$result.rootedUntypedProbeCount -ne 0 -or
        [int]$result.typedDispatchCount -ne 0 -or
        [int]$result.untypedDispatchCount -ne 0 -or
        -not [bool]$result.missingRootFailureObserved
    ) {
        throw 'Shipping missing-root mutant did not observe the required rooted-bridge failure.'
    }
    foreach ($rawAssemblyName in @($result.loadedAssemblies)) {
        Assert-JsonValueType `
            -Value $rawAssemblyName `
            -ExpectedKind string `
            -Path 'shippingResult.loadedAssemblies[]'
    }
    $loadedAssemblies = [string[]]@($result.loadedAssemblies)
    Assert-NoShippingTestAssemblies -AssemblyNames $loadedAssemblies -Label "Shipping $ExpectedMode loaded assembly inventory"
    foreach ($requiredAssembly in @('Assembly-CSharp', 'WallstopStudios.DxMessaging')) {
        if ($loadedAssemblies -cnotcontains $requiredAssembly) {
            throw "Shipping $ExpectedMode loaded assembly inventory is missing '$requiredAssembly'."
        }
    }
}

function Invoke-ShippingFidelityPlayer {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][ValidateSet('positive', 'missing-root-mutant')][string]$Mode,
        [Parameter(Mandatory = $true)][string]$ResultPath,
        [Parameter(Mandatory = $true)][string]$RuntimeProfilePath,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [int]$TimeoutSeconds = 1800
    )

    $arguments = @(
        '-batchmode',
        '-nographics',
        '-logFile', '-',
        '-dxmShippingResult', $ResultPath,
        '-dxmRuntimeProfile', $RuntimeProfilePath,
        '-dxmShippingMode', $Mode
    )
    return Invoke-ProcessWithTreeKillTimeout `
        -FilePath $ExecutablePath `
        -Arguments $arguments `
        -TimeoutSeconds $TimeoutSeconds `
        -LogPath $LogPath `
        -Label "Run shipping-fidelity player ($Mode)"
}

function Test-NUnitResults {
    # The NUnit results.xml is the SOLE source of truth for editmode/playmode and
    # the standalone player run. $UnityExitCode is the process exit code of the
    # editor/player that produced the file; it is ADVISORY only -- a valid passing
    # results.xml means the run succeeded EVEN IF the process then exited non-zero
    # (a benign background-thread shutdown-race crash after RunFinished already
    # wrote the file). A missing/invalid/failing file still fails loudly, and the
    # exit code is folded into the diagnostics so a crash-before-results is named.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$LogPath,
        [string]$Project,
        [int]$UnityExitCode = 0
    )

    $exitNote = if ($UnityExitCode -ne 0) {
        " Unity exited $UnityExitCode / $(Get-NativeExitCodeDescription -ExitCode $UnityExitCode) (the results FILE, not the exit code, is the source of truth)."
    } else {
        ''
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-CiError "No NUnit results XML exists at $Path for $Label.$exitNote"
        Write-UnityResultFailureDiagnostics -LogPath $LogPath -Project $Project -Label $Label
        throw "Unity did not produce NUnit results for $Label.$exitNote"
    }

    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    $run = $xml.SelectSingleNode('//test-run')
    if (-not $run) {
        Write-CiError "NUnit results at $Path do not contain a <test-run> element.$exitNote"
        Write-UnityResultFailureDiagnostics -LogPath $LogPath -Project $Project -Label $Label
        throw "Invalid NUnit results for $Label."
    }

    $total = [int]$run.total
    $passed = [int]$run.passed
    $failed = [int]$run.failed
    $skipped = [int]$run.skipped

    Write-Host "Results: total=$total passed=$passed failed=$failed skipped=$skipped"
    Write-SuiteWallClockSummary -LogPath $LogPath -Label $Label
    if ($total -lt 1) {
        Write-CiError "0 tests ran for $Label -- check assembly selection and package testables.$exitNote"
        throw "0 tests ran for $Label."
    }
    if ($failed -gt 0) {
        # Enumerate WHICH tests failed (fullname + message + stack) BEFORE the
        # throw so the operator sees the actionable detail, not just the count.
        # Best-effort inside the helper's own try/catch -- it never masks the
        # real failure below. ($exitNote is intentionally omitted here: when tests
        # genuinely failed, the named failing tests ARE the actionable signal and
        # the producing process's exit code is noise. The exit note is folded into
        # the missing-file / invalid / zero-test branches above, where the exit
        # code IS the most informative remaining clue.)
        Write-UnityFailedTestAnnotations -Xml $xml -Label $Label
        Write-CiError "$failed tests failed for $Label."
        throw "$failed tests failed for $Label."
    }

    # PASS. If the producing process exited non-zero despite the valid passing
    # file, narrate the benign post-work shutdown crash (and KEEP it green).
    if ($UnityExitCode -ne 0) {
        Write-UnityBenignExitWarning -Label $Label -ExitCode $UnityExitCode -LogPath $LogPath
    }
    Write-CiNotice "${Label}: total=$total passed=$passed failed=$failed skipped=$skipped"
}

$RepoRoot = Resolve-FullPath -Path $RepoRoot
Assert-RepoRoot -Path $RepoRoot
$ArtifactsPath = Resolve-FullPath -Path $ArtifactsPath
New-Item -ItemType Directory -Force -Path $ArtifactsPath | Out-Null

$isShippingFidelity = $TestMode -eq 'shipping'
if ($isShippingFidelity) {
    if (-not [string]::IsNullOrWhiteSpace($AssemblyNames)) {
        throw 'AssemblyNames must be empty for a shipping-fidelity player.'
    }
    if ($IncludeComparisons) {
        throw 'IncludeComparisons is not valid for a shipping-fidelity player.'
    }
    if (-not [string]::IsNullOrWhiteSpace($TestCategory)) {
        throw 'TestCategory is not valid for a shipping-fidelity player.'
    }
    if ($StandaloneScriptingBackend -cne 'IL2CPP') {
        throw 'A shipping-fidelity player requires the IL2CPP scripting backend.'
    }
    if ([string]::IsNullOrWhiteSpace($CanonicalProfilePath)) {
        throw 'A shipping-fidelity player requires CanonicalProfilePath.'
    }
    if ($StandalonePlayerRunCount -ne 1) {
        throw 'StandalonePlayerRunCount is not valid for a shipping-fidelity player.'
    }
    if (
        ($ShippingTopology -ceq 'semantic' -and $ShippingMessageTypeCount -ne 18) -or
        ($ShippingTopology -ceq 'cardinality' -and $ShippingMessageTypeCount -notin @(1, 16, 256, 1000))
    ) {
        throw 'Shipping fidelity requires semantic topology with 18 message types or cardinality topology with 1, 16, 256, or 1000 message types.'
    }
} elseif ([string]::IsNullOrWhiteSpace($AssemblyNames)) {
    throw "AssemblyNames must be non-empty for TestMode '$TestMode'."
}

$canonicalProfileId = ''
$canonicalProfileSha256 = ''
$resolvedCanonicalProfilePath = ''
$managedStrippingLevel = 'Disabled'
$includeTestAssemblies = $true
if (-not [string]::IsNullOrWhiteSpace($CanonicalProfilePath)) {
    if (($TestMode -ne 'standalone' -and -not $isShippingFidelity) -or $StandaloneScriptingBackend -cne 'IL2CPP') {
        throw 'CanonicalProfilePath is valid only for a standalone or shipping IL2CPP run.'
    }
    $profileCandidate = if ([System.IO.Path]::IsPathRooted($CanonicalProfilePath)) {
        $CanonicalProfilePath
    } else {
        [System.IO.Path]::Combine($RepoRoot, $CanonicalProfilePath)
    }
    $resolvedCanonicalProfilePath = Resolve-FullPath -Path $profileCandidate
    $profileValidatorPath = Join-Path $PSScriptRoot 'validate-il2cpp-profile.ps1'
    & $profileValidatorPath -ProfilePath $resolvedCanonicalProfilePath -ProfileOnly
    $canonicalProfile = Get-Content -LiteralPath $resolvedCanonicalProfilePath -Raw | ConvertFrom-Json
    $canonicalProfileId = [string]$canonicalProfile.profileId
    $canonicalProfileSha256 = (Get-FileHash -LiteralPath $resolvedCanonicalProfilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $managedStrippingLevel = [string]$canonicalProfile.configuration.managedStrippingLevel
    $includeTestAssemblies = [bool]$canonicalProfile.buildOptions.includeTestAssemblies
    if ($isShippingFidelity) {
        $shippingProfileLevels = [ordered]@{
            'shipping-fidelity-il2cpp-minimal-player-v1' = 'Minimal'
            'shipping-fidelity-il2cpp-low-player-v1' = 'Low'
            'shipping-fidelity-il2cpp-medium-player-v1' = 'Medium'
            'shipping-fidelity-il2cpp-player-v1' = 'High'
        }
        if (
            -not $shippingProfileLevels.Contains($canonicalProfileId) -or
            $managedStrippingLevel -cne $shippingProfileLevels[$canonicalProfileId] -or
            $includeTestAssemblies
        ) {
            throw 'Shipping fidelity requires a reviewed Minimal, Low, Medium, or High profile with includeTestAssemblies=false.'
        }
    } elseif (
        $canonicalProfileId -cne 'canonical-il2cpp-verdict-player-v1' -or
        $managedStrippingLevel -cne 'Disabled' -or
        -not $includeTestAssemblies
    ) {
        throw 'The standalone test player requires its reviewed profile with Disabled stripping and includeTestAssemblies=true.'
    }
    $profileArtifactFileName = [System.IO.Path]::GetFileName($resolvedCanonicalProfilePath)
    $profileArtifactPath = Join-Path $ArtifactsPath $profileArtifactFileName
    Copy-Item -LiteralPath $resolvedCanonicalProfilePath -Destination $profileArtifactPath -Force
    $profileHashFileName = "{0}.sha256" -f [System.IO.Path]::GetFileNameWithoutExtension($profileArtifactFileName)
    [System.IO.File]::WriteAllText(
        (Join-Path $ArtifactsPath $profileHashFileName),
        "$canonicalProfileSha256  $profileArtifactFileName`n"
    )
}

Initialize-UnityCacheEnvironment -Root $RepoRoot -Version $UnityVersion -Path $CachePath

# Release is now the repo-wide Unity CI contract. The historical switches remain
# accepted for workflow/back-compat, but the effective mode is always Release:
# editor/test compilations get -releaseCodeOptimization, and standalone generated
# players omit BuildOptions.Development.
$UseReleaseCodeOptimization = $true
$UseReleasePlayerBuild = $true

$ProjectPath = Initialize-EphemeralProject `
    -Root $RepoRoot `
    -Version $UnityVersion `
    -Mode $TestMode `
    -Path $ProjectPath `
    -IncludeComparisons:$IncludeComparisons `
    -Backend $StandaloneScriptingBackend `
    -ManagedStrippingLevel $managedStrippingLevel `
    -DevelopmentBuild:(-not $UseReleasePlayerBuild) `
    -CanonicalProfileId $canonicalProfileId `
    -CanonicalProfileSha256 $canonicalProfileSha256 `
    -ShippingTopology $ShippingTopology `
    -ShippingMessageTypeCount $ShippingMessageTypeCount `
    -RepoRoot $RepoRoot `
    -ArtifactsPath $ArtifactsPath
$shippingPreResolutionManifestSha256 = ''
if ($isShippingFidelity) {
    $shippingPreResolutionManifestSha256 = (
        Get-FileHash `
            -LiteralPath (Join-Path $ProjectPath 'Packages/manifest.json') `
            -Algorithm SHA256
    ).Hash.ToLowerInvariant()
}
$LibraryPath = Join-Path $ProjectPath 'Library'
$LibraryEntries = @(
    if (Test-Path -LiteralPath $LibraryPath -PathType Container) {
        Get-ChildItem -LiteralPath $LibraryPath -Force | Select-Object -First 1
    }
)
$LibraryState = if ($LibraryEntries.Count -gt 0) { 'warm' } else { 'cold' }
New-Item -ItemType Directory -Force -Path $LibraryPath | Out-Null

Write-Host "::group::Ephemeral Unity project"
Write-Host "RepoRoot: $RepoRoot"
Write-Host "ProjectPath: $ProjectPath"
Write-Host "LibraryPath: $LibraryPath"
Write-Host "LibraryState: $LibraryState"
Write-Host "ArtifactsPath: $ArtifactsPath"
Write-Host "IncludeComparisons: $IncludeComparisons"
Write-Host "StandaloneScriptingBackend: $StandaloneScriptingBackend"
Write-Host "StandalonePlayerRunCount: $StandalonePlayerRunCount"
Write-Host "ManagedStrippingLevel: $managedStrippingLevel"
Write-Host "IncludeTestAssemblies: $includeTestAssemblies"
if ($isShippingFidelity) {
    Write-Host "ShippingTopology: $ShippingTopology"
    Write-Host "ShippingMessageTypeCount: $ShippingMessageTypeCount"
}
Write-Host "ReleasePlayerBuild: $UseReleasePlayerBuild"
Write-Host "ReleaseCodeOptimization: $UseReleaseCodeOptimization"
Write-Host "Manifest:"
Get-Content -LiteralPath ([System.IO.Path]::Combine($ProjectPath, 'Packages', 'manifest.json'))
Write-Host "Package analyzer payload (Runtime/Analyzers - applies natively, no Assets copy):"
$analyzerPayloadDir = [System.IO.Path]::Combine($RepoRoot, 'Runtime', 'Analyzers')
if (Test-Path -LiteralPath $analyzerPayloadDir -PathType Container) {
    Get-ChildItem -LiteralPath $analyzerPayloadDir -Filter '*.dll' -File |
        Select-Object -ExpandProperty Name |
        Sort-Object |
        ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host "  (missing)"
}
Write-Host "::endgroup::"

if ($GenerateOnly) {
    Write-CiNotice "Generated ephemeral Unity project only: $ProjectPath"
    exit 0
}

if (-not $UnityEditorPath -or $UnityEditorPath.Trim().Length -eq 0) {
    throw 'UnityEditorPath is required. Validate the manually installed editor before running Unity tests.'
}

if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity editor not found: $UnityEditorPath"
}

# Export the resolved editor path so a workflow if:always() step (which runs in a
# SEPARATE process after this one exits) can run `Unity.exe -returnlicense` to
# return the seat as defense-in-depth.
if ($env:GITHUB_ENV) {
    Add-Content -LiteralPath $env:GITHUB_ENV -Value "UNITY_EDITOR_PATH=$UnityEditorPath"
}

# Classic SERIAL activation: the paid seat is activated from UNITY_SERIAL +
# UNITY_EMAIL + UNITY_PASSWORD and explicitly returned on EVERY exit path so the
# seat is never leaked. All three credentials are required together; we test each
# with IsNullOrWhiteSpace so a blank-but-set secret counts as missing.
$hasLicenseCreds = (
    -not [string]::IsNullOrWhiteSpace($env:UNITY_SERIAL) -and
    -not [string]::IsNullOrWhiteSpace($env:UNITY_EMAIL) -and
    -not [string]::IsNullOrWhiteSpace($env:UNITY_PASSWORD)
)
# In CI all three credentials are MANDATORY: a missing one means the editor would
# launch unlicensed and fail opaquely. The error names the missing VARS (never
# their values). Locally, missing creds is fine -- we assume the machine is
# already licensed (Hub sign-in / a local .ulf) and simply skip activate/return.
if ($env:GITHUB_ACTIONS -eq 'true' -and -not $hasLicenseCreds) {
    $missing = @()
    if ([string]::IsNullOrWhiteSpace($env:UNITY_SERIAL)) { $missing += 'UNITY_SERIAL' }
    if ([string]::IsNullOrWhiteSpace($env:UNITY_EMAIL)) { $missing += 'UNITY_EMAIL' }
    if ([string]::IsNullOrWhiteSpace($env:UNITY_PASSWORD)) { $missing += 'UNITY_PASSWORD' }
    throw "Serial Unity activation requires UNITY_SERIAL, UNITY_EMAIL, and UNITY_PASSWORD in CI. Missing or empty: $($missing -join ', ')."
}

# Array-wrap the capture so it is ALWAYS an array under Set-StrictMode -Version
# Latest. Get-AcceleratorArguments `return @()` on its empty path emits ZERO
# objects, so a bare `$x = Get-Foo` assigns AutomationNull (the empty array
# unwraps to nothing). Then reading `$x.Count` THROWS "property 'Count' cannot be
# found on this object" under StrictMode 2.0+ (verified on pwsh 7.6.1). @(...)
# forces Count 0 when empty so the read is safe. (The later `... + $x` concat was
# fine either way: `+` DROPS the empty/AutomationNull capture rather than adding
# it -- only a LITERAL $null operand would add a spurious element.)
$acceleratorArgs = @(Get-AcceleratorArguments -Endpoint $env:UNITY_ACCELERATOR_ENDPOINT -Version $UnityVersion -Mode $TestMode)
if ($acceleratorArgs.Count -gt 0) {
    Write-CiNotice "Unity Accelerator enabled for namespace dxmessaging-$UnityVersion-$TestMode (endpoint normalized at the script boundary; value masked)."
} else {
    Write-CiNotice "Unity Accelerator disabled; UNITY_ACCELERATOR_ENDPOINT is unset."
}

$testPlatform = switch ($TestMode) {
    'editmode' { 'EditMode' }
    'playmode' { 'PlayMode' }
    'standalone' { 'StandaloneWindows64' }
    'shipping' { '' }
}

$categoryArgs = @()
if (-not [string]::IsNullOrWhiteSpace($TestCategory)) {
    $categoryArgs = @('-testCategory', $TestCategory)
    Write-CiNotice "Unity test category filter enabled: $TestCategory"
} else {
    Write-CiNotice "Unity test category filter disabled."
}

$resultsPath = Join-Path $ArtifactsPath 'results.xml'
$logPath = Join-Path $ArtifactsPath 'unity.log'
$configureLogPath = Join-Path $ArtifactsPath 'configure.log'
$startupProbeLogPath = Join-Path $ArtifactsPath 'unity-startup-probe.log'
# The standalone-configure SUCCESS MARKER: DxmCiTestConfigurator.Apply writes it
# as its final action (path handed in via DXM_CONFIGURE_MARKER_PATH). A fresh
# marker is the source of truth that the configure pass completed -- even if Unity
# then crashed in a background thread during shutdown and returned a crash exit
# code -- so we never fail a successful configure on a benign teardown crash.
$configureMarkerPath = Join-Path $ArtifactsPath 'configure-complete.marker'
$configuredProfileEvidencePath = Join-Path $ArtifactsPath 'configured-profile.json'
$prebuildProfileEvidencePath = Join-Path $ArtifactsPath 'prebuild-profile.json'
$postbuildProfileEvidencePath = Join-Path $ArtifactsPath 'postbuild-profile.json'
$buildOptionsProfileEvidencePath = Join-Path $ArtifactsPath 'build-options-profile.json'
$runtimeProfileEvidencePath = Join-Path $ArtifactsPath 'runtime-profile.json'

# STANDALONE split-build artifacts. The built IL2CPP player goes under a stable
# per-run project Build directory, not project Temp: Unity's test player build
# pipeline can populate Temp/PlayerWithTests or copy through Temp and then clean
# it before this script's post-build assertion runs. The player still stays out
# of $ArtifactsPath because a full IL2CPP player is hundreds of MB; only the
# small player log and NUnit XML are uploaded.
$standaloneExe = if ($isShippingFidelity) {
    [System.IO.Path]::Combine($ProjectPath, 'Build', 'DxmShippingPlayer', 'DxmShippingPlayer.exe')
} else {
    [System.IO.Path]::Combine($ProjectPath, 'Build', 'DxmTestPlayer', 'DxmTestPlayer.exe')
}
$playerLogPath = Join-Path $ArtifactsPath 'player.log'
$shippingBuildMarkerPath = Join-Path $ArtifactsPath 'shipping-build-complete.marker'
$shippingAssemblyEvidencePath = Join-Path $ArtifactsPath 'shipping-assemblies.json'
$shippingPositiveResultPath = Join-Path $ArtifactsPath 'shipping-positive.json'
$shippingPositiveRuntimeProfilePath = Join-Path $ArtifactsPath 'shipping-positive-runtime-profile.json'
$shippingMutantResultPath = Join-Path $ArtifactsPath 'shipping-missing-root-mutant.json'
$shippingMutantRuntimeProfilePath = Join-Path $ArtifactsPath 'shipping-missing-root-mutant-runtime-profile.json'

# Activation/return carry the serial/email/password in their argument arrays and
# Unity may echo account/serial fragments into the activation log, so these logs
# MUST NOT live under $ArtifactsPath (the workflow uploads that as an artifact and
# the credentials would leak). Write them to a NON-uploaded temp dir instead.
$licenseLogDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$activateLogPath = Join-Path $licenseLogDir "unity-activate-$UnityVersion-$TestMode.log"
$preflightReturnLogPath = Join-Path $licenseLogDir "unity-return-preflight-$UnityVersion-$TestMode.log"
$returnLogPath = Join-Path $licenseLogDir "unity-return-$UnityVersion-$TestMode.log"

# The workflow may treat only this run's post-activation return as cleanup proof.
# Delete any prior-run evidence before activation, and keep return-at-start output
# in a separate file so it can never confirm cleanup for the activation below.
if (Test-Path -LiteralPath $returnLogPath) {
    Remove-Item -LiteralPath $returnLogPath -Force
}

# Return-at-start (defense-in-depth): reclaim a seat that a PRIOR force-killed run
# on this persistent self-hosted runner may have leaked before its own finally /
# the workflow if:always() step could run. Best-effort and never throws; if no
# seat is held this is a harmless no-op. Done BEFORE the activate so we start each
# run from a clean licensing state.
if ($hasLicenseCreds) {
    Invoke-UnityLicenseReturn -EditorPath $UnityEditorPath -Email $env:UNITY_EMAIL -Password $env:UNITY_PASSWORD -LogPath $preflightReturnLogPath
}

try {
    Invoke-UnityNativeStartupProbe -EditorPath $UnityEditorPath -LogPath $startupProbeLogPath

    # Activate the paid seat BEFORE configure/run so the test editor launches
    # licensed. Activation THROWS on failure (caught by this try's finally, which
    # still returns the seat). Skipped locally when creds are absent (the machine
    # is assumed already licensed).
    if ($hasLicenseCreds) {
        Invoke-UnityLicenseActivate -EditorPath $UnityEditorPath -Serial $env:UNITY_SERIAL -Email $env:UNITY_EMAIL -Password $env:UNITY_PASSWORD -LogPath $activateLogPath
    }

    if ($TestMode -eq 'standalone' -or $isShippingFidelity) {
        $configurationScope = if ($isShippingFidelity) { 'shipping-fidelity' } else { 'standalone' }
        # CONFIGURE the standalone IL2CPP project. The CONFIGURED PROJECT (proven by
        # the success marker DxmCiTestConfigurator.Apply writes as its final action)
        # is the source of truth -- NOT Unity's process exit code. Delete any stale
        # marker, hand the path in via DXM_CONFIGURE_MARKER_PATH, and validate a
        # FRESH marker after the run. A non-zero exit with a fresh marker is a benign
        # post-work shutdown crash (for example the DirectoryMonitor file-watcher
        # thread faulting during teardown, which returns 0xC0000005 even though the
        # configuration fully succeeded); a MISSING marker is a real configure
        # failure that fails loudly with the usual diagnostics.
        if (Test-Path -LiteralPath $configureMarkerPath -PathType Leaf) {
            Remove-Item -LiteralPath $configureMarkerPath -Force
        }
        $env:DXM_CONFIGURE_MARKER_PATH = $configureMarkerPath
        if (Test-Path -LiteralPath $configuredProfileEvidencePath -PathType Leaf) {
            Remove-Item -LiteralPath $configuredProfileEvidencePath -Force
        }
        if (
            -not [string]::IsNullOrWhiteSpace($canonicalProfileId) -and
            -not $isShippingFidelity
        ) {
            $env:DXM_CONFIGURED_PROFILE_PATH = $configuredProfileEvidencePath
        }
        $configureStartedUtc = [DateTime]::UtcNow
        $configureArgs = @(
            '-quit',
            '-batchmode',
            '-nographics',
            '-projectPath', $ProjectPath,
            '-buildTarget', 'StandaloneWindows64',
            '-executeMethod', 'DxmCiTestConfigurator.Apply',
            '-logFile', '-'
        ) + $acceleratorArgs
        $configureExit = Invoke-UnityEditor `
            -EditorPath $UnityEditorPath `
            -Arguments $configureArgs `
            -Label "Configure $configurationScope IL2CPP project" `
            -LogPath $configureLogPath
        # The configurator has run; drop the marker-path env var so it cannot be
        # inherited by the later build/player child processes (only Apply reads it,
        # so this is hygiene against a future invocation accidentally writing it).
        Remove-Item -LiteralPath Env:\DXM_CONFIGURE_MARKER_PATH -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath Env:\DXM_CONFIGURED_PROFILE_PATH -ErrorAction SilentlyContinue
        $configureProblem = Test-UnityConfigureMarker -MarkerPath $configureMarkerPath -StartedUtc $configureStartedUtc
        if (-not [string]::IsNullOrWhiteSpace($configureProblem)) {
            Write-UnityRunFailureDiagnostics `
                -Project $ProjectPath `
                -LogPath $configureLogPath `
                -CscLabel "$configurationScope configure" `
                -DiagnosticsLabel "Unity $configurationScope configure"
            throw "Configure $configurationScope IL2CPP project failed ($configureProblem; Unity exit code $configureExit / $(Get-NativeExitCodeDescription -ExitCode $configureExit)). See the streamed Unity log above (also saved to $configureLogPath)."
        }
        if ($configureExit -ne 0) {
            Write-UnityBenignExitWarning -Label "Configure $configurationScope IL2CPP project" -ExitCode $configureExit -LogPath $configureLogPath
        }
        Write-AnalyzerSetupDiagnostics -Project $ProjectPath -LogPath $configureLogPath -Label "$configurationScope configure"
        if (
            -not [string]::IsNullOrWhiteSpace($canonicalProfileId) -and
            -not $isShippingFidelity
        ) {
            & $profileValidatorPath `
                -ProfilePath $resolvedCanonicalProfilePath `
                -EvidencePath $configuredProfileEvidencePath `
                -EvidenceKind configuration `
                -ExpectedSha256 $canonicalProfileSha256
        } elseif (
            $isShippingFidelity -and
            (Test-Path -LiteralPath $configuredProfileEvidencePath -PathType Leaf)
        ) {
            throw 'Shipping configuration must defer reviewed engine-stripping evidence to the direct player build.'
        }
    }

    if ($isShippingFidelity) {
        Write-ShippingPackageResolutionEvidence `
            -ProjectPath $ProjectPath `
            -ArtifactsPath $ArtifactsPath `
            -ExpectedRepoRoot $RepoRoot `
            -ExpectedManifestSha256 $shippingPreResolutionManifestSha256
        # Build a stripped consumer through BuildPipeline directly. This path does
        # not invoke Unity Test Framework, add test assemblies, or establish a
        # PlayerConnection. The same immutable binary runs both the positive AOT
        # root proof and the expected-failure missing-root control.
        $shippingPositiveLogPath = Join-Path $ArtifactsPath 'shipping-positive-player.log'
        $shippingMutantLogPath = Join-Path $ArtifactsPath 'shipping-missing-root-mutant-player.log'
        $shippingManifestPath = Join-Path $ArtifactsPath 'shipping-player-manifest.json'
        $shippingBuildReportPath = Join-Path $ArtifactsPath 'shipping-build-report.json'
        $shippingCellEvidencePath = Join-Path $ArtifactsPath 'shipping-cell-evidence.json'
        $shippingBuildStartedUtc = [DateTime]::UtcNow
        foreach ($staleShippingPath in @(
            $shippingBuildMarkerPath,
            $shippingAssemblyEvidencePath,
            $shippingBuildReportPath,
            $shippingCellEvidencePath,
            $prebuildProfileEvidencePath,
            $postbuildProfileEvidencePath,
            $buildOptionsProfileEvidencePath,
            $shippingPositiveResultPath,
            $shippingPositiveRuntimeProfilePath,
            $shippingMutantResultPath,
            $shippingMutantRuntimeProfilePath,
            $shippingPositiveLogPath,
            $shippingMutantLogPath,
            $shippingManifestPath
        )) {
            if (Test-Path -LiteralPath $staleShippingPath -PathType Leaf) {
                Remove-Item -LiteralPath $staleShippingPath -Force
            }
        }
        $standaloneExeDir = Split-Path -Parent $standaloneExe
        if ($standaloneExeDir -and (Test-Path -LiteralPath $standaloneExeDir -PathType Container)) {
            Remove-Item -LiteralPath $standaloneExeDir -Recurse -Force
        }
        if ($standaloneExeDir) {
            New-Item -ItemType Directory -Force -Path $standaloneExeDir | Out-Null
        }

        $env:DXM_PLAYER_BUILD_PATH = $standaloneExe
        $env:DXM_SHIPPING_BUILD_MARKER_PATH = $shippingBuildMarkerPath
        $env:DXM_SHIPPING_ASSEMBLY_EVIDENCE_PATH = $shippingAssemblyEvidencePath
        $env:DXM_SHIPPING_BUILD_REPORT_PATH = $shippingBuildReportPath
        $env:DXM_PREBUILD_CONFIG_PROFILE_PATH = $prebuildProfileEvidencePath
        $env:DXM_POSTBUILD_CONFIG_PROFILE_PATH = $postbuildProfileEvidencePath
        $env:DXM_BUILD_OPTIONS_PROFILE_PATH = $buildOptionsProfileEvidencePath
        $shippingBuildArgs = @(
            '-quit',
            '-batchmode',
            '-nographics',
            '-projectPath', $ProjectPath,
            '-buildTarget', 'StandaloneWindows64',
            '-executeMethod', 'DxmShippingFidelityBuilder.Build',
            '-releaseCodeOptimization',
            '-logFile', '-'
        ) + $acceleratorArgs
        # The editor wall clock covers editor startup, package resolution, script
        # compilation, and the IL2CPP build. The narrower BuildPipeline duration
        # comes from the generated builder's build report.
        $shippingBuildStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $shippingBuildResult = Invoke-ProcessWithTreeKillTimeout `
            -FilePath $UnityEditorPath `
            -Arguments $shippingBuildArgs `
            -TimeoutSeconds (Get-StandaloneBuildTimeoutSeconds) `
            -LogPath $logPath `
            -Label "Build shipping-fidelity IL2CPP player (Unity $UnityVersion)"
        $shippingBuildStopwatch.Stop()
        foreach ($buildEnvironmentVariable in @(
            'DXM_SHIPPING_BUILD_MARKER_PATH',
            'DXM_SHIPPING_ASSEMBLY_EVIDENCE_PATH',
            'DXM_SHIPPING_BUILD_REPORT_PATH',
            'DXM_BUILD_OPTIONS_PROFILE_PATH',
            'DXM_PREBUILD_CONFIG_PROFILE_PATH',
            'DXM_POSTBUILD_CONFIG_PROFILE_PATH'
        )) {
            Remove-Item -LiteralPath "Env:\$buildEnvironmentVariable" -ErrorAction SilentlyContinue
        }

        $shippingMarkerProblem = Test-UnityConfigureMarker `
            -MarkerPath $shippingBuildMarkerPath `
            -StartedUtc $shippingBuildStartedUtc
        $shippingBuildProblem = Test-StandalonePlayerBuildOutput `
            -ExpectedExe $standaloneExe `
            -BuildStartedUtc $shippingBuildStartedUtc
        if (
            -not [string]::IsNullOrWhiteSpace($shippingMarkerProblem) -or
            -not [string]::IsNullOrWhiteSpace($shippingBuildProblem)
        ) {
            Write-UnityRunFailureDiagnostics `
                -Project $ProjectPath `
                -LogPath $logPath `
                -CscLabel "$UnityVersion shipping-fidelity build" `
                -DiagnosticsLabel "Unity $UnityVersion shipping-fidelity build"
            Write-StandaloneBuildOutputDiagnostics `
                -Project $ProjectPath `
                -ExpectedExe $standaloneExe `
                -LogPath $logPath `
                -BuildStartedUtc $shippingBuildStartedUtc
            throw "Shipping-fidelity build did not produce fresh complete evidence (marker: $shippingMarkerProblem; player: $shippingBuildProblem; exit code $($shippingBuildResult.ExitCode))."
        }
        if ($shippingBuildResult.TimedOut -or $shippingBuildResult.ExitCode -ne 0) {
            Write-UnityBenignExitWarning `
                -Label "Build shipping-fidelity IL2CPP player (Unity $UnityVersion)" `
                -ExitCode $shippingBuildResult.ExitCode `
                -TimedOut:$shippingBuildResult.TimedOut `
                -LogPath $logPath
        }
        foreach ($shippingBuildConfigurationPath in @(
            $prebuildProfileEvidencePath,
            $postbuildProfileEvidencePath
        )) {
            & $profileValidatorPath `
                -ProfilePath $resolvedCanonicalProfilePath `
                -EvidencePath $shippingBuildConfigurationPath `
                -EvidenceKind configuration `
                -ExpectedSha256 $canonicalProfileSha256
        }
        & $profileValidatorPath `
            -ProfilePath $resolvedCanonicalProfilePath `
            -EvidencePath $buildOptionsProfileEvidencePath `
            -EvidenceKind buildOptions `
            -ExpectedSha256 $canonicalProfileSha256
        Test-ShippingAssemblyEvidence `
            -Path $shippingAssemblyEvidencePath `
            -ExpectedProfileId $canonicalProfileId `
            -ExpectedProfileSha256 $canonicalProfileSha256 `
            -ExpectedUnityVersion $UnityVersion
        Test-ShippingBuildReport `
            -Path $shippingBuildReportPath `
            -ExpectedProfileId $canonicalProfileId `
            -ExpectedProfileSha256 $canonicalProfileSha256 `
            -ExpectedUnityVersion $UnityVersion `
            -ExpectedTopology $ShippingTopology `
            -ExpectedMessageTypeCount $ShippingMessageTypeCount `
            -BuildStartedUtc $shippingBuildStartedUtc

        $shippingManifestBefore = Get-StandalonePlayerManifest -ExecutablePath $standaloneExe
        $shippingRuns = @(
            [ordered]@{
                Mode = 'positive'
                ResultPath = $shippingPositiveResultPath
                RuntimePath = $shippingPositiveRuntimeProfilePath
                LogPath = $shippingPositiveLogPath
            },
            [ordered]@{
                Mode = 'missing-root-mutant'
                ResultPath = $shippingMutantResultPath
                RuntimePath = $shippingMutantRuntimeProfilePath
                LogPath = $shippingMutantLogPath
            }
        )
        $shippingPlayerTimeoutSeconds = Get-StandaloneTestPlayerTimeoutSeconds
        $shippingPlayerWallClockMs = @{}
        foreach ($shippingRun in $shippingRuns) {
            $shippingPlayerStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $shippingPlayerResult = Invoke-ShippingFidelityPlayer `
                -ExecutablePath $standaloneExe `
                -Mode $shippingRun.Mode `
                -ResultPath $shippingRun.ResultPath `
                -RuntimeProfilePath $shippingRun.RuntimePath `
                -LogPath $shippingRun.LogPath `
                -TimeoutSeconds $shippingPlayerTimeoutSeconds
            $shippingPlayerStopwatch.Stop()
            $shippingPlayerWallClockMs[$shippingRun.Mode] = [double]$shippingPlayerStopwatch.Elapsed.TotalMilliseconds
            Test-ShippingFidelityResult `
                -Path $shippingRun.ResultPath `
                -ExpectedMode $shippingRun.Mode `
                -ExpectedProfileId $canonicalProfileId `
                -ExpectedProfileSha256 $canonicalProfileSha256 `
                -ExpectedUnityVersion $UnityVersion `
                -ExpectedTopology $ShippingTopology `
                -ExpectedMessageTypeCount $ShippingMessageTypeCount `
                -ExpectedDispatchLoopCount $ShippingDispatchLoopIterations
            & $profileValidatorPath `
                -ProfilePath $resolvedCanonicalProfilePath `
                -EvidencePath $shippingRun.RuntimePath `
                -EvidenceKind runtime `
                -ExpectedSha256 $canonicalProfileSha256
            if ($shippingPlayerResult.TimedOut -or $shippingPlayerResult.ExitCode -ne 0) {
                Write-UnityBenignExitWarning `
                    -Label "Run shipping-fidelity player ($($shippingRun.Mode))" `
                    -ExitCode $shippingPlayerResult.ExitCode `
                    -TimedOut:$shippingPlayerResult.TimedOut `
                    -LogPath $shippingRun.LogPath
            }
        }

        $shippingManifestAfter = Get-StandalonePlayerManifest -ExecutablePath $standaloneExe
        $shippingManifestMatches = (
            ($shippingManifestBefore | ConvertTo-Json -Depth 10 -Compress) -ceq
            ($shippingManifestAfter | ConvertTo-Json -Depth 10 -Compress)
        )
        Write-JsonArtifact -Path $shippingManifestPath -Value ([ordered]@{
                schemaVersion = 2
                topologyId = "$ShippingTopology-$ShippingMessageTypeCount-v1"
                messageTypeCount = $ShippingMessageTypeCount
                playerDirectoryManifestMatches = $shippingManifestMatches
                playerDirectoryManifestBefore = $shippingManifestBefore
                playerDirectoryManifestAfter = $shippingManifestAfter
                runs = @('positive', 'missing-root-mutant')
            })
        Test-ShippingPlayerManifestEvidence `
            -Path $shippingManifestPath `
            -ExpectedTopology $ShippingTopology `
            -ExpectedMessageTypeCount $ShippingMessageTypeCount
        Write-ShippingCellEvidence `
            -Path $shippingCellEvidencePath `
            -BuildReportPath $shippingBuildReportPath `
            -PositiveResultPath $shippingPositiveResultPath `
            -PlayerDirectoryManifest $shippingManifestBefore `
            -PlayerExecutableName ([System.IO.Path]::GetFileName($standaloneExe)) `
            -ProfileId $canonicalProfileId `
            -ProfileSha256 $canonicalProfileSha256 `
            -ManagedStrippingLevel $managedStrippingLevel `
            -Topology $ShippingTopology `
            -MessageTypeCount $ShippingMessageTypeCount `
            -UnityVersion $UnityVersion `
            -LibraryState $LibraryState `
            -EditorBuildWallClockMs ([double]$shippingBuildStopwatch.Elapsed.TotalMilliseconds) `
            -PositivePlayerWallClockMs $shippingPlayerWallClockMs['positive'] `
            -MutantPlayerWallClockMs $shippingPlayerWallClockMs['missing-root-mutant']
        Write-CiNotice 'Shipping-fidelity player passed positive AOT dispatch and the missing-root mutant with an unchanged stripped binary.'
    } elseif ($TestMode -eq 'standalone') {
        # STANDALONE SPLIT BUILD + FILE-BASED RESULTS (zero PlayerConnection
        # dependency). The legacy `-runTests -testPlatform StandaloneWindows64` flow
        # had the built player stream NUnit results back to the editor over
        # PlayerConnection/TCP; on the self-hosted runners' multi-NIC networks the
        # player cannot reach the editor's listener (TcpProtobufClient errorcode
        # 10060) and the editor's run never completes, hanging the 120-minute step.
        # Instead we (2a) BUILD the player via the editor -- the generated
        # DxmCiStandaloneBuildModifier clears AutoRunPlayer|ConnectToHost|
        # ConnectWithProfiler and IPostBuildCleanup exits the editor after the build
        # -- then (2b) RUN the built exe directly, where the generated
        # DxmCiStandaloneTestCallback writes NUnit XML to -dxmTestResults and quits,
        # then (2c) validate the FILE (the source of truth). Both 2a and 2b run under
        # the hard tree-kill watchdog so neither can hang to the step timeout.

        # (2a) BUILD. Set DXM_PLAYER_BUILD_PATH so the modifier redirects the player
        # output to a known path under the project's Build dir, then build with
        # -runTests (so PlayerLauncher's ModifyBuildOptions reflection path fires) but
        # NO -quit (the editor must reach PostBuildCleanup, which arms the exit).
        $env:DXM_PLAYER_BUILD_PATH = $standaloneExe
        if (-not [string]::IsNullOrWhiteSpace($canonicalProfileId)) {
            foreach ($staleBuildProfilePath in @(
                $prebuildProfileEvidencePath,
                $postbuildProfileEvidencePath,
                $buildOptionsProfileEvidencePath
            )) {
                if (Test-Path -LiteralPath $staleBuildProfilePath -PathType Leaf) {
                    Remove-Item -LiteralPath $staleBuildProfilePath -Force
                }
            }
            $env:DXM_PREBUILD_CONFIG_PROFILE_PATH = $prebuildProfileEvidencePath
            $env:DXM_POSTBUILD_CONFIG_PROFILE_PATH = $postbuildProfileEvidencePath
            $env:DXM_BUILD_OPTIONS_PROFILE_PATH = $buildOptionsProfileEvidencePath
        }
        $standaloneBuildStartedUtc = [DateTime]::UtcNow
        $standaloneExeDir = Split-Path -Parent $standaloneExe
        if ($standaloneExeDir -and (Test-Path -LiteralPath $standaloneExeDir -PathType Container)) {
            Remove-Item -LiteralPath $standaloneExeDir -Recurse -Force
        }
        if ($standaloneExeDir) {
            New-Item -ItemType Directory -Force -Path $standaloneExeDir | Out-Null
        }
        if (Test-Path -LiteralPath $playerLogPath -PathType Leaf) {
            Remove-Item -LiteralPath $playerLogPath -Force
        }
        $buildArgs = @(
            '-batchmode',
            '-nographics',
            '-projectPath', $ProjectPath,
            '-runTests',
            '-testPlatform', 'StandaloneWindows64',
            '-testResults', $resultsPath,
            '-assemblyNames', $AssemblyNames,
            '-releaseCodeOptimization',
            '-buildTarget', 'StandaloneWindows64',
            '-logFile', '-'
        ) + $categoryArgs + $acceleratorArgs

        $buildResult = Invoke-ProcessWithTreeKillTimeout `
            -FilePath $UnityEditorPath `
            -Arguments $buildArgs `
            -TimeoutSeconds (Get-StandaloneBuildTimeoutSeconds) `
            -LogPath $logPath `
            -Label "Build standalone IL2CPP test player (Unity $UnityVersion)"
        Remove-Item -LiteralPath Env:\DXM_BUILD_OPTIONS_PROFILE_PATH -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath Env:\DXM_PREBUILD_CONFIG_PROFILE_PATH -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath Env:\DXM_POSTBUILD_CONFIG_PROFILE_PATH -ErrorAction SilentlyContinue

        # POST-BUILD ASSERT (the BUILT PLAYER EXE is the source of truth): the exe
        # MUST exist at DXM_PLAYER_BUILD_PATH, be fresh for this build, and include
        # its companion _Data directory. A non-zero build exit code OR a watchdog
        # tree-kill is fatal ONLY when the exe is missing/stale/incomplete: Unity can
        # crash in a background thread during shutdown AFTER the player is fully
        # built, or defer Application.Quit in -batchmode IL2CPP (the watchdog then
        # tree-kills an already-finished build). Validating the exe FIRST -- before
        # consulting the exit code -- keeps those benign post-build crashes from
        # turning a good build red, while a genuinely failed build (which leaves no
        # fresh, complete exe) still fails loudly with full diagnostics.
        $standaloneBuildProblem = Test-StandalonePlayerBuildOutput `
            -ExpectedExe $standaloneExe `
            -BuildStartedUtc $standaloneBuildStartedUtc
        if (-not [string]::IsNullOrWhiteSpace($standaloneBuildProblem)) {
            Write-UnityRunFailureDiagnostics `
                -Project $ProjectPath `
                -LogPath $logPath `
                -CscLabel "$UnityVersion standalone build" `
                -DiagnosticsLabel "Unity $UnityVersion standalone build"
            Write-StandaloneBuildOutputDiagnostics `
                -Project $ProjectPath `
                -ExpectedExe $standaloneExe `
                -LogPath $logPath `
                -BuildStartedUtc $standaloneBuildStartedUtc
            if ($buildResult.TimedOut) {
                throw "Standalone test-player build timed out and the process tree was killed before producing a valid player at $standaloneExe ($standaloneBuildProblem). Raise the limit via DXM_STANDALONE_BUILD_TIMEOUT_SECONDS (0 disables the timeout). See the build log at $logPath."
            }
            throw "Editor build produced invalid DxMessaging test player output at $standaloneExe ($standaloneBuildProblem; build exit code $($buildResult.ExitCode) / $(Get-NativeExitCodeDescription -ExitCode $buildResult.ExitCode)). The build modifier may not have run, Unity may have cleaned a Temp output, or a stale player was detected. See the build log at $logPath."
        }
        # The exe is valid. If the build process nonetheless exited non-zero or was
        # tree-killed, narrate the benign post-build shutdown crash and keep going.
        if ($buildResult.TimedOut -or $buildResult.ExitCode -ne 0) {
            Write-UnityBenignExitWarning -Label "Build standalone IL2CPP test player (Unity $UnityVersion)" -ExitCode $buildResult.ExitCode -TimedOut:$buildResult.TimedOut -LogPath $logPath
        }
        if (-not [string]::IsNullOrWhiteSpace($canonicalProfileId)) {
            foreach ($buildConfigurationEvidencePath in @(
                $prebuildProfileEvidencePath,
                $postbuildProfileEvidencePath
            )) {
                & $profileValidatorPath `
                    -ProfilePath $resolvedCanonicalProfilePath `
                    -EvidencePath $buildConfigurationEvidencePath `
                    -EvidenceKind configuration `
                    -ExpectedSha256 $canonicalProfileSha256
            }
            & $profileValidatorPath `
                -ProfilePath $resolvedCanonicalProfilePath `
                -EvidencePath $buildOptionsProfileEvidencePath `
                -EvidenceKind buildOptions `
                -ExpectedSha256 $canonicalProfileSha256
        }

        # MISSED-CASE GUARD: even when the exe exists, scan the build log for the
        # signatures of a NON-redirected AutoRun build (PlayerWithTests /
        # AutoRunPlayer = True). If present, the modifier did not fully take and a
        # live run may still attempt the 10060 dial-out -- surface a ::warning::.
        if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            $buildLogText = Get-Content -LiteralPath $logPath -Raw
            if ($buildLogText -match 'PlayerWithTests' -or $buildLogText -match 'options\.AutoRunPlayer = True') {
                Write-Host "::warning::Standalone build log mentions PlayerWithTests / AutoRunPlayer = True; the DxmCiStandaloneBuildModifier may not have fully suppressed the player auto-run. If the player run hangs on a TcpProtobufClient 10060, verify the modifier compiled."
            }
        }

        # (2b) RUN the built exe directly (no PlayerConnection), under the watchdog.
        # Run 1 keeps the canonical filenames consumed by publication. Optional
        # same-player repeats use deliberately noncanonical names under a diagnostic
        # subdirectory, so recursive results.xml/player.log discovery cannot mix
        # those observations into the published first run.
        $playerTimeoutSeconds = Get-StandaloneTestPlayerTimeoutSeconds
        $captureSamePlayerEvidence = $StandalonePlayerRunCount -gt 1
        $samePlayerEvidenceRoot = Join-Path $ArtifactsPath 'same-player-repeats'
        $playerManifestBefore = $null
        $playerRunRecords = New-Object System.Collections.Generic.List[object]
        if ($captureSamePlayerEvidence) {
            if (Test-Path -LiteralPath $samePlayerEvidenceRoot -PathType Container) {
                Remove-Item -LiteralPath $samePlayerEvidenceRoot -Recurse -Force
            }
            New-Item -ItemType Directory -Force -Path $samePlayerEvidenceRoot | Out-Null
            $playerManifestBefore = Get-StandalonePlayerManifest -ExecutablePath $standaloneExe
        }

        for ($playerRunIndex = 1; $playerRunIndex -le $StandalonePlayerRunCount; $playerRunIndex++) {
            $runNumber = '{0:D2}' -f $playerRunIndex
            if ($playerRunIndex -eq 1) {
                $currentResultsPath = $resultsPath
                $currentPlayerLogPath = $playerLogPath
            } else {
                $currentRunDirectory = Join-Path $samePlayerEvidenceRoot "run-$runNumber"
                New-Item -ItemType Directory -Force -Path $currentRunDirectory | Out-Null
                $currentResultsPath = Join-Path $currentRunDirectory "repeat-$runNumber-results.xml"
                $currentPlayerLogPath = Join-Path $currentRunDirectory "repeat-$runNumber-player.log"
            }
            $hostConditionEvidencePath = if ($captureSamePlayerEvidence) {
                Join-Path $samePlayerEvidenceRoot "run-$runNumber-host-conditions.json"
            } else {
                ''
            }
            $processEvidencePath = if ($playerRunIndex -eq 1) {
                Join-Path $ArtifactsPath 'standalone-process.json'
            } else {
                Join-Path $currentRunDirectory "repeat-$runNumber-process.json"
            }
            $currentRuntimeProfilePath = if ([string]::IsNullOrWhiteSpace($canonicalProfileId)) {
                ''
            } elseif ($playerRunIndex -eq 1) {
                $runtimeProfileEvidencePath
            } else {
                Join-Path $currentRunDirectory "repeat-$runNumber-runtime-profile.json"
            }

            # Delete STALE per-run outputs first. A timeout can then honor only the
            # file written by THIS launch, never a prior local run's leftover.
            foreach ($staleOutputPath in @(
                $currentResultsPath,
                $currentPlayerLogPath,
                $hostConditionEvidencePath,
                $processEvidencePath,
                $currentRuntimeProfilePath
            )) {
                if (
                    -not [string]::IsNullOrWhiteSpace($staleOutputPath) -and
                    (Test-Path -LiteralPath $staleOutputPath -PathType Leaf)
                ) {
                    Remove-Item -LiteralPath $staleOutputPath -Force
                }
            }

            $playerResult = Invoke-StandaloneTestPlayer `
                -EditorBuiltExePath $standaloneExe `
                -ResultsPath $currentResultsPath `
                -LogPath $currentPlayerLogPath `
                -RuntimeProfilePath $currentRuntimeProfilePath `
                -TimeoutSeconds $playerTimeoutSeconds `
                -HostConditionEvidencePath $hostConditionEvidencePath `
                -ProcessEvidencePath $processEvidencePath `
                -ProcessorAffinityMask $StandalonePlayerProcessorAffinityMask `
                -PriorityClass $StandalonePlayerPriorityClass `
                -RunIndex $playerRunIndex `
                -RunCount $StandalonePlayerRunCount

            # A watchdog timeout is fatal ONLY when the player wrote no results. If
            # results exist, validate them and treat deferred Application.Quit as a
            # post-work shutdown condition rather than a benchmark failure.
            $playerExitForValidation = $playerResult.ExitCode
            if ($playerResult.TimedOut) {
                if (Test-Path -LiteralPath $currentResultsPath -PathType Leaf) {
                    Write-Host "::warning::Standalone test player run $playerRunIndex/$StandalonePlayerRunCount exceeded the ${playerTimeoutSeconds}s watchdog and was tree-killed, but it had already written $currentResultsPath; honoring that results file as the source of truth (Application.Quit was likely deferred in -batchmode IL2CPP). Raise DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS if this recurs."
                    # Pass exit 0 so the validator does not mislabel watchdog sentinel
                    # 124 as a native shutdown crash.
                    $playerExitForValidation = 0
                } else {
                    throw "Standalone test player run $playerRunIndex/$StandalonePlayerRunCount timed out after $playerTimeoutSeconds second(s) and was tree-killed before writing results to $currentResultsPath. Raise the limit via DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS (0 disables the timeout). See the player log at $currentPlayerLogPath."
                }
            }

            # (2c) VALIDATE every repeat independently. A later failed repeat cannot
            # hide behind the canonical first run's passing results.xml.
            Test-NUnitResults `
                -Path $currentResultsPath `
                -Label "Unity $UnityVersion standalone run $playerRunIndex/$StandalonePlayerRunCount" `
                -LogPath $currentPlayerLogPath `
                -Project $ProjectPath `
                -UnityExitCode $playerExitForValidation

            if (-not [string]::IsNullOrWhiteSpace($canonicalProfileId)) {
                & $profileValidatorPath `
                    -ProfilePath $resolvedCanonicalProfilePath `
                    -EvidencePath $currentRuntimeProfilePath `
                    -EvidenceKind runtime `
                    -ExpectedSha256 $canonicalProfileSha256
            }

            if ($captureSamePlayerEvidence) {
                $relativeResultsPath = $currentResultsPath.Substring($ArtifactsPath.Length).TrimStart(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar
                ).Replace('\', '/')
                $relativePlayerLogPath = $currentPlayerLogPath.Substring($ArtifactsPath.Length).TrimStart(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar
                ).Replace('\', '/')
                $playerRunRecords.Add([ordered]@{
                        runIndex = $playerRunIndex
                        resultsPath = $relativeResultsPath
                        playerLogPath = $relativePlayerLogPath
                        hostConditionsFile = [System.IO.Path]::GetFileName($hostConditionEvidencePath)
                        processId = $playerResult.ProcessId
                        processorAffinityMask = $playerResult.ProcessorAffinityMask
                        timedOut = $playerResult.TimedOut
                    })
            }
        }

        if ($captureSamePlayerEvidence) {
            $playerManifestAfter = Get-StandalonePlayerManifest -ExecutablePath $standaloneExe
            $manifestBeforeJson = $playerManifestBefore | ConvertTo-Json -Depth 10 -Compress
            $manifestAfterJson = $playerManifestAfter | ConvertTo-Json -Depth 10 -Compress
            $playerDirectoryManifestMatches = $manifestBeforeJson -ceq $manifestAfterJson
            $samePlayerEvidence = [ordered]@{
                schemaVersion = 1
                runCount = $StandalonePlayerRunCount
                canonicalPublishedRunIndex = 1
                playerDirectoryManifestMatches = $playerDirectoryManifestMatches
                playerDirectoryManifestBefore = $playerManifestBefore
                playerDirectoryManifestAfter = $playerManifestAfter
                runs = @($playerRunRecords.ToArray())
            }
            Write-JsonArtifact `
                -Path (Join-Path $samePlayerEvidenceRoot 'same-player-evidence.json') `
                -Value $samePlayerEvidence
            if (-not $playerDirectoryManifestMatches) {
                throw 'Standalone player directory manifest changed between same-player repeats.'
            }
            Write-CiNotice "Standalone same-player evidence captured $StandalonePlayerRunCount validated launches with an unchanged player directory manifest."
        }
    } else {
        # MUST NOT include '-quit' alongside '-runTests': per the Unity Editor manual
        # (https://docs.unity3d.com/Manual/EditorCommandLineArguments.html), if the
        # Editor is running tests with -runTests, -quit causes it to QUIT IMMEDIATELY
        # before in-progress tests can complete -- the editor exits 0 having written
        # no results.xml.
        $testArgs = @(
            '-batchmode',
            '-nographics',
            '-projectPath', $ProjectPath,
            '-runTests',
            '-testPlatform', $testPlatform,
            '-testResults', $resultsPath,
            '-assemblyNames', $AssemblyNames,
            '-releaseCodeOptimization',
            '-logFile', '-'
        )
        $testArgs = $testArgs + $categoryArgs + $acceleratorArgs

        # Delete any STALE results file first so the file validation below can only
        # honor results THIS run wrote (defensive for local re-runs; CI checkout
        # already cleans the gitignored .artifacts tree per job).
        if (Test-Path -LiteralPath $resultsPath -PathType Leaf) {
            Remove-Item -LiteralPath $resultsPath -Force
        }

        # Run the editor; capture (do NOT throw on) its exit code. The NUnit
        # results.xml is the source of truth: Test-NUnitResults fails loudly on a
        # missing/invalid/failing file AND folds the exit code into its diagnostics,
        # but PASSES a valid run that exited non-zero only because Unity crashed in a
        # background thread during shutdown AFTER RunFinished wrote the file.
        $runExit = Invoke-UnityEditorTestsWithPackageManagerRetry `
            -EditorPath $UnityEditorPath `
            -Arguments $testArgs `
            -Label "Run Unity $UnityVersion $TestMode tests" `
            -LogPath $logPath `
            -ResultsPath $resultsPath `
            -Project $ProjectPath
        Write-AnalyzerSetupDiagnostics -Project $ProjectPath -LogPath $logPath -Label "$UnityVersion $TestMode test compile"
        Test-NUnitResults -Path $resultsPath -Label "Unity $UnityVersion $TestMode" -LogPath $logPath -Project $ProjectPath -UnityExitCode $runExit
    }
} finally {
    foreach ($temporaryEnvironmentVariable in @(
        'DXM_CONFIGURE_MARKER_PATH',
        'DXM_CONFIGURED_PROFILE_PATH',
        'DXM_PREBUILD_CONFIG_PROFILE_PATH',
        'DXM_POSTBUILD_CONFIG_PROFILE_PATH',
        'DXM_BUILD_OPTIONS_PROFILE_PATH',
        'DXM_PLAYER_BUILD_PATH',
        'DXM_SHIPPING_BUILD_MARKER_PATH',
        'DXM_SHIPPING_ASSEMBLY_EVIDENCE_PATH',
        'DXM_SHIPPING_BUILD_REPORT_PATH'
    )) {
        Remove-Item -LiteralPath "Env:\$temporaryEnvironmentVariable" -ErrorAction SilentlyContinue
    }
    # Standalone/local callers retain deterministic return. Organization CI passes
    # Central so the immediately following trusted return action owns the one
    # authoritative post-activation attempt and its typed evidence.
    if ($hasLicenseCreds -and $LicenseReturnOwner -eq 'Local') {
        Invoke-UnityLicenseReturn -EditorPath $UnityEditorPath -Email $env:UNITY_EMAIL -Password $env:UNITY_PASSWORD -LogPath $returnLogPath
    }
}
