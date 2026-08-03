# File: scripts/deployment/sign-artifacts.ps1
#requires -Version 5.1
<#
.SYNOPSIS
    Signs first-party binaries with Azure Trusted Signing.

.DESCRIPTION
    Signs .exe/.dll files under the given paths using signtool and the Azure
    Trusted Signing dlib. The Trusted Signing client is downloaded from
    NuGet on first use and cached under LOCALAPPDATA.

    Configuration is a JSON file with the signtool metadata shape:

        {
          "Endpoint": "https://weu.codesigning.azure.net",
          "CodeSigningAccountName": "optimal2-signing",
          "CertificateProfileName": "optimal2-public-trust"
        }

    The config path resolves in order: -ConfigPath, the OMP_TRUSTED_SIGNING_CONFIG
    environment variable, then scripts/deployment/trusted-signing.json next to
    this script. Authentication uses DefaultAzureCredential: run 'az login'
    interactively, or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET
    for a service principal with the 'Trusted Signing Certificate Profile
    Signer' role.

.PARAMETER Path
    Files or directories to sign. Directories are searched recursively.

.PARAMETER ConfigPath
    Trusted Signing metadata JSON. See DESCRIPTION for resolution order.

.PARAMETER IncludePattern
    File name patterns considered first-party and eligible for signing.

.PARAMETER SkipIfUnconfigured
    Exit successfully without signing when no configuration is found. Used by
    the packaging pipeline so unsigned developer builds keep working.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [string]$ConfigPath = '',
    [string[]]$IncludePattern = @(
        'OpenModulePlatform.*.dll',
        'OpenModulePlatform.*.exe',
        'IbsPackager.*.dll',
        'IbsPackager.*.exe',
        'EArkivChecker.*.dll',
        'EArkivChecker.*.exe',
        'LogSearch.*.dll',
        'LogSearch.*.exe',
        'iKrock2.*.dll',
        'iKrock2.*.exe',
        'VajSkrivare.*.dll',
        'VajSkrivare.*.exe',
        'ODVGateway.*.dll',
        'ODVGateway.*.exe',
        'Dokumentbibliotek.*.dll',
        'Dokumentbibliotek.*.exe'
    ),
    [switch]$SkipIfUnconfigured
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$TrustedSigningClientVersion = '1.0.60'
$SdkBuildToolsVersion = '10.0.26100.4948'
$TimestampUrl = 'http://timestamp.acs.microsoft.com'

function Get-NuGetPackageCached {
    # Downloads and expands a NuGet package into the local cache once, so the
    # signing pipeline has no machine prerequisites beyond PowerShell. The
    # probe pattern may contain wildcards because some packages nest content
    # under an inner version folder that differs from the package version.
    param(
        [Parameter(Mandatory = $true)][string]$PackageId,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$ProbePattern
    )

    $cacheRoot = Join-Path $env:LOCALAPPDATA "OMP\nuget-tools\$PackageId\$Version"
    $existing = Get-ChildItem -Path (Join-Path $cacheRoot $ProbePattern) -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing) {
        return $existing.FullName
    }

    Write-Host "Downloading $PackageId $Version from nuget.org..."
    $packageUrl = "https://www.nuget.org/api/v2/package/$PackageId/$Version"
    $tempFile = Join-Path ([IO.Path]::GetTempPath()) ($PackageId + '-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Invoke-WebRequest -Uri $packageUrl -OutFile $tempFile -UseBasicParsing
        New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
        Expand-Archive -LiteralPath $tempFile -DestinationPath $cacheRoot -Force
    }
    finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }

    $downloaded = Get-ChildItem -Path (Join-Path $cacheRoot $ProbePattern) -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $downloaded) {
        throw "NuGet package $PackageId $Version did not contain a file matching: $ProbePattern"
    }

    return $downloaded.FullName
}

function Resolve-SigningConfigPath {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (-not (Test-Path -LiteralPath $Explicit -PathType Leaf)) {
            throw "Trusted Signing config was not found: $Explicit"
        }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $fromEnvironment = $env:OMP_TRUSTED_SIGNING_CONFIG
    if (-not [string]::IsNullOrWhiteSpace($fromEnvironment) -and (Test-Path -LiteralPath $fromEnvironment -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $fromEnvironment).Path
    }

    $default = Join-Path $PSScriptRoot 'trusted-signing.json'
    if (Test-Path -LiteralPath $default -PathType Leaf) {
        return (Resolve-Path -LiteralPath $default).Path
    }

    return $null
}

