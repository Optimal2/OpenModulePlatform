# File: scripts/deployment/package-hostagent-first.ps1
[CmdletBinding()]
param(
    [string]$ConfigPath = '',
    [string]$RepositoryRoot = '',
    [string]$OpenDocViewerRoot = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Configuration = '',
    [switch]$SkipRestore,
    [switch]$SkipOpenDocViewerBuild,
    [switch]$SkipOpenDocViewerNpmInstall,
    [switch]$SkipZip,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'omp-suite.local.psd1'
}

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Import-DeploymentConfig {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @{}
    }

    $config = if (Get-Command Import-PowerShellDataFile -ErrorAction SilentlyContinue) {
        Import-PowerShellDataFile -LiteralPath $Path
    }
    else {
        # Windows PowerShell hosts used by older installer packages may lack
        # Import-PowerShellDataFile. The package config is a repository-owned
        # .psd1 data file, so evaluating it as a scriptblock is an acceptable
        # compatibility fallback for this packaging-only path.
        & ([scriptblock]::Create((Get-Content -LiteralPath $Path -Raw)))
    }
    if ($null -eq $config) {
        return @{}
    }

    return $config
}

function Get-ConfigValue {
    param(
        [hashtable]$Config,
        [string]$Name,
        $DefaultValue = $null
    )

    if ($Config.ContainsKey($Name) -and $null -ne $Config[$Name]) {
        return $Config[$Name]
    }

    return $DefaultValue
}

function Get-NestedConfigValue {
    param(
        [hashtable]$Config,
        [string]$Section,
        [string]$Name,
        $DefaultValue = $null
    )

    if ($Config.ContainsKey($Section) -and $Config[$Section] -is [hashtable]) {
        $sectionTable = [hashtable]$Config[$Section]
        if ($sectionTable.ContainsKey($Name) -and $null -ne $sectionTable[$Name]) {
            return $sectionTable[$Name]
        }
    }

    return $DefaultValue
}

function Resolve-DeploymentPath {
    param(
        [string]$Path,
        [string]$BasePath
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Join-DeploymentPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Child
    )

    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($Root, $Child))
}

function Resolve-ContentWebAppRuntimePath {
    param(
        [Parameter(Mandatory = $true)][string]$ConfiguredPath,
        [Parameter(Mandatory = $true)][string]$DefaultRelativePath,
        [Parameter(Mandatory = $true)][string]$WebAppsRoot,
        [Parameter(Mandatory = $true)][string]$ContentWebAppPath
    )

    $path = $ConfiguredPath
    if ([string]::IsNullOrWhiteSpace($path)) {
        $path = $DefaultRelativePath
    }

    if ([System.IO.Path]::IsPathRooted($path)) {
        return [System.IO.Path]::GetFullPath($path)
    }

    $contentRoot = Join-DeploymentPath -Root $WebAppsRoot -Child $ContentWebAppPath
    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($contentRoot, $path))
}

function Get-ContentWebAppFileMirrors {
    param(
        [string]$SharedServerReportsPath,
        [string]$SharedHtmlFilesPath,
        [Parameter(Mandatory = $true)][string]$ServerReportsPath,
        [Parameter(Mandatory = $true)][string]$HtmlFilesPath,
        [Parameter(Mandatory = $true)][string]$WebAppsRoot,
        [Parameter(Mandatory = $true)][string]$ContentWebAppPath
    )

    $mirrors = @()

    if (-not [string]::IsNullOrWhiteSpace($SharedServerReportsPath)) {
        $mirrors += [ordered]@{
            SourcePath = [System.IO.Path]::GetFullPath($SharedServerReportsPath)
            TargetPath = Resolve-ContentWebAppRuntimePath `
                -ConfiguredPath $ServerReportsPath `
                -DefaultRelativePath 'App_Data/ContentReports' `
                -WebAppsRoot $WebAppsRoot `
                -ContentWebAppPath $ContentWebAppPath
            DeleteStaleTargetEntries = $true
            ExcludedEntries = @()
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($SharedHtmlFilesPath)) {
        $mirrors += [ordered]@{
            SourcePath = [System.IO.Path]::GetFullPath($SharedHtmlFilesPath)
            TargetPath = Resolve-ContentWebAppRuntimePath `
                -ConfiguredPath $HtmlFilesPath `
                -DefaultRelativePath 'App_Data/ContentPages' `
                -WebAppsRoot $WebAppsRoot `
                -ContentWebAppPath $ContentWebAppPath
            DeleteStaleTargetEntries = $true
            ExcludedEntries = @()
        }
    }

    return $mirrors
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required file not found: $Source"
    }

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-ModuleDefinitionsFromManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($definition in @($manifest.moduleDefinitions)) {
        if ($null -eq $definition) {
            continue
        }

        $relativePath = [string]$definition.path
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            throw "Module definition entry in '$ManifestPath' is missing path."
        }

        $sourcePath = Resolve-DeploymentPath -Path $relativePath -BasePath $RepositoryRoot
        $destinationName = [System.IO.Path]::GetFileName($sourcePath)
        Copy-RequiredFile -Source $sourcePath -Destination (Join-Path $Destination $destinationName)
    }
}

