# File: scripts/deployment/package-omp-suite.ps1
[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'omp-suite.local.psd1'),
    [string]$RepositoryRoot = '',
    [string]$OpenDocViewerRoot = '',
    [string]$OutputRoot = '',
    [string]$Version = '',
    [string]$Configuration = '',
    [switch]$SkipRestore,
    [switch]$SkipOpenDocViewerBuild,
    [switch]$SkipOpenDocViewerNpmInstall,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Import-DeploymentConfig {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
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
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Force
    }

    Compress-Archive -Path (Join-Path $Source '*') -DestinationPath $Destination -Force
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

$config = Import-DeploymentConfig -Path $ConfigPath
$scriptRootParent = Split-Path -Parent $PSScriptRoot
$defaultRepositoryRoot = Split-Path -Parent $scriptRootParent

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [string](Get-ConfigValue -Config $config -Name 'RepositoryRoot' -DefaultValue $defaultRepositoryRoot)
}
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)

$workspaceRoot = Split-Path -Parent $RepositoryRoot
if ([string]::IsNullOrWhiteSpace($OpenDocViewerRoot)) {
    $OpenDocViewerRoot = [string](Get-ConfigValue -Config $config -Name 'OpenDocViewerRoot' -DefaultValue (Join-Path $workspaceRoot 'OpenDocViewer'))
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string](Get-ConfigValue -Config $config -Name 'Version' -DefaultValue '0.3.3')
}
if ([string]::IsNullOrWhiteSpace($Configuration)) {
    $Configuration = [string](Get-ConfigValue -Config $config -Name 'Configuration' -DefaultValue 'Release')
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = [string](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'OutputRoot' -DefaultValue (Join-Path $RepositoryRoot 'artifacts\suite-release'))
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
if (-not $PSBoundParameters.ContainsKey('KeepStaging')) {
    $KeepStaging = [bool](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'KeepStaging' -DefaultValue $false)
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$packageRoot = Join-Path $OutputRoot ("OpenModulePlatformSuite-$Version")
$payloadRoot = Join-Path $packageRoot 'payload'
$sqlRoot = Join-Path $packageRoot 'sql'
$buildRoot = Join-Path $packageRoot '.build'
$zipPath = Join-Path $OutputRoot ("OpenModulePlatformSuite-$Version.zip")

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $sqlRoot -Force | Out-Null

Write-Step 'Publishing OpenModulePlatform'
$publishScript = Join-Path $RepositoryRoot 'publish-all.ps1'
if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) {
    throw "OpenModulePlatform publish script was not found: $publishScript"
}

$publishRoot = Join-Path $buildRoot 'omp-publish'
$publishArgs = @(
    '-Root', $RepositoryRoot,
    '-Configuration', $Configuration,
    '-OutputRoot', $publishRoot,
    '-CleanOutput'
)
if (-not $SkipRestore) {
    $publishArgs += '-Restore'
}
& $publishScript @publishArgs

$payloadItems = @(
    @{ Source = 'OpenModulePlatform.Auth'; Zip = 'OpenModulePlatform.Auth.zip' },
    @{ Source = 'OpenModulePlatform.Portal'; Zip = 'OpenModulePlatform.Portal.zip' },
    @{ Source = 'OpenModulePlatform.HostAgent.WindowsService'; Zip = 'OpenModulePlatform.HostAgent.WindowsService.zip' },
    @{ Source = 'OpenModulePlatform.WorkerManager.WindowsService'; Zip = 'OpenModulePlatform.WorkerManager.WindowsService.zip' },
    @{ Source = 'OpenModulePlatform.WorkerProcessHost'; Zip = 'OpenModulePlatform.WorkerProcessHost.zip' },
    @{ Source = 'OpenModulePlatform.Service.ExampleServiceAppModule'; Zip = 'OpenModulePlatform.Service.ExampleServiceAppModule.zip' },
    @{ Source = 'OpenModulePlatform.Web.ExampleServiceAppModule'; Zip = 'OpenModulePlatform.Web.ExampleServiceAppModule.zip' },
    @{ Source = 'OpenModulePlatform.Web.ExampleWebAppBlazorModule'; Zip = 'OpenModulePlatform.Web.ExampleWebAppBlazorModule.zip' },
    @{ Source = 'OpenModulePlatform.Web.ExampleWebAppModule'; Zip = 'OpenModulePlatform.Web.ExampleWebAppModule.zip' },
    @{ Source = 'OpenModulePlatform.Web.ExampleWorkerAppModule'; Zip = 'OpenModulePlatform.Web.ExampleWorkerAppModule.zip' },
    @{ Source = 'OpenModulePlatform.Web.iFrameWebAppModule'; Zip = 'OpenModulePlatform.Web.iFrameWebAppModule.zip' },
    @{ Source = 'OpenModulePlatform.Worker.ExampleWorkerAppModule'; Zip = 'OpenModulePlatform.Worker.ExampleWorkerAppModule.zip' }
)

foreach ($item in $payloadItems) {
    Compress-FolderToZip -Source (Join-Path $publishRoot $item.Source) -Destination (Join-Path $payloadRoot $item.Zip)
}

Write-Step 'Publishing OpenDocViewer'
if (-not (Test-Path -LiteralPath $OpenDocViewerRoot -PathType Container)) {
    throw "OpenDocViewer repository root was not found: $OpenDocViewerRoot"
}

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

Write-Step 'Copying SQL scripts'
$sqlFiles = @(
    @{ Source = 'sql\1-setup-openmoduleplatform.sql'; Destination = 'OpenModulePlatform\1-setup-openmoduleplatform.sql' },
    @{ Source = 'sql\2-initialize-openmoduleplatform.sql'; Destination = 'OpenModulePlatform\2-initialize-openmoduleplatform.sql' },
    @{ Source = 'OpenModulePlatform.Portal\sql\1-setup-omp-portal.sql'; Destination = 'OpenModulePlatform.Portal\1-setup-omp-portal.sql' },
    @{ Source = 'OpenModulePlatform.Portal\sql\2-initialize-omp-portal.sql'; Destination = 'OpenModulePlatform.Portal\2-initialize-omp-portal.sql' },
    @{ Source = 'examples\WebAppModule\Sql\1-setup-example-webapp.sql'; Destination = 'examples\WebAppModule\1-setup-example-webapp.sql' },
    @{ Source = 'examples\WebAppModule\Sql\2-initialize-example-webapp.sql'; Destination = 'examples\WebAppModule\2-initialize-example-webapp.sql' },
    @{ Source = 'examples\WebAppBlazorModule\Sql\1-setup-example-webapp-blazor.sql'; Destination = 'examples\WebAppBlazorModule\1-setup-example-webapp-blazor.sql' },
    @{ Source = 'examples\WebAppBlazorModule\Sql\2-initialize-example-webapp-blazor.sql'; Destination = 'examples\WebAppBlazorModule\2-initialize-example-webapp-blazor.sql' },
    @{ Source = 'examples\ServiceAppModule\Sql\1-setup-example-serviceapp.sql'; Destination = 'examples\ServiceAppModule\1-setup-example-serviceapp.sql' },
    @{ Source = 'examples\ServiceAppModule\Sql\2-initialize-example-serviceapp.sql'; Destination = 'examples\ServiceAppModule\2-initialize-example-serviceapp.sql' },
    @{ Source = 'examples\WorkerAppModule\Sql\1-setup-example-workerapp.sql'; Destination = 'examples\WorkerAppModule\1-setup-example-workerapp.sql' },
    @{ Source = 'examples\WorkerAppModule\Sql\2-initialize-example-workerapp.sql'; Destination = 'examples\WorkerAppModule\2-initialize-example-workerapp.sql' },
    @{ Source = 'OpenModulePlatform.Web.iFrameWebAppModule\Sql\1-setup-iframe-webapp.sql'; Destination = 'OpenModulePlatform.Web.iFrameWebAppModule\1-setup-iframe-webapp.sql' },
    @{ Source = 'OpenModulePlatform.Web.iFrameWebAppModule\Sql\2-initialize-iframe-webapp.sql'; Destination = 'OpenModulePlatform.Web.iFrameWebAppModule\2-initialize-iframe-webapp.sql' }
)

foreach ($file in $sqlFiles) {
    Copy-RequiredFile -Source (Join-Path $RepositoryRoot $file.Source) -Destination (Join-Path $sqlRoot $file.Destination)
}

Write-Step 'Copying deployment scripts'
Copy-RequiredFile -Source (Join-Path $PSScriptRoot 'install-omp-suite.ps1') -Destination (Join-Path $packageRoot 'install-omp-suite.ps1')
Copy-RequiredFile -Source (Join-Path $PSScriptRoot 'uninstall-omp-suite.ps1') -Destination (Join-Path $packageRoot 'uninstall-omp-suite.ps1')
Copy-RequiredFile -Source (Join-Path $PSScriptRoot 'omp-suite.config.sample.psd1') -Destination (Join-Path $packageRoot 'omp-suite.config.sample.psd1')
Copy-RequiredFile -Source (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $packageRoot 'INSTALLATION.md')

$manifest = [ordered]@{
    schema = 'OpenModulePlatform.SuiteReleaseManifest.v1'
    createdUtc = [DateTime]::UtcNow.ToString('o')
    version = $Version
    payloads = [ordered]@{}
}

foreach ($item in $payloadItems) {
    $manifest.payloads[$item.Source] = 'payload/' + $item.Zip
}
$manifest.payloads['OpenDocViewer'] = 'payload/OpenDocViewer.dist.zip'

$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -Force

if (-not $KeepStaging) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'OpenModulePlatform suite package created.' -ForegroundColor Green
Write-Host "Package root: $packageRoot"
Write-Host "Package zip:  $zipPath"
