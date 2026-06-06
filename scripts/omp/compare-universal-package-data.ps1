<#
.SYNOPSIS
Compares two universal OMP packages or object roots at portable-object data level.

.DESCRIPTION
The outer universal zip file contains timestamps and package metadata that may
legitimately differ between generators. This script compares the import-relevant
portable object data instead:

- package item paths
- normalized JSON object files
- artifact package payload content after extracting payload/artifact.zip
- artifact package min module-definition version and configuration files

Use this to verify that repository export, installer object roots, installer
export, and browser-built universal packages contain the same OMP objects even
when the zip file bytes themselves differ.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FirstPackage,

    [Parameter(Mandatory = $true)]
    [string]$SecondPackage,

    [switch]$CommonOnly,

    [switch]$KeepExtracted
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function New-TempDirectory {
    param([Parameter(Mandatory = $true)][string]$Prefix)

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ($Prefix + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
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

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Get-JsonSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $json = $raw | ConvertFrom-Json
    $normalized = $json | ConvertTo-Json -Depth 100 -Compress
    return Get-TextSha256 -Text $normalized
}

function Get-ObjectPropertyValue {
    param(
        [object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties |
        Where-Object { $_.Name.Equals($Name, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-DirectorySignature {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return 'missing-directory'
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName) {
        $relativePath = Get-RelativePath -BasePath $Root -Path $file.FullName
        $lines.Add(($relativePath + "`t" + $file.Length + "`t" + (Get-FileSha256 -Path $file.FullName)))
    }

    return Get-TextSha256 -Text ($lines -join "`n")
}

function Expand-ZipFile {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $Destination)
}

function Get-ArtifactPackageSignature {
    param([Parameter(Mandatory = $true)][string]$ArtifactPackagePath)

    $root = New-TempDirectory -Prefix 'omp-artifact-compare-'
    try {
        Expand-ZipFile -ZipPath $ArtifactPackagePath -Destination $root
        $manifestPath = Join-Path $root 'omp-artifact-package.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            return 'legacy:' + (Get-DirectorySignature -Root $root)
        }

        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $payload = Get-ObjectPropertyValue -Object $manifest -Name 'payload'
        $payloadPath = [string](Get-ObjectPropertyValue -Object $payload -Name 'path')
        $payloadType = [string](Get-ObjectPropertyValue -Object $payload -Name 'type')
        if ([string]::IsNullOrWhiteSpace($payloadPath)) {
            throw "Artifact package manifest is missing payload.path: $ArtifactPackagePath"
        }

        $contentRoot = New-TempDirectory -Prefix 'omp-artifact-content-'
        try {
            $payloadFullPath = Join-Path $root ($payloadPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            if ($payloadType.Equals('zip', [StringComparison]::OrdinalIgnoreCase) -or $payloadPath.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
                Expand-ZipFile -ZipPath $payloadFullPath -Destination $contentRoot
            }
            else {
                Copy-Item -LiteralPath $payloadFullPath -Destination $contentRoot -Recurse
            }

            $moduleDefinition = Get-ObjectPropertyValue -Object $manifest -Name 'moduleDefinition'
            $minimum = [string](Get-ObjectPropertyValue -Object $moduleDefinition -Name 'minVersion')
            if ([string]::IsNullOrWhiteSpace($minimum)) {
                $minimum = [string](Get-ObjectPropertyValue -Object $manifest -Name 'minModuleDefinitionVersion')
            }

            $configurationLines = [System.Collections.Generic.List[string]]::new()
            foreach ($configurationFile in @((Get-ObjectPropertyValue -Object $manifest -Name 'configurationFiles'))) {
                if ($null -eq $configurationFile) {
                    continue
                }

                $relativePath = [string](Get-ObjectPropertyValue -Object $configurationFile -Name 'relativePath')
                $source = [string](Get-ObjectPropertyValue -Object $configurationFile -Name 'source')
                if ([string]::IsNullOrWhiteSpace($source)) {
                    $source = [string](Get-ObjectPropertyValue -Object $configurationFile -Name 'path')
                }

                $sourcePath = Join-Path $root ($source.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                $configurationLines.Add(($relativePath + "`t" + (Get-FileSha256 -Path $sourcePath)))
            }

            return 'manifest:' `
                + 'payload=' + (Get-DirectorySignature -Root $contentRoot) `
                + ';minimum=' + $minimum `
                + ';configuration=' + (Get-TextSha256 -Text (($configurationLines | Sort-Object) -join "`n"))
        }
        finally {
            if (-not $KeepExtracted) {
                Remove-Item -LiteralPath $contentRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    finally {
        if (-not $KeepExtracted) {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-PackageObjectSignatures {
    param([Parameter(Mandatory = $true)][string]$PackagePath)

    $root = New-TempDirectory -Prefix 'omp-universal-compare-'
    Expand-ZipFile -ZipPath $PackagePath -Destination $root

    $signatures = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName) {
        $relativePath = Get-RelativePath -BasePath $root -Path $file.FullName
        if ($relativePath.Equals('omp-universal-package.json', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if ($relativePath.StartsWith('artifacts/', [StringComparison]::OrdinalIgnoreCase) -and $relativePath.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
            $signatures[$relativePath] = Get-ArtifactPackageSignature -ArtifactPackagePath $file.FullName
            continue
        }

        if ($relativePath.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
            $signatures[$relativePath] = 'json:' + (Get-JsonSha256 -Path $file.FullName)
            continue
        }

        $signatures[$relativePath] = 'file:' + (Get-FileSha256 -Path $file.FullName)
    }

    return [pscustomobject]@{
        OwnsRoot = $true
        Root = $root
        Signatures = $signatures
    }
}

function Get-ObjectRootSignatures {
    param([Parameter(Mandatory = $true)][string]$Root)

    $signatures = [ordered]@{}
    $folders = @(
        @{ Folder = 'module-definitions'; Pattern = '*.json' },
        @{ Folder = 'artifacts'; Pattern = '*.zip' },
        @{ Folder = 'host-configs'; Pattern = '*.json' },
        @{ Folder = 'host-configs'; Pattern = '*.zip' },
        @{ Folder = 'config-overlays'; Pattern = '*.json' },
        @{ Folder = 'config-overlays'; Pattern = '*.zip' },
        @{ Folder = 'widgets'; Pattern = '*.json' },
        @{ Folder = 'widget-data'; Pattern = '*.zip' }
    )

    foreach ($folder in $folders) {
        $folderPath = Join-Path $Root $folder.Folder
        if (-not (Test-Path -LiteralPath $folderPath -PathType Container)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $folderPath -Filter $folder.Pattern -File -Recurse | Sort-Object FullName) {
            $relativePath = Get-RelativePath -BasePath $Root -Path $file.FullName
            if ($relativePath.StartsWith('artifacts/', [StringComparison]::OrdinalIgnoreCase) -and $relativePath.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
                $signatures[$relativePath] = Get-ArtifactPackageSignature -ArtifactPackagePath $file.FullName
                continue
            }

            if ($relativePath.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
                $signatures[$relativePath] = 'json:' + (Get-JsonSha256 -Path $file.FullName)
                continue
            }

            $signatures[$relativePath] = 'file:' + (Get-FileSha256 -Path $file.FullName)
        }
    }

    return [pscustomobject]@{
        OwnsRoot = $false
        Root = $Root
        Signatures = $signatures
    }
}

function Get-PortableObjectSignatures {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path -PathType Container) {
        return Get-ObjectRootSignatures -Root $Path
    }

    return Get-PackageObjectSignatures -PackagePath $Path
}

$firstPath = Resolve-FullPath -Path $FirstPackage
$secondPath = Resolve-FullPath -Path $SecondPackage
if (-not (Test-Path -LiteralPath $firstPath)) {
    throw "First package or object root was not found: $firstPath"
}

if (-not (Test-Path -LiteralPath $secondPath)) {
    throw "Second package or object root was not found: $secondPath"
}

$first = Get-PortableObjectSignatures -Path $firstPath
$second = Get-PortableObjectSignatures -Path $secondPath

try {
    $allPaths = @($first.Signatures.Keys + $second.Signatures.Keys) |
        Sort-Object -Unique
    $differences = [System.Collections.Generic.List[object]]::new()
    foreach ($path in $allPaths) {
        $firstHasPath = $first.Signatures.Contains($path)
        $secondHasPath = $second.Signatures.Contains($path)
        if (-not $firstHasPath -or -not $secondHasPath) {
            if ($CommonOnly) {
                continue
            }

            $differences.Add([pscustomobject]@{
                Path = $path
                Difference = if ($firstHasPath) { 'Only in first package' } else { 'Only in second package' }
            })
            continue
        }

        if (-not [string]::Equals([string]$first.Signatures[$path], [string]$second.Signatures[$path], [StringComparison]::Ordinal)) {
            $differences.Add([pscustomobject]@{
                Path = $path
                Difference = 'Different object data'
            })
        }
    }

    if ($differences.Count -eq 0) {
        Write-Host 'Universal package object data is identical.'
        exit 0
    }

    Write-Host 'Universal package object data differs:'
    $differences | Select-Object Difference, Path | Format-Table -Wrap
    exit 1
}
finally {
    if (-not $KeepExtracted -and $first.OwnsRoot) {
        Remove-Item -LiteralPath $first.Root -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not $KeepExtracted -and $second.OwnsRoot) {
        Remove-Item -LiteralPath $second.Root -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($KeepExtracted) {
        Write-Host "First extracted root:  $($first.Root)"
        Write-Host "Second extracted root: $($second.Root)"
    }
}
