<#
.SYNOPSIS
Writes a DPAPI-protected HostAgent credential-store entry and updates HostAgent self-upgrade settings.

.DESCRIPTION
Use this on the target Windows host when an existing HostAgent installation must be
migrated to credential-store based service-account credentials. The script keeps
the password out of command history by prompting for a secure string by default.

Run from an elevated PowerShell session on each HostAgent host, then restart the
active HostAgent service so the new settings are loaded.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$HostAgentInstallPath,

    [string]$AppSettingsPath = '',

    [string]$CredentialStorePath = '',

    [string]$CredentialKey = 'hostagent:self-upgrade',

    [Parameter(Mandatory = $true)]
    [string]$UserName,

    [securestring]$Password,

    [ValidateSet('LocalMachine', 'CurrentUser')]
    [string]$ProtectionScope = 'LocalMachine',

    [ValidateSet('PortalAdminApproved', 'Full')]
    [string]$AutomationMode = 'Full',

    [string]$EntropyPurpose = 'OpenModulePlatform.HostAgent.CredentialStore.v1',

    [switch]$AlsoSetDefaultServiceAppCredential,

    [switch]$AlsoSetDefaultIisAppPoolCredential
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Protect-Password {
    param(
        [Parameter(Mandatory = $true)] [string]$PlainText,
        [Parameter(Mandatory = $true)] [string]$Scope,
        [Parameter(Mandatory = $true)] [string]$Purpose
    )

    Add-Type -AssemblyName System.Security
    $scopeValue = if ($Scope -eq 'CurrentUser') {
        [Security.Cryptography.DataProtectionScope]::CurrentUser
    }
    else {
        [Security.Cryptography.DataProtectionScope]::LocalMachine
    }

    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($PlainText)
    $entropyBytes = [Text.Encoding]::UTF8.GetBytes($Purpose)
    $encryptedBytes = [Security.Cryptography.ProtectedData]::Protect($passwordBytes, $entropyBytes, $scopeValue)
    return [Convert]::ToBase64String($encryptedBytes)
}

function Resolve-PathText {
    param(
        [string]$Value,
        [Parameter(Mandatory = $true)] [string]$Fallback
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return [IO.Path]::GetFullPath($Fallback)
    }

    return [IO.Path]::GetFullPath($Value)
}

$installPath = [IO.Path]::GetFullPath($HostAgentInstallPath)
$settingsPath = Resolve-PathText -Value $AppSettingsPath -Fallback (Join-Path $installPath 'appsettings.Production.json')
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "HostAgent appsettings file was not found: $settingsPath"
}

if ($null -eq $Password) {
    $Password = Read-Host -Prompt 'HostAgent service account password' -AsSecureString
}

