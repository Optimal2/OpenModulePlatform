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

By default every version of every object is included. Pass -LatestOnly to keep
only the highest version per versioned object identity (artifact packages and
dashboard widgets), matching the installer GUI export when "include historical
artifacts" is unchecked. All other object kinds are always kept.

.PARAMETER LatestOnly
Keep only the latest version per artifact identity
(module__app__type__target) and per dashboard widget identity. Versions are
compared with the same numeric major.minor.patch semantics as the installer
GUI. Without this switch the output is unchanged: all versions are included.
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

    [string]$TargetHostProfile = '',

    [switch]$LatestOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

. (Join-Path $PSScriptRoot 'runtime-configuration-files.ps1')

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

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-PortableObjectVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ($Kind.Equals('artifact-package', [StringComparison]::OrdinalIgnoreCase) -or $Kind.Equals('artifact', [StringComparison]::OrdinalIgnoreCase)) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($Path)
        $parts = $name.Split([string[]]@('__'), [StringSplitOptions]::None)
        if ($parts.Length -ge 5 -and -not [string]::IsNullOrWhiteSpace($parts[$parts.Length - 1])) {
            return $parts[$parts.Length - 1].Trim()
        }

        return ''
    }

    if ($Kind.Equals('module-definition', [StringComparison]::OrdinalIgnoreCase)) {
        try {
            $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
            $definitionVersion = [string](Get-JsonPropertyValue -Object $document -Name 'definitionVersion')
            if ([string]::IsNullOrWhiteSpace($definitionVersion)) {
                return ''
            }

            return $definitionVersion.Trim()
        }
        catch {
            return ''
        }
    }

    if ($Kind.Equals('host-config', [StringComparison]::OrdinalIgnoreCase) -or $Kind.Equals('host-configuration', [StringComparison]::OrdinalIgnoreCase)) {
        return Get-JsonOrZipManifestVersion -Path $Path -JsonPropertyName 'configurationVersion' -ZipManifestName 'omp-host-config.json'
    }

    if ($Kind.Equals('config-overlay', [StringComparison]::OrdinalIgnoreCase)) {
        return Get-JsonOrZipManifestVersion -Path $Path -JsonPropertyName 'overlayVersion' -ZipManifestName 'omp-config-overlay.json'
    }

    if ($Kind.Equals('widget-data', [StringComparison]::OrdinalIgnoreCase)) {
        return Get-JsonOrZipManifestVersion -Path $Path -JsonPropertyName 'packageVersion' -ZipManifestName 'omp-widget-runtime-data.json'
    }

    if ($Kind.Equals('dashboard-widget', [StringComparison]::OrdinalIgnoreCase)) {
        try {
            $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
            $packageVersion = [string](Get-JsonPropertyValue -Object $document -Name 'packageVersion')
            if (-not [string]::IsNullOrWhiteSpace($packageVersion)) {
                return $packageVersion.Trim()
            }

            $widgetVersions = @(
                foreach ($widget in @((Get-JsonPropertyValue -Object $document -Name 'widgets'))) {
                    if ($null -eq $widget) {
                        continue
                    }

                    $widgetVersion = [string](Get-JsonPropertyValue -Object $widget -Name 'widgetVersion')
                    if (-not [string]::IsNullOrWhiteSpace($widgetVersion)) {
                        $widgetVersion.Trim()
                    }
                }
            )

            $latestWidgetVersion = @($widgetVersions | Sort-Object -Descending | Select-Object -First 1)
            return [string]($latestWidgetVersion | Select-Object -First 1)
        }
        catch {
            return ''
        }
    }

    return ''
}