function Add-UniqueResolvedPath {
    param(
        [System.Collections.Generic.List[string]]$Paths,
        [string]$Path,
        [string]$BasePath
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $resolved = Resolve-DeploymentPath -Path $Path -BasePath $BasePath
    if (-not (Test-Path -LiteralPath (Join-Path $resolved 'omp-components.json') -PathType Leaf)) {
        return
    }

    if (-not ($Paths | Where-Object { [string]::Equals($_, $resolved, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)) {
        $Paths.Add($resolved) | Out-Null
    }
}

function Get-DeveloperSourceRoots {
    param(
        [string]$ConfiguredRootsText,
        [string]$ConfigDirectory,
        [string]$RepositoryRoot,
        [string]$OpenDocViewerRoot
    )

    $roots = [System.Collections.Generic.List[string]]::new()
    Add-UniqueResolvedPath -Paths $roots -Path $RepositoryRoot -BasePath $ConfigDirectory
    Add-UniqueResolvedPath -Paths $roots -Path $OpenDocViewerRoot -BasePath $ConfigDirectory

    foreach ($entry in $ConfiguredRootsText.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)) {
        Add-UniqueResolvedPath -Paths $roots -Path $entry.Trim() -BasePath $ConfigDirectory
    }

    $workspaceRoot = Split-Path -Parent (Resolve-DeploymentPath -Path $RepositoryRoot -BasePath $ConfigDirectory)
    if (-not [string]::IsNullOrWhiteSpace($workspaceRoot) -and (Test-Path -LiteralPath $workspaceRoot -PathType Container)) {
        foreach ($candidate in Get-ChildItem -LiteralPath $workspaceRoot -Directory) {
            Add-UniqueResolvedPath -Paths $roots -Path $candidate.FullName -BasePath $ConfigDirectory
        }
    }

    return @($roots)
}

function Get-ArtifactArchiveRoots {
    param(
        [object[]]$ConfiguredRoots,
        [string]$ConfigDirectory,
        [string]$RuntimeRoot,
        [string[]]$SourceRoots
    )

    $roots = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in @($ConfiguredRoots)) {
        if ($null -eq $entry) {
            continue
        }

        $resolved = Resolve-DeploymentPath -Path ([string]$entry) -BasePath $ConfigDirectory
        if (Test-Path -LiteralPath $resolved -PathType Container) {
            if (-not ($roots | Where-Object { [string]::Equals($_, $resolved, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)) {
                $roots.Add($resolved) | Out-Null
            }
        }
    }

    $defaultArchiveRoot = Join-DeploymentPath -Root $RuntimeRoot -Child 'ArtifactArchive'
    if (Test-Path -LiteralPath $defaultArchiveRoot -PathType Container) {
        if (-not ($roots | Where-Object { [string]::Equals($_, $defaultArchiveRoot, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)) {
            $roots.Add($defaultArchiveRoot) | Out-Null
        }
    }

    foreach ($sourceRoot in @($SourceRoots)) {
        $sourceArtifactRoot = Join-Path $sourceRoot 'artifacts'
        if (Test-Path -LiteralPath $sourceArtifactRoot -PathType Container) {
            if (-not ($roots | Where-Object { [string]::Equals($_, $sourceArtifactRoot, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)) {
                $roots.Add($sourceArtifactRoot) | Out-Null
            }
        }
    }

    return @($roots)
}

function Copy-AvailableArtifactPackage {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)][hashtable]$CopiedFiles
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        return
    }

    $fileName = [System.IO.Path]::GetFileName($Source)
    if ($CopiedFiles.ContainsKey($fileName)) {
        return
    }

    Copy-RequiredFile -Source $Source -Destination (Join-Path $DestinationRoot $fileName)
    $CopiedFiles[$fileName] = $true
}

function Copy-AvailableArtifactPackagesFromManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string[]]$ArchiveRoots,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)][hashtable]$CopiedFiles
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($component in @($manifest.components)) {
        if ($null -eq $component -or -not (Test-ComponentHasArtifactIdentity -Component $component)) {
            continue
        }

        $packageName = Get-ArtifactPackageFileName `
            -ModuleKey ([string]$component.moduleKey) `
            -AppKey ([string]$component.appKey) `
            -PackageType ([string]$component.packageType) `
            -TargetName ([string]$component.targetName) `
            -Version ([string]$component.version)
        foreach ($archiveRoot in $ArchiveRoots) {
            $candidate = Join-Path $archiveRoot $packageName
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                Copy-AvailableArtifactPackage -Source $candidate -DestinationRoot $DestinationRoot -CopiedFiles $CopiedFiles
                break
            }
        }
    }
}

function Compress-FolderToZip {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Cannot package missing folder: $Source"
    }

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        Remove-Item -LiteralPath $Destination -Force
    }

    Compress-Archive -Path (Join-Path $Source '*') -DestinationPath $Destination -Force
}

function Remove-RuntimeConfigurationFilesFromFolder {
    param([Parameter(Mandatory = $true)][string]$Path)

    Get-ChildItem -LiteralPath $Path -File -Recurse | Where-Object {
        [string]::Equals($_.Name, 'appsettings.json', [StringComparison]::OrdinalIgnoreCase) -or
        ($_.Name.StartsWith('appsettings.', [StringComparison]::OrdinalIgnoreCase) -and $_.Name.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase))
    } | Remove-Item -Force
}

function Compress-ArtifactPayloadFolderToZip {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$BuildRoot
    )

    $stagingRoot = Join-Path $BuildRoot ('artifact-payload-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
        Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $stagingRoot -Recurse -Force
        Remove-RuntimeConfigurationFilesFromFolder -Path $stagingRoot
        Compress-FolderToZip -Source $stagingRoot -Destination $Destination
    }
    finally {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-ManifestPropertyValue {
    param(
        [object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) {
        return ''
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return ''
    }

    return [string]$property.Value
}

function Get-ArtifactPackageFileName {
    param(
        [Parameter(Mandatory = $true)][string]$ModuleKey,
        [Parameter(Mandatory = $true)][string]$AppKey,
        [Parameter(Mandatory = $true)][string]$PackageType,
        [Parameter(Mandatory = $true)][string]$TargetName,
        [Parameter(Mandatory = $true)][string]$Version
    )

    return "$ModuleKey`__$AppKey`__$PackageType`__$TargetName`__$Version.zip"
}

function Test-ComponentHasArtifactIdentity {
    param([object]$Component)

    return -not [string]::IsNullOrWhiteSpace((Get-ManifestPropertyValue -Object $Component -Name 'moduleKey')) `
        -and -not [string]::IsNullOrWhiteSpace((Get-ManifestPropertyValue -Object $Component -Name 'appKey')) `
        -and -not [string]::IsNullOrWhiteSpace((Get-ManifestPropertyValue -Object $Component -Name 'packageType')) `
        -and -not [string]::IsNullOrWhiteSpace((Get-ManifestPropertyValue -Object $Component -Name 'targetName')) `
        -and -not [string]::IsNullOrWhiteSpace((Get-ManifestPropertyValue -Object $Component -Name 'version'))
}

function New-ArtifactPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadZip,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$BuildRoot,
        [object[]]$ConfigurationFiles = @()
    )

    $stagingRoot = Join-Path $BuildRoot ('artifact-package-' + [Guid]::NewGuid().ToString('N'))
    $payloadDestination = Join-Path $stagingRoot 'payload\artifact.zip'

    try {
        Copy-RequiredFile -Source $PayloadZip -Destination $payloadDestination

        $manifestConfigurationFiles = @()
        foreach ($configurationFile in $ConfigurationFiles) {
            $relativePath = [string]$configurationFile.RelativePath
            $sourcePath = [string]$configurationFile.SourcePath
            $packageSourcePath = [string]$configurationFile.PackageSourcePath

            if ([string]::IsNullOrWhiteSpace($relativePath) -or [string]::IsNullOrWhiteSpace($sourcePath) -or [string]::IsNullOrWhiteSpace($packageSourcePath)) {
                throw "Artifact configuration file entries require RelativePath, SourcePath, and PackageSourcePath."
            }

            $configurationDestination = Join-Path $stagingRoot $packageSourcePath.Replace('/', '\')
            Copy-RequiredFile -Source $sourcePath -Destination $configurationDestination
            $manifestConfigurationFiles += [ordered]@{
                relativePath = $relativePath.Replace('\', '/').Trim('/')
                source = $packageSourcePath.Replace('\', '/').Trim('/')
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

        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stagingRoot 'omp-artifact-package.json') -Encoding UTF8
        Compress-FolderToZip -Source $stagingRoot -Destination $Destination
    }
    finally {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Compress-PackageRootToZip {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $items = @(Get-ChildItem -LiteralPath $PackageRoot -Force | Where-Object {
            -not [string]::Equals($_.Name, '.build', [StringComparison]::OrdinalIgnoreCase)
        })
    if ($items.Count -eq 0) {
        throw "Package root has no distributable content: $PackageRoot"
    }

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        Remove-Item -LiteralPath $Destination -Force
    }

    Compress-Archive -Path @($items | ForEach-Object { $_.FullName }) -DestinationPath $Destination -Force
}

function Test-ZipContainsEntry {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
        return $false
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $normalizedEntryName = $EntryName.Replace('\', '/')
        foreach ($entry in $archive.Entries) {
            if ([string]::Equals($entry.FullName.Replace('\', '/'), $normalizedEntryName, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    }
    finally {
        $archive.Dispose()
    }
}

function New-NeutralOpenDocViewerSiteConfig {
    return @'
// Generated by the OpenModulePlatform HostAgent-first package builder.
// Keep site-specific OpenDocViewer overrides in this artifact configuration file.
(function (w) {
  w.__ODV_SITE_CONFIG__ = w.__ODV_SITE_CONFIG__ || {};
})(window);
'@
}

function New-OpenDocViewerArtifactPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadZip,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string]$SiteConfigPath,
        [Parameter(Mandatory = $true)][string]$BuildRoot
    )

    $stagingRoot = Join-Path $BuildRoot ('odv-artifact-package-' + [Guid]::NewGuid().ToString('N'))
    $payloadDestination = Join-Path $stagingRoot 'payload\artifact.zip'
    $configurationDestination = Join-Path $stagingRoot 'configuration\odv.site.config.js'

    try {
        Copy-RequiredFile -Source $PayloadZip -Destination $payloadDestination
        New-Item -ItemType Directory -Path (Split-Path -Parent $configurationDestination) -Force | Out-Null

        if ([string]::IsNullOrWhiteSpace($SiteConfigPath)) {
            Set-Content -LiteralPath $configurationDestination -Value (New-NeutralOpenDocViewerSiteConfig) -Encoding UTF8
        }
        else {
            Copy-RequiredFile -Source $SiteConfigPath -Destination $configurationDestination
        }

        $manifest = [ordered]@{
            formatVersion = 1
            payload = [ordered]@{
                type = 'zip'
                path = 'payload/artifact.zip'
            }
            configurationFiles = @(
                [ordered]@{
                    relativePath = 'odv.site.config.js'
                    source = 'configuration/odv.site.config.js'
                }
            )
        }

        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stagingRoot 'omp-artifact-package.json') -Encoding UTF8
        Compress-FolderToZip -Source $stagingRoot -Destination $Destination
    }
    finally {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-NodePackageVersion {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$DisplayName
    )

    $packageJsonPath = Join-Path $PackageRoot 'package.json'
    if (-not (Test-Path -LiteralPath $packageJsonPath -PathType Leaf)) {
        throw "$DisplayName package.json was not found in: $PackageRoot"
    }

    $packageJson = Get-Content -LiteralPath $packageJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $versionProperty = $packageJson.PSObject.Properties['version']
    if ($null -eq $versionProperty -or [string]::IsNullOrWhiteSpace([string]$versionProperty.Value)) {
        throw "$DisplayName package.json does not contain a version."
    }

    return ([string]$versionProperty.Value).Trim()
}

function Get-ProjectNameFromComponent {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][object]$Component
    )

    $projectPath = Join-Path $RepositoryRoot ([string]$Component.projectPath)
    if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
        return [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
    }

    $project = Get-ChildItem -LiteralPath $projectPath -Filter '*.csproj' -File | Select-Object -First 1
    if ($null -eq $project) {
        throw "Component '$($Component.componentKey)' project file was not found below $projectPath."
    }

    return [System.IO.Path]::GetFileNameWithoutExtension($project.FullName)
}

function Add-ArtifactEntry {
    param(
        [System.Collections.ArrayList]$Artifacts,
        [string]$Source,
        [string]$Target,
        [bool]$IsExample = $false,
        [bool]$Overwrite = $true,
        [bool]$RemoveRuntimeConfigurationFiles = $true
    )

    [void]$Artifacts.Add([ordered]@{
            source = $Source.Replace('\', '/')
            target = $Target.Replace('\', '/')
            overwrite = $Overwrite
            removeRuntimeConfigurationFiles = $RemoveRuntimeConfigurationFiles
            isExample = $IsExample
        })
}

function Copy-AdditionalArtifactFiles {
    param(
        [object[]]$Entries,
        [Parameter(Mandatory = $true)][string]$ConfigDirectory,
        [Parameter(Mandatory = $true)][string]$PayloadRoot,
        [Parameter(Mandatory = $true)][System.Collections.ArrayList]$Artifacts,
        [Parameter(Mandatory = $true)][string]$AvailableArtifactsRoot,
        [Parameter(Mandatory = $true)][hashtable]$AvailableArtifactPackageFiles
    )

    foreach ($entry in $Entries) {
        $sourcePath = ''
        $payloadPath = ''
        $targetPath = ''
        $isExample = $false
        $overwrite = $true
        $removeRuntimeConfigurationFiles = $true

        if ($entry -is [hashtable]) {
            $sourcePath = [string]$entry.Source
            $payloadPath = [string]$entry.Payload
            $targetPath = [string]$entry.Target
            if ($entry.ContainsKey('IsExample')) {
                $isExample = [bool]$entry.IsExample
            }
            if ($entry.ContainsKey('Overwrite')) {
                $overwrite = [bool]$entry.Overwrite
            }
            if ($entry.ContainsKey('RemoveRuntimeConfigurationFiles')) {
                $removeRuntimeConfigurationFiles = [bool]$entry.RemoveRuntimeConfigurationFiles
            }
        }
        else {
            $sourcePath = [string]$entry
        }

        if ([string]::IsNullOrWhiteSpace($sourcePath)) {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($targetPath)) {
            throw "Additional artifact entry '$sourcePath' is missing Target."
        }

        $resolvedSource = Resolve-DeploymentPath -Path $sourcePath -BasePath $ConfigDirectory
        if ([string]::IsNullOrWhiteSpace($payloadPath)) {
            $payloadPath = 'payload/' + [System.IO.Path]::GetFileName($resolvedSource)
        }

        $payloadPath = $payloadPath.Replace('\', '/')
        if ([System.IO.Path]::IsPathRooted($payloadPath) `
                -or $payloadPath.Contains('../') `
                -or $payloadPath.IndexOf('..\', [StringComparison]::Ordinal) -ge 0) {
            throw "Additional artifact payload path must be relative and stay inside the package: $payloadPath"
        }

        $destination = Join-Path (Split-Path -Parent $PayloadRoot) $payloadPath
        Copy-RequiredFile -Source $resolvedSource -Destination $destination
        Add-ArtifactEntry `
            -Artifacts $Artifacts `
            -Source $payloadPath `
            -Target $targetPath `
            -IsExample $isExample `
            -Overwrite $overwrite `
            -RemoveRuntimeConfigurationFiles $removeRuntimeConfigurationFiles
        Copy-AvailableArtifactPackage `
            -Source $destination `
            -DestinationRoot $AvailableArtifactsRoot `
            -CopiedFiles $AvailableArtifactPackageFiles
    }
}