function Get-SignToolPath {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Where-Object { $_.Name -match '^10\.' } |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($candidate) {
            return $candidate
        }
    }

    $onPath = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    # No Windows SDK installed: fall back to the SDK build-tools NuGet package,
    # which ships signtool and keeps the pipeline prerequisite-free.
    return Get-NuGetPackageCached `
        -PackageId 'Microsoft.Windows.SDK.BuildTools' `
        -Version $SdkBuildToolsVersion `
        -ProbePattern 'bin\*\x64\signtool.exe'
}

function Get-TrustedSigningDlibPath {
    return Get-NuGetPackageCached `
        -PackageId 'Microsoft.Trusted.Signing.Client' `
        -Version $TrustedSigningClientVersion `
        -ProbePattern 'bin\x64\Azure.CodeSigning.Dlib.dll'
}

function Get-EligibleFiles {
    param(
        [string[]]$Roots,
        [string[]]$Patterns
    )

    $files = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

    foreach ($root in $Roots) {
        if (Test-Path -LiteralPath $root -PathType Leaf) {
            $candidates = @(Get-Item -LiteralPath $root)
        }
        elseif (Test-Path -LiteralPath $root -PathType Container) {
            $candidates = @(Get-ChildItem -LiteralPath $root -Recurse -File -Include '*.dll', '*.exe')
        }
        else {
            throw "Sign path was not found: $root"
        }

        foreach ($candidate in $candidates) {
            $matchesPattern = $false
            foreach ($pattern in $Patterns) {
                if ($candidate.Name -like $pattern) {
                    $matchesPattern = $true
                    break
                }
            }
            if (-not $matchesPattern) {
                continue
            }

            $signature = Get-AuthenticodeSignature -LiteralPath $candidate.FullName
            if ($signature.Status -eq 'Valid') {
                continue
            }

            if ($seen.Add($candidate.FullName)) {
                $files.Add($candidate.FullName) | Out-Null
            }
        }
    }

    return $files
}

$resolvedConfig = Resolve-SigningConfigPath -Explicit $ConfigPath
if ($null -eq $resolvedConfig) {
    if ($SkipIfUnconfigured) {
        Write-Host 'Trusted Signing is not configured; skipping code signing for this run.'
        exit 0
    }

    throw 'No Trusted Signing configuration was found. Pass -ConfigPath, set OMP_TRUSTED_SIGNING_CONFIG, or create scripts/deployment/trusted-signing.json (see trusted-signing.sample.json).'
}

$configJson = Get-Content -LiteralPath $resolvedConfig -Raw | ConvertFrom-Json
foreach ($required in @('Endpoint', 'CodeSigningAccountName', 'CertificateProfileName')) {
    $value = $configJson.PSObject.Properties[$required]
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value.Value)) {
        throw "Trusted Signing config '$resolvedConfig' is missing required property '$required'."
    }
}

$targets = Get-EligibleFiles -Roots $Path -Patterns $IncludePattern
if ($targets.Count -eq 0) {
    Write-Host 'No unsigned first-party binaries were found under the given paths; nothing to sign.'
    exit 0
}

$signTool = Get-SignToolPath
$dlib = Get-TrustedSigningDlibPath

Write-Host "Signing $($targets.Count) file(s) with Trusted Signing profile '$($configJson.CertificateProfileName)'..."

$batchSize = 25
for ($offset = 0; $offset -lt $targets.Count; $offset += $batchSize) {
    $count = [Math]::Min($batchSize, $targets.Count - $offset)
    $batch = $targets.GetRange($offset, $count)
    $arguments = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/dlib', $dlib, '/dmdf', $resolvedConfig) + @($batch)
    & $signTool @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE. Check Azure authentication (az login or AZURE_* service principal variables) and that the identity holds the 'Trusted Signing Certificate Profile Signer' role."
    }
}

Write-Host "Signed $($targets.Count) file(s)."
exit 0
