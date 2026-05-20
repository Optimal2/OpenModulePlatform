<#
.SYNOPSIS
Creates a standard OpenModulePlatform artifact package zip.

.DESCRIPTION
The generated zip uses the same manifest envelope that Portal upload,
HostAgent folder import, and HostAgent-first bootstrap packages consume.
The outer filename is:

  moduleKey__appKey__packageType__targetName__version.zip

Configuration files are supplied as relative-path/source-path pairs using:

  -ConfigurationFile 'odv.site.config.js=C:\config\odv.site.config.js'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModuleKey,
    [Parameter(Mandatory = $true)][string]$AppKey,
    [Parameter(Mandatory = $true)][string]$PackageType,
    [Parameter(Mandatory = $true)][string]$TargetName,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PayloadPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string[]]$ConfigurationFile = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$script:TokenPattern = '^[A-Za-z0-9][A-Za-z0-9._+-]*$'

function Test-MetadataToken {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match $script:TokenPattern
}

function Normalize-ZipPath {
    param([string]$Value)

    $normalized = $Value.Trim().Replace('\', '/').Trim('/')
    if ([string]::IsNullOrWhiteSpace($normalized) `
            -or $normalized.Contains(':') `
            -or $normalized.Contains([char]0)) {
        throw "Package paths must be relative and stay inside the package: $Value"
    }

    $invalid = [System.IO.Path]::GetInvalidFileNameChars()
    $segments = $normalized.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($segments.Count -eq 0) {
        throw "Package path is empty: $Value"
    }

    foreach ($segment in $segments) {
        if ($segment -eq '.' -or $segment -eq '..' -or $segment.IndexOfAny($invalid) -ge 0) {
            throw "Package paths must not contain invalid or parent directory segments: $Value"
        }
    }

    return ($segments -join '/')
}

function Get-SafeConfigurationSourceName {
    param(
        [string]$RelativePath,
        [int]$Index
    )

    $name = ($RelativePath.Split('/') | Select-Object -Last 1)
    if ([string]::IsNullOrWhiteSpace($name)) {
        $name = "config-$Index.txt"
    }

    $safeChars = foreach ($ch in $name.ToCharArray()) {
        if ([char]::IsLetterOrDigit($ch) -or $ch -eq '.' -or $ch -eq '_' -or $ch -eq '+' -or $ch -eq '-') {
            $ch
        }
        else {
            '-'
        }
    }

    $safe = -join $safeChars
    if ([string]::IsNullOrWhiteSpace($safe)) {
        $safe = "config-$Index.txt"
    }

    return ('configuration/{0:000}-{1}' -f $Index, $safe)
}

function Resolve-ConfigurationMapping {
    param(
        [string]$Mapping,
        [int]$Index
    )

    $separatorIndex = $Mapping.IndexOf('=')
    if ($separatorIndex -le 0 -or $separatorIndex -eq ($Mapping.Length - 1)) {
        throw "ConfigurationFile entries must use relative-path=source-path syntax: $Mapping"
    }

    $relativePath = Normalize-ZipPath -Value $Mapping.Substring(0, $separatorIndex)
    $sourcePath = [System.IO.Path]::GetFullPath($Mapping.Substring($separatorIndex + 1))
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Configuration source file was not found: $sourcePath"
    }

    return [ordered]@{
        RelativePath = $relativePath
        SourcePath = $sourcePath
        PackageSourcePath = Get-SafeConfigurationSourceName -RelativePath $relativePath -Index $Index
    }
}

function Compress-PayloadDirectory {
    param(
        [string]$SourceDirectory,
        [string]$DestinationZip
    )

    $items = @(Get-ChildItem -LiteralPath $SourceDirectory -Force)
    if ($items.Count -eq 0) {
        throw "Payload directory is empty: $SourceDirectory"
    }

    $parent = Split-Path -Parent $DestinationZip
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (Test-Path -LiteralPath $DestinationZip -PathType Leaf) {
        Remove-Item -LiteralPath $DestinationZip -Force
    }

    Compress-Archive -Path @($items | ForEach-Object { $_.FullName }) -DestinationPath $DestinationZip -Force
}

foreach ($token in @($ModuleKey, $AppKey, $PackageType, $TargetName, $Version)) {
    if (-not (Test-MetadataToken -Value $token)) {
        throw "Artifact identity tokens must match $script:TokenPattern. Invalid token: $token"
    }
}

$payloadFullPath = [System.IO.Path]::GetFullPath($PayloadPath)
if (-not (Test-Path -LiteralPath $payloadFullPath)) {
    throw "Payload path was not found: $payloadFullPath"
}

$artifactFileName = "$ModuleKey`__$AppKey`__$PackageType`__$TargetName`__$Version.zip"
$resolvedOutputPath = if ($OutputPath.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
    [System.IO.Path]::GetFullPath($OutputPath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $OutputPath $artifactFileName))
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-artifact-package-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $tempRoot 'package'
$payloadZip = Join-Path $tempRoot 'artifact-payload.zip'

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

    if (Test-Path -LiteralPath $payloadFullPath -PathType Container) {
        Compress-PayloadDirectory -SourceDirectory $payloadFullPath -DestinationZip $payloadZip
    }
    elseif ($payloadFullPath.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $payloadFullPath -Destination $payloadZip -Force
    }
    else {
        throw "PayloadPath must be a directory or a .zip file: $payloadFullPath"
    }

    $payloadDestination = Join-Path $packageRoot 'payload\artifact.zip'
    New-Item -ItemType Directory -Path (Split-Path -Parent $payloadDestination) -Force | Out-Null
    Copy-Item -LiteralPath $payloadZip -Destination $payloadDestination -Force

    $configurationItems = [System.Collections.Generic.List[object]]::new()
    $index = 1
    foreach ($mapping in $ConfigurationFile) {
        $configurationItems.Add((Resolve-ConfigurationMapping -Mapping $mapping -Index $index))
        $index++
    }

    $manifestConfigurationFiles = @()
    foreach ($item in $configurationItems) {
        $packagePath = Join-Path $packageRoot ($item.PackageSourcePath.Replace('/', '\'))
        New-Item -ItemType Directory -Path (Split-Path -Parent $packagePath) -Force | Out-Null
        Copy-Item -LiteralPath $item.SourcePath -Destination $packagePath -Force
        $manifestConfigurationFiles += [ordered]@{
            relativePath = $item.RelativePath
            source = $item.PackageSourcePath
        }
    }

    $manifest = [ordered]@{
        formatVersion = 1
        payload = [ordered]@{
            type = 'zip'
            path = 'payload/artifact.zip'
        }
        configurationFiles = @($manifestConfigurationFiles)
    }

    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'omp-artifact-package.json') -Encoding UTF8

    New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedOutputPath) -Force | Out-Null
    if (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf) {
        Remove-Item -LiteralPath $resolvedOutputPath -Force
    }

    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $resolvedOutputPath -Force
    Write-Host "Created artifact package: $resolvedOutputPath"
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
