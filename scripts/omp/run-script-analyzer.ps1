#Requires -Version 5.1
<#
.SYNOPSIS
    Runs PSScriptAnalyzer (security + Windows PowerShell 5.1 compatibility)
    over every committed PowerShell file in the repository.

.DESCRIPTION
    Bootstraps PSScriptAnalyzer into the repository-local module cache
    (<repoRoot>/.psmodules, gitignored) when the cache is empty -- the same
    pattern as pester-bootstrap.ps1: a globally installed copy seeds the cache
    byte-for-byte when one exists, otherwise Save-Module restores the gallery
    copy, and the module is ALWAYS imported by full path from the cache. The
    user-scoped module folders are never installed to and never loaded from:
    the old bootstrap installed to the user scope (the user's Documents on the
    hub's loaded E: data disk) and hung the pre-push gate for hours (measured
    2026-10-07). Then enumerates committed scripts via `git ls-files` and
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
# Dot-sourcing pester-bootstrap.ps1 provides Get-PesterManifestVersion (a
# generic manifest reader despite the name) and
# Get-WindowsPowerShellSafeModulePath for the restore below.
. (Join-Path $PSScriptRoot 'pester-bootstrap.ps1')

$analyzerCacheRoot = Join-Path $repoRoot '.psmodules'

function Get-CachedScriptAnalyzer {
    <#
    .SYNOPSIS
        Returns the newest PSScriptAnalyzer in the repository-local cache as
        @{ Version; ManifestPath }, or $null when the cache holds none.
        Save-Module lays modules out as <root>/<name>/<version>; the directory
        name is a claim and the manifest's declared ModuleVersion is the fact,
        so a folder whose manifest is missing or disagrees is skipped (the
        half-restored-folder guard from pester-bootstrap.ps1).
    #>
    param([Parameter(Mandatory = $true)][string]$CacheRoot)

    $moduleRoot = Join-Path $CacheRoot 'PSScriptAnalyzer'
    if (-not (Test-Path -LiteralPath $moduleRoot -PathType Container)) {
        return $null
    }

    $candidates = @()
    foreach ($versionDirectory in @(Get-ChildItem -LiteralPath $moduleRoot -Directory)) {
        $manifestPath = Join-Path $versionDirectory.FullName 'PSScriptAnalyzer.psd1'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            continue
        }

        $declaredVersion = Get-PesterManifestVersion -ManifestPath $manifestPath
        $parsedVersion = $null
        if (-not [version]::TryParse($declaredVersion, [ref]$parsedVersion)) {
            continue
        }
        if (-not [string]::Equals($versionDirectory.Name, $declaredVersion, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $candidates += [pscustomobject]@{ Version = $parsedVersion; ManifestPath = $manifestPath }
    }

    return ($candidates | Sort-Object Version -Descending | Select-Object -First 1)
}

function Restore-ScriptAnalyzer {
    <#
    .SYNOPSIS
        Seeds the repository-local cache with PSScriptAnalyzer. A globally
        installed copy is copied in byte-for-byte (the same bytes PSGallery
        would hand back, without a network round-trip); otherwise Save-Module
        restores the gallery copy. What gets imported is always the cache.
    #>
    param([Parameter(Mandatory = $true)][string]$CacheRoot)

    $null = New-Item -ItemType Directory -Path $CacheRoot -Force

    $globalCopies = @(Get-Module -ListAvailable -Name PSScriptAnalyzer | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.ModuleBase 'PSScriptAnalyzer.psd1') -PathType Leaf
    })
    if ($globalCopies.Count -gt 0) {
        $global = @($globalCopies | Sort-Object Version -Descending)[0]
        $target = Join-Path (Join-Path $CacheRoot 'PSScriptAnalyzer') ($global.Version.ToString())
        Write-Host "Copying the globally installed PSScriptAnalyzer $($global.Version) ($($global.ModuleBase)) into the repository-local cache."
        $null = New-Item -ItemType Directory -Path $target -Force
        # -LiteralPath would take the '*' literally and copy nothing; enumerate the folder instead.
        Get-ChildItem -LiteralPath $global.ModuleBase -Force | Copy-Item -Destination $target -Recurse -Force
    }
    else {
        Write-Host "PSScriptAnalyzer is not in the repository-local cache ($CacheRoot); restoring from PSGallery..."
        $originalModulePath = $env:PSModulePath
        try {
            if ($PSVersionTable.PSVersion.Major -le 5) {
                $env:PSModulePath = Get-WindowsPowerShellSafeModulePath
            }
            try {
                Save-Module -Name PSScriptAnalyzer -Path $CacheRoot -Repository PSGallery -Force
            }
            catch {
                throw "Could not restore PSScriptAnalyzer from PSGallery into '$CacheRoot': $($_.Exception.Message). Check network/proxy access to PSGallery, or install the module once on this machine so the cache can be seeded from it."
            }
        }
        finally {
            $env:PSModulePath = $originalModulePath
        }
    }

    $cached = Get-CachedScriptAnalyzer -CacheRoot $CacheRoot
    if (-not $cached) {
        throw "The restore completed but no PSScriptAnalyzer was found under '$CacheRoot'; the restore cannot be trusted."
    }
    return $cached
}

$cached = Get-CachedScriptAnalyzer -CacheRoot $analyzerCacheRoot
if (-not $cached) {
    $cached = Restore-ScriptAnalyzer -CacheRoot $analyzerCacheRoot
}

# By full path from the repository-local cache, never by name: a module-path
# lookup is exactly how a user-scoped copy gets loaded instead.
Import-Module $cached.ManifestPath -Force
$analyzerVersion = $cached.Version

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
    $files.Count, $settingsPath, $analyzerVersion)

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
