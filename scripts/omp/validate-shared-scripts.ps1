#Requires -Version 5.1
<#
.SYNOPSIS
    Fails when a consumer repository's copy of a shared omp script has drifted
    from the canonical copy in OpenModulePlatform.

.DESCRIPTION
    scripts/omp/bump-version.ps1 is copied verbatim into all nine repositories.
    Keeping the copies identical has so far been a manual act - done on
    2026-08-25 and again on 2026-08-28 - and nothing held them that way. The next
    fix to the canonical file recreated the drift the day it landed, and the
    failure was SILENT: a repository running a stale copy looks green locally,
    and the difference surfaces only when someone runs a bump that behaves
    differently from the neighbouring repository. That is typically mid-incident,
    which is what happened on 2026-08-23. It happened once more on 2026-09-02,
    when a fix to the canonical file left the other eight behind until somebody
    noticed by hand.

    WHY THIS SHAPE. The alternatives considered were (a) a shared canonical
    source the repositories fetch from, (b) a hash recorded in each repository
    and checked locally, and (c) a CI guard in OpenModulePlatform that compares
    across repositories.

    (c) cannot work: the repositories are a mix of public and private, and CI
    checks out one of them - it cannot read the others' work trees.

    (b) cannot detect the failure that matters. A repository that never received
    an update carries a stale script AND a stale recorded hash, which agree with
    each other, so the check passes while the repository is behind. It would only
    catch local tampering, which is not the problem.

    (a) is what this is, in the form the repository already uses: the consumer
    compares against the canonical file in the sibling OpenModulePlatform
    checkout, exactly as validate-shared-dependencies.ps1 (Check 14) does for
    shared PROJECT trees. Same neighbour resolution, same fail-open behaviour
    when the neighbour is absent, and - importantly - the guard itself is CALLED
    from the platform repository rather than copied into the consumers. A guard
    that were copied would be subject to the very drift it exists to detect; as
    the Check 14 wiring puts it, two implementations of the same rule are how the
    original gap went silent.

    HOW THE REFERENCE IS UPDATED. There is no separate reference to maintain:
    the canonical file IS the reference. When it changes in OpenModulePlatform,
    copy it to the eight consumers in the same change. This guard is what tells
    you which repositories still need it.

.PARAMETER ConsumerRepositoryRoot
    Root of the repository being validated.

.PARAMETER PlatformRepositoryRoot
    Root of the OpenModulePlatform checkout. Defaults to $env:OpenModulePlatformRoot,
    then to a sibling directory named OpenModulePlatform - the same resolution
    order the Check 14 wiring uses.

.PARAMETER Strict
    Treat an absent platform repository as a failure instead of an unverified skip.

.EXAMPLE
    .\scripts\omp\validate-shared-scripts.ps1 -ConsumerRepositoryRoot $RepoRoot
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ConsumerRepositoryRoot,

    [Parameter(Mandatory = $false)]
    [string] $PlatformRepositoryRoot = '',

    [Parameter(Mandatory = $false)]
    [switch] $Strict
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The scripts that must be byte-identical across the fleet, relative to a
# repository root. Add to this list only for files that are genuinely shared
# verbatim; a file with legitimate per-repository differences does not belong
# here, and forcing it in would turn the guard into noise.
$sharedScripts = @('scripts/omp/bump-version.ps1')

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

$consumerRoot = [System.IO.Path]::GetFullPath($ConsumerRepositoryRoot)

$platformRoot = $PlatformRepositoryRoot
if ([string]::IsNullOrWhiteSpace($platformRoot)) {
    $platformRoot = $env:OpenModulePlatformRoot
}
if ([string]::IsNullOrWhiteSpace($platformRoot)) {
    $platformRoot = [System.IO.Path]::GetFullPath((Join-Path $consumerRoot '..\OpenModulePlatform'))
}

# The consumer IS the platform repository: nothing to compare against itself.
if ([System.IO.Path]::GetFullPath($platformRoot) -eq $consumerRoot) {
    Write-Host 'Shared scripts: this repository is the canonical source; nothing to compare.'
    exit 0
}

if (-not (Test-Path -LiteralPath $platformRoot -PathType Container)) {
    $message = "Shared scripts: NOT VERIFIED - the OpenModulePlatform checkout was not found at '$platformRoot', so the shared scripts could not be compared against their canonical copies. This is expected in CI, which checks out one repository at a time."
    if ($Strict) {
        throw $message + ' -Strict was passed, so an unverifiable check is a failure.'
    }

    # Visible, not silent: an unmeasured check must never read as a passing one.
    Write-Warning $message
    exit 0
}

$drift = @()
foreach ($relative in $sharedScripts) {
    $consumerPath = Join-Path $consumerRoot ($relative -replace '/', '\')
    $platformPath = Join-Path $platformRoot ($relative -replace '/', '\')

    if (-not (Test-Path -LiteralPath $platformPath -PathType Leaf)) {
        Write-Warning "Shared scripts: NOT VERIFIED - '$relative' does not exist in the platform repository at '$platformPath'."
        continue
    }

    if (-not (Test-Path -LiteralPath $consumerPath -PathType Leaf)) {
        $drift += "  - $relative is missing from this repository but is shipped by the platform repository."
        continue
    }

    $consumerHash = Get-FileSha256 -Path $consumerPath
    $platformHash = Get-FileSha256 -Path $platformPath

    if ($consumerHash -ne $platformHash) {
        $drift += "  - $relative differs from the canonical copy (this repository: $($consumerHash.Substring(0,16)), platform: $($platformHash.Substring(0,16)))."
    }
    else {
        Write-Host "Shared scripts: '$relative' matches the canonical copy ($($platformHash.Substring(0,16)))."
    }
}

if ($drift.Count -gt 0) {
    throw (
        "Shared script drift detected against '$platformRoot':" + [Environment]::NewLine +
        ($drift -join [Environment]::NewLine) + [Environment]::NewLine +
        'Copy the canonical file(s) from the platform repository into this one and commit them in the same change. ' +
        'A stale copy looks green locally and only surfaces when a bump behaves differently here than in a neighbouring repository - typically mid-incident.'
    )
}
