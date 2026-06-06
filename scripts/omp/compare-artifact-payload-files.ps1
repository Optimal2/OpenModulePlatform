<#
.SYNOPSIS
Compares payload files inside two OMP artifact package zips.

.DESCRIPTION
Extracts each artifact package, expands payload/artifact.zip, and reports file
paths that are missing or have different SHA-256 hashes. This is a diagnostic
companion to compare-universal-package-data.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FirstArtifactPackage,

    [Parameter(Mandatory = $true)]
    [string]$SecondArtifactPackage,

    [int]$First = 100
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

function Expand-ArtifactPayload {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $artifactRoot = Join-Path $Destination 'artifact'
    $payloadRoot = Join-Path $Destination 'payload'
    New-Item -ItemType Directory -Path $artifactRoot, $payloadRoot -Force | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $artifactRoot)

    $payloadZip = Join-Path $artifactRoot 'payload\artifact.zip'
    if (-not (Test-Path -LiteralPath $payloadZip -PathType Leaf)) {
        throw "Artifact payload was not found: $payloadZip"
    }

    [System.IO.Compression.ZipFile]::ExtractToDirectory($payloadZip, $payloadRoot)
    return $payloadRoot
}

function Get-PayloadMap {
    param([Parameter(Mandatory = $true)][string]$Root)

    $map = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        $relativePath = Get-RelativePath -BasePath $Root -Path $file.FullName
        $map[$relativePath] = [pscustomobject]@{
            Path = $relativePath
            Length = $file.Length
            Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }

    return $map
}

$firstPath = Resolve-FullPath -Path $FirstArtifactPackage
$secondPath = Resolve-FullPath -Path $SecondArtifactPackage
if (-not (Test-Path -LiteralPath $firstPath -PathType Leaf)) {
    throw "FirstArtifactPackage does not exist: $firstPath"
}

if (-not (Test-Path -LiteralPath $secondPath -PathType Leaf)) {
    throw "SecondArtifactPackage does not exist: $secondPath"
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-artifact-payload-diff-' + [Guid]::NewGuid().ToString('N'))
try {
    $firstPayloadRoot = Expand-ArtifactPayload -PackagePath $firstPath -Destination (Join-Path $workRoot 'first')
    $secondPayloadRoot = Expand-ArtifactPayload -PackagePath $secondPath -Destination (Join-Path $workRoot 'second')
    $firstMap = Get-PayloadMap -Root $firstPayloadRoot
    $secondMap = Get-PayloadMap -Root $secondPayloadRoot

    $differences = [System.Collections.Generic.List[object]]::new()
    foreach ($path in @($firstMap.Keys | Sort-Object)) {
        if (-not $secondMap.ContainsKey($path)) {
            $differences.Add([pscustomobject]@{
                Difference = 'Only in first'
                Path = $path
                FirstLength = $firstMap[$path].Length
                SecondLength = ''
            })
            continue
        }

        if (-not $firstMap[$path].Hash.Equals($secondMap[$path].Hash, [StringComparison]::OrdinalIgnoreCase)) {
            $differences.Add([pscustomobject]@{
                Difference = 'Different'
                Path = $path
                FirstLength = $firstMap[$path].Length
                SecondLength = $secondMap[$path].Length
            })
        }
    }

    foreach ($path in @($secondMap.Keys | Sort-Object)) {
        if (-not $firstMap.ContainsKey($path)) {
            $differences.Add([pscustomobject]@{
                Difference = 'Only in second'
                Path = $path
                FirstLength = ''
                SecondLength = $secondMap[$path].Length
            })
        }
    }

    if ($differences.Count -eq 0) {
        Write-Host 'Artifact payload files are identical.'
        return
    }

    $differences |
        Sort-Object Path, Difference |
        Select-Object -First $First |
        Format-Table Difference, Path, FirstLength, SecondLength -AutoSize

    if ($differences.Count -gt $First) {
        Write-Host "Displayed $First of $($differences.Count) difference(s)."
    }

    exit 1
}
finally {
    if (Test-Path -LiteralPath $workRoot -PathType Container) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
