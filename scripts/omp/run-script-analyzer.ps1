#Requires -Version 5.1
<#
.SYNOPSIS
    Runs PSScriptAnalyzer (security + Windows PowerShell 5.1 compatibility)
    over every committed PowerShell file in the repository.

.DESCRIPTION
    Bootstraps PSScriptAnalyzer from PSGallery into CurrentUser scope when the
    module is missing, enumerates committed scripts via `git ls-files`, and
    analyzes them with scripts/omp/PSScriptAnalyzerSettings.psd1.

    Exits 1 when any diagnostic of Severity Error or Warning is found, so the
    script can be used as a local grind and as a CI gate.

.EXAMPLE
    pwsh -File scripts/omp/run-script-analyzer.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$settingsPath = Join-Path $PSScriptRoot 'PSScriptAnalyzerSettings.psd1'

if (-not (Test-Path $settingsPath)) {
    Write-Error "Settings file not found: $settingsPath"
    exit 1
}

# --- Bootstrap PSScriptAnalyzer -------------------------------------------
$analyzerModule = Get-Module -ListAvailable PSScriptAnalyzer |
    Sort-Object Version -Descending |
    Select-Object -First 1

if (-not $analyzerModule) {
    Write-Host 'PSScriptAnalyzer is not installed; installing from PSGallery (CurrentUser scope)...'
    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
    Install-Module -Name PSScriptAnalyzer -Scope CurrentUser -Force -AllowClobber
    $analyzerModule = Get-Module -ListAvailable PSScriptAnalyzer |
        Sort-Object Version -Descending |
        Select-Object -First 1
}

Import-Module PSScriptAnalyzer -MinimumVersion $analyzerModule.Version -Force

# --- Enumerate committed scripts -------------------------------------------
$relativeFiles = git -C $repoRoot ls-files '*.ps1' '*.psm1' '*.psd1'
if ($LASTEXITCODE -ne 0) {
    Write-Error 'git ls-files failed.'
    exit 1
}

$files = @()
foreach ($relativeFile in $relativeFiles) {
    if ($relativeFile) {
        $files += Join-Path $repoRoot $relativeFile
    }
}

Write-Host ("Analyzing {0} committed PowerShell files with {1} (PSScriptAnalyzer {2})..." -f `
    $files.Count, $settingsPath, $analyzerModule.Version)

# --- Analyze ----------------------------------------------------------------
$diagnostics = @()
foreach ($file in $files) {
    $diagnostics += @(Invoke-ScriptAnalyzer -Path $file -Settings $settingsPath)
}

# --- Report -----------------------------------------------------------------
$gating = @($diagnostics | Where-Object { $_.Severity -in @('Error', 'Warning') })

foreach ($diagnostic in $gating) {
    Write-Host ("{0}: {1}:{2}:{3} [{4}] {5}" -f `
        $diagnostic.Severity, `
        $diagnostic.ScriptPath, `
        $diagnostic.Line, `
        $diagnostic.Column, `
        $diagnostic.RuleName, `
        $diagnostic.Message)
}

Write-Host ''
Write-Host ("Diagnostics: {0} total, {1} error(s), {2} warning(s)." -f `
    $diagnostics.Count, `
    @($gating | Where-Object { $_.Severity -eq 'Error' }).Count, `
    @($gating | Where-Object { $_.Severity -eq 'Warning' }).Count)

if ($gating.Count -gt 0) {
    Write-Host 'Script analyzer grind FAILED. Fix the diagnostics or suppress them with a documented justification.'
    exit 1
}

Write-Host 'Script analyzer grind PASSED.'
exit 0
