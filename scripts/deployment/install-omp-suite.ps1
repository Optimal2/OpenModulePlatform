# File: scripts/deployment/install-omp-suite.ps1
[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'omp-suite.local.psd1'),
    [ValidateSet('Source', 'Package', '')]
    [string]$DeploymentMode = '',
    [switch]$SkipPackageBuild,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:appcmdPath = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Confirm-DeploymentAction {
    param([string]$Message)
    if ($Yes) { return $true }
    $answer = Read-Host "$Message [y/j/N]"
    return $answer -imatch '^(y|yes|j|ja)$'
}

function Import-RequiredDeploymentConfig {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $samplePath = Join-Path $PSScriptRoot 'omp-suite.config.sample.psd1'
        throw "Deployment config not found: $Path. Copy $samplePath to omp-suite.local.psd1 and adjust it for this machine."
    }

    $config = Import-PowerShellDataFile -LiteralPath $Path
    if ($null -eq $config) {
        throw "Deployment config could not be read: $Path"
    }

    return $config
}

function Get-ConfigValue {
    param(
        [hashtable]$Config,
        [string]$Name,
        $DefaultValue = $null
    )

    if ($Config.ContainsKey($Name) -and $null -ne $Config[$Name]) {
        return $Config[$Name]
    }

    return $DefaultValue
}

function Get-NestedConfigValue {
    param(
        [hashtable]$Config,
        [string]$Section,
        [string]$Name,
        $DefaultValue = $null
    )

    if ($Config.ContainsKey($Section) -and $Config[$Section] -is [hashtable]) {
        $sectionTable = [hashtable]$Config[$Section]
        if ($sectionTable.ContainsKey($Name) -and $null -ne $sectionTable[$Name]) {
            return $sectionTable[$Name]
        }
    }

    return $DefaultValue
}

function Resolve-DeploymentPath {
    param(
        [string]$Path,
        [string]$BasePath
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
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

function Invoke-NativeCheckedRedacted {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string[]]$DisplayArguments
    )

    Write-Host "> $FilePath $($DisplayArguments -join ' ')"
    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($DisplayArguments -join ' ')"
    }
}

function Test-IsWindowsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal -ArgumentList $identity
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertFrom-SecureStringToPlainText {
    param([Parameter(Mandatory = $true)][Security.SecureString]$SecureString)

    # IIS and Windows service APIs still require a plain-text password at the
    # configuration boundary. The script never stores this value in Git and keeps
    # the managed string scope as short as practical.
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Resolve-WindowsAccountName {
    param([string]$Account)

    if ([string]::IsNullOrWhiteSpace($Account)) {
        return ''
    }

    $trimmed = $Account.Trim()
    if ($trimmed.StartsWith('.\', [System.StringComparison]::Ordinal)) {
        return "$env:COMPUTERNAME\$($trimmed.Substring(2))"
    }

    if ($trimmed.IndexOf('\') -lt 0 -and $trimmed.IndexOf('@') -lt 0) {
        return "$env:COMPUTERNAME\$trimmed"
    }

    return $trimmed
}

function Get-RunAsCredential {
    param(
        [string]$User,
        [string]$Password
    )

    if ([string]::IsNullOrWhiteSpace($User)) {
        return $null
    }

    $resolvedUser = Resolve-WindowsAccountName -Account $User
    if ([string]::IsNullOrWhiteSpace($Password)) {
        $securePassword = Read-Host "Password for $resolvedUser" -AsSecureString
    }
    else {
        $securePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
    }

    return New-Object System.Management.Automation.PSCredential -ArgumentList $resolvedUser, $securePassword
}

function ConvertTo-SqlBracketName {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '[' + $Value.Replace(']', ']]') + ']'
}

function ConvertTo-SqlUnicodeLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "N'$($Value.Replace("'", "''"))'"
}

function ConvertTo-NormalizedCertificateText {
    param([string]$Value)

    return ($Value -replace '\s', '').ToLowerInvariant()
}

function Get-ConfigEntryValue {
    param(
        [object]$Entry,
        [string]$Name,
        $DefaultValue = $null
    )

    if ($null -eq $Entry) {
        return $DefaultValue
    }

    if ($Entry -is [hashtable] -and $Entry.ContainsKey($Name) -and $null -ne $Entry[$Name]) {
        return $Entry[$Name]
    }

    $property = $Entry.PSObject.Properties[$Name]
    if ($null -ne $property -and $null -ne $property.Value) {
        return $property.Value
    }

    return $DefaultValue
}

function Resolve-ConfiguredHostProfile {
    if ($script:ConfiguredHosts.Count -eq 0) {
        return
    }

    $candidate = $script:HostKey
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = $env:COMPUTERNAME
    }

    foreach ($hostEntry in $script:ConfiguredHosts) {
        $knownHost = [string](Get-ConfigEntryValue -Entry $hostEntry -Name 'HostKey' -DefaultValue '')
        if ([string]::IsNullOrWhiteSpace($knownHost)) {
            continue
        }

        $knownShortName = ($knownHost -split '\.')[0]
        if ($candidate.Equals($knownHost, [StringComparison]::OrdinalIgnoreCase) -or
            $candidate.Equals($knownShortName, [StringComparison]::OrdinalIgnoreCase)) {
            $script:HostKey = $knownHost

            if ([string]::IsNullOrWhiteSpace($script:HostName)) {
                $displayName = [string](Get-ConfigEntryValue -Entry $hostEntry -Name 'DisplayName' -DefaultValue '')
                $script:HostName = if ([string]::IsNullOrWhiteSpace($displayName)) { $env:COMPUTERNAME } else { $displayName }
            }

            if ([string]::IsNullOrWhiteSpace($script:IisCertificateSerialNumber)) {
                $script:IisCertificateSerialNumber = [string](Get-ConfigEntryValue -Entry $hostEntry -Name 'CertificateSerialNumber' -DefaultValue '')
            }

            if ([string]::IsNullOrWhiteSpace($script:IisCertificateThumbprint)) {
                $script:IisCertificateThumbprint = [string](Get-ConfigEntryValue -Entry $hostEntry -Name 'CertificateThumbprint' -DefaultValue '')
            }

            return
        }
    }
}

function Get-SqlConnectionString {
    param([string]$TargetDatabase)

    Add-Type -AssemblyName System.Data
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Data Source'] = $script:SqlServer
    $builder['Initial Catalog'] = $TargetDatabase
    $builder['TrustServerCertificate'] = $true
    if ([string]::Equals($script:SqlAuthentication, 'SqlLogin', [StringComparison]::OrdinalIgnoreCase)) {
        $builder['Integrated Security'] = $false
        $builder['User ID'] = $script:SqlUser
        $builder['Password'] = $script:SqlPassword
    }
    else {
        $builder['Integrated Security'] = $true
    }

    return $builder.ConnectionString
}

function Split-SqlBatches {
    param([Parameter(Mandatory = $true)][string]$SqlText)

    $batches = New-Object System.Collections.Generic.List[string]
    $reader = New-Object System.IO.StringReader($SqlText)
    $builder = New-Object System.Text.StringBuilder

    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) {
            break
        }

        if ($line -match '^\s*GO(?:\s+([0-9]+))?\s*(?:--.*)?$') {
            $batch = $builder.ToString()
            if (-not [string]::IsNullOrWhiteSpace($batch)) {
                $repeat = 1
                if ($Matches[1]) {
                    $repeat = [int]$Matches[1]
                }

                for ($i = 0; $i -lt $repeat; $i++) {
                    $batches.Add($batch)
                }
            }

            [void]$builder.Clear()
            continue
        }

        [void]$builder.AppendLine($line)
    }

    $lastBatch = $builder.ToString()
    if (-not [string]::IsNullOrWhiteSpace($lastBatch)) {
        $batches.Add($lastBatch)
    }

    return $batches
}

