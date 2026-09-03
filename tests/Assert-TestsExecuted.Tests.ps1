#Requires -Version 5.1
# Pester 5's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
    Proves the zero-execution gate (scripts/omp/assert-tests-executed.ps1)
    fails red on every false-green shape and passes on a real run.

.DESCRIPTION
    The gate's contract is its exit code, so every test runs the script as a
    child powershell.exe process against generated TRX fixtures and asserts
    the exit code:

      missing results directory            -> 1
      directory without any .trx file      -> 1
      .trx with executed = 0               -> 1
      .trx with executed > 0               -> 0
      two .trx files, one with 0 executed  -> 0 without -RequirePerFile
                                           -> 1 with -RequirePerFile

    The two-file case is the hardening added 2026-09: the directory sum lets a
    test project whose filter matched nothing hide behind a sibling project
    that did run, so -RequirePerFile requires executed > 0 in every file.

    Pester 5 runs every container in a separate session state, so the shared
    harness (gate path + TRX fixture writer) lives in
    Assert-TestsExecuted.TestHelpers.ps1 and is dot-sourced from each Describe
    block's BeforeAll.
#>

Set-StrictMode -Version Latest

Describe 'assert-tests-executed: directory and file presence' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Assert-TestsExecuted.TestHelpers.ps1')
    }

    It 'Fails when the results directory does not exist' {
        $missing = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-gate-missing-' + [Guid]::NewGuid().ToString('N'))

        $result = Invoke-ExecutionGate -ResultsDirectory $missing

        $result.ExitCode | Should -Be 1
        ($result.Output -match 'results directory not found') | Should -Be $true
    }

    It 'Fails when the results directory contains no .trx files' {
        $dir = New-GateResultsDirectory
        try {
            $result = Invoke-ExecutionGate -ResultsDirectory $dir

            $result.ExitCode | Should -Be 1
            ($result.Output -match 'no \.trx files found') | Should -Be $true
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }
}

Describe 'assert-tests-executed: execution counters' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Assert-TestsExecuted.TestHelpers.ps1')
    }

    It 'Fails when the only .trx file shows 0 executed tests' {
        $dir = New-GateResultsDirectory
        try {
            New-TrxFile -Path (Join-Path $dir 'zero.trx') -Total 5 -Executed 0

            $result = Invoke-ExecutionGate -ResultsDirectory $dir

            $result.ExitCode | Should -Be 1
            ($result.Output -match '0 tests executed') | Should -Be $true
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }

    It 'Passes when tests were executed' {
        $dir = New-GateResultsDirectory
        try {
            New-TrxFile -Path (Join-Path $dir 'ran.trx') -Total 5 -Executed 5

            $result = Invoke-ExecutionGate -ResultsDirectory $dir

            $result.ExitCode | Should -Be 0
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }

    It 'Passes without -RequirePerFile when one of two files ran nothing (directory sum masks it)' {
        $dir = New-GateResultsDirectory
        try {
            New-TrxFile -Path (Join-Path $dir 'ran.trx') -Total 5 -Executed 5
            New-TrxFile -Path (Join-Path $dir 'zero.trx') -Total 3 -Executed 0

            $result = Invoke-ExecutionGate -ResultsDirectory $dir

            $result.ExitCode | Should -Be 0
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }

    It 'Fails with -RequirePerFile when one of two files ran nothing, and names the file' {
        $dir = New-GateResultsDirectory
        try {
            New-TrxFile -Path (Join-Path $dir 'ran.trx') -Total 5 -Executed 5
            New-TrxFile -Path (Join-Path $dir 'zero.trx') -Total 3 -Executed 0

            $result = Invoke-ExecutionGate -ResultsDirectory $dir -RequirePerFile

            $result.ExitCode | Should -Be 1
            ($result.Output -match 'zero\.trx') | Should -Be $true
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }
}

Describe 'assert-tests-executed: skip reasons stay visible' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Assert-TestsExecuted.TestHelpers.ps1')
    }

    It 'Prints each NotExecuted test and its reason with -ShowSkipReasons' {
        $dir = New-GateResultsDirectory
        try {
            New-TrxFile -Path (Join-Path $dir 'mixed.trx') -Total 2 -Executed 1 `
                -SkippedTestName 'Suite.UiTest' -SkipReason 'Skipped: no local Chromium'

            $result = Invoke-ExecutionGate -ResultsDirectory $dir -ShowSkipReasons

            $result.ExitCode | Should -Be 0
            ($result.Output -match 'Suite\.UiTest') | Should -Be $true
            ($result.Output -match 'no local Chromium') | Should -Be $true
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }
}
