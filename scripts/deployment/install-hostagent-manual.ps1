<#
.SYNOPSIS
Installs a versioned OMP HostAgent Windows service manually.

.DESCRIPTION
Use this as a recovery tool when HostAgent self-upgrade cannot complete
reliably. The script installs a HostAgent payload from a published folder, a
plain payload zip, an OMP artifact-package zip, or a universal-package zip.

The script copies production settings and the DPAPI credential-store file from
an existing HostAgent installation unless explicit source paths are supplied.
It configures the installed HostAgent in Normal runtime mode and disables
HostAgent self-upgrade by default, so the service can be stabilized before
automatic upgrades are re-enabled.

Run from an elevated PowerShell session on the target host.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,

    [string]$ServiceNamePrefix = 'EMP.HostAgent',

    [string]$SettingsSourcePath = '',

    [string]$CredentialStoreSourcePath = '',

    [string]$ServiceAccountName = '',

    [securestring]$ServiceAccountPassword,

    [string]$ReferenceServiceName = '',

    [switch]$EnableSelfUpgrade,

    [switch]$DisableOtherHostAgentServices,

    [switch]$StartService
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.Path]::GetFullPath($Path)
}

function Ensure-Property {
    param(
        [Parameter(Mandatory = $true)] [pscustomobject]$Object,
        [Parameter(Mandatory = $true)] [string]$Name,
        [object]$Value
    )

    if ($null -eq $Object.PSObject.Properties[$Name]) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function ConvertFrom-SecureStringToPlainText {
    param([Parameter(Mandatory = $true)][securestring]$Value)

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        if ($ptr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        }
    }
}

function Get-ScPath {
    return Join-Path $env:windir 'System32\sc.exe'
}

function Invoke-Sc {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & (Get-ScPath) @Arguments 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output | Out-String).Trim()
    }
}

function Invoke-ScChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $result = Invoke-Sc -Arguments $Arguments
    if ($result.ExitCode -ne 0) {
        throw "sc.exe $($Arguments -join ' ') failed with exit code $($result.ExitCode): $($result.Output)"
    }

    return $result
}

function Test-ServiceExists {
    param([Parameter(Mandatory = $true)][string]$Name)

    $result = Invoke-Sc -Arguments @('query', $Name)
    return $result.ExitCode -eq 0
}

function Stop-ServiceIfRunning {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$TimeoutSeconds = 45
    )

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq 'Stopped') {
        return
    }

    Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 300
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -eq $service -or $service.Status -eq 'Stopped') {
            return
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Windows service '$Name' did not stop within $TimeoutSeconds second(s)."
}

function Get-ExistingHostAgentServices {
    param([Parameter(Mandatory = $true)][string]$Prefix)

    $trimmedPrefix = $Prefix.Trim().TrimEnd('.')
    Get-CimInstance Win32_Service |
        Where-Object {
            $_.Name -eq $trimmedPrefix -or
            $_.Name.StartsWith($trimmedPrefix + '.', [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object @{ Expression = { if ($_.State -eq 'Running') { 0 } else { 1 } } }, Name
}

function Resolve-ReferenceServiceName {
    param(
        [Parameter(Mandatory = $true)][string]$ExplicitName,
        [Parameter(Mandatory = $true)][string]$Prefix,
        [Parameter(Mandatory = $true)][string]$TargetName
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitName)) {
        return $ExplicitName.Trim()
    }

    $service = Get-ExistingHostAgentServices -Prefix $Prefix |
        Where-Object { $_.Name -ne $TargetName } |
        Select-Object -First 1
    if ($null -eq $service) {
        return ''
    }

    return [string]$service.Name
}

function Resolve-ServiceAccountName {
    param(
        [string]$ExplicitName,
        [string]$ReferenceName
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitName)) {
        return $ExplicitName.Trim()
    }

    if ([string]::IsNullOrWhiteSpace($ReferenceName)) {
        return ''
    }

    $escaped = $ReferenceName.Replace("'", "''")
    $service = Get-CimInstance Win32_Service -Filter "Name='$escaped'" -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return ''
    }

    return [string]$service.StartName
}

