<#
.SYNOPSIS
Merges portable OMP objects from multiple universal packages into one object root.

.DESCRIPTION
This helper is intended for validation. It extracts the import-relevant folders
from one or more universal package zip files into a single directory, preserving
the standard universal package object shape:

  module-definitions/
  artifacts/
  host-configs/
  config-overlays/
  widgets/
  widget-data/

If two packages contain the same object path with different bytes, the script
fails instead of silently choosing one.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [string]$PackageFilter = '*-global-*-universal.zip'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath)
    if (-not $baseFullPath.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString(), [StringComparison]::Ordinal)) {
        $baseFullPath += [System.IO.Path]::DirectorySeparatorChar
    }

    $targetFullPath = [System.IO.Path]::GetFullPath($Path)
    $baseUri = [Uri]::new($baseFullPath)
    $targetUri = [Uri]::new($targetFullPath)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('\', '/')
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$packageRootPath = Resolve-FullPath -Path $PackageRoot
if (-not (Test-Path -LiteralPath $packageRootPath -PathType Container)) {
    throw "PackageRoot does not exist: $packageRootPath"
}

$outputRootPath = Resolve-FullPath -Path $OutputRoot
if (Test-Path -LiteralPath $outputRootPath -PathType Container) {
    Remove-Item -LiteralPath $outputRootPath -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

$objectFolders = @(
    'module-definitions',
    'artifacts',
    'host-configs',
    'config-overlays',
    'widgets',
    'widget-data'
)

$packages = Get-ChildItem -LiteralPath $packageRootPath -Filter $PackageFilter -File |
    Sort-Object Name

if ($packages.Count -eq 0) {
    throw "No packages matched '$PackageFilter' below $packageRootPath."
}

$copied = 0
foreach ($package in $packages) {
    $extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-universal-merge-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($package.FullName, $extractRoot)

        foreach ($folder in $objectFolders) {
            $sourceFolder = Join-Path $extractRoot $folder
            if (-not (Test-Path -LiteralPath $sourceFolder -PathType Container)) {
                continue
            }

            foreach ($file in Get-ChildItem -LiteralPath $sourceFolder -File -Recurse) {
                $relativePath = Get-RelativePath -BasePath $extractRoot -Path $file.FullName
                $destination = Join-Path $outputRootPath $relativePath
                $destinationDirectory = Split-Path -Parent $destination
                New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

                if (Test-Path -LiteralPath $destination -PathType Leaf) {
                    $sourceHash = Get-FileSha256 -Path $file.FullName
                    $destinationHash = Get-FileSha256 -Path $destination
                    if (-not $sourceHash.Equals($destinationHash, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Conflicting object path while merging universal packages: $relativePath"
                    }

                    continue
                }

                Copy-Item -LiteralPath $file.FullName -Destination $destination
                $copied++
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $extractRoot -PathType Container) {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }
    }
}

Write-Host "Merged $($packages.Count) universal package(s)."
Write-Host "Copied $copied object file(s)."
Write-Host "Output root: $outputRootPath"