function Invoke-SqlText {
    param(
        [Parameter(Mandatory = $true)][string]$Query,
        [string]$TargetDatabase = $script:Database,
        [string]$SourceName = '<inline SQL>'
    )

    $connection = New-Object System.Data.SqlClient.SqlConnection (Get-SqlConnectionString -TargetDatabase $TargetDatabase)
    $connection.Open()
    try {
        $batchNumber = 0
        foreach ($batch in (Split-SqlBatches -SqlText $Query)) {
            $batchNumber++
            $command = $connection.CreateCommand()
            $command.CommandText = $batch
            # Finite timeout avoids indefinite hangs while still allowing large schema scripts.
            $command.CommandTimeout = 3600
            try {
                [void]$command.ExecuteNonQuery()
            }
            catch {
                throw "SQL failed in '$SourceName' batch $batchNumber on database '$TargetDatabase'. $($_.Exception.Message)"
            }
            finally {
                $command.Dispose()
            }
        }
    }
    finally {
        $connection.Dispose()
    }
}

function Invoke-SqlFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$TargetDatabase = $script:Database,
        [switch]$PatchBootstrapPrincipal
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "SQL file not found: $Path"
    }

    $content = Get-Content -LiteralPath $Path -Raw
    $content = $content.Replace('USE [OpenModulePlatform]', 'USE ' + (ConvertTo-SqlBracketName -Value $script:Database))

    if ($PatchBootstrapPrincipal) {
        $bootstrapPrincipal = @($script:BootstrapPortalAdminPrincipals)[0]
        if ([string]::IsNullOrWhiteSpace($bootstrapPrincipal) -or $bootstrapPrincipal -like 'DOMAIN\*') {
            throw 'Set BootstrapPortalAdminPrincipals in the deployment config before running the OMP initializer.'
        }

        $principalLiteral = ConvertTo-SqlUnicodeLiteral -Value $bootstrapPrincipal
        $principalTypeLiteral = ConvertTo-SqlUnicodeLiteral -Value $script:BootstrapPortalAdminPrincipalType
        $content = [regex]::Replace($content, "DECLARE\s+@BootstrapPortalAdminPrincipal\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';", "DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = $principalLiteral;")
        $content = [regex]::Replace($content, "DECLARE\s+@BootstrapPortalAdminPrincipalType\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';", "DECLARE @BootstrapPortalAdminPrincipalType nvarchar(50) = $principalTypeLiteral;")
    }

    Invoke-SqlText -Query $content -TargetDatabase $TargetDatabase -SourceName $Path
}

function Ensure-Database {
    Write-Step 'Checking target database'
    $dbName = $script:Database.Replace("'", "''")

    if ($script:CreateDatabase) {
        Invoke-SqlText -TargetDatabase 'master' -Query @"
DECLARE @DatabaseName sysname = N'$dbName';
DECLARE @sql nvarchar(max);

IF DB_ID(@DatabaseName) IS NULL
BEGIN
    SET @sql = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName);
    EXEC sys.sp_executesql @sql;
END
"@
        return
    }

    Invoke-SqlText -TargetDatabase 'master' -Query @"
DECLARE @DatabaseName sysname = N'$dbName';

IF DB_ID(@DatabaseName) IS NULL
BEGIN
    RAISERROR('Database does not exist: %s. Create the database before running the installer, or set Options.CreateDatabase to true for explicit dev/test bootstrap.', 11, 1, @DatabaseName);
END
"@
}

function Ensure-AdditionalBootstrapPrincipals {
    $principals = @($script:BootstrapPortalAdminPrincipals)
    if ($principals.Count -le 1) {
        return
    }

    Write-Step 'Ensuring additional bootstrap principals'
    $values = @()
    foreach ($principal in $principals | Select-Object -Skip 1) {
        if (-not [string]::IsNullOrWhiteSpace($principal)) {
            $values += '(' + (ConvertTo-SqlUnicodeLiteral -Value $script:BootstrapPortalAdminPrincipalType) + ', ' + (ConvertTo-SqlUnicodeLiteral -Value $principal) + ')'
        }
    }

    if ($values.Count -eq 0) {
        return
    }

    Invoke-SqlText -Query @"
DECLARE @PortalAdminsRoleId int;
SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';

DECLARE @Principals table(PrincipalType nvarchar(50) NOT NULL, Principal nvarchar(256) NOT NULL);
INSERT INTO @Principals(PrincipalType, Principal)
VALUES
$(($values -join ",`r`n"));

INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
SELECT @PortalAdminsRoleId, p.PrincipalType, p.Principal
FROM @Principals p
WHERE @PortalAdminsRoleId IS NOT NULL
  AND NOT EXISTS (
      SELECT 1
      FROM omp.RolePrincipals existing
      WHERE existing.RoleId = @PortalAdminsRoleId
        AND existing.PrincipalType = p.PrincipalType
        AND existing.Principal = p.Principal);
"@
}

