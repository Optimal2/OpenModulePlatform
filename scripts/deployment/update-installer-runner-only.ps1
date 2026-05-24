# File: scripts/deployment/update-installer-runner-only.ps1
<#
.SYNOPSIS
Updates only the runnable installer executable in an existing HostAgent-first package.

.DESCRIPTION
Developer/private installer packages can be kept intentionally minimal in Git:
the root OpenModulePlatform.Bootstrapper.exe plus machine-specific configs. This
helper refreshes only that executable from source. It does not rebuild module
definitions, artifact packages, SQL payloads, package manifests, tools, or any
other generated package content.

Use the installer GUI package sync action afterwards when a developer machine
needs to populate the ignored package object library before an install.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [string]$RepositoryRoot = '',

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Resolve-RepositoryRoot {
    param([string]$ConfiguredRoot)

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredRoot)) {
        $resolved = Resolve-FullPath -Path $ConfiguredRoot
        if (-not (Test-Path -LiteralPath (Join-Path $resolved 'OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.csproj') -PathType Leaf)) {
            throw "RepositoryRoot does not look like an OpenModulePlatform repository: $resolved"
        }

        return $resolved
    }

    $scriptRoot = Resolve-FullPath -Path $PSScriptRoot
    $candidate = [System.IO.DirectoryInfo]$scriptRoot
    while ($null -ne $candidate) {
        $projectPath = Join-Path $candidate.FullName 'OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.csproj'
        if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
            return $candidate.FullName
        }

        $candidate = $candidate.Parent
    }

    throw 'Could not locate the OpenModulePlatform repository root. Pass -RepositoryRoot.'
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

$packageRootPath = Resolve-FullPath -Path $PackageRoot
if (-not (Test-Path -LiteralPath $packageRootPath -PathType Container)) {
    throw "PackageRoot does not exist: $packageRootPath"
}

$configsRoot = Join-Path $packageRootPath 'configs'
if (-not (Test-Path -LiteralPath $configsRoot -PathType Container)) {
    throw "Minimal installer packages must keep machine configs in '$configsRoot'."
}

$repositoryRootPath = Resolve-RepositoryRoot -ConfiguredRoot $RepositoryRoot
$projectPath = Join-Path $repositoryRootPath 'OpenModulePlatform.Bootstrapper\OpenModulePlatform.Bootstrapper.csproj'
$publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-installer-runner-only-' + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $publishRoot | Out-Null

    Invoke-NativeChecked dotnet @(
        'publish',
        $projectPath,
        '-c',
        $Configuration,
        '-o',
        $publishRoot,
        '-r',
        'win-x64',
        '--self-contained',
        'false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '--nologo',
        '--verbosity',
        'minimal'
    )

    $sourceExe = Join-Path $publishRoot 'OpenModulePlatform.Bootstrapper.exe'
    if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
        throw "Publish did not produce OpenModulePlatform.Bootstrapper.exe: $publishRoot"
    }

    $targetExe = Join-Path $packageRootPath 'OpenModulePlatform.Bootstrapper.exe'
    Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force

    Write-Host "Updated installer runner: $targetExe"
    Write-Host 'Package object libraries were not rebuilt.'
}
finally {
    if (Test-Path -LiteralPath $publishRoot -PathType Container) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
}
