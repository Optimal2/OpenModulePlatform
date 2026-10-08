# Shared contract for portable bootstrap passwords. These are the fields the
# bootstrapper decrypts; appSettings and SQL secrets are validated separately
# by the package builder and must not silently become encrypted runtime settings.
function Assert-BootstrapSqlPasswordSupported {
    param([Parameter(Mandatory = $true)][object]$Config)

    $sql = $Config.PSObject.Properties['sql']
    if ($null -eq $sql -or $null -eq $sql.Value) { return }
    $password = $sql.Value.PSObject.Properties['password']
    if ($null -eq $password) { return }

    # ResolveInstallerSecret handles only the five HostAgent field patterns.
    # Reject even malformed envelopes here, before encryption or file writes.
    if (([string]$password.Value).TrimStart().StartsWith('enc:aesgcm:', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'sql.password cannot be encrypted: the installer does not decrypt this field. Use integrated security and leave sql.password empty.'
    }
}

function Get-BootstrapPortableSecretFields {
    param([Parameter(Mandatory = $true)][object]$Config)

    foreach ($pattern in @(
        'hostAgent.serviceAccountPassword',
        'hostAgent.iisAppPoolPassword',
        'hostAgent.serviceAppPassword',
        'hostAgent.iisAppPoolOverrides.*.password',
        'hostAgent.serviceAppIdentityOverrides.*.password'
    )) {
        $nodes = @([pscustomobject]@{ Value = $Config; Path = '' })
        foreach ($segment in $pattern.Split('.')) {
            $nodes = @(foreach ($node in $nodes) {
                if ($null -eq $node.Value) { continue }
                foreach ($property in $node.Value.PSObject.Properties) {
                    if ($segment -eq '*' -or $property.Name -ieq $segment) {
                        $path = if ($node.Path) { $node.Path + '.' + $property.Name } else { $property.Name }
                        [pscustomobject]@{ Value = $property.Value; Path = $path; Property = $property }
                    }
                }
            })
        }
        $nodes
    }
}

function Test-BootstrapEncryptedSecret {
    param([AllowNull()][string]$Value)

    # Protect-PortableSecret emits nonce:ciphertext:tag, all base64. Check the
    # envelope without the key; this cannot authenticate the ciphertext.
    if ($null -eq $Value -or -not $Value.StartsWith('enc:aesgcm:v1:', [StringComparison]::Ordinal)) {
        return $false
    }
    $parts = $Value.Substring('enc:aesgcm:v1:'.Length).Split(':')
    if ($parts.Count -ne 3) { return $false }
    try {
        foreach ($part in $parts) {
            if ([string]::IsNullOrEmpty($part) -or $part -cmatch '[^A-Za-z0-9+/=]') { return $false }
            $bytes = [Convert]::FromBase64String($part)
            if ([Convert]::ToBase64String($bytes) -cne $part) { return $false }
        }
        return ([Convert]::FromBase64String($parts[0]).Length -eq 12 -and
            [Convert]::FromBase64String($parts[1]).Length -gt 0 -and
            [Convert]::FromBase64String($parts[2]).Length -eq 16)
    }
    catch { return $false }
}
