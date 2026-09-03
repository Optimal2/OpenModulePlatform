#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the OMP Pester script test suites (tests/*.Tests.ps1) and returns a
    pass/fail exit code for use as a blocking gate.

.DESCRIPTION
    Canonical entry point for the script test suites, used by both the local
    pre-push gate (.githooks/pre-push.ps1) and GitHub CI
    (.github/workflows/ci.yml).

    The suites use the Pester 5 dialect ('Should -Be' etc.); the Pester 3.4.0
    legacy dialect ('Should Be') was removed in Pester 5, and the suites were
    migrated off it in 2026-09. Pester 3.4.0 ships inbox with Windows
    PowerShell 5.1 but is EOL, so the module is pinned to an explicit 5.x
    version instead of floating to whatever a machine happens to carry.
    Callers invoke this runner via powershell.exe so the parent and spawned
    child processes share the same engine: the suites stay on Windows
    PowerShell 5.1 because one suite spawns child powershell.exe processes as
    a Windows requirement -- it is the Pester MODULE version that is pinned,
    not the engine requirement.

    Shared per-suite harness code lives in tests/*.TestHelpers.ps1 and is
    dot-sourced from each Describe block's BeforeAll, because Pester 5 runs
    every container in a separate session state where file-scope functions
    and variables are not visible.

    Invoke-Pester never sets $LASTEXITCODE, so the explicit exit below IS the
    contract: exit 1 when any test fails, exit 0 otherwise.

.EXAMPLE
    powershell.exe -NoProfile -File scripts/omp/run-script-tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Pin Pester 5.9.1 explicitly: the suites use the Pester 5 dialect, and CI
# images also carry Pester 3.4.0 inbox in the Windows PowerShell module path,
# so auto-load could silently pick the wrong version and fail every suite with
# CommandNotFoundException ('Should -Be' does not exist in Pester 3.4). CI
# installs this exact version before invoking the runner; on a dev machine a
# missing pin fails loudly here with the install command, rather than
# drifting to another version.
$script:RequiredPesterVersion = '5.9.1'
if (-not (Get-Module -ListAvailable Pester | Where-Object { $_.Version -eq [Version]$script:RequiredPesterVersion })) {
    Write-Host "Pester $script:RequiredPesterVersion is not installed for Windows PowerShell 5.1." -ForegroundColor Red
    Write-Host "Install it (CurrentUser scope): Install-Module Pester -RequiredVersion $script:RequiredPesterVersion -Scope CurrentUser -Force -SkipPublisherCheck" -ForegroundColor Red
    exit 1
}
Import-Module Pester -RequiredVersion $script:RequiredPesterVersion -Force

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testsPath = Join-Path $repoRoot 'tests'

$results = Invoke-Pester -Path $testsPath -PassThru
if ($results.FailedCount -gt 0) {
    exit 1
}
exit 0