function Add-VersionOverride {
    param(
        [hashtable]$Overrides,
        [string]$ScriptPath,
        [string]$Version
    )

    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $Overrides[$ScriptPath.Replace('\', '/')] = $Version
    }
}

function Add-VersionVariableOverride {
    param(
        [hashtable]$Overrides,
        [string]$ScriptPath,
        [string]$VariableName,
        [string]$Version
    )

    if ([string]::IsNullOrWhiteSpace($VariableName) -or [string]::IsNullOrWhiteSpace($Version)) {
        return
    }

    $key = $ScriptPath.Replace('\', '/')
    if (-not $Overrides.ContainsKey($key)) {
        $Overrides[$key] = @{}
    }

    $Overrides[$key][$VariableName.Trim().TrimStart([char]'@')] = $Version
}

$config = Import-DeploymentConfig -Path $ConfigPath
$configPathForResolution = if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) { [System.IO.Path]::GetFullPath($ConfigPath) } else { Join-Path $PSScriptRoot 'omp-suite.local.psd1' }
$configDirectory = Split-Path -Parent $configPathForResolution
$scriptRootParent = Split-Path -Parent $PSScriptRoot
$defaultRepositoryRoot = Split-Path -Parent $scriptRootParent

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [string](Get-ConfigValue -Config $config -Name 'RepositoryRoot' -DefaultValue $defaultRepositoryRoot)
}
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = $defaultRepositoryRoot
}
$RepositoryRoot = Resolve-DeploymentPath -Path $RepositoryRoot -BasePath $configDirectory

$workspaceRoot = Split-Path -Parent $RepositoryRoot
if ([string]::IsNullOrWhiteSpace($OpenDocViewerRoot)) {
    $OpenDocViewerRoot = [string](Get-ConfigValue -Config $config -Name 'OpenDocViewerRoot' -DefaultValue (Join-Path $workspaceRoot 'OpenDocViewer'))
}
if ([string]::IsNullOrWhiteSpace($OpenDocViewerRoot)) {
    $OpenDocViewerRoot = Join-Path $workspaceRoot 'OpenDocViewer'
}
$OpenDocViewerRoot = Resolve-DeploymentPath -Path $OpenDocViewerRoot -BasePath $configDirectory

$developerSourceRootText = [string](Get-NestedConfigValue -Config $config -Section 'DeveloperSource' -Name 'SourceRoot' -DefaultValue $RepositoryRoot)
$developerSourceRoots = Get-DeveloperSourceRoots `
    -ConfiguredRootsText $developerSourceRootText `
    -ConfigDirectory $configDirectory `
    -RepositoryRoot $RepositoryRoot `
    -OpenDocViewerRoot $OpenDocViewerRoot
$runtimeRootForArtifactArchive = [string](Get-ConfigValue -Config $config -Name 'RuntimeRoot' -DefaultValue 'C:\OMP')
$runtimeRootForArtifactArchive = Resolve-DeploymentPath -Path $runtimeRootForArtifactArchive -BasePath $configDirectory
$artifactArchiveRoots = Get-ArtifactArchiveRoots `
    -ConfiguredRoots @((Get-NestedConfigValue -Config $config -Section 'HostAgentFirst' -Name 'AvailableArtifactArchiveRoots' -DefaultValue @())) `
    -ConfigDirectory $configDirectory `
    -RuntimeRoot $runtimeRootForArtifactArchive `
    -SourceRoots $developerSourceRoots

$componentManifestPath = Join-Path $RepositoryRoot 'omp-components.json'
if (-not (Test-Path -LiteralPath $componentManifestPath -PathType Leaf)) {
    throw "Component manifest was not found: $componentManifestPath"
}
$componentManifest = Get-Content -LiteralPath $componentManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$components = @($componentManifest.components)

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string](Get-ConfigValue -Config $config -Name 'Version' -DefaultValue ([string]$componentManifest.repositoryVersion))
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = '0.3.3'
}
if ([string]::IsNullOrWhiteSpace($Configuration)) {
    $Configuration = [string](Get-ConfigValue -Config $config -Name 'Configuration' -DefaultValue 'Release')
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = [string](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'OutputRoot' -DefaultValue (Join-Path $RepositoryRoot 'artifacts\hostagent-first'))
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepositoryRoot 'artifacts\hostagent-first'
}

if (-not $PSBoundParameters.ContainsKey('SkipRestore')) {
    $SkipRestore = [bool](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'SkipRestore' -DefaultValue $false)
}
if (-not $PSBoundParameters.ContainsKey('SkipOpenDocViewerBuild')) {
    $SkipOpenDocViewerBuild = [bool](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'SkipOpenDocViewerBuild' -DefaultValue $false)
}
if (-not $PSBoundParameters.ContainsKey('SkipOpenDocViewerNpmInstall')) {
    $SkipOpenDocViewerNpmInstall = [bool](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'SkipOpenDocViewerNpmInstall' -DefaultValue $false)
}
if (-not $PSBoundParameters.ContainsKey('SkipZip')) {
    $SkipZip = [bool](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'SkipZip' -DefaultValue $false)
}
if (-not $PSBoundParameters.ContainsKey('KeepStaging')) {
    $KeepStaging = [bool](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'KeepStaging' -DefaultValue $false)
}

$OutputRoot = Resolve-DeploymentPath -Path $OutputRoot -BasePath $RepositoryRoot
$packageRoot = Join-Path $OutputRoot ("OpenModulePlatformHostAgentFirst-$Version")
$payloadRoot = Join-Path $packageRoot 'payload'
$sqlRoot = Join-Path $packageRoot 'sql'
$toolsRoot = Join-Path $packageRoot 'tools'
$availableModuleDefinitionsRoot = Join-Path $packageRoot 'available-module-definitions'
$availableArtifactsRoot = Join-Path $packageRoot 'available-artifacts'
$availableArtifactPackageFiles = @{}
$buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-hostagent-first-build-' + [Guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $OutputRoot ("OpenModulePlatformHostAgentFirst-$Version.zip")

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $sqlRoot -Force | Out-Null
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $availableModuleDefinitionsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $availableArtifactsRoot -Force | Out-Null

Write-Step 'Publishing OpenModulePlatform'
$publishScript = Join-Path $RepositoryRoot 'publish-all.ps1'
$publishRoot = Join-Path $buildRoot 'omp-publish'
$publishArgs = @{
    Root = $RepositoryRoot
    Configuration = $Configuration
    OutputRoot = $publishRoot
    CleanOutput = $true
}
if (-not $SkipRestore) {
    $publishArgs.Restore = $true
}
& $publishScript @publishArgs

Write-Step 'Copying component payloads'
$componentPayloadSources = @{}
$componentPayloadRoot = Join-Path $buildRoot 'component-payloads'
New-Item -ItemType Directory -Path $componentPayloadRoot -Force | Out-Null
$workerProcessHostComponent = $components | Where-Object { $_.componentKey -eq 'omp-workerprocesshost' } | Select-Object -First 1

function New-WorkerManagerArtifactConfigurationFile {
    param(
        [Parameter(Mandatory = $true)][object]$WorkerProcessHostComponent,
        [Parameter(Mandatory = $true)][string]$BuildRoot
    )

    $workerProcessHostCacheRelativePath = [string]::Join(
        '/',
        @(
            [string]$WorkerProcessHostComponent.packageType,
            [string]$WorkerProcessHostComponent.targetName,
            [string]$WorkerProcessHostComponent.version
        ))
    $workerProcessExecutablePath = "{{Omp.Json.HostAgent.LocalArtifactCacheRoot}}/$workerProcessHostCacheRelativePath/OpenModulePlatform.WorkerProcessHost.exe"
    $configurationPath = Join-Path $BuildRoot 'worker-manager-appsettings.json'
    $configuration = [ordered]@{
        ConnectionStrings = [ordered]@{
            OmpDb = '{{Omp.Json.ConnectionStrings.OmpDb}}'
        }
        WorkerManager = [ordered]@{
            CatalogMode = 'OmpDatabase'
            HostKey = '{{Omp.Json.HostKey}}'
            HostName = ''
            RefreshSeconds = 15
            WorkerProcessPath = $workerProcessExecutablePath
            StopTimeoutSeconds = 15
            RestartDelaySeconds = 5
            RestartWindowSeconds = 300
            MaxRestartsPerWindow = 5
            OmpDatabase = [ordered]@{
                RuntimeKind = 'windows-worker-plugin'
                RunningDesiredState = 1
                UseHostArtifactCache = $true
            }
            HostAgentRpc = [ordered]@{
                Enabled = $true
                PipeName = ''
                TimeoutSeconds = 60
            }
            Workers = @()
        }
        Logging = [ordered]@{
            LogLevel = [ordered]@{
                Default = 'Information'
                'Microsoft.Hosting.Lifetime' = 'Information'
            }
        }
        NLog = [ordered]@{
            autoReload = $true
            throwConfigExceptions = $true
            variables = [ordered]@{
                appName = 'OpenModulePlatform.WorkerManager.WindowsService'
                logDirectory = '${basedir}/logs'
            }
            targets = [ordered]@{
                logfile = [ordered]@{
                    type = 'File'
                    fileName = '${var:logDirectory}/${var:appName}-${shortdate}.log'
                    layout = '${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}'
                }
                console = [ordered]@{
                    type = 'Console'
                    layout = '${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}'
                }
            }
            rules = @(
                [ordered]@{
                    logger = 'Microsoft.Hosting.Lifetime'
                    minLevel = 'Info'
                    writeTo = 'console,logfile'
                    final = $true
                },
                [ordered]@{
                    logger = 'Microsoft.*'
                    maxLevel = 'Info'
                    final = $true
                },
                [ordered]@{
                    logger = '*'
                    minLevel = 'Info'
                    writeTo = 'console,logfile'
                }
            )
        }
    }

    $configuration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configurationPath -Encoding UTF8
    return [ordered]@{
        RelativePath = 'appsettings.json'
        SourcePath = $configurationPath
        PackageSourcePath = 'configuration/appsettings.json'
    }
}