function Ensure-RunAsDatabaseAccess {
    if (-not $script:GrantRunAsDatabaseAccess -or $null -eq $script:RunAsCredential) {
        return
    }

    Write-Step 'Ensuring database access for run-as account'
    $principal = $script:RunAsCredential.UserName.Replace("'", "''")
    Invoke-SqlText -TargetDatabase 'master' -Query @"
DECLARE @principal sysname = N'$principal';
DECLARE @sql nvarchar(max);

IF SUSER_ID(@principal) IS NULL
BEGIN
    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@principal) + N' FROM WINDOWS;';
    EXEC sys.sp_executesql @sql;
END
"@

    Invoke-SqlText -Query @"
DECLARE @principal sysname = N'$principal';
DECLARE @sql nvarchar(max);

IF DATABASE_PRINCIPAL_ID(@principal) IS NULL
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@principal) + N' FOR LOGIN ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
END

IF IS_ROLEMEMBER(N'db_owner', @principal) <> 1
BEGIN
    EXEC sys.sp_addrolemember N'db_owner', @principal;
END
"@
}

function Expand-PayloadZip {
    param(
        [Parameter(Mandatory = $true)][string]$ZipName,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $zipPath = Join-Path (Join-Path $script:PackageRoot 'payload') $ZipName
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw "Payload zip not found: $zipPath"
    }

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $Destination -Force
}

