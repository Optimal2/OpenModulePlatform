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

Runtime configuration files such as appsettings.json and
appsettings.Development.json are removed from payload directories before the
immutable artifact payload is zipped. Put those files in -ConfigurationFile,
artifact configuration rows, or config overlays instead.

Use -MinModuleDefinitionVersion only when this artifact requires SQL, OMP
metadata, or another module contract from a newer module definition. Leave it
empty for ordinary code-only artifact releases.

Use -MinWorkerHostVersion for worker plugins compiled against a worker-host
contract that older omp-workerprocesshost artifacts do not implement.
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
    [string[]]$ConfigurationFile = @(),
    [string]$MinModuleDefinitionVersion,
    [string]$MinWorkerHostVersion,
    # Source provenance stamped into the artifact manifest so a package can be
    # traced back to the exact commit it was built from. All three are optional
    # on write and ignored when absent on read: older importers keep working.
    # The fields are omitted (not guessed) when no commit SHA is supplied.
    [string]$SourceRepositoryKey = '',
    [string]$SourceCommitSha = '',
    [switch]$SourceDirty
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Shared canonical runtime-configuration rule (mirrors
# OpenModulePlatform.Artifacts RuntimeConfigurationFiles).
. (Join-Path $PSScriptRoot '..\omp\runtime-configuration-files.ps1')

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

function Assert-ZipPayloadDoesNotContainRuntimeConfiguration {
    param([Parameter(Mandatory = $true)][string]$ZipPath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not [string]::IsNullOrWhiteSpace($entry.Name) `
                    -and (Test-OmpRuntimeConfigurationFileName -FileName $entry.Name)) {
                throw "Payload zip contains runtime configuration file '$($entry.FullName)'. Put runtime configuration in -ConfigurationFile instead."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Add-WorkerPluginCompatibilityManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadZip,
        [Parameter(Mandatory = $true)][string]$MinVersion
    )

    $entryName = 'omp-worker-plugin.json'
    $archive = [System.IO.Compression.ZipFile]::Open($PayloadZip, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $existing = $archive.Entries | Where-Object {
            [string]::Equals($_.FullName.Replace('\', '/'), $entryName, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($null -ne $existing) {
            throw "Artifact payload already contains reserved compatibility metadata '$entryName'."
        }

        $document = [ordered]@{
            formatVersion = 1
            workerHost = [ordered]@{
                componentKey = 'omp-workerprocesshost'
                minVersion = $MinVersion.Trim()
            }
        } | ConvertTo-Json -Depth 4

        $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $stream = $entry.Open()
        try {
            $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
            try {
                $writer.Write($document)
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-EmbeddedWorkerHostMinVersion {
    param([Parameter(Mandatory = $true)][string]$PayloadZip)

    $entryName = 'omp-worker-plugin.json'
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PayloadZip)
    try {
        $entry = $archive.Entries | Where-Object {
            [string]::Equals($_.FullName.Replace('\', '/'), $entryName, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($null -eq $entry) {
            return ''
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())
        try {
            $document = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }

        if ([int]$document.formatVersion -ne 1 `
                -or $null -eq $document.workerHost `
                -or -not [string]::Equals([string]$document.workerHost.componentKey, 'omp-workerprocesshost', [StringComparison]::OrdinalIgnoreCase) `
                -or [string]::IsNullOrWhiteSpace([string]$document.workerHost.minVersion)) {
            throw "Artifact payload compatibility metadata '$entryName' is invalid."
        }

        return ([string]$document.workerHost.minVersion).Trim()
    }
    finally {
        $archive.Dispose()
    }
}

