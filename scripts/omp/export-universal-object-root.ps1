<#
.SYNOPSIS
Creates a universal OMP package zip from an existing portable object root.

.DESCRIPTION
This is a command-line companion to the installer/Portal export flow. It expects
an object root with the standard folders:

  module-definitions/
  artifacts/
  host-configs/
  config-overlays/
  widgets/
  widget-data/

The generated zip contains the same portable object paths plus
omp-universal-package.json. The outer zip metadata is transport-only; validate
import-relevant data with compare-universal-package-data.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ObjectRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$PackageKey = 'omp-universal',

    [string]$PackageVersion = '',

    [string]$DisplayName = '',

    [string]$Description = '',

    [string]$TargetHostProfile = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
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

function Add-ZipEntryFromText {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$EntryName,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )

    $entry = $Archive.CreateEntry($EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    try {
        $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
        try {
            $writer.Write($Text)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $PackageVersion = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmm')
}

if ([string]::IsNullOrWhiteSpace($DisplayName)) {
    $DisplayName = $PackageKey
}

$objectRootPath = Resolve-FullPath -Path $ObjectRoot
if (-not (Test-Path -LiteralPath $objectRootPath -PathType Container)) {
    throw "ObjectRoot does not exist: $objectRootPath"
}

$outputPathFull = Resolve-FullPath -Path $OutputPath
New-Item -ItemType Directory -Path (Split-Path -Parent $outputPathFull) -Force | Out-Null
if (Test-Path -LiteralPath $outputPathFull -PathType Leaf) {
    Remove-Item -LiteralPath $outputPathFull -Force
}

$folderKinds = @(
    @{ Folder = 'module-definitions'; Kind = 'module-definition'; Patterns = @('*.json') },
    @{ Folder = 'artifacts'; Kind = 'artifact'; Patterns = @('*.zip') },
    @{ Folder = 'host-configs'; Kind = 'host-config'; Patterns = @('*.json', '*.zip') },
    @{ Folder = 'config-overlays'; Kind = 'config-overlay'; Patterns = @('*.json', '*.zip') },
    @{ Folder = 'widgets'; Kind = 'dashboard-widget'; Patterns = @('*.json') },
    @{ Folder = 'widget-data'; Kind = 'widget-data'; Patterns = @('*.zip') }
)

$files = foreach ($folder in $folderKinds) {
    $folderPath = Join-Path $objectRootPath $folder.Folder
    if (-not (Test-Path -LiteralPath $folderPath -PathType Container)) {
        continue
    }

    foreach ($pattern in $folder.Patterns) {
        foreach ($file in Get-ChildItem -LiteralPath $folderPath -Filter $pattern -File -Recurse) {
            $relativePath = Get-RelativePath -BasePath $objectRootPath -Path $file.FullName
            [pscustomobject]@{
                FullName = $file.FullName
                PackagePath = $relativePath
                Kind = $folder.Kind
            }
        }
    }
}

$items = [System.Collections.Generic.List[object]]::new()
$archive = [System.IO.Compression.ZipFile]::Open($outputPathFull, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in @($files | Sort-Object PackagePath)) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $file.FullName,
            $file.PackagePath,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $items.Add([ordered]@{
            kind = $file.Kind
            path = $file.PackagePath
        })
    }

    $manifest = [ordered]@{
        formatVersion = 1
        objectType = 'universal-module-package'
        packageKey = $PackageKey
        packageVersion = $PackageVersion
        displayName = $DisplayName
        description = if ([string]::IsNullOrWhiteSpace($Description)) { $null } else { $Description }
        targetHostProfile = if ([string]::IsNullOrWhiteSpace($TargetHostProfile)) { $null } else { $TargetHostProfile }
        createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        items = $items
    }

    Add-ZipEntryFromText `
        -Archive $archive `
        -EntryName 'omp-universal-package.json' `
        -Text ($manifest | ConvertTo-Json -Depth 20)
}
finally {
    $archive.Dispose()
}

Write-Host "Universal package: $outputPathFull"
Write-Host "Package items: $($items.Count)"
