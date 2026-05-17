# File: scripts/write-local-runtime-config.ps1
[CmdletBinding()]
param(
    # E:\OMP is the documented local development runtime root for this repository.
    # Pass -RuntimeRoot explicitly when a workstation uses a different local layout.
    [string]$RuntimeRoot = 'E:\OMP',
    [string]$SqlServer = 'localhost',
    [string]$Database = 'OpenModulePlatform',
    [string]$HostKey = 'sample-host',
    [string]$PortalTitle = 'OMP Portal',
    [switch]$Overwrite
)

$ErrorActionPreference = 'Stop'
$script:JsonSerializationDepth = 12

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
    # A bare file name has no parent directory to create; root paths such as C:\
    # already exist and are left untouched.
    if (-not [string]::IsNullOrWhiteSpace($directory) -and
        -not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $json = $Object | ConvertTo-Json -Depth $script:JsonSerializationDepth
    Set-Content -LiteralPath $Path -Value $json -Encoding UTF8
    Write-Host "Wrote config: $Path"
}

# TrustServerCertificate=true is only for local development with dev SQL Server certificates.
# Do not reuse the generated appsettings files for shared test or production environments.
function Get-OmpConnectionString {
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Data Source'] = $SqlServer
    $builder['Initial Catalog'] = $Database
    $builder['Integrated Security'] = $true
    $builder['TrustServerCertificate'] = $true
    return $builder.ConnectionString
}

function New-NLogConfig {
    param([Parameter(Mandatory = $true)][string]$AppName)

    return [ordered]@{
        autoReload = $true
        throwConfigExceptions = $true
        variables = [ordered]@{
            appName = $AppName
            logDirectory = '${basedir}/logs'
        }
        targets = [ordered]@{
            logfile = [ordered]@{
                type = 'File'
                fileName = '${var:logDirectory}/${var:appName}-${shortdate}.log'
                layout = '${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}'
            }
            console = [ordered]@{
                type = 'Console'
                layout = '${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}'
            }
        }
        rules = @(
            [ordered]@{
                logger = 'Microsoft.Hosting.Lifetime'
                minLevel = 'Info'
                writeTo = 'console,logfile'
                final = $true
            },
            [ordered]@{
                logger = 'Microsoft.*'
                maxLevel = 'Info'
                final = $true
            },
            [ordered]@{
                logger = '*'
                minLevel = 'Info'
                writeTo = 'console,logfile'
            }
        )
    }
}

$connectionString = Get-OmpConnectionString
$dataProtectionKeyPath = Join-Path $RuntimeRoot 'DataProtectionKeys'
$artifactStoreRoot = Join-Path $RuntimeRoot 'ArtifactStore'

$ompAuthConfig = [ordered]@{
    CookieName = '.OpenModulePlatform.Auth'
    LoginPath = '/auth/login'
    LogoutPath = '/auth/logout'
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
    ArtifactUpload = [ordered]@{
        ArtifactStoreRoot = $artifactStoreRoot
        MaxUploadBytes = 536870912
    }
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

$contentWebAppConfig = [ordered]@{
    Portal = [ordered]@{
        Title = 'Content'
        DefaultCulture = 'sv-SE'
        SupportedCultures = @('sv-SE', 'en-US')
        PortalTopBar = [ordered]@{
            Enabled = $true
            PortalBaseUrl = '/'
        }
        AllowAnonymous = $false
        PermissionMode = 'Any'
    }
    ConnectionStrings = [ordered]@{
        OmpDb = $connectionString
    }
    OmpAuth = $ompAuthConfig
    ContentWebAppModule = [ordered]@{
        AppInstanceId = '11111111-1111-1111-1111-111111111232'
        HomeSlug = 'home'
        ServerReportsPath = 'App_Data/ContentReports'
        AllowedServerReportDatabases = @($Database)
        ServerReportDefaultMaxRows = 100
        ServerReportMaxRowsLimit = 1000
        ServerReportQueryTimeoutSeconds = 30
    }
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default = 'Information'
            'Microsoft.AspNetCore' = 'Warning'
        }
    }
}

$iframeWebAppConfig = [ordered]@{
    Portal = [ordered]@{
        Title = 'iFrame Web App Module'
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

$hostAgentConfig = [ordered]@{
    ConnectionStrings = [ordered]@{
        OmpDb = $connectionString
    }
    HostAgent = [ordered]@{
        HostKey = $HostKey
        HostName = $env:COMPUTERNAME
        RefreshSeconds = 30
        CentralArtifactRoot = $artifactStoreRoot
        LocalArtifactCacheRoot = (Join-Path $RuntimeRoot 'ArtifactCache')
        MaterializeTemplates = $true
        ProcessHostDeployments = $true
        ProvisionAppInstanceArtifacts = $true
        ProvisionExplicitRequirements = $true
        DeployWebApps = $true
        IisSiteName = 'OpenModulePlatform'
        WebAppsRoot = (Join-Path $RuntimeRoot 'WebApps')
        PortalPhysicalPath = (Join-Path $RuntimeRoot 'Sites\Portal')
        UseAppOfflineForWebAppDeployment = $true
        AppOfflineShutdownDelayMilliseconds = 1500
        StopIisAppPoolForWebAppDeployment = $false
        StartIisAppPoolAfterWebAppDeployment = $false
        IisAppPoolStopTimeoutSeconds = 30
        WebAppDeploymentExcludedEntries = @('appsettings.json', 'appsettings.*.json', 'logs', 'App_Data')
        DeployServiceApps = $true
        ServicesRoot = (Join-Path $RuntimeRoot 'Services')
        StopServiceForServiceAppDeployment = $true
        StartServiceAfterServiceAppDeployment = $true
        ServiceAppStopTimeoutSeconds = 30
        ServiceAppStartTimeoutSeconds = 30
        ServiceAppDeploymentExcludedEntries = @('appsettings.json', 'appsettings.*.json', 'logs', 'App_Data')
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
    NLog = New-NLogConfig -AppName 'OpenModulePlatform.HostAgent.WindowsService'
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
    NLog = New-NLogConfig -AppName 'OpenModulePlatform.WorkerManager.WindowsService'
}

New-Item -ItemType Directory -Path $dataProtectionKeyPath -Force | Out-Null
Write-JsonFile -Path (Join-Path $RuntimeRoot 'Sites\Portal\appsettings.json') -Object $portalConfig -Overwrite:$Overwrite
Write-JsonFile -Path (Join-Path $RuntimeRoot 'WebApps\auth\appsettings.json') -Object $authConfig -Overwrite:$Overwrite
Write-JsonFile -Path (Join-Path $RuntimeRoot 'WebApps\content\appsettings.json') -Object $contentWebAppConfig -Overwrite:$Overwrite
Write-JsonFile -Path (Join-Path $RuntimeRoot 'WebApps\iFrameWebAppModule\appsettings.json') -Object $iframeWebAppConfig -Overwrite:$Overwrite
Write-JsonFile -Path (Join-Path $RuntimeRoot 'Services\HostAgent\appsettings.json') -Object $hostAgentConfig -Overwrite:$Overwrite
Write-JsonFile -Path (Join-Path $RuntimeRoot 'Services\WorkerManager\appsettings.json') -Object $workerManagerConfig -Overwrite:$Overwrite
