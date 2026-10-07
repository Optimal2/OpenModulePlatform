# Pester 6's 'Should -Be/-Not -Be/-Match' parameters are provided by the pinned
# Pester module (6.1.0), not by the inbox Pester 3.4.0 profile the compatibility
# rule measures against; suppress for the whole file, not per assertion.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
Pester tests for the PSScriptAnalyzer bootstrap in
scripts/omp/run-script-analyzer.ps1.

.DESCRIPTION
The old bootstrap ran Install-Module -Scope CurrentUser when the analyzer was
missing: it wrote to the user's Documents on the hub's loaded E: data disk and
hung the pre-push gate for hours (measured 2026-10-07). The fixed bootstrap
uses the repository-local .psmodules cache exactly like pester-bootstrap.ps1:
Save-Module into <repoRoot>/.psmodules, import by full path from the cache,
never the user scope. These assertions pin that contract against regression.
#>

Describe 'run-script-analyzer.ps1 bootstrap' {
    BeforeAll {
        $script:analyzerSource = Get-Content (Join-Path $PSScriptRoot '..\scripts\omp\run-script-analyzer.ps1') -Raw -Encoding UTF8
    }

    It 'Never installs a module (no Install-Module anywhere in the script)' {
        $script:analyzerSource | Should -Not -Match 'Install-Module'
    }

    It 'Never touches the CurrentUser scope' {
        $script:analyzerSource | Should -Not -Match '-Scope\s+CurrentUser'
        $script:analyzerSource | Should -Not -Match 'Set-PSRepository'
    }

    It 'Bootstraps into the repository-local .psmodules cache (pester-bootstrap pattern)' {
        $script:analyzerSource | Should -Match '\.psmodules'
        $script:analyzerSource | Should -Match 'Save-Module'
    }

    It 'Imports the analyzer by full path from the cache, never by name from the module path' {
        $script:analyzerSource | Should -Match 'Import-Module\s+\$cached\.ManifestPath'
        $script:analyzerSource | Should -Not -Match 'Import-Module\s+PSScriptAnalyzer'
    }
}