function Test-BuiltInServiceAccount {
    param([string]$AccountName)

    if ([string]::IsNullOrWhiteSpace($AccountName)) {
        return $true
    }

    $normalized = $AccountName.Trim()
    return $normalized -eq 'LocalSystem' -or
        $normalized -eq 'NT AUTHORITY\LocalService' -or
        $normalized -eq 'NT AUTHORITY\NetworkService'
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $SourceDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $DestinationDirectory -Recurse -Force
    }
}

function Expand-ZipToDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    if (Test-Path -LiteralPath $DestinationDirectory) {
        Remove-Item -LiteralPath $DestinationDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $DestinationDirectory)
}

function Find-HostAgentPayloadDirectory {
    param([Parameter(Mandatory = $true)][string]$Root)

    $direct = Join-Path $Root 'OpenModulePlatform.HostAgent.WindowsService.exe'
    if (Test-Path -LiteralPath $direct -PathType Leaf) {
        return $Root
    }

    $match = Get-ChildItem -LiteralPath $Root -Recurse -Filter 'OpenModulePlatform.HostAgent.WindowsService.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length }, FullName |
        Select-Object -First 1
    if ($null -eq $match) {
        return ''
    }

    return $match.DirectoryName
}

function Expand-HostAgentSource {
    param(
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$VersionText,
        [Parameter(Mandatory = $true)][string]$WorkRoot,
        [int]$Depth = 0
    )

    if ($Depth -gt 5) {
        throw "Could not resolve HostAgent payload below '$InputPath'. Nested package depth exceeded."
    }

    $fullInputPath = Resolve-FullPath -Path $InputPath
    if (Test-Path -LiteralPath $fullInputPath -PathType Container) {
        $payloadDirectory = Find-HostAgentPayloadDirectory -Root $fullInputPath
        if (-not [string]::IsNullOrWhiteSpace($payloadDirectory)) {
            Copy-DirectoryContents -SourceDirectory $payloadDirectory -DestinationDirectory $DestinationDirectory
            return
        }

        $nested = @()
        $preferredArtifactName = "*omp_hostagent*__$VersionText.zip"
        $nested += Get-ChildItem -LiteralPath $fullInputPath -Recurse -File -Filter $preferredArtifactName -ErrorAction SilentlyContinue
        $nested += Get-ChildItem -LiteralPath $fullInputPath -Recurse -File -Filter 'OpenModulePlatform.HostAgent.WindowsService.zip' -ErrorAction SilentlyContinue
        $nested += Get-ChildItem -LiteralPath $fullInputPath -Recurse -File -Filter '*omp_hostagent*.zip' -ErrorAction SilentlyContinue
        $nested += Get-ChildItem -LiteralPath $fullInputPath -Recurse -File -Filter '*HostAgent*.zip' -ErrorAction SilentlyContinue
        $candidate = $nested |
            Sort-Object @{ Expression = { if ($_.Name -like $preferredArtifactName) { 0 } else { 1 } } }, FullName |
            Select-Object -First 1

        if ($null -ne $candidate) {
            Expand-HostAgentSource `
                -InputPath $candidate.FullName `
                -DestinationDirectory $DestinationDirectory `
                -VersionText $VersionText `
                -WorkRoot $WorkRoot `
                -Depth ($Depth + 1)
            return
        }

        throw "HostAgent executable or nested HostAgent package was not found below '$fullInputPath'."
    }

    if (-not (Test-Path -LiteralPath $fullInputPath -PathType Leaf)) {
        throw "Source path was not found: $fullInputPath"
    }

    if ([IO.Path]::GetExtension($fullInputPath) -ne '.zip') {
        throw "Source path must be a folder or zip file: $fullInputPath"
    }

    $unpack = Join-Path $WorkRoot ('unpack-' + [Guid]::NewGuid().ToString('N'))
    Expand-ZipToDirectory -ZipPath $fullInputPath -DestinationDirectory $unpack
    Expand-HostAgentSource `
        -InputPath $unpack `
        -DestinationDirectory $DestinationDirectory `
        -VersionText $VersionText `
        -WorkRoot $WorkRoot `
        -Depth ($Depth + 1)
}

function Resolve-SettingsSourceFile {
    param(
        [string]$ExplicitPath,
        [string]$Root,
        [string]$TargetInstallPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $full = Resolve-FullPath -Path $ExplicitPath
        if (Test-Path -LiteralPath $full -PathType Container) {
            $full = Join-Path $full 'appsettings.Production.json'
        }

        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "Settings source file was not found: $full"
        }

        return $full
    }

    $candidates = @()
    if (Test-Path -LiteralPath $Root -PathType Container) {
        $candidates += Get-ChildItem -LiteralPath $Root -Directory -Filter 'HostAgent-*' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -ne $TargetInstallPath } |
            Sort-Object LastWriteTimeUtc -Descending |
            ForEach-Object { Join-Path $_.FullName 'appsettings.Production.json' }
        $candidates += Join-Path (Join-Path $Root 'HostAgent') 'appsettings.Production.json'
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw 'No HostAgent appsettings.Production.json source was found. Pass -SettingsSourcePath explicitly.'
}

function Resolve-ConfiguredCredentialStorePath {
    param(
        [pscustomobject]$Config,
        [string]$SettingsDirectory
    )

    if ($null -eq $Config.PSObject.Properties['HostAgent']) {
        return ''
    }

    $hostAgent = $Config.HostAgent
    if ($null -eq $hostAgent.PSObject.Properties['CredentialStore']) {
        return ''
    }

    $credentialStore = $hostAgent.CredentialStore
    if ($null -eq $credentialStore.PSObject.Properties['FilePath']) {
        return ''
    }

    $path = [string]$credentialStore.FilePath
    if ([string]::IsNullOrWhiteSpace($path)) {
        return ''
    }

    if ([IO.Path]::IsPathRooted($path)) {
        return [IO.Path]::GetFullPath($path)
    }

    return [IO.Path]::GetFullPath((Join-Path $SettingsDirectory $path))
}

function Update-HostAgentSettings {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSettingsPath,
        [Parameter(Mandatory = $true)][string]$TargetSettingsPath,
        [Parameter(Mandatory = $true)][string]$TargetInstallPath,
        [Parameter(Mandatory = $true)][string]$TargetServiceName,
        [Parameter(Mandatory = $true)][string]$TargetVersion,
        [Parameter(Mandatory = $true)][string]$Prefix,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][bool]$SelfUpgradeEnabled,
        [string]$AccountName,
        [string]$ExplicitCredentialStorePath
    )

    $settingsDirectory = Split-Path -Parent $SourceSettingsPath
    $config = Get-Content -LiteralPath $SourceSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Ensure-Property -Object $config -Name 'HostAgent' -Value ([pscustomobject]@{})
    $hostAgent = $config.HostAgent
    Ensure-Property -Object $hostAgent -Name 'SelfUpgrade' -Value ([pscustomobject]@{})
    Ensure-Property -Object $hostAgent -Name 'CredentialStore' -Value ([pscustomobject]@{})

    $sourceCredentialStorePath = if (-not [string]::IsNullOrWhiteSpace($ExplicitCredentialStorePath)) {
        Resolve-FullPath -Path $ExplicitCredentialStorePath
    }
    else {
        Resolve-ConfiguredCredentialStorePath -Config $config -SettingsDirectory $settingsDirectory
    }

    if ([string]::IsNullOrWhiteSpace($sourceCredentialStorePath)) {
        $fallbackStorePath = Join-Path $settingsDirectory 'hostagent.credentials.json'
        if (Test-Path -LiteralPath $fallbackStorePath -PathType Leaf) {
            $sourceCredentialStorePath = $fallbackStorePath
        }
    }

    $stagedCredentialStorePath = Join-Path (Split-Path -Parent $TargetSettingsPath) 'hostagent.credentials.json'
    $finalCredentialStorePath = Join-Path $TargetInstallPath 'hostagent.credentials.json'
    if (-not [string]::IsNullOrWhiteSpace($sourceCredentialStorePath)) {
        if (-not (Test-Path -LiteralPath $sourceCredentialStorePath -PathType Leaf)) {
            throw "Credential store source file was not found: $sourceCredentialStorePath"
        }

        Copy-Item -LiteralPath $sourceCredentialStorePath -Destination $stagedCredentialStorePath -Force
        $hostAgent.CredentialStore.FilePath = $finalCredentialStorePath
    }

    $hostAgent.ServiceName = $TargetServiceName
    $hostAgent.Version = $TargetVersion
    $hostAgent.RuntimeMode = 'Normal'
    $hostAgent.TakeoverFromServiceName = ''
    $hostAgent.SelfUpgrade.IsEnabled = $SelfUpgradeEnabled
    $hostAgent.SelfUpgrade.ServiceNamePrefix = $Prefix
    $hostAgent.SelfUpgrade.InstallRoot = $Root

    if (-not [string]::IsNullOrWhiteSpace($AccountName)) {
        $hostAgent.SelfUpgrade.ServiceAccountName = $AccountName.Trim()
    }

    $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $TargetSettingsPath -Encoding UTF8
}

