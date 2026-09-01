#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProfilePath,

    [string]$EvidencePath,

    [ValidateSet('configuration', 'buildOptions', 'runtime')]
    [string]$EvidenceKind,

    [string]$ExpectedSha256,

    [switch]$ProfileOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RequiredJsonObject {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label does not exist: $Path"
    }
    try {
        $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        throw "$Label is not valid JSON: $($_.Exception.Message)"
    }
    if ($null -eq $value -or $value -is [System.Array]) {
        throw "$Label must contain one JSON object."
    }
    return $value
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actual = @($Value.PSObject.Properties.Name)
    $missing = @($Expected | Where-Object { $actual -cnotcontains $_ })
    $extra = @($actual | Where-Object { $Expected -cnotcontains $_ })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        throw "$Label properties differ (missing=$($missing -join ','), extra=$($extra -join ','))."
    }
}

function Assert-EquivalentJsonValue {
    param(
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ($Expected -is [bool]) {
        if ($Actual -isnot [bool] -or $Actual -ne $Expected) {
            throw "$Path differs (expected=$Expected, actual=$Actual)."
        }
        return
    }
    if ($Expected -is [string]) {
        if ($Actual -isnot [string] -or $Actual -cne $Expected) {
            throw "$Path differs (expected='$Expected', actual='$Actual')."
        }
        return
    }
    if ($Expected -is [int] -or $Expected -is [long]) {
        if ($Actual -isnot [int] -and $Actual -isnot [long]) {
            throw "$Path has the wrong type."
        }
        if ([long]$Actual -ne [long]$Expected) {
            throw "$Path differs (expected=$Expected, actual=$Actual)."
        }
        return
    }
    throw "$Path uses an unsupported profile value type '$($Expected.GetType().FullName)'."
}

$profile = Get-RequiredJsonObject -Path $ProfilePath -Label 'IL2CPP profile'

Assert-ExactProperties `
    -Value $profile `
    -Expected @('schemaVersion', 'profileId', 'configuration', 'buildOptions', 'runtime') `
    -Label 'IL2CPP profile'

if (
    ($profile.schemaVersion -isnot [int] -and $profile.schemaVersion -isnot [long]) -or
    [long]$profile.schemaVersion -ne 1
) {
    throw "Unsupported IL2CPP profile schemaVersion '$($profile.schemaVersion)'."
}
if (
    $profile.profileId -isnot [string] -or
    $profile.profileId -cnotmatch '^[a-z0-9][a-z0-9-]*-v1$'
) {
    throw "Invalid IL2CPP profileId '$($profile.profileId)'."
}

$profileSchemas = [ordered]@{
    configuration = [ordered]@{
        buildTarget = 'string'
        scriptingBackend = 'string'
        apiCompatibilityLevel = 'string'
        codeOptimization = 'string'
        il2cppCompilerConfiguration = 'string'
        il2cppCodeGeneration = 'string'
        managedStrippingLevel = 'string'
        incrementalGc = 'bool'
        stripEngineCode = 'bool'
    }
    buildOptions = [ordered]@{
        developmentBuild = 'bool'
        allowDebugging = 'bool'
        deepProfiling = 'bool'
        enableAssertions = 'bool'
        includeTestAssemblies = 'bool'
        autoRunPlayer = 'bool'
        connectToHost = 'bool'
        connectWithProfiler = 'bool'
        cleanBuildCache = 'bool'
        detailedBuildReport = 'bool'
    }
    runtime = [ordered]@{
        debugBuild = 'bool'
    }
}
foreach ($group in $profileSchemas.Keys) {
    Assert-ExactProperties `
        -Value $profile.$group `
        -Expected @($profileSchemas[$group].Keys) `
        -Label "IL2CPP profile $group"
    foreach ($property in $profile.$group.PSObject.Properties) {
        $expectedType = $profileSchemas[$group][$property.Name]
        if ($expectedType -eq 'bool' -and $property.Value -isnot [bool]) {
            throw "IL2CPP profile $group.$($property.Name) must be a Boolean."
        }
        if ($expectedType -eq 'string' -and $property.Value -isnot [string]) {
            throw "IL2CPP profile $group.$($property.Name) must be a string."
        }
    }
}

