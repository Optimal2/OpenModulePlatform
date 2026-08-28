#Requires -Version 5.1
<#
.SYNOPSIS
    Derives the CI .NET version matrix from the repository's own version
    files instead of a hand-written list in the workflow.

.DESCRIPTION
    The platform's version claims live in the repository: global.json pins the
    SDK line and roll-forward policy, and every .csproj declares the target
    framework the code is built for. A workflow matrix written by hand next to
    those files drifts away from them and then proves the wrong thing, so this
    script reads those files and emits the matrix GitHub Actions consumes.

    Sources read:
      - global.json            -> sdk.version, sdk.rollForward
      - all committed *.csproj -> TargetFramework / TargetFrameworks

    The script is also the unsupported-version gate: any target framework
    whose major version does not match the pinned SDK major fails the
    derivation (exit via thrown error), so a matrix pointed at an unsupported
    .NET version fails before any build starts. netstandard* target frameworks
    are exempt: Roslyn analyzer assemblies (OpenModulePlatform.Web.Shared
    .Analyzers) are loaded by the compiler of the referencing build, not by a
    runtime the platform ships or supports.

    Legs emitted:
      - sdk-pinned       (cadence: push)      exact SDK from global.json
      - sdk-latest-band  (cadence: push)      newest SDK the rollForward
                                              policy allows ('<major>.0.x')
      - runtime-floor    (cadence: scheduled) pinned SDK, tests forced onto the
                                              oldest runtime patch of the
                                              supported major via
                                              DOTNET_ROLL_FORWARD=Disable

    Scheduled legs are only emitted with -IncludeScheduled; when omitted they
    are listed as explicitly excluded in the log, so a reduced run never reads
    as full coverage.

    The matrix JSON is appended to GITHUB_OUTPUT as 'legs' when that
    environment variable is set (GitHub Actions), and is always printed as the
    last stdout line for local inspection and tests.

.PARAMETER IncludeScheduled
    Include cadence=scheduled legs (runtime floor) in the emitted matrix.
    CI passes this for schedule and workflow_dispatch events only.

.EXAMPLE
    pwsh -File scripts/omp/get-ci-version-matrix.ps1 -IncludeScheduled
#>
[CmdletBinding()]
param(
    [switch]$IncludeScheduled
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# --- Read the SDK pin from global.json --------------------------------------
$globalJsonPath = Join-Path $repoRoot 'global.json'
if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
    throw "global.json not found at $globalJsonPath - the matrix cannot be derived without the repository SDK pin."
}

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$sdkVersion = [string]$globalJson.sdk.version
$rollForward = [string]$globalJson.sdk.rollForward

if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw 'global.json does not pin sdk.version - there is no repository SDK truth to derive the matrix from.'
}
if ([string]::IsNullOrWhiteSpace($rollForward)) {
    # The .NET default when rollForward is absent is 'patch'.
    $rollForward = 'patch'
}

$sdkVersionParts = $sdkVersion.Split('.')
if ($sdkVersionParts.Count -ne 3 -or -not ($sdkVersion -match '^\d+\.\d+\.\d+$')) {
    throw "global.json sdk.version '$sdkVersion' is not a three-part version; the matrix derivation expects e.g. '10.0.200'."
}
$sdkMajor = $sdkVersionParts[0]

# --- Map the roll-forward policy to the legs it allows ----------------------
# The matrix never crosses the pinned major: the repository's target
# frameworks are pinned to one major, so a policy that lets the SDK drift to
# another major is a contradiction the gate below would catch anyway.
$allowLatestBand = $false
switch ($rollForward) {
    'latestFeature' { $allowLatestBand = $true }
    'latestPatch'   { $allowLatestBand = $false }
    'patch'         { $allowLatestBand = $false }
    'disable'       { $allowLatestBand = $false }
    default {
        throw "global.json rollForward '$rollForward' is not mapped by this script. 'latestMinor'/'latestMajor' let the SDK drift across version lines the target frameworks do not claim; extend this script deliberately if that ever becomes supported."
    }
}

# --- Read every committed target framework ----------------------------------
$csprojFiles = git -C $repoRoot ls-files '*.csproj'
if ($LASTEXITCODE -ne 0) {
    throw "'git ls-files *.csproj' exited with $LASTEXITCODE; the target-framework scan could not run."
}
if (-not $csprojFiles) {
    throw 'git ls-files returned no .csproj files; refusing to derive a matrix from an empty target-framework set.'
}

$unsupportedFrameworks = New-Object System.Collections.Generic.List[string]
$exemptFrameworks = New-Object System.Collections.Generic.List[string]
$frameworkMajors = New-Object System.Collections.Generic.List[string]

