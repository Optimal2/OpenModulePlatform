#Requires -Version 5.1
<#
.SYNOPSIS
    Proves the CI matrix leg's runtime SDK gate actually fails an unsupported
    or unpinned SDK.

.DESCRIPTION
    Two layers guard the version matrix. The first is the derivation script
    (get-ci-version-matrix.ps1), which is covered by Get-CiVersionMatrix.Tests.ps1.
    The second is this gate: whatever global.json and setup-dotnet resolved on
    the runner, the SDK that will actually build must be on the major the
    target frameworks claim, and an exact-pin leg must build on exactly the SDK
    it asked for.

    That second layer had never been seen red. On 2026-08-28 a deliberate
    sabotage commit (05f55fce) pointed the matrix at unsupported .NET 9 to
    prove the gate; CI run 33182860062 came back GREEN, with the matrix leg
    literally named "SABOTAGE: unsupported .NET major, the gate must fail"
    reported as success. The reason is the one documented in ci.yml: the hosted
    windows-latest image carries newer SDK bands, so a leg that asks
    setup-dotnet for an old version still resolves and builds on the image's
    SDK under rollForward. The follow-up commit e5297bab added the pinExact
    narrowing and this assertion to close exactly that hole -- but it landed
    AFTER the sabotage was removed (596a2b59, whose message claims "gate proven
    red"). The claim was not backed by a red run.

    These tests close that gap durably: the gate logic lives in
    scripts/omp/assert-leg-sdk.ps1 and is proven to throw here, on every CI run
    and every pre-push, instead of once by a sabotage commit that has to be
    remembered, repeated and trusted.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:AssertScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/omp/assert-leg-sdk.ps1'

function Invoke-Gate {
    <#
        Invokes the gate in-process so a thrown failure is catchable, and
        reports both whether it threw and what it said. A gate that throws the
        wrong message is not a working gate.
    #>
    param(
        [string] $ResolvedSdk,
        [string] $ExpectedMajor,
        [string] $PinnedSdk,
        [string] $PinExact,
        [string] $LegName = 'test-leg'
    )

    try {
        & $script:AssertScript `
            -ResolvedSdk $ResolvedSdk `
            -ExpectedMajor $ExpectedMajor `
            -PinnedSdk $PinnedSdk `
            -PinExact $PinExact `
            -LegName $LegName | Out-Null
        return @{ Threw = $false; ErrorMessage = '' }
    }
    catch {
        return @{ Threw = $true; ErrorMessage = $_.Exception.Message }
    }
}

Describe 'assert-leg-sdk: unsupported major gate' {
    It 'Fails when the resolved SDK is on a major the repository does not support' {
        # This is the exact case the 2026-08-28 sabotage was meant to prove and
        # did not: the leg asked for .NET 9 but the image built on .NET 10.
        $result = Invoke-Gate -ResolvedSdk '9.0.100' -ExpectedMajor '10' -PinnedSdk '9.0.100' -PinExact 'false'

        $result.Threw | Should Be $true
        ($result.ErrorMessage -match 'not on the supported .NET major') | Should Be $true
    }

    It 'Names the offending versions in the failure so the log is readable' {
        $result = Invoke-Gate -ResolvedSdk '9.0.100' -ExpectedMajor '10' -PinnedSdk '9.0.100' -PinExact 'false'

        ($result.ErrorMessage -match '9\.0\.100') | Should Be $true
        ($result.ErrorMessage -match '10') | Should Be $true
    }

    It 'Passes when the resolved SDK is on the supported major' {
        $result = Invoke-Gate -ResolvedSdk '10.0.400' -ExpectedMajor '10' -PinnedSdk '10.0.x' -PinExact 'false'

        $result.Threw | Should Be $false
    }

    It 'Does not accept a major that merely starts with the same digits' {
        # '1' must not satisfy an expectation of '10', nor '10' an expectation
        # of '1'. A prefix comparison without the dot would let both through.
        $result = Invoke-Gate -ResolvedSdk '1.0.100' -ExpectedMajor '10' -PinnedSdk '1.0.100' -PinExact 'false'

        $result.Threw | Should Be $true
    }
}

Describe 'assert-leg-sdk: exact-pin gate' {
    It 'Fails when an exact-pin leg silently rolled forward to another SDK' {
        # The documented failure mode: the leg asked for 10.0.200 but the image
        # resolved 10.0.400 under rollForward, so a "pinned" leg proved nothing.
        $result = Invoke-Gate -ResolvedSdk '10.0.400' -ExpectedMajor '10' -PinnedSdk '10.0.200' -PinExact 'true'

        $result.Threw | Should Be $true
        ($result.ErrorMessage -match 'the pin did not hold') | Should Be $true
    }

    It 'Passes when an exact-pin leg resolved exactly its pinned SDK' {
        $result = Invoke-Gate -ResolvedSdk '10.0.200' -ExpectedMajor '10' -PinnedSdk '10.0.200' -PinExact 'true'

        $result.Threw | Should Be $false
    }

    It 'Allows band drift on a leg that does not claim an exact pin' {
        $result = Invoke-Gate -ResolvedSdk '10.0.400' -ExpectedMajor '10' -PinnedSdk '10.0.x' -PinExact 'false'

        $result.Threw | Should Be $false
    }
}

Describe 'assert-leg-sdk: refuses to pass on missing input' {
    It 'Fails when the resolved SDK could not be read instead of passing silently' {
        # An empty version means `dotnet --version` failed. Absence of a
        # measurement must never read as a passing measurement.
        $result = Invoke-Gate -ResolvedSdk '' -ExpectedMajor '10' -PinnedSdk '10.0.200' -PinExact 'true'

        $result.Threw | Should Be $true
        ($result.ErrorMessage -match 'could not be determined') | Should Be $true
    }

    It 'Fails when the expected major is missing rather than accepting anything' {
        $result = Invoke-Gate -ResolvedSdk '10.0.400' -ExpectedMajor '' -PinnedSdk '10.0.x' -PinExact 'false'

        $result.Threw | Should Be $true
    }
}
