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
      fewer .trx files than -MinimumTrxFiles -> 1
      .trx with executed = 0               -> 1
      .trx with executed > 0               -> 0
      two .trx files, one with 0 executed  -> 0 without -RequirePerFile
                                           -> 1 with -RequirePerFile
      malformed .trx (no Counters node,
        missing counters attributes,
        zero-byte, or broken XML)          -> 1

    The two-file case is the hardening added 2026-09: the directory sum lets a
    test project whose filter matched nothing hide behind a sibling project
    that did run, so -RequirePerFile requires executed > 0 in every file. The
    malformed-file cases fail REGARDLESS of -RequirePerFile: a truncated or
    corrupt .trx is not a legitimate zero run and must never blend into the
    directory sum.

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
        $result.Output | Should -Match 'results directory not found'
    }

    It 'Fails when the results directory contains no .trx files' {
        $dir = New-GateResultsDirectory
        try {
            $result = Invoke-ExecutionGate -ResultsDirectory $dir

            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'no \.trx files found'
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
            $result.Output | Should -Match '0 tests executed'
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
            $result.Output | Should -Match 'zero\.trx'
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
            $result.Output | Should -Match 'Suite\.UiTest'
            $result.Output | Should -Match 'no local Chromium'
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }
}

Describe 'assert-tests-executed: malformed files never blend into the sum' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Assert-TestsExecuted.TestHelpers.ps1')
    }

    It 'Fails and names the file when a .trx has no ResultSummary/Counters node' {
        $dir = New-GateResultsDirectory
        try {
            New-TrxFile -Path (Join-Path $dir 'ran.trx') -Total 5 -Executed 5
            New-RawTrxFile -Path (Join-Path $dir 'stub.trx') -Content @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results />
</TestRun>
"@

            $result = Invoke-ExecutionGate -ResultsDirectory $dir

            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'no ResultSummary/Counters node'
            $result.Output | Should -Match 'stub\.trx'
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }

    It 'Fails when the Counters node lacks the executed attribute' {
        $dir = New-GateResultsDirectory
        try {
            New-RawTrxFile -Path (Join-Path $dir 'attrless.trx') -Content @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="5" passed="5" failed="0" />
  </ResultSummary>
</TestRun>
"@

            $result = Invoke-ExecutionGate -ResultsDirectory $dir

            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'attrless\.trx'
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }

    It 'Fails on a zero-byte .trx file' {
        $dir = New-GateResultsDirectory
        try {
            New-RawTrxFile -Path (Join-Path $dir 'empty.trx') -Content ''

            $result = Invoke-ExecutionGate -ResultsDirectory $dir

            $result.ExitCode | Should -Be 1
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }

    It 'Fails on a .trx whose XML is not well-formed' {
        $dir = New-GateResultsDirectory
        try {
            New-RawTrxFile -Path (Join-Path $dir 'broken.trx') -Content '<TestRun><ResultSummary><Counters total="5" executed="5"'

            $result = Invoke-ExecutionGate -ResultsDirectory $dir

            $result.ExitCode | Should -Be 1
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }

    It 'Fails when fewer .trx files exist than -MinimumTrxFiles expects' {
        $dir = New-GateResultsDirectory
        try {
            New-TrxFile -Path (Join-Path $dir 'ran.trx') -Total 5 -Executed 5

            $result = Invoke-ExecutionGate -ResultsDirectory $dir -MinimumTrxFiles 2

            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'expected at least 2'
        }
        finally {
            Remove-GateResultsDirectory -Path $dir
        }
    }
}
