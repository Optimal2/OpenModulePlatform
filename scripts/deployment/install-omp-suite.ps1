# File: scripts/deployment/install-omp-suite.ps1
[CmdletBinding()]
param(
    [string]$ConfigPath = '',
    [ValidateSet('Source', 'Package', '')]
    [string]$DeploymentMode = '',
    [switch]$SkipPackageBuild,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'omp-suite.local.psd1'
}

$script:appcmdPath = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'

function Write-Step {
    param([string]$Message)
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

function Confirm-DeploymentAction {
    param([string]$Message)
    if ($Yes) { return $true }
    $answer = Read-Host "$Message [Y/N, default N]"
    return $answer.Trim() -ieq 'Y'
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

function Test-IsUncPath {
    param([string]$Path)

    return -not [string]::IsNullOrWhiteSpace($Path) -and
        $Path.StartsWith('\\', [System.StringComparison]::Ordinal)
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

function ConvertTo-SqlNullableUnicodeLiteral {
    param([object]$Value)

    if ($null -eq $Value) {
        return 'NULL'
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return 'NULL'
    }

    return ConvertTo-SqlUnicodeLiteral -Value $text
}

function ConvertTo-SqlNullableIntLiteral {
    param([object]$Value)

    if ($null -eq $Value) {
        return 'NULL'
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return 'NULL'
    }

    return ([int]$text).ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

function Join-UrlPath {
    param(
        [string]$BaseUrl,
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        return $null
    }

    $base = $BaseUrl.TrimEnd('/')
    $relative = $RelativePath.Trim('/\')
    if ([string]::IsNullOrWhiteSpace($relative)) {
        return $base + '/'
    }

    return $base + '/' + $relative + '/'
}

function Resolve-OpenDocViewerPackageVersion {
    if (-not [string]::IsNullOrWhiteSpace($script:OpenDocViewerVersion)) {
        return $script:OpenDocViewerVersion.Trim()
    }

    $manifestPath = Join-Path $script:PackageRoot 'manifest.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifestVersion = [string](Get-ObjectPropertyValue -Entry $manifest -Name 'openDocViewerVersion' -DefaultValue '')
        if (-not [string]::IsNullOrWhiteSpace($manifestVersion)) {
            return $manifestVersion.Trim()
        }
    }

    Write-Warning 'OpenDocViewer.Version was not configured and manifest.json did not contain openDocViewerVersion. Falling back to the OMP suite version for compatibility with older packages.'
    return $script:Version
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

    $artifactVersionRegex = [regex]::new("(?m)^\s*DECLARE\s+@ArtifactVersion\s+nvarchar\(\d+\)\s*=\s*N'(?:''|[^'])*';\s*$")
    if ($artifactVersionRegex.IsMatch($content)) {
        $versionLiteral = ConvertTo-SqlUnicodeLiteral -Value $script:Version
        $content = $artifactVersionRegex.Replace(
            $content,
            [System.Text.RegularExpressions.MatchEvaluator]{
                param([System.Text.RegularExpressions.Match]$match)
                "DECLARE @ArtifactVersion nvarchar(50) = $versionLiteral;"
            },
            1)
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

function Ensure-ConfiguredHosts {
    $hostRows = @()
    $seenHostKeys = @{}
    $defaultSortOrder = 10

    foreach ($hostEntry in @($script:ConfiguredHosts)) {
        $hostKey = [string](Get-ConfigEntryValue -Entry $hostEntry -Name 'HostKey' -DefaultValue '')
        if ([string]::IsNullOrWhiteSpace($hostKey)) {
            continue
        }

        $normalizedHostKey = $hostKey.Trim().ToLowerInvariant()
        if ($seenHostKeys.ContainsKey($normalizedHostKey)) {
            continue
        }

        $seenHostKeys[$normalizedHostKey] = $true
        $displayName = [string](Get-ConfigEntryValue -Entry $hostEntry -Name 'DisplayName' -DefaultValue $hostKey)
        $baseUrl = [string](Get-ConfigEntryValue -Entry $hostEntry -Name 'BaseUrl' -DefaultValue '')
        $environment = [string](Get-ConfigEntryValue -Entry $hostEntry -Name 'Environment' -DefaultValue ([string](Get-ConfigValue -Config $config -Name 'EnvironmentName' -DefaultValue 'environment')))
        $sortOrder = [int](Get-ConfigEntryValue -Entry $hostEntry -Name 'SortOrder' -DefaultValue $defaultSortOrder)
        $defaultSortOrder += 10
        $resolvedDisplayName = if ([string]::IsNullOrWhiteSpace($displayName)) { $hostKey.Trim() } else { $displayName.Trim() }
        $resolvedBaseUrl = if ([string]::IsNullOrWhiteSpace($baseUrl)) { $null } else { $baseUrl.Trim() }
        $resolvedEnvironment = if ([string]::IsNullOrWhiteSpace($environment)) { $null } else { $environment.Trim() }

        $hostRows += [pscustomobject]@{
            HostKey = $hostKey.Trim()
            DisplayName = $resolvedDisplayName
            BaseUrl = $resolvedBaseUrl
            Environment = $resolvedEnvironment
            SortOrder = $sortOrder
        }
    }

    if ($hostRows.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($script:HostKey)) {
        $displayName = if ([string]::IsNullOrWhiteSpace($script:HostName)) { $script:HostKey } else { $script:HostName }
        $hostRows += [pscustomobject]@{
            HostKey = $script:HostKey.Trim()
            DisplayName = $displayName.Trim()
            BaseUrl = $null
            Environment = [string](Get-ConfigValue -Config $config -Name 'EnvironmentName' -DefaultValue 'environment')
            SortOrder = 10
        }
    }

    if ($hostRows.Count -eq 0) {
        return
    }

    Write-Step 'Ensuring configured OMP hosts'
    $values = @()
    foreach ($hostRow in $hostRows) {
        $values += '(' +
            (ConvertTo-SqlUnicodeLiteral -Value $hostRow.HostKey) + ', ' +
            (ConvertTo-SqlNullableUnicodeLiteral -Value $hostRow.DisplayName) + ', ' +
            (ConvertTo-SqlNullableUnicodeLiteral -Value $hostRow.BaseUrl) + ', ' +
            (ConvertTo-SqlNullableUnicodeLiteral -Value $hostRow.Environment) + ', ' +
            ([int]$hostRow.SortOrder).ToString([System.Globalization.CultureInfo]::InvariantCulture) +
            ')'
    }

    Invoke-SqlText -Query @"
DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @DefaultHostTemplateId int;

SELECT @InstanceId = InstanceId,
       @InstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceKey = N'default';

SELECT @DefaultHostTemplateId = HostTemplateId
FROM omp.HostTemplates
WHERE TemplateKey = N'default-host';

IF @InstanceId IS NULL OR @InstanceTemplateId IS NULL OR @DefaultHostTemplateId IS NULL
BEGIN
    THROW 51012, 'Default OMP instance and host template must exist before configured hosts can be registered.', 1;
END

DECLARE @Hosts table
(
    HostKey nvarchar(128) NOT NULL PRIMARY KEY,
    DisplayName nvarchar(200) NULL,
    BaseUrl nvarchar(300) NULL,
    Environment nvarchar(100) NULL,
    SortOrder int NOT NULL
);

INSERT INTO @Hosts(HostKey, DisplayName, BaseUrl, Environment, SortOrder)
VALUES
$(($values -join ",`r`n"));

MERGE omp.Hosts AS target
USING @Hosts AS source
ON target.InstanceId = @InstanceId
AND target.HostKey = source.HostKey
WHEN MATCHED THEN
    UPDATE SET DisplayName = source.DisplayName,
               BaseUrl = source.BaseUrl,
               Environment = source.Environment,
               OsFamily = COALESCE(target.OsFamily, N'Windows'),
               Architecture = COALESCE(target.Architecture, N'x64'),
               IsEnabled = 1,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(HostId, InstanceId, HostKey, DisplayName, BaseUrl, Environment, OsFamily, Architecture, IsEnabled)
    VALUES(NEWID(), @InstanceId, source.HostKey, source.DisplayName, source.BaseUrl, source.Environment, N'Windows', N'x64', 1);

MERGE omp.InstanceTemplateHosts AS target
USING @Hosts AS source
ON target.InstanceTemplateId = @InstanceTemplateId
AND target.HostKey = source.HostKey
WHEN MATCHED THEN
    UPDATE SET HostTemplateId = @DefaultHostTemplateId,
               DisplayName = source.DisplayName,
               Environment = source.Environment,
               SortOrder = source.SortOrder,
               IsEnabled = 1,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(InstanceTemplateId, HostTemplateId, HostKey, DisplayName, Environment, SortOrder, IsEnabled)
    VALUES(@InstanceTemplateId, @DefaultHostTemplateId, source.HostKey, source.DisplayName, source.Environment, source.SortOrder, 1);

INSERT INTO omp.HostDeploymentAssignments(HostId, HostTemplateId, AssignedBy, IsActive)
SELECT h.HostId,
       @DefaultHostTemplateId,
       N'install-script',
       1
FROM @Hosts source
INNER JOIN omp.Hosts h
    ON h.InstanceId = @InstanceId
   AND h.HostKey = source.HostKey
WHERE NOT EXISTS
(
    SELECT 1
    FROM omp.HostDeploymentAssignments existing
    WHERE existing.HostId = h.HostId
      AND existing.HostTemplateId = @DefaultHostTemplateId
      AND existing.IsActive = 1
);
"@
}

function Ensure-ConfiguredConfigSettings {
    $settings = @($script:ConfigSettings)
    if ($settings.Count -eq 0) {
        return
    }

    Write-Step 'Ensuring OMP config settings'
    $values = @()
    foreach ($settingEntry in $settings) {
        $category = [string](Get-ConfigEntryValue -Entry $settingEntry -Name 'ConfigCategory' -DefaultValue (Get-ConfigEntryValue -Entry $settingEntry -Name 'Category' -DefaultValue ''))
        $setting = [string](Get-ConfigEntryValue -Entry $settingEntry -Name 'ConfigSetting' -DefaultValue (Get-ConfigEntryValue -Entry $settingEntry -Name 'Setting' -DefaultValue ''))
        if ([string]::IsNullOrWhiteSpace($category) -or [string]::IsNullOrWhiteSpace($setting)) {
            throw 'Every ConfigSettings entry must include ConfigCategory/Category and ConfigSetting/Setting.'
        }

        $configValue = Get-ConfigEntryValue -Entry $settingEntry -Name 'ConfigValue' -DefaultValue (Get-ConfigEntryValue -Entry $settingEntry -Name 'Value' -DefaultValue $null)
        $configUsr = Get-ConfigEntryValue -Entry $settingEntry -Name 'ConfigUsr' -DefaultValue (Get-ConfigEntryValue -Entry $settingEntry -Name 'UserId' -DefaultValue $null)
        $permissionName = Get-ConfigEntryValue -Entry $settingEntry -Name 'PermissionName' -DefaultValue (Get-ConfigEntryValue -Entry $settingEntry -Name 'Permission' -DefaultValue $null)
        $roleName = Get-ConfigEntryValue -Entry $settingEntry -Name 'RoleName' -DefaultValue (Get-ConfigEntryValue -Entry $settingEntry -Name 'Role' -DefaultValue $null)
        $priority = Get-ConfigEntryValue -Entry $settingEntry -Name 'ConfigPriority' -DefaultValue (Get-ConfigEntryValue -Entry $settingEntry -Name 'Priority' -DefaultValue 0)

        $values += '(' +
            (ConvertTo-SqlUnicodeLiteral -Value $category.Trim()) + ', ' +
            (ConvertTo-SqlUnicodeLiteral -Value $setting.Trim()) + ', ' +
            (ConvertTo-SqlNullableUnicodeLiteral -Value $configValue) + ', ' +
            (ConvertTo-SqlNullableIntLiteral -Value $configUsr) + ', ' +
            (ConvertTo-SqlNullableUnicodeLiteral -Value $permissionName) + ', ' +
            (ConvertTo-SqlNullableUnicodeLiteral -Value $roleName) + ', ' +
            (ConvertTo-SqlNullableIntLiteral -Value $priority) +
            ')'
    }

    Invoke-SqlText -Query @"
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @Settings table
(
    ConfigCategory nvarchar(100) NOT NULL,
    ConfigSetting nvarchar(200) NOT NULL,
    ConfigValue nvarchar(max) NULL,
    ConfigUsr int NULL,
    PermissionName nvarchar(200) NULL,
    RoleName nvarchar(200) NULL,
    ConfigPriority int NOT NULL
);

INSERT INTO @Settings(ConfigCategory, ConfigSetting, ConfigValue, ConfigUsr, PermissionName, RoleName, ConfigPriority)
VALUES
$(($values -join ",`r`n"));

IF EXISTS
(
    SELECT 1
    FROM @Settings s
    LEFT JOIN omp.config_setting_definitions def
        ON def.ConfigCategory = s.ConfigCategory
       AND def.ConfigSetting = s.ConfigSetting
    WHERE def.ConfigSettingId IS NULL
)
BEGIN
    THROW 51009, 'ConfigSettings references a config definition that is not registered by OMP.', 1;
END

IF EXISTS
(
    SELECT 1
    FROM @Settings s
    LEFT JOIN omp.Permissions p ON p.Name = s.PermissionName
    WHERE s.PermissionName IS NOT NULL
      AND p.PermissionId IS NULL
)
BEGIN
    THROW 51010, 'ConfigSettings references a PermissionName that does not exist.', 1;
END

IF EXISTS
(
    SELECT 1
    FROM @Settings s
    LEFT JOIN omp.Roles r ON r.Name = s.RoleName
    WHERE s.RoleName IS NOT NULL
      AND r.RoleId IS NULL
)
BEGIN
    THROW 51011, 'ConfigSettings references a RoleName that does not exist.', 1;
END

;WITH ResolvedSettings AS
(
    SELECT def.ConfigSettingId,
           s.ConfigValue,
           s.ConfigUsr,
           p.PermissionId AS ConfigPermission,
           r.RoleId AS ConfigRole,
           s.ConfigPriority
    FROM @Settings s
    INNER JOIN omp.config_setting_definitions def
        ON def.ConfigCategory = s.ConfigCategory
       AND def.ConfigSetting = s.ConfigSetting
    LEFT JOIN omp.Permissions p ON p.Name = s.PermissionName
    LEFT JOIN omp.Roles r ON r.Name = s.RoleName
)
MERGE omp.config_settings AS target
USING ResolvedSettings AS source
ON target.ConfigSettingId = source.ConfigSettingId
   AND ISNULL(target.ConfigUsr, -2147483648) = ISNULL(source.ConfigUsr, -2147483648)
   AND ISNULL(target.ConfigPermission, -2147483648) = ISNULL(source.ConfigPermission, -2147483648)
   AND ISNULL(target.ConfigRole, -2147483648) = ISNULL(source.ConfigRole, -2147483648)
WHEN MATCHED THEN
    UPDATE SET ConfigValue = source.ConfigValue,
               ConfigPriority = source.ConfigPriority
WHEN NOT MATCHED THEN
    INSERT(ConfigSettingId, ConfigValue, ConfigUsr, ConfigPermission, ConfigRole, ConfigPriority)
    VALUES(source.ConfigSettingId, source.ConfigValue, source.ConfigUsr, source.ConfigPermission, source.ConfigRole, source.ConfigPriority);
"@
}

function Ensure-OpenDocViewerMetadata {
    if (-not $script:InstallOpenDocViewer) {
        return
    }

    Write-Step 'Ensuring OpenDocViewer OMP metadata'

    $routePath = $script:OpenDocViewerAppPath.Trim('/\')
    if ([string]::IsNullOrWhiteSpace($routePath)) {
        throw 'Iis.OpenDocViewerAppPath must not be empty when Options.InstallOpenDocViewer is true.'
    }

    $installPath = Join-Path $script:WebAppsRoot $routePath
    $publicUrl = Join-UrlPath -BaseUrl $script:PublicBaseUrl -RelativePath $routePath

    $displayNameLiteral = ConvertTo-SqlUnicodeLiteral -Value $script:OpenDocViewerDisplayName
    $versionLiteral = ConvertTo-SqlUnicodeLiteral -Value $script:OpenDocViewerVersion
    $routePathLiteral = ConvertTo-SqlUnicodeLiteral -Value $routePath
    $publicUrlLiteral = ConvertTo-SqlNullableUnicodeLiteral -Value $publicUrl
    $installPathLiteral = ConvertTo-SqlUnicodeLiteral -Value $installPath

    Invoke-SqlText -Query @"
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_opendocviewer')
BEGIN
    EXEC(N'CREATE SCHEMA [omp_opendocviewer]');
END

DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @OpenDocViewerModuleId int;
DECLARE @OpenDocViewerAppId int;
DECLARE @OpenDocViewerArtifactId int;
DECLARE @SeedModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111241';
DECLARE @SeedAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111242';
DECLARE @OpenDocViewerModuleInstanceId uniqueidentifier;
DECLARE @OpenDocViewerTemplateModuleInstanceId int;
DECLARE @OpenDocViewerAppInstanceId uniqueidentifier;
DECLARE @ArtifactVersion nvarchar(50) = $versionLiteral;

SELECT @InstanceId = InstanceId,
       @InstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceKey = N'default';

IF @InstanceId IS NULL
BEGIN
    THROW 51013, 'Default OMP instance not found. Run the core SQL setup/init scripts first.', 1;
END

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'opendocviewer')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'OpenDocViewer',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_opendocviewer',
        Description = N'First-party OMP registration for the OpenDocViewer static web application',
        IsEnabled = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'opendocviewer';
END
ELSE
BEGIN
    INSERT INTO omp.Modules(ModuleKey, DisplayName, ModuleType, SchemaName, Description, IsEnabled, SortOrder)
    VALUES(N'opendocviewer', N'OpenDocViewer', N'WebAppModule', N'omp_opendocviewer', N'First-party OMP registration for the OpenDocViewer static web application', 1, 310);
END

SELECT @OpenDocViewerModuleId = ModuleId
FROM omp.Modules
WHERE ModuleKey = N'opendocviewer';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @OpenDocViewerModuleId AND AppKey = N'opendocviewer_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = $displayNameLiteral,
        AppType = N'WebApp',
        Description = N'Static web application definition for OpenDocViewer',
        IsEnabled = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @OpenDocViewerModuleId
      AND AppKey = N'opendocviewer_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(@OpenDocViewerModuleId, N'opendocviewer_webapp', $displayNameLiteral, N'WebApp', N'Static web application definition for OpenDocViewer', 1, 310);
END

SELECT @OpenDocViewerAppId = AppId
FROM omp.Apps
WHERE ModuleId = @OpenDocViewerModuleId
  AND AppKey = N'opendocviewer_webapp';

MERGE omp.Artifacts AS target
USING
(
    SELECT @OpenDocViewerAppId AS AppId,
           @ArtifactVersion AS Version,
           N'web-app' AS PackageType,
           N'opendocviewer' AS TargetName,
           N'opendocviewer/web/' + @ArtifactVersion AS RelativePath,
           CAST(1 AS bit) AS IsEnabled
) AS source
ON target.AppId = source.AppId
AND target.Version = source.Version
AND target.PackageType = source.PackageType
AND target.TargetName = source.TargetName
WHEN MATCHED THEN
    UPDATE SET RelativePath = source.RelativePath,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(AppId, Version, PackageType, TargetName, RelativePath, IsEnabled)
    VALUES(source.AppId, source.Version, source.PackageType, source.TargetName, source.RelativePath, source.IsEnabled);

SELECT @OpenDocViewerArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @OpenDocViewerAppId
  AND Version = @ArtifactVersion
  AND PackageType = N'web-app'
  AND TargetName = N'opendocviewer';

MERGE omp.ModuleInstances AS target
USING
(
    SELECT @SeedModuleInstanceId AS ModuleInstanceId,
           @InstanceId AS InstanceId,
           @OpenDocViewerModuleId AS ModuleId,
           N'opendocviewer' AS ModuleInstanceKey,
           N'OpenDocViewer' AS DisplayName,
           N'OpenDocViewer module instance for the default OMP instance' AS Description,
           CAST(1 AS bit) AS IsEnabled,
           CAST(310 AS int) AS SortOrder
) AS source
ON target.ModuleInstanceId = source.ModuleInstanceId
OR (target.InstanceId = source.InstanceId AND target.ModuleInstanceKey = source.ModuleInstanceKey)
WHEN MATCHED THEN
    UPDATE SET ModuleId = source.ModuleId,
               DisplayName = source.DisplayName,
               Description = source.Description,
               IsEnabled = source.IsEnabled,
               SortOrder = source.SortOrder,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName, Description, IsEnabled, SortOrder)
    VALUES(source.ModuleInstanceId, source.InstanceId, source.ModuleId, source.ModuleInstanceKey, source.DisplayName, source.Description, source.IsEnabled, source.SortOrder);

SELECT @OpenDocViewerModuleInstanceId = ModuleInstanceId
FROM omp.ModuleInstances
WHERE InstanceId = @InstanceId
  AND ModuleInstanceKey = N'opendocviewer';

MERGE omp.InstanceTemplateModuleInstances AS target
USING
(
    SELECT @InstanceTemplateId AS InstanceTemplateId,
           @OpenDocViewerModuleId AS ModuleId,
           N'opendocviewer' AS ModuleInstanceKey,
           N'OpenDocViewer' AS DisplayName,
           N'OpenDocViewer module instance in the default template' AS Description,
           CAST(310 AS int) AS SortOrder,
           CAST(1 AS bit) AS IsEnabled
) AS source
ON target.InstanceTemplateId = source.InstanceTemplateId
AND target.ModuleInstanceKey = source.ModuleInstanceKey
WHEN MATCHED THEN
    UPDATE SET ModuleId = source.ModuleId,
               DisplayName = source.DisplayName,
               Description = source.Description,
               SortOrder = source.SortOrder,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(InstanceTemplateId, ModuleId, ModuleInstanceKey, DisplayName, Description, SortOrder, IsEnabled)
    VALUES(source.InstanceTemplateId, source.ModuleId, source.ModuleInstanceKey, source.DisplayName, source.Description, source.SortOrder, source.IsEnabled);

SELECT @OpenDocViewerTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'opendocviewer';

-- OpenDocViewer is a host-neutral app instance. Host Agent deploys it on each
-- configured host while the portal/topbar still shows one logical app entry.
MERGE omp.AppInstances AS target
USING
(
    SELECT @SeedAppInstanceId AS AppInstanceId,
           @OpenDocViewerModuleInstanceId AS ModuleInstanceId,
           CAST(NULL AS uniqueidentifier) AS HostId,
           @OpenDocViewerAppId AS AppId,
           N'opendocviewer_webapp' AS AppInstanceKey,
           $displayNameLiteral AS DisplayName,
           N'OpenDocViewer static web app managed by OMP Host Agent' AS Description,
           $routePathLiteral AS RoutePath,
           $publicUrlLiteral AS PublicUrl,
           $installPathLiteral AS InstallPath,
           N'opendocviewer' AS InstallationName,
           @OpenDocViewerArtifactId AS ArtifactId,
           CAST(1 AS bit) AS IsEnabled,
           CAST(1 AS bit) AS IsAllowed,
           CAST(1 AS tinyint) AS DesiredState,
           CAST(310 AS int) AS SortOrder
) AS source
ON target.AppInstanceId = source.AppInstanceId
OR (target.ModuleInstanceId = source.ModuleInstanceId AND target.AppInstanceKey = source.AppInstanceKey)
WHEN MATCHED THEN
    UPDATE SET ModuleInstanceId = source.ModuleInstanceId,
               HostId = source.HostId,
               AppId = source.AppId,
               DisplayName = source.DisplayName,
               Description = source.Description,
               RoutePath = source.RoutePath,
               PublicUrl = source.PublicUrl,
               InstallPath = source.InstallPath,
               InstallationName = source.InstallationName,
               ArtifactId = source.ArtifactId,
               IsEnabled = source.IsEnabled,
               IsAllowed = source.IsAllowed,
               DesiredState = source.DesiredState,
               SortOrder = source.SortOrder,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(AppInstanceId, ModuleInstanceId, HostId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, ArtifactId, IsEnabled, IsAllowed, DesiredState, SortOrder)
    VALUES(source.AppInstanceId, source.ModuleInstanceId, source.HostId, source.AppId, source.AppInstanceKey, source.DisplayName, source.Description, source.RoutePath, source.PublicUrl, source.InstallPath, source.InstallationName, source.ArtifactId, source.IsEnabled, source.IsAllowed, source.DesiredState, source.SortOrder);

SELECT @OpenDocViewerAppInstanceId = AppInstanceId
FROM omp.AppInstances
WHERE ModuleInstanceId = @OpenDocViewerModuleInstanceId
  AND AppInstanceKey = N'opendocviewer_webapp';

MERGE omp.InstanceTemplateAppInstances AS target
USING
(
    SELECT @OpenDocViewerTemplateModuleInstanceId AS InstanceTemplateModuleInstanceId,
           CAST(NULL AS int) AS InstanceTemplateHostId,
           @OpenDocViewerAppId AS AppId,
           N'opendocviewer_webapp' AS AppInstanceKey,
           $displayNameLiteral AS DisplayName,
           N'OpenDocViewer static web app managed by OMP Host Agent' AS Description,
           $routePathLiteral AS RoutePath,
           $publicUrlLiteral AS PublicUrl,
           $installPathLiteral AS InstallPath,
           N'opendocviewer' AS InstallationName,
           @OpenDocViewerArtifactId AS DesiredArtifactId,
           CAST(1 AS tinyint) AS DesiredState,
           CAST(310 AS int) AS SortOrder,
           CAST(1 AS bit) AS IsEnabled
) AS source
ON target.InstanceTemplateModuleInstanceId = source.InstanceTemplateModuleInstanceId
AND target.AppInstanceKey = source.AppInstanceKey
WHEN MATCHED THEN
    UPDATE SET InstanceTemplateHostId = source.InstanceTemplateHostId,
               AppId = source.AppId,
               DisplayName = source.DisplayName,
               Description = source.Description,
               RoutePath = source.RoutePath,
               PublicUrl = source.PublicUrl,
               InstallPath = source.InstallPath,
               InstallationName = source.InstallationName,
               DesiredArtifactId = source.DesiredArtifactId,
               DesiredState = source.DesiredState,
               SortOrder = source.SortOrder,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(InstanceTemplateModuleInstanceId, InstanceTemplateHostId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, DesiredArtifactId, DesiredState, SortOrder, IsEnabled)
    VALUES(source.InstanceTemplateModuleInstanceId, source.InstanceTemplateHostId, source.AppId, source.AppInstanceKey, source.DisplayName, source.Description, source.RoutePath, source.PublicUrl, source.InstallPath, source.InstallationName, source.DesiredArtifactId, source.DesiredState, source.SortOrder, source.IsEnabled);
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
    SET @sql = N'ALTER ROLE [db_owner] ADD MEMBER ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
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

function Remove-ArtifactRuntimeConfigurationFiles {
    param([Parameter(Mandatory = $true)][string]$Destination)

    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
        return
    }

    foreach ($pattern in @('appsettings.json', 'appsettings.*.json', 'odv.site.config.js')) {
        Get-ChildItem -LiteralPath $Destination -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }
}

function Expand-ArtifactPayloadZip {
    param(
        [Parameter(Mandatory = $true)][string]$ZipName,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Expand-PayloadZip -ZipName $ZipName -Destination $Destination
    Remove-ArtifactRuntimeConfigurationFiles -Destination $Destination
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

function Get-ObjectPropertyValue {
    param(
        [Parameter(Mandatory = $true)][object]$Entry,
        [Parameter(Mandatory = $true)][string]$Name,
        $DefaultValue = $null
    )

    $property = $Entry.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $DefaultValue
    }

    return $property.Value
}

function Resolve-ContentSeedFile {
    param(
        [Parameter(Mandatory = $true)][string]$SeedRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw 'Content Web App seed file path must not be empty.'
    }

    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Content Web App seed file path must be relative to the seed folder: $RelativePath"
    }

    $trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $seedRootFull = [System.IO.Path]::GetFullPath($SeedRoot).TrimEnd($trimChars)
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $seedRootFull $RelativePath))
    $requiredPrefix = $seedRootFull + [System.IO.Path]::DirectorySeparatorChar

    if (-not $candidate.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Content Web App seed file path escapes the seed folder: $RelativePath"
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Content Web App seed file was not found: $candidate"
    }

    return $candidate
}

function Resolve-ContentWebAppServerReportsPath {
    $configuredPath = $script:ContentWebAppServerReportsPath
    if ([string]::IsNullOrWhiteSpace($configuredPath)) {
        $configuredPath = 'App_Data/ContentReports'
    }

    if ([System.IO.Path]::IsPathRooted($configuredPath)) {
        return [System.IO.Path]::GetFullPath($configuredPath)
    }

    $contentRoot = Join-Path $script:WebAppsRoot $script:ContentWebAppPath
    return [System.IO.Path]::GetFullPath((Join-Path $contentRoot $configuredPath))
}

function Resolve-ContentWebAppHtmlFilesPath {
    $configuredPath = $script:ContentWebAppHtmlFilesPath
    if ([string]::IsNullOrWhiteSpace($configuredPath)) {
        $configuredPath = 'App_Data/ContentPages'
    }

    if ([System.IO.Path]::IsPathRooted($configuredPath)) {
        return [System.IO.Path]::GetFullPath($configuredPath)
    }

    $contentRoot = Join-Path $script:WebAppsRoot $script:ContentWebAppPath
    return [System.IO.Path]::GetFullPath((Join-Path $contentRoot $configuredPath))
}

function Resolve-ContentWebAppSeedServerReportsPath {
    if (-not [string]::IsNullOrWhiteSpace($script:ContentWebAppSharedServerReportsPath)) {
        return [System.IO.Path]::GetFullPath($script:ContentWebAppSharedServerReportsPath)
    }

    return Resolve-ContentWebAppServerReportsPath
}

function Resolve-ContentWebAppSeedHtmlFilesPath {
    if (-not [string]::IsNullOrWhiteSpace($script:ContentWebAppSharedHtmlFilesPath)) {
        return [System.IO.Path]::GetFullPath($script:ContentWebAppSharedHtmlFilesPath)
    }

    return Resolve-ContentWebAppHtmlFilesPath
}

function Get-HostAgentFileMirrors {
    $mirrors = @()

    if (-not [string]::IsNullOrWhiteSpace($script:ContentWebAppSharedServerReportsPath)) {
        $mirrors += [ordered]@{
            SourcePath = $script:ContentWebAppSharedServerReportsPath
            TargetPath = Resolve-ContentWebAppServerReportsPath
            DeleteStaleTargetEntries = $true
            ExcludedEntries = @()
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($script:ContentWebAppSharedHtmlFilesPath)) {
        $mirrors += [ordered]@{
            SourcePath = $script:ContentWebAppSharedHtmlFilesPath
            TargetPath = Resolve-ContentWebAppHtmlFilesPath
            DeleteStaleTargetEntries = $true
            ExcludedEntries = @()
        }
    }

    return $mirrors
}

function Add-SqlParameter {
    param(
        [Parameter(Mandatory = $true)][System.Data.SqlClient.SqlCommand]$Command,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][System.Data.SqlDbType]$Type,
        [object]$Value = $null,
        [int]$Size = 0
    )

    if ($Size -ne 0) {
        $parameter = $Command.Parameters.Add($Name, $Type, $Size)
    }
    else {
        $parameter = $Command.Parameters.Add($Name, $Type)
    }

    if ($null -eq $Value) {
        $parameter.Value = [DBNull]::Value
    }
    else {
        $parameter.Value = $Value
    }
}

function Invoke-ContentSeedPageUpsert {
    param(
        [Parameter(Mandatory = $true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory = $true)][string]$Slug,
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$ContentType,
        [Parameter(Mandatory = $true)][string]$Body,
        [object]$ServerReportKey,
        [bool]$IsEnabled,
        [object]$SortOrder
    )

    $command = $Connection.CreateCommand()
    $command.CommandTimeout = 3600
    $command.CommandText = @"
SET XACT_ABORT ON;

DECLARE @ContentId uniqueidentifier;

SELECT @ContentId = content_id
FROM omp_content.contents
WHERE app_instance_id = @AppInstanceId
  AND slug = @Slug;

IF @ContentId IS NULL
BEGIN
    SET @ContentId = NEWID();

    INSERT INTO omp_content.contents(
        content_id,
        app_instance_id,
        slug,
        title,
        content_type,
        body,
        server_report_key,
        is_enabled,
        sort_order,
        created_by,
        updated_by)
    VALUES(
        @ContentId,
        @AppInstanceId,
        @Slug,
        @Title,
        @ContentType,
        @Body,
        @ServerReportKey,
        @IsEnabled,
        @SortOrder,
        N'install-script',
        N'install-script');
END
ELSE
BEGIN
    UPDATE omp_content.contents
    SET title = @Title,
        content_type = @ContentType,
        body = @Body,
        server_report_key = @ServerReportKey,
        is_enabled = @IsEnabled,
        sort_order = @SortOrder,
        updated_by = N'install-script',
        updated_at = SYSUTCDATETIME()
    WHERE content_id = @ContentId;
END

SELECT @ContentId;
"@

    Add-SqlParameter -Command $command -Name '@AppInstanceId' -Type ([System.Data.SqlDbType]::UniqueIdentifier) -Value ([Guid]$script:ContentWebAppAppInstanceId)
    Add-SqlParameter -Command $command -Name '@Slug' -Type ([System.Data.SqlDbType]::NVarChar) -Size 256 -Value $Slug
    Add-SqlParameter -Command $command -Name '@Title' -Type ([System.Data.SqlDbType]::NVarChar) -Size 200 -Value $Title
    Add-SqlParameter -Command $command -Name '@ContentType' -Type ([System.Data.SqlDbType]::NVarChar) -Size 20 -Value $ContentType
    Add-SqlParameter -Command $command -Name '@Body' -Type ([System.Data.SqlDbType]::NVarChar) -Size -1 -Value $Body
    Add-SqlParameter -Command $command -Name '@ServerReportKey' -Type ([System.Data.SqlDbType]::NVarChar) -Size 128 -Value $ServerReportKey
    Add-SqlParameter -Command $command -Name '@IsEnabled' -Type ([System.Data.SqlDbType]::Bit) -Value $IsEnabled
    Add-SqlParameter -Command $command -Name '@SortOrder' -Type ([System.Data.SqlDbType]::Int) -Value $SortOrder

    try {
        return $command.ExecuteScalar()
    }
    finally {
        $command.Dispose()
    }
}

function Set-ContentSeedRoleAccess {
    param(
        [Parameter(Mandatory = $true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory = $true)][Guid]$ContentId,
        [Parameter(Mandatory = $true)][string]$RoleName,
        [bool]$CanRead,
        [bool]$CanWrite
    )

    $command = $Connection.CreateCommand()
    $command.CommandTimeout = 3600
    $command.CommandText = @"
DECLARE @RoleId int;
SELECT @RoleId = RoleId FROM omp.Roles WHERE Name = @RoleName;

IF @RoleId IS NULL
    THROW 53110, 'Content Web App seed role was not found.', 1;

MERGE omp_content.content_role_access AS target
USING
(
    SELECT @ContentId AS content_id,
           @RoleId AS role_id,
           @CanRead AS can_read,
           @CanWrite AS can_write
) AS source
ON target.content_id = source.content_id
AND target.role_id = source.role_id
WHEN MATCHED THEN
    UPDATE SET can_read = source.can_read,
               can_write = source.can_write
WHEN NOT MATCHED THEN
    INSERT(content_id, role_id, can_read, can_write)
    VALUES(source.content_id, source.role_id, source.can_read, source.can_write);
"@

    Add-SqlParameter -Command $command -Name '@ContentId' -Type ([System.Data.SqlDbType]::UniqueIdentifier) -Value $ContentId
    Add-SqlParameter -Command $command -Name '@RoleName' -Type ([System.Data.SqlDbType]::NVarChar) -Size 200 -Value $RoleName
    Add-SqlParameter -Command $command -Name '@CanRead' -Type ([System.Data.SqlDbType]::Bit) -Value ($CanRead -or $CanWrite)
    Add-SqlParameter -Command $command -Name '@CanWrite' -Type ([System.Data.SqlDbType]::Bit) -Value $CanWrite

    try {
        [void]$command.ExecuteNonQuery()
    }
    catch {
        throw "Failed to apply Content Web App seed role access for role '$RoleName'. $($_.Exception.Message)"
    }
    finally {
        $command.Dispose()
    }
}

function Copy-ContentWebAppSeedReports {
    param([Parameter(Mandatory = $true)][string]$SeedRoot)

    $reportsRoot = Join-Path $SeedRoot 'reports'
    if (-not (Test-Path -LiteralPath $reportsRoot -PathType Container)) {
        return
    }

    $reportFiles = @(Get-ChildItem -LiteralPath $reportsRoot -Filter '*.json' -File)
    if ($reportFiles.Count -eq 0) {
        return
    }

    $targetRoot = Resolve-ContentWebAppSeedServerReportsPath
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null

    foreach ($reportFile in $reportFiles) {
        Copy-Item -LiteralPath $reportFile.FullName -Destination (Join-Path $targetRoot $reportFile.Name) -Force
    }

    Write-Host "Copied $($reportFiles.Count) Content Web App server report definition(s) to: $targetRoot"
}

function Copy-ContentWebAppSeedHtmlFiles {
    param([Parameter(Mandatory = $true)][string]$SeedRoot)

    $pagesRoot = Join-Path $SeedRoot 'pages'
    if (-not (Test-Path -LiteralPath $pagesRoot -PathType Container)) {
        return
    }

    $htmlFiles = @(
        Get-ChildItem -LiteralPath $pagesRoot -File |
            Where-Object { $_.Extension -in @('.html', '.htm') }
    )
    if ($htmlFiles.Count -eq 0) {
        return
    }

    $targetRoot = Resolve-ContentWebAppSeedHtmlFilesPath
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null

    foreach ($htmlFile in $htmlFiles) {
        Copy-Item -LiteralPath $htmlFile.FullName -Destination (Join-Path $targetRoot $htmlFile.Name) -Force
    }

    Write-Host "Copied $($htmlFiles.Count) Content Web App HTML file(s) to: $targetRoot"
}

function Import-ContentWebAppSeedPages {
    param([Parameter(Mandatory = $true)][string]$SeedRoot)

    $manifestPath = Join-Path $SeedRoot 'content-seed.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $pages = @((Get-ObjectPropertyValue -Entry $manifest -Name 'pages' -DefaultValue @()))
    if ($pages.Count -eq 0) {
        return
    }

    $connection = New-Object System.Data.SqlClient.SqlConnection (Get-SqlConnectionString -TargetDatabase $script:Database)
    $connection.Open()
    try {
        foreach ($page in $pages) {
            $slug = [string](Get-ObjectPropertyValue -Entry $page -Name 'slug' -DefaultValue '')
            $title = [string](Get-ObjectPropertyValue -Entry $page -Name 'title' -DefaultValue $slug)
            $contentType = ([string](Get-ObjectPropertyValue -Entry $page -Name 'contentType' -DefaultValue 'html')).ToLowerInvariant()
            $bodyFile = [string](Get-ObjectPropertyValue -Entry $page -Name 'bodyFile' -DefaultValue '')
            $serverReportKey = Get-ObjectPropertyValue -Entry $page -Name 'serverReportKey' -DefaultValue $null
            $htmlFileKey = Get-ObjectPropertyValue -Entry $page -Name 'htmlFileKey' -DefaultValue $null
            $isEnabled = [bool](Get-ObjectPropertyValue -Entry $page -Name 'isEnabled' -DefaultValue $true)
            $sortOrder = Get-ObjectPropertyValue -Entry $page -Name 'sortOrder' -DefaultValue $null

            if ([string]::IsNullOrWhiteSpace($slug)) {
                throw "Content Web App seed page in '$manifestPath' is missing slug."
            }

            if ($contentType -notin @('markdown', 'html', 'html_file', 'server_report')) {
                throw "Content Web App seed page '$slug' has unsupported content type '$contentType'."
            }

            if ($contentType -eq 'html_file' -and [string]::IsNullOrWhiteSpace([string]$htmlFileKey)) {
                throw "Content Web App seed page '$slug' is an HTML file page but has no htmlFileKey."
            }

            if ($contentType -eq 'server_report' -and [string]::IsNullOrWhiteSpace([string]$serverReportKey)) {
                throw "Content Web App seed page '$slug' is a server report page but has no serverReportKey."
            }

            $contentKey = if ($contentType -eq 'html_file') {
                $htmlFileKey
            } elseif ($contentType -eq 'server_report') {
                $serverReportKey
            } else {
                $null
            }

            $body = ''
            if ($contentType -in @('markdown', 'html') -and -not [string]::IsNullOrWhiteSpace($bodyFile)) {
                $bodyPath = Resolve-ContentSeedFile -SeedRoot $SeedRoot -RelativePath $bodyFile
                $body = Get-Content -LiteralPath $bodyPath -Raw -Encoding UTF8
            }

            $contentId = [Guid](Invoke-ContentSeedPageUpsert `
                -Connection $connection `
                -Slug $slug `
                -Title $title `
                -ContentType $contentType `
                -Body $body `
                -ServerReportKey $contentKey `
                -IsEnabled $isEnabled `
                -SortOrder $sortOrder)

            $roleAccesses = @((Get-ObjectPropertyValue -Entry $page -Name 'roleAccesses' -DefaultValue @()))
            foreach ($roleAccess in $roleAccesses) {
                $roleName = [string](Get-ObjectPropertyValue -Entry $roleAccess -Name 'roleName' -DefaultValue '')
                if ([string]::IsNullOrWhiteSpace($roleName)) {
                    throw "Content Web App seed page '$slug' has a role access entry without roleName."
                }

                $canWrite = [bool](Get-ObjectPropertyValue -Entry $roleAccess -Name 'canWrite' -DefaultValue $false)
                $canRead = [bool](Get-ObjectPropertyValue -Entry $roleAccess -Name 'canRead' -DefaultValue $true)
                Set-ContentSeedRoleAccess -Connection $connection -ContentId $contentId -RoleName $roleName -CanRead $canRead -CanWrite $canWrite
            }

            Write-Host "Imported Content Web App seed page: $slug"
        }
    }
    finally {
        $connection.Dispose()
    }
}

function Install-ContentWebAppSeed {
    if (-not $script:InstallContentWebApp) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($script:ContentWebAppSeedPath) -or
        -not (Test-Path -LiteralPath $script:ContentWebAppSeedPath -PathType Container)) {
        return
    }

    Write-Step 'Installing Content Web App seed'
    Copy-ContentWebAppSeedReports -SeedRoot $script:ContentWebAppSeedPath
    Copy-ContentWebAppSeedHtmlFiles -SeedRoot $script:ContentWebAppSeedPath

    if ($script:RunSql) {
        Import-ContentWebAppSeedPages -SeedRoot $script:ContentWebAppSeedPath
    }
    else {
        Write-Warning 'Skipping Content Web App seed page import because Options.RunSql is false.'
    }
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
        @{ Path = (Join-Path $script:WebAppsRoot $script:ContentWebAppPath); IncludePortalTopBar = $true; IncludeOpenDocViewer = $false; ContentWebAppModule = $true },
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
                UseForwardedHeaders = $script:PortalUseForwardedHeaders
                ForwardedHeadersTrustAllProxies = $script:PortalForwardedHeadersTrustAllProxies
                ForwardedHeadersKnownProxies = $script:PortalForwardedHeadersKnownProxies
                ForwardedHeadersKnownNetworks = $script:PortalForwardedHeadersKnownNetworks
            }
        }

        if ([string]::Equals([System.IO.Path]::GetFullPath([string]$folder.Path), [System.IO.Path]::GetFullPath($script:PortalPath), [StringComparison]::OrdinalIgnoreCase)) {
            $settings.Portal['Title'] = $script:PortalTitle
            $settings.Portal['DefaultCulture'] = $script:DefaultCulture
            $settings.Portal['SupportedCultures'] = $script:SupportedCultures
            $settings.Portal['AllowAnonymous'] = $script:PortalAllowAnonymous
            $settings.Portal['PermissionMode'] = $script:PortalPermissionMode
            $settings.ArtifactUpload = [ordered]@{
                ArtifactStoreRoot = $script:ArtifactStoreRoot
                AvailableModuleDefinitionsRoot = Join-Path $script:ArtifactStoreRoot '_available\module-definitions'
                AvailableArtifactsRoot = Join-Path $script:ArtifactStoreRoot '_available\artifacts'
                AvailableHostConfigurationsRoot = Join-Path $script:ArtifactStoreRoot '_available\host-configs'
                AvailableConfigOverlaysRoot = Join-Path $script:ArtifactStoreRoot '_available\config-overlays'
                MaxUploadBytes = 536870912
            }
        }

        if ([bool]$folder.IncludeOpenDocViewer) {
            $settings.OpenDocViewer = [ordered]@{
                BaseUrl = $odvBaseUrl
                SampleFileUrl = $odvSampleUrl
            }
        }

        if ($folder.ContainsKey('ContentWebAppModule') -and [bool]$folder.ContentWebAppModule) {
            $settings.ContentWebAppModule = [ordered]@{
                AppInstanceId = $script:ContentWebAppAppInstanceId
                HomeSlug = $script:ContentWebAppHomeSlug
                ServerReportsPath = $script:ContentWebAppServerReportsPath
                HtmlFilesPath = $script:ContentWebAppHtmlFilesPath
                AllowedServerReportDatabases = $script:ContentWebAppAllowedServerReportDatabases
                ServerReportDefaultMaxRows = $script:ContentWebAppServerReportDefaultMaxRows
                ServerReportMaxRowsLimit = $script:ContentWebAppServerReportMaxRowsLimit
                ServerReportQueryTimeoutSeconds = $script:ContentWebAppServerReportQueryTimeoutSeconds
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
                MaterializeTemplates = $true
                ProcessHostDeployments = $true
                ProvisionAppInstanceArtifacts = $true
                ProvisionExplicitRequirements = $true
                ArtifactZipImport = [ordered]@{
                    IsEnabled = $script:ArtifactZipImportEnabled
                    ImportPath = $script:ArtifactZipImportPath
                    ProcessedPath = $script:ArtifactZipImportProcessedPath
                    FailedPath = $script:ArtifactZipImportFailedPath
                    MaxFilesPerCycle = 10
                    CopyConfigurationFilesFromPreviousVersion = $true
                }
                DeployWebApps = $true
                IisSiteName = $script:IisSiteName
                WebAppsRoot = $script:WebAppsRoot
                PortalPhysicalPath = $script:PortalPath
                UseAppOfflineForWebAppDeployment = $true
                AppOfflineShutdownDelayMilliseconds = 1500
                StopIisAppPoolForWebAppDeployment = $false
                StartIisAppPoolAfterWebAppDeployment = $false
                IisAppPoolStopTimeoutSeconds = 30
                WebAppDeploymentExcludedEntries = @('appsettings.json', 'appsettings.*.json', 'logs', 'App_Data')
                DeployServiceApps = $true
                ServicesRoot = $script:ServicesRoot
                StopServiceForServiceAppDeployment = $true
                StartServiceAfterServiceAppDeployment = $true
                ServiceAppStopTimeoutSeconds = 30
                ServiceAppStartTimeoutSeconds = 30
                ServiceAppDeploymentExcludedEntries = @('appsettings.json', 'appsettings.*.json', 'logs', 'App_Data')
                FileMirrors = @(Get-HostAgentFileMirrors)
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
    Expand-ArtifactPayloadZip -ZipName 'OpenModulePlatform.Portal.zip' -Destination (Join-Path $script:ArtifactStoreRoot "omp-portal\web\$($script:Version)")
    Expand-PayloadZip -ZipName 'OpenModulePlatform.Auth.zip' -Destination (Join-Path $script:WebAppsRoot 'auth')

    if ($script:InstallOpenDocViewer) {
        Expand-PayloadZip -ZipName 'OpenDocViewer.dist.zip' -Destination (Join-Path $script:WebAppsRoot $script:OpenDocViewerAppPath)
        Expand-ArtifactPayloadZip -ZipName 'OpenDocViewer.dist.zip' -Destination (Join-Path $script:ArtifactStoreRoot "opendocviewer\web\$($script:OpenDocViewerVersion)")
    }

    if ($script:InstallContentWebApp) {
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.ContentWebAppModule.zip' -Destination (Join-Path $script:WebAppsRoot $script:ContentWebAppPath)
        Expand-ArtifactPayloadZip -ZipName 'OpenModulePlatform.Web.ContentWebAppModule.zip' -Destination (Join-Path $script:ArtifactStoreRoot "content-webapp\web\$($script:Version)")
    }

    if ($script:InstallExamples) {
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.ExampleWebAppModule.zip' -Destination (Join-Path $script:WebAppsRoot 'ExampleWebAppModule')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.ExampleWebAppBlazorModule.zip' -Destination (Join-Path $script:WebAppsRoot 'ExampleWebAppBlazorModule')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.ExampleServiceAppModule.zip' -Destination (Join-Path $script:WebAppsRoot 'ExampleServiceAppModule')
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.ExampleWorkerAppModule.zip' -Destination (Join-Path $script:WebAppsRoot 'ExampleWorkerAppModule')
        Expand-ArtifactPayloadZip -ZipName 'OpenModulePlatform.Web.ExampleWebAppModule.zip' -Destination (Join-Path $script:ArtifactStoreRoot "example-webapp\web\$($script:Version)")
        Expand-ArtifactPayloadZip -ZipName 'OpenModulePlatform.Web.ExampleWebAppBlazorModule.zip' -Destination (Join-Path $script:ArtifactStoreRoot "example-webapp-blazor\web\$($script:Version)")
        Expand-ArtifactPayloadZip -ZipName 'OpenModulePlatform.Web.ExampleServiceAppModule.zip' -Destination (Join-Path $script:ArtifactStoreRoot "example-serviceapp\web\$($script:Version)")
        Expand-ArtifactPayloadZip -ZipName 'OpenModulePlatform.Web.ExampleWorkerAppModule.zip' -Destination (Join-Path $script:ArtifactStoreRoot "example-workerapp\web\$($script:Version)")
        Expand-ArtifactPayloadZip -ZipName 'OpenModulePlatform.Service.ExampleServiceAppModule.zip' -Destination (Join-Path $script:ArtifactStoreRoot "example-serviceapp\service\$($script:Version)")
        Expand-ArtifactPayloadZip -ZipName 'OpenModulePlatform.Worker.ExampleWorkerAppModule.zip' -Destination (Join-Path $script:ArtifactStoreRoot "example-workerapp\worker\$($script:Version)")
    }

    if ($script:InstallIFrameWebApp) {
        Expand-PayloadZip -ZipName 'OpenModulePlatform.Web.iFrameWebAppModule.zip' -Destination (Join-Path $script:WebAppsRoot 'iFrameWebAppModule')
        Expand-ArtifactPayloadZip -ZipName 'OpenModulePlatform.Web.iFrameWebAppModule.zip' -Destination (Join-Path $script:ArtifactStoreRoot "iframe-webapp\web\$($script:Version)")
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
    Ensure-ConfiguredHosts
    Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot 'sql\OpenModulePlatform.Portal\1-setup-omp-portal.sql')
    Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot 'sql\OpenModulePlatform.Portal\2-initialize-omp-portal.sql') -PatchBootstrapPrincipal
    Ensure-OpenDocViewerMetadata

    if ($script:InstallContentWebApp) {
        $contentSqlFiles = @(
            'sql\OpenModulePlatform.Web.ContentWebAppModule\1-setup-content-webapp.sql',
            'sql\OpenModulePlatform.Web.ContentWebAppModule\3-add-server-report-support.sql',
            'sql\OpenModulePlatform.Web.ContentWebAppModule\2-initialize-content-webapp.sql'
        )

        foreach ($relativePath in $contentSqlFiles) {
            Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot $relativePath)
        }
    }

    if ($script:InstallExamples) {
        $exampleSqlFiles = @(
            'sql\examples\WebAppModule\1-setup-example-webapp.sql',
            'sql\examples\WebAppModule\2-initialize-example-webapp.sql',
            'sql\examples\WebAppBlazorModule\1-setup-example-webapp-blazor.sql',
            'sql\examples\WebAppBlazorModule\2-initialize-example-webapp-blazor.sql',
            'sql\examples\ServiceAppModule\1-setup-example-serviceapp.sql',
            'sql\examples\ServiceAppModule\2-initialize-example-serviceapp.sql',
            'sql\examples\WorkerAppModule\1-setup-example-workerapp.sql',
            'sql\examples\WorkerAppModule\2-initialize-example-workerapp.sql'
        )

        foreach ($relativePath in $exampleSqlFiles) {
            Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot $relativePath)
        }

        $exampleServicePath = (Join-Path $script:ServicesRoot 'ExampleServiceAppModule').Replace("'", "''")
        $exampleServiceName = ([string]$script:Services.ExampleService).Replace("'", "''")
        Invoke-SqlText -TargetDatabase $script:Database -Query @"
UPDATE omp.AppInstances
SET InstallPath = N'$exampleServicePath',
    InstallationName = N'$exampleServiceName',
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppInstanceKey = N'example_serviceapp_service';

UPDATE omp.InstanceTemplateAppInstances
SET InstallPath = N'$exampleServicePath',
    InstallationName = N'$exampleServiceName',
    UpdatedUtc = SYSUTCDATETIME()
WHERE AppInstanceKey = N'example_serviceapp_service';
"@
    }

    if ($script:InstallIFrameWebApp) {
        $iframeSqlFiles = @(
            'sql\OpenModulePlatform.Web.iFrameWebAppModule\1-setup-iframe-webapp.sql',
            'sql\OpenModulePlatform.Web.iFrameWebAppModule\2-initialize-iframe-webapp.sql'
        )

        foreach ($relativePath in $iframeSqlFiles) {
            Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot $relativePath)
        }
    }

    Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot 'sql\OpenModulePlatform.Portal\4-ensure-topbar-hover-user-setting.sql')
    Invoke-SqlFile -TargetDatabase $script:Database -Path (Join-Path $script:PackageRoot 'sql\OpenModulePlatform.Portal\3-sync-omp-portal-entries.sql')

    Ensure-ConfiguredConfigSettings
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

    foreach ($serviceName in @(
            $script:Services.HostAgent,
            $script:Services.WorkerManager,
            $script:Services.ExampleService,
            'OMP.Service.ExampleServiceAppModule',
            'OpenModulePlatform.HostAgent',
            'OpenModulePlatform.WorkerManager',
            'OpenModulePlatform.Service.ExampleServiceAppModule')) {
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
    }

    if ($script:InstallIFrameWebApp) {
        Ensure-IisWebApplication -AppPath 'iFrameWebAppModule' -PhysicalPath (Join-Path $script:WebAppsRoot 'iFrameWebAppModule') -AppPoolName $script:AppPools.IFrameWebApp -AnonymousEnabled $true
    }

    if ($script:InstallContentWebApp) {
        Ensure-IisWebApplication -AppPath $script:ContentWebAppPath -PhysicalPath (Join-Path $script:WebAppsRoot $script:ContentWebAppPath) -AppPoolName $script:AppPools.ContentWebApp -AnonymousEnabled $true
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

    if (-not $script:StartServices) {
        Write-Host 'Windows services were installed/configured with Automatic startup but were not started. Start them manually after validating the deployment.' -ForegroundColor Yellow
    }
}

