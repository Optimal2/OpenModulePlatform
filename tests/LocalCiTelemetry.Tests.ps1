# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions are provided by the pinned module.')]
param()

Describe 'Local CI telemetry writer' {
    BeforeAll {
        . (Join-Path $PSScriptRoot '..\scripts\local-ci-telemetry.ps1')
        $repositoryRoot = Split-Path -Parent $PSScriptRoot
    }
    BeforeEach {
        $originalAppData = $env:APPDATA
        $env:APPDATA = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $arguments = @{ Repo = 'test-repo'; RepositoryRoot = $repositoryRoot; Status = 'pass'; DurationMs = 1 }
        $target = Join-Path $env:APPDATA '@private\ai-orchestrator\local-ci-telemetry\test-repo.jsonl'
    }
    AfterEach { $env:APPDATA = $originalAppData }

    It 'rejects path separators, whitespace and trailing newlines before creating a directory' {
        foreach ($name in @('../escape', 'a/b', 'a\b', 'a b', "repo`n")) {
            $arguments.Repo = $name
            { Write-LocalCiTelemetry @arguments } | Should -Throw '*Repo must contain only*'
        }
        Test-Path -LiteralPath $env:APPDATA | Should -BeFalse
    }

    It 'appends complete UTF-8 records without a BOM when the directory already exists' {
        Write-LocalCiTelemetry @arguments
        Write-LocalCiTelemetry @arguments
        $bytes = [System.IO.File]::ReadAllBytes($target)
        $bytes[0] | Should -Be 123
        $lines = [System.IO.File]::ReadAllLines($target)
        $lines.Count | Should -Be 2
        foreach ($line in $lines) { ($line | ConvertFrom-Json).repo | Should -Be 'test-repo' }
    }

    It 'fails within a bounded retry when locked and leaves existing records intact' {
        Write-LocalCiTelemetry @arguments
        $stream = [System.IO.File]::Open($target, 'Open', 'ReadWrite', 'None')
        try { { Write-LocalCiTelemetry @arguments } | Should -Throw }
        finally { $stream.Dispose() }
        [System.IO.File]::ReadAllLines($target).Count | Should -Be 1
        Write-LocalCiTelemetry @arguments
        [System.IO.File]::ReadAllLines($target).Count | Should -Be 2
    }

    It 'counts malformed XML and emits a verbose diagnostic' {
        $null = New-Item -ItemType Directory -Path $env:APPDATA -Force
        [System.IO.File]::WriteAllText((Join-Path $env:APPDATA 'broken.trx'), '<broken')
        $messages = @(Get-LocalCiTrxCounters -ResultsDirectory $env:APPDATA -SuiteName probe -Verbose 4>&1)
        @($messages | Where-Object { $_ -is [System.Management.Automation.VerboseRecord] }).Count | Should -Be 1
        ($messages | Where-Object { $_ -isnot [System.Management.Automation.VerboseRecord] }).malformed_files | Should -Be 1
    }

    It 'returns null when the TRX results directory does not exist' {
        Get-LocalCiTrxCounters -ResultsDirectory (Join-Path $env:APPDATA 'no-such-dir') -SuiteName probe | Should -Be $null
    }

    It 'returns null when the TRX directory contains no .trx files' {
        $null = New-Item -ItemType Directory -Path $env:APPDATA -Force
        Get-LocalCiTrxCounters -ResultsDirectory $env:APPDATA -SuiteName probe | Should -Be $null
    }

    It 'counts a TRX file whose Counters node is missing as malformed' {
        $null = New-Item -ItemType Directory -Path $env:APPDATA -Force
        [System.IO.File]::WriteAllText((Join-Path $env:APPDATA 'no-counters.trx'), '<TestRun><ResultSummary /></TestRun>')
        $suite = Get-LocalCiTrxCounters -ResultsDirectory $env:APPDATA -SuiteName probe
        $suite.malformed_files | Should -Be 1
        $suite.total | Should -Be 0
    }

    It 'counts a TRX file with a non-integer counter as malformed' {
        $null = New-Item -ItemType Directory -Path $env:APPDATA -Force
        [System.IO.File]::WriteAllText((Join-Path $env:APPDATA 'bad-counter.trx'), '<TestRun><ResultSummary><Counters total="not-a-number" executed="1" /></ResultSummary></TestRun>')
        $suite = Get-LocalCiTrxCounters -ResultsDirectory $env:APPDATA -SuiteName probe
        $suite.malformed_files | Should -Be 1
        $suite.total | Should -Be 0
    }

    It 'discards every counter from a file with a later invalid or missing counter' {
        $null = New-Item -ItemType Directory -Path $env:APPDATA -Force
        [System.IO.File]::WriteAllText((Join-Path $env:APPDATA 'valid.trx'), '<TestRun><ResultSummary><Counters total="7" executed="6" passed="4" failed="2" notExecuted="1" /></ResultSummary></TestRun>')
        [System.IO.File]::WriteAllText((Join-Path $env:APPDATA 'invalid.trx'), '<TestRun><ResultSummary><Counters total="100" executed="99" passed="98" failed="not-a-number" notExecuted="1" /></ResultSummary></TestRun>')
        [System.IO.File]::WriteAllText((Join-Path $env:APPDATA 'missing.trx'), '<TestRun><ResultSummary><Counters total="200" passed="198" /></ResultSummary></TestRun>')

        $suite = Get-LocalCiTrxCounters -ResultsDirectory $env:APPDATA -SuiteName probe
        $suite.trx_files | Should -Be 3
        $suite.malformed_files | Should -Be 2
        $suite.total | Should -Be 7
        $suite.executed | Should -Be 6
        $suite.passed | Should -Be 4
        $suite.failed | Should -Be 2
        $suite.notExecuted | Should -Be 1
    }

    It 'enforces UTF-8 bytes rather than character count when dropping suite detail' {
        $bigSuite = [pscustomobject]@{ name = ([string][char]0x20ac * 1500) }
        Write-LocalCiTelemetry @arguments -Suites @($bigSuite)
        $line = [System.IO.File]::ReadAllLines($target)[0]
        [System.Text.Encoding]::UTF8.GetByteCount($line) | Should -BeLessOrEqual 4096
        @(($line | ConvertFrom-Json).suites).Count | Should -Be 0
    }

    It 'rejects an oversized UTF-8 record without appending a partial line' {
        Write-LocalCiTelemetry @arguments
        $original = [System.IO.File]::ReadAllText($target)
        { Write-LocalCiTelemetry @arguments -TestSkipReason ([string][char]0x20ac * 1500) } | Should -Throw '*exceeds*'
        [System.IO.File]::ReadAllText($target) | Should -BeExactly $original
    }

    It 'drops per-suite detail when the record exceeds the size limit and still writes a valid line' {
        $bigSuite = [pscustomobject]@{ name = ('x' * 5000); total = 1; executed = 1; passed = 1; failed = 0; notExecuted = 0; trx_files = 1; malformed_files = 0 }
        Write-LocalCiTelemetry @arguments -Suites @($bigSuite)
        $record = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
        $record.repo | Should -Be 'test-repo'
        @($record.suites).Count | Should -Be 0
    }

    It 'throws when the record still exceeds the size limit after dropping suites' {
        { Write-LocalCiTelemetry @arguments -TestSkipReason ('x' * 5000) } | Should -Throw '*exceeds*'
    }
}
