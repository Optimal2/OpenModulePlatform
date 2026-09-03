# Shared setup for Assert-LegSdk.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 5 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

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
