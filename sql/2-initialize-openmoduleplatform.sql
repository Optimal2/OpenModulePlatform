-- File: sql/2-initialize-openmoduleplatform.sql
-- IMPORTANT: use the HostAgent-first installer for automated local installs, or
-- replace the bootstrap literals below manually with a single-quote-escaped
-- Windows principal and the matching principal type.
/*
OpenModulePlatform core initialization script.

Seeds the default OMP instance, bootstrap RBAC placeholders, baseline host
rows, and shared structural values that live in the omp schema.

Prerequisites:
- Run 1-setup-openmoduleplatform.sql first.
- Set @BootstrapPortalAdminPrincipal to the Windows user or group that should
  receive the initial PortalAdmins role.
- Set @BootstrapPortalAdminPrincipalType to N'ADUser' for a Windows user or
  N'ADGroup' for a Windows group. Legacy N'User' values are normalized to
  N'ADUser'. The source default is N'ADUser' because local bootstrap installs
  normally grant the current Windows user. Prefer the HostAgent-first installer
  for local user installs because it escapes the value before running SQL.

Portal, content, iframe, and example modules are initialized separately from
their own module sql folders.
*/
USE [OpenModulePlatform];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

-------------------------------------------------------------------------------
-- Seed baseline instance, templates, host, and structural placeholders
-------------------------------------------------------------------------------
DECLARE @DefaultInstanceId uniqueidentifier;
DECLARE @DefaultHostId uniqueidentifier;
-- The seeded host is a development/template placeholder for current Windows IIS
-- deployments. Real hosts can override Architecture during environment setup.
DECLARE @DefaultHostArchitecture nvarchar(20) = N'x64';
DECLARE @DefaultInstanceTemplateId int;
DECLARE @DefaultHostTemplateId int;
DECLARE @IisHostTemplateId int;
DECLARE @ServiceHostTemplateId int;
DECLARE @PortalAdminsRoleId int;
DECLARE @EveryoneRoleId int;
DECLARE @AuthenticatedUsersRoleId int;
DECLARE @BaselineHostAgentArtifactVersion nvarchar(50) = N'0.3.55';
DECLARE @BaselineWorkerManagerArtifactVersion nvarchar(50) = N'0.3.8';
DECLARE @BaselineWorkerProcessHostArtifactVersion nvarchar(50) = N'0.3.3';
DECLARE @DefaultHostKey nvarchar(128) = N'sample-host';
DECLARE @DefaultHostDisplayName nvarchar(200) = N'Sample Host';
DECLARE @DefaultHostEnvironment nvarchar(50) = N'Development';
DECLARE @SeedDefaultHost bit = 1;
DECLARE @SuppressedDefaultHostId uniqueidentifier;
-- SECURITY: This sentinel placeholder is only for source-controlled bootstrap
-- scripts. It must never be executed in production unchanged. Deployment
-- automation should patch it from a protected environment-specific value; the
-- THROW check below is a fail-safe, not the primary control.
-- The bootstrap stays as a literal replacement instead of a stored procedure
-- parameter so the same script can initialize a clean database with sqlcmd or
-- the local installer before any application-side deployment helpers exist.
DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = N'__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__';
DECLARE @BootstrapPortalAdminPrincipalType nvarchar(50) = N'ADUser';
DECLARE @InsertedInstanceTemplateIds TABLE (InstanceTemplateId int NOT NULL);
DECLARE @InsertedHostTemplateIds TABLE (HostTemplateId int NOT NULL);

IF @BootstrapPortalAdminPrincipal = N'__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__'
BEGIN
    -- Set @BootstrapPortalAdminPrincipal manually or use the HostAgent-first
    -- installer profile bootstrapPortalAdminPrincipal setting.
    -- This script inserts one bootstrap principal per execution.
    THROW 51000, 'Bootstrap portal admin principal was not replaced.', 1;
END

-- The setup script also adds database constraint
-- CK_omp_RolePrincipals_NoBootstrapPlaceholders so this sentinel cannot be
-- persisted if a different deployment path bypasses the initialization guard.