foreach ($relativePath in $csprojFiles) {
    $fullPath = Join-Path $repoRoot $relativePath
    $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
    $tfmMatches = [regex]::Matches($content, '<TargetFrameworks?>([^<]+)</TargetFrameworks?>')
    foreach ($tfmMatch in $tfmMatches) {
        foreach ($tfm in ($tfmMatch.Groups[1].Value -split ';')) {
            $tfm = $tfm.Trim()
            if ([string]::IsNullOrWhiteSpace($tfm)) { continue }
            if ($tfm -match '^net(\d+)\.\d+(-\w+)?$') {
                $tfmMajor = $Matches[1]
                if ($tfmMajor -ne $sdkMajor) {
                    $unsupportedFrameworks.Add("$relativePath targets '$tfm' but global.json pins SDK major $sdkMajor")
                } else {
                    if (-not $frameworkMajors.Contains($tfmMajor)) { $frameworkMajors.Add($tfmMajor) }
                }
            } elseif ($tfm -match '^netstandard') {
                $exemptFrameworks.Add("$relativePath targets '$tfm'")
            } else {
                $unsupportedFrameworks.Add("$relativePath targets '$tfm', which is neither net<major>.x nor an exempt netstandard analyzer target")
            }
        }
    }
}

Write-Host "Repository version truth:"
Write-Host "  global.json pins SDK $sdkVersion (rollForward: $rollForward)"
Write-Host "  target frameworks claim .NET major(s): $($frameworkMajors -join ', ')"
foreach ($exempt in $exemptFrameworks) {
    Write-Host "  exempt (analyzer, compiler-loaded): $exempt"
}

if ($unsupportedFrameworks.Count -gt 0) {
    Write-Host 'Unsupported target frameworks found:'
    $unsupportedFrameworks | ForEach-Object { Write-Host "  - $_" }
    throw "The repository targets frameworks the pinned SDK major ($sdkMajor) does not support. Either the code moved to a new .NET major and global.json must follow, or the stray target framework is wrong. The CI matrix refuses to claim support it cannot prove."
}

if (-not $frameworkMajors.Contains($sdkMajor)) {
    throw "No committed project targets the pinned SDK major $sdkMajor; global.json and the codebase disagree about the supported .NET line."
}

# --- Build the matrix legs ---------------------------------------------------
$runtimeFloor = "$sdkMajor.0.0"

$legs = New-Object System.Collections.Generic.List[hashtable]
$legs.Add(@{
    name          = 'sdk-pinned'
    sdk           = $sdkVersion
    runtimeFloor  = ''
    expectedMajor = $sdkMajor
    pinExact      = 'true'
    cadence       = 'push'
    purpose       = "exact SDK pin from global.json ($sdkVersion); proves the repository still builds on the oldest SDK band the rollForward policy accepts. CI narrows the workspace global.json to rollForward=disable for this leg, because hosted images carry newer SDK bands that latestFeature would otherwise silently roll forward to."
})

if ($allowLatestBand) {
    $legs.Add(@{
        name          = 'sdk-latest-band'
        sdk           = "$sdkMajor.0.x"
        runtimeFloor  = ''
        expectedMajor = $sdkMajor
        pinExact      = 'false'
        cadence       = 'push'
        purpose       = "newest SDK band the rollForward policy ($rollForward) accepts; this is what hosted images and dev machines drift to"
    })
}

$scheduledLegs = New-Object System.Collections.Generic.List[hashtable]
$scheduledLegs.Add(@{
    name          = 'runtime-floor'
    sdk           = $sdkVersion
    runtimeFloor  = $runtimeFloor
    expectedMajor = $sdkMajor
    pinExact      = 'true'
    cadence       = 'scheduled'
    purpose       = "pinned SDK build, but tests run with DOTNET_ROLL_FORWARD=Disable so the test hosts load exactly runtime $runtimeFloor - the oldest runtime patch the net$sdkMajor.0 target claims to run on"
})

# --- Log what runs and, just as loudly, what does not ------------------------
Write-Host ''
Write-Host 'Matrix legs in this run:'
foreach ($leg in $legs) {
    Write-Host "  $($leg.name): sdk=$($leg.sdk) cadence=$($leg.cadence) - $($leg.purpose)"
}
if ($IncludeScheduled) {
    foreach ($leg in $scheduledLegs) { $legs.Add($leg) }
    foreach ($leg in $scheduledLegs) {
        Write-Host "  $($leg.name): sdk=$($leg.sdk) runtimeFloor=$($leg.runtimeFloor) cadence=$($leg.cadence) - $($leg.purpose)"
    }
}

Write-Host ''
Write-Host 'Explicitly NOT covered (so a reduced run never reads as full coverage):'
if (-not $IncludeScheduled) {
    foreach ($leg in $scheduledLegs) {
        Write-Host "  OMITTED on this event: $($leg.name) ($($leg.purpose)) - runs on the weekly schedule and workflow_dispatch only."
    }
}
if (-not $allowLatestBand) {
    Write-Host "  OMITTED: sdk-latest-band - rollForward '$rollForward' does not allow drifting to newer feature bands, so CI must not either."
}
Write-Host "  OMITTED: SDK feature bands below the $sdkVersion pin - rollForward '$rollForward' rejects them by design; building on them would test a configuration global.json forbids."
Write-Host "  OMITTED: .NET majors other than $sdkMajor - unsupported; any target framework declaring one fails this script before the matrix is emitted."

$matrix = @{ include = @($legs.ToArray()) }
$matrixJson = ConvertTo-Json -InputObject $matrix -Compress -Depth 4

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "legs=$matrixJson" -Encoding UTF8
}

# Last stdout line is the machine-readable matrix for local runs and tests.
Write-Output $matrixJson