foreach ($component in $components) {
    $packageTemplate = [string]$component.packageFileTemplate
    if ([string]::IsNullOrWhiteSpace($packageTemplate)) {
        continue
    }

    $componentKey = [string]$component.componentKey
    $projectName = Get-ProjectNameFromComponent -RepositoryRoot $RepositoryRoot -Component $component
    $source = Join-Path $publishRoot $projectName
    $payloadZip = Join-Path $componentPayloadRoot "$componentKey.zip"
    Compress-FolderToZip -Source $source -Destination $payloadZip

    if (Test-ComponentHasArtifactIdentity -Component $component) {
        $artifactPayloadZip = Join-Path $componentPayloadRoot "$componentKey.artifact.zip"
        Compress-ArtifactPayloadFolderToZip -Source $source -Destination $artifactPayloadZip -BuildRoot $buildRoot
        $artifactPackageName = Get-ArtifactPackageFileName `
            -ModuleKey ([string]$component.moduleKey) `
            -AppKey ([string]$component.appKey) `
            -PackageType ([string]$component.packageType) `
            -TargetName ([string]$component.targetName) `
            -Version ([string]$component.version)
        $destination = Join-Path $payloadRoot $artifactPackageName
        $configurationFiles = @()
        if ([string]::Equals($componentKey, 'omp-workermanager-service', [StringComparison]::OrdinalIgnoreCase)) {
            if ($null -eq $workerProcessHostComponent) {
                throw "Component manifest must include omp-workerprocesshost before packaging omp-workermanager-service."
            }

            $configurationFiles += New-WorkerManagerArtifactConfigurationFile `
                -WorkerProcessHostComponent $workerProcessHostComponent `
                -BuildRoot $buildRoot
        }

        New-ArtifactPackage -PayloadZip $artifactPayloadZip -Destination $destination -BuildRoot $buildRoot -ConfigurationFiles $configurationFiles
        Copy-AvailableArtifactPackage -Source $destination -DestinationRoot $availableArtifactsRoot -CopiedFiles $availableArtifactPackageFiles
        $componentPayloadSources[$componentKey] = "payload/$artifactPackageName"

        if ([string]::Equals($componentKey, 'omp-hostagent-service', [StringComparison]::OrdinalIgnoreCase)) {
            # The bootstrapper itself still needs a direct HostAgent service zip
            # for first install/repair. The manifest artifact package above is
            # what OMP stores for later HostAgent self-upgrade.
            $bootstrapDestination = Join-Path $packageRoot $packageTemplate
            Copy-RequiredFile -Source $payloadZip -Destination $bootstrapDestination
        }
    }
    else {
        # Bootstrap infrastructure that is not represented by normal OMP app
        # metadata remains a direct payload zip for the installer itself.
        $destination = Join-Path $packageRoot $packageTemplate
        Copy-RequiredFile -Source $payloadZip -Destination $destination
        $componentPayloadSources[$componentKey] = $packageTemplate.Replace('\', '/')
    }
}

$openDocViewerPackageZip = [string](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'OpenDocViewerPackageZip' -DefaultValue '')
$openDocViewerPackageZip = Resolve-DeploymentPath -Path $openDocViewerPackageZip -BasePath $configDirectory
$openDocViewerVersion = [string](Get-NestedConfigValue -Config $config -Section 'OpenDocViewer' -Name 'Version' -DefaultValue '')
if ([string]::IsNullOrWhiteSpace($openDocViewerVersion)) {
    $openDocViewerVersion = Get-NodePackageVersion -PackageRoot $OpenDocViewerRoot -DisplayName 'OpenDocViewer'
}
$openDocViewerSiteConfigPath = [string](Get-NestedConfigValue -Config $config -Section 'OpenDocViewer' -Name 'SiteConfigPath' -DefaultValue '')
$openDocViewerSiteConfigPath = Resolve-DeploymentPath -Path $openDocViewerSiteConfigPath -BasePath $configDirectory
$openDocViewerArtifactPackageName = "opendocviewer__opendocviewer_webapp__web-app__opendocviewer__$openDocViewerVersion.zip"
$openDocViewerArtifactPackagePath = Join-Path $payloadRoot $openDocViewerArtifactPackageName

Write-Step 'Publishing OpenDocViewer'
if (-not (Test-Path -LiteralPath $OpenDocViewerRoot -PathType Container)) {
    throw "OpenDocViewer repository root was not found: $OpenDocViewerRoot"
}
Write-Host "OpenDocViewer version: $openDocViewerVersion"

if (-not [string]::IsNullOrWhiteSpace($openDocViewerPackageZip)) {
    if (Test-ZipContainsEntry -ZipPath $openDocViewerPackageZip -EntryName 'omp-artifact-package.json') {
        Copy-RequiredFile -Source $openDocViewerPackageZip -Destination $openDocViewerArtifactPackagePath
    }
    else {
        New-OpenDocViewerArtifactPackage `
            -PayloadZip $openDocViewerPackageZip `
            -Destination $openDocViewerArtifactPackagePath `
            -SiteConfigPath $openDocViewerSiteConfigPath `
            -BuildRoot $buildRoot
    }
}
else {
    if (-not $SkipOpenDocViewerBuild) {
        Push-Location $OpenDocViewerRoot
        try {
            if (-not $SkipOpenDocViewerNpmInstall) {
                Invoke-NativeChecked npm 'ci'
            }
            Invoke-NativeChecked npm 'run' 'build'
        }
        finally {
            Pop-Location
        }
    }

    $openDocViewerDistZip = Join-Path $buildRoot 'OpenDocViewer.dist.zip'
    Compress-FolderToZip -Source (Join-Path $OpenDocViewerRoot 'dist') -Destination $openDocViewerDistZip
    New-OpenDocViewerArtifactPackage `
        -PayloadZip $openDocViewerDistZip `
        -Destination $openDocViewerArtifactPackagePath `
        -SiteConfigPath $openDocViewerSiteConfigPath `
        -BuildRoot $buildRoot
}
Copy-AvailableArtifactPackage -Source $openDocViewerArtifactPackagePath -DestinationRoot $availableArtifactsRoot -CopiedFiles $availableArtifactPackageFiles

Write-Step 'Copying SQL scripts'
$sqlFiles = @(
    @{ Source = 'sql\1-setup-openmoduleplatform.sql'; Destination = 'OpenModulePlatform\1-setup-openmoduleplatform.sql' },
    @{ Source = 'sql\2-initialize-openmoduleplatform.sql'; Destination = 'OpenModulePlatform\2-initialize-openmoduleplatform.sql' },
    @{ Source = 'sql\3-initialize-opendocviewer.sql'; Destination = 'OpenModulePlatform\3-initialize-opendocviewer.sql' },
    @{ Source = 'OpenModulePlatform.Auth\sql\2-initialize-omp-auth.sql'; Destination = 'OpenModulePlatform.Auth\2-initialize-omp-auth.sql' },
    @{ Source = 'OpenModulePlatform.Portal\sql\1-setup-omp-portal.sql'; Destination = 'OpenModulePlatform.Portal\1-setup-omp-portal.sql' },
    @{ Source = 'OpenModulePlatform.Portal\sql\2-initialize-omp-portal.sql'; Destination = 'OpenModulePlatform.Portal\2-initialize-omp-portal.sql' },
    @{ Source = 'OpenModulePlatform.Portal\sql\3-sync-omp-portal-entries.sql'; Destination = 'OpenModulePlatform.Portal\3-sync-omp-portal-entries.sql' },
    @{ Source = 'OpenModulePlatform.Portal\sql\4-ensure-topbar-hover-user-setting.sql'; Destination = 'OpenModulePlatform.Portal\4-ensure-topbar-hover-user-setting.sql' },
    @{ Source = 'OpenModulePlatform.Web.ContentWebAppModule\Sql\1-setup-content-webapp.sql'; Destination = 'OpenModulePlatform.Web.ContentWebAppModule\1-setup-content-webapp.sql' },
    @{ Source = 'OpenModulePlatform.Web.ContentWebAppModule\Sql\2-initialize-content-webapp.sql'; Destination = 'OpenModulePlatform.Web.ContentWebAppModule\2-initialize-content-webapp.sql' },
    @{ Source = 'OpenModulePlatform.Web.ContentWebAppModule\Sql\3-add-server-report-support.sql'; Destination = 'OpenModulePlatform.Web.ContentWebAppModule\3-add-server-report-support.sql' },
    @{ Source = 'OpenModulePlatform.Web.iFrameWebAppModule\Sql\1-setup-iframe-webapp.sql'; Destination = 'OpenModulePlatform.Web.iFrameWebAppModule\1-setup-iframe-webapp.sql' },
    @{ Source = 'OpenModulePlatform.Web.iFrameWebAppModule\Sql\2-initialize-iframe-webapp.sql'; Destination = 'OpenModulePlatform.Web.iFrameWebAppModule\2-initialize-iframe-webapp.sql' },
    @{ Source = 'examples\WebAppModule\Sql\1-setup-example-webapp.sql'; Destination = 'examples\WebAppModule\1-setup-example-webapp.sql' },
    @{ Source = 'examples\WebAppModule\Sql\2-initialize-example-webapp.sql'; Destination = 'examples\WebAppModule\2-initialize-example-webapp.sql' },
    @{ Source = 'examples\WebAppBlazorModule\Sql\1-setup-example-webapp-blazor.sql'; Destination = 'examples\WebAppBlazorModule\1-setup-example-webapp-blazor.sql' },
    @{ Source = 'examples\WebAppBlazorModule\Sql\2-initialize-example-webapp-blazor.sql'; Destination = 'examples\WebAppBlazorModule\2-initialize-example-webapp-blazor.sql' },
    @{ Source = 'examples\ServiceAppModule\Sql\1-setup-example-serviceapp.sql'; Destination = 'examples\ServiceAppModule\1-setup-example-serviceapp.sql' },
    @{ Source = 'examples\ServiceAppModule\Sql\2-initialize-example-serviceapp.sql'; Destination = 'examples\ServiceAppModule\2-initialize-example-serviceapp.sql' },
    @{ Source = 'examples\WorkerAppModule\Sql\1-setup-example-workerapp.sql'; Destination = 'examples\WorkerAppModule\1-setup-example-workerapp.sql' },
    @{ Source = 'examples\WorkerAppModule\Sql\2-initialize-example-workerapp.sql'; Destination = 'examples\WorkerAppModule\2-initialize-example-workerapp.sql' }
)

foreach ($file in $sqlFiles) {
    Copy-RequiredFile -Source (Join-Path $RepositoryRoot $file.Source) -Destination (Join-Path $sqlRoot $file.Destination)
}

Write-Step 'Copying module definitions'
$moduleDefinitionsDestination = Join-Path $packageRoot 'module-definitions'
New-Item -ItemType Directory -Path $moduleDefinitionsDestination -Force | Out-Null
Copy-ModuleDefinitionsFromManifest -ManifestPath $componentManifestPath -RepositoryRoot $RepositoryRoot -Destination $moduleDefinitionsDestination
Copy-ModuleDefinitionsFromManifest -ManifestPath (Join-Path $OpenDocViewerRoot 'omp-components.json') -RepositoryRoot $OpenDocViewerRoot -Destination $moduleDefinitionsDestination
foreach ($sourceRoot in $developerSourceRoots) {
    Copy-ModuleDefinitionsFromManifest `
        -ManifestPath (Join-Path $sourceRoot 'omp-components.json') `
        -RepositoryRoot $sourceRoot `
        -Destination $availableModuleDefinitionsRoot
}

$additionalModuleDefinitionFiles = @((Get-NestedConfigValue -Config $config -Section 'HostAgentFirst' -Name 'AdditionalModuleDefinitionFiles' -DefaultValue @()))
foreach ($entry in $additionalModuleDefinitionFiles) {
    $sourcePath = ''
    $destinationName = ''
    if ($entry -is [hashtable]) {
        $sourcePath = [string]$entry.Source
        $destinationName = [string]$entry.Destination
    }
    else {
        $sourcePath = [string]$entry
    }

    if ([string]::IsNullOrWhiteSpace($sourcePath)) {
        continue
    }

    $resolvedSource = Resolve-DeploymentPath -Path $sourcePath -BasePath $configDirectory
    if ([string]::IsNullOrWhiteSpace($destinationName)) {
        $destinationName = [System.IO.Path]::GetFileName($resolvedSource)
    }

    Copy-RequiredFile -Source $resolvedSource -Destination (Join-Path $moduleDefinitionsDestination $destinationName)
    Copy-RequiredFile -Source $resolvedSource -Destination (Join-Path $availableModuleDefinitionsRoot $destinationName)
}

$additionalSqlIncludes = @()
$additionalSqlFiles = @((Get-NestedConfigValue -Config $config -Section 'HostAgentFirst' -Name 'AdditionalSqlFiles' -DefaultValue @()))
foreach ($entry in $additionalSqlFiles) {
    $sourcePath = ''
    $destinationName = ''
    if ($entry -is [hashtable]) {
        $sourcePath = [string]$entry.Source
        $destinationName = [string]$entry.Destination
    }
    else {
        $sourcePath = [string]$entry
    }

    if ([string]::IsNullOrWhiteSpace($sourcePath)) {
        continue
    }

    $resolvedSource = Resolve-DeploymentPath -Path $sourcePath -BasePath $configDirectory
    if ([string]::IsNullOrWhiteSpace($destinationName)) {
        $destinationName = Join-Path 'Customer' ([System.IO.Path]::GetFileName($resolvedSource))
    }

    Copy-RequiredFile -Source $resolvedSource -Destination (Join-Path $sqlRoot $destinationName)
    $additionalSqlIncludes += ':r ' + $destinationName.Replace('/', '\')
}

$bootstrapSql = @'
/*
Package-local HostAgent-first bootstrap.

OpenModulePlatform.Bootstrapper expands these SQLCMD-style includes and patches
database name, bootstrap administrator, and component artifact versions from
bootstrap.local.sample.json.
*/

:r OpenModulePlatform\1-setup-openmoduleplatform.sql
:r OpenModulePlatform\2-initialize-openmoduleplatform.sql
:r OpenModulePlatform.Auth\2-initialize-omp-auth.sql
:r OpenModulePlatform.Portal\1-setup-omp-portal.sql
:r OpenModulePlatform.Portal\2-initialize-omp-portal.sql
:r OpenModulePlatform.Portal\4-ensure-topbar-hover-user-setting.sql
:r OpenModulePlatform.Web.ContentWebAppModule\1-setup-content-webapp.sql
:r OpenModulePlatform.Web.ContentWebAppModule\3-add-server-report-support.sql
:r OpenModulePlatform.Web.ContentWebAppModule\2-initialize-content-webapp.sql
:r OpenModulePlatform.Web.iFrameWebAppModule\1-setup-iframe-webapp.sql
:r OpenModulePlatform.Web.iFrameWebAppModule\2-initialize-iframe-webapp.sql
:r OpenModulePlatform\3-initialize-opendocviewer.sql
:r examples\WebAppModule\1-setup-example-webapp.sql
:r examples\WebAppModule\2-initialize-example-webapp.sql
:r examples\WebAppBlazorModule\1-setup-example-webapp-blazor.sql
:r examples\WebAppBlazorModule\2-initialize-example-webapp-blazor.sql
:r examples\ServiceAppModule\1-setup-example-serviceapp.sql
:r examples\ServiceAppModule\2-initialize-example-serviceapp.sql
:r examples\WorkerAppModule\1-setup-example-workerapp.sql
:r examples\WorkerAppModule\2-initialize-example-workerapp.sql
'@
if ($additionalSqlIncludes.Count -gt 0) {
    $bootstrapSql += [Environment]::NewLine
    $bootstrapSql += '/* Package-specific SQL extensions. */'
    $bootstrapSql += [Environment]::NewLine
    $bootstrapSql += [string]::Join([Environment]::NewLine, $additionalSqlIncludes)
    $bootstrapSql += [Environment]::NewLine
}
$bootstrapSql += [Environment]::NewLine
$bootstrapSql += '/* Final Portal navigation sync after all app instances have been registered. */'
$bootstrapSql += [Environment]::NewLine
$bootstrapSql += ':r OpenModulePlatform.Portal\3-sync-omp-portal-entries.sql'
$bootstrapSql += [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $sqlRoot 'bootstrap-local.sql') -Value $bootstrapSql -Encoding UTF8

Write-Step 'Copying bootstrapper'
Compress-FolderToZip -Source (Join-Path $publishRoot 'OpenModulePlatform.Bootstrapper') -Destination (Join-Path $payloadRoot 'OpenModulePlatform.Bootstrapper.zip')
Copy-Item -LiteralPath (Join-Path $publishRoot 'OpenModulePlatform.Bootstrapper') -Destination (Join-Path $toolsRoot 'OpenModulePlatform.Bootstrapper') -Recurse -Force
$bootstrapConfigEditorSource = Join-Path $RepositoryRoot 'tools\bootstrap-config-editor'
if (Test-Path -LiteralPath $bootstrapConfigEditorSource -PathType Container) {
    Copy-Item -LiteralPath $bootstrapConfigEditorSource -Destination (Join-Path $toolsRoot 'bootstrap-config-editor') -Recurse -Force
}

$rootBootstrapperPublishRoot = Join-Path $buildRoot 'bootstrapper-root'
Invoke-NativeChecked dotnet 'publish' (Join-Path $RepositoryRoot 'OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.csproj') `
    '-c' $Configuration `
    '-o' $rootBootstrapperPublishRoot `
    '-r' 'win-x64' `
    '--self-contained' 'false' `
    '-p:PublishSingleFile=true' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' `
    '-p:DebugType=None' `
    '-p:DebugSymbols=false'
Copy-Item -Path (Join-Path $rootBootstrapperPublishRoot '*') -Destination $packageRoot -Recurse -Force

Write-Step 'Writing bootstrap manifest'
$artifacts = [System.Collections.ArrayList]::new()
foreach ($component in $components) {
    $componentKey = [string]$component.componentKey
    $packageTemplate = [string]$component.packageFileTemplate
    $relativeTemplate = [string]$component.relativePathTemplate
    $componentVersion = [string]$component.version
    if ([string]::IsNullOrWhiteSpace($packageTemplate) -or [string]::IsNullOrWhiteSpace($relativeTemplate)) {
        continue
    }

    $artifactSource = [string]$componentPayloadSources[$componentKey]
    if ([string]::IsNullOrWhiteSpace($artifactSource)) {
        $artifactSource = $packageTemplate.Replace('\', '/')
    }

    $isExample = $packageTemplate -like '*Example*' -or $relativeTemplate -like '*example-*'
    Add-ArtifactEntry -Artifacts $artifacts -Source $artifactSource -Target ($relativeTemplate.Replace('{version}', $componentVersion)) -IsExample $isExample
}
Add-ArtifactEntry -Artifacts $artifacts -Source "payload/$openDocViewerArtifactPackageName" -Target "opendocviewer/web/$openDocViewerVersion"

$additionalArtifactFiles = @((Get-NestedConfigValue -Config $config -Section 'HostAgentFirst' -Name 'AdditionalArtifactFiles' -DefaultValue @()))
Copy-AdditionalArtifactFiles `
    -Entries $additionalArtifactFiles `
    -ConfigDirectory $configDirectory `
    -PayloadRoot $payloadRoot `
    -Artifacts $artifacts `
    -AvailableArtifactsRoot $availableArtifactsRoot `
    -AvailableArtifactPackageFiles $availableArtifactPackageFiles

foreach ($sourceRoot in $developerSourceRoots) {
    Copy-AvailableArtifactPackagesFromManifest `
        -ManifestPath (Join-Path $sourceRoot 'omp-components.json') `
        -ArchiveRoots $artifactArchiveRoots `
        -DestinationRoot $availableArtifactsRoot `
        -CopiedFiles $availableArtifactPackageFiles
}

$versionOverrides = @{}
$versionVariableOverrides = @{}
$hostAgentVersion = [string]($components | Where-Object { $_.componentKey -eq 'omp-hostagent-service' } | Select-Object -First 1).version
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform/2-initialize-openmoduleplatform.sql' -Version $hostAgentVersion
Add-VersionVariableOverride -Overrides $versionVariableOverrides -ScriptPath 'OpenModulePlatform/2-initialize-openmoduleplatform.sql' -VariableName 'WorkerManagerArtifactVersion' -Version ([string]($components | Where-Object { $_.componentKey -eq 'omp-workermanager-service' } | Select-Object -First 1).version)
Add-VersionVariableOverride -Overrides $versionVariableOverrides -ScriptPath 'OpenModulePlatform/2-initialize-openmoduleplatform.sql' -VariableName 'WorkerProcessHostArtifactVersion' -Version ([string]($components | Where-Object { $_.componentKey -eq 'omp-workerprocesshost' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform.Auth/2-initialize-omp-auth.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'omp-auth-web' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform.Portal/2-initialize-omp-portal.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'omp-portal-web' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform.Web.ContentWebAppModule/2-initialize-content-webapp.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'content-webapp' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform.Web.iFrameWebAppModule/2-initialize-iframe-webapp.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'iframe-webapp' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'examples/WebAppModule/2-initialize-example-webapp.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-webapp' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'examples/WebAppBlazorModule/2-initialize-example-webapp-blazor.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-webapp-blazor' } | Select-Object -First 1).version)
Add-VersionVariableOverride -Overrides $versionVariableOverrides -ScriptPath 'examples/ServiceAppModule/2-initialize-example-serviceapp.sql' -VariableName 'WebArtifactVersion' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-serviceapp-web' } | Select-Object -First 1).version)
Add-VersionVariableOverride -Overrides $versionVariableOverrides -ScriptPath 'examples/ServiceAppModule/2-initialize-example-serviceapp.sql' -VariableName 'ServiceArtifactVersion' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-serviceapp-service' } | Select-Object -First 1).version)
Add-VersionVariableOverride -Overrides $versionVariableOverrides -ScriptPath 'examples/WorkerAppModule/2-initialize-example-workerapp.sql' -VariableName 'WebArtifactVersion' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-workerapp-web' } | Select-Object -First 1).version)
Add-VersionVariableOverride -Overrides $versionVariableOverrides -ScriptPath 'examples/WorkerAppModule/2-initialize-example-workerapp.sql' -VariableName 'WorkerArtifactVersion' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-workerapp-worker' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform/3-initialize-opendocviewer.sql' -Version $openDocViewerVersion

$runtimeRoot = [string](Get-ConfigValue -Config $config -Name 'RuntimeRoot' -DefaultValue 'E:\OMP')
$webRoot = [string](Get-ConfigValue -Config $config -Name 'WebRoot' -DefaultValue (Join-DeploymentPath -Root $runtimeRoot -Child 'Sites'))
$webAppsRoot = [string](Get-ConfigValue -Config $config -Name 'WebAppsRoot' -DefaultValue (Join-DeploymentPath -Root $runtimeRoot -Child 'WebApps'))
$servicesRoot = [string](Get-ConfigValue -Config $config -Name 'ServicesRoot' -DefaultValue (Join-DeploymentPath -Root $runtimeRoot -Child 'Services'))
$artifactStoreRoot = [string](Get-ConfigValue -Config $config -Name 'ArtifactStoreRoot' -DefaultValue (Join-DeploymentPath -Root $runtimeRoot -Child 'ArtifactStore'))
$artifactCacheRoot = [string](Get-ConfigValue -Config $config -Name 'ArtifactCacheRoot' -DefaultValue (Join-DeploymentPath -Root $runtimeRoot -Child 'ArtifactCache'))
$artifactZipImportEnabled = [bool](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportEnabled' -DefaultValue $false)
$artifactZipImportPath = [string](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportPath' -DefaultValue (Join-DeploymentPath -Root $runtimeRoot -Child 'ArtifactImports'))
$artifactZipImportProcessedPath = [string](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportProcessedPath' -DefaultValue '')
$artifactZipImportFailedPath = [string](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportFailedPath' -DefaultValue '')
$webAppDataProtectionKeyPath = [string](Get-ConfigValue -Config $config -Name 'WebAppDataProtectionKeyPath' -DefaultValue (Join-DeploymentPath -Root $runtimeRoot -Child 'DataProtectionKeys'))
$portalPhysicalPath = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'PortalPhysicalPath' -DefaultValue '')
if ([string]::IsNullOrWhiteSpace($portalPhysicalPath)) {
    $portalPhysicalPath = Join-DeploymentPath -Root $webRoot -Child 'Portal'
}
$iisSiteName = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'SiteName' -DefaultValue 'OpenModulePlatform')
$iisProtocol = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'Protocol' -DefaultValue 'http')
$iisPort = [int](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'Port' -DefaultValue 80)
$iisHostHeader = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'HostHeader' -DefaultValue '')
$contentWebAppPath = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'ContentWebAppPath' -DefaultValue 'content')
$contentWebAppServerReportsPath = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'ServerReportsPath' -DefaultValue 'App_Data/ContentReports')
$contentWebAppHtmlFilesPath = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'HtmlFilesPath' -DefaultValue 'App_Data/ContentPages')
$contentWebAppSharedServerReportsPath = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'SharedServerReportsPath' -DefaultValue '')
$contentWebAppSharedHtmlFilesPath = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'SharedHtmlFilesPath' -DefaultValue '')
$contentWebAppFileMirrors = Get-ContentWebAppFileMirrors `
    -SharedServerReportsPath $contentWebAppSharedServerReportsPath `
    -SharedHtmlFilesPath $contentWebAppSharedHtmlFilesPath `
    -ServerReportsPath $contentWebAppServerReportsPath `
    -HtmlFilesPath $contentWebAppHtmlFilesPath `
    -WebAppsRoot $webAppsRoot `
    -ContentWebAppPath $contentWebAppPath
$hostAgentServiceName = [string](Get-NestedConfigValue -Config $config -Section 'Services' -Name 'HostAgent' -DefaultValue 'OMP.HostAgent')
$additionalServiceNamesToRemove = [System.Collections.Generic.List[string]]::new()
foreach ($name in @(
        [string](Get-NestedConfigValue -Config $config -Section 'Services' -Name 'WorkerManager' -DefaultValue 'OMP.WorkerManager'),
        [string](Get-NestedConfigValue -Config $config -Section 'Services' -Name 'ExampleService' -DefaultValue 'OMP.Service.ExampleServiceAppModule'),
        'OpenModulePlatform.HostAgent',
        'OpenModulePlatform.WorkerManager',
        'OpenModulePlatform.Service.ExampleServiceAppModule'
    )) {
    if (-not [string]::IsNullOrWhiteSpace($name) -and -not $additionalServiceNamesToRemove.Contains($name.Trim())) {
        $additionalServiceNamesToRemove.Add($name.Trim())
    }
}
foreach ($name in @((Get-NestedConfigValue -Config $config -Section 'HostAgentFirst' -Name 'AdditionalServiceNamesToRemove' -DefaultValue @()))) {
    $nameText = [string]$name
    if (-not [string]::IsNullOrWhiteSpace($nameText) -and -not $additionalServiceNamesToRemove.Contains($nameText.Trim())) {
        $additionalServiceNamesToRemove.Add($nameText.Trim())
    }
}
$runAsUser = [string](Get-ConfigValue -Config $config -Name 'RunAsUser' -DefaultValue '')
$runAsPassword = [string](Get-ConfigValue -Config $config -Name 'RunAsPassword' -DefaultValue '')
$iisAppPoolOverrides = Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'IisAppPoolOverrides' -DefaultValue @{}
$bootstrapPrincipal = @((Get-ConfigValue -Config $config -Name 'BootstrapPortalAdminPrincipals' -DefaultValue @('DOMAIN\UserOrGroup')))[0]
$bootstrapPrincipalType = [string](Get-ConfigValue -Config $config -Name 'BootstrapPortalAdminPrincipalType' -DefaultValue 'ADUser')
$sqlServer = [string](Get-ConfigValue -Config $config -Name 'SqlServer' -DefaultValue 'localhost')
$database = [string](Get-ConfigValue -Config $config -Name 'Database' -DefaultValue 'OpenModulePlatform')
$sqlAuthentication = [string](Get-ConfigValue -Config $config -Name 'SqlAuthentication' -DefaultValue 'Integrated')
$sqlIntegrated = -not [string]::Equals($sqlAuthentication, 'SqlLogin', [StringComparison]::OrdinalIgnoreCase)
$includeExampleApps = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallExamples' -DefaultValue $true)
$hostAgentHostKey = [string](Get-ConfigValue -Config $config -Name 'HostKey' -DefaultValue '')
if ([string]::IsNullOrWhiteSpace($hostAgentHostKey)) {
    $hostAgentHostKey = 'sample-host'
}
$hostAgentHostName = [string](Get-ConfigValue -Config $config -Name 'HostName' -DefaultValue '')
$developerSourceRoot = if ([string]::IsNullOrWhiteSpace($developerSourceRootText)) { $RepositoryRoot } else { $developerSourceRootText }
$developerPackageConfigPath = [string](Get-NestedConfigValue -Config $config -Section 'DeveloperSource' -Name 'PackageConfigPath' -DefaultValue $ConfigPath)
$developerPackageOutputRoot = [string](Get-NestedConfigValue -Config $config -Section 'DeveloperSource' -Name 'PackageOutputRoot' -DefaultValue $OutputRoot)
$portableEncryptionKey = [string](Get-NestedConfigValue -Config $config -Section 'Security' -Name 'PortableEncryptionKey' -DefaultValue '')
$portableEncryptionKeyEnvironmentVariable = [string](Get-NestedConfigValue -Config $config -Section 'Security' -Name 'PortableEncryptionKeyEnvironmentVariable' -DefaultValue '')

$bootstrapConfig = [ordered]@{
    schema = 'OpenModulePlatform.HostAgentFirstBootstrap.v1'
    security = [ordered]@{
        portableEncryptionKey = $portableEncryptionKey
        portableEncryptionKeyEnvironmentVariable = $portableEncryptionKeyEnvironmentVariable
    }
    developerSource = [ordered]@{
        sourceRoot = $developerSourceRoot
        packageConfigPath = $developerPackageConfigPath
        packageOutputRoot = $developerPackageOutputRoot
    }
    sql = [ordered]@{
        enabled = $true
        server = $sqlServer
        database = $database
        integratedSecurity = $sqlIntegrated
        userId = [string](Get-ConfigValue -Config $config -Name 'SqlUser' -DefaultValue '')
        password = [string](Get-ConfigValue -Config $config -Name 'SqlPassword' -DefaultValue '')
        trustServerCertificate = $true
        createDatabase = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'CreateDatabase' -DefaultValue $false)
        commandTimeoutSeconds = 3600
        bootstrapPortalAdminPrincipal = [string]$bootstrapPrincipal
        bootstrapPortalAdminPrincipalType = $bootstrapPrincipalType
        scripts = @(
            [ordered]@{
                path = 'sql/bootstrap-local.sql'
            }
        )
        artifactVersionOverrides = $versionOverrides
        artifactVersionVariableOverrides = $versionVariableOverrides
    }
    artifactStoreRoot = $artifactStoreRoot
    includeExampleApps = $includeExampleApps
    artifacts = @($artifacts)
    hostAgent = [ordered]@{
        enabled = $true
        serviceName = $hostAgentServiceName
        additionalServiceNamesToRemove = @($additionalServiceNamesToRemove)
        displayName = 'OpenModulePlatform HostAgent'
        description = 'OpenModulePlatform artifact provisioning agent.'
        serviceAccountName = $runAsUser
        serviceAccountPassword = $runAsPassword
        installPath = (Join-DeploymentPath -Root $servicesRoot -Child 'HostAgent')
        packagePath = 'payload/OpenModulePlatform.HostAgent.WindowsService.zip'
        backupExistingInstall = $true
        startService = $true
        settingsFileName = 'appsettings.Production.json'
        localArtifactCacheRoot = $artifactCacheRoot
        hostKey = $hostAgentHostKey
        hostName = $hostAgentHostName
        refreshSeconds = 30
        deployWebApps = $true
        iisSiteName = $iisSiteName
        ensureIisSite = $true
        iisBindingProtocol = $iisProtocol
        iisBindingPort = $iisPort
        iisBindingHostHeader = $iisHostHeader
        webAppsRoot = $webAppsRoot
        portalPhysicalPath = $portalPhysicalPath
        iisAppPoolNamePrefix = 'OMP_'
        iisAppPoolUserName = $runAsUser
        iisAppPoolPassword = $runAsPassword
        iisAppPoolOverrides = $iisAppPoolOverrides
        deployServiceApps = $true
        servicesRoot = $servicesRoot
        credentialStore = [ordered]@{
            automationMode = ''
            filePath = ''
            protectionScope = 'LocalMachine'
            entropyPurpose = 'OpenModulePlatform.HostAgent.CredentialStore.v1'
        }
        appSettings = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = '{SqlConnectionString}'
            }
            HostAgent = [ordered]@{
                ServiceName = $hostAgentServiceName
                Version = $hostAgentVersion
                HostKey = '{HostAgent.HostKey}'
                HostName = '{HostAgent.HostName}'
                RefreshSeconds = 30
                CentralArtifactRoot = '{ArtifactStoreRoot}'
                LocalArtifactCacheRoot = '{HostAgent.LocalArtifactCacheRoot}'
                MaterializeTemplates = $true
                ProcessHostDeployments = $true
                ProvisionAppInstanceArtifacts = $true
                ProvisionExplicitRequirements = $true
                ArtifactZipImport = [ordered]@{
                    IsEnabled = $artifactZipImportEnabled
                    ImportPath = $artifactZipImportPath
                    ProcessedPath = $artifactZipImportProcessedPath
                    FailedPath = $artifactZipImportFailedPath
                    MaxFilesPerCycle = 10
                    CopyConfigurationFilesFromPreviousVersion = $true
                }
                DeployWebApps = $true
                IisSiteName = $iisSiteName
                EnsureIisSite = $true
                IisBindingProtocol = $iisProtocol
                IisBindingPort = $iisPort
                IisBindingHostHeader = $iisHostHeader
                WebAppsRoot = $webAppsRoot
                PortalPhysicalPath = $portalPhysicalPath
                IisAppPoolNamePrefix = 'OMP_'
                IisAppPoolUserName = $runAsUser
                IisAppPoolPasswordCredentialKey = ''
                IisAppPoolOverrides = @{}
                WebAppDataProtectionKeyPath = $webAppDataProtectionKeyPath
                DeployServiceApps = $true
                ServicesRoot = $servicesRoot
                FileMirrors = @($contentWebAppFileMirrors)
                SelfUpgrade = [ordered]@{
                    IsEnabled = $true
                    InstallRoot = $servicesRoot
                    ServiceNamePrefix = $hostAgentServiceName
                    ServiceAccountName = $runAsUser
                    ServiceAccountPasswordCredentialKey = ''
                    TakeoverStopTimeoutSeconds = 45
                    DeletePreviousServiceAfterTakeover = $true
                    StartPreparedService = $true
                }
                CredentialStore = [ordered]@{
                    AutomationMode = ''
                    FilePath = ''
                    ProtectionScope = 'LocalMachine'
                    EntropyPurpose = 'OpenModulePlatform.HostAgent.CredentialStore.v1'
                }
                EnableRpc = $true
                RpcPipeName = ''
                RpcRequestTimeoutSeconds = 60
            }
        }
    }
}

$configsRoot = Join-Path $packageRoot 'configs'
New-Item -ItemType Directory -Force -Path $configsRoot | Out-Null
$bootstrapConfigJson = $bootstrapConfig | ConvertTo-Json -Depth 18
$rootBootstrapConfigPath = Join-Path $packageRoot 'bootstrap.local.sample.json'
$profileBootstrapConfigPath = Join-Path $configsRoot 'bootstrap.local.sample.json'
$bootstrapConfigJson | Set-Content -LiteralPath $profileBootstrapConfigPath -Encoding UTF8
# Keep the root-level file for command-line and older package automation while
# the GUI now locks onto the configs\*.json profile matching the local computer.
$bootstrapConfigJson | Set-Content -LiteralPath $rootBootstrapConfigPath -Encoding UTF8

$cmd = @'
@echo off
setlocal
set ROOT=%~dp0
"%ROOT%tools\OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.exe" --gui --config-dir "%ROOT%configs"
endlocal
'@
Set-Content -LiteralPath (Join-Path $packageRoot 'install-hostagent-first.cmd') -Value $cmd -Encoding ASCII

$consoleCmd = @'
@echo off
setlocal
set ROOT=%~dp0
"%ROOT%tools\OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.exe" --config "%ROOT%bootstrap.local.sample.json"
endlocal
'@
Set-Content -LiteralPath (Join-Path $packageRoot 'install-hostagent-first-console.cmd') -Value $consoleCmd -Encoding ASCII

$uninstallScript = @'
param(
    [string]$ConfigPath = '',
    [switch]$RemoveRuntimeFiles,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $scriptRoot 'bootstrap.local.sample.json'
}

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Resolve-ConfiguredPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Confirm-Action {
    param([string]$Prompt)

    if ($Yes) {
        return $true
    }

    Write-Host "$Prompt [Y/N, default N]: " -NoNewline
    $answer = Read-Host
    return [string]::Equals($answer.Trim(), 'Y', [StringComparison]::OrdinalIgnoreCase)
}

function Get-PropertyValue {
    param(
        $Object,
        [string]$Name,
        $DefaultValue = ''
    )

    if ($null -eq $Object) {
        return $DefaultValue
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $DefaultValue
    }

    return $property.Value
}

function Get-ServiceExecutablePath {
    param([string]$PathName)

    if ([string]::IsNullOrWhiteSpace($PathName)) {
        return ''
    }

    $trimmed = $PathName.Trim()
    if ($trimmed.StartsWith('"', [StringComparison]::Ordinal)) {
        $endQuote = $trimmed.IndexOf('"', 1)
        if ($endQuote -gt 1) {
            return $trimmed.Substring(1, $endQuote - 1)
        }
    }

    $exeIndex = $trimmed.IndexOf('.exe', [StringComparison]::OrdinalIgnoreCase)
    if ($exeIndex -ge 0) {
        return $trimmed.Substring(0, $exeIndex + 4)
    }

    return $trimmed
}

function Stop-AndDeleteService {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return
    }

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping service $Name"
        Stop-Service -Name $Name -Force -ErrorAction Stop
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
    }

    if ($null -ne (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
        Write-Host "Deleting service $Name"
        & sc.exe delete $Name | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to delete service '$Name'. sc.exe exit code: $LASTEXITCODE"
        }
    }
}

function Remove-ConfiguredDirectory {
    param([string]$Path)

    $resolved = Resolve-ConfiguredPath -Path $Path
    if ([string]::IsNullOrWhiteSpace($resolved) -or -not (Test-Path -LiteralPath $resolved)) {
        return
    }

    Write-Host "Removing $resolved"
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Bootstrap config was not found: $ConfigPath"
}

$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$hostAgent = $config.hostAgent
$serviceName = [string](Get-PropertyValue -Object $hostAgent -Name 'serviceName')
$additionalServiceNamesToRemove = @((Get-PropertyValue -Object $hostAgent -Name 'additionalServiceNamesToRemove' -DefaultValue @()))
$servicesRoot = [string](Get-PropertyValue -Object $hostAgent -Name 'servicesRoot')
$iisSiteName = [string](Get-PropertyValue -Object $hostAgent -Name 'iisSiteName')
$appPoolPrefix = [string](Get-PropertyValue -Object $hostAgent -Name 'iisAppPoolNamePrefix' -DefaultValue 'OMP_')

Write-Host 'OpenModulePlatform HostAgent-first uninstall'
Write-Host "Config:              $ConfigPath"
Write-Host "HostAgent service:   $serviceName"
Write-Host "IIS site:            $iisSiteName"
Write-Host "Remove runtime files: $RemoveRuntimeFiles"

if (-not (Confirm-Action -Prompt 'Continue with uninstall')) {
    Write-Host 'Uninstall cancelled.'
    exit 2
}

Write-Step 'Removing Windows services'
$serviceNames = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($serviceName)) {
    $serviceNames.Add($serviceName)
}
foreach ($configuredServiceName in $additionalServiceNamesToRemove) {
    $configuredServiceNameText = [string]$configuredServiceName
    if (-not [string]::IsNullOrWhiteSpace($configuredServiceNameText) -and -not $serviceNames.Contains($configuredServiceNameText.Trim())) {
        $serviceNames.Add($configuredServiceNameText.Trim())
    }
}

$resolvedServicesRoot = Resolve-ConfiguredPath -Path $servicesRoot
if (-not [string]::IsNullOrWhiteSpace($resolvedServicesRoot)) {
    $normalizedRoot = $resolvedServicesRoot.TrimEnd('\') + '\'
    $runtimeServices = @(Get-CimInstance Win32_Service | Where-Object {
            $servicePath = Get-ServiceExecutablePath -PathName $_.PathName
            -not [string]::IsNullOrWhiteSpace($servicePath) -and
            $servicePath.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)
        })
    foreach ($runtimeService in $runtimeServices) {
        if (-not $serviceNames.Contains([string]$runtimeService.Name)) {
            $serviceNames.Add([string]$runtimeService.Name)
        }
    }
}

foreach ($name in @($serviceNames)) {
    Stop-AndDeleteService -Name $name
}

Write-Step 'Removing IIS site and app pools'
$appcmd = Join-Path $env:windir 'system32\inetsrv\appcmd.exe'
if (Test-Path -LiteralPath $appcmd -PathType Leaf) {
    if (-not [string]::IsNullOrWhiteSpace($iisSiteName)) {
        & $appcmd list site /name:$iisSiteName | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Deleting IIS site $iisSiteName"
            & $appcmd delete site /site.name:$iisSiteName | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to delete IIS site '$iisSiteName'. appcmd.exe exit code: $LASTEXITCODE"
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($appPoolPrefix)) {
        $appPools = @(& $appcmd list apppool /text:name | Where-Object {
                $_.StartsWith($appPoolPrefix, [StringComparison]::OrdinalIgnoreCase)
            })
        foreach ($appPool in $appPools) {
            Write-Host "Deleting IIS app pool $appPool"
            & $appcmd delete apppool /apppool.name:$appPool | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to delete IIS app pool '$appPool'. appcmd.exe exit code: $LASTEXITCODE"
            }
        }
    }
}
else {
    Write-Warning "IIS appcmd.exe was not found. Skipping IIS cleanup."
}

if ($RemoveRuntimeFiles) {
    Write-Step 'Removing runtime folders'
    Remove-ConfiguredDirectory -Path ([string](Get-PropertyValue -Object $hostAgent -Name 'portalPhysicalPath'))
    Remove-ConfiguredDirectory -Path ([string](Get-PropertyValue -Object $hostAgent -Name 'webAppsRoot'))
    Remove-ConfiguredDirectory -Path ([string](Get-PropertyValue -Object $hostAgent -Name 'localArtifactCacheRoot'))
    Remove-ConfiguredDirectory -Path ([string](Get-PropertyValue -Object $config -Name 'artifactStoreRoot'))
    Remove-ConfiguredDirectory -Path ([string](Get-PropertyValue -Object $hostAgent -Name 'servicesRoot'))

    $hostAgentSettings = Get-PropertyValue -Object (Get-PropertyValue -Object $hostAgent -Name 'appSettings') -Name 'HostAgent' -DefaultValue $null
    Remove-ConfiguredDirectory -Path ([string](Get-PropertyValue -Object $hostAgentSettings -Name 'WebAppDataProtectionKeyPath'))
    $artifactZipImport = Get-PropertyValue -Object $hostAgentSettings -Name 'ArtifactZipImport' -DefaultValue $null
    Remove-ConfiguredDirectory -Path ([string](Get-PropertyValue -Object $artifactZipImport -Name 'ImportPath'))
    Remove-ConfiguredDirectory -Path ([string](Get-PropertyValue -Object $artifactZipImport -Name 'ProcessedPath'))
    Remove-ConfiguredDirectory -Path ([string](Get-PropertyValue -Object $artifactZipImport -Name 'FailedPath'))
}

Write-Host ''
Write-Host 'OpenModulePlatform local runtime uninstall completed.' -ForegroundColor Green
'@
Set-Content -LiteralPath (Join-Path $packageRoot 'uninstall-hostagent-first.ps1') -Value $uninstallScript -Encoding UTF8

$uninstallCmd = @'
@echo off
setlocal
set ROOT=%~dp0
powershell.exe -NoLogo -NoProfile -File "%ROOT%uninstall-hostagent-first.ps1" -ConfigPath "%ROOT%bootstrap.local.sample.json"
endlocal
'@
Set-Content -LiteralPath (Join-Path $packageRoot 'uninstall-hostagent-first.cmd') -Value $uninstallCmd -Encoding ASCII

$uninstallCleanCmd = @'
@echo off
setlocal
set ROOT=%~dp0
powershell.exe -NoLogo -NoProfile -File "%ROOT%uninstall-hostagent-first.ps1" -ConfigPath "%ROOT%bootstrap.local.sample.json" -RemoveRuntimeFiles
endlocal
'@
Set-Content -LiteralPath (Join-Path $packageRoot 'uninstall-hostagent-first-clean.cmd') -Value $uninstallCleanCmd -Encoding ASCII

$manifest = [ordered]@{
    schema = 'OpenModulePlatform.HostAgentFirstPackage.v1'
    createdUtc = [DateTime]::UtcNow.ToString('o')
    version = $Version
    repository = [ordered]@{
        key = [string]$componentManifest.repositoryKey
        version = [string]$componentManifest.repositoryVersion
    }
    openDocViewerVersion = $openDocViewerVersion
    bootstrapConfig = 'configs/bootstrap.local.sample.json'
    bootstrapper = 'tools/OpenModulePlatform.Bootstrapper/OpenModulePlatform.Bootstrapper.exe'
    payloads = @($artifacts | ForEach-Object { $_.source })
    availableModuleDefinitions = @(
        Get-ChildItem -LiteralPath $availableModuleDefinitionsRoot -Filter '*.json' -File |
            Sort-Object Name |
            ForEach-Object { 'available-module-definitions/' + $_.Name }
    )
    availableArtifactPackages = @(
        Get-ChildItem -LiteralPath $availableArtifactsRoot -Filter '*.zip' -File |
            Sort-Object Name |
            ForEach-Object { 'available-artifacts/' + $_.Name }
    )
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding UTF8

Copy-RequiredFile -Source (Join-Path $RepositoryRoot 'docs\HOST_AGENT_FIRST_INSTALL.md') -Destination (Join-Path $packageRoot 'INSTALLATION.md')

if ($SkipZip) {
    if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
        Remove-Item -LiteralPath $zipPath -Force
    }
}
else {
    Compress-PackageRootToZip -PackageRoot $packageRoot -Destination $zipPath
}

if (-not $KeepStaging) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'OpenModulePlatform HostAgent-first package created.' -ForegroundColor Green
Write-Host "Package root: $packageRoot"
if ($SkipZip) {
    Write-Host 'Package zip:  skipped'
}
else {
    Write-Host "Package zip:  $zipPath"
}