function Get-JsonOrZipManifestVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$JsonPropertyName,
        [Parameter(Mandatory = $true)][string]$ZipManifestName
    )

    try {
        if ([System.IO.Path]::GetExtension($Path).Equals('.json', [StringComparison]::OrdinalIgnoreCase)) {
            $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
            $version = [string](Get-JsonPropertyValue -Object $document -Name $JsonPropertyName)
            if ([string]::IsNullOrWhiteSpace($version)) {
                return ''
            }

            return $version.Trim()
        }

        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $manifestEntry = $archive.Entries |
                Where-Object { $_.FullName -eq $ZipManifestName } |
                Select-Object -First 1
            if ($null -eq $manifestEntry) {
                return ''
            }

            $stream = $manifestEntry.Open()
            try {
                $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
                try {
                    $document = $reader.ReadToEnd() | ConvertFrom-Json
                    $version = [string](Get-JsonPropertyValue -Object $document -Name $JsonPropertyName)
                    if ([string]::IsNullOrWhiteSpace($version)) {
                        return ''
                    }

                    return $version.Trim()
                }
                finally {
                    $reader.Dispose()
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
    catch {
        return ''
    }
}

function Compare-UniversalPackageVersionText {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Left,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Right
    )

    # Mirrors CompareVersionText in OpenModulePlatform.Bootstrapper/Program.Gui.cs.
    $leftVersion = $null
    $rightVersion = $null
    if ([System.Version]::TryParse($Left, [ref]$leftVersion) -and [System.Version]::TryParse($Right, [ref]$rightVersion)) {
        return $leftVersion.CompareTo($rightVersion)
    }

    $splitChars = [char[]]@('.', '-', '+')
    $leftParts = @($Left.Split($splitChars, [StringSplitOptions]::RemoveEmptyEntries))
    $rightParts = @($Right.Split($splitChars, [StringSplitOptions]::RemoveEmptyEntries))
    $count = [Math]::Max($leftParts.Length, $rightParts.Length)
    for ($index = 0; $index -lt $count; $index++) {
        $leftPart = if ($index -lt $leftParts.Length) { $leftParts[$index] } else { '0' }
        $rightPart = if ($index -lt $rightParts.Length) { $rightParts[$index] } else { '0' }
        $leftNumber = 0
        $rightNumber = 0
        if ([int]::TryParse($leftPart, [ref]$leftNumber) -and [int]::TryParse($rightPart, [ref]$rightNumber)) {
            $numberComparison = $leftNumber.CompareTo($rightNumber)
            if ($numberComparison -ne 0) {
                return $numberComparison
            }

            continue
        }

        $textComparison = [string]::Compare($leftPart, $rightPart, [StringComparison]::OrdinalIgnoreCase)
        if ($textComparison -ne 0) {
            return $textComparison
        }
    }

    return 0
}

function ConvertTo-SafeUniversalPackagePathSegment {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    # Mirrors SanitizeUniversalPackagePathSegment in OpenModulePlatform.Bootstrapper/Program.Gui.cs.
    $invalidChars = @{}
    foreach ($ch in [System.IO.Path]::GetInvalidFileNameChars()) {
        $invalidChars[$ch] = $true
    }
    foreach ($ch in [char[]]@('/', '\', ':')) {
        $invalidChars[$ch] = $true
    }

    $chars = foreach ($ch in $Value.Trim().ToCharArray()) {
        if ($invalidChars.ContainsKey($ch)) { '_' } else { $ch }
    }

    $sanitized = ([string]::new([char[]]@($chars))).Trim('.', ' ')
    if ([string]::IsNullOrWhiteSpace($sanitized)) {
        return 'host'
    }

    return $sanitized
}

function Get-UniversalPackageVersionedIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Version
    )

    # Mirrors TryParseUniversalPackageArtifactIdentity and
    # TryParseUniversalPackageWidgetIdentity in OpenModulePlatform.Bootstrapper/Program.Gui.cs.
    if (($Kind.Equals('artifact-package', [StringComparison]::OrdinalIgnoreCase) -or $Kind.Equals('artifact', [StringComparison]::OrdinalIgnoreCase)) `
            -and $PackagePath.StartsWith('artifacts/', [StringComparison]::OrdinalIgnoreCase)) {
        $fileName = [System.IO.Path]::GetFileNameWithoutExtension($PackagePath)
        $parts = $fileName.Split([string[]]@('__'), [StringSplitOptions]::None)
        if ($parts.Length -ne 5) {
            return $null
        }

        return [pscustomobject]@{
            IdentityKey = 'artifact__' + ($parts[0..3] -join '__')
            Version = $parts[4]
            PackagePath = $PackagePath
        }
    }

    if ($Kind.Equals('dashboard-widget', [StringComparison]::OrdinalIgnoreCase) `
            -and $PackagePath.StartsWith('widgets/', [StringComparison]::OrdinalIgnoreCase)) {
        $widgetVersion = if ([string]::IsNullOrWhiteSpace($Version)) { '0.0.0' } else { $Version }
        $fileName = [System.IO.Path]::GetFileNameWithoutExtension($PackagePath)
        $versionSuffix = '__' + (ConvertTo-SafeUniversalPackagePathSegment -Value $widgetVersion)
        $identity = if ($fileName.EndsWith($versionSuffix, [StringComparison]::OrdinalIgnoreCase)) {
            $fileName.Substring(0, $fileName.Length - $versionSuffix.Length)
        }
        else {
            $fileName
        }

        if ([string]::IsNullOrWhiteSpace($identity)) {
            return $null
        }

        return [pscustomobject]@{
            IdentityKey = 'widget__' + $identity
            Version = $widgetVersion
            PackagePath = $PackagePath
        }
    }

    return $null
}

