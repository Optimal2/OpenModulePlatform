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
against Microsoft's published SHA-512 before it is packaged. A pre-downloaded
bundle can be supplied with -HostingBundlePath; its file name must carry the
same runtime major the repository targets. The bundle is large and must never
be committed - it lives in the output folder and the local cache only.

Sample profiles (*.sample.json) are refused: a fresh-install package always
targets a real, prepared profile.

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
    # from this repository's TargetFramework, falling back to 10.
    [int]$RuntimeMajor = 0,

    # Where the package folder and zip are written.
    [string]$OutputRoot = '',

    # Package folder/zip base name. Default: OpenModulePlatformFreshInstall-<profile>.
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

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
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

function Get-ManifestValue {
    param(
        [object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Read-ProfileConfig {
    <#
    .SYNOPSIS
    Loads and validates the host profile. Returns the parsed bootstrap.json.
    Refuses sample profiles and folders that still contain sample files.
    #>
    param([Parameter(Mandatory = $true)][string]$ProfileFolder)

    if (-not (Test-Path -LiteralPath $ProfileFolder -PathType Container)) {
        throw "Profile folder not found: $ProfileFolder"
    }

    $bootstrapPath = Join-Path $ProfileFolder 'bootstrap.json'
    if (-not (Test-Path -LiteralPath $bootstrapPath -PathType Leaf)) {
        throw "The profile folder must contain bootstrap.json: $ProfileFolder"
    }

    $sampleFiles = @(Get-ChildItem -LiteralPath $ProfileFolder -Filter '*.sample.json' -Recurse -File)
    if ($sampleFiles.Count -gt 0) {
        $sampleList = ($sampleFiles | ForEach-Object { $_.FullName }) -join ', '
        throw "Sample profiles cannot be packaged for a fresh install. Remove these files and prepare a real profile: $sampleList"
    }

    return (Get-Content -LiteralPath $bootstrapPath -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-ProfileMachineNames {
    param([Parameter(Mandatory = $true)][object]$Config)

    $names = [System.Collections.Generic.List[string]]::new()
    $profile = Get-ManifestValue -Object $Config -Name 'profile'
    foreach ($name in @((Get-ManifestValue -Object $profile -Name 'machineNames'))) {
        if (-not [string]::IsNullOrWhiteSpace([string]$name)) {
            $names.Add(([string]$name).Trim())
        }
    }

    $hostAgent = Get-ManifestValue -Object $Config -Name 'hostAgent'
    foreach ($name in @((Get-ManifestValue -Object $hostAgent -Name 'hostName'), (Get-ManifestValue -Object $hostAgent -Name 'hostKey'))) {
        if (-not [string]::IsNullOrWhiteSpace([string]$name)) {
            $names.Add(([string]$name).Trim())
        }
    }

    # The comma keeps a single (or zero) name an array through function-output
    # unrolling, so the caller's .Count works under StrictMode on PS 5.1 too.
    return , @($names | Select-Object -Unique)
}

function Get-ProfilePayloadReferences {
    <#
    .SYNOPSIS
    The payload-relative files the install chain reads for this profile:
    enabled artifact sources and the HostAgent package.
    #>
    param([Parameter(Mandatory = $true)][object]$Config)

    $references = [System.Collections.Generic.List[string]]::new()
    foreach ($artifact in @((Get-ManifestValue -Object $Config -Name 'artifacts'))) {
        if ($null -eq $artifact) {
            continue
        }

        $enabled = Get-ManifestValue -Object $artifact -Name 'enabled'
        if ($null -ne $enabled -and -not [bool]$enabled) {
            continue
        }

        $source = [string](Get-ManifestValue -Object $artifact -Name 'source')
        if (-not [string]::IsNullOrWhiteSpace($source) -and -not [System.IO.Path]::IsPathRooted($source)) {
            $references.Add($source.Replace('\', '/'))
        }
    }

    $hostAgent = Get-ManifestValue -Object $Config -Name 'hostAgent'
    $packagePath = [string](Get-ManifestValue -Object $hostAgent -Name 'packagePath')
    if (-not [string]::IsNullOrWhiteSpace($packagePath) -and -not [System.IO.Path]::IsPathRooted($packagePath)) {
        $references.Add($packagePath.Replace('\', '/'))
    }

    # Same unrolling guard as Get-ProfileMachineNames.
    return , @($references | Select-Object -Unique)
}

function Copy-PayloadTree {
    <#
    .SYNOPSIS
    Copies a whole payload subtree (module definitions, SQL, host configs,
    config overlays) when the payload source carries it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    $source = Join-Path $SourceRoot ($RelativePath.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        return $false
    }

    $destination = Join-Path $DestinationRoot ($RelativePath.Replace('/', '\'))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
    return $true
}

function Get-RepositoryRuntimeMajor {
    <#
    .SYNOPSIS
    The .NET major this repository targets, parsed from Directory.Build.props
    (netX.0 / netX.0-windows). Falls back to 10.
    #>
    param([Parameter(Mandatory = $true)][string]$RepositoryRootPath)

    $propsPath = Join-Path $RepositoryRootPath 'Directory.Build.props'
    if (Test-Path -LiteralPath $propsPath -PathType Leaf) {
        $text = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
        $match = [regex]::Match($text, '<TargetFrameworks?>net(?<major>\d+)\.')
        if ($match.Success) {
            return [int]$match.Groups['major'].Value
        }
    }

    return 10
}

function Get-HostingBundleRelease {
    <#
    .SYNOPSIS
    Reads Microsoft's official release metadata for a runtime major and returns
    the latest release's Windows hosting bundle (version, url, sha512).
    #>
    param([Parameter(Mandatory = $true)][int]$Major)

    $metadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$Major.0/releases.json"
    Write-Host "Reading Microsoft's release metadata: $metadataUrl"
    $metadata = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 60
    $release = @($metadata.releases)[0]
    $aspNetCoreRuntime = Get-ManifestValue -Object $release -Name 'aspnetcore-runtime'
    foreach ($file in @((Get-ManifestValue -Object $aspNetCoreRuntime -Name 'files'))) {
        $url = [string](Get-ManifestValue -Object $file -Name 'url')
        $hash = [string](Get-ManifestValue -Object $file -Name 'hash')
        if ([string]::IsNullOrWhiteSpace($url) -or [string]::IsNullOrWhiteSpace($hash)) {
            continue
        }

        $fileName = [System.IO.Path]::GetFileName($url)
        if ($fileName -match '^dotnet-hosting-\d+\.\d+\.\d+-win\.exe$') {
            return [ordered]@{
                Version = [string]$release.'release-version'
                FileName = $fileName
                Url = $url
                Sha512 = $hash.ToLowerInvariant()
            }
        }
    }

    throw "No Windows hosting bundle (dotnet-hosting-*-win.exe) found in Microsoft's release metadata for .NET $Major."
}

function Test-FileSha512 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash
    return [string]::Equals($actual, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)
}

function Get-HostingBundleInstaller {
    <#
    .SYNOPSIS
    Ensures prereqs\dotnet-hosting-<version>-win.exe: from -HostingBundlePath
    (file name must carry the required major), or downloaded from Microsoft's
    official URL into the local cache and verified against Microsoft's
    published SHA-512. Returns the staged file, version and hash.
    #>
    param(
        [string]$SuppliedPath,
        [Parameter(Mandatory = $true)][int]$Major,
        [Parameter(Mandatory = $true)][string]$CacheFolder,
        [Parameter(Mandatory = $true)][string]$DestinationFolder
    )

    New-Item -ItemType Directory -Path $DestinationFolder -Force | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($SuppliedPath)) {
        $supplied = Resolve-FullPath -Path $SuppliedPath
        if (-not (Test-Path -LiteralPath $supplied -PathType Leaf)) {
            throw "Hosting bundle not found: $supplied"
        }

        $fileName = [System.IO.Path]::GetFileName($supplied)
        $match = [regex]::Match($fileName, '^dotnet-hosting-(?<version>\d+\.\d+\.\d+)-win\.exe$')
        if (-not $match.Success) {
            throw "The hosting bundle file name must be dotnet-hosting-<version>-win.exe, got: $fileName"
        }

        $version = $match.Groups['version'].Value
        if ([int]($version.Split('.')[0]) -ne $Major) {
            throw "The hosting bundle $fileName targets .NET $($version.Split('.')[0]), but the package needs major $Major exactly (no roll-forward across majors)."
        }

        $destination = Join-Path $DestinationFolder $fileName
        Copy-Item -LiteralPath $supplied -Destination $destination -Force
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA512).Hash.ToLowerInvariant()
        return [ordered]@{
            Path = $destination
            Version = $version
            Sha512 = $hash
            Source = 'local file (SHA-512 recorded, not verified against Microsoft)'
        }
    }

    $release = Get-HostingBundleRelease -Major $Major
    New-Item -ItemType Directory -Path $CacheFolder -Force | Out-Null
    $cached = Join-Path $CacheFolder $release.FileName
    if ((Test-Path -LiteralPath $cached -PathType Leaf) -and (Test-FileSha512 -Path $cached -ExpectedHash $release.Sha512)) {
        Write-Host "Using cached $($release.FileName) (SHA-512 verified against Microsoft's published hash)."
    }
    else {
        if (Test-Path -LiteralPath $cached -PathType Leaf) {
            Write-Warning "Cached $($release.FileName) failed the SHA-512 check; downloading again."
            Remove-Item -LiteralPath $cached -Force
        }

        Write-Host "Downloading $($release.FileName) from $($release.Url)"
        Invoke-WebRequest -Uri $release.Url -OutFile $cached -TimeoutSec 600
        if (-not (Test-FileSha512 -Path $cached -ExpectedHash $release.Sha512)) {
            Remove-Item -LiteralPath $cached -Force -ErrorAction SilentlyContinue
            throw "SHA-512 mismatch for $($release.FileName): the download does not match Microsoft's published hash. The file was deleted."
        }
    }

    $destination = Join-Path $DestinationFolder $release.FileName
    Copy-Item -LiteralPath $cached -Destination $destination -Force
    return [ordered]@{
        Path = $destination
        Version = $release.Version
        Sha512 = $release.Sha512
        Source = "downloaded from Microsoft (SHA-512 verified)"
    }
}

function Publish-Installer {
    <#
    .SYNOPSIS
    Publishes OpenModulePlatform.Installer.exe self-contained single-file
    win-x64 (or copies a supplied prebuilt exe) into the package root.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRootPath,
        [Parameter(Mandatory = $true)][string]$BuildRoot,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string]$SuppliedExePath,
        [string]$SigningConfigPath
    )

    if (-not [string]::IsNullOrWhiteSpace($SuppliedExePath)) {
        $supplied = Resolve-FullPath -Path $SuppliedExePath
        if (-not [string]::Equals([System.IO.Path]::GetFileName($supplied), 'OpenModulePlatform.Installer.exe', [StringComparison]::OrdinalIgnoreCase)) {
            throw "The supplied installer must be named OpenModulePlatform.Installer.exe: $supplied"
        }

        Copy-RequiredFile -Source $supplied -Destination $Destination
        return
    }

    $publishRoot = Join-Path $BuildRoot 'installer-publish'
    $project = Join-Path $RepositoryRootPath 'OpenModulePlatform.Installer\OpenModulePlatform.Installer.csproj'
    Write-Host "> dotnet publish $project (self-contained single-file win-x64)"
    & dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -o $publishRoot
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish of OpenModulePlatform.Installer failed with exit code $LASTEXITCODE."
    }

    # Sign the published exe when Azure Trusted Signing is configured; a no-op
    # otherwise (same opt-in behaviour as package-hostagent-first.ps1).
    $signScript = Join-Path $RepositoryRootPath 'scripts\deployment\sign-artifacts.ps1'
    if (Test-Path -LiteralPath $signScript -PathType Leaf) {
        & $signScript -Path $publishRoot -ConfigPath $SigningConfigPath -SkipIfUnconfigured
        if ($LASTEXITCODE -ne 0) {
            throw "Code signing failed for '$publishRoot' with exit code $LASTEXITCODE."
        }
    }

    Copy-RequiredFile -Source (Join-Path $publishRoot 'OpenModulePlatform.Installer.exe') -Destination $Destination
}

function Write-FreshInstallReadme {
    param([Parameter(Mandatory = $true)][string]$Path)

    $readme = @'
OpenModulePlatform - nyinstallation / fresh installation
=========================================================

SVENSKA
-------
Det här paketet installerar OpenModulePlatform på en NY server. Det kan inte
uppgradera, reparera eller avinstallera en befintlig installation.

1. Kopiera hela paketmappen till servern (till exempel D:\OMP-Install).
2. Högerklicka OpenModulePlatform.Installer.exe och välj
   "Kör som administratör" - eller kör i en administratörsterminal:
       OpenModulePlatform.Installer.exe --dry-run
   Torrkörningen kontrollerar allt utan att ändra något. Åtgärda eventuella
   röda rader som installationsprogrammet inte själv kan åtgärda (till exempel
   saknad databas eller saknat certifikat) och kör --dry-run igen.
3. När torrkörningen är grön: starta OpenModulePlatform.Installer.exe som
   administratör och följ dialogen.

Programmet skapar aldrig databasen - den måste finnas sedan tidigare
(databasadministratören skapar den). Varje steg loggas till en tidsstämplad
loggfil bredvid programmet. Avslutkoder vid --dry-run: 0 = grönt, 2 = ingen
eller flera matchande profiler, 3 = installationen skulle blockeras,
1 = fel.

ENGLISH
-------
This package installs OpenModulePlatform on a NEW server. It cannot upgrade,
repair or uninstall an existing installation.

1. Copy the whole package folder to the server (for example D:\OMP-Install).
2. Right-click OpenModulePlatform.Installer.exe and choose
   "Run as administrator" - or run in an elevated terminal:
       OpenModulePlatform.Installer.exe --dry-run
   The dry run checks everything without changing anything. Fix any red lines
   the installer cannot fix itself (for example a missing database or a
   missing certificate) and run --dry-run again.
3. When the dry run is green: start OpenModulePlatform.Installer.exe as
   administrator and follow the dialog.

The installer never creates the database - it must already exist (the database
administrator creates it). Every step is logged to a timestamped log file next
to the executable. --dry-run exit codes: 0 = green, 2 = no or several matching
profiles, 3 = the installation would be blocked, 1 = an error.
'@
    Set-Content -LiteralPath $Path -Value $readme -Encoding UTF8
}

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

$packageRoot = Join-Path $OutputRoot $PackageName
$zipPath = Join-Path $OutputRoot ($PackageName + '.zip')
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
