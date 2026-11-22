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

# Because an existing output file is refused, a half-written one must never be left at
# the output name: it would block every later run until somebody removed it by hand.

Describe 'export-universal-object-root.ps1 leaves no half-written package behind' {
    BeforeAll {
        $exportScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\omp\export-universal-object-root.ps1'

        function New-ObjectRoot {
            $objectRoot = Join-Path $TestDrive ('object-root-' + [Guid]::NewGuid().ToString('N'))
            $definitions = Join-Path $objectRoot 'module-definitions'
            $null = New-Item -ItemType Directory -Path $definitions -Force
            [System.IO.File]::WriteAllText((Join-Path $definitions 'a_first.module-definition.json'), '{ "moduleKey": "a_first", "definitionVersion": "1.0.0" }')
            [System.IO.File]::WriteAllText((Join-Path $definitions 'b_second.module-definition.json'), '{ "moduleKey": "b_second", "definitionVersion": "1.0.0" }')
            return $objectRoot
        }
    }

    It 'Removes the partial package when packing fails, and the next run with the same output succeeds' {
        $objectRoot = New-ObjectRoot
        $outputDirectory = Join-Path $TestDrive ('out-' + [Guid]::NewGuid().ToString('N'))
        $outputPath = Join-Path $outputDirectory 'package.zip'
        $second = Join-Path $objectRoot 'module-definitions\b_second.module-definition.json'

        # The second object cannot be read, so the failure comes after the first entry
        # has already been written into the archive.
        $lock = [System.IO.File]::Open($second, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        try {
            { & $exportScript -ObjectRoot $objectRoot -OutputPath $outputPath } | Should -Throw
        }
        finally {
            $lock.Dispose()
        }

        Test-Path -LiteralPath $outputPath | Should -BeFalse
        @(Get-ChildItem -LiteralPath $outputDirectory -Force).Count | Should -Be 0

        & $exportScript -ObjectRoot $objectRoot -OutputPath $outputPath

        $archive = [System.IO.Compression.ZipFile]::OpenRead($outputPath)
        try {
            $names = @($archive.Entries | ForEach-Object { $_.FullName })
        }
        finally {
            $archive.Dispose()
        }
        $names | Should -Contain 'omp-universal-package.json'
        $names | Should -Contain 'module-definitions/a_first.module-definition.json'
        $names | Should -Contain 'module-definitions/b_second.module-definition.json'
        @(Get-ChildItem -LiteralPath $outputDirectory -Force).Name | Should -Be @('package.zip')
    }

    It 'Leaves only the finished package beside it after a normal run' {
        $objectRoot = New-ObjectRoot
        $outputDirectory = Join-Path $TestDrive ('out-' + [Guid]::NewGuid().ToString('N'))
        $outputPath = Join-Path $outputDirectory 'package.zip'

        & $exportScript -ObjectRoot $objectRoot -OutputPath $outputPath

        @(Get-ChildItem -LiteralPath $outputDirectory -Force).Name | Should -Be @('package.zip')
    }
}