function Grant-RunAsFolderAccess {
    if ($null -eq $script:RunAsCredential) {
        return
    }

    Write-Step 'Granting runtime folder access'
    foreach ($path in @($script:RuntimeRoot, $script:WebRoot, $script:WebAppsRoot, $script:ServicesRoot, $script:ArtifactStoreRoot, $script:ArtifactCacheRoot, $script:DataProtectionKeyPath)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        if (Test-IsUncPath -Path $path) {
            Write-Warning "Skipping ACL grant on UNC path. Ensure the run-as account has access: $path"
            continue
        }

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
$script:ArtifactZipImportEnabled = [bool](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportEnabled' -DefaultValue $false)
$script:ArtifactZipImportPath = [System.IO.Path]::GetFullPath([string](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportPath' -DefaultValue (Join-Path $script:RuntimeRoot 'ArtifactImports')))
$script:ArtifactZipImportProcessedPath = [string](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportProcessedPath' -DefaultValue '')
$script:ArtifactZipImportFailedPath = [string](Get-NestedConfigValue -Config $config -Section 'HostAgent' -Name 'ArtifactZipImportFailedPath' -DefaultValue '')
$script:DataProtectionKeyPath = [System.IO.Path]::GetFullPath([string](Get-ConfigValue -Config $config -Name 'DataProtectionKeyPath' -DefaultValue (Join-Path $script:RuntimeRoot 'DataProtectionKeys')))

$script:SqlServer = [string](Get-ConfigValue -Config $config -Name 'SqlServer' -DefaultValue 'localhost')
$script:Database = [string](Get-ConfigValue -Config $config -Name 'Database' -DefaultValue 'OpenModulePlatform')
$script:SqlAuthentication = [string](Get-ConfigValue -Config $config -Name 'SqlAuthentication' -DefaultValue 'Integrated')
$script:SqlUser = [string](Get-ConfigValue -Config $config -Name 'SqlUser' -DefaultValue '')
$script:SqlPassword = [string](Get-ConfigValue -Config $config -Name 'SqlPassword' -DefaultValue '')
$script:BootstrapPortalAdminPrincipals = @((Get-ConfigValue -Config $config -Name 'BootstrapPortalAdminPrincipals' -DefaultValue @("$env:USERDOMAIN\$env:USERNAME")))
$script:BootstrapPortalAdminPrincipalType = [string](Get-ConfigValue -Config $config -Name 'BootstrapPortalAdminPrincipalType' -DefaultValue 'ADUser')
$script:ConfigSettings = @((Get-ConfigValue -Config $config -Name 'ConfigSettings' -DefaultValue @()))
if ($script:BootstrapPortalAdminPrincipalType -ieq 'User') {
    $script:BootstrapPortalAdminPrincipalType = 'ADUser'
}
elseif ($script:BootstrapPortalAdminPrincipalType -ieq 'ADUser') {
    $script:BootstrapPortalAdminPrincipalType = 'ADUser'
}
elseif ($script:BootstrapPortalAdminPrincipalType -ieq 'ADGroup') {
    $script:BootstrapPortalAdminPrincipalType = 'ADGroup'
}
else {
    throw "BootstrapPortalAdminPrincipalType must be ADUser or ADGroup."
}
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
$script:ContentWebAppPath = [string](Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'ContentWebAppPath' -DefaultValue 'content')
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
$script:PortalForwardedHeadersTrustAllProxies = [bool](Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'ForwardedHeadersTrustAllProxies' -DefaultValue $false)
$script:PortalForwardedHeadersKnownProxies = @((Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'ForwardedHeadersKnownProxies' -DefaultValue @()))
$script:PortalForwardedHeadersKnownNetworks = @((Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'ForwardedHeadersKnownNetworks' -DefaultValue @()))
$script:PortalPermissionMode = [string](Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'PermissionMode' -DefaultValue 'Any')
$script:PortalBaseUrl = [string](Get-NestedConfigValue -Config $config -Section 'Portal' -Name 'PortalBaseUrl' -DefaultValue '')
if ([string]::IsNullOrWhiteSpace($script:PortalBaseUrl)) {
    $script:PortalBaseUrl = if ([string]::IsNullOrWhiteSpace($script:PublicBaseUrl)) { '/' } else { $script:PublicBaseUrl }
}
$script:ContentWebAppAppInstanceId = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'AppInstanceId' -DefaultValue '11111111-1111-1111-1111-111111111232')
$script:ContentWebAppHomeSlug = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'HomeSlug' -DefaultValue 'home')
$script:ContentWebAppServerReportsPath = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'ServerReportsPath' -DefaultValue 'App_Data/ContentReports')
$script:ContentWebAppHtmlFilesPath = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'HtmlFilesPath' -DefaultValue 'App_Data/ContentPages')
$script:ContentWebAppSharedServerReportsPath = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'SharedServerReportsPath' -DefaultValue '')
$script:ContentWebAppSharedHtmlFilesPath = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'SharedHtmlFilesPath' -DefaultValue '')
$script:ContentWebAppAllowedServerReportDatabases = @(
    Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'AllowedServerReportDatabases' -DefaultValue @($script:Database)
)
$script:ContentWebAppServerReportDefaultMaxRows = [int](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'ServerReportDefaultMaxRows' -DefaultValue 100)
$script:ContentWebAppServerReportMaxRowsLimit = [int](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'ServerReportMaxRowsLimit' -DefaultValue 1000)
$script:ContentWebAppServerReportQueryTimeoutSeconds = [int](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'ServerReportQueryTimeoutSeconds' -DefaultValue 30)
$script:OpenDocViewerDisplayName = [string](Get-NestedConfigValue -Config $config -Section 'OpenDocViewer' -Name 'DisplayName' -DefaultValue 'OpenDocViewer')
$script:OpenDocViewerVersion = [string](Get-NestedConfigValue -Config $config -Section 'OpenDocViewer' -Name 'Version' -DefaultValue '')
if ([string]::IsNullOrWhiteSpace($script:OpenDocViewerDisplayName)) {
    $script:OpenDocViewerDisplayName = 'OpenDocViewer'
}

