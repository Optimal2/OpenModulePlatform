#Requires -Version 5.1
<#
.SYNOPSIS
Installs the independent, fixed-name HostAgent alarm service on this machine.
.DESCRIPTION
Run on the host after upgrading HostAgent to a version that excludes Sentinel
from superseded-service cleanup. No remote service or database operations are performed.
Existing configuration is preserved. Remove the service and event source with -Uninstall;
files and recovery state are retained for inspection.
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [string]$SourceDir = '',
    [string]$InstallDir = '',
    [string]$OmpRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$serviceName = 'OMP.HostAgent.Sentinel'
$exeName = 'OpenModulePlatform.HostAgent.Sentinel.exe'

# Resolve defaults before elevation so the child does not depend on its working directory.
if ([string]::IsNullOrWhiteSpace($OmpRoot)) { $OmpRoot = $env:OMP_ROOT }
if ([string]::IsNullOrWhiteSpace($OmpRoot) -and [string]::IsNullOrWhiteSpace($InstallDir)) {
    $agents = @(Get-CimInstance Win32_Service | Where-Object { $_.Name -match '^OMP\.HostAgent\.\d+\.\d+\.\d+$' })
    if ($agents.Count -ne 1 -or $agents[0].PathName -notmatch '^"([^"]+)"') {
        throw 'Cannot infer OMP root from exactly one HostAgent. Pass -OmpRoot or -InstallDir.'
    }
    $agentDirectory = Split-Path -Parent $Matches[1]
    $OmpRoot = Split-Path -Parent (Split-Path -Parent $agentDirectory)
}
if ([string]::IsNullOrWhiteSpace($InstallDir)) { $InstallDir = Join-Path $OmpRoot 'Services\HostAgentSentinel' }
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
if ($InstallDir.TrimEnd('\') -eq [IO.Path]::GetPathRoot($InstallDir).TrimEnd('\')) {
    throw 'InstallDir must be a dedicated service directory, not a drive root.'
}
if (-not $Uninstall) {
    if ([string]::IsNullOrWhiteSpace($SourceDir)) {
        $SourceDir = Join-Path $PSScriptRoot '..\..\OpenModulePlatform.HostAgent.Sentinel\bin\Release\net48'
    }
    $SourceDir = [IO.Path]::GetFullPath($SourceDir)
    foreach ($file in @($exeName, "$exeName.config")) {
        if (-not (Test-Path -LiteralPath (Join-Path $SourceDir $file) -PathType Leaf)) {
            throw "Missing payload file: $file. Build Sentinel in Release or pass -SourceDir."
        }
    }
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # UAC relaunch in the same edition, never an encoded command or execution-policy bypass.
    $shellPath = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile', '-File', ('"{0}"' -f $PSCommandPath), '-InstallDir', ('"{0}"' -f $InstallDir))
    if ($Uninstall) { $arguments += '-Uninstall' }
    else { $arguments += @('-SourceDir', ('"{0}"' -f $SourceDir)) }
    $child = Start-Process -FilePath $shellPath -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    exit $child.ExitCode
}

function Invoke-ServiceControl {
    param([string[]]$Arguments)
    & sc.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failed ($LASTEXITCODE): $($Arguments[0])" }
}

$binaryPath = '"{0}"' -f (Join-Path $InstallDir $exeName)
$registered = Get-CimInstance Win32_Service -Filter "Name='OMP.HostAgent.Sentinel'"
if (-not $Uninstall) {
    $incompatible = @(Get-CimInstance Win32_Service | Where-Object {
        $_.Name -match '^OMP\.HostAgent\.(\d+\.\d+\.\d+)$' -and [version]$Matches[1] -lt [version]'0.3.263'
    })
    if ($incompatible.Count -gt 0) {
        throw 'Upgrade HostAgent to 0.3.263 or later first. Older cleanup can delete the Sentinel service.'
    }
    if ($null -ne $registered -and ($registered.PathName -ne $binaryPath -or $registered.StartName -ne 'LocalSystem')) {
        throw 'Existing Sentinel path/account differs. Uninstall explicitly before changing identity or location.'
    }
    if (Test-Path -LiteralPath $InstallDir) {
        $directoryInfo = Get-Item -LiteralPath $InstallDir
        if (($directoryInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'InstallDir itself must not be a reparse point.'
        }
        $unexpected = @(Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $_.Name -notin @($exeName, "$exeName.config") })
        if ($unexpected.Count -gt 0) { throw 'InstallDir contains unrelated files. Use a dedicated Sentinel directory.' }
    }
}
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    $existing.Dispose()
}
if ($Uninstall) {
    if ($null -ne $existing) { Invoke-ServiceControl -Arguments @('delete', $serviceName) }
    if ([Diagnostics.EventLog]::SourceExists($serviceName)) { Remove-EventLog -Source $serviceName }
    Write-Host 'Sentinel service and event source removed; payload/configuration/state files retained.'
    exit 0
}

# LocalSystem needs no new credentials and can inspect SCM, WMI and process lifetime.
# The directory must not allow unprivileged users to replace a LocalSystem executable.
$null = New-Item -ItemType Directory -Path $InstallDir -Force
$stateDir = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'OMP\HostAgentSentinel'
$null = New-Item -ItemType Directory -Path $stateDir -Force
foreach ($directory in @($InstallDir, $stateDir)) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $identity = New-Object Security.Principal.SecurityIdentifier($sid)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $directory -AclObject $acl
}
$destinationExe = Join-Path $InstallDir $exeName
if ($SourceDir.TrimEnd('\') -ne $InstallDir.TrimEnd('\')) {
    Copy-Item -LiteralPath (Join-Path $SourceDir $exeName) -Destination $destinationExe -Force
    if (-not (Test-Path -LiteralPath "$destinationExe.config")) {
        Copy-Item -LiteralPath (Join-Path $SourceDir "$exeName.config") -Destination "$destinationExe.config"
    }
}
if (-not [Diagnostics.EventLog]::SourceExists($serviceName)) {
    New-EventLog -LogName Application -Source $serviceName
}
elseif ([Diagnostics.EventLog]::LogNameFromSourceName($serviceName, '.') -ne 'Application') {
    throw 'The Sentinel source is registered in a different log. Correct the source registration before installing.'
}
if ($null -eq $existing) {
    New-Service -Name $serviceName -DisplayName 'OMP HostAgent Sentinel' -BinaryPathName $binaryPath -StartupType Automatic | Out-Null
}
else {
    Set-Service -Name $serviceName -StartupType Automatic
}
Invoke-ServiceControl -Arguments @('description', $serviceName, 'Fixed alarm point for the local versioned OMP HostAgent.')
Invoke-ServiceControl -Arguments @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/300000/restart/300000/restart/300000')
Invoke-ServiceControl -Arguments @('failureflag', $serviceName, '1')
$startedAt = Get-Date
Start-Service -Name $serviceName
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    $heartbeat = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $serviceName; Id = 10; StartTime = $startedAt } -MaxEvents 1 -ErrorAction SilentlyContinue
    if ($null -ne $heartbeat -and (Get-Service -Name $serviceName).Status -eq 'Running') {
        Write-Host "Sentinel Running; first OK event: $($heartbeat.TimeCreated.ToString('o'))"
        exit 0
    }
    Start-Sleep -Seconds 2
}
throw 'No OK event 10 with Sentinel Running within two minutes. Inspect Application events 100-103/200 and SCM; do not rename or relocate binaries to bypass security software.'
