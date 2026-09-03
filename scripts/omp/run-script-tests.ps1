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

    Invoke-Pester never sets $LASTEXITCODE, so the explicit exits below ARE
    the contract. FailedCount alone is not enough: it only counts tests that
    RAN and failed, so a suite that dies at discovery time (syntax error, a
    bad BeforeDiscovery) or a tests path that resolves to nothing would
    report FailedCount 0 and exit 0 -- the same false green the
    zero-execution TRX gate closes for dotnet test. The runner therefore
    also fails when the overall Pester result is not 'Passed' (covers
    container/discovery failures), when zero tests passed, and when fewer
    containers ran than suite files exist on disk (a suite renamed away from
    the *.Tests.ps1 glob would otherwise silently stop running).

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

$suiteFileCount = @(Get-ChildItem -LiteralPath $testsPath -Filter '*.Tests.ps1' -File).Count
if ($results.Result -ne 'Passed') {
    Write-Host "GATE FAIL: overall Pester result is '$($results.Result)', not 'Passed' (a container failed before or during its run)." -ForegroundColor Red
    exit 1
}
if ($results.PassedCount -eq 0) {
    Write-Host "GATE FAIL: Pester ran 0 passing tests. A green exit from zero assertions proves nothing." -ForegroundColor Red
    exit 1
}
if (@($results.Containers).Count -ne $suiteFileCount) {
    Write-Host "GATE FAIL: $suiteFileCount suite file(s) match *.Tests.ps1 under $testsPath, but $(@($results.Containers).Count) container(s) ran." -ForegroundColor Red
    exit 1
}
if ($results.FailedCount -gt 0) {
    exit 1
}
exit 0
