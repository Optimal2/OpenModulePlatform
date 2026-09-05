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
    $manifest = Join-Path $modulePath 'Pester.psd1'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        return $null
    }
    # The directory name is a claim; the manifest is the fact. A hand-copied or
    # half-restored folder named 5.9.1 that carries another version would otherwise
    # be served as the pin (independent review, 2026-09-05).
    $declared = Get-PesterManifestVersion -ManifestPath $manifest
    if ($declared -ne $RequiredVersion) {
        throw "The repository-local cache holds a Pester manifest declaring version '$declared' under the folder for $RequiredVersion ($modulePath); refusing to serve it. Delete the folder and rerun."
    }
    return $modulePath
}

function Get-PesterManifestVersion {
    <#
    .SYNOPSIS
        Reads ModuleVersion out of a module manifest without importing the module.
    #>
    param([Parameter(Mandatory = $true)][string] $ManifestPath)

    # Not Import-PowerShellDataFile: under Windows PowerShell 5.1 with PowerShell 7 module
    # folders on the path, Microsoft.PowerShell.Utility can resolve to the 7.x copy and the
    # cmdlet is then missing (observed 2026-09-05). The restricted-language check below is
    # exactly what that cmdlet does: data only, no commands, no variables.
    $content = Get-Content -LiteralPath $ManifestPath -Raw
    $block = [scriptblock]::Create($content)
    $block.CheckRestrictedLanguage([string[]] @(), [string[]] @(), $false)
    $data = & $block
    return [string] $data.ModuleVersion
}

function Get-WindowsPowerShellSafeModulePath {
    <#
    .SYNOPSIS
        Under Windows PowerShell 5.1 a PSModulePath that also lists PowerShell 7
        module folders (a common leftover of a user-scoped install) makes module
        auto-resolution pick PowerShell 7's PowerShellGet, whose PackageManagement
        assembly cannot load in 5.1 -- and Save-Module then fails with an opaque
        'module could not be loaded'. Returns the current path with those folders
        dropped; the caller applies it only for the duration of the restore.
    #>
    $entries = @($env:PSModulePath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    # Wildcards, not regex: a regex here needs escaped backslashes, and one copy of this file
    # reached CI with them stripped ("Malformed \p{X} character escape", 2026-09-05). -like is
    # case-insensitive in PowerShell and has nothing to escape.
    $kept = @($entries | Where-Object {
        $e = $_.TrimEnd([char]92)
        -not (($e -like '*\PowerShell\7') -or ($e -like '*\PowerShell\7\*') -or ($e -like '*\Program Files\PowerShell\Modules') -or ($e -like '*\Documents\PowerShell\Modules'))
    })
    return ($kept -join ';')
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

    # Local source first: a globally installed copy of EXACTLY the pinned version is
    # the same bytes PSGallery would hand back, and copying it into the cache keeps
    # every property the cache exists for (import by full path, nothing global
    # consulted at import time) without a network round-trip. The version is taken
    # from its manifest, never from the folder name. (Independent review, 2026-09-05:
    # on a developer box with 5.9.1 installed globally the old runner worked and the
    # gallery-only restore regressed it.)
    $global = @(Get-Module -ListAvailable -Name Pester | Where-Object {
        $_.Version.ToString() -eq $RequiredVersion -and (Test-Path -LiteralPath (Join-Path $_.ModuleBase 'Pester.psd1') -PathType Leaf)
    }) | Select-Object -First 1
    if ($global -and (Get-PesterManifestVersion -ManifestPath (Join-Path $global.ModuleBase 'Pester.psd1')) -eq $RequiredVersion) {
        $target = Join-Path (Join-Path $CacheRoot 'Pester') $RequiredVersion
        Write-Host "Copying the globally installed Pester $RequiredVersion ($($global.ModuleBase)) into the repository-local cache."
        $null = New-Item -ItemType Directory -Path $target -Force
        # -LiteralPath would take the '*' literally and copy nothing; enumerate the folder instead.
        Get-ChildItem -LiteralPath $global.ModuleBase -Force | Copy-Item -Destination $target -Recurse -Force
    }
    else {
        $originalModulePath = $env:PSModulePath
        try {
            if ($PSVersionTable.PSVersion.Major -le 5) {
                $env:PSModulePath = Get-WindowsPowerShellSafeModulePath
            }
            try {
                Save-Module -Name Pester -RequiredVersion $RequiredVersion -Path $CacheRoot -Repository PSGallery -Force
            }
            catch {
                throw "Could not restore Pester $RequiredVersion from PSGallery into '$CacheRoot': $($_.Exception.Message). No globally installed Pester $RequiredVersion was available to copy either. Check network/proxy access to PSGallery, or install the exact version once (Install-Module Pester -RequiredVersion $RequiredVersion -Scope CurrentUser) so the cache can be seeded from it."
            }
        }
        finally {
            $env:PSModulePath = $originalModulePath
        }
    }

    $modulePath = Get-PinnedPesterCachePath -CacheRoot $CacheRoot -RequiredVersion $RequiredVersion
    if (-not $modulePath) {
        throw "The restore completed but Pester $RequiredVersion was not found under '$CacheRoot'; the restore cannot be trusted."
    }
    return $modulePath
}

function Ensure-PinnedPester {
    <#
    .SYNOPSIS
        Returns the module directory of the pinned Pester, restoring it into
        the repository-local cache first when missing. The globally installed
        Pester is deliberately NOT consulted at import time: depending on the
        local machine's state is exactly the failure this bootstrap removes. (A
        global copy of the EXACT version may seed the cache, see Restore-PinnedPester;
        what is imported is always the cache, by full path, with its manifest checked.)
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
