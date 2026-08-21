<#
.SYNOPSIS
OMP pre-push gate — runs the repository's CI-equivalent checks locally.

.DESCRIPTION
This script is invoked by the git pre-push hook. It builds the solution,
runs all tests, and runs the OMP component/module validators. It blocks the
push if any step fails.

IMPORTANT: A local green gate does NOT guarantee CI will pass. SDKs, tool
versions, and environment may differ from the GitHub Actions runner. For this
public repository, verify CI on HEAD after pushing.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Preserve Unicode output (e.g. warning sign) on Windows PowerShell 5.1.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Write-Banner {
    $banner = @(
        '============================================================'
        'PRE-PUSH GATE PASSED (local)'
        [string][char]0x26A0 + ' This does NOT guarantee CI will pass — SDK/environment may differ.'
        'For PUBLIC repos: verify auto-CI on HEAD after push:'
        '  gh run list --branch main --workflow=ci.yml'
        '============================================================'
    )
    foreach ($line in $banner) {
        Write-Host $line
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solutionPath = Join-Path $repoRoot 'OpenModulePlatform.slnx'

# ---------------------------------------------------------------------------
# Read the push refs Git feeds on stdin and resolve the upstream base commit.
# stdin lines: <local ref> <local sha> <remote ref> <remote sha>
# Use the remote sha (current tip of the tracking ref) as the diff baseline.
# ---------------------------------------------------------------------------
$stdinText = [Console]::In.ReadToEnd()
$stdinLines = $stdinText -split "`r?`n"

$baseCommit = $null
foreach ($line in $stdinLines) {
    $fields = $line -split '\s+'
    if ($fields.Count -ge 4) {
        $remoteSha = $fields[3]
        if (-not [string]::IsNullOrWhiteSpace($remoteSha) -and $remoteSha -notmatch '^0+$') {
            $baseCommit = $remoteSha
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($baseCommit)) {
    Write-Host 'No remote SHA supplied by git (new branch or no tracking ref); falling back to origin/main.'
    $baseCommit = 'origin/main'
}

Write-Host ''
Write-Host '============================================================'
Write-Host 'OMP PRE-PUSH GATE'
Write-Host "Repository: $repoRoot"
Write-Host "Solution:   $solutionPath"
Write-Host "Base commit for validators: $baseCommit"
Write-Host '============================================================'
Write-Host ''

# ---------------------------------------------------------------------------
# 1. Build Release.
# ---------------------------------------------------------------------------
Write-Host '--- Step 1: dotnet build (Release) ---'
& dotnet build $solutionPath -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- BUILD FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Build passed ---'
Write-Host ''

# ---------------------------------------------------------------------------
# 2. Run tests.
# ---------------------------------------------------------------------------
# The UI suite (Category=Ui) is excluded from the push gate: it needs a
# one-time Chromium download and boots the built apps against a provisioned
# database. Run it separately: dotnet test --filter "Category=Ui".
Write-Host '--- Step 2: dotnet test (Release, no rebuild, Category!=Ui) ---'
& dotnet test $solutionPath -c Release --no-build --filter "Category!=Ui"
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- TESTS FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Tests passed ---'
Write-Host ''

# ---------------------------------------------------------------------------
# 3. Analyze PowerShell scripts.
#
# R8-P4-13: run-script-analyzer.ps1, the validator's own -SelfTest and
# validate-webshared-contracts.ps1 all existed and none of them were wired to
# this gate -- the contracts validator was not reachable from any gate at all.
# The banner said "PRE-PUSH GATE PASSED" regardless, which is worse than having
# no gate: it is a green light for checks that never ran. IbsPackager's
# local-ci.ps1 has run all three since R3.
# ---------------------------------------------------------------------------
Write-Host '--- Step 3: run-script-analyzer.ps1 ---'
$scriptAnalyzer = Join-Path $repoRoot 'scripts\omp\run-script-analyzer.ps1'
& $scriptAnalyzer
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- POWERSHELL SCRIPT ANALYSIS FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Script analysis passed ---'
Write-Host ''

# ---------------------------------------------------------------------------
# 4. Run Pester script tests (bump-version + component-version validator).
# ---------------------------------------------------------------------------
Write-Host '--- Step 4: run-script-tests.ps1 (Pester script tests) ---'
$scriptTests = Join-Path $repoRoot 'scripts\omp\run-script-tests.ps1'
& $scriptTests
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- SCRIPT TESTS FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Script tests passed ---'
Write-Host ''

# ---------------------------------------------------------------------------
# 5. Validate component versions against the upstream base.
#
# -SelfTest first: the validator's own helpers have PS5.1 pitfalls (BOM
# handling, worktree change detection) and a validator that is silently broken
# passes everything.
# ---------------------------------------------------------------------------
Write-Host '--- Step 5: validate-component-versions.ps1 ---'
$componentValidator = Join-Path $repoRoot 'scripts\omp\validate-component-versions.ps1'
& $componentValidator -BaseCommit $baseCommit -SelfTest
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- COMPONENT VERSION VALIDATION FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Component version validation passed ---'
Write-Host ''

# ---------------------------------------------------------------------------
# 6. Validate module definitions.
# ---------------------------------------------------------------------------
Write-Host '--- Step 6: validate-module-definitions.ps1 ---'
$moduleValidator = Join-Path $repoRoot 'scripts\omp\validate-module-definitions.ps1'
& $moduleValidator
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- MODULE DEFINITION VALIDATION FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Module definition validation passed ---'
Write-Host ''

# ---------------------------------------------------------------------------
# 7. Validate Web.Shared contracts.
# ---------------------------------------------------------------------------
Write-Host '--- Step 7: validate-webshared-contracts.ps1 ---'
$contractsValidator = Join-Path $repoRoot 'scripts\omp\validate-webshared-contracts.ps1'
& $contractsValidator
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- WEB.SHARED CONTRACT VALIDATION FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Web.Shared contract validation passed ---'
Write-Host ''

Write-Banner
exit 0
