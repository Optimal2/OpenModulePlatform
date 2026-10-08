# File: scripts/deployment/build-fresh-install-package.ps1
<#
.SYNOPSIS
Builds a fresh-install package: one folder and one zip that a server operator
copies to a new server and runs, with no other tooling.

.DESCRIPTION
The package contains OpenModulePlatform.Installer.exe (published self-contained
single-file win-x64 from this repository, or supplied via -InstallerExePath),
exactly one prepared host profile under hosts\<profile>\, the artifact/SQL/
module-definition payload the install chain needs (copied from a gathered
payload source such as a package-hostagent-first.ps1 output folder), the .NET
hosting bundle under prereqs\, and a bilingual README.txt.

The hosting bundle is downloaded from Microsoft's official release metadata
(release-metadata/<major>.0/releases.json) into a local cache and verified
against Microsoft's published SHA-512 before it is packaged; the download URL
is restricted to Microsoft's official hosts. A pre-downloaded bundle can be
supplied with -HostingBundlePath; its file name must carry the same runtime
major the repository targets. The bundle is large and must never be committed -
it lives in the output folder and the local cache only.

Sample profiles are refused (*.sample.json files, or the sample profile folder
itself): a fresh-install package always targets a real, prepared profile.
Clear-text passwords are refused (bootstrap.json hostAgent/sql password fields
and every *Password field in an optional package.psd1); enc:aesgcm:v1: values
are allowed. A set security.portableEncryptionKey warns: the package then
carries a secret and must be guarded.

The package name must be a plain folder name directly under -OutputRoot, and
every artifact/payload reference must be relative and stay inside the payload
source root - the builder never reads outside the payload source and never
deletes or writes outside the package folder it creates.

Example:
  .\build-fresh-install-package.ps1 `
      -ProfilePath D:\profiles\web-server-01 `
      -PayloadSourceRoot D:\packages\OpenModulePlatformHostAgentFirst-0.3.979
#>
[CmdletBinding()]
param(
    # Folder containing the prepared host profile's bootstrap.json.
    [Parameter(Mandatory = $true)]
    [string]$ProfilePath,

    # A gathered payload folder (for example an extracted package-hostagent-first
    # package): data\global\{artifacts,module-definitions,sql,...} and payload\.
    [Parameter(Mandatory = $true)]
    [string]$PayloadSourceRoot,

    # This repository's root. Defaults to the script's own repository.
    [string]$RepositoryRoot = '',

    # A prebuilt OpenModulePlatform.Installer.exe. When empty the installer is
    # published from this repository (self-contained single-file win-x64).
    [string]$InstallerExePath = '',

    # A pre-downloaded dotnet-hosting-<version>-win.exe. When empty the bundle
    # is downloaded from Microsoft's official URL and SHA-512 verified.
    [string]$HostingBundlePath = '',

    # The ASP.NET Core runtime major the artifacts need (the hosting bundle
    # must match exactly; there is no roll-forward across majors). 0 = detect
    # from OpenModulePlatform.Installer.csproj's TargetFramework; detection
    # fails loudly when the csproj is missing or carries no TargetFramework.
    [int]$RuntimeMajor = 0,

    # Where the package folder and zip are written.
    [string]$OutputRoot = '',

    # Package folder/zip base name; a plain folder name directly under
    # -OutputRoot (no path separators, no '..', no rooted path). Default:
    # OpenModulePlatformFreshInstall-<profile>.
    [string]$PackageName = '',

    # Local download cache for the hosting bundle (outside the repository).
    [string]$CacheRoot = '',

    # Optional Azure Trusted Signing configuration for the installer exe.
    [string]$CodeSigningConfigPath = '',

    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\omp\Assert-SafeToClean.ps1')
. (Join-Path $PSScriptRoot 'fresh-install-package-helpers.ps1')

# ---------------------------------------------------------------- main ----

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$RepositoryRoot = Resolve-FullPath -Path $RepositoryRoot

$ProfilePath = Resolve-FullPath -Path $ProfilePath
$PayloadSourceRoot = Resolve-FullPath -Path $PayloadSourceRoot

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepositoryRoot 'artifacts\fresh-install'
}
$OutputRoot = Resolve-FullPath -Path $OutputRoot

$profileName = (Split-Path -Leaf $ProfilePath)
if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $PackageName = "OpenModulePlatformFreshInstall-$profileName"
}

if ($RuntimeMajor -le 0) {
    $RuntimeMajor = Get-RepositoryRuntimeMajor -RepositoryRootPath $RepositoryRoot
}

if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $cacheBase = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($cacheBase)) {
        $cacheBase = [System.IO.Path]::GetTempPath()
    }

    $CacheRoot = Join-Path $cacheBase 'OpenModulePlatform\installer-prereqs'
}