-- Keep bootstrap principal type normalization here even though the setup script
-- also migrates stored legacy values. This script is often patched and executed
-- independently by installers, so it must validate its own input.
SET @BootstrapPortalAdminPrincipalType =
    CASE UPPER(LTRIM(RTRIM(@BootstrapPortalAdminPrincipalType)))
        WHEN N'USER' THEN N'ADUser'
        WHEN N'ADUSER' THEN N'ADUser'
        WHEN N'ADGROUP' THEN N'ADGroup'
        ELSE @BootstrapPortalAdminPrincipalType
    END;

IF @BootstrapPortalAdminPrincipalType NOT IN (N'ADUser', N'ADGroup')
BEGIN
    THROW 51003, 'Set @BootstrapPortalAdminPrincipalType to N''ADUser'' for a Windows user or N''ADGroup'' for a Windows group.', 1;
END

-- Keep this validation intentionally Windows-principal oriented. The bootstrap
-- principal is seeded into AD-backed RBAC and should be either DOMAIN\Name or
-- UPN form; customer-specific installers patch this literal before execution.
IF @BootstrapPortalAdminPrincipal <> LTRIM(RTRIM(@BootstrapPortalAdminPrincipal))
BEGIN
    THROW 51004, 'Bootstrap principal must not contain leading or trailing whitespace.', 1;
END