function Compress-DirectoryToZip {
    <#
    .SYNOPSIS
    Writes every file under SourceDirectory into DestinationZip with
    FORWARD-SLASH entry names, on every host.

    .DESCRIPTION
    Compress-Archive under Windows PowerShell 5.1's inbox Archive module
    1.0.1.0 writes BACKSLASH entry names (configuration\001-...,
    payload\artifact.zip), and the consumers of these packages read entries
    by their forward-slash names -- a package built on a clean 5.1 host was
    unreadable (measured 2026-10-07). Building each entry name explicitly
    through System.IO.Compression makes the bytes independent of the host's
    Archive module version. Empty directories are not represented; the
    package formats carry no empty-directory semantics. Hidden files ARE
    included (the staging copy and this enumeration see them); the old
    Compress-Archive packing silently dropped them -- a deliberate difference,
    documented in docs/ARTIFACT_PACKAGES.md ("Zip entry semantics").
    #>
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationZip
    )

    $parent = Split-Path -Parent $DestinationZip
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (Test-Path -LiteralPath $DestinationZip -PathType Leaf) {
        Remove-Item -LiteralPath $DestinationZip -Force
    }

    $sourceRootFull = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
    $archive = [System.IO.Compression.ZipFile]::Open($DestinationZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        # Zip bytes are artifact identity too. Enumeration order and source file
        # timestamps vary across fresh checkouts and rebuilds, without changing
        # content. Ordinal order and the ZIP epoch make both archive layers stable.
        $files = [string[]]@([System.IO.Directory]::EnumerateFiles($sourceRootFull, '*', [System.IO.SearchOption]::AllDirectories))
        [Array]::Sort($files, [StringComparer]::Ordinal)
        foreach ($file in $files) {
            $entryName = $file.Substring($sourceRootFull.Length).TrimStart('\', '/') -replace '\\', '/'
            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $inputStream = [System.IO.File]::OpenRead($file)
            try {
                $outputStream = $entry.Open()
                try { $inputStream.CopyTo($outputStream) }
                finally { $outputStream.Dispose() }
            }
            finally { $inputStream.Dispose() }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Compress-PayloadDirectory {
    param(
        [string]$SourceDirectory,
        [string]$DestinationZip
    )

    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-artifact-payload-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
        Get-ChildItem -LiteralPath $SourceDirectory -Force | Copy-Item -Destination $stagingRoot -Recurse -Force
        Remove-OmpRuntimeConfigurationFilesFromFolder -Path $stagingRoot

        $items = @(Get-ChildItem -LiteralPath $stagingRoot -Force)
        if ($items.Count -eq 0) {
            throw "Payload directory is empty after runtime configuration files were removed: $SourceDirectory"
        }

        Compress-DirectoryToZip -SourceDirectory $stagingRoot -DestinationZip $DestinationZip
    }
    finally {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
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
        Assert-ZipPayloadDoesNotContainRuntimeConfiguration -ZipPath $payloadFullPath
        Copy-Item -LiteralPath $payloadFullPath -Destination $payloadZip -Force
    }
    else {
        throw "PayloadPath must be a directory or a .zip file: $payloadFullPath"
    }

    $embeddedMinWorkerHostVersion = Get-EmbeddedWorkerHostMinVersion -PayloadZip $payloadZip
    $resolvedMinWorkerHostVersion = if ([string]::IsNullOrWhiteSpace($MinWorkerHostVersion)) {
        $embeddedMinWorkerHostVersion
    }
    else {
        $MinWorkerHostVersion.Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($embeddedMinWorkerHostVersion) `
            -and -not [string]::IsNullOrWhiteSpace($MinWorkerHostVersion) `
            -and -not [string]::Equals($embeddedMinWorkerHostVersion, $MinWorkerHostVersion.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact payload worker-host requirement '$embeddedMinWorkerHostVersion' does not match requested version '$($MinWorkerHostVersion.Trim())'."
    }
    if ([string]::IsNullOrWhiteSpace($embeddedMinWorkerHostVersion) `
            -and -not [string]::IsNullOrWhiteSpace($resolvedMinWorkerHostVersion)) {
        Add-WorkerPluginCompatibilityManifest -PayloadZip $payloadZip -MinVersion $resolvedMinWorkerHostVersion
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
    if (-not [string]::IsNullOrWhiteSpace($MinModuleDefinitionVersion)) {
        $manifest.moduleDefinition = [ordered]@{
            minVersion = $MinModuleDefinitionVersion.Trim()
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($resolvedMinWorkerHostVersion)) {
        $manifest.workerHost = [ordered]@{
            componentKey = 'omp-workerprocesshost'
            minVersion = $resolvedMinWorkerHostVersion
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($SourceCommitSha)) {
        if (-not [string]::IsNullOrWhiteSpace($SourceRepositoryKey)) {
            $manifest.sourceRepositoryKey = $SourceRepositoryKey.Trim()
        }

        $manifest.sourceCommitSha = $SourceCommitSha.Trim()
        $manifest.sourceDirty = [bool]$SourceDirty
    }

    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'omp-artifact-package.json') -Encoding UTF8

    New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedOutputPath) -Force | Out-Null
    if (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf) {
        Remove-Item -LiteralPath $resolvedOutputPath -Force
    }

    Compress-DirectoryToZip -SourceDirectory $packageRoot -DestinationZip $resolvedOutputPath
    Write-Host "Created artifact package: $resolvedOutputPath"
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
