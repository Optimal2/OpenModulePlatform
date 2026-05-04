# File: scripts/write-local-runtime-config.ps1
[CmdletBinding()]
param(
    [string]$RuntimeRoot = 'E:\OMP',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$HostKey = 'sample-host',
    [string]$PortalTitle = 'OMP Portal',
    [switch]$Overwrite
)

$ErrorActionPreference = 'Stop'

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Object,
        [switch]$Overwrite
    )

    if ((Test-Path -LiteralPath $Path) -and -not $Overwrite) {
        Write-Warning "Existing config preserved: $Path"
        return
    }

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $json = $Object | ConvertTo-Json -Depth 20
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
    Write-Host "Wrote config: $Path"
}

# TrustServerCertificate=true is only for local development with dev SQL Server certificates.
$connectionString = "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true;"
$dataProtectionKeyPath = Join-Path $RuntimeRoot 'DataProtectionKeys'

$ompAuthConfig = [ordered]@{
    CookieName = '.OpenModulePlatform.Auth'
    LoginPath = '/auth/login'
    AccessDeniedPath = '/status/403'
    ApplicationName = 'OpenModulePlatform'
    DataProtectionKeyPath = $dataProtectionKeyPath
}

$portalConfig = [ordered]@{
    Portal = [ordered]@{
        Title = $PortalTitle
        DefaultCulture = 'sv-SE'
        SupportedCultures = @('sv-SE', 'en-US')
        PortalTopBar = [ordered]@{
            Enabled = $true
            PortalBaseUrl = '/'
        }
        AllowAnonymous = $false
        UseForwardedHeaders = $false
        PermissionMode = 'Any'
    }
    ConnectionStrings = [ordered]@{
        OmpDb = $connectionString
    }
    OmpAuth = $ompAuthConfig
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default = 'Information'
            'Microsoft.AspNetCore' = 'Warning'
        }
    }
}

$authConfig = [ordered]@{
    ConnectionStrings = [ordered]@{
        OmpDb = $connectionString
    }
    OmpAuth = $ompAuthConfig
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default = 'Information'
            'Microsoft.AspNetCore' = 'Warning'
        }
    }
}

$hostAgentConfig = [ordered]@{
    ConnectionStrings = [ordered]@{
        OmpDb = $connectionString
    }
    HostAgent = [ordered]@{
        HostKey = $HostKey
        HostName = $env:COMPUTERNAME
        RefreshSeconds = 30
        CentralArtifactRoot = (Join-Path $RuntimeRoot 'ArtifactStore')
        LocalArtifactCacheRoot = (Join-Path $RuntimeRoot 'ArtifactCache')
        ProvisionAppInstanceArtifacts = $true
        ProvisionExplicitRequirements = $true
        MaxArtifactsPerCycle = 100
        EnableRpc = $true
        RpcPipeName = ''
        RpcRequestTimeoutSeconds = 60
    }
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default = 'Information'
            'Microsoft.Hosting.Lifetime' = 'Information'
        }
    }
}

$workerManagerConfig = [ordered]@{
    ConnectionStrings = [ordered]@{
        OmpDb = $connectionString
    }
    WorkerManager = [ordered]@{
        CatalogMode = 'OmpDatabase'
        HostKey = $HostKey
        HostName = $env:COMPUTERNAME
        RefreshSeconds = 15
        WorkerProcessPath = (Join-Path $RuntimeRoot 'Services\WorkerProcessHost\OpenModulePlatform.WorkerProcessHost.exe')
        StopTimeoutSeconds = 15
        RestartDelaySeconds = 5
        RestartWindowSeconds = 300
        MaxRestartsPerWindow = 5
        OmpDatabase = [ordered]@{
            RuntimeKind = 'windows-worker-plugin'
            RunningDesiredState = 1
            UseHostArtifactCache = $true
        }
        HostAgentRpc = [ordered]@{
            Enabled = $true
            PipeName = ''
            TimeoutSeconds = 60
        }
        Workers = @()
    }
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default = 'Information'
            'Microsoft.Hosting.Lifetime' = 'Information'
        }
    }
}

New-Item -ItemType Directory -Path $dataProtectionKeyPath -Force | Out-Null
Write-JsonFile -Path (Join-Path $RuntimeRoot 'Sites\Portal\appsettings.json') -Object $portalConfig -Overwrite:$Overwrite
Write-JsonFile -Path (Join-Path $RuntimeRoot 'WebApps\auth\appsettings.json') -Object $authConfig -Overwrite:$Overwrite
Write-JsonFile -Path (Join-Path $RuntimeRoot 'Services\HostAgent\appsettings.json') -Object $hostAgentConfig -Overwrite:$Overwrite
Write-JsonFile -Path (Join-Path $RuntimeRoot 'Services\WorkerManager\appsettings.json') -Object $workerManagerConfig -Overwrite:$Overwrite
