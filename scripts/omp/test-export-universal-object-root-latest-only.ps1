<#
.SYNOPSIS
Smoke test for export-universal-object-root.ps1 -LatestOnly.

.DESCRIPTION
Builds a temporary object root with a deliberate old duplicate artifact, a
latest artifact, duplicate widgets, and non-versioned objects. Runs the export
with and without -LatestOnly and asserts that:

  * with -LatestOnly the old duplicate artifact/widget versions are dropped
    while the latest versions and all other objects remain;
  * without -LatestOnly every version is kept (unchanged default behavior);
  * the default (global) export always excludes host-configs/ and
    config-overlays/, while a -TargetHostProfile export keeps them.

Exits with code 0 on success and throws on the first failed assertion.
Windows PowerShell 5.1 compatible.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$exportScript = Join-Path $PSScriptRoot 'export-universal-object-root.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-export-latest-only-' + [Guid]::NewGuid().ToString('N'))

function Write-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Write-FakeArtifactZip {
    # export-universal-object-root.ps1 validates artifact zips with the
    # runtime-configuration guard, so test artifacts must be real zip files.
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

function Read-UniversalPackageItemPaths {
    param([Parameter(Mandatory = $true)][string]$PackagePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $manifestEntry = $archive.Entries |
            Where-Object { $_.FullName -eq 'omp-universal-package.json' } |
            Select-Object -First 1
        if ($null -eq $manifestEntry) {
            throw "omp-universal-package.json missing in $PackagePath"
        }

        $stream = $manifestEntry.Open()
        try {
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
            try {
                $document = $reader.ReadToEnd() | ConvertFrom-Json
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }

        return @($document.items | ForEach-Object { [string]$_.path } | Sort-Object)
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ItemPaths {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][object[]]$Actual,
        [Parameter(Mandatory = $true)][object[]]$Expected
    )

    $actualSorted = @($Actual | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    $difference = Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actualSorted
    if ($null -ne $difference) {
        $expectedText = $expectedSorted -join ', '
        $actualText = $actualSorted -join ', '
        throw "$Label failed. Expected: [$expectedText]. Actual: [$actualText]."
    }

    Write-Host "PASS: $Label ($($actualSorted.Count) items)"
}

try {
    $objectRoot = Join-Path $testRoot 'object-root'

    # Artifact duplicates: two identities x two versions (old + latest).
    Write-FakeArtifactZip -Path (Join-Path $objectRoot 'artifacts\odvgateway__odvgateway__artifact__omp-webapp__0.1.32.zip') -PayloadText 'old artifact payload'
    Write-FakeArtifactZip -Path (Join-Path $objectRoot 'artifacts\odvgateway__odvgateway__artifact__omp-webapp__0.1.34.zip') -PayloadText 'latest artifact payload'
    Write-FakeArtifactZip -Path (Join-Path $objectRoot 'artifacts\omp-hostagent__omp-hostagent__artifact__omp-service__0.3.153.zip') -PayloadText 'old artifact payload'
    Write-FakeArtifactZip -Path (Join-Path $objectRoot 'artifacts\omp-hostagent__omp-hostagent__artifact__omp-service__0.3.160.zip') -PayloadText 'latest artifact payload'

    # Widget duplicates: one identity x two versions, plus a second widget.
    Write-TextFile -Path (Join-Path $objectRoot 'widgets\my-widget__1.0.0.json') -Text '{ "packageVersion": "1.0.0" }'
    Write-TextFile -Path (Join-Path $objectRoot 'widgets\my-widget__1.2.0.json') -Text '{ "packageVersion": "1.2.0" }'
    Write-TextFile -Path (Join-Path $objectRoot 'widgets\other-widget__0.5.0.json') -Text '{ "packageVersion": "0.5.0" }'

    # Non-versioned object kinds: always kept in both modes.
    Write-TextFile -Path (Join-Path $objectRoot 'module-definitions\omp_core.module-definition.json') -Text '{ "definitionVersion": "1.0.0" }'
    Write-TextFile -Path (Join-Path $objectRoot 'host-configs\host1.json') -Text '{ "configurationVersion": "1.0.0" }'
    Write-TextFile -Path (Join-Path $objectRoot 'config-overlays\overlay1.json') -Text '{ "overlayVersion": "1.0.0" }'
    Write-TextFile -Path (Join-Path $objectRoot 'widget-data\data1.zip') -Text 'widget data payload'

    # Global exports (no -TargetHostProfile) must be host-agnostic: host-configs
    # and config-overlays are per-host by definition and never included.
    $globalNonVersionedPaths = @(
        'module-definitions/omp_core.module-definition.json',
        'widget-data/data1.zip'
    )
    $hostSpecificPaths = @(
        'host-configs/host1.json',
        'config-overlays/overlay1.json'
    )

    $allOutput = Join-Path $testRoot 'all.zip'
    & $exportScript -ObjectRoot $objectRoot -OutputPath $allOutput | Out-Null
    Assert-ItemPaths -Label 'default (global) export keeps all versions and excludes host objects' -Actual (Read-UniversalPackageItemPaths -PackagePath $allOutput) -Expected (@(
        'artifacts/odvgateway__odvgateway__artifact__omp-webapp__0.1.32.zip',
        'artifacts/odvgateway__odvgateway__artifact__omp-webapp__0.1.34.zip',
        'artifacts/omp-hostagent__omp-hostagent__artifact__omp-service__0.3.153.zip',
        'artifacts/omp-hostagent__omp-hostagent__artifact__omp-service__0.3.160.zip',
        'widgets/my-widget__1.0.0.json',
        'widgets/my-widget__1.2.0.json',
        'widgets/other-widget__0.5.0.json'
    ) + $globalNonVersionedPaths)

    $latestOutput = Join-Path $testRoot 'latest.zip'
    & $exportScript -ObjectRoot $objectRoot -OutputPath $latestOutput -LatestOnly | Out-Null
    Assert-ItemPaths -Label '-LatestOnly drops old duplicates and excludes host objects' -Actual (Read-UniversalPackageItemPaths -PackagePath $latestOutput) -Expected (@(
        'artifacts/odvgateway__odvgateway__artifact__omp-webapp__0.1.34.zip',
        'artifacts/omp-hostagent__omp-hostagent__artifact__omp-service__0.3.160.zip',
        'widgets/my-widget__1.2.0.json',
        'widgets/other-widget__0.5.0.json'
    ) + $globalNonVersionedPaths)

    $hostOutput = Join-Path $testRoot 'host.zip'
    & $exportScript -ObjectRoot $objectRoot -OutputPath $hostOutput -LatestOnly -TargetHostProfile 'host1' | Out-Null
    Assert-ItemPaths -Label 'host-targeted export keeps host objects' -Actual (Read-UniversalPackageItemPaths -PackagePath $hostOutput) -Expected (@(
        'artifacts/odvgateway__odvgateway__artifact__omp-webapp__0.1.34.zip',
        'artifacts/omp-hostagent__omp-hostagent__artifact__omp-service__0.3.160.zip',
        'widgets/my-widget__1.2.0.json',
        'widgets/other-widget__0.5.0.json'
    ) + $globalNonVersionedPaths + $hostSpecificPaths)

    Write-Host 'All -LatestOnly smoke assertions passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