Write-Step 'Reading the host profile'
$config = Read-ProfileConfig -ProfileFolder $ProfilePath
$machineNames = Get-ProfileMachineNames -Config $config
if ($machineNames.Count -eq 0) {
    throw "The profile names no machine (profile.machineNames, hostAgent.hostName or hostAgent.hostKey); a fresh-install package must be locked to the target server."
}

$payloadReferences = Get-ProfilePayloadReferences -Config $config
if ($payloadReferences.Count -eq 0) {
    throw "The profile references no artifact payload; nothing to install."
}

$packageRoot = Resolve-PackageRoot -OutputRoot $OutputRoot -PackageName $PackageName
$zipPath = $packageRoot + '.zip'
$buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-fresh-install-build-' + [Guid]::NewGuid().ToString('N'))

if (Test-Path -LiteralPath $packageRoot) {
    Assert-SafeToClean -Path $packageRoot -RepositoryRoot $RepositoryRoot
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

try {
    Write-Step 'Publishing the installer'
    Publish-Installer `
        -RepositoryRootPath $RepositoryRoot `
        -BuildRoot $buildRoot `
        -Destination (Join-Path $packageRoot 'OpenModulePlatform.Installer.exe') `
        -SuppliedExePath $InstallerExePath `
        -SigningConfigPath $CodeSigningConfigPath

    Write-Step "Copying profile $profileName"
    $profileDestination = Join-Path $packageRoot (Join-Path 'hosts' $profileName)
    New-Item -ItemType Directory -Path $profileDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $ProfilePath '*') -Destination $profileDestination -Recurse -Force
    # A profile-local sql\ folder resolves through data\hosts\<configKey>\sql at
    # install time: ResolvePackageDataPath checks data\hosts\<config file name
    # without extension> (bootstrap) and data\global.
    $profileSql = Join-Path $profileDestination 'sql'
    if (Test-Path -LiteralPath $profileSql -PathType Container) {
        $hostSqlDestination = Join-Path $packageRoot 'data\hosts\bootstrap'
        New-Item -ItemType Directory -Path $hostSqlDestination -Force | Out-Null
        Move-Item -LiteralPath $profileSql -Destination (Join-Path $hostSqlDestination 'sql')
    }

    Write-Step 'Copying the install payload'
    foreach ($reference in $payloadReferences) {
        $source = Join-Path $PayloadSourceRoot ($reference.Replace('/', '\'))
        $destination = Join-Path $packageRoot ($reference.Replace('/', '\'))
        Copy-RequiredFile -Source $source -Destination $destination
    }

    foreach ($tree in @('data/global/module-definitions', 'data/global/sql', 'data/global/host-configs', 'data/global/config-overlays')) {
        [void](Copy-PayloadTree -SourceRoot $PayloadSourceRoot -RelativePath $tree -DestinationRoot $packageRoot)
    }

    Write-Step "Hosting bundle for .NET $RuntimeMajor"
    $bundle = Get-HostingBundleInstaller `
        -SuppliedPath $HostingBundlePath `
        -Major $RuntimeMajor `
        -CacheFolder $CacheRoot `
        -DestinationFolder (Join-Path $packageRoot 'prereqs')

    Write-Step 'Writing README.txt'
    Write-FreshInstallReadme -Path (Join-Path $packageRoot 'README.txt')

    if ($SkipZip) {
        if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
            Remove-Item -LiteralPath $zipPath -Force
        }
    }
    else {
        Write-Step 'Creating the zip'
        if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
            Remove-Item -LiteralPath $zipPath -Force
        }

        Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -Force
    }
}
finally {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$packageBytes = (Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum
$zipBytes = if ((Test-Path -LiteralPath $zipPath -PathType Leaf)) { (Get-Item -LiteralPath $zipPath).Length } else { 0 }

Write-Host ''
Write-Host 'Fresh-install package created.' -ForegroundColor Green
Write-Host "Profile:          $profileName"
Write-Host "Machine names:    $($machineNames -join ', ')"
Write-Host "Artifacts:        $($payloadReferences.Count) payload file(s)"
Write-Host "Hosting bundle:   dotnet-hosting-$($bundle.Version)-win.exe ($($bundle.Source))"
Write-Host "  SHA-512:        $($bundle.Sha512)"
Write-Host ("Package folder:   {0} ({1:n1} MB)" -f $packageRoot, ($packageBytes / 1MB))
if ($SkipZip) {
    Write-Host 'Package zip:      skipped'
}
else {
    Write-Host ("Package zip:      {0} ({1:n1} MB)" -f $zipPath, ($zipBytes / 1MB))
}
