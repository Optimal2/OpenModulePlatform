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
        RoutePath, InstallationName, IsEnabled, IsAllowed, DesiredState, SortOrder)
    VALUES(
        @AuthAppInstanceId, @AuthModuleInstanceId, NULL, @AuthAppId, N'omp_auth', N'OMP Auth',
        N'Shared authentication web application for the default OMP instance', N'auth', N'auth', 1, 1, 1, 90);
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
        RoutePath, InstallationName, DesiredState, SortOrder)
    VALUES(
        @AuthTemplateModuleInstanceId, NULL, @AuthAppId, N'omp_auth', N'OMP Auth',
        N'Shared authentication web application for the default template', N'auth', N'auth', 1, 90);
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
            (N'auth', N'providerSessionLifetimes', N'Global JSON object that maps auth provider ids to OMP session lifetime minutes. Provider id 0 is the fallback for providers without an override, for example {"0":600,"2":120}. Missing, empty, or invalid values use the built-in 600-minute default. Changes apply only to new sign-ins.', N'^\s*\{[\s\S]*\}\s*$', N'{"0":600}; {"0":600,"2":120}', 100, CONVERT(bit, 1)),
            (N'auth', N'selfRegistrationEnabled', N'Controls whether users may create their own OMP account from the login page or account settings.', N'(?i)^(true|false)$', N'true; false', 110, CONVERT(bit, 1)),
            (N'auth', N'sessionRevocationCacheSeconds', N'How many seconds a verified session account state (account status and security stamp) may be cached per user and application before it is read again. The window bounds how quickly a disabled account or changed password ends an active session. 0 checks every request; values above 300 are clamped to 300. Missing, empty, or invalid values use the built-in 60-second default.', N'^\d{1,3}$', N'60; 0; 300', 120, CONVERT(bit, 1)),
            (N'auth', N'sessionRevocationFailureMode', N'Controls what happens to an active session when the account state cannot be verified, for example while the database is unavailable. strict rejects the session (the user signs in again once the state can be read); lenient keeps the session until the next check. Missing, empty, or invalid values use strict.', N'(?i)^(strict|lenient)$', N'strict; lenient', 130, CONVERT(bit, 1))
    ) AS source(ConfigCategory, ConfigSetting, Description, ValidationRegex, ExampleValues, SortOrder, IsEnabled)
    ON target.ConfigCategory = source.ConfigCategory
       AND target.ConfigSetting = source.ConfigSetting
    WHEN MATCHED THEN
        UPDATE SET Description = source.Description,
                   ValidationRegex = source.ValidationRegex,
                   ExampleValues = source.ExampleValues,
                   SortOrder = source.SortOrder,
                   IsEnabled = source.IsEnabled,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(ConfigCategory, ConfigSetting, Description, ValidationRegex, ExampleValues, SortOrder, IsEnabled)
        VALUES(source.ConfigCategory, source.ConfigSetting, source.Description, source.ValidationRegex, source.ExampleValues, source.SortOrder, source.IsEnabled);

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
                (N'auth', N'providerSessionLifetimes', N'{"0":600}'),
                (N'auth', N'selfRegistrationEnabled', N'false'),
                (N'auth', N'sessionRevocationCacheSeconds', N'60'),
                (N'auth', N'sessionRevocationFailureMode', N'strict')
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
