[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 dialect: parameters come from the pinned Pester module, not the inbox 3.4.0 profile.')]
param()
<#
.SYNOPSIS
    Guards the Windows PowerShell 5.1 Utility-module session hazard.

.DESCRIPTION
    Under Windows PowerShell 5.1, Get-FileHash and Import-PowerShellDataFile
    are function commands layered by the inbox Microsoft.PowerShell.Utility
    module on top of the console snap-in. On a machine whose PSModulePath also
    lists PowerShell 7 module folders, the first use of such a command inside
    a Pester container resolves the 7.x Utility copy first; that import fails
    under 5.1 with no fallback, so the 10 inbox function commands stay
    unrecognized in every session state of the process (measured 2026-10-20:
    ArtifactPackageDeterminism, FreshInstallPackage build cases, the
    Import-PowerShellDataFile case, and the deterministic-publish step fail
    exactly this way). scripts/local-ci.ps1 pre-imports the inbox copy by full
    path before the gate, which makes every later resolution serve the loaded
    module instead of searching the path. These assertions fail red on a
    hostile path without that pre-import and stay green with it (and on a clean
    path either way), so the hazard can never go silent again.
#>

Describe 'Windows PowerShell Utility session integrity' {
    It 'Resolves Get-FileHash in this session' {
        ($null -ne (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) | Should -BeTrue
    }

    It 'Resolves Import-PowerShellDataFile in this session' {
        ($null -ne (Get-Command Import-PowerShellDataFile -ErrorAction SilentlyContinue)) | Should -BeTrue
    }

    It 'Has the inbox Utility function commands loaded on Windows PowerShell 5.1' -Skip:($PSVersionTable.PSVersion.Major -gt 5) {
        $utility = Get-Module Microsoft.PowerShell.Utility
        ($null -ne $utility) | Should -BeTrue
        $utility.ExportedCommands.ContainsKey('Get-FileHash') | Should -BeTrue
        $utility.ExportedCommands.ContainsKey('Import-PowerShellDataFile') | Should -BeTrue
    }

    AfterAll {
        # Later gate steps (deterministic publish) hash in this same process:
        # leaving the session without Get-FileHash must fail here, not there.
        ($null -ne (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) | Should -BeTrue
    }
}