function Quote-BinaryPathArgument {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value.Contains('"')) {
        throw "Service binary path argument contains a double quote and cannot be safely quoted: $Value"
    }

    return '"' + $Value + '"'
}

function New-ServiceBinaryPath {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return (Quote-BinaryPathArgument -Value $ExecutablePath) + ' --service-name=' + $Name
}

function Resolve-ServiceDisplayName {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$VersionText
    )

    $prefix = $Name
    if ($prefix.EndsWith('.' + $VersionText, [StringComparison]::OrdinalIgnoreCase)) {
        $prefix = $prefix.Substring(0, $prefix.Length - $VersionText.Length - 1)
    }

    $baseDisplayName = $prefix.Replace('.', ' ')
    if (-not $baseDisplayName.StartsWith('OMP', [StringComparison]::OrdinalIgnoreCase)) {
        $baseDisplayName = 'OMP ' + $baseDisplayName
    }

    return "$baseDisplayName $VersionText"
}

function Install-OrUpdateService {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [string]$AccountName,
        [securestring]$AccountPassword
    )

    $binaryPath = New-ServiceBinaryPath -ExecutablePath $ExecutablePath -Name $Name
    $arguments = @(
        if (Test-ServiceExists -Name $Name) { 'config' } else { 'create' }
        $Name
        'binPath='
        $binaryPath
        'start='
        'auto'
        'DisplayName='
        $DisplayName
    )

    $plainPassword = $null
    try {
        if (-not (Test-BuiltInServiceAccount -AccountName $AccountName)) {
            if ($null -eq $AccountPassword) {
                $AccountPassword = Read-Host -Prompt "Password for $AccountName" -AsSecureString
            }

            $plainPassword = ConvertFrom-SecureStringToPlainText -Value $AccountPassword
            if ([string]::IsNullOrWhiteSpace($plainPassword)) {
                throw 'Service account password cannot be empty.'
            }

            $arguments += @('obj=', $AccountName.Trim(), 'password=', $plainPassword)
        }
        elseif (-not [string]::IsNullOrWhiteSpace($AccountName)) {
            $arguments += @('obj=', $AccountName.Trim())
        }

        Invoke-ScChecked -Arguments $arguments | Out-Null
        Invoke-ScChecked -Arguments @('description', $Name, 'OpenModulePlatform HostAgent runtime service.') | Out-Null
    }
    finally {
        $plainPassword = $null
    }
}