function Get-OmpConnectionString {
    if ([string]::Equals($script:SqlAuthentication, 'SqlLogin', [StringComparison]::OrdinalIgnoreCase)) {
        return "Server=$script:SqlServer;Database=$script:Database;User ID=$script:SqlUser;Password=$script:SqlPassword;TrustServerCertificate=True;"
    }

    return "Server=$script:SqlServer;Database=$script:Database;Trusted_Connection=True;TrustServerCertificate=True;"
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Write-RuntimeConfiguration {
    Write-Step 'Writing runtime configuration'
    $connectionString = Get-OmpConnectionString
    $odvAppPath = $script:OpenDocViewerAppPath.Trim('/')
    $odvBaseUrl = '/' + $odvAppPath + '/'
    $odvSampleUrl = $odvBaseUrl + 'sample.pdf'
    $portalTopBar = [ordered]@{
        Enabled = $true
        PortalBaseUrl = $script:PortalBaseUrl
    }

    New-Item -ItemType Directory -Path $script:DataProtectionKeyPath -Force | Out-Null

    $ompAuth = [ordered]@{
        CookieName = '.OpenModulePlatform.Auth'
        LoginPath = '/auth/login'
        LogoutPath = '/auth/logout'
        AccessDeniedPath = '/status/403'
        ApplicationName = 'OpenModulePlatform'
        DataProtectionKeyPath = $script:DataProtectionKeyPath
    }

    $webAppFolders = @(
        @{ Path = $script:PortalPath; IncludePortalTopBar = $true; IncludeOpenDocViewer = $false },
        @{ Path = (Join-Path $script:WebAppsRoot 'auth'); IncludePortalTopBar = $false; IncludeOpenDocViewer = $false },
        @{ Path = (Join-Path $script:WebAppsRoot 'ExampleWebAppModule'); IncludePortalTopBar = $true; IncludeOpenDocViewer = $true },
        @{ Path = (Join-Path $script:WebAppsRoot 'ExampleWebAppBlazorModule'); IncludePortalTopBar = $true; IncludeOpenDocViewer = $true },
        @{ Path = (Join-Path $script:WebAppsRoot 'ExampleServiceAppModule'); IncludePortalTopBar = $true; IncludeOpenDocViewer = $true },
        @{ Path = (Join-Path $script:WebAppsRoot 'ExampleWorkerAppModule'); IncludePortalTopBar = $true; IncludeOpenDocViewer = $true },
        @{ Path = (Join-Path $script:WebAppsRoot 'iFrameWebAppModule'); IncludePortalTopBar = $true; IncludeOpenDocViewer = $false }
    )

    foreach ($folder in $webAppFolders) {
        if (-not (Test-Path -LiteralPath $folder.Path -PathType Container)) {
            continue
        }

        $settings = [ordered]@{
            ConnectionStrings = [ordered]@{
                OmpDb = $connectionString
            }
            OmpAuth = $ompAuth
        }

        if ([bool]$folder.IncludePortalTopBar) {
            $settings.Portal = [ordered]@{
                PortalTopBar = $portalTopBar
            }
        }

        if ([string]::Equals([System.IO.Path]::GetFullPath([string]$folder.Path), [System.IO.Path]::GetFullPath($script:PortalPath), [StringComparison]::OrdinalIgnoreCase)) {
            $settings.Portal['Title'] = $script:PortalTitle
            $settings.Portal['DefaultCulture'] = $script:DefaultCulture
            $settings.Portal['SupportedCultures'] = $script:SupportedCultures
            $settings.Portal['AllowAnonymous'] = $script:PortalAllowAnonymous
            $settings.Portal['UseForwardedHeaders'] = $script:PortalUseForwardedHeaders
            $settings.Portal['PermissionMode'] = $script:PortalPermissionMode
        }

        if ([bool]$folder.IncludeOpenDocViewer) {
            $settings.OpenDocViewer = [ordered]@{
                BaseUrl = $odvBaseUrl
                SampleFileUrl = $odvSampleUrl
            }
        }

        Write-JsonFile -Value $settings -Path (Join-Path $folder.Path 'appsettings.Production.json')
    }

    $hostName = if ([string]::IsNullOrWhiteSpace($script:HostName)) { $env:COMPUTERNAME } else { $script:HostName }

    $hostAgentPath = Join-Path $script:ServicesRoot 'HostAgent'
    if (Test-Path -LiteralPath $hostAgentPath -PathType Container) {
        Write-JsonFile -Path (Join-Path $hostAgentPath 'appsettings.Production.json') -Value ([ordered]@{
            ConnectionStrings = [ordered]@{ OmpDb = $connectionString }
            HostAgent = [ordered]@{
                HostKey = $script:HostKey
                HostName = $hostName
                RefreshSeconds = 30
                CentralArtifactRoot = $script:ArtifactStoreRoot
                LocalArtifactCacheRoot = $script:ArtifactCacheRoot
                ProvisionAppInstanceArtifacts = $true
                ProvisionExplicitRequirements = $true
                MaxArtifactsPerCycle = 100
                EnableRpc = $true
                RpcPipeName = ''
                RpcRequestTimeoutSeconds = 60
            }
        })
    }

    $workerManagerPath = Join-Path $script:ServicesRoot 'WorkerManager'
    if (Test-Path -LiteralPath $workerManagerPath -PathType Container) {
        Write-JsonFile -Path (Join-Path $workerManagerPath 'appsettings.Production.json') -Value ([ordered]@{
            ConnectionStrings = [ordered]@{ OmpDb = $connectionString }
            WorkerManager = [ordered]@{
                CatalogMode = 'OmpDatabase'
                HostKey = $script:HostKey
                HostName = $hostName
                RefreshSeconds = 15
                WorkerProcessPath = (Join-Path $script:ServicesRoot 'WorkerProcessHost\OpenModulePlatform.WorkerProcessHost.exe')
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
        })
    }

    $workerProcessHostPath = Join-Path $script:ServicesRoot 'WorkerProcessHost'
    if (Test-Path -LiteralPath $workerProcessHostPath -PathType Container) {
        Write-JsonFile -Path (Join-Path $workerProcessHostPath 'appsettings.Production.json') -Value ([ordered]@{
            ConnectionStrings = [ordered]@{ OmpDb = $connectionString }
            WorkerProcess = [ordered]@{
                AppInstanceId = '00000000-0000-0000-0000-000000000000'
                WorkerInstanceId = '00000000-0000-0000-0000-000000000000'
                WorkerInstanceKey = ''
                WorkerTypeKey = ''
                PluginAssemblyPath = ''
                ShutdownEventName = ''
                ConfigurationJson = ''
            }
        })
    }

    $exampleServicePath = Join-Path $script:ServicesRoot 'ExampleServiceAppModule'
    if (Test-Path -LiteralPath $exampleServicePath -PathType Container) {
        Write-JsonFile -Path (Join-Path $exampleServicePath 'appsettings.Production.json') -Value ([ordered]@{
            ConnectionStrings = [ordered]@{ OmpDb = $connectionString }
        })
    }
}

function Deploy-Payloads {
    Write-Step 'Deploying payloads'
    New-Item -ItemType Directory -Path $script:PortalPath -Force | Out-Null
    New-Item -ItemType Directory -Path $script:WebAppsRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $script:ServicesRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $script:ArtifactStoreRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $script:ArtifactCacheRoot -Force | Out-Null

    Expand-PayloadZip -ZipName 'OpenModulePlatform.Portal.zip' -Destination $script:PortalPath
    Expand-PayloadZip -ZipName 'OpenModulePlatform.Auth.zip' -Destination (Join-Path $script:WebAppsRoot 'auth')

    if ($script:InstallOpenDocViewer) {
        Expand-PayloadZip -ZipName 'OpenDocViewer.dist.zip' -Destination (Join-Path $script:WebAppsRoot $script:OpenDocViewerAppPath)
    }

    if ($script:InstallExamples) {
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.ExampleWebAppModule.zip' -Destination (Join-Path $script:WebAppsRoot 'ExampleWebAppModule')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.ExampleWebAppBlazorModule.zip' -Destination (Join-Path $script:WebAppsRoot 'ExampleWebAppBlazorModule')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.ExampleServiceAppModule.zip' -Destination (Join-Path $script:WebAppsRoot 'ExampleServiceAppModule')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.ExampleWorkerAppModule.zip' -Destination (Join-Path $script:WebAppsRoot 'ExampleWorkerAppModule')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.iFrameWebAppModule.zip' -Destination (Join-Path $script:WebAppsRoot 'iFrameWebAppModule')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Worker.ExampleWorkerAppModule.zip' -Destination (Join-Path $script:ArtifactStoreRoot 'example-workerapp\worker\1.0.0')
    }

    if ($script:InstallRuntimeServices) {
        Expand-PayloadZip -ZipName 'OpenModulePlatform.HostAgent.WindowsService.zip' -Destination (Join-Path $script:ServicesRoot 'HostAgent')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.WorkerManager.WindowsService.zip' -Destination (Join-Path $script:ServicesRoot 'WorkerManager')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.WorkerProcessHost.zip' -Destination (Join-Path $script:ServicesRoot 'WorkerProcessHost')
    }

    if ($script:InstallExampleService) {
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Service.ExampleServiceAppModule.zip' -Destination (Join-Path $script:ServicesRoot 'ExampleServiceAppModule')
    }
}

function Run-InstallSql {
    if (-not $script:RunSql) {
        return
    }

    Write-Step 'Running SQL scripts'
    Ensure-Database
    Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot 'sql\OpenModulePlatform\1-setup-openmoduleplatform.sql')
    Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot 'sql\OpenModulePlatform\2-initialize-openmoduleplatform.sql') -PatchBootstrapPrincipal
    Ensure-AdditionalBootstrapPrincipals
    Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot 'sql\OpenModulePlatform.Portal\1-setup-omp-portal.sql')
    Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot 'sql\OpenModulePlatform.Portal\2-initialize-omp-portal.sql') -PatchBootstrapPrincipal

    if ($script:InstallExamples) {
        $exampleSqlFiles = @(
            'sql\examples\WebAppModule\1-setup-example-webapp.sql',
            'sql\examples\WebAppModule\2-initialize-example-webapp.sql',
            'sql\examples\WebAppBlazorModule\1-setup-example-webapp-blazor.sql',
            'sql\examples\WebAppBlazorModule\2-initialize-example-webapp-blazor.sql',
            'sql\examples\ServiceAppModule\1-setup-example-serviceapp.sql',
            'sql\examples\ServiceAppModule\2-initialize-example-serviceapp.sql',
            'sql\examples\WorkerAppModule\1-setup-example-workerapp.sql',
            'sql\examples\WorkerAppModule\2-initialize-example-workerapp.sql',
            'sql\OpenModulePlatform.Web.iFrameWebAppModule\1-setup-iframe-webapp.sql',
            'sql\OpenModulePlatform.Web.iFrameWebAppModule\2-initialize-iframe-webapp.sql'
        )

        foreach ($relativePath in $exampleSqlFiles) {
            Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot $relativePath)
        }
    }

    Ensure-RunAsDatabaseAccess
}

