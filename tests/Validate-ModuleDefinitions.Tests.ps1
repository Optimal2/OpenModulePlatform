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
