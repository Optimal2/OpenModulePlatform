# Pester 6's 'Should -Be' parameters are provided by the pinned Pester module
# (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility rule
# measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()

# scripts/omp/test-runtime-configuration-guard.ps1 holds the parity check
# between runtime-configuration-files.ps1 and the canonical C# list in
# OpenModulePlatform.Artifacts/RuntimeConfigurationFiles.cs, plus functional
# checks of the packaging guard. Nothing ran it, so the mirror could drift
# from the import-time validator without any gate noticing. This suite runs
# it in the Pester gate (run-script-tests.ps1: pre-push and CI).

Describe 'Runtime configuration guard self-test' {
    It 'test-runtime-configuration-guard.ps1 passes' {
        $guardTest = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\omp\test-runtime-configuration-guard.ps1'
        # Run in a child process of the current engine: the script ends with
        # 'exit', sets StrictMode and loads its own helpers.
        $engine = (Get-Process -Id $PID).Path

        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = & $engine -NoProfile -NonInteractive -File $guardTest 2>&1 | Out-String -Width 4096
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        $exitCode | Should -Be 0 -Because $output
    }
}