function Set-IisAuthentication {
    param(
        [string]$Location,
        [bool]$AnonymousEnabled,
        [object]$WindowsEnabled = $null
    )

    if ($null -eq $WindowsEnabled) {
        $WindowsEnabled = -not $AnonymousEnabled
    }

    $anonymousValue = $AnonymousEnabled.ToString().ToLowerInvariant()
    $windowsValue = ([bool]$WindowsEnabled).ToString().ToLowerInvariant()

    Invoke-NativeChecked $script:appcmdPath set config $Location `
        '/section:system.webServer/security/authentication/anonymousAuthentication' `
        "/enabled:$anonymousValue" `
        '/userName:' `
        '/password:' `
        '/commit:apphost'

    Invoke-NativeChecked $script:appcmdPath set config $Location `
        '/section:system.webServer/security/authentication/windowsAuthentication' `
        "/enabled:$windowsValue" `
        '/commit:apphost'
}

function Test-IisAppPool {
    param([string]$Name)

    $output = & $script:appcmdPath list apppool "/name:$Name" 2>$null
    $exitCode = $LASTEXITCODE
    return $exitCode -eq 0 -and $null -ne $output
}

function Get-IisAppPoolState {
    param([string]$Name)

    $output = & $script:appcmdPath list apppool "/name:$Name" /text:state 2>$null
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -or $null -eq $output) {
        return $null
    }

    return [string]$output
}

function Test-IisSite {
    param([string]$Name)

    $output = & $script:appcmdPath list site "/name:$Name" 2>$null
    $exitCode = $LASTEXITCODE
    return $exitCode -eq 0 -and $null -ne $output
}

function Get-IisCertificateThumbprint {
    if (-not [string]::IsNullOrWhiteSpace($script:IisCertificateThumbprint)) {
        return (ConvertTo-NormalizedCertificateText -Value $script:IisCertificateThumbprint)
    }

    if ([string]::IsNullOrWhiteSpace($script:IisCertificateSerialNumber)) {
        throw 'Iis.CertificateThumbprint or Iis.CertificateSerialNumber is required when Iis.Protocol is https.'
    }

    $expectedSerial = ConvertTo-NormalizedCertificateText -Value $script:IisCertificateSerialNumber
    $matches = @(Get-ChildItem -Path Cert:\LocalMachine\My | Where-Object {
            (ConvertTo-NormalizedCertificateText -Value $_.SerialNumber) -eq $expectedSerial
        })

    if ($matches.Count -eq 0) {
        throw "Certificate with serial number '$script:IisCertificateSerialNumber' was not found in Cert:\LocalMachine\My."
    }

    if ($matches.Count -gt 1) {
        throw "More than one certificate matched serial number '$script:IisCertificateSerialNumber'."
    }

    if ($matches[0].NotAfter -lt (Get-Date)) {
        throw "Certificate '$($matches[0].Subject)' expired at $($matches[0].NotAfter)."
    }

    return $matches[0].Thumbprint
}

function Ensure-IisSiteBinding {
    Import-Module WebAdministration -ErrorAction Stop

    $bindingInformation = "*:$($script:IisPort):$($script:IisHostHeader)"
    if ($script:IisRemoveOtherBindings) {
        foreach ($binding in @(Get-WebBinding -Name $script:IisSiteName)) {
            $isDesiredBinding = $binding.protocol -eq $script:IisProtocol -and $binding.bindingInformation -eq $bindingInformation
            if (-not $isDesiredBinding) {
                Write-Host "Removing IIS binding: $($binding.protocol) $($binding.bindingInformation)"
                Remove-WebBinding -Name $script:IisSiteName -Protocol $binding.protocol -BindingInformation $binding.bindingInformation
            }
        }
    }

    $binding = @(Get-WebBinding -Name $script:IisSiteName -Protocol $script:IisProtocol -ErrorAction SilentlyContinue |
        Where-Object { $_.bindingInformation -eq $bindingInformation }) |
        Select-Object -First 1

    if ($null -eq $binding) {
        New-WebBinding -Name $script:IisSiteName -Protocol $script:IisProtocol -IPAddress '*' -Port $script:IisPort -HostHeader $script:IisHostHeader | Out-Null
        $binding = @(Get-WebBinding -Name $script:IisSiteName -Protocol $script:IisProtocol |
            Where-Object { $_.bindingInformation -eq $bindingInformation }) |
            Select-Object -First 1
    }

    if ([string]::Equals($script:IisProtocol, 'https', [StringComparison]::OrdinalIgnoreCase)) {
        $thumbprint = Get-IisCertificateThumbprint
        $binding.AddSslCertificate($thumbprint, 'My')
        Write-Host "Bound certificate $thumbprint to $($script:IisProtocol)/$bindingInformation"
    }
}

