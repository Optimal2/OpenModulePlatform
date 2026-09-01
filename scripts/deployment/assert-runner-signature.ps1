#Requires -Version 5.1
<#
.SYNOPSIS
    Refuses to replace a signed installer runner with an unsigned or invalidly
    signed one.

.DESCRIPTION
    update-installer-runner-only.ps1 rebuilds OpenModulePlatform.Bootstrapper.exe
    and copies it into three places in an installed package: the package root,
    the tools copy, and the tools zip. Until 2026-09-01 it did all three with
    Copy-Item -Force and never invoked the signing path, so a developer rebuild
    silently replaced a signed production runner with an unsigned binary. Nothing
    in the flow told the operator that had happened.

    Signing itself stays optional: sign-artifacts.ps1 is a no-op unless signing
    is configured, so unsigned developer packaging keeps working. What this gate
    forbids is a DOWNGRADE - taking a package whose runner is signed and putting
    an unsigned or broken-signature binary in its place.

    The decision is a parameterised script rather than inline code in the refresh
    script because inline script code cannot be tested, and an untested gate is a
    gate nobody has seen fail. Proven red by tests/Assert-RunnerSignature.Tests.ps1.

.PARAMETER TargetSignatureStatus
    Authenticode status of the runner currently in the package: 'Valid',
    'NotSigned', 'HashMismatch', 'UnknownError', or 'NoTarget' when the package
    has no runner yet. Empty means it could not be determined, which is a
    failure - an unreadable target might well be signed.

.PARAMETER NewSignatureStatus
    Authenticode status of the freshly built runner, same value set.

.PARAMETER TargetPath
    Path of the runner being replaced, used in the failure message.

.PARAMETER NewPath
    Path of the replacement, used in the failure message.

.EXAMPLE
    $target = (Get-AuthenticodeSignature -LiteralPath $targetExe).Status
    $new    = (Get-AuthenticodeSignature -LiteralPath $sourceExe).Status
    .\scripts\deployment\assert-runner-signature.ps1 `
        -TargetSignatureStatus $target -NewSignatureStatus $new `
        -TargetPath $targetExe -NewPath $sourceExe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string] $TargetSignatureStatus,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string] $NewSignatureStatus,

    [Parameter(Mandatory = $false)]
    [string] $TargetPath = '(unknown target)',

    [Parameter(Mandatory = $false)]
    [string] $NewPath = '(unknown replacement)'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The values Get-AuthenticodeSignature can report, plus the synthetic 'NoTarget'
# for a package that has no runner yet. Anything outside this set is a value the
# gate does not understand, and guessing at it is how a downgrade slips through.
$known = @('Valid', 'NotSigned', 'HashMismatch', 'NotTrusted', 'UnknownError', 'Incompatible', 'NoTarget')

$target = $TargetSignatureStatus.Trim()
$new = $NewSignatureStatus.Trim()

if (-not $target) {
    throw "Runner replacement refused: the signature status of '$TargetPath' could not be determined. An unreadable target may well be signed, and a measurement that could not be taken must never read as a measurement that passed."
}

if ($known -notcontains $target) {
    throw "Runner replacement refused: unknown signature status '$target' for '$TargetPath'. The gate does not guess."
}

# An unsigned target is not protected: developer packaging is allowed to stay
# unsigned, and signing an unsigned package is an improvement, not a downgrade.
if ($target -ne 'Valid') {
    Write-Host "Runner replacement allowed: the target '$TargetPath' is not signed (status: $target)."
    exit 0
}

if (-not $new) {
    throw "Runner replacement refused: '$TargetPath' is signed, but the signature status of the replacement '$NewPath' could not be determined. Refusing to overwrite a signed runner with an unverified binary."
}

if ($known -notcontains $new) {
    throw "Runner replacement refused: unknown signature status '$new' for the replacement '$NewPath'. The gate does not guess."
}

if ($new -ne 'Valid') {
    throw "Runner replacement refused: '$TargetPath' carries a valid signature, but the replacement '$NewPath' has signature status '$new'. Replacing a signed runner with an unsigned or invalidly signed binary would silently downgrade the installed package. Run the signing step (scripts/deployment/sign-artifacts.ps1) before refreshing, or refresh a package whose runner is not signed."
}

Write-Host "Runner replacement allowed: both '$TargetPath' and '$NewPath' carry valid signatures."
