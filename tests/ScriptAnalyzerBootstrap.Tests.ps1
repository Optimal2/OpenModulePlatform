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

    It 'Pins an exact PSScriptAnalyzer version, like the Pester 6.1.0 pin' {
        # The diagnostics a gate reports must not drift with whatever analyzer
        # version a machine happens to carry (second opinion, 2026-10-08).
        $script:analyzerSource | Should -Match '\[string\] \$RequiredVersion = ''1\.25\.0'''
        $script:analyzerSource | Should -Match 'Save-Module -Name PSScriptAnalyzer -RequiredVersion \$RequiredVersion'
        # Dot-sourcing pester-bootstrap.ps1 rebinds $RequiredVersion to the
        # PESTER pin in this scope; the analyzer pin must be snapshot first.
        $script:analyzerSource | Should -Match '\$pinnedScriptAnalyzerVersion = \$RequiredVersion'
        $script:analyzerSource | Should -Match 'Get-CachedScriptAnalyzer -CacheRoot \$analyzerCacheRoot -RequiredVersion \$pinnedScriptAnalyzerVersion'
    }

    It 'Serves and seeds only the pinned version' {
        # The cache lookup is the exact pinned folder, verified against its
        # manifest -- never "the newest folder the cache happens to hold"...
        $script:analyzerSource | Should -Match 'Join-Path \(Join-Path \$CacheRoot ''PSScriptAnalyzer''\) \$RequiredVersion'
        $script:analyzerSource | Should -Not -Match 'Sort-Object Version -Descending'
        # ...and a globally installed copy seeds the cache only when it IS the
        # pinned version, verified by manifest, never "whatever Get-Module
        # -ListAvailable finds first".
        $script:analyzerSource | Should -Match '\$_\.Version\.ToString\(\) -eq \$RequiredVersion'
        $script:analyzerSource | Should -Match 'Get-PesterManifestVersion -ManifestPath \(Join-Path \$global\.ModuleBase ''PSScriptAnalyzer\.psd1''\)\) -eq \$RequiredVersion'
    }
}