function Test-IisApplicationExact {
    param([string]$Name)

    foreach ($line in @(& $script:appcmdPath list app 2>$null)) {
        $text = $line.ToString()
        if ($text -match '^APP "([^"]+)"' -and [string]::Equals($Matches[1], $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Stop-ExistingRuntime {
    Write-Step 'Stopping existing runtime'

    foreach ($serviceName in @($script:Services.HostAgent, $script:Services.WorkerManager, $script:Services.ExampleService)) {
        if ([string]::IsNullOrWhiteSpace($serviceName)) {
            continue
        }

        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Write-Host "Stopping service: $serviceName"
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
    }

    if (-not $script:ConfigureIis -or -not (Test-Path -LiteralPath $script:appcmdPath -PathType Leaf)) {
        return
    }

    $appPoolNames = @($script:AppPools.PSObject.Properties.Value) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Sort-Object -Unique

    foreach ($appPoolName in $appPoolNames) {
        if (Test-IisAppPool -Name $appPoolName) {
            Write-Host "Stopping app pool: $appPoolName"
            & $script:appcmdPath stop apppool "/apppool.name:$appPoolName" 2>$null | Out-Null
        }
    }
}

function Start-ConfiguredAppPools {
    if (-not $script:ConfigureIis -or -not (Test-Path -LiteralPath $script:appcmdPath -PathType Leaf)) {
        return
    }

    Write-Step 'Starting configured app pools'
    $appPoolNames = @($script:AppPools.PSObject.Properties.Value) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Sort-Object -Unique

    foreach ($appPoolName in $appPoolNames) {
        if (-not (Test-IisAppPool -Name $appPoolName)) {
            continue
        }

        $state = Get-IisAppPoolState -Name $appPoolName
        if (-not [string]::Equals($state, 'Started', [StringComparison]::OrdinalIgnoreCase)) {
            Invoke-NativeChecked $script:appcmdPath start apppool "/apppool.name:$appPoolName"
        }
    }
}

function Ensure-IisAppPool {
    param([string]$Name)

    if (-not (Test-IisAppPool -Name $Name)) {
        Invoke-NativeChecked $script:appcmdPath add apppool "/name:$Name"
    }

    Invoke-NativeChecked $script:appcmdPath set apppool "/apppool.name:$Name" '/managedRuntimeVersion:' '/processModel.loadUserProfile:true'

    if ($null -ne $script:RunAsCredential) {
        $plainPassword = ConvertFrom-SecureStringToPlainText -SecureString $script:RunAsCredential.Password
        try {
            $args = @(
                'set',
                'apppool',
                "/apppool.name:$Name",
                '/processModel.identityType:SpecificUser',
                "/processModel.userName:$($script:RunAsCredential.UserName)",
                "/processModel.password:$plainPassword"
            )
            $displayArgs = @(
                'set',
                'apppool',
                "/apppool.name:$Name",
                '/processModel.identityType:SpecificUser',
                "/processModel.userName:$($script:RunAsCredential.UserName)",
                '/processModel.password:***'
            )
            Invoke-NativeCheckedRedacted -FilePath $script:appcmdPath -Arguments $args -DisplayArguments $displayArgs
        }
        finally {
            $plainPassword = ''
        }
    }
}

function Ensure-IisWebApplication {
    param(
        [string]$AppPath,
        [string]$PhysicalPath,
        [string]$AppPoolName,
        [bool]$AnonymousEnabled,
        [object]$WindowsEnabled = $null
    )

    Ensure-IisAppPool -Name $AppPoolName

    $fullAppName = "$script:IisSiteName/$AppPath"
    $exists = Test-IisApplicationExact -Name $fullAppName

    if (-not $exists) {
        Invoke-NativeChecked $script:appcmdPath add app "/site.name:$script:IisSiteName" "/path:/$AppPath" "/physicalPath:$PhysicalPath" "/applicationPool:$AppPoolName"
    }
    else {
        Invoke-NativeChecked $script:appcmdPath set vdir "$script:IisSiteName/$AppPath/" "/physicalPath:$PhysicalPath"
        Invoke-NativeChecked $script:appcmdPath set app "$script:IisSiteName/$AppPath" "/applicationPool:$AppPoolName"
    }

    Set-IisAuthentication -Location "$script:IisSiteName/$AppPath" -AnonymousEnabled $AnonymousEnabled -WindowsEnabled $WindowsEnabled
}

function Ensure-Iis {
    if (-not $script:ConfigureIis) {
        return
    }
    if (-not (Test-IsWindowsAdministrator)) {
        throw 'IIS configuration requires an elevated PowerShell session.'
    }
    if (-not (Test-Path -LiteralPath $script:appcmdPath -PathType Leaf)) {
        throw "IIS appcmd.exe was not found: $script:appcmdPath"
    }

    Write-Step 'Ensuring IIS site and applications'
    Ensure-IisAppPool -Name $script:AppPools.Portal

    if (-not (Test-IisSite -Name $script:IisSiteName)) {
        $binding = "$($script:IisProtocol)/*:$($script:IisPort):$($script:IisHostHeader)"
        Invoke-NativeChecked $script:appcmdPath add site "/name:$script:IisSiteName" "/bindings:$binding" "/physicalPath:$script:PortalPath"
    }
    else {
        Invoke-NativeChecked $script:appcmdPath set vdir "/vdir.name:$script:IisSiteName/" "/physicalPath:$script:PortalPath"
    }

    Invoke-NativeChecked $script:appcmdPath set app "/app.name:$script:IisSiteName/" "/applicationPool:$($script:AppPools.Portal)"
    Ensure-IisSiteBinding
    Set-IisAuthentication -Location $script:IisSiteName -AnonymousEnabled $true

    Ensure-IisWebApplication -AppPath 'auth' -PhysicalPath (Join-Path $script:WebAppsRoot 'auth') -AppPoolName $script:AppPools.Auth -AnonymousEnabled $true -WindowsEnabled $true

    if ($script:InstallExamples) {
        Ensure-IisWebApplication -AppPath 'ExampleWebAppModule' -PhysicalPath (Join-Path $script:WebAppsRoot 'ExampleWebAppModule') -AppPoolName $script:AppPools.ExampleWebApp -AnonymousEnabled $true
        Ensure-IisWebApplication -AppPath 'ExampleWebAppBlazorModule' -PhysicalPath (Join-Path $script:WebAppsRoot 'ExampleWebAppBlazorModule') -AppPoolName $script:AppPools.ExampleWebAppBlazor -AnonymousEnabled $true
        Ensure-IisWebApplication -AppPath 'ExampleServiceAppModule' -PhysicalPath (Join-Path $script:WebAppsRoot 'ExampleServiceAppModule') -AppPoolName $script:AppPools.ExampleServiceWebApp -AnonymousEnabled $true
        Ensure-IisWebApplication -AppPath 'ExampleWorkerAppModule' -PhysicalPath (Join-Path $script:WebAppsRoot 'ExampleWorkerAppModule') -AppPoolName $script:AppPools.ExampleWorkerWebApp -AnonymousEnabled $true
        Ensure-IisWebApplication -AppPath 'iFrameWebAppModule' -PhysicalPath (Join-Path $script:WebAppsRoot 'iFrameWebAppModule') -AppPoolName $script:AppPools.IFrameWebApp -AnonymousEnabled $true
    }

    if ($script:InstallOpenDocViewer) {
        Ensure-IisWebApplication -AppPath $script:OpenDocViewerAppPath -PhysicalPath (Join-Path $script:WebAppsRoot $script:OpenDocViewerAppPath) -AppPoolName $script:AppPools.OpenDocViewer -AnonymousEnabled $true -WindowsEnabled $false
    }
}

function Wait-ServiceDeleted {
    param([string]$Name)

    for ($i = 0; $i -lt 20; $i++) {
        if ($null -eq (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
            return
        }

        Start-Sleep -Milliseconds 500
    }
}

function Install-WindowsService {
    param(
        [string]$Name,
        [string]$DisplayName,
        [string]$Description,
        [string]$ExecutablePath
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Service executable not found: $ExecutablePath"
    }

    $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        if ($existing.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $Name -Force -ErrorAction Stop
            (Get-Service -Name $Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }

        Invoke-NativeChecked (Join-Path $env:windir 'System32\sc.exe') delete $Name
        Wait-ServiceDeleted -Name $Name
    }

    $binaryPath = '"' + $ExecutablePath + '"'
    if ($null -ne $script:RunAsCredential) {
        New-Service -Name $Name -BinaryPathName $binaryPath -DisplayName $DisplayName -Description $Description -StartupType Automatic -Credential $script:RunAsCredential | Out-Null
    }
    else {
        New-Service -Name $Name -BinaryPathName $binaryPath -DisplayName $DisplayName -Description $Description -StartupType Automatic | Out-Null
    }

    if ($script:StartServices) {
        Start-Service -Name $Name -ErrorAction Stop
    }
}

function Ensure-Services {
    if (-not $script:InstallRuntimeServices -and -not $script:InstallExampleService) {
        return
    }
    if (-not (Test-IsWindowsAdministrator)) {
        throw 'Windows service installation requires an elevated PowerShell session.'
    }

    Write-Step 'Ensuring Windows services'

    if ($script:InstallRuntimeServices) {
        Install-WindowsService -Name $script:Services.HostAgent -DisplayName 'OpenModulePlatform HostAgent' -Description 'OpenModulePlatform artifact provisioning agent.' -ExecutablePath (Join-Path $script:ServicesRoot 'HostAgent\OpenModulePlatform.HostAgent.WindowsService.exe')
        Install-WindowsService -Name $script:Services.WorkerManager -DisplayName 'OpenModulePlatform WorkerManager' -Description 'OpenModulePlatform worker process manager.' -ExecutablePath (Join-Path $script:ServicesRoot 'WorkerManager\OpenModulePlatform.WorkerManager.WindowsService.exe')
    }

    if ($script:InstallExampleService) {
        Install-WindowsService -Name $script:Services.ExampleService -DisplayName 'OpenModulePlatform Service - ExampleServiceAppModule' -Description 'Example OMP service app module.' -ExecutablePath (Join-Path $script:ServicesRoot 'ExampleServiceAppModule\OpenModulePlatform.Service.ExampleServiceAppModule.exe')
    }
}

function Grant-RunAsFolderAccess {
    if ($null -eq $script:RunAsCredential) {
        return
    }

    Write-Step 'Granting runtime folder access'
    foreach ($path in @($script:RuntimeRoot, $script:WebRoot, $script:WebAppsRoot, $script:ServicesRoot, $script:ArtifactStoreRoot, $script:ArtifactCacheRoot, $script:DataProtectionKeyPath)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        Invoke-NativeChecked icacls $path '/grant' ('{0}:(OI)(CI)M' -f $script:RunAsCredential.UserName) '/T' '/C' '/Q'
    }
}

$config = Import-RequiredDeploymentConfig -Path $ConfigPath
$configPathForResolution = [System.IO.Path]::GetFullPath($ConfigPath)
$configDirectory = Split-Path -Parent $configPathForResolution
$scriptRootParent = Split-Path -Parent $PSScriptRoot
$defaultRepositoryRoot = Split-Path -Parent $scriptRootParent

if ([string]::IsNullOrWhiteSpace($DeploymentMode)) {
    $DeploymentMode = [string](Get-ConfigValue -Config $config -Name 'DeploymentMode' -DefaultValue 'Source')
}

$script:Version = [string](Get-ConfigValue -Config $config -Name 'Version' -DefaultValue '0.3.3')
$script:RepositoryRoot = [string](Get-ConfigValue -Config $config -Name 'RepositoryRoot' -DefaultValue $defaultRepositoryRoot)
if ([string]::IsNullOrWhiteSpace($script:RepositoryRoot)) {
    $script:RepositoryRoot = $defaultRepositoryRoot
}
$script:RepositoryRoot = Resolve-DeploymentPath -Path $script:RepositoryRoot -BasePath $configDirectory

$script:RuntimeRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'RuntimeRoot' -DefaultValue 'C:\OMP'))
$script:WebRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'WebRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'Sites')))
$script:WebAppsRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'WebAppsRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'WebApps')))
$script:ServicesRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'ServicesRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'Services')))
$script:ArtifactStoreRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'ArtifactStoreRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'ArtifactStore')))
$script:ArtifactCacheRoot = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'ArtifactCacheRoot' -DefaultValue (Join-Path $script:RuntimeRoot 'ArtifactCache')))
$script:DataProtectionKeyPath = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'DataProtectionKeyPath' -DefaultValue (Join-Path $script:RuntimeRoot 'DataProtectionKeys')))

