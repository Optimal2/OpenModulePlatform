#Requires -Version 5.1
<#
.SYNOPSIS
    Proves that an unsigned or invalidly signed runner cannot replace a signed one.

.DESCRIPTION
    update-installer-runner-only.ps1 rebuilds OpenModulePlatform.Bootstrapper.exe
    and copies it over three places in an installed package: the package root,
    the tools copy, and the tools zip. Until 2026-09-01 it did all three with
    Copy-Item -Force and never called the signing path at all -- so a developer
    rebuild silently replaced a signed production runner with an unsigned binary,
    and the operator had no way to notice.

    The decision lives in scripts/deployment/assert-runner-signature.ps1 as a
    parameterised gate rather than inline in the refresh script, for the same
    reason the CI matrix gate was extracted: inline script code cannot be tested,
    and an untested gate is a gate nobody has seen fail.

    Signature *status* is passed in rather than read from disk here, so the
    decision can be proven without shipping signed and unsigned test binaries.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:GateScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/deployment/assert-runner-signature.ps1'

function Invoke-Gate {
    param(
        [string] $TargetStatus,
        [string] $NewStatus,
        [string] $TargetPath = 'C:\pkg\OpenModulePlatform.Bootstrapper.exe',
        [string] $NewPath = 'C:\temp\OpenModulePlatform.Bootstrapper.exe'
    )

    try {
        & $script:GateScript `
            -TargetSignatureStatus $TargetStatus `
            -NewSignatureStatus $NewStatus `
            -TargetPath $TargetPath `
            -NewPath $NewPath | Out-Null
        return @{ Threw = $false; ErrorMessage = '' }
    }
    catch {
        return @{ Threw = $true; ErrorMessage = $_.Exception.Message }
    }
}

Describe 'assert-runner-signature: a signed target is protected' {
    It 'Refuses an UNSIGNED replacement for a signed runner' {
        $result = Invoke-Gate -TargetStatus 'NotSigned' -NewStatus 'NotSigned' -TargetPath 'C:\pkg\r.exe'
        # A signed target with an unsigned replacement is the actual regression.
        $result = Invoke-Gate -TargetStatus 'Valid' -NewStatus 'NotSigned'

        $result.Threw | Should Be $true
        ($result.ErrorMessage -match 'signed') | Should Be $true
    }

    It 'Refuses an INVALIDLY signed replacement for a signed runner' {
        # A tampered or expired signature is not better than no signature.
        $result = Invoke-Gate -TargetStatus 'Valid' -NewStatus 'HashMismatch'

        $result.Threw | Should Be $true
    }

    It 'Names both paths in the failure so the operator can see what was refused' {
        $result = Invoke-Gate -TargetStatus 'Valid' -NewStatus 'NotSigned' `
            -TargetPath 'C:\pkg\target.exe' -NewPath 'C:\build\new.exe'

        ($result.ErrorMessage -match 'target\.exe') | Should Be $true
        ($result.ErrorMessage -match 'new\.exe') | Should Be $true
    }

    It 'Allows a validly signed replacement for a signed runner' {
        $result = Invoke-Gate -TargetStatus 'Valid' -NewStatus 'Valid'

        $result.Threw | Should Be $false
    }
}

Describe 'assert-runner-signature: unsigned developer packaging keeps working' {
    It 'Allows an unsigned replacement when the target was never signed' {
        # Signing is optional for developer packaging (sign-artifacts.ps1 is a
        # no-op unless configured). The gate must not break that flow.
        $result = Invoke-Gate -TargetStatus 'NotSigned' -NewStatus 'NotSigned'

        $result.Threw | Should Be $false
    }

    It 'Allows a signed replacement for an unsigned target' {
        $result = Invoke-Gate -TargetStatus 'NotSigned' -NewStatus 'Valid'

        $result.Threw | Should Be $false
    }

    It 'Allows the replacement when there is no target at all' {
        $result = Invoke-Gate -TargetStatus 'NoTarget' -NewStatus 'NotSigned'

        $result.Threw | Should Be $false
    }
}

Describe 'assert-runner-signature: refuses to pass on an unreadable status' {
    It 'Fails when the target status could not be determined' {
        # Absence of a measurement must never read as a passing measurement:
        # an unreadable target might well be signed.
        $result = Invoke-Gate -TargetStatus '' -NewStatus 'Valid'

        $result.Threw | Should Be $true
        ($result.ErrorMessage -match 'could not be determined') | Should Be $true
    }

    It 'Fails when the replacement status could not be determined for a signed target' {
        $result = Invoke-Gate -TargetStatus 'Valid' -NewStatus ''

        $result.Threw | Should Be $true
    }

    It 'Fails on an unknown status value rather than guessing' {
        $result = Invoke-Gate -TargetStatus 'Valid' -NewStatus 'ProbablyFine'

        $result.Threw | Should Be $true
    }
}