-- Disallow characters that are invalid in Windows/AD account names or risky in
-- deployment logs/scripts. Newlines are handled here instead of the trim check
-- above because LTRIM/RTRIM are kept for compatibility-style edge trimming.
-- Bootstrap accepts only DOMAIN\Name or user@domain.
DECLARE @BootstrapPrincipalInvalidCharacters TABLE (Value nvarchar(1) NOT NULL);
INSERT INTO @BootstrapPrincipalInvalidCharacters(Value)
VALUES(N''''), (N'"'), (N'<'), (N'>'), (N'|'), (N'?'), (N'*'), (N';'), (NCHAR(10)), (NCHAR(13));

IF EXISTS
(
    SELECT 1
    FROM @BootstrapPrincipalInvalidCharacters invalid
    WHERE CHARINDEX(invalid.Value, @BootstrapPortalAdminPrincipal) > 0
)
BEGIN
    THROW 51005, 'Bootstrap principal contains characters that are not valid for AD bootstrap values.', 1;
END

DECLARE @BootstrapPrincipalSlashPosition int = CHARINDEX(N'\', @BootstrapPortalAdminPrincipal);
DECLARE @BootstrapPrincipalAtPosition int = CHARINDEX(N'@', @BootstrapPortalAdminPrincipal);
DECLARE @BootstrapPrincipalSecondSlashPosition int = 0;
DECLARE @BootstrapPrincipalSecondAtPosition int = 0;

IF @BootstrapPrincipalSlashPosition > 0
BEGIN
    SET @BootstrapPrincipalSecondSlashPosition = CHARINDEX(N'\', @BootstrapPortalAdminPrincipal, @BootstrapPrincipalSlashPosition + 1);
END

IF @BootstrapPrincipalAtPosition > 0
BEGIN
    SET @BootstrapPrincipalSecondAtPosition = CHARINDEX(N'@', @BootstrapPortalAdminPrincipal, @BootstrapPrincipalAtPosition + 1);
END

IF @BootstrapPrincipalSlashPosition > 0 AND @BootstrapPrincipalAtPosition > 0
BEGIN
    THROW 51009, 'Bootstrap principal must use either DOMAIN\Name or user@domain form, not both.', 1;
END

IF @BootstrapPrincipalSecondSlashPosition > 0
BEGIN
    THROW 51010, 'Bootstrap principal DOMAIN\Name form must contain exactly one backslash.', 1;
END

IF @BootstrapPrincipalSecondAtPosition > 0
BEGIN
    THROW 51011, 'Bootstrap principal UPN form must contain exactly one at sign.', 1;
END

IF @BootstrapPrincipalSlashPosition = 0 AND @BootstrapPrincipalAtPosition = 0
BEGIN
    THROW 51006, 'Bootstrap principal must use DOMAIN\Name or user@domain form.', 1;
END

IF @BootstrapPrincipalSlashPosition > 0
    AND (@BootstrapPrincipalSlashPosition = 1 OR @BootstrapPrincipalSlashPosition = LEN(@BootstrapPortalAdminPrincipal))
BEGIN
    THROW 51007, 'Bootstrap principal DOMAIN\Name form is incomplete.', 1;
END

IF @BootstrapPrincipalAtPosition > 0
    AND (@BootstrapPrincipalAtPosition = 1 OR @BootstrapPrincipalAtPosition = LEN(@BootstrapPortalAdminPrincipal))
BEGIN
    THROW 51008, 'Bootstrap principal UPN form is incomplete.', 1;
END


IF NOT EXISTS (SELECT 1 FROM omp.InstanceTemplates WHERE TemplateKey = N'default')
BEGIN
    INSERT INTO omp.InstanceTemplates(TemplateKey, DisplayName, Description)
    OUTPUT inserted.InstanceTemplateId INTO @InsertedInstanceTemplateIds(InstanceTemplateId)
    VALUES(N'default', N'Default Instance Template', N'Minimal baseline template for an OMP instance');
END

IF NOT EXISTS (SELECT 1 FROM omp.HostTemplates WHERE TemplateKey = N'default-host')
BEGIN
    INSERT INTO omp.HostTemplates(TemplateKey, DisplayName, Description)
    OUTPUT inserted.HostTemplateId INTO @InsertedHostTemplateIds(HostTemplateId)
    VALUES(N'default-host', N'Default Host Template', N'Minimal baseline host template for development and examples');
END

MERGE omp.HostTemplates AS target
USING
(
    VALUES
        (N'IISHost', N'IIS Host', N'Hosts that run IIS web applications for this OMP installation.', 100, CONVERT(bit, 1)),
        (N'ServiceHost', N'Service Host', N'Hosts that run Windows services and worker runtimes for this OMP installation.', 200, CONVERT(bit, 1))
) AS source(TemplateKey, DisplayName, Description, SortOrder, IsEnabled)
ON target.TemplateKey = source.TemplateKey
WHEN MATCHED THEN
    UPDATE SET DisplayName = source.DisplayName,
               Description = source.Description,
               SortOrder = source.SortOrder,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(TemplateKey, DisplayName, Description, SortOrder, IsEnabled)
    VALUES(source.TemplateKey, source.DisplayName, source.Description, source.SortOrder, source.IsEnabled);

SELECT TOP (1) @DefaultInstanceTemplateId = InstanceTemplateId
FROM @InsertedInstanceTemplateIds;

IF @DefaultInstanceTemplateId IS NULL
BEGIN
    SELECT TOP (1) @DefaultInstanceTemplateId = InstanceTemplateId
    FROM omp.InstanceTemplates
    WHERE TemplateKey = N'default'
    ORDER BY InstanceTemplateId;
END

IF @DefaultInstanceTemplateId IS NULL
BEGIN
    THROW 51001, 'Unable to resolve the default instance template id after seeding omp.InstanceTemplates.', 1;
END

SELECT TOP (1) @DefaultHostTemplateId = HostTemplateId
FROM @InsertedHostTemplateIds;

IF @DefaultHostTemplateId IS NULL
BEGIN
    SELECT TOP (1) @DefaultHostTemplateId = HostTemplateId
    FROM omp.HostTemplates
    WHERE TemplateKey = N'default-host'
    ORDER BY HostTemplateId;
END

IF @DefaultHostTemplateId IS NULL
BEGIN
    THROW 51002, 'Unable to resolve the default host template id after seeding omp.HostTemplates.', 1;
END

SELECT TOP (1) @IisHostTemplateId = HostTemplateId
FROM omp.HostTemplates
WHERE TemplateKey = N'IISHost'
ORDER BY HostTemplateId;

SELECT TOP (1) @ServiceHostTemplateId = HostTemplateId
FROM omp.HostTemplates
WHERE TemplateKey = N'ServiceHost'
ORDER BY HostTemplateId;

IF @IisHostTemplateId IS NULL OR @ServiceHostTemplateId IS NULL
BEGIN
    THROW 51003, 'Unable to resolve the standard IISHost and ServiceHost role ids after seeding omp.HostTemplates.', 1;
END

SELECT @DefaultInstanceId = InstanceId
FROM omp.Instances
WHERE InstanceKey = N'default';

IF @DefaultInstanceId IS NULL
BEGIN
    SET @DefaultInstanceId = NEWID();

    INSERT INTO omp.Instances(
        InstanceId,
        InstanceKey,
        DisplayName,
        Description,
        InstanceTemplateId)
    VALUES(
        @DefaultInstanceId,
        N'default',
        N'Default Instance',
        N'Default OMP instance seeded by the install script',
        @DefaultInstanceTemplateId);
END
ELSE
BEGIN
    UPDATE omp.Instances
    SET InstanceKey = N'default',
        DisplayName = N'Default Instance',
        Description = N'Default OMP instance seeded by the install script',
        InstanceTemplateId = @DefaultInstanceTemplateId,
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceId = @DefaultInstanceId;
END

SELECT @DefaultHostId = HostId
FROM omp.Hosts
WHERE InstanceId = @DefaultInstanceId
  AND HostKey = @DefaultHostKey;

IF @DefaultHostKey = N'sample-host'
   AND EXISTS
   (
       SELECT 1
       FROM omp.Hosts
       WHERE InstanceId = @DefaultInstanceId
         AND HostKey <> @DefaultHostKey
         AND IsEnabled = 1
   )
BEGIN
    -- sample-host is only a source-controlled bootstrap placeholder. Once a
    -- real host exists in the installation, repair scripts must not recreate
    -- or keep the placeholder in desired topology or HostAgent upgrade views.
    SET @SeedDefaultHost = 0;

    SELECT @SuppressedDefaultHostId = HostId
    FROM omp.Hosts
    WHERE InstanceId = @DefaultInstanceId
      AND HostKey = @DefaultHostKey;

    IF @SuppressedDefaultHostId IS NOT NULL
    BEGIN
        UPDATE omp.Hosts
        SET IsEnabled = 0,
            UpdatedUtc = SYSUTCDATETIME()
        WHERE HostId = @SuppressedDefaultHostId;

        UPDATE omp.HostDeploymentAssignments
        SET IsActive = 0,
            UpdatedUtc = SYSUTCDATETIME()
        WHERE HostId = @SuppressedDefaultHostId;

        UPDATE omp.HostAgentDesiredStates
        SET IsEnabled = 0,
            UpdatedUtc = SYSUTCDATETIME()
        WHERE HostId = @SuppressedDefaultHostId;
    END

    UPDATE omp.InstanceTemplateHosts
    SET IsEnabled = 0,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceTemplateId = @DefaultInstanceTemplateId
      AND HostKey = @DefaultHostKey;

    SET @DefaultHostId = NULL;
END

IF @SeedDefaultHost = 1 AND @DefaultHostId IS NULL
BEGIN
    SET @DefaultHostId = NEWID();

    INSERT INTO omp.Hosts(HostId, InstanceId, HostKey, DisplayName, BaseUrl, Environment, OsFamily, Architecture)
    VALUES(@DefaultHostId, @DefaultInstanceId, @DefaultHostKey, @DefaultHostDisplayName, NULL, @DefaultHostEnvironment, N'Windows', @DefaultHostArchitecture);
END
ELSE IF @SeedDefaultHost = 1
BEGIN
    UPDATE omp.Hosts
    SET InstanceId = @DefaultInstanceId,
        HostKey = @DefaultHostKey,
        DisplayName = @DefaultHostDisplayName,
        BaseUrl = NULL,
        Environment = @DefaultHostEnvironment,
        OsFamily = N'Windows',
        Architecture = @DefaultHostArchitecture,
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE HostId = @DefaultHostId;
END

IF @SeedDefaultHost = 1
   AND NOT EXISTS (SELECT 1 FROM omp.InstanceTemplateHosts WHERE InstanceTemplateId = @DefaultInstanceTemplateId AND HostKey = @DefaultHostKey)
BEGIN
    INSERT INTO omp.InstanceTemplateHosts(InstanceTemplateId, HostTemplateId, HostKey, DisplayName, Environment, SortOrder)
    VALUES(@DefaultInstanceTemplateId, @DefaultHostTemplateId, @DefaultHostKey, @DefaultHostDisplayName, @DefaultHostEnvironment, 100);
END

IF @SeedDefaultHost = 1 AND @DefaultHostId IS NOT NULL
BEGIN
    MERGE omp.HostDeploymentAssignments AS target
    USING
    (
        VALUES
            (@DefaultHostId, @DefaultHostTemplateId, N'install-script', CONVERT(bit, 1)),
            (@DefaultHostId, @IisHostTemplateId, N'install-script', CONVERT(bit, 1)),
            (@DefaultHostId, @ServiceHostTemplateId, N'install-script', CONVERT(bit, 1))
    ) AS source(HostId, HostTemplateId, AssignedBy, IsActive)
    ON target.HostId = source.HostId
    AND target.HostTemplateId = source.HostTemplateId
    WHEN MATCHED THEN
        UPDATE SET AssignedBy = source.AssignedBy,
                   IsActive = source.IsActive
    WHEN NOT MATCHED THEN
        INSERT(HostId, HostTemplateId, AssignedBy, IsActive)
        VALUES(source.HostId, source.HostTemplateId, source.AssignedBy, source.IsActive);
END

-------------------------------------------------------------------------------
-- Seed core module metadata
-------------------------------------------------------------------------------
DECLARE @CoreModuleId int;
DECLARE @HostAgentAppId int;
DECLARE @WorkerManagerAppId int;
DECLARE @WorkerProcessHostAppId int;
DECLARE @HostAgentArtifactId int;
DECLARE @WorkerManagerArtifactId int;
DECLARE @WorkerProcessHostArtifactId int;
DECLARE @CoreTemplateModuleInstanceId int;
DECLARE @DefaultInstanceTemplateHostId int;

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'omp_core')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'OpenModulePlatform Core',
        ModuleType = N'PlatformCore',
        SchemaName = N'omp',
        Description = N'Core OMP schema, bootstrap data, and host-local infrastructure metadata.',
        IsEnabled = 1,
        SortOrder = 0,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'omp_core';
END
ELSE
BEGIN
    INSERT INTO omp.Modules(
        ModuleKey,
        DisplayName,
        ModuleType,
        SchemaName,
        Description,
        IsEnabled,
        SortOrder)
    VALUES(
        N'omp_core',
        N'OpenModulePlatform Core',
        N'PlatformCore',
        N'omp',
        N'Core OMP schema, bootstrap data, and host-local infrastructure metadata.',
        1,
        0);
END

SELECT @CoreModuleId = ModuleId
FROM omp.Modules
WHERE ModuleKey = N'omp_core';

MERGE omp.Apps AS target
USING
(
    VALUES
        (@CoreModuleId, N'omp_hostagent', N'OMP HostAgent', N'HostAgent', N'Host-local OMP deployment agent that can prepare versioned self-upgrades.', 10, CONVERT(bit, 1)),
        (@CoreModuleId, N'omp_workermanager', N'OMP WorkerManager', N'ServiceApp', N'Host-local Windows service that starts and supervises OMP worker plugin processes.', 20, CONVERT(bit, 1)),
        (@CoreModuleId, N'omp_workerprocesshost', N'OMP Worker Process Host', N'WorkerHost', N'Host-local executable used by WorkerManager to run worker plugin artifacts.', 30, CONVERT(bit, 1))
) AS source(ModuleId, AppKey, DisplayName, AppType, Description, SortOrder, IsEnabled)
ON target.ModuleId = source.ModuleId
AND target.AppKey = source.AppKey
WHEN MATCHED THEN
    UPDATE SET DisplayName = source.DisplayName,
               AppType = source.AppType,
               Description = source.Description,
               SortOrder = source.SortOrder,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(ModuleId, AppKey, DisplayName, AppType, Description, SortOrder, IsEnabled)
    VALUES(source.ModuleId, source.AppKey, source.DisplayName, source.AppType, source.Description, source.SortOrder, source.IsEnabled);

SELECT TOP (1) @HostAgentAppId = AppId
FROM omp.Apps
WHERE ModuleId = @CoreModuleId
  AND AppKey = N'omp_hostagent'
ORDER BY AppId;

SELECT TOP (1) @WorkerManagerAppId = AppId
FROM omp.Apps
WHERE ModuleId = @CoreModuleId
  AND AppKey = N'omp_workermanager'
ORDER BY AppId;

SELECT TOP (1) @WorkerProcessHostAppId = AppId
FROM omp.Apps
WHERE ModuleId = @CoreModuleId
  AND AppKey = N'omp_workerprocesshost'
ORDER BY AppId;

MERGE omp.Artifacts AS target
USING
(
    SELECT @HostAgentAppId AS AppId,
           @BaselineHostAgentArtifactVersion AS Version,
           N'host-agent' AS PackageType,
           N'omp-hostagent' AS TargetName,
           N'omp-hostagent/hostagent/' + @BaselineHostAgentArtifactVersion AS RelativePath,
           CAST(1 AS bit) AS IsEnabled
    UNION ALL
    SELECT @WorkerManagerAppId AS AppId,
           @BaselineWorkerManagerArtifactVersion AS Version,
           N'service-app' AS PackageType,
           N'omp-workermanager' AS TargetName,
           N'omp-workermanager/service/' + @BaselineWorkerManagerArtifactVersion AS RelativePath,
           CAST(1 AS bit) AS IsEnabled
    UNION ALL
    SELECT @WorkerProcessHostAppId AS AppId,
           @BaselineWorkerProcessHostArtifactVersion AS Version,
           N'worker-host' AS PackageType,
           N'omp-workerprocesshost' AS TargetName,
           N'omp-workerprocesshost/host/' + @BaselineWorkerProcessHostArtifactVersion AS RelativePath,
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
    INSERT (AppId, Version, PackageType, TargetName, RelativePath, IsEnabled)
    VALUES(source.AppId, source.Version, source.PackageType, source.TargetName, source.RelativePath, source.IsEnabled);

-- Repair runs seed the packaged baseline artifact rows but should never
-- downgrade desired state after newer compatible core artifacts have been
-- imported. Use the latest registered artifacts for template state.
SELECT TOP (1) @HostAgentArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @HostAgentAppId
  AND PackageType = N'host-agent'
  AND TargetName = N'omp-hostagent'
  AND IsEnabled = 1
ORDER BY
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 4)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 3)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 2)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 1)), 0) DESC,
    Version DESC,
    ArtifactId DESC;

SELECT TOP (1) @WorkerManagerArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @WorkerManagerAppId
  AND PackageType = N'service-app'
  AND TargetName = N'omp-workermanager'
  AND IsEnabled = 1
ORDER BY
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 4)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 3)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 2)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 1)), 0) DESC,
    Version DESC,
    ArtifactId DESC;

SELECT TOP (1) @WorkerProcessHostArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @WorkerProcessHostAppId
  AND PackageType = N'worker-host'
  AND TargetName = N'omp-workerprocesshost'
  AND IsEnabled = 1
ORDER BY
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 4)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 3)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 2)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 1)), 0) DESC,
    Version DESC,
    ArtifactId DESC;

MERGE omp.InstanceTemplateModuleInstances AS target
USING
(
    SELECT @DefaultInstanceTemplateId AS InstanceTemplateId,
           @CoreModuleId AS ModuleId,
           N'omp_core' AS ModuleInstanceKey,
           N'OpenModulePlatform Core' AS DisplayName,
           N'Core runtime services and host-local infrastructure.' AS Description,
           0 AS SortOrder,
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

SELECT TOP (1) @CoreTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @DefaultInstanceTemplateId
  AND ModuleInstanceKey = N'omp_core'
ORDER BY InstanceTemplateModuleInstanceId;

SELECT TOP (1) @DefaultInstanceTemplateHostId = InstanceTemplateHostId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateId = @DefaultInstanceTemplateId
  AND HostKey = @DefaultHostKey
ORDER BY InstanceTemplateHostId;

IF @CoreTemplateModuleInstanceId IS NOT NULL
   AND @ServiceHostTemplateId IS NOT NULL
BEGIN
    MERGE omp.InstanceTemplateAppInstances AS target
    USING
    (
        VALUES
            (@CoreTemplateModuleInstanceId, NULL, @ServiceHostTemplateId, @WorkerProcessHostAppId, N'omp_workerprocesshost', N'OMP Worker Process Host', N'Host-local executable used by WorkerManager to run worker plugin artifacts.', NULL, NULL, NULL, NULL, @WorkerProcessHostArtifactId, 1, 20, CONVERT(bit, 1), CONVERT(bit, 1)),
            (@CoreTemplateModuleInstanceId, NULL, @ServiceHostTemplateId, @WorkerManagerAppId, N'omp_workermanager', N'OMP WorkerManager', N'Host-local Windows service that starts and supervises OMP worker plugin processes.', NULL, NULL, N'WorkerManager', N'OMP.WorkerManager', @WorkerManagerArtifactId, 1, 30, CONVERT(bit, 1), CONVERT(bit, 1))
    ) AS source(InstanceTemplateModuleInstanceId, InstanceTemplateHostId, TargetHostTemplateId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, DesiredArtifactId, DesiredState, SortOrder, IsEnabled, IsAllowed)
    ON target.InstanceTemplateModuleInstanceId = source.InstanceTemplateModuleInstanceId
    AND target.AppInstanceKey = source.AppInstanceKey
    WHEN MATCHED THEN
        UPDATE SET InstanceTemplateHostId = source.InstanceTemplateHostId,
                   TargetHostTemplateId = source.TargetHostTemplateId,
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
                   IsAllowed = source.IsAllowed,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(InstanceTemplateModuleInstanceId, InstanceTemplateHostId, TargetHostTemplateId, AppId, AppInstanceKey, DisplayName, Description, RoutePath, PublicUrl, InstallPath, InstallationName, DesiredArtifactId, DesiredState, SortOrder, IsEnabled, IsAllowed)
        VALUES(source.InstanceTemplateModuleInstanceId, source.InstanceTemplateHostId, source.TargetHostTemplateId, source.AppId, source.AppInstanceKey, source.DisplayName, source.Description, source.RoutePath, source.PublicUrl, source.InstallPath, source.InstallationName, source.DesiredArtifactId, source.DesiredState, source.SortOrder, source.IsEnabled, source.IsAllowed);
END

-------------------------------------------------------------------------------
-- Seed baseline administrative placeholder role
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Roles WHERE Name = N'PortalAdmins')
BEGIN
    INSERT INTO omp.Roles(Name, Description) VALUES(N'PortalAdmins', N'Administrative bootstrap role for OMP modules and portal');
END

IF NOT EXISTS (SELECT 1 FROM omp.Roles WHERE Name = N'Everyone')
BEGIN
    INSERT INTO omp.Roles(Name, Description)
    VALUES(N'Everyone', N'Built-in baseline role for permissions available to every signed-in OMP principal.');
END
ELSE
BEGIN
    UPDATE omp.Roles
    SET Description = N'Built-in baseline role for permissions available to every signed-in OMP principal.'
    WHERE Name = N'Everyone'
      AND ISNULL(Description, N'') = N'';
END

IF NOT EXISTS (SELECT 1 FROM omp.Roles WHERE Name = N'AuthenticatedUsers')
BEGIN
    INSERT INTO omp.Roles(Name, Description)
    VALUES(N'AuthenticatedUsers', N'Built-in baseline role for permissions available to authenticated OMP users.');
END
ELSE
BEGIN
    UPDATE omp.Roles
    SET Description = N'Built-in baseline role for permissions available to authenticated OMP users.'
    WHERE Name = N'AuthenticatedUsers'
      AND ISNULL(Description, N'') = N'';
END

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';
SELECT @EveryoneRoleId = RoleId FROM omp.Roles WHERE Name = N'Everyone';
SELECT @AuthenticatedUsersRoleId = RoleId FROM omp.Roles WHERE Name = N'AuthenticatedUsers';

IF @EveryoneRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePrincipals
       WHERE RoleId = @EveryoneRoleId
         AND PrincipalType = N'OMPSystem'
         AND Principal = N'Everyone'
   )
BEGIN
    INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
    VALUES(@EveryoneRoleId, N'OMPSystem', N'Everyone');
END

IF @AuthenticatedUsersRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePrincipals
       WHERE RoleId = @AuthenticatedUsersRoleId
         AND PrincipalType = N'OMPSystem'
         AND Principal = N'AuthenticatedUsers'
   )
BEGIN
    INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
    VALUES(@AuthenticatedUsersRoleId, N'OMPSystem', N'AuthenticatedUsers');
END

-------------------------------------------------------------------------------
-- Seed built-in authentication providers
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.auth_providers WHERE display_name = N'AD')
BEGIN
    INSERT INTO omp.auth_providers(display_name, is_enabled)
    VALUES(N'AD', 1);
END

IF NOT EXISTS (SELECT 1 FROM omp.auth_providers WHERE display_name = N'lpwd')
BEGIN
    INSERT INTO omp.auth_providers(display_name, is_enabled)
    VALUES(N'lpwd', 1);
END

-------------------------------------------------------------------------------
-- Seed baseline instance branding settings
-------------------------------------------------------------------------------
MERGE omp.config_setting_definitions AS target
USING
(
    VALUES
        (N'branding', N'platformName', N'Display name for the installed OpenModulePlatform instance.', 10, CONVERT(bit, 1)),
        (N'branding', N'portalName', N'Display name for the portal concept in this installation.', 20, CONVERT(bit, 1)),
        (N'rbac', N'authenticatedUsersWindowsDomains', N'Comma-, semicolon-, or newline-separated Windows account domain/workgroup/computer prefixes that may receive the built-in AuthenticatedUsers principal. Empty or * accepts any authenticated principal.', 100, CONVERT(bit, 1))
) AS source(ConfigCategory, ConfigSetting, Description, SortOrder, IsEnabled)
ON target.ConfigCategory = source.ConfigCategory
   AND target.ConfigSetting = source.ConfigSetting
WHEN MATCHED THEN
    UPDATE SET Description = source.Description,
               SortOrder = source.SortOrder,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(ConfigCategory, ConfigSetting, Description, SortOrder, IsEnabled)
    VALUES(source.ConfigCategory, source.ConfigSetting, source.Description, source.SortOrder, source.IsEnabled);

-- Insert defaults only. Environment/customer installers may intentionally
-- override these rows (for example VGR uses EMP), so rerunning the generic core
-- initialization must not reset an existing branding choice.
MERGE omp.config_settings AS target
USING
(
    SELECT def.ConfigSettingId,
           seed.ConfigValue,
           seed.ConfigPriority
    FROM
    (
        VALUES
            (N'branding', N'platformName', N'OMP', 0),
            (N'branding', N'portalName', N'Portal', 0),
            (N'rbac', N'authenticatedUsersWindowsDomains', N'', 0)
    ) AS seed(ConfigCategory, ConfigSetting, ConfigValue, ConfigPriority)
    INNER JOIN omp.config_setting_definitions def
        ON def.ConfigCategory = seed.ConfigCategory
       AND def.ConfigSetting = seed.ConfigSetting
) AS source(ConfigSettingId, ConfigValue, ConfigPriority)
ON target.ConfigSettingId = source.ConfigSettingId
   AND target.ConfigUsr IS NULL
   AND target.ConfigPermission IS NULL
   AND target.ConfigRole IS NULL
WHEN NOT MATCHED THEN
    INSERT(ConfigSettingId, ConfigValue, ConfigPriority)
    VALUES(source.ConfigSettingId, source.ConfigValue, source.ConfigPriority);

/*
Bootstrap administrative principal rows.

Set @BootstrapPortalAdminPrincipal before you try to sign in to OMP Portal or
other OMP modules that rely on the shared PortalAdmins bootstrap role.
Examples:
- PrincipalType N'ADUser' with principal DOMAIN\your.user
- PrincipalType N'ADGroup' with principal DOMAIN\OMP Portal Admins

The local installer can add more principals after this script runs. This script
inserts the configured principal if it is missing and intentionally does not
overwrite existing bootstrap principals.

Do not pass the principal through sqlcmd -v. SQLCMD variables are textual
substitution before T-SQL parsing, so values containing SQL metacharacters cannot
be safely validated inside this script after substitution. Use the PowerShell
installer for automated local runs, or manually escape single quotes in the
literal above.
*/
-- Repeat the legacy User -> ADUser migration here instead of relying on a shared
-- stored procedure. The setup script may be run before programmable objects
-- exist, and installers sometimes rerun only this initialization script after an
-- older deployment.
DELETE legacy
FROM omp.RolePrincipals legacy
WHERE legacy.PrincipalType = N'User'
  AND EXISTS
  (
      SELECT 1
      FROM omp.RolePrincipals currentPrincipal
      WHERE currentPrincipal.RoleId = legacy.RoleId
        AND currentPrincipal.PrincipalType = N'ADUser'
        AND currentPrincipal.Principal = legacy.Principal
  );

UPDATE omp.RolePrincipals
SET PrincipalType = N'ADUser'
WHERE PrincipalType = N'User';

IF NOT EXISTS
(
    SELECT 1
    FROM omp.RolePrincipals
    WHERE RoleId = @PortalAdminsRoleId
      AND PrincipalType = @BootstrapPortalAdminPrincipalType
      AND Principal = @BootstrapPortalAdminPrincipal
)
BEGIN
    INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
    VALUES(@PortalAdminsRoleId, @BootstrapPortalAdminPrincipalType, @BootstrapPortalAdminPrincipal);
END
GO