$script:SqlServer = [string](Get-ConfigValue -Config $config -Name 'SqlServer' -DefaultValue 'localhost')
$script:Database = [string](Get-ConfigValue -Config $config -Name 'Database' -DefaultValue 'OpenModulePlatform')
$script:SqlAuthentication = [string](Get-ConfigValue -Config $config -Name 'SqlAuthentication' -DefaultValue 'Integrated')
$script:SqlUser = [string](Get-ConfigValue -Config $config -Name 'SqlUser' -DefaultValue '')
$script:SqlPassword = [string](Get-ConfigValue -Config $config -Name 'SqlPassword' -DefaultValue '')
$script:BootstrapPortalAdminPrincipals = @((Get-ConfigValue -Config $config -Name 'BootstrapPortalAdminPrincipals' -DefaultValue @("$env:USERDOMAIN\$env:USERNAME")))
$script:BootstrapPortalAdminPrincipalType = [string](Get-ConfigValue -Config $config -Name 'BootstrapPortalAdminPrincipalType' -DefaultValue 'User')
$script:HostKey = [string](Get-ConfigValue -Config $config -Name 'HostKey' -DefaultValue '')
$script:HostName = [string](Get-ConfigValue -Config $config -Name 'HostName' -DefaultValue '')
$script:ConfiguredHosts = @((Get-ConfigValue -Config $config -Name 'Hosts' -DefaultValue @()))
$script:PublicBaseUrl = [string](Get-ConfigValue -Config $config -Name 'PublicBaseUrl' -DefaultValue '')

