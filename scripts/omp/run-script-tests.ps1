#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the OMP Pester script test suites (tests/*.Tests.ps1) and returns a
    pass/fail exit code for use as a blocking gate.

.DESCRIPTION
    Canonical entry point for the script test suites, used by both the local
    pre-push gate (.githooks/pre-push.ps1) and GitHub CI
    (.github/workflows/ci.yml).

    The suites use the Pester 3.4.0 legacy dialect ('Should Be'), which was
    removed in Pester 5, so this script must run under Windows PowerShell 5.1
    where Pester 3.4.0 ships inbox. Callers invoke it via powershell.exe so
    the parent and spawned child processes share the same engine.

    Pester 3.4's Invoke-Pester never sets $LASTEXITCODE, so the explicit exit
    below IS the contract: exit 1 when any test fails, exit 0 otherwise.

.EXAMPLE
    powershell.exe -NoProfile -File scripts/omp/run-script-tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Pin Pester 3.4.0 explicitly: the suites use the legacy 'Should Be' dialect
# (removed in Pester 5), and CI images also carry Pester 5 in the Windows
# PowerShell module path, so auto-load would silently pick the newest version
# and fail every test with CommandNotFoundException for the file-scope helper
# functions (Pester 5 runs containers in a separate session state). 3.4.0
# ships inbox with Windows PowerShell 5.1; if it is missing, fail loudly here
# rather than drift to another version.
Import-Module Pester -RequiredVersion 3.4.0 -Force

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testsPath = Join-Path $repoRoot 'tests'

$results = Invoke-Pester -Path $testsPath -PassThru
if ($results.FailedCount -gt 0) {
    exit 1
}
exit 0
