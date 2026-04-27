# File: OpenModulePlatform.Service.ExampleServiceAppModule/Deploy/Install-Service.ps1
[CmdletBinding()]
param(
    [string]$ServiceName = 'OpenModulePlatform.Service.ExampleServiceAppModule',
    [string]$DisplayName = 'OpenModulePlatform Service - ExampleServiceAppModule',
    [string]$Description = 'Example OMP service app installed from a published folder.',
    [string]$InstallRoot = "$env:ProgramFiles\OpenModulePlatform\ServiceApps",
    [string]$AppFolderName = 'ExampleServiceAppModule',
    [string]$ExecutableName = 'OpenModulePlatform.Service.ExampleServiceAppModule.exe',
    [ValidateSet('Automatic', 'Manual', 'Disabled')]
    [string]$StartupType = 'Automatic',
    [switch]$SkipStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsWindows {
    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-ForServiceStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [System.ServiceProcess.ServiceControllerStatus]$Status,
        [int]$TimeoutSeconds = 30
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -eq $Status) {
            return
        }

        Start-Sleep -Seconds 1
    }

    throw "Timed out waiting for service '$Name' to reach status '$Status'."
}

function Resolve-PublishSourcePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutableName,
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath
    )

    $scriptDirectory = Split-Path -Parent $ScriptPath
    $candidateDirectories = New-Object System.Collections.Generic.List[string]

    foreach ($candidate in @(
        $scriptDirectory,
        (Get-Location).Path
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            $normalized = [System.IO.Path]::GetFullPath($candidate)
            if (-not $candidateDirectories.Contains($normalized)) {
                $candidateDirectories.Add($normalized)
            }
        }
    }

    foreach ($candidateDirectory in $candidateDirectories) {
        $candidateExecutablePath = Join-Path $candidateDirectory $ExecutableName
        if (Test-Path -LiteralPath $candidateExecutablePath) {
            return $candidateDirectory
        }
    }

    $searchRoots = New-Object System.Collections.Generic.List[string]
    foreach ($root in @(
        $scriptDirectory,
        (Split-Path -Parent $scriptDirectory),
        (Join-Path (Split-Path -Parent $scriptDirectory) 'bin')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($root) -and (Test-Path -LiteralPath $root)) {
            $normalized = [System.IO.Path]::GetFullPath($root)
            if (-not $searchRoots.Contains($normalized)) {
                $searchRoots.Add($normalized)
            }
        }
    }

    $matches = @()
    foreach ($root in $searchRoots) {
        try {
            $matches += Get-ChildItem -LiteralPath $root -Filter $ExecutableName -File -Recurse -ErrorAction SilentlyContinue
        }
        catch [System.UnauthorizedAccessException] {
            Write-Verbose "Skipped inaccessible search root '$root'."
        }
        catch [System.IO.IOException] {
            Write-Verbose "Skipped search root '$root' because of an I/O error."
        }
    }

    $publishMatch = $matches |
        Where-Object { $_.DirectoryName -match '[\\/]publish([\\/]|$)' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -ne $publishMatch) {
        return $publishMatch.DirectoryName
    }

    $anyMatch = $matches |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -ne $anyMatch) {
        return $anyMatch.DirectoryName
    }

    throw "Could not find '$ExecutableName'. Run the script from the published output folder or keep the script in Deploy and make sure a publish output exists under the project."
}

if (-not (Test-IsWindows)) {
    throw 'This script only supports Windows hosts.'
}

if (-not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session (Run as Administrator).'
}

$sourcePath = Resolve-PublishSourcePath -ExecutableName $ExecutableName -ScriptPath $MyInvocation.MyCommand.Path
$sourceExecutablePath = Join-Path $sourcePath $ExecutableName
if (-not (Test-Path -LiteralPath $sourceExecutablePath)) {
    throw "Could not find '$ExecutableName' in '$sourcePath'."
}

$targetPath = Join-Path $InstallRoot $AppFolderName
$targetExecutablePath = Join-Path $targetPath $ExecutableName

Write-Host "SourcePath  : $sourcePath"
Write-Host "TargetPath  : $targetPath"
Write-Host "ServiceName : $ServiceName"

New-Item -ItemType Directory -Path $targetPath -Force | Out-Null

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existingService) {
    if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping existing service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        Wait-ForServiceStatus -Name $ServiceName -Status Stopped -TimeoutSeconds 30
    }
}

Write-Host 'Copying published files to Program Files...'
$robocopyLog = & robocopy $sourcePath $targetPath *.* /E /R:2 /W:2 /NFL /NDL /NJH /NJS /NP
$robocopyExitCode = $LASTEXITCODE
if ($robocopyExitCode -ge 8) {
    throw "Robocopy failed with exit code $robocopyExitCode.`n$robocopyLog"
}

if (-not (Test-Path -LiteralPath $targetExecutablePath)) {
    throw "Install target executable was not found after copy: '$targetExecutablePath'."
}

$binaryPath = '"{0}"' -f $targetExecutablePath

if ($null -eq $existingService) {
    Write-Host "Creating Windows service '$ServiceName'..."
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName $DisplayName -Description $Description -StartupType $StartupType | Out-Null
}
else {
    Write-Host "Updating existing Windows service '$ServiceName'..."
    $startupMap = @{
        Automatic = 'auto'
        Manual    = 'demand'
        Disabled  = 'disabled'
    }

    $configOutput = & sc.exe config $ServiceName binPath= $binaryPath start= $startupMap[$StartupType] DisplayName= $DisplayName 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe config failed with exit code $LASTEXITCODE.`n$configOutput"
    }

    $descriptionOutput = & sc.exe description $ServiceName $Description 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe description failed with exit code $LASTEXITCODE.`n$descriptionOutput"
    }
}

if (-not $SkipStart) {
    Write-Host "Starting service '$ServiceName'..."
    Start-Service -Name $ServiceName -ErrorAction Stop
    Wait-ForServiceStatus -Name $ServiceName -Status Running -TimeoutSeconds 30
}

Write-Host ''
Write-Host 'Done.'
Write-Host "InstalledPath : $targetPath"
Write-Host "Executable    : $targetExecutablePath"
Write-Host "ServiceName   : $ServiceName"
