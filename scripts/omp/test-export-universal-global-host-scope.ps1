<#
.SYNOPSIS
Smoke test: global universal packages must be host-agnostic.

.DESCRIPTION
Reproduces the operator-flagged 2026-07-24 incident: an object root (and a
repository export) that contains host-specific host-configs/ and
config-overlays/ objects must never leak them into a GLOBAL universal package
(a package without a target host profile), because such a package is copied to
Universal/installer/exports for manual customer (VGR) upload and must not be
able to touch any specific host's configuration on import.

Asserts for both global build paths:

  * scripts/omp/export-universal-object-root.ps1 (the dashboard one-click
    "build from object root" flow, run with -LatestOnly):
      - global export contains ZERO host-configs/ and ZERO config-overlays/
        entries, even when the object root holds host overlays;
      - a -TargetHostProfile export still includes the host objects.
  * scripts/omp/export-universal-package.ps1 (the build-universal-package.ps1
    repository flow):
      - global export contains ZERO host-configs/ and ZERO config-overlays/
        entries;
      - passing -ConfigOverlayFile without a target host profile fails fast;
      - a -TargetHostProfile export with -ConfigOverlayFile includes the
        overlay.

Exits with code 0 on success and throws on the first failed assertion.
Windows PowerShell 5.1 compatible.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$objectRootExportScript = Join-Path $PSScriptRoot 'export-universal-object-root.ps1'
$repositoryExportScript = Join-Path $PSScriptRoot 'export-universal-package.ps1'
$ompRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-export-global-host-scope-' + [Guid]::NewGuid().ToString('N'))

function Write-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Write-FakeArtifactZip {
    # Both exporters validate artifact zips with the runtime-configuration
    # guard, so test artifacts must be real zip files.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$PayloadText
    )

    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-fake-artifact-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $staging 'content.txt'), $PayloadText, [System.Text.UTF8Encoding]::new($false))
        New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
        Compress-Archive -Path (Join-Path $staging 'content.txt') -DestinationPath $Path -Force
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Read-UniversalPackageEntryNames {
    param([Parameter(Mandatory = $true)][string]$PackagePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        return @($archive.Entries | ForEach-Object { [string]$_.FullName } | Sort-Object)
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-NoHostSpecificEntries {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][object[]]$Entries
    )

    $hostEntries = @($Entries | Where-Object {
            $_.StartsWith('host-configs/', [StringComparison]::OrdinalIgnoreCase) -or
            $_.StartsWith('config-overlays/', [StringComparison]::OrdinalIgnoreCase)
        })
    if ($hostEntries.Count -gt 0) {
        throw "$Label failed. Package contains $($hostEntries.Count) host-specific entrie(s): $($hostEntries -join ', ')"
    }

    Write-Host "PASS: $Label (0 host-configs/ and 0 config-overlays/ entries)"
}

function Assert-ContainsEntry {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][object[]]$Entries,
        [Parameter(Mandatory = $true)][string]$Prefix
    )

    $matches = @($Entries | Where-Object { $_.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -eq 0) {
        throw "$Label failed. Expected at least one '$Prefix' entry. Actual entries: $($Entries -join ', ')"
    }

    Write-Host "PASS: $Label ($($matches.Count) '$Prefix' entrie(s))"
}