$defaultAppPools = @{
    Portal = 'OMP_Portal'
    Auth = 'OMP_Auth'
    OpenDocViewer = 'OMP_OpenDocViewer'
    ExampleWebApp = 'OMP_ExampleWebAppModule'
    ExampleWebAppBlazor = 'OMP_ExampleWebAppBlazorModule'
    ExampleServiceWebApp = 'OMP_ExampleServiceAppModule'
    ExampleWorkerWebApp = 'OMP_ExampleWorkerAppModule'
    ContentWebApp = 'OMP_ContentWebAppModule'
    IFrameWebApp = 'OMP_iFrameWebAppModule'
}
$configuredPools = Get-NestedConfigValue -Config $config -Section 'Iis' -Name 'AppPools' -DefaultValue @{}
foreach ($key in @($configuredPools.Keys)) {
    $defaultAppPools[$key] = [string]$configuredPools[$key]
}
$script:AppPools = [pscustomobject]$defaultAppPools

$defaultServices = @{
    HostAgent = 'OMP.HostAgent'
    WorkerManager = 'OMP.WorkerManager'
    ExampleService = 'OMP.Service.ExampleServiceAppModule'
}
$configuredServices = Get-ConfigValue -Config $config -Name 'Services' -DefaultValue @{}
foreach ($key in @($configuredServices.Keys)) {
    $defaultServices[$key] = [string]$configuredServices[$key]
}
$script:Services = [pscustomobject]$defaultServices

