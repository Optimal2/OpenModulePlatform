#Requires -Version 5.1
<#
.SYNOPSIS
    Ensures the pinned Pester version is available from a repository-local
    module cache, restoring it from PSGallery when the cache is empty.

.DESCRIPTION
    run-script-tests.ps1 pins an exact Pester version, but the pin used to be
    satisfied by whatever the local machine happened to have installed, so a
    clean machine failed before a single test ran. This bootstrap moves the
    dependency into the repository: the pinned module lives under
    <repoRoot>/.psmodules (gitignored), and Ensure-PinnedPester restores that
    exact version from PSGallery when the cache does not hold it.

    The cache root is prepended to THIS PROCESS's $env:PSModulePath only, so
    the user's global PowerShell environment is never modified, and a globally
    installed Pester of a different version (Windows carries 3.4.0 inbox) can
    never shadow the pin: the runner imports the resolved Pester.psd1 by full
    path.

    The file is dual-purpose: dot-sourced it provides the functions (that is
    how run-script-tests.ps1 and the test suite consume it), and invoked as a
    script it performs the ensure, imports the pinned module, and prints what
    was loaded -- the shape the PesterBootstrap test suite drives as a child
    process, because module-path state and exit codes are per-process.

.EXAMPLE
    powershell.exe -NoProfile -File scripts/omp/pester-bootstrap.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $RequiredVersion = '5.9.1',

    [Parameter(Mandatory = $false)]
    [string] $CacheRoot = ''
)

$ErrorActionPreference = 'Stop'

function Get-PinnedPesterCachePath {
    <#
    .SYNOPSIS
        Returns the module directory of the pinned Pester inside the
        repository-local cache, or $null when the cache does not hold that
        exact version. Save-Module lays modules out as <root>/<name>/<version>.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $CacheRoot,
        [Parameter(Mandatory = $true)][string] $RequiredVersion
    )

    $modulePath = Join-Path (Join-Path $CacheRoot 'Pester') $RequiredVersion
    if (Test-Path -LiteralPath (Join-Path $modulePath 'Pester.psd1') -PathType Leaf) {
        return $modulePath
    }
    return $null
}

function Add-PinnedPesterCacheToModulePath {
    <#
    .SYNOPSIS
        Prepends the cache root to THIS PROCESS's $env:PSModulePath so module
        auto-resolution finds the pinned copy first. Setting an environment
        variable in-process is inherently process-scoped; the user's global
        PowerShell environment is left untouched. Idempotent.
    #>
    param([Parameter(Mandatory = $true)][string] $CacheRoot)

    $fullCacheRoot = [System.IO.Path]::GetFullPath($CacheRoot).TrimEnd('\')
    $entries = @($env:PSModulePath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($entry in $entries) {
        if ([System.IO.Path]::GetFullPath($entry).TrimEnd('\') -ieq $fullCacheRoot) {
            return
        }
    }
    $env:PSModulePath = $fullCacheRoot + ';' + $env:PSModulePath
}

function Restore-PinnedPester {
    <#
    .SYNOPSIS
        Downloads the pinned Pester from PSGallery into the cache root and
        verifies the expected layout actually landed. -Force answers the
        untrusted-repository prompt without touching the machine's repository
        trust settings.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $CacheRoot,
        [Parameter(Mandatory = $true)][string] $RequiredVersion
    )

    $null = New-Item -ItemType Directory -Path $CacheRoot -Force
    Save-Module -Name Pester -RequiredVersion $RequiredVersion -Path $CacheRoot -Repository PSGallery -Force

    $modulePath = Get-PinnedPesterCachePath -CacheRoot $CacheRoot -RequiredVersion $RequiredVersion
    if (-not $modulePath) {
        throw "Save-Module completed but Pester $RequiredVersion was not found under '$CacheRoot'; the restore cannot be trusted."
    }
    return $modulePath
}

function Ensure-PinnedPester {
    <#
    .SYNOPSIS
        Returns the module directory of the pinned Pester, restoring it into
        the repository-local cache first when missing. The globally installed
        Pester (any version) is deliberately NOT consulted: depending on the
        local machine's state is exactly the failure this bootstrap removes.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $RequiredVersion,
        [Parameter(Mandatory = $true)][string] $CacheRoot
    )

    $modulePath = Get-PinnedPesterCachePath -CacheRoot $CacheRoot -RequiredVersion $RequiredVersion
    if ($modulePath) {
        Write-Host "Pester $RequiredVersion found in the repository-local cache ($modulePath)."
    }
    else {
        Write-Host "Pester $RequiredVersion is not in the repository-local cache ($CacheRoot); restoring from PSGallery..."
        $modulePath = Restore-PinnedPester -CacheRoot $CacheRoot -RequiredVersion $RequiredVersion
        Write-Host "Pester $RequiredVersion restored to $modulePath."
    }

    Add-PinnedPesterCacheToModulePath -CacheRoot $CacheRoot
    return $modulePath
}

# Script mode: run the ensure, import the pinned module by full path, and
# print what was loaded. Skipped when dot-sourced (InvocationName is '.').
if ($MyInvocation.InvocationName -ne '.') {
    $effectiveCacheRoot = $CacheRoot
    if ([string]::IsNullOrWhiteSpace($effectiveCacheRoot)) {
        $effectiveCacheRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.psmodules'
    }

    $pesterModulePath = Ensure-PinnedPester -RequiredVersion $RequiredVersion -CacheRoot $effectiveCacheRoot
    Import-Module (Join-Path $pesterModulePath 'Pester.psd1') -Force
    $loaded = Get-Module Pester
    Write-Host "Loaded Pester $($loaded.Version) from $($loaded.ModuleBase)"
    exit 0
}
