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

    $config = Import-PowerShellDataFile -LiteralPath $Path
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
        [bool]$IsExample = $false
    )

    [void]$Artifacts.Add([ordered]@{
            source = $Source.Replace('\', '/')
            target = $Target.Replace('\', '/')
            overwrite = $true
            removeRuntimeConfigurationFiles = $true
            isExample = $IsExample
        })
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
$buildRoot = Join-Path $packageRoot '.build'
$zipPath = Join-Path $OutputRoot ("OpenModulePlatformHostAgentFirst-$Version.zip")

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $sqlRoot -Force | Out-Null
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null

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
foreach ($component in $components) {
    $packageTemplate = [string]$component.packageFileTemplate
    if ([string]::IsNullOrWhiteSpace($packageTemplate)) {
        continue
    }

    $projectName = Get-ProjectNameFromComponent -RepositoryRoot $RepositoryRoot -Component $component
    $source = Join-Path $publishRoot $projectName
    $destination = Join-Path $packageRoot $packageTemplate
    Compress-FolderToZip -Source $source -Destination $destination
}

$openDocViewerPackageZip = [string](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'OpenDocViewerPackageZip' -DefaultValue '')
$openDocViewerPackageZip = Resolve-DeploymentPath -Path $openDocViewerPackageZip -BasePath $configDirectory
$openDocViewerVersion = [string](Get-NestedConfigValue -Config $config -Section 'OpenDocViewer' -Name 'Version' -DefaultValue '')
if ([string]::IsNullOrWhiteSpace($openDocViewerVersion)) {
    $openDocViewerVersion = Get-NodePackageVersion -PackageRoot $OpenDocViewerRoot -DisplayName 'OpenDocViewer'
}

Write-Step 'Publishing OpenDocViewer'
if (-not (Test-Path -LiteralPath $OpenDocViewerRoot -PathType Container)) {
    throw "OpenDocViewer repository root was not found: $OpenDocViewerRoot"
}
Write-Host "OpenDocViewer version: $openDocViewerVersion"

if (-not [string]::IsNullOrWhiteSpace($openDocViewerPackageZip)) {
    Copy-RequiredFile -Source $openDocViewerPackageZip -Destination (Join-Path $payloadRoot 'OpenDocViewer.dist.zip')
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
    Compress-FolderToZip -Source (Join-Path $OpenDocViewerRoot 'dist') -Destination (Join-Path $payloadRoot 'OpenDocViewer.dist.zip')
}

Write-Step 'Copying SQL scripts'
$sqlFiles = @(
    @{ Source = 'sql\1-setup-openmoduleplatform.sql'; Destination = 'OpenModulePlatform\1-setup-openmoduleplatform.sql' },
    @{ Source = 'sql\2-initialize-openmoduleplatform.sql'; Destination = 'OpenModulePlatform\2-initialize-openmoduleplatform.sql' },
    @{ Source = 'sql\3-initialize-opendocviewer.sql'; Destination = 'OpenModulePlatform\3-initialize-opendocviewer.sql' },
    @{ Source = 'OpenModulePlatform.Auth\sql\2-initialize-omp-auth.sql'; Destination = 'OpenModulePlatform.Auth\2-initialize-omp-auth.sql' },
    @{ Source = 'OpenModulePlatform.Portal\sql\1-setup-omp-portal.sql'; Destination = 'OpenModulePlatform.Portal\1-setup-omp-portal.sql' },
    @{ Source = 'OpenModulePlatform.Portal\sql\2-initialize-omp-portal.sql'; Destination = 'OpenModulePlatform.Portal\2-initialize-omp-portal.sql' },
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
Set-Content -LiteralPath (Join-Path $sqlRoot 'bootstrap-local.sql') -Value $bootstrapSql -Encoding UTF8

Write-Step 'Copying bootstrapper'
Compress-FolderToZip -Source (Join-Path $publishRoot 'OpenModulePlatform.Bootstrapper') -Destination (Join-Path $payloadRoot 'OpenModulePlatform.Bootstrapper.zip')
Copy-Item -LiteralPath (Join-Path $publishRoot 'OpenModulePlatform.Bootstrapper') -Destination (Join-Path $toolsRoot 'OpenModulePlatform.Bootstrapper') -Recurse -Force

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
    $packageTemplate = [string]$component.packageFileTemplate
    $relativeTemplate = [string]$component.relativePathTemplate
    $componentVersion = [string]$component.version
    if ([string]::IsNullOrWhiteSpace($packageTemplate) -or [string]::IsNullOrWhiteSpace($relativeTemplate)) {
        continue
    }

    $isExample = $packageTemplate -like '*Example*' -or $relativeTemplate -like '*example-*'
    Add-ArtifactEntry -Artifacts $artifacts -Source $packageTemplate -Target ($relativeTemplate.Replace('{version}', $componentVersion)) -IsExample $isExample
}
Add-ArtifactEntry -Artifacts $artifacts -Source 'payload/OpenDocViewer.dist.zip' -Target "opendocviewer/web/$openDocViewerVersion"

$versionOverrides = @{}
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform.Auth/2-initialize-omp-auth.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'omp-auth-web' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform.Portal/2-initialize-omp-portal.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'omp-portal-web' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform.Web.ContentWebAppModule/2-initialize-content-webapp.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'content-webapp' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform.Web.iFrameWebAppModule/2-initialize-iframe-webapp.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'iframe-webapp' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'examples/WebAppModule/2-initialize-example-webapp.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-webapp' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'examples/WebAppBlazorModule/2-initialize-example-webapp-blazor.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-webapp-blazor' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'examples/ServiceAppModule/2-initialize-example-serviceapp.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-serviceapp-web' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'examples/WorkerAppModule/2-initialize-example-workerapp.sql' -Version ([string]($components | Where-Object { $_.componentKey -eq 'example-workerapp-web' } | Select-Object -First 1).version)
Add-VersionOverride -Overrides $versionOverrides -ScriptPath 'OpenModulePlatform/3-initialize-opendocviewer.sql' -Version $openDocViewerVersion

$runtimeRoot = [string](Get-ConfigValue -Config $config -Name 'RuntimeRoot' -DefaultValue 'E:\OMP')
$webRoot = [string](Get-ConfigValue -Config $config -Name 'WebRoot' -DefaultValue (Join-Path $runtimeRoot 'Sites'))
$webAppsRoot = [string](Get-ConfigValue -Config $config -Name 'WebAppsRoot' -DefaultValue (Join-Path $runtimeRoot 'WebApps'))
$servicesRoot = [string](Get-ConfigValue -Config $config -Name 'ServicesRoot' -DefaultValue (Join-Path $runtimeRoot 'Services'))
$artifactStoreRoot = [string](Get-ConfigValue -Config $config -Name 'ArtifactStoreRoot' -DefaultValue (Join-Path $runtimeRoot 'ArtifactStore'))
$artifactCacheRoot = [string](Get-ConfigValue -Config $config -Name 'ArtifactCacheRoot' -DefaultValue (Join-Path $runtimeRoot 'ArtifactCache'))
$artifactZipImportEnabled = [bool](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportEnabled' -DefaultValue $false)
$artifactZipImportPath = [string](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportPath' -DefaultValue (Join-Path $runtimeRoot 'ArtifactImports'))
$artifactZipImportProcessedPath = [string](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportProcessedPath' -DefaultValue '')
$artifactZipImportFailedPath = [string](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportFailedPath' -DefaultValue '')
$webAppDataProtectionKeyPath = [string](Get-ConfigValue -Config $config -Name 'WebAppDataProtectionKeyPath' -DefaultValue (Join-Path $runtimeRoot 'DataProtectionKeys'))
$portalPhysicalPath = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'PortalPhysicalPath' -DefaultValue '')
if ([string]::IsNullOrWhiteSpace($portalPhysicalPath)) {
    $portalPhysicalPath = Join-Path $webRoot 'Portal'
}
$iisSiteName = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'SiteName' -DefaultValue 'OpenModulePlatform')
$iisProtocol = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'Protocol' -DefaultValue 'http')
$iisPort = [int](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'Port' -DefaultValue 80)
$iisHostHeader = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'HostHeader' -DefaultValue '')
$hostAgentServiceName = [string](Get-NestedConfigValue -Config $config -Section 'Services' -Name 'HostAgent' -DefaultValue 'OpenModulePlatform.HostAgent')
$runAsUser = [string](Get-ConfigValue -Config $config -Name 'RunAsUser' -DefaultValue '')
$runAsPassword = [string](Get-ConfigValue -Config $config -Name 'RunAsPassword' -DefaultValue '')
$bootstrapPrincipal = @((Get-ConfigValue -Config $config -Name 'BootstrapPortalAdminPrincipals' -DefaultValue @('DOMAIN\UserOrGroup')))[0]
$bootstrapPrincipalType = [string](Get-ConfigValue -Config $config -Name 'BootstrapPortalAdminPrincipalType' -DefaultValue 'ADUser')
$sqlServer = [string](Get-ConfigValue -Config $config -Name 'SqlServer' -DefaultValue 'localhost')
$database = [string](Get-ConfigValue -Config $config -Name 'Database' -DefaultValue 'OpenModulePlatform')
$sqlAuthentication = [string](Get-ConfigValue -Config $config -Name 'SqlAuthentication' -DefaultValue 'Integrated')
$sqlIntegrated = -not [string]::Equals($sqlAuthentication, 'SqlLogin', [StringComparison]::OrdinalIgnoreCase)
$includeExampleApps = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallExamples' -DefaultValue $true)

$bootstrapConfig = [ordered]@{
    schema = 'OpenModulePlatform.HostAgentFirstBootstrap.v1'
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
    }
    artifactStoreRoot = $artifactStoreRoot
    includeExampleApps = $includeExampleApps
    artifacts = @($artifacts)
    hostAgent = [ordered]@{
        enabled = $true
        serviceName = $hostAgentServiceName
        displayName = 'OpenModulePlatform HostAgent'
        description = 'OpenModulePlatform artifact provisioning agent.'
        serviceAccountName = $runAsUser
        serviceAccountPassword = $runAsPassword
        installPath = (Join-Path $servicesRoot 'HostAgent')
        packagePath = 'payload/OpenModulePlatform.HostAgent.WindowsService.zip'
        backupExistingInstall = $true
        startService = $true
        settingsFileName = 'appsettings.Production.json'
        localArtifactCacheRoot = $artifactCacheRoot
        hostKey = [string](Get-ConfigValue -Config $config -Name 'HostKey' -DefaultValue '')
        hostName = [string](Get-ConfigValue -Config $config -Name 'HostName' -DefaultValue '')
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
        deployServiceApps = $true
        servicesRoot = $servicesRoot
        appSettings = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = '{SqlConnectionString}'
            }
            HostAgent = [ordered]@{
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
                IisAppPoolPassword = $runAsPassword
                WebAppDataProtectionKeyPath = $webAppDataProtectionKeyPath
                DeployServiceApps = $true
                ServicesRoot = $servicesRoot
                EnableRpc = $true
                RpcPipeName = ''
                RpcRequestTimeoutSeconds = 60
            }
        }
    }
}

$bootstrapConfig | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath (Join-Path $packageRoot 'bootstrap.local.sample.json') -Encoding UTF8

$cmd = @'
@echo off
setlocal
set ROOT=%~dp0
"%ROOT%tools\OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.exe" --gui --config "%ROOT%bootstrap.local.sample.json"
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

$manifest = [ordered]@{
    schema = 'OpenModulePlatform.HostAgentFirstPackage.v1'
    createdUtc = [DateTime]::UtcNow.ToString('o')
    version = $Version
    repository = [ordered]@{
        key = [string]$componentManifest.repositoryKey
        version = [string]$componentManifest.repositoryVersion
    }
    openDocViewerVersion = $openDocViewerVersion
    bootstrapConfig = 'bootstrap.local.sample.json'
    bootstrapper = 'tools/OpenModulePlatform.Bootstrapper/OpenModulePlatform.Bootstrapper.exe'
    payloads = @($artifacts | ForEach-Object { $_.source })
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