$script:IisSiteName = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'SiteName' -DefaultValue 'OpenModulePlatform')
$script:IisProtocol = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'Protocol' -DefaultValue 'http')
$script:IisPort = [int](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'Port' -DefaultValue 8088)
$script:IisHostHeader = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'HostHeader' -DefaultValue '')
$script:IisCertificateThumbprint = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'CertificateThumbprint' -DefaultValue '')
$script:IisCertificateSerialNumber = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'CertificateSerialNumber' -DefaultValue '')
$script:IisRemoveOtherBindings = [bool](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'RemoveOtherBindings' -DefaultValue $false)
$script:OpenDocViewerAppPath = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'OpenDocViewerAppPath' -DefaultValue 'opendocviewer')
$portalPhysicalPath = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'PortalPhysicalPath' -DefaultValue '')
if ([string]::IsNullOrWhiteSpace($portalPhysicalPath)) {
    $portalPhysicalPath = Join-Path $script:WebRoot 'Portal'
}
$script:PortalPath = [System.IO.Path]::GetFullPath($portalPhysicalPath)

Resolve-ConfiguredHostProfile
if ([string]::IsNullOrWhiteSpace($script:HostKey)) {
    $script:HostKey = 'sample-host'
}

$script:PortalTitle = [string](Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'Title' -DefaultValue 'OpenModulePlatform')
$script:DefaultCulture = [string](Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'DefaultCulture' -DefaultValue 'sv-SE')
$script:SupportedCultures = @((Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'SupportedCultures' -DefaultValue @('sv-SE', 'en-US')))
$script:PortalAllowAnonymous = [bool](Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'AllowAnonymous' -DefaultValue $false)
$script:PortalUseForwardedHeaders = [bool](Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'UseForwardedHeaders' -DefaultValue $false)
$script:PortalPermissionMode = [string](Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'PermissionMode' -DefaultValue 'Any')
$script:PortalBaseUrl = [string](Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'PortalBaseUrl' -DefaultValue '')
if ([string]::IsNullOrWhiteSpace($script:PortalBaseUrl)) {
    $script:PortalBaseUrl = if ([string]::IsNullOrWhiteSpace($script:PublicBaseUrl)) { '/' } else { $script:PublicBaseUrl }
}

$defaultAppPools = @{
    Portal = 'OMP_Portal'
    Auth = 'OMP_Auth'
    OpenDocViewer = 'OMP_OpenDocViewer'
    ExampleWebApp = 'OMP_ExampleWebAppModule'
    ExampleWebAppBlazor = 'OMP_ExampleWebAppBlazorModule'
    ExampleServiceWebApp = 'OMP_ExampleServiceAppModule'
    ExampleWorkerWebApp = 'OMP_ExampleWorkerAppModule'
    IFrameWebApp = 'OMP_iFrameWebAppModule'
}
$configuredPools = Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'AppPools' -DefaultValue @{}
foreach ($key in @($configuredPools.Keys)) {
    $defaultAppPools[$key] = [string]$configuredPools[$key]
}
$script:AppPools = [pscustomobject]$defaultAppPools

$defaultServices = @{
    HostAgent = 'OpenModulePlatform.HostAgent'
    WorkerManager = 'OpenModulePlatform.WorkerManager'
    ExampleService = 'OpenModulePlatform.Service.ExampleServiceAppModule'
}
$configuredServices = Get-ConfigValue -Config $config -Name 'Services' -DefaultValue @{}
foreach ($key in @($configuredServices.Keys)) {
    $defaultServices[$key] = [string]$configuredServices[$key]
}
$script:Services = [pscustomobject]$defaultServices

$script:InstallOpenDocViewer = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallOpenDocViewer' -DefaultValue $true)
$script:InstallExamples = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallExamples' -DefaultValue $true)
$script:InstallRuntimeServices = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallRuntimeServices' -DefaultValue $true)
$script:InstallExampleService = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallExampleService' -DefaultValue $true)
$script:ConfigureIis = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'ConfigureIis' -DefaultValue $true)
$script:RunSql = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'RunSql' -DefaultValue $true)
$script:StartServices = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'StartServices' -DefaultValue $true)
$script:CreateDatabase = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'CreateDatabase' -DefaultValue $false)
$script:GrantRunAsDatabaseAccess = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'GrantRunAsDatabaseAccess' -DefaultValue $false)

$runAsUser = [string](Get-ConfigValue -Config $config -Name 'RunAsUser' -DefaultValue '')
$runAsPassword = [string](Get-ConfigValue -Config $config -Name 'RunAsPassword' -DefaultValue '')
$script:RunAsCredential = Get-RunAsCredential -User $runAsUser -Password $runAsPassword

if ([string]::Equals($DeploymentMode, 'Source', [StringComparison]::OrdinalIgnoreCase)) {
    if (-not $SkipPackageBuild) {
        Write-Step 'Building package from source'
        $packageScript = Join-Path $PSScriptRoot 'package-omp-suite.ps1'
        & $packageScript -ConfigPath $ConfigPath -KeepStaging
    }

    $outputRoot = [string](Get-NestedConfigValue -Config $config -Section 'Package' -Name 'OutputRoot' -DefaultValue (Join-Path $script:RepositoryRoot 'artifacts\suite-release'))
    if ([string]::IsNullOrWhiteSpace($outputRoot)) {
        $outputRoot = Join-Path $script:RepositoryRoot 'artifacts\suite-release'
    }
    $script:PackageRoot = Join-Path (Resolve-DeploymentPath -Path $outputRoot -BasePath $script:RepositoryRoot) ("OpenModulePlatformSuite-$script:Version")
}
else {
    $script:PackageRoot = [string](Get-ConfigValue -Config $config -Name 'PackageRoot' -DefaultValue $PSScriptRoot)
    if ([string]::IsNullOrWhiteSpace($script:PackageRoot)) {
        $script:PackageRoot = $PSScriptRoot
    }
    $script:PackageRoot = Resolve-DeploymentPath -Path $script:PackageRoot -BasePath $PSScriptRoot
}

if (-not (Test-Path -LiteralPath (Join-Path $script:PackageRoot 'payload') -PathType Container)) {
    throw "Package payload folder was not found: $script:PackageRoot"
}

Write-Host ''
Write-Host "Installing OpenModulePlatform suite for '$([string](Get-ConfigValue -Config $config -Name 'EnvironmentName' -DefaultValue 'environment'))' from: $script:PackageRoot" -ForegroundColor Yellow
if (-not (Confirm-DeploymentAction 'Continue with installation')) {
    Write-Host 'Installation cancelled.'
    return
}

Stop-ExistingRuntime
Deploy-Payloads
Grant-RunAsFolderAccess
Write-RuntimeConfiguration
Run-InstallSql
Ensure-Iis
Start-ConfiguredAppPools
Ensure-Services

Write-Host ''
Write-Host 'OpenModulePlatform suite installation completed.' -ForegroundColor Green
