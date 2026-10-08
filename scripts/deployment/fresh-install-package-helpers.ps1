# File: scripts/deployment/fresh-install-package-helpers.ps1
<#
.SYNOPSIS
Helper functions for build-fresh-install-package.ps1, dot-sourced by that
script and by tests/FreshInstallPackage/FreshInstallPackage.Tests.ps1 (so the
pure validations can be unit-tested without building a package).

No code runs at dot-source time; this file only defines functions.
#>

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

function Test-ClearTextSecret {
    <#
    .SYNOPSIS
    True when a password field holds a clear-text value. Empty fields are
    fine, and enc:aesgcm:v1: values (scripts/protect-bootstrap-config-secrets.ps1)
    are allowed: they are encrypted, not clear text.
    #>
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    return -not $Value.Trim().StartsWith('enc:aesgcm:v1:', [System.StringComparison]::Ordinal)
}

function Find-PasswordFields {
    <#
    .SYNOPSIS
    Recursively names every *Password field under a parsed package.psd1 node
    that holds a clear-text value.
    #>
    param(
        [object]$Node,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $bad = [System.Collections.Generic.List[string]]::new()
    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($key in $Node.Keys) {
            $value = $Node[$key]
            $childPath = "$Path.$key"
            if (([string]$key).EndsWith('Password', [System.StringComparison]::OrdinalIgnoreCase) -and (Test-ClearTextSecret -Value ([string]$value))) {
                $bad.Add($childPath)
            }

            # The recursive call returns one (possibly empty) array object;
            # foreach enumerates its elements. Do NOT wrap in @() - that would
            # nest the array instead.
            foreach ($item in (Find-PasswordFields -Node $value -Path $childPath)) {
                $bad.Add($item)
            }
        }
    }
    elseif ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        $index = 0
        foreach ($item in $Node) {
            foreach ($sub in (Find-PasswordFields -Node $item -Path ("{0}[{1}]" -f $Path, $index))) {
                $bad.Add($sub)
            }

            $index++
        }
    }

    # The comma keeps an empty (or single) result an array through output
    # unrolling, so the caller's .Count works under StrictMode on PS 5.1 too.
    return , @($bad)
}

function Find-ClearTextPasswordFields {
    <#
    .SYNOPSIS
    Names the password fields that hold clear-text values: bootstrap.json
    hostAgent.serviceAccountPassword / hostAgent.iisAppPoolPassword /
    hostAgent.serviceAppPassword and sql.password, plus every *Password field
    in an optional package.psd1 next to bootstrap.json. A fresh-install
    package must never carry clear-text passwords.
    #>
    param(
        [Parameter(Mandatory = $true)][object]$Config,
        [Parameter(Mandatory = $true)][string]$ProfileFolder
    )

    $bad = [System.Collections.Generic.List[string]]::new()

    $hostAgent = Get-ManifestValue -Object $Config -Name 'hostAgent'
    foreach ($field in @('serviceAccountPassword', 'iisAppPoolPassword', 'serviceAppPassword')) {
        $value = [string](Get-ManifestValue -Object $hostAgent -Name $field)
        if (Test-ClearTextSecret -Value $value) {
            $bad.Add("hostAgent.$field")
        }
    }

    $sql = Get-ManifestValue -Object $Config -Name 'sql'
    $sqlPassword = [string](Get-ManifestValue -Object $sql -Name 'password')
    if (Test-ClearTextSecret -Value $sqlPassword) {
        $bad.Add('sql.password')
    }

    $psd1Path = Join-Path $ProfileFolder 'package.psd1'
    if (Test-Path -LiteralPath $psd1Path -PathType Leaf) {
        $data = Import-PowerShellDataFile -LiteralPath $psd1Path
        foreach ($field in (Find-PasswordFields -Node $data -Path 'package.psd1')) {
            $bad.Add($field)
        }
    }

    return , @($bad)
}

