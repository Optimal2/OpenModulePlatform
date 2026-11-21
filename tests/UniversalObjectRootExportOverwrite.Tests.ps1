# Pester 6's 'Should -Be/-Throw' parameters are provided by the pinned Pester
# module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility rule
# measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()

# export-universal-package.ps1 refuses to overwrite an existing package.
# export-universal-object-root.ps1 deleted the existing file instead, so the
# same output name could silently change content.

Describe 'export-universal-object-root.ps1 never overwrites an existing package' {
    BeforeAll {
        $exportScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\omp\export-universal-object-root.ps1'
    }

    It 'Refuses an existing output file and leaves it unchanged' {
        $objectRoot = Join-Path $TestDrive 'object-root'
        $null = New-Item -ItemType Directory -Path $objectRoot -Force
        $outputPath = Join-Path $TestDrive 'existing.zip'
        [System.IO.File]::WriteAllText($outputPath, 'existing package')

        { & $exportScript -ObjectRoot $objectRoot -OutputPath $outputPath } | Should -Throw '*already exists and was not replaced*'

        [System.IO.File]::ReadAllText($outputPath) | Should -Be 'existing package'
    }
}
