param(
    [Parameter(Mandatory = $true)]
    [string[]] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-PortableEncryptionKey {
    $bytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    'base64:' + [Convert]::ToBase64String($bytes)
}

function ConvertTo-AesKey {
    param([Parameter(Mandatory = $true)][string] $KeyText)

    $trimmed = $KeyText.Trim()
    if ($trimmed.StartsWith('base64:', [System.StringComparison]::OrdinalIgnoreCase)) {
        $bytes = [Convert]::FromBase64String($trimmed.Substring('base64:'.Length))
        if ($bytes.Length -ne 32) {
            throw 'Portable encryption key must decode to 32 bytes.'
        }

        return $bytes
    }

    return [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($trimmed))
}

function Protect-PortableSecret {
    param(
        [AllowNull()][string] $Value,
        [Parameter(Mandatory = $true)][byte[]] $Key
    )

    if ([string]::IsNullOrEmpty($Value) -or $Value.StartsWith('enc:aesgcm:v1:', [System.StringComparison]::Ordinal)) {
        return $Value
    }

    $nonce = [byte[]]::new(12)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($nonce)
    $plainText = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $cipherText = [byte[]]::new($plainText.Length)
    $tag = [byte[]]::new(16)
    $aes = [System.Security.Cryptography.AesGcm]::new($Key, $tag.Length)
    try {
        $aes.Encrypt($nonce, $plainText, $cipherText, $tag)
    }
    finally {
        $aes.Dispose()
    }

    'enc:aesgcm:v1:{0}:{1}:{2}' -f @(
        [Convert]::ToBase64String($nonce),
        [Convert]::ToBase64String($cipherText),
        [Convert]::ToBase64String($tag))
}

function Ensure-Property {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)] $Value
    )

    if ($null -eq $Object.PSObject.Properties[$Name]) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Remove-PropertyIfPresent {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($null -ne $Object.PSObject.Properties[$Name]) {
        $Object.PSObject.Properties.Remove($Name)
    }
}

function Protect-ConfigFile {
    param([Parameter(Mandatory = $true)][string] $FilePath)

    $json = Get-Content -LiteralPath $FilePath -Raw
    $config = $json | ConvertFrom-Json
    Ensure-Property -Object $config -Name 'security' -Value ([pscustomobject]@{
        portableEncryptionKey = ''
        portableEncryptionKeyEnvironmentVariable = ''
    })
    Ensure-Property -Object $config.security -Name 'portableEncryptionKey' -Value ''
    Ensure-Property -Object $config.security -Name 'portableEncryptionKeyEnvironmentVariable' -Value ''

    if ([string]::IsNullOrWhiteSpace([string]$config.security.portableEncryptionKey)) {
        $config.security.portableEncryptionKey = New-PortableEncryptionKey
    }

    $key = ConvertTo-AesKey -KeyText ([string]$config.security.portableEncryptionKey)
    if ($null -ne $config.hostAgent) {
        Ensure-Property -Object $config.hostAgent -Name 'serviceAccountCredentialKey' -Value ''
        Ensure-Property -Object $config.hostAgent -Name 'iisAppPoolPasswordCredentialKey' -Value ''
        Ensure-Property -Object $config.hostAgent -Name 'credentialStore' -Value ([pscustomobject]@{
            automationMode = ''
            filePath = ''
            protectionScope = 'LocalMachine'
            entropyPurpose = 'OpenModulePlatform.HostAgent.CredentialStore.v1'
        })

        if ($null -ne $config.hostAgent.PSObject.Properties['serviceAccountPassword']) {
            $config.hostAgent.serviceAccountPassword = Protect-PortableSecret -Value $config.hostAgent.serviceAccountPassword -Key $key
        }

        if ($null -ne $config.hostAgent.PSObject.Properties['iisAppPoolPassword']) {
            $config.hostAgent.iisAppPoolPassword = Protect-PortableSecret -Value $config.hostAgent.iisAppPoolPassword -Key $key
        }

        if ($null -ne $config.hostAgent.iisAppPoolOverrides) {
            foreach ($property in $config.hostAgent.iisAppPoolOverrides.PSObject.Properties) {
                $identity = $property.Value
                if ($null -eq $identity) {
                    continue
                }

                Ensure-Property -Object $identity -Name 'passwordCredentialKey' -Value ''
                if ($null -ne $identity.PSObject.Properties['password']) {
                    $identity.password = Protect-PortableSecret -Value $identity.password -Key $key
                }
                elseif ($null -ne $identity.PSObject.Properties['Password']) {
                    $identity.Password = Protect-PortableSecret -Value $identity.Password -Key $key
                }
            }
        }

        if ($null -ne $config.hostAgent.appSettings -and $null -ne $config.hostAgent.appSettings.HostAgent) {
            $hostAgentSettings = $config.hostAgent.appSettings.HostAgent
            Remove-PropertyIfPresent -Object $hostAgentSettings -Name 'IisAppPoolPassword'
            Ensure-Property -Object $hostAgentSettings -Name 'IisAppPoolPasswordCredentialKey' -Value ''

            if ($null -ne $hostAgentSettings.IisAppPoolOverrides) {
                foreach ($property in $hostAgentSettings.IisAppPoolOverrides.PSObject.Properties) {
                    if ($null -ne $property.Value) {
                        Remove-PropertyIfPresent -Object $property.Value -Name 'Password'
                        Remove-PropertyIfPresent -Object $property.Value -Name 'password'
                        Ensure-Property -Object $property.Value -Name 'PasswordCredentialKey' -Value ''
                    }
                }
            }

            if ($null -ne $hostAgentSettings.SelfUpgrade) {
                Remove-PropertyIfPresent -Object $hostAgentSettings.SelfUpgrade -Name 'ServiceAccountPassword'
                Ensure-Property -Object $hostAgentSettings.SelfUpgrade -Name 'ServiceAccountPasswordCredentialKey' -Value ''
            }

            Ensure-Property -Object $hostAgentSettings -Name 'CredentialStore' -Value ([pscustomobject]@{
                AutomationMode = ''
                FilePath = ''
                ProtectionScope = 'LocalMachine'
                EntropyPurpose = 'OpenModulePlatform.HostAgent.CredentialStore.v1'
            })
        }
    }

    $config |
        ConvertTo-Json -Depth 100 |
        Set-Content -LiteralPath $FilePath -Encoding UTF8

    Write-Host "Protected bootstrap config secrets in $FilePath"
}

foreach ($item in $Path) {
    $resolved = Resolve-Path -LiteralPath $item
    foreach ($pathInfo in $resolved) {
        if ((Get-Item -LiteralPath $pathInfo.Path) -is [System.IO.DirectoryInfo]) {
            Get-ChildItem -LiteralPath $pathInfo.Path -Filter '*.json' -File -Recurse |
                ForEach-Object { Protect-ConfigFile -FilePath $_.FullName }
        }
        else {
            Protect-ConfigFile -FilePath $pathInfo.Path
        }
    }
}