$script:InstallOpenDocViewer = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallOpenDocViewer' -DefaultValue $true)
$script:InstallContentWebApp = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallContentWebApp' -DefaultValue $true)
$script:InstallIFrameWebApp = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallIFrameWebApp' -DefaultValue $true)
$script:InstallExamples = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallExamples' -DefaultValue $true)
$script:InstallRuntimeServices = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallRuntimeServices' -DefaultValue $true)
$script:InstallExampleService = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'InstallExampleService' -DefaultValue $true)
$script:ConfigureIis = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'ConfigureIis' -DefaultValue $true)
$script:RunSql = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'RunSql' -DefaultValue $true)
$script:StartServices = [bool](Get-NestedConfigValue -Config $config -Section 'Options' -Name 'StartServices' -DefaultValue $false)
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

$script:OpenDocViewerVersion = Resolve-OpenDocViewerPackageVersion

$contentWebAppSeedPath = [string](Get-NestedConfigValue -Config $config -Section 'ContentWebApp' -Name 'SeedPath' -DefaultValue 'content-webapp-seed')
if ([string]::IsNullOrWhiteSpace($contentWebAppSeedPath)) {
    $script:ContentWebAppSeedPath = ''
}
else {
    $script:ContentWebAppSeedPath = Resolve-DeploymentPath -Path $contentWebAppSeedPath -BasePath $script:PackageRoot
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
Install-ContentWebAppSeed
Ensure-Iis
Start-ConfiguredAppPools
Ensure-Services

Write-Host ''
Write-Host 'OpenModulePlatform suite installation completed.' -ForegroundColor Green
