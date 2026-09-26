#Requires -Version 5.1
# Pester 5's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (5.9.1), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 5 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
    Proves that sign-artifacts.ps1 reports an external overlay whose
    codeSigning.includePatterns section is missing or misspelled.

.DESCRIPTION
    Module binaries built in other repositories are only signed when their name
    patterns come from the gitignored omp-components.external.json overlay. An
    overlay without codeSigning.includePatterns used to contribute no patterns
    without a word, so those binaries shipped unsigned and nothing said so.

    Pester 5 runs every container in a separate session state, so the shared
    harness lives in Sign-Artifacts.TestHelpers.ps1 and is dot-sourced from
    the Describe block's BeforeAll.
#>

Set-StrictMode -Version Latest

Describe 'sign-artifacts: external overlay codeSigning section' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Sign-Artifacts.TestHelpers.ps1')
    }

    BeforeEach {
        $script:sandbox = New-SignArtifactsSandbox
    }

    AfterEach {
        Remove-SignArtifactsSandbox -Sandbox $script:sandbox
    }

    It 'Does not warn when the overlay declares codeSigning.includePatterns' {
        [System.IO.File]::WriteAllText($script:sandbox.OverlayPath, '{ "codeSigning": { "includePatterns": [ "ExampleModule.*.dll" ] } }', [System.Text.Encoding]::UTF8)

        $result = Invoke-SignArtifacts -Sandbox $script:sandbox

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Not -Match 'codeSigning'
        $result.Output | Should -Match 'nothing to sign'
    }

    It 'Warns with the expected structure when the overlay has no codeSigning section' {
        [System.IO.File]::WriteAllText($script:sandbox.OverlayPath, '{ "sharedProjects": [] }', [System.Text.Encoding]::UTF8)

        $result = Invoke-SignArtifacts -Sandbox $script:sandbox

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'omp-components\.external\.json'
        $result.Output | Should -Match '"codeSigning": \{ "includePatterns"'
    }

    It 'Warns when includePatterns is misspelled' {
        [System.IO.File]::WriteAllText($script:sandbox.OverlayPath, '{ "codeSigning": { "includePattern": [ "ExampleModule.*.dll" ] } }', [System.Text.Encoding]::UTF8)

        $result = Invoke-SignArtifacts -Sandbox $script:sandbox

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'codeSigning\.includePatterns'
    }

    It 'Warns when includePatterns is an empty list' {
        [System.IO.File]::WriteAllText($script:sandbox.OverlayPath, '{ "codeSigning": { "includePatterns": [] } }', [System.Text.Encoding]::UTF8)

        $result = Invoke-SignArtifacts -Sandbox $script:sandbox

        $result.Output | Should -Match 'codeSigning\.includePatterns'
    }

    It 'Fails with -Strict when the overlay has no codeSigning section' {
        [System.IO.File]::WriteAllText($script:sandbox.OverlayPath, '{ "sharedProjects": [] }', [System.Text.Encoding]::UTF8)

        { Invoke-SignArtifacts -Sandbox $script:sandbox -Strict } | Should -Throw -ExpectedMessage '*codeSigning.includePatterns*'
    }
}
