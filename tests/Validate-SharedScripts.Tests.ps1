#Requires -Version 5.1
# Pester 5's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
    Proves the shared-script drift guard fails loudly on divergence and stays
    quiet-but-visible when it cannot measure.

.DESCRIPTION
    scripts/omp/bump-version.ps1 is copied verbatim into all nine repositories.
    Keeping the copies identical was a manual act twice (2026-08-25 and
    2026-08-28), and nothing held them that way: the next fix to the canonical
    file recreated the drift the day it landed, silently. A repository running a
    stale copy looks green locally; the difference surfaces only when someone
    runs a bump that behaves differently from the neighbouring repository -
    typically mid-incident, which is exactly what happened on 2026-08-23. It
    happened again on 2026-09-02, when this very campaign's fix to the canonical
    file left the other eight behind for as long as it took to notice.

    The guard follows the pattern validate-shared-dependencies.ps1 already
    established: one canonical implementation in OpenModulePlatform, CALLED by
    the consumers rather than copied into them. A guard that were itself copied
    would be subject to the drift it exists to detect - as the Check 14 code puts
    it, two implementations of the same rule are how the original gap went silent.

    Pester 5 runs every container in a separate session state, so the shared
    harness (guard path + pair helpers) lives in
    Validate-SharedScripts.TestHelpers.ps1 and is dot-sourced from each
    Describe block's BeforeAll.
#>

Set-StrictMode -Version Latest

Describe 'validate-shared-scripts: drift is loud' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-SharedScripts.TestHelpers.ps1')
    }

    It 'Fails when the consumer copy differs from the canonical one' {
        $pair = New-Pair -ConsumerBody 'stale content' -PlatformBody 'canonical content'
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should -Be $true
            ($result.Output -match 'bump-version\.ps1') | Should -Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Names both hashes so the divergence can be identified' {
        $pair = New-Pair -ConsumerBody 'stale content' -PlatformBody 'canonical content'
        try {
            $result = Invoke-Guard -Pair $pair
            # Two different SHA-256 prefixes must appear in the message.
            ([regex]::Matches($result.Output, '[0-9a-f]{16}')).Count -ge 2 | Should -Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Says how to resolve the drift rather than only reporting it' {
        $pair = New-Pair -ConsumerBody 'stale content' -PlatformBody 'canonical content'
        try {
            $result = Invoke-Guard -Pair $pair
            ($result.Output -match 'Copy') | Should -Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Passes when the copies are byte-identical' {
        $pair = New-Pair -ConsumerBody 'same content' -PlatformBody 'same content'
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should -Be $false
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Treats a trailing-newline difference as drift, not as equal' {
        # Byte-identical means byte-identical: a copy that differs only in its
        # final newline is still a different file, and pretending otherwise is
        # how a guard starts having opinions.
        $pair = New-Pair -ConsumerBody "same content`n" -PlatformBody 'same content'
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should -Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }
}

Describe 'validate-shared-scripts: cannot-measure is visible, never silently green' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-SharedScripts.TestHelpers.ps1')
    }

    It 'Skips with a visible note when the platform repository is not beside the consumer' {
        # CI checks out one repository, so the neighbour is usually absent. That
        # must not fail the build - but it must not read as a passing check either.
        $pair = New-Pair -ConsumerBody 'anything' -PlatformBody '' -OmitPlatformRoot
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should -Be $false
            ($result.Output -match 'not verified|skipp') | Should -Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Fails on a missing neighbour when -Strict is passed' {
        # The opt-in form for environments that DO have the neighbour and want
        # its absence treated as a problem.
        $pair = New-Pair -ConsumerBody 'anything' -PlatformBody '' -OmitPlatformRoot
        try {
            $result = Invoke-Guard -Pair $pair -Strict
            $result.Threw | Should -Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Fails when the consumer has no copy of a script the platform ships' {
        # A missing shared script is drift of the most complete kind.
        $pair = New-Pair -ConsumerBody '' -PlatformBody 'canonical content' -OmitConsumerScript
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Threw | Should -Be $true
            ($result.Output -match 'missing|saknas') | Should -Be $true
        }
        finally { Remove-Pair -Pair $pair }
    }
}

Describe 'validate-shared-scripts: exitkoden ar kontraktet' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-SharedScripts.TestHelpers.ps1')
    }

    It 'Avslutar med 0 i synk, sa anroparen inte laser ett stale LASTEXITCODE' {
        # Utan ett uttryckligt exit 0 lamnade vakten $LASTEXITCODE fran senaste
        # NATIVE-kommando, och anroparna laser exakt den variabeln. I sex repon
        # maskerades det av att Check 14 kor precis fore och nollstaller; i de tva
        # utan Check 14 rapporterades ett SYNKAT repo som rott.
        $pair = New-Pair -ConsumerBody 'same content' -PlatformBody 'same content'
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Kod | Should -Be 0
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Avslutar med 1 vid drift, sa den kopplade felgrenen faktiskt nas' {
        $pair = New-Pair -ConsumerBody 'stale content' -PlatformBody 'canonical content'
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Kod | Should -Be 1
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Avslutar med 0 nar grannen saknas utan -Strict' {
        $pair = New-Pair -ConsumerBody 'anything' -PlatformBody '' -OmitPlatformRoot
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Kod | Should -Be 0
        }
        finally { Remove-Pair -Pair $pair }
    }
}

Describe 'validate-shared-scripts: radslutsstil ar inte drift' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-SharedScripts.TestHelpers.ps1')
    }

    It 'Behandlar CRLF och LF som samma innehall' {
        # Sex konsumenter har lokal core.autocrlf=true medan plattformens egna
        # utcheckningar kor utan, sa samma git-innehall hamnar med olika radslut
        # pa disk. En ra byte-hash gav da ett PERMANENT falskt driftstopp med
        # instruktionen "kopiera den kanoniska filen" - fast den redan var
        # identisk. Uppmatt i granskning 2026-09-02.
        $pair = New-Pair -ConsumerBody "rad ett`r`nrad tva`r`n" -PlatformBody "rad ett`nrad tva`n"
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Kod | Should -Be 0
        }
        finally { Remove-Pair -Pair $pair }
    }

    It 'Behandlar en SAKNAD avslutande radbrytning som skillnad, inte som stil' {
        $pair = New-Pair -ConsumerBody "rad ett`n" -PlatformBody 'rad ett'
        try {
            $result = Invoke-Guard -Pair $pair
            $result.Kod | Should -Be 1
        }
        finally { Remove-Pair -Pair $pair }
    }
}
