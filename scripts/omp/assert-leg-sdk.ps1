#Requires -Version 5.1
<#
.SYNOPSIS
    Second layer of the CI version-matrix gate: asserts that the SDK which will
    actually build a matrix leg is the SDK that leg claims.

.DESCRIPTION
    The first layer is get-ci-version-matrix.ps1, which refuses to derive a
    matrix whose target frameworks disagree with the pinned SDK major. That
    layer runs before any build and is covered by Get-CiVersionMatrix.Tests.ps1.

    This layer runs inside each build-test leg, after setup-dotnet, and answers
    a different question: whatever global.json and setup-dotnet resolved on this
    runner, is the SDK about to build the solution the one this leg claims?

    Two failures it catches, both measured on windows-latest 2026-08-28:

      1. Unsupported major. The hosted image carries its own SDK bands in
         C:\Program Files\dotnet, so a leg that asks setup-dotnet for an
         unsupported version can still resolve and build on the image's newer
         SDK under rollForward. Without this assertion such a leg passes and
         reports that an unsupported version works.

      2. A pin that did not hold. A leg asking for 10.0.200 resolves the
         image's 10.0.400 under rollForward latestFeature -- a "pinned" leg
         that proves nothing about the pinned SDK.

    Why this is a script and not inline workflow YAML: as inline PowerShell the
    assertion could not be tested, and it showed. The deliberate sabotage on
    2026-08-28 (commit 05f55fce) that was supposed to prove the gate red came
    back GREEN -- CI run 33182860062, with the leg named "SABOTAGE: unsupported
    .NET major, the gate must fail" reported as success -- because the fix that
    would have caught it landed afterwards (e5297bab), while the commit that
    removed the sabotage (596a2b59) already claimed the gate was "proven red".
    As a script the gate is proven red by Assert-LegSdk.Tests.ps1 on every CI
    run and every pre-push, rather than once by a sabotage nobody re-runs.

.PARAMETER ResolvedSdk
    The SDK version `dotnet --version` reported on the runner. Empty means the
    call failed; that is a failure, never a pass.

.PARAMETER ExpectedMajor
    The .NET major the repository's target frameworks claim, from the matrix.

.PARAMETER PinnedSdk
    The SDK version this leg asked setup-dotnet for (e.g. '10.0.200' or '10.0.x').

.PARAMETER PinExact
    'true' when this leg claims to build on exactly PinnedSdk.

.PARAMETER LegName
    Matrix leg name, used in failure messages so the log names the culprit.

.EXAMPLE
    .\scripts\omp\assert-leg-sdk.ps1 -ResolvedSdk (dotnet --version) `
        -ExpectedMajor '10' -PinnedSdk '10.0.200' -PinExact 'true' -LegName 'sdk-pinned'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string] $ResolvedSdk,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string] $ExpectedMajor,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string] $PinnedSdk,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string] $PinExact,

    [Parameter(Mandatory = $false)]
    [string] $LegName = 'unnamed'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolved = $ResolvedSdk.Trim()
$major = $ExpectedMajor.Trim()

# A measurement that could not be taken must never read as a measurement that
# passed. An empty version means `dotnet --version` failed, or global.json
# rejected the installed SDK set -- either way the leg cannot claim anything.
if (-not $resolved) {
    throw "Leg '$LegName': the resolved SDK could not be determined (dotnet --version returned nothing, or global.json rejected the installed SDK set). The leg cannot claim a version it never measured."
}

if (-not $major) {
    throw "Leg '$LegName': no expected .NET major was supplied, so there is nothing to check the resolved SDK ($resolved) against. Refusing to pass an unchecked leg."
}

# Compare on the major COMPONENT, not a string prefix: '1' must not satisfy an
# expectation of '10', and '10' must not satisfy '1'. Anchoring on the dot is
# what makes that comparison correct.
$resolvedMajor = $resolved.Split('.')[0]
if ($resolvedMajor -ne $major) {
    throw "Leg '$LegName': installed SDK $resolved is not on the supported .NET major $major; this leg claims a version the repository does not support."
}

if ($PinExact.Trim() -eq 'true' -and $resolved -ne $PinnedSdk.Trim()) {
    throw "Leg '$LegName': must build on exactly SDK $PinnedSdk but resolved $resolved; the pin did not hold."
}

Write-Host "Leg '$LegName': resolved SDK $resolved is on the supported major $major (pinned: $PinnedSdk, pinExact: $PinExact)."