function Read-ProfileConfig {
    <#
    .SYNOPSIS
    Loads and validates the host profile. Returns the parsed bootstrap.json.
    Refuses sample profiles (a *.sample.json anywhere in the folder, or the
    sample profile folder itself) and clear-text passwords; warns when the
    profile carries the portable encryption key.
    #>
    param([Parameter(Mandatory = $true)][string]$ProfileFolder)

    if (-not (Test-Path -LiteralPath $ProfileFolder -PathType Container)) {
        throw "Profile folder not found: $ProfileFolder"
    }

    if ([string]::Equals((Split-Path -Leaf $ProfileFolder), 'sample', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Sample profiles cannot be packaged for a fresh install. '$ProfileFolder' is the sample profile folder; copy it to a host-specific folder and prepare a real profile there."
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

    $config = Get-Content -LiteralPath $bootstrapPath -Raw -Encoding UTF8 | ConvertFrom-Json

    # Direct assignment on purpose: the function returns one array object
    # (comma-guarded, like Get-ProfileMachineNames); wrapping in @() would
    # nest it and make .Count lie.
    $clearTextFields = Find-ClearTextPasswordFields -Config $config -ProfileFolder $ProfileFolder
    if ($clearTextFields.Count -gt 0) {
        throw "The profile stores clear-text passwords in $($clearTextFields -join ', '). Encrypt them first (scripts/protect-bootstrap-config-secrets.ps1 produces enc:aesgcm:v1: values) or leave them empty; a fresh-install package must never carry clear-text passwords."
    }

    $security = Get-ManifestValue -Object $config -Name 'security'
    $portableKey = [string](Get-ManifestValue -Object $security -Name 'portableEncryptionKey')
    if (-not [string]::IsNullOrWhiteSpace($portableKey)) {
        Write-Warning 'security.portableEncryptionKey is set: the package carries the portable encryption key (a secret). Guard the package like a password and never commit it.'
    }

    return $config
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

function Assert-SafePayloadReference {
    <#
    .SYNOPSIS
    A payload reference (an artifact source, the HostAgent package path) must
    stay inside the payload source root and the package root: a relative path
    with no drive prefix and no '..' segment. Anything else could read from
    outside the gathered payload or write outside the package folder.
    #>
    param([Parameter(Mandatory = $true)][string]$Reference)

    if ($Reference -match '^[A-Za-z]:') {
        throw "Artifact/payload sources must be relative to the payload root, not absolute paths: $Reference"
    }

    if ($Reference.Replace('\', '/').Split('/') -contains '..') {
        throw "Artifact/payload sources must stay inside the payload root ('..' is not allowed): $Reference"
    }

    # IsPathRooted comes last: on .NET Framework (Windows PowerShell 5.1) it
    # throws on characters that are invalid in paths instead of returning
    # false, and the checks above must win with their plain messages.
    $rooted = $false
    try {
        $rooted = [System.IO.Path]::IsPathRooted($Reference)
    }
    catch {
        $rooted = $true
    }

    if ($rooted) {
        throw "Artifact/payload sources must be relative to the payload root, not absolute paths: $Reference"
    }
}

function Get-ProfilePayloadReferences {
    <#
    .SYNOPSIS
    The payload-relative files the install chain reads for this profile:
    enabled artifact sources and the HostAgent package. Every reference must
    be relative and stay inside the payload root (Assert-SafePayloadReference).
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
        if (-not [string]::IsNullOrWhiteSpace($source)) {
            Assert-SafePayloadReference -Reference $source
            $references.Add($source.Replace('\', '/'))
        }
    }

    $hostAgent = Get-ManifestValue -Object $Config -Name 'hostAgent'
    $packagePath = [string](Get-ManifestValue -Object $hostAgent -Name 'packagePath')
    if (-not [string]::IsNullOrWhiteSpace($packagePath)) {
        Assert-SafePayloadReference -Reference $packagePath
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

function Resolve-PackageRoot {
    <#
    .SYNOPSIS
    Validates -PackageName and returns the package folder's full path,
    guaranteed to sit directly under -OutputRoot. The builder deletes and
    recreates this folder, so it must never point anywhere else: a '..\name'
    input must not escape -OutputRoot and delete a sibling folder.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$PackageName
    )

    $name = $PackageName.Trim()
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw 'The package name must not be empty.'
    }

    # The plain-string checks come before any System.IO.Path call: on
    # .NET Framework (Windows PowerShell 5.1) IsPathRooted throws on
    # characters that are invalid in paths instead of returning false.
    if ($name -match '[/\\]' -or $name.Contains('..') -or $name -eq '.') {
        throw "The package name must be a plain folder name under the output root ('..' and path separators are not allowed): $PackageName"
    }

    if ($name.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "The package name contains characters that are not valid in a folder name: $PackageName"
    }

    if ([System.IO.Path]::IsPathRooted($name) -or $name -match '^[A-Za-z]:') {
        throw "The package name must be a plain folder name under the output root, not a rooted path: $PackageName"
    }

    $fullOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
    $packageRoot = [System.IO.Path]::GetFullPath((Join-Path $fullOutputRoot $name))
    $outputPrefix = $fullOutputRoot.TrimEnd('\') + '\'
    if (-not $packageRoot.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The package folder must stay under the output root: $packageRoot is outside $fullOutputRoot"
    }

    return $packageRoot
}

function Get-RepositoryRuntimeMajor {
    <#
    .SYNOPSIS
    The .NET major the installer targets, parsed from
    OpenModulePlatform.Installer\OpenModulePlatform.Installer.csproj
    (<TargetFramework>netX.0-windows</TargetFramework>). Directory.Build.props
    carries no TargetFramework, and detection FAILS LOUDLY when the csproj is
    missing or has no TargetFramework: a silent fallback could package the
    wrong hosting bundle, and the bundle must match the major exactly.
    #>
    param([Parameter(Mandatory = $true)][string]$RepositoryRootPath)

    $csprojPath = Join-Path $RepositoryRootPath 'OpenModulePlatform.Installer\OpenModulePlatform.Installer.csproj'
    if (-not (Test-Path -LiteralPath $csprojPath -PathType Leaf)) {
        throw "Cannot detect the runtime major: $csprojPath does not exist. Pass -RuntimeMajor explicitly."
    }

    $text = Get-Content -LiteralPath $csprojPath -Raw -Encoding UTF8
    $match = [regex]::Match($text, '<TargetFrameworks?>\s*net(?<major>\d+)\.')
    if (-not $match.Success) {
        throw "Cannot detect the runtime major: $csprojPath has no <TargetFramework>net<major>... entry. Pass -RuntimeMajor explicitly."
    }

    return [int]$match.Groups['major'].Value
}

function Test-MicrosoftDownloadUrl {
    <#
    .SYNOPSIS
    The hosting bundle may only be downloaded from Microsoft's official hosts
    over https. The URL comes from the release metadata; this check is what
    stops a tampered (or mistaken) metadata document from redirecting the
    download to another host.
    #>
    param([Parameter(Mandatory = $true)][string]$Url)

    $uri = $null
    if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$uri)) {
        return $false
    }

    if ($uri.Scheme -ne [System.Uri]::UriSchemeHttps) {
        return $false
    }

    $allowedHosts = @(
        'builds.dotnet.microsoft.com',
        'download.visualstudio.microsoft.com',
        'dotnetcli.azureedge.net'
    )
    return $allowedHosts -contains $uri.Host
}

function Get-HostingBundleRelease {
    <#
    .SYNOPSIS
    Reads Microsoft's official release metadata for a runtime major and returns
    the latest release's Windows hosting bundle (version, url, sha512). The
    download URL is restricted to Microsoft's official hosts
    (Test-MicrosoftDownloadUrl).
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
            if (-not (Test-MicrosoftDownloadUrl -Url $url)) {
                throw "Refusing the hosting bundle URL from the release metadata: only https URLs on builds.dotnet.microsoft.com, download.visualstudio.microsoft.com and dotnetcli.azureedge.net are allowed, got: $url"
            }

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
loggfil bredvid programmet. Avslutkoder vid --dry-run: 0 = grönt,
2 = ingen eller flera matchande profiler, 3 = installationen skulle
blockeras, 4 = en installation finns redan på den här datorn, 1 = fel.

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
profiles, 3 = the installation would be blocked, 4 = an installation already
exists on this computer, 1 = an error.
'@
    Set-Content -LiteralPath $Path -Value $readme -Encoding UTF8
}
