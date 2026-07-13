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
Write-Host '--- Step 2: dotnet test (Release, no rebuild) ---'
& dotnet test $solutionPath -c Release --no-build
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- TESTS FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Tests passed ---'
Write-Host ''

# ---------------------------------------------------------------------------
# 3. Validate component versions against the upstream base.
# ---------------------------------------------------------------------------
Write-Host '--- Step 3: validate-component-versions.ps1 ---'
$componentValidator = Join-Path $repoRoot 'scripts\omp\validate-component-versions.ps1'
& $componentValidator -BaseCommit $baseCommit
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- COMPONENT VERSION VALIDATION FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Component version validation passed ---'
Write-Host ''

# ---------------------------------------------------------------------------
# 4. Validate module definitions.
# ---------------------------------------------------------------------------
Write-Host '--- Step 4: validate-module-definitions.ps1 ---'
$moduleValidator = Join-Path $repoRoot 'scripts\omp\validate-module-definitions.ps1'
& $moduleValidator
if ($LASTEXITCODE -ne 0) {
    Write-Host '--- MODULE DEFINITION VALIDATION FAILED ---' -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host '--- Module definition validation passed ---'
Write-Host ''

Write-Banner
exit 0