$fixedProfileValues = [ordered]@{
    configuration = [ordered]@{
        buildTarget = 'StandaloneWindows64'
        scriptingBackend = 'IL2CPP'
        apiCompatibilityLevel = 'NET_Standard_2_0'
        codeOptimization = 'Release'
        il2cppCompilerConfiguration = 'Release'
        il2cppCodeGeneration = 'OptimizeSpeed'
        incrementalGc = $true
        stripEngineCode = $true
    }
    buildOptions = [ordered]@{
        developmentBuild = $false
        allowDebugging = $false
        deepProfiling = $false
        enableAssertions = $false
        autoRunPlayer = $false
        connectToHost = $false
        connectWithProfiler = $false
        cleanBuildCache = $true
        detailedBuildReport = $true
    }
    runtime = [ordered]@{
        debugBuild = $false
    }
}
foreach ($group in $fixedProfileValues.Keys) {
    foreach ($property in $fixedProfileValues[$group].Keys) {
        Assert-EquivalentJsonValue `
            -Expected $fixedProfileValues[$group][$property] `
            -Actual $profile.$group.$property `
            -Path "profile.$group.$property"
    }
}

$profileVariants = [ordered]@{
    'canonical-il2cpp-verdict-player-v1' = [ordered]@{
        managedStrippingLevel = 'Disabled'
        includeTestAssemblies = $true
    }
    'shipping-fidelity-il2cpp-player-v1' = [ordered]@{
        managedStrippingLevel = 'High'
        includeTestAssemblies = $false
    }
}
if (-not $profileVariants.Contains($profile.profileId)) {
    $supportedProfileIds = @($profileVariants.Keys) -join "', '"
    throw "Unsupported IL2CPP profileId '$($profile.profileId)'. Supported profileIds: '$supportedProfileIds'."
}
$selectedVariant = $profileVariants[$profile.profileId]
foreach ($variantProperty in $selectedVariant.Keys) {
    $variantGroup = if ($variantProperty -ceq 'managedStrippingLevel') {
        'configuration'
    } else {
        'buildOptions'
    }
    Assert-EquivalentJsonValue `
        -Expected $selectedVariant[$variantProperty] `
        -Actual $profile.$variantGroup.$variantProperty `
        -Path "profile.$variantGroup.$variantProperty"
}

$profileSha256 = (Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash.ToLowerInvariant()
if (
    -not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
    $profileSha256 -cne $ExpectedSha256.ToLowerInvariant()
) {
    throw "IL2CPP profile SHA-256 differs (expected=$($ExpectedSha256.ToLowerInvariant()), actual=$profileSha256)."
}

if ($ProfileOnly) {
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath) -or -not [string]::IsNullOrWhiteSpace($EvidenceKind)) {
        throw 'Do not pass EvidencePath or EvidenceKind with ProfileOnly.'
    }
    Write-Host "Validated IL2CPP profile $($profile.profileId) ($profileSha256)."
    return
}
if ([string]::IsNullOrWhiteSpace($EvidencePath) -or [string]::IsNullOrWhiteSpace($EvidenceKind)) {
    throw 'EvidencePath and EvidenceKind are required unless ProfileOnly is set.'
}

$evidence = Get-RequiredJsonObject -Path $EvidencePath -Label "$EvidenceKind profile evidence"

Assert-ExactProperties `
    -Value $evidence `
    -Expected @('schemaVersion', 'profileId', 'profileSha256', 'evidenceKind', 'unityVersion', 'values') `
    -Label "$EvidenceKind profile evidence"

Assert-EquivalentJsonValue `
    -Expected $profile.schemaVersion `
    -Actual $evidence.schemaVersion `
    -Path "$EvidenceKind.schemaVersion"
Assert-EquivalentJsonValue `
    -Expected $profile.profileId `
    -Actual $evidence.profileId `
    -Path "$EvidenceKind.profileId"
if ($evidence.profileSha256 -isnot [string] -or $evidence.profileSha256 -cne $profileSha256) {
    throw "$EvidenceKind profile evidence profileSha256 differs (expected=$profileSha256, actual=$($evidence.profileSha256))."
}
if ($evidence.evidenceKind -isnot [string] -or $evidence.evidenceKind -cne $EvidenceKind) {
    throw "$EvidenceKind profile evidence kind differs (actual=$($evidence.evidenceKind))."
}
if ($evidence.unityVersion -isnot [string] -or [string]::IsNullOrWhiteSpace($evidence.unityVersion)) {
    throw "$EvidenceKind profile evidence unityVersion must be a non-empty string."
}

$expectedValues = $profile.$EvidenceKind
Assert-ExactProperties `
    -Value $evidence.values `
    -Expected @($expectedValues.PSObject.Properties.Name) `
    -Label "$EvidenceKind profile evidence values"

foreach ($property in $expectedValues.PSObject.Properties) {
    Assert-EquivalentJsonValue `
        -Expected $property.Value `
        -Actual $evidence.values.($property.Name) `
        -Path "$EvidenceKind.$($property.Name)"
}

Write-Host "Validated $EvidenceKind profile evidence for $($profile.profileId) ($profileSha256)."