function Select-LatestUniversalPackageObjects {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Files
    )

    # Mirrors FilterLatestUniversalPackageVersionedObjects in
    # OpenModulePlatform.Bootstrapper/Program.Gui.cs: keep only the highest
    # version per artifact/widget identity, keep every non-versioned object.
    $latestByIdentity = New-Object 'System.Collections.Generic.Dictionary[string,psobject]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Files) {
        $identity = Get-UniversalPackageVersionedIdentity -Kind $file.Kind -PackagePath $file.PackagePath -Version ([string]$file.Version)
        if ($null -eq $identity) {
            continue
        }

        $current = $null
        if (-not $latestByIdentity.TryGetValue($identity.IdentityKey, [ref]$current) -or
            (Compare-UniversalPackageVersionText -Left $identity.Version -Right ([string]$current.Version)) -gt 0) {
            $latestByIdentity[$identity.IdentityKey] = $identity
        }
    }

    $latestPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($identity in $latestByIdentity.Values) {
        [void]$latestPaths.Add($identity.PackagePath)
    }

    return @($Files | Where-Object {
        $identity = Get-UniversalPackageVersionedIdentity -Kind $_.Kind -PackagePath $_.PackagePath -Version ([string]$_.Version)
        ($null -eq $identity) -or $latestPaths.Contains($_.PackagePath)
    })
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
    @{ Folder = 'artifacts'; Kind = 'artifact-package'; Patterns = @('*.zip') },
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
                Version = Get-PortableObjectVersion -Kind $folder.Kind -Path $file.FullName
            }
        }
    }
}

if ($LatestOnly) {
    $files = Select-LatestUniversalPackageObjects -Files @($files)
}

# Fail-fast guard (same rule as the import-time validator): no assembled
# artifact payload may contain runtime configuration files.
foreach ($file in @($files)) {
    if ($file.Kind.Equals('artifact-package', [StringComparison]::OrdinalIgnoreCase)) {
        Assert-OmpArtifactPackageHasNoRuntimeConfiguration `
            -ZipPath $file.FullName `
            -Description "Universal package object '$($file.PackagePath)'"
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
        $item = [ordered]@{
            kind = $file.Kind
            path = $file.PackagePath
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$file.Version)) {
            $item.version = [string]$file.Version
        }

        $items.Add($item)
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
