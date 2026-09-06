# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 assertions are provided by the pinned module.')]
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
}
