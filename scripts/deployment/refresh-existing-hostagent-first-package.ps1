# File: scripts/deployment/refresh-existing-hostagent-first-package.ps1
<#
.SYNOPSIS
Refreshes an existing HostAgent-first installer package without running from the package directory.

.DESCRIPTION
The bootstrapper can rebuild and replace an installer package, but the process
that performs the replacement must not run from the package being replaced.
This helper copies the package-local bootstrapper runner to a temporary folder
and starts the refresh from there. That keeps the package directory unlocked and
lets the bootstrapper preserve package-local configs and host data correctly.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [string]$ConfigPath = '',

    [string]$ConfigName = '',

    [switch]$RestartGui
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Resolve-ConfigPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$ExplicitPath,
        [string]$Name
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = Resolve-FullPath -Path $ExplicitPath
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "ConfigPath does not exist: $resolved"
        }

        return $resolved
    }

    $configsRoot = Join-Path $Root 'configs'
    if (-not (Test-Path -LiteralPath $configsRoot -PathType Container)) {
        $sample = Join-Path $Root 'bootstrap.local.sample.json'
        if (Test-Path -LiteralPath $sample -PathType Leaf) {
            return $sample
        }

        throw "Package does not contain a configs folder or bootstrap.local.sample.json: $Root"
    }

    if (-not [string]::IsNullOrWhiteSpace($Name)) {
        $fileName = if ($Name.EndsWith('.json', [System.StringComparison]::OrdinalIgnoreCase)) { $Name } else { "$Name.json" }
        $named = Join-Path $configsRoot $fileName
        if (-not (Test-Path -LiteralPath $named -PathType Leaf)) {
            throw "ConfigName does not resolve to an existing config: $named"
        }

        return $named
    }

    $machineName = $env:COMPUTERNAME
    foreach ($candidate in Get-ChildItem -LiteralPath $configsRoot -Filter '*.json' -File) {
        try {
            $json = Get-Content -LiteralPath $candidate.FullName -Raw | ConvertFrom-Json
            $machineNames = @($json.profile.machineNames)
            if ($machineNames -contains $machineName) {
                return $candidate.FullName
            }
        }
        catch {
            Write-Warning "Could not inspect config '$($candidate.FullName)': $($_.Exception.Message)"
        }
    }

    throw "No config matched computer name '$machineName'. Pass -ConfigName or -ConfigPath."
}

function Copy-Runner {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RunnerRoot
    )

    $toolRoot = Join-Path $Root 'tools\OpenModulePlatform.Bootstrapper'
    if (Test-Path -LiteralPath $toolRoot -PathType Container) {
        Copy-Item -Path (Join-Path $toolRoot '*') -Destination $RunnerRoot -Recurse -Force
        return
    }

    $singleExe = Join-Path $Root 'OpenModulePlatform.Bootstrapper.exe'
    if (Test-Path -LiteralPath $singleExe -PathType Leaf) {
        Copy-Item -LiteralPath $singleExe -Destination $RunnerRoot -Force
        return
    }

    throw "Package does not contain a bootstrapper runner under tools or package root: $Root"
}

$packageRootPath = Resolve-FullPath -Path $PackageRoot
if (-not (Test-Path -LiteralPath $packageRootPath -PathType Container)) {
    throw "PackageRoot does not exist: $packageRootPath"
}

$configPathValue = Resolve-ConfigPath -Root $packageRootPath -ExplicitPath $ConfigPath -Name $ConfigName
$runnerRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('omp-installer-refresh-runner-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runnerRoot | Out-Null

try {
    Copy-Runner -Root $packageRootPath -RunnerRoot $runnerRoot

    $runnerDll = Join-Path $runnerRoot 'OpenModulePlatform.Bootstrapper.dll'
    $runnerExe = Join-Path $runnerRoot 'OpenModulePlatform.Bootstrapper.exe'
    $arguments = @()
    $fileName = ''

    if (Test-Path -LiteralPath $runnerDll -PathType Leaf) {
        $fileName = 'dotnet'
        $arguments += $runnerDll
    }
    elseif (Test-Path -LiteralPath $runnerExe -PathType Leaf) {
        $fileName = $runnerExe
    }
    else {
        throw "Copied runner does not contain OpenModulePlatform.Bootstrapper.dll or .exe: $runnerRoot"
    }

    $arguments += @(
        '--refresh-installer-package',
        '--config',
        $configPathValue,
        '--payload-root',
        $packageRootPath
    )

    if ($RestartGui) {
        $arguments += '--restart-gui'
    }

    Write-Host "Refreshing HostAgent-first package"
    Write-Host "Package root: $packageRootPath"
    Write-Host "Config:       $configPathValue"
    Write-Host "Runner root:  $runnerRoot"

    if ($fileName.Equals('dotnet', [System.StringComparison]::OrdinalIgnoreCase)) {
        Push-Location -LiteralPath $runnerRoot
        try {
            & $fileName @arguments
            $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
        }
        finally {
            Pop-Location
        }
    }
    else {
        $process = Start-Process `
            -FilePath $fileName `
            -ArgumentList $arguments `
            -WorkingDirectory $runnerRoot `
            -WindowStyle Hidden `
            -Wait `
            -PassThru
        $exitCode = $process.ExitCode
    }

    if ($exitCode -ne 0) {
        throw "Installer package refresh failed with exit code $exitCode. Check the omp-installer-refresh log in the temp folder."
    }
}
finally {
    if (Test-Path -LiteralPath $runnerRoot -PathType Container) {
        Remove-Item -LiteralPath $runnerRoot -Recurse -Force
    }
}