function Disable-OtherServices {
    param(
        [Parameter(Mandatory = $true)][string]$Prefix,
        [Parameter(Mandatory = $true)][string]$TargetName
    )

    foreach ($service in Get-ExistingHostAgentServices -Prefix $Prefix) {
        if ($service.Name -eq $TargetName) {
            continue
        }

        Stop-ServiceIfRunning -Name $service.Name
        Invoke-ScChecked -Arguments @('config', $service.Name, 'start=', 'disabled') | Out-Null
        Write-Host "Disabled $($service.Name)"
    }
}

function Wait-ServiceRunning {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -eq 'Running') {
            return
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Windows service '$Name' did not reach Running state within $TimeoutSeconds second(s)."
}

if (-not (Test-IsAdministrator)) {
    throw 'This script must be run from an elevated PowerShell session.'
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "Version must use simplified SemVer-compatible text such as 0.3.86."
}

$installRootFull = Resolve-FullPath -Path $InstallRoot
$servicePrefix = $ServiceNamePrefix.Trim().TrimEnd('.')
if ([string]::IsNullOrWhiteSpace($servicePrefix)) {
    throw 'ServiceNamePrefix cannot be empty.'
}

$targetServiceName = "$servicePrefix.$Version"
$targetInstallPath = Join-Path $installRootFull ('HostAgent-' + $Version)
$referenceService = Resolve-ReferenceServiceName `
    -ExplicitName $ReferenceServiceName `
    -Prefix $servicePrefix `
    -TargetName $targetServiceName
$resolvedServiceAccountName = Resolve-ServiceAccountName `
    -ExplicitName $ServiceAccountName `
    -ReferenceName $referenceService

if ([string]::IsNullOrWhiteSpace($resolvedServiceAccountName)) {
    throw 'Service account could not be resolved. Pass -ServiceAccountName explicitly.'
}

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('omp-hostagent-manual-' + [Guid]::NewGuid().ToString('N'))
$stagingPath = $targetInstallPath + '.staging-' + [Guid]::NewGuid().ToString('N')
$backupPath = $targetInstallPath + '.backup-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss')

try {
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

    Expand-HostAgentSource `
        -InputPath $SourcePath `
        -DestinationDirectory $stagingPath `
        -VersionText $Version `
        -WorkRoot $workRoot

    $settingsSourceFile = Resolve-SettingsSourceFile `
        -ExplicitPath $SettingsSourcePath `
        -Root $installRootFull `
        -TargetInstallPath $targetInstallPath
    $targetSettingsPath = Join-Path $stagingPath 'appsettings.Production.json'

    Update-HostAgentSettings `
        -SourceSettingsPath $settingsSourceFile `
        -TargetSettingsPath $targetSettingsPath `
        -TargetInstallPath $stagingPath `
        -TargetServiceName $targetServiceName `
        -TargetVersion $Version `
        -Prefix $servicePrefix `
        -Root $installRootFull `
        -SelfUpgradeEnabled ([bool]$EnableSelfUpgrade) `
        -AccountName $resolvedServiceAccountName `
        -ExplicitCredentialStorePath $CredentialStoreSourcePath

    $executablePath = Join-Path $stagingPath 'OpenModulePlatform.HostAgent.WindowsService.exe'
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "HostAgent executable was not found after extraction: $executablePath"
    }

    if ($PSCmdlet.ShouldProcess($targetInstallPath, "Install HostAgent $Version")) {
        if (Test-ServiceExists -Name $targetServiceName) {
            Stop-ServiceIfRunning -Name $targetServiceName
        }

        if (Test-Path -LiteralPath $targetInstallPath -PathType Container) {
            Move-Item -LiteralPath $targetInstallPath -Destination $backupPath -Force
        }

        Move-Item -LiteralPath $stagingPath -Destination $targetInstallPath -Force
        $stagingPath = ''

        $installedExecutablePath = Join-Path $targetInstallPath 'OpenModulePlatform.HostAgent.WindowsService.exe'
        $displayName = Resolve-ServiceDisplayName -Name $targetServiceName -VersionText $Version
        Install-OrUpdateService `
            -Name $targetServiceName `
            -ExecutablePath $installedExecutablePath `
            -DisplayName $displayName `
            -AccountName $resolvedServiceAccountName `
            -AccountPassword $ServiceAccountPassword

        if ($DisableOtherHostAgentServices) {
            Disable-OtherServices -Prefix $servicePrefix -TargetName $targetServiceName
        }

        if ($StartService) {
            Start-Service -Name $targetServiceName
            Wait-ServiceRunning -Name $targetServiceName
        }
    }

    Write-Host "Installed HostAgent $Version as $targetServiceName"
    Write-Host "Install path: $targetInstallPath"
    Write-Host "Settings source: $settingsSourceFile"
    Write-Host "Self-upgrade enabled: $([bool]$EnableSelfUpgrade)"
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($stagingPath) -and (Test-Path -LiteralPath $stagingPath)) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
