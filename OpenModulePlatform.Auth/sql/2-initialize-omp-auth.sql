-- File: OpenModulePlatform.Auth/sql/2-initialize-omp-auth.sql
/*
Seeds OMP registration rows for the shared authentication web application.

The Auth app is platform infrastructure rather than a user-facing module menu
entry. HostAgent still needs a normal web-app artifact and app instance so it
can deploy the /auth IIS application in HostAgent-first installations.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
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

DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @AuthModuleId int;
DECLARE @AuthModuleInstanceId uniqueidentifier;
DECLARE @AuthTemplateModuleInstanceId int;
DECLARE @AuthAppId int;
DECLARE @AuthAppInstanceId uniqueidentifier;
DECLARE @AuthArtifactId int;
DECLARE @BaselineArtifactVersion nvarchar(50) = N'0.3.4';

SELECT TOP (1)
       @InstanceId = InstanceId,
       @InstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceKey = N'default'
ORDER BY InstanceId;

IF @InstanceId IS NULL
    THROW 50000, 'Default OMP instance not found. Run the core SQL setup/init scripts first.', 1;

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'omp_auth')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'OMP Auth',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp',
        Description = N'Shared OpenModulePlatform authentication web application',
        IsEnabled = 1,
        SortOrder = 90,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'omp_auth';
END
ELSE
BEGIN
    INSERT INTO omp.Modules(ModuleKey, DisplayName, ModuleType, SchemaName, Description, IsEnabled, SortOrder)
    VALUES(N'omp_auth', N'OMP Auth', N'WebAppModule', N'omp', N'Shared OpenModulePlatform authentication web application', 1, 90);
END

SELECT @AuthModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'omp_auth';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @AuthModuleId AND AppKey = N'omp_auth')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'OMP Auth',
        AppType = N'WebApp',
        Description = N'Authentication web application for OMP cookie sign-in',
        IsEnabled = 1,
        SortOrder = 90,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @AuthModuleId AND AppKey = N'omp_auth';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(@AuthModuleId, N'omp_auth', N'OMP Auth', N'WebApp', N'Authentication web application for OMP cookie sign-in', 1, 90);
END

SELECT @AuthAppId = AppId
FROM omp.Apps
WHERE ModuleId = @AuthModuleId
  AND AppKey = N'omp_auth';

MERGE omp.Artifacts AS target
USING
(
    SELECT @AuthAppId AS AppId,
           @BaselineArtifactVersion AS Version,
           N'web-app' AS PackageType,
           N'omp-auth' AS TargetName,
           N'omp-auth/web/' + @BaselineArtifactVersion AS RelativePath,
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

-- Repair runs seed the packaged baseline artifact row but should never
-- downgrade desired state after a newer compatible Auth artifact has been
-- imported. Use the latest registered Auth artifact for app/template state.
SELECT TOP (1) @AuthArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @AuthAppId
  AND PackageType = N'web-app'
  AND TargetName = N'omp-auth'
  AND IsEnabled = 1
ORDER BY
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 4)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 3)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 2)), 0) DESC,
    COALESCE(TRY_CONVERT(int, PARSENAME(Version, 1)), 0) DESC,
    Version DESC,
    ArtifactId DESC;

SELECT @AuthArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @AuthAppId
  AND Version = @BaselineArtifactVersion
  AND PackageType = N'web-app'
  AND TargetName = N'omp-auth'
  AND @AuthArtifactId IS NULL;

MERGE omp.ArtifactConfigurationFiles AS target
USING
(
    SELECT @AuthArtifactId AS ArtifactId,
           N'appsettings.json' AS RelativePath,
           N'{
  "ConnectionStrings": {
    "OmpDb": "{{Omp.Json.ConnectionStrings.OmpDb}}"
  },
  "OmpAuth": {
    "CookieName": ".OpenModulePlatform.Auth",
    "LoginPath": "/auth/login",
    "LogoutPath": "/auth/logout",
    "AccessDeniedPath": "/status/403",
    "ApplicationName": "OpenModulePlatform",
    "DataProtectionKeyPath": "{{Omp.Json.HostAgent.WebAppDataProtectionKeyPath}}"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}' AS FileContent,
           CAST(1 AS bit) AS IsEnabled
) AS source
ON target.ArtifactId = source.ArtifactId
AND target.RelativePath = source.RelativePath
WHEN MATCHED THEN
    UPDATE SET FileContent = source.FileContent,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (ArtifactId, RelativePath, FileContent, IsEnabled)
    VALUES(source.ArtifactId, source.RelativePath, source.FileContent, source.IsEnabled);

SELECT @AuthModuleInstanceId = ModuleInstanceId
FROM omp.ModuleInstances
WHERE InstanceId = @InstanceId
  AND ModuleInstanceKey = N'omp_auth';

IF @AuthModuleInstanceId IS NULL
BEGIN
    SET @AuthModuleInstanceId = NEWID();

    INSERT INTO omp.ModuleInstances(
        ModuleInstanceId,
        InstanceId,
        ModuleId,
        ModuleInstanceKey,
        DisplayName,
        Description,
        IsEnabled,
        SortOrder)
    VALUES(
        @AuthModuleInstanceId,
        @InstanceId,
        @AuthModuleId,
        N'omp_auth',
        N'OMP Auth',
        N'Authentication app instance for the default OMP instance',
        1,
        90);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @AuthModuleId,
        ModuleInstanceKey = N'omp_auth',
        DisplayName = N'OMP Auth',
        Description = N'Authentication app instance for the default OMP instance',
        IsEnabled = 1,
        SortOrder = 90,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleInstanceId = @AuthModuleInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateModuleInstances
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'omp_auth'
)
BEGIN
    INSERT INTO omp.InstanceTemplateModuleInstances(
        InstanceTemplateId,
        ModuleId,
        ModuleInstanceKey,
        DisplayName,
        Description,
        SortOrder)
    VALUES(
        @InstanceTemplateId,
        @AuthModuleId,
        N'omp_auth',
        N'OMP Auth',
        N'Authentication app instance for the default template',
        90);
END
ELSE
BEGIN
    UPDATE omp.InstanceTemplateModuleInstances
    SET ModuleId = @AuthModuleId,
        DisplayName = N'OMP Auth',
        Description = N'Authentication app instance for the default template',
        SortOrder = 90,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'omp_auth';
END

SELECT @AuthTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'omp_auth';

SELECT @AuthAppInstanceId = AppInstanceId
FROM omp.AppInstances
WHERE ModuleInstanceId = @AuthModuleInstanceId
  AND AppInstanceKey = N'omp_auth';

IF @AuthAppInstanceId IS NULL
BEGIN
    SET @AuthAppInstanceId = NEWID();

    INSERT INTO omp.AppInstances(
        AppInstanceId, ModuleInstanceId, HostId, AppId, AppInstanceKey, DisplayName, Description,
        RoutePath, InstallationName, ArtifactId, IsEnabled, IsAllowed, DesiredState, SortOrder)
    VALUES(
        @AuthAppInstanceId, @AuthModuleInstanceId, NULL, @AuthAppId, N'omp_auth', N'OMP Auth',
        N'Shared authentication web application for the default OMP instance', N'auth', N'auth', @AuthArtifactId, 1, 1, 1, 90);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @AuthModuleInstanceId,
        HostId = NULL,
        AppId = @AuthAppId,
        AppInstanceKey = N'omp_auth',
        DisplayName = N'OMP Auth',
        Description = N'Shared authentication web application for the default OMP instance',
        RoutePath = N'auth',
        InstallationName = N'auth',
        ArtifactId = @AuthArtifactId,
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 90,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @AuthAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @AuthTemplateModuleInstanceId
      AND AppInstanceKey = N'omp_auth'
)
BEGIN
    INSERT INTO omp.InstanceTemplateAppInstances(
        InstanceTemplateModuleInstanceId, InstanceTemplateHostId, AppId, AppInstanceKey, DisplayName, Description,
        RoutePath, InstallationName, DesiredArtifactId, DesiredState, SortOrder)
    VALUES(
        @AuthTemplateModuleInstanceId, NULL, @AuthAppId, N'omp_auth', N'OMP Auth',
        N'Shared authentication web application for the default template', N'auth', N'auth', @AuthArtifactId, 1, 90);
END
ELSE
BEGIN
    UPDATE omp.InstanceTemplateAppInstances
    SET InstanceTemplateHostId = NULL,
        AppId = @AuthAppId,
        DisplayName = N'OMP Auth',
        Description = N'Shared authentication web application for the default template',
        RoutePath = N'auth',
        InstallationName = N'auth',
        DesiredArtifactId = @AuthArtifactId,
        DesiredState = 1,
        SortOrder = 90,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceTemplateModuleInstanceId = @AuthTemplateModuleInstanceId
      AND AppInstanceKey = N'omp_auth';
END

IF OBJECT_ID(N'omp.config_setting_definitions', N'U') IS NOT NULL
   AND OBJECT_ID(N'omp.config_settings', N'U') IS NOT NULL
BEGIN
    MERGE omp.config_setting_definitions AS target
    USING
    (
        VALUES
            (N'auth', N'selfRegistrationEnabled', N'Controls whether users may create their own OMP account from the login page or account settings.', 110, CONVERT(bit, 1))
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

    MERGE omp.config_settings AS target
    USING
    (
        SELECT def.ConfigSettingId,
               defaults.ConfigValue,
               0 AS ConfigPriority
        FROM omp.config_setting_definitions def
        INNER JOIN
        (
            VALUES
                (N'auth', N'selfRegistrationEnabled', N'true')
        ) AS defaults(ConfigCategory, ConfigSetting, ConfigValue)
            ON defaults.ConfigCategory = def.ConfigCategory
           AND defaults.ConfigSetting = def.ConfigSetting
    ) AS source(ConfigSettingId, ConfigValue, ConfigPriority)
    ON target.ConfigSettingId = source.ConfigSettingId
       AND target.ConfigUsr IS NULL
       AND target.ConfigPermission IS NULL
       AND target.ConfigRole IS NULL
    WHEN NOT MATCHED THEN
        INSERT(ConfigSettingId, ConfigValue, ConfigPriority)
        VALUES(source.ConfigSettingId, source.ConfigValue, source.ConfigPriority);
END
GO
