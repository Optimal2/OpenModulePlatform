# Pester 5 assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()

Describe 'Module definition setup table union' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ModuleDefinitions.TestHelpers.ps1')
    }

    BeforeEach {
        $fixtureRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
    }

    It 'Accepts declared tables created across two setup files' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'Module definition validation passed\.'
    }

    It 'Rejects a truly missing table without reporting tables created in another file' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -RequiredTables First,Second,Missing
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match 'sample\.module-definition\.json'
        $result.Output | Should -Match 'setup-1\.sql'
        $result.Output | Should -Match 'setup-2\.sql'
        $result.Output | Should -Match 'sample\.Missing'
        $result.Output | Should -Not -Match 'sample\.(First|Second)'
    }

    It 'Rejects an undeclared table and names its SQL source file' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -SqlTexts @(
            'CREATE TABLE [sample].[First] (Id int);',
            "CREATE TABLE [sample].[Second] (Id int);`nCREATE TABLE [sample].[Extra] (Id int);"
        )
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match 'sample\.module-definition\.json'
        $result.Output | Should -Match 'sample\.Extra[^\r\n]*setup-2\.sql'
        $result.Output | Should -Not -Match 'sample\.(First|Second)'
    }

    It 'Still accepts a single setup file containing all declared tables' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -SqlTexts @(
            "CREATE TABLE [sample].[First] (Id int);`nCREATE TABLE [sample].[Second] (Id int);"
        )
        (Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot).ExitCode | Should -Be 0
    }

    It 'Rejects a missing table even when setup creates no tables' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -SqlTexts @('SELECT 1;') -RequiredTables Missing
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match 'setup-1\.sql'
        $result.Output | Should -Match 'sample\.Missing'
    }

    It 'Deduplicates table names case-insensitively across setup files' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -SqlTexts @(
            'CREATE TABLE [sample].[First] (Id int);',
            'CREATE TABLE SAMPLE.FIRST (Id int);'
        ) -RequiredTables First
        (Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot).ExitCode | Should -Be 0
    }

    It 'Does not count tables created by non-setup scripts' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -ScriptKeys setup-first,upgrade-second
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match 'sample\.Second'
    }
}

Describe 'Module definition manifest and embedding checks' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ModuleDefinitions.TestHelpers.ps1')
    }

    BeforeEach {
        $fixtureRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
    }

    It 'Rejects a manifest module key that differs from the definition' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -ManifestModuleKey other
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match "Module key mismatch for 'sample\.module-definition\.json'\. Manifest='other', definition='sample'"
    }

    It 'Rejects a manifest definition version that differs from the definition' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -ManifestDefinitionVersion 2.0.0
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match "Definition version mismatch for 'sample\.module-definition\.json'\. Manifest='2\.0\.0', definition='1\.0\.0'"
    }

    It 'Rejects a manifest entry whose definition file does not exist' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -ManifestDefinitionPath 'missing.module-definition.json'
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match 'Module definition file was not found: missing\.module-definition\.json'
    }

    It 'Rejects a referenced SQL file that does not exist' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -OmitSqlFiles
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match "SQL script referenced by 'sample\.module-definition\.json' was not found: setup-1\.sql"
        $result.Output | Should -Match "was not found: setup-2\.sql"
    }

    It 'Rejects a content encoding other than base64-utf8' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -ContentEncoding plain
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match "SQL script 'setup-first' in 'sample\.module-definition\.json' has contentEncoding 'plain', expected 'base64-utf8'"
    }

    It 'Rejects embedded content that no longer matches the SQL file' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -EmbeddedSqlText 'CREATE TABLE [sample].[Stale] (Id int);'
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match "SQL script 'setup-first' in 'sample\.module-definition\.json' has embedded content that does not match 'setup-1\.sql'"
        $result.Output | Should -Match 'embed-module-definition-sql\.ps1'
    }

    It 'Rejects a stale sha256 even when the embedded content is current' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot -Sha256Override ('0' * 64)
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match "SQL script 'setup-first' in 'sample\.module-definition\.json' has sha256 '0{64}', expected '[0-9a-f]{64}'"
        $result.Output | Should -Not -Match 'embedded content that does not match'
    }

    It 'Accepts a fixture whose manifest, definition and embedding all agree' {
        New-ModuleDefinitionFixture -RootPath $fixtureRoot
        $result = Invoke-ModuleDefinitionValidator -RootPath $fixtureRoot
        $result.ExitCode | Should -Be 0
        $result.Output | Should -Not -Match 'mismatch|not found|does not match|sha256'
    }
}