try {
    # ---------------------------------------------------------------
    # Part 1: object-root export (the dashboard one-click button flow)
    # ---------------------------------------------------------------
    $objectRoot = Join-Path $testRoot 'data\global'
    Write-TextFile -Path (Join-Path $objectRoot 'module-definitions\omp_core.module-definition.json') -Text '{ "definitionVersion": "1.0.0" }'
    Write-FakeArtifactZip -Path (Join-Path $objectRoot 'artifacts\omp-hostagent__omp-hostagent__artifact__omp-service__0.3.160.zip') -PayloadText 'artifact payload'
    Write-TextFile -Path (Join-Path $objectRoot 'widgets\my-widget__1.2.0.json') -Text '{ "packageVersion": "1.2.0" }'

    # The proven 2026-07-24 problem: host-specific overlays (always hostKey-bound)
    # sitting in the GLOBAL object root.
    Write-TextFile -Path (Join-Path $objectRoot 'config-overlays\local-vajskrivare-appsettings.json') -Text '{ "hostKey": "localhost", "overlayVersion": "1.0.0" }'
    Write-TextFile -Path (Join-Path $objectRoot 'config-overlays\vgr-test-vajskrivare-appsettings.json') -Text '{ "hostKey": "VGMS1850.vgregion.se", "overlayVersion": "1.0.0" }'
    Write-TextFile -Path (Join-Path $objectRoot 'host-configs\vgr-test-host.json') -Text '{ "hostKey": "VGMS1850.vgregion.se", "configurationVersion": "1.0.0" }'

    $globalZip = Join-Path $testRoot 'global.zip'
    & $objectRootExportScript -ObjectRoot $objectRoot -OutputPath $globalZip -LatestOnly | Out-Null
    $globalEntries = Read-UniversalPackageEntryNames -PackagePath $globalZip
    Assert-NoHostSpecificEntries -Label 'object-root global export (-LatestOnly) excludes host objects' -Entries $globalEntries
    Assert-ContainsEntry -Label 'object-root global export keeps module definitions' -Entries $globalEntries -Prefix 'module-definitions/'
    Assert-ContainsEntry -Label 'object-root global export keeps artifacts' -Entries $globalEntries -Prefix 'artifacts/'
    Assert-ContainsEntry -Label 'object-root global export keeps global widgets' -Entries $globalEntries -Prefix 'widgets/'

    $hostZip = Join-Path $testRoot 'host.zip'
    & $objectRootExportScript -ObjectRoot $objectRoot -OutputPath $hostZip -LatestOnly -TargetHostProfile 'vgr-test' | Out-Null
    $hostEntries = Read-UniversalPackageEntryNames -PackagePath $hostZip
    Assert-ContainsEntry -Label 'object-root host-targeted export keeps config overlays' -Entries $hostEntries -Prefix 'config-overlays/'
    Assert-ContainsEntry -Label 'object-root host-targeted export keeps host configs' -Entries $hostEntries -Prefix 'host-configs/'

    # ---------------------------------------------------------------
    # Part 2: repository export (the build-universal-package.ps1 flow)
    # ---------------------------------------------------------------
    $fakeRepo = Join-Path $testRoot 'fake-repo'
    Write-TextFile -Path (Join-Path $fakeRepo 'omp-components.json') -Text @'
{
  "manifestVersion": 1,
  "repositoryKey": "omp-global-host-scope-test",
  "repositoryVersion": "1.0.0",
  "moduleDefinitions": [
    { "moduleKey": "demo_module", "path": "demo.module-definition.json" }
  ],
  "components": []
}
'@
    Write-TextFile -Path (Join-Path $fakeRepo 'demo.module-definition.json') -Text '{ "moduleKey": "demo_module", "definitionVersion": "1.0.0" }'
    $overlaySource = Join-Path $testRoot 'vgr-test-overlay.json'
    Write-TextFile -Path $overlaySource -Text '{ "hostKey": "VGMS1850.vgregion.se", "overlayVersion": "1.0.0" }'

    $repoGlobalZip = Join-Path $testRoot 'repo-global.zip'
    & $repositoryExportScript `
        -RepositoryRoot $fakeRepo `
        -OmpRepositoryRoot $ompRepositoryRoot `
        -OutputPath $repoGlobalZip | Out-Null
    $repoGlobalEntries = Read-UniversalPackageEntryNames -PackagePath $repoGlobalZip
    Assert-NoHostSpecificEntries -Label 'repository global export excludes host objects' -Entries $repoGlobalEntries
    Assert-ContainsEntry -Label 'repository global export keeps module definitions' -Entries $repoGlobalEntries -Prefix 'module-definitions/'

    # Global mode + explicit host inputs must fail fast, not silently drop them.
    $failedAsExpected = $false
    try {
        & $repositoryExportScript `
            -RepositoryRoot $fakeRepo `
            -OmpRepositoryRoot $ompRepositoryRoot `
            -OutputPath (Join-Path $testRoot 'repo-global-invalid.zip') `
            -ConfigOverlayFile $overlaySource | Out-Null
    }
    catch {
        $failedAsExpected = $true
        if (-not $_.Exception.Message.Contains('host-specific')) {
            throw "repository global export with explicit overlay failed, but with an unexpected message: $($_.Exception.Message)"
        }
    }

    if (-not $failedAsExpected) {
        throw 'repository global export with explicit -ConfigOverlayFile did NOT fail; host overlays could silently enter a global package.'
    }

    Write-Host 'PASS: repository global export with explicit -ConfigOverlayFile fails fast'

    $repoHostZip = Join-Path $testRoot 'repo-host.zip'
    & $repositoryExportScript `
        -RepositoryRoot $fakeRepo `
        -OmpRepositoryRoot $ompRepositoryRoot `
        -OutputPath $repoHostZip `
        -TargetHostProfile 'vgr-test' `
        -ConfigOverlayFile $overlaySource | Out-Null
    $repoHostEntries = Read-UniversalPackageEntryNames -PackagePath $repoHostZip
    Assert-ContainsEntry -Label 'repository host-targeted export keeps config overlays' -Entries $repoHostEntries -Prefix 'config-overlays/'

    Write-Host 'All global/host-scope smoke assertions passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
