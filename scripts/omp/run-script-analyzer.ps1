#Requires -Version 5.1
<#
.SYNOPSIS
    Runs PSScriptAnalyzer (security + Windows PowerShell 5.1 compatibility)
    over every committed PowerShell file in the repository.

.DESCRIPTION
    Bootstraps the PINNED PSScriptAnalyzer version into the repository-local
    module cache (<repoRoot>/.psmodules, gitignored) when the cache does not
    hold that exact version -- the same pattern as pester-bootstrap.ps1: a
    globally installed copy of EXACTLY the pinned version seeds the cache
    byte-for-byte when one exists, otherwise Save-Module restores the pinned
    gallery copy, and the module is ALWAYS imported by full path from the
    cache. The user-scoped module folders are never installed to and never
    loaded from: the old bootstrap installed to the user scope (the user's
    Documents on the hub's loaded E: data disk) and hung the pre-push gate for
    hours (measured 2026-10-07). The version is pinned, like the Pester 6.1.0
    pin in pester-bootstrap.ps1, so the diagnostics a gate reports cannot
    drift with whatever analyzer version a machine happens to carry (second
    opinion, 2026-10-08). Then enumerates committed scripts via `git
    ls-files` and analyzes them with scripts/omp/PSScriptAnalyzerSettings.psd1.

    Exits 1 when any diagnostic of Severity Error or Warning is found, so the
    script can be used as a local grind and as a CI gate.

.EXAMPLE
    pwsh -File scripts/omp/run-script-analyzer.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $RequiredVersion = '1.25.0'
)

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
# Get-WindowsPowerShellSafeModulePath for the restore below. Its own param
# block ($RequiredVersion = '6.1.0' for Pester, $CacheRoot = '') rebinds those
# variables in THIS scope when dot-sourced, so snapshot the analyzer pin first.
$pinnedScriptAnalyzerVersion = $RequiredVersion
. (Join-Path $PSScriptRoot 'pester-bootstrap.ps1')

$analyzerCacheRoot = Join-Path $repoRoot '.psmodules'

function Get-CachedScriptAnalyzer {
    <#
    .SYNOPSIS
        Returns the pinned PSScriptAnalyzer in the repository-local cache as
        @{ Version; ManifestPath }, or $null when the cache does not hold that
        exact version. Save-Module lays modules out as <root>/<name>/<version>;
        the directory name is a claim and the manifest's declared ModuleVersion
        is the fact, so a folder whose manifest is missing or disagrees is
        skipped, and so is any version other than the pin (the half-restored-
        folder guard from pester-bootstrap.ps1, plus the pin itself).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$CacheRoot,
        [Parameter(Mandatory = $true)][string]$RequiredVersion
    )

    $versionDirectory = Get-Item -LiteralPath (Join-Path (Join-Path $CacheRoot 'PSScriptAnalyzer') $RequiredVersion) -ErrorAction SilentlyContinue
    if ($null -eq $versionDirectory -or -not $versionDirectory.PSIsContainer) {
        return $null
    }

    $manifestPath = Join-Path $versionDirectory.FullName 'PSScriptAnalyzer.psd1'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return $null
    }

    $declaredVersion = Get-PesterManifestVersion -ManifestPath $manifestPath
    $parsedVersion = $null
    if (-not [version]::TryParse($declaredVersion, [ref]$parsedVersion)) {
        return $null
    }
    if (-not [string]::Equals($declaredVersion, $RequiredVersion, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The repository-local cache holds a PSScriptAnalyzer manifest declaring version '$declaredVersion' under the folder for $RequiredVersion ($($versionDirectory.FullName)); refusing to serve it. Delete the folder and rerun."
    }

    return [pscustomobject]@{ Version = $parsedVersion; ManifestPath = $manifestPath }
}

function Restore-ScriptAnalyzer {
    <#
    .SYNOPSIS
        Seeds the repository-local cache with the pinned PSScriptAnalyzer. A
        globally installed copy of EXACTLY the pinned version is copied in
        byte-for-byte (the same bytes PSGallery would hand back, without a
        network round-trip); any other global version is ignored, and
        Save-Module restores the pinned gallery copy instead. What gets
        imported is always the cache.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$CacheRoot,
        [Parameter(Mandatory = $true)][string]$RequiredVersion
    )

    $null = New-Item -ItemType Directory -Path $CacheRoot -Force

    $global = @(Get-Module -ListAvailable -Name PSScriptAnalyzer | Where-Object {
        $_.Version.ToString() -eq $RequiredVersion -and (Test-Path -LiteralPath (Join-Path $_.ModuleBase 'PSScriptAnalyzer.psd1') -PathType Leaf)
    }) | Select-Object -First 1
    if ($global -and (Get-PesterManifestVersion -ManifestPath (Join-Path $global.ModuleBase 'PSScriptAnalyzer.psd1')) -eq $RequiredVersion) {
        $target = Join-Path (Join-Path $CacheRoot 'PSScriptAnalyzer') $RequiredVersion
        Write-Host "Copying the globally installed PSScriptAnalyzer $RequiredVersion ($($global.ModuleBase)) into the repository-local cache."
        $null = New-Item -ItemType Directory -Path $target -Force
        # -LiteralPath would take the '*' literally and copy nothing; enumerate the folder instead.
        Get-ChildItem -LiteralPath $global.ModuleBase -Force | Copy-Item -Destination $target -Recurse -Force
    }
    else {
        Write-Host "PSScriptAnalyzer $RequiredVersion is not in the repository-local cache ($CacheRoot); restoring from PSGallery..."
        $originalModulePath = $env:PSModulePath
        try {
            if ($PSVersionTable.PSVersion.Major -le 5) {
                $env:PSModulePath = Get-WindowsPowerShellSafeModulePath
            }
            try {
                Save-Module -Name PSScriptAnalyzer -RequiredVersion $RequiredVersion -Path $CacheRoot -Repository PSGallery -Force
            }
            catch {
                throw "Could not restore PSScriptAnalyzer $RequiredVersion from PSGallery into '$CacheRoot': $($_.Exception.Message). No globally installed PSScriptAnalyzer $RequiredVersion was available to copy either. Check network/proxy access to PSGallery, or install the exact version once on this machine so the cache can be seeded from it."
            }
        }
        finally {
            $env:PSModulePath = $originalModulePath
        }
    }

    $cached = Get-CachedScriptAnalyzer -CacheRoot $CacheRoot -RequiredVersion $RequiredVersion
    if (-not $cached) {
        throw "The restore completed but PSScriptAnalyzer $RequiredVersion was not found under '$CacheRoot'; the restore cannot be trusted."
    }
    return $cached
}

$cached = Get-CachedScriptAnalyzer -CacheRoot $analyzerCacheRoot -RequiredVersion $pinnedScriptAnalyzerVersion
if (-not $cached) {
    $cached = Restore-ScriptAnalyzer -CacheRoot $analyzerCacheRoot -RequiredVersion $pinnedScriptAnalyzerVersion
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
