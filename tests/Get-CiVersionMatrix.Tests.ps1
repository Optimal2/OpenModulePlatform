# Pester 5's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for scripts/omp/get-ci-version-matrix.ps1.

.DESCRIPTION
The CI version matrix is derived from the repository's own version files
(global.json SDK pin + committed target frameworks), and the derivation script
is also the unsupported-version gate: a matrix pointed at a .NET major the
code does not target must fail before any build starts. These tests build
temporary repositories with controlled global.json/.csproj content and verify
both the emitted legs and the gate.

The script is invoked in-process with &, so a thrown gate failure is caught by
the test instead of terminating the Pester host. GITHUB_OUTPUT is cleared
around each invocation: in GitHub Actions that variable exists in every step
and the script would otherwise append its matrix output to the step's output
file.

Pester 5 runs every container in a separate session state, so the shared
harness (script path + temp-repo helpers) lives in
Get-CiVersionMatrix.TestHelpers.ps1 and is dot-sourced from each Describe
block's BeforeAll.
#>

Describe 'get-ci-version-matrix: derivation from repository version files' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Get-CiVersionMatrix.TestHelpers.ps1')
    }

    It 'Emits pinned and latest-band push legs plus the scheduled runtime-floor leg' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot
            $result = Invoke-MatrixScript -ScriptPath $scriptPath -IncludeScheduled

            $result.Threw | Should -Be $false
            $names = @($result.Matrix.include | ForEach-Object { $_.name })
            $names -contains 'sdk-pinned' | Should -Be $true
            $names -contains 'sdk-latest-band' | Should -Be $true
            $names -contains 'runtime-floor' | Should -Be $true

            $pinned = @($result.Matrix.include | Where-Object { $_.name -eq 'sdk-pinned' })[0]
            $pinned.sdk | Should -Be '10.0.200'
            $pinned.cadence | Should -Be 'push'
            $pinned.pinExact | Should -Be 'true'

            $latest = @($result.Matrix.include | Where-Object { $_.name -eq 'sdk-latest-band' })[0]
            $latest.sdk | Should -Be '10.0.x'
            $latest.pinExact | Should -Be 'false'

            $floor = @($result.Matrix.include | Where-Object { $_.name -eq 'runtime-floor' })[0]
            $floor.runtimeFloor | Should -Be '10.0.0'
            $floor.cadence | Should -Be 'scheduled'
            $floor.pinExact | Should -Be 'true'
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Excludes scheduled legs unless -IncludeScheduled is passed' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should -Be $false
            $names = @($result.Matrix.include | ForEach-Object { $_.name })
            $names.Count | Should -Be 2
            $names -contains 'runtime-floor' | Should -Be $false
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Derives the SDK major and runtime floor from global.json, not a hardcoded list' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -SdkVersion '8.0.100' -RollForward 'latestFeature' -TargetFrameworks @('net8.0')
            $result = Invoke-MatrixScript -ScriptPath $scriptPath -IncludeScheduled

            $result.Threw | Should -Be $false
            $pinned = @($result.Matrix.include | Where-Object { $_.name -eq 'sdk-pinned' })[0]
            $pinned.sdk | Should -Be '8.0.100'
            $pinned.expectedMajor | Should -Be '8'
            $floor = @($result.Matrix.include | Where-Object { $_.name -eq 'runtime-floor' })[0]
            $floor.runtimeFloor | Should -Be '8.0.0'
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }
}

Describe 'get-ci-version-matrix: unsupported-version gate' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Get-CiVersionMatrix.TestHelpers.ps1')
    }

    It 'Fails when a project targets a major the pinned SDK does not support' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -TargetFrameworks @('net10.0', 'net9.0')
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should -Be $true
            ($result.ErrorMessage -match 'does not support') | Should -Be $true
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Fails when global.json pins an SDK major below the target frameworks' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -SdkVersion '9.0.100' -RollForward 'latestFeature' -TargetFrameworks @('net10.0')
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should -Be $true
            ($result.ErrorMessage -match 'does not support') | Should -Be $true
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Fails loudly on a cross-major rollForward policy instead of deriving a wrong matrix' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -RollForward 'latestMajor'
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should -Be $true
            ($result.ErrorMessage -match 'not mapped') | Should -Be $true
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }

    It 'Allows netstandard analyzer targets as compiler-loaded exemptions' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -TargetFrameworks @('net10.0', 'netstandard2.0')
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should -Be $false
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }
}

Describe 'get-ci-version-matrix: rollForward mapping' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Get-CiVersionMatrix.TestHelpers.ps1')
    }

    It 'Emits only the pinned leg when rollForward forbids band drift' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $scriptPath = New-TemporaryMatrixRepository -RootPath $repoRoot -RollForward 'disable'
            $result = Invoke-MatrixScript -ScriptPath $scriptPath

            $result.Threw | Should -Be $false
            $names = @($result.Matrix.include | ForEach-Object { $_.name })
            $names.Count | Should -Be 1
            $names[0] | Should -Be 'sdk-pinned'
        }
        finally {
            Remove-TemporaryMatrixRepository -RootPath $repoRoot
        }
    }
}