$plainPassword = ConvertFrom-SecureStringToPlainText -Value $Password
try {
    if ([string]::IsNullOrWhiteSpace($plainPassword)) {
        throw 'Password cannot be empty.'
    }

    $json = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8
    $config = $json | ConvertFrom-Json
    Ensure-Property -Object $config -Name 'HostAgent' -Value ([pscustomobject]@{})
    $hostAgent = $config.HostAgent
    Ensure-Property -Object $hostAgent -Name 'SelfUpgrade' -Value ([pscustomobject]@{})
    Ensure-Property -Object $hostAgent -Name 'CredentialStore' -Value ([pscustomobject]@{})
    Ensure-Property -Object $hostAgent.CredentialStore -Name 'FilePath' -Value ''
    Ensure-Property -Object $hostAgent.CredentialStore -Name 'AutomationMode' -Value $AutomationMode
    Ensure-Property -Object $hostAgent.CredentialStore -Name 'ProtectionScope' -Value $ProtectionScope
    Ensure-Property -Object $hostAgent.CredentialStore -Name 'EntropyPurpose' -Value $EntropyPurpose
    Ensure-Property -Object $hostAgent.SelfUpgrade -Name 'ServiceAccountName' -Value ''
    Ensure-Property -Object $hostAgent.SelfUpgrade -Name 'ServiceAccountPasswordCredentialKey' -Value ''

    $storePath = if ([string]::IsNullOrWhiteSpace($CredentialStorePath)) {
        $configuredStorePath = [string]$hostAgent.CredentialStore.FilePath
        Resolve-PathText -Value $configuredStorePath -Fallback (Join-Path $installPath 'hostagent.credentials.json')
    }
    else {
        Resolve-PathText -Value $CredentialStorePath -Fallback ''
    }

    $entry = [ordered]@{
        userName = $UserName.Trim()
        encryptedPassword = Protect-Password -PlainText $plainPassword -Scope $ProtectionScope -Purpose $EntropyPurpose
        protectionProvider = 'WindowsDpapi'
        protectionScope = $ProtectionScope
        description = 'Written by set-hostagent-credential-store.ps1.'
        updatedUtc = [DateTimeOffset]::UtcNow
    }

    $document = if (Test-Path -LiteralPath $storePath -PathType Leaf) {
        Get-Content -LiteralPath $storePath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    else {
        [pscustomobject]@{
            formatVersion = 1
            updatedUtc = [DateTimeOffset]::UtcNow
            credentials = [pscustomobject]@{}
        }
    }

    Ensure-Property -Object $document -Name 'formatVersion' -Value 1
    Ensure-Property -Object $document -Name 'credentials' -Value ([pscustomobject]@{})
    if ($null -eq $document.credentials.PSObject.Properties[$CredentialKey]) {
        $document.credentials | Add-Member -NotePropertyName $CredentialKey -NotePropertyValue ([pscustomobject]$entry)
    }
    else {
        $document.credentials.PSObject.Properties[$CredentialKey].Value = [pscustomobject]$entry
    }
    $document.updatedUtc = [DateTimeOffset]::UtcNow

    $hostAgent.SelfUpgrade.ServiceAccountName = $UserName.Trim()
    $hostAgent.SelfUpgrade.ServiceAccountPasswordCredentialKey = $CredentialKey
    $hostAgent.CredentialStore.AutomationMode = $AutomationMode
    $hostAgent.CredentialStore.FilePath = $storePath
    $hostAgent.CredentialStore.ProtectionScope = $ProtectionScope
    $hostAgent.CredentialStore.EntropyPurpose = $EntropyPurpose

    if ($AlsoSetDefaultServiceAppCredential) {
        $hostAgent.ServiceAppUserName = $UserName.Trim()
        $hostAgent.ServiceAppPasswordCredentialKey = $CredentialKey
    }

    if ($AlsoSetDefaultIisAppPoolCredential) {
        $hostAgent.IisAppPoolUserName = $UserName.Trim()
        $hostAgent.IisAppPoolPasswordCredentialKey = $CredentialKey
    }

    if ($PSCmdlet.ShouldProcess($storePath, "Write HostAgent credential '$CredentialKey'")) {
        $storeDirectory = Split-Path -Parent $storePath
        if (-not [string]::IsNullOrWhiteSpace($storeDirectory)) {
            New-Item -ItemType Directory -Path $storeDirectory -Force | Out-Null
        }
        $document | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $storePath -Encoding UTF8
    }

    if ($PSCmdlet.ShouldProcess($settingsPath, 'Update HostAgent credential-store settings')) {
        $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    }

    if ($WhatIfPreference) {
        Write-Host "Credential '$CredentialKey' would be written to $storePath"
        Write-Host "HostAgent self-upgrade settings would be updated in $settingsPath"
    }
    else {
        Write-Host "Credential '$CredentialKey' was written to $storePath"
        Write-Host "Updated HostAgent self-upgrade settings in $settingsPath"
        Write-Host 'Restart the active HostAgent service so it reloads these settings.'
    }
}
finally {
    $plainPassword = $null
}
