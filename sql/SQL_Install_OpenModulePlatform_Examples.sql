-- File: sql/SQL_Install_OpenModulePlatform_Examples.sql
/*
OpenModulePlatform example install script.

Run this script after SQL_Install_OpenModulePlatform.sql.

This script creates the example module schemas and registers:
- one web-only example module
- one service-backed example module
- module instances and app instances for the default OMP instance
- template topology rows for the default instance template
- a sample service app instance on the sample host
- sample jobs for the service-backed example

Important:
- The sample service app instance is seeded with deliberate placeholder values.
- Replace all REPLACE_ME values in omp.AppInstances before starting the sample service app.
*/
USE [OpenModulePlatform];
GO

-------------------------------------------------------------------------------
-- Example schemas
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_webapp_module')
    EXEC('CREATE SCHEMA [omp_example_webapp_module]');
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_serviceapp_module')
    EXEC('CREATE SCHEMA [omp_example_serviceapp_module]');
GO

-------------------------------------------------------------------------------
-- Example WebAppModule tables
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp_example_webapp_module.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp_module.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL,
        ConfigJson nvarchar(max) NOT NULL,
        Comment nvarchar(400) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleWeb_Config_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_webapp_module.Configurations WHERE VersionNo = 0)
BEGIN
    INSERT INTO omp_example_webapp_module.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"sampleMode": true}', N'Initial example web configuration', N'install-script');
END
GO

-------------------------------------------------------------------------------
-- Example ServiceAppModule tables
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp_example_serviceapp_module.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_serviceapp_module.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL,
        ConfigJson nvarchar(max) NOT NULL,
        Comment nvarchar(400) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleService_Config_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL
    );
END
GO

IF OBJECT_ID(N'omp_example_serviceapp_module.Jobs', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_serviceapp_module.Jobs
    (
        JobId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        RequestType nvarchar(100) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        Status tinyint NOT NULL,
        Attempts int NOT NULL CONSTRAINT DF_ExampleService_Jobs_Attempts DEFAULT(0),
        RequestedUtc datetime2(3) NOT NULL,
        RequestedBy nvarchar(256) NULL,
        ClaimedByAppInstanceId uniqueidentifier NULL,
        ClaimedUtc datetime2(3) NULL,
        CompletedUtc datetime2(3) NULL,
        ResultJson nvarchar(max) NULL,
        LastError nvarchar(max) NULL,
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleService_Jobs_UpdatedUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID(N'omp_example_serviceapp_module.JobExecutions', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_serviceapp_module.JobExecutions
    (
        JobExecutionId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        JobId bigint NOT NULL,
        AppInstanceId uniqueidentifier NOT NULL,
        StartedUtc datetime2(3) NOT NULL,
        FinishedUtc datetime2(3) NULL,
        Outcome nvarchar(50) NOT NULL,
        ResultJson nvarchar(max) NULL,
        ErrorMessage nvarchar(max) NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_serviceapp_module.Configurations WHERE VersionNo = 0)
BEGIN
    INSERT INTO omp_example_serviceapp_module.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"scanBatchSize": 1, "sampleMode": true}', N'Initial example service configuration', N'install-script');
END
GO

-------------------------------------------------------------------------------
-- Registration for example modules, definitions, instances and templates
-------------------------------------------------------------------------------
DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @SampleHostId uniqueidentifier;
DECLARE @SampleTemplateHostId int;
DECLARE @PortalAdminsRoleId int;

DECLARE @WebModuleId int;
DECLARE @WebModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111201';
DECLARE @WebTemplateModuleInstanceId int;
DECLARE @WebAppId int;
DECLARE @WebAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111202';
DECLARE @WebViewPermissionId int;
DECLARE @WebAdminPermissionId int;

DECLARE @ServiceModuleId int;
DECLARE @ServiceModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111301';
DECLARE @ServiceTemplateModuleInstanceId int;
DECLARE @ServiceWebAppId int;
DECLARE @ServiceWebAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111302';
DECLARE @ServiceAppId int;
DECLARE @ServiceAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111303';
DECLARE @ServiceViewPermissionId int;
DECLARE @ServiceAdminPermissionId int;
DECLARE @InitialServiceConfigId int;
DECLARE @ServiceArtifactId int;

SELECT @InstanceId = InstanceId, @InstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceKey = N'default';

IF @InstanceId IS NULL
    THROW 50000, 'Default OMP instance not found. Run SQL_Install_OpenModulePlatform.sql first.', 1;

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';
SELECT @SampleHostId = HostId FROM omp.Hosts WHERE InstanceId = @InstanceId AND HostKey = N'sample-host';
SELECT @SampleTemplateHostId = InstanceTemplateHostId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateId = @InstanceTemplateId
  AND HostKey = N'sample-host';
SELECT TOP (1) @InitialServiceConfigId = ConfigId
FROM omp_example_serviceapp_module.Configurations
WHERE VersionNo = 0
ORDER BY ConfigId DESC;

-------------------------------------------------------------------------------
-- Example WebAppModule registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleWebAppModule.View', N'Read access to the Example WebAppModule');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'ExampleWebAppModule.Admin',
        N'Administrative access to the Example WebAppModule');

SELECT @WebViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.View';
SELECT @WebAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_webapp_module')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example WebAppModule',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_example_webapp_module',
        Description = N'Web-only example module for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 300,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'example_webapp_module';
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
        N'example_webapp_module',
        N'Example WebAppModule',
        N'WebAppModule',
        N'omp_example_webapp_module',
        N'Web-only example module for OpenModulePlatform',
        1,
        300);
END

SELECT @WebModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'example_webapp_module';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @WebModuleId AND AppKey = N'example_webapp_module_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example WebAppModule',
        AppType = N'WebApp',
        Description = N'Web app definition for the web-only example module',
        IsEnabled = 1,
        SortOrder = 300,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @WebModuleId AND AppKey = N'example_webapp_module_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(
        ModuleId,
        AppKey,
        DisplayName,
        AppType,
        Description,
        IsEnabled,
        SortOrder)
    VALUES(
        @WebModuleId,
        N'example_webapp_module_webapp',
        N'Example WebAppModule',
        N'WebApp',
        N'Web app definition for the web-only example module',
        1,
        300);
END

SELECT @WebAppId = AppId FROM omp.Apps WHERE ModuleId = @WebModuleId AND AppKey = N'example_webapp_module_webapp';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @WebAppId AND PermissionId = @WebViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@WebAppId, @WebViewPermissionId, 0);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @WebViewPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @WebViewPermissionId);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @WebAdminPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @WebAdminPermissionId);

IF NOT EXISTS (SELECT 1 FROM omp.ModuleInstances WHERE ModuleInstanceId = @WebModuleInstanceId)
BEGIN
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
        @WebModuleInstanceId,
        @InstanceId,
        @WebModuleId,
        N'example_webapp_module',
        N'Example WebAppModule',
        N'Web-only example module instance',
        1,
        300);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @WebModuleId,
        ModuleInstanceKey = N'example_webapp_module',
        DisplayName = N'Example WebAppModule',
        Description = N'Web-only example module instance',
        IsEnabled = 1,
        SortOrder = 300,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleInstanceId = @WebModuleInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateModuleInstances
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'example_webapp_module'
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
        @WebModuleId,
        N'example_webapp_module',
        N'Example WebAppModule',
        N'Web-only example module instance in the default template',
        300);
END

SELECT @WebTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'example_webapp_module';

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @WebAppInstanceId)
BEGIN
    INSERT INTO omp.AppInstances(
        AppInstanceId,
        ModuleInstanceId,
        HostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        RoutePath,
        InstallationName,
        IsEnabled,
        IsAllowed,
        DesiredState,
        SortOrder)
    VALUES(
        @WebAppInstanceId,
        @WebModuleInstanceId,
        @SampleHostId,
        @WebAppId,
        N'example_webapp_module_webapp',
        N'Example WebAppModule',
        N'Primary web app instance for the example WebAppModule',
        N'ExampleWebAppModule',
        N'webapp',
        1,
        1,
        1,
        300);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @WebModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @WebAppId,
        AppInstanceKey = N'example_webapp_module_webapp',
        DisplayName = N'Example WebAppModule',
        Description = N'Primary web app instance for the example WebAppModule',
        RoutePath = N'ExampleWebAppModule',
        InstallationName = N'webapp',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 300,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @WebAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @WebTemplateModuleInstanceId
      AND AppInstanceKey = N'example_webapp_module_webapp'
)
BEGIN
    INSERT INTO omp.InstanceTemplateAppInstances(
        InstanceTemplateModuleInstanceId,
        InstanceTemplateHostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        RoutePath,
        InstallationName,
        DesiredState,
        SortOrder)
    VALUES(
        @WebTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @WebAppId,
        N'example_webapp_module_webapp',
        N'Example WebAppModule',
        N'Primary web app instance for the example WebAppModule',
        N'ExampleWebAppModule',
        N'webapp',
        1,
        300);
END

-------------------------------------------------------------------------------
-- Example ServiceAppModule registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleServiceAppModule.View', N'Read access to the Example ServiceAppModule');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'ExampleServiceAppModule.Admin',
        N'Administrative access to the Example ServiceAppModule');

SELECT @ServiceViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View';
SELECT @ServiceAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_serviceapp_module')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example ServiceAppModule',
        ModuleType = N'HostAppModule',
        SchemaName = N'omp_example_serviceapp_module',
        Description = N'Combined web app and service app example module for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'example_serviceapp_module';
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
        N'example_serviceapp_module',
        N'Example ServiceAppModule',
        N'HostAppModule',
        N'omp_example_serviceapp_module',
        N'Combined web app and service app example module for OpenModulePlatform',
        1,
        400);
END

SELECT @ServiceModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'example_serviceapp_module';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example ServiceAppModule',
        AppType = N'WebApp',
        Description = N'Web app definition for the example HostAppModule',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @ServiceModuleId,
        N'example_serviceapp_module_webapp',
        N'Example ServiceAppModule',
        N'WebApp',
        N'Web app definition for the example HostAppModule',
        1,
        400);
END

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_service')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example Service Worker',
        AppType = N'ServiceApp',
        Description = N'Service app definition for the example HostAppModule',
        IsEnabled = 1,
        SortOrder = 401,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_service';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @ServiceModuleId,
        N'example_serviceapp_module_service',
        N'Example Service Worker',
        N'ServiceApp',
        N'Service app definition for the example HostAppModule',
        1,
        401);
END

SELECT @ServiceWebAppId = AppId FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_webapp';
SELECT @ServiceAppId = AppId FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_service';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @ServiceWebAppId AND PermissionId = @ServiceViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@ServiceWebAppId, @ServiceViewPermissionId, 0);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @ServiceViewPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @ServiceViewPermissionId);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @ServiceAdminPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @ServiceAdminPermissionId);

IF NOT EXISTS
(
    SELECT 1
    FROM omp.Artifacts
    WHERE AppId = @ServiceAppId
      AND Version = N'1.0.0'
      AND PackageType = N'folder'
      AND TargetName = N'win-x64'
)
    INSERT INTO omp.Artifacts(AppId, Version, PackageType, TargetName, RelativePath, IsEnabled)
    VALUES(@ServiceAppId, N'1.0.0', N'folder', N'win-x64', N'publish/ExampleServiceAppModule', 1);

SELECT TOP (1) @ServiceArtifactId = ArtifactId FROM omp.Artifacts WHERE AppId = @ServiceAppId ORDER BY ArtifactId DESC;

IF NOT EXISTS (SELECT 1 FROM omp.ModuleInstances WHERE ModuleInstanceId = @ServiceModuleInstanceId)
BEGIN
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
        @ServiceModuleInstanceId,
        @InstanceId,
        @ServiceModuleId,
        N'example_serviceapp_module',
        N'Example ServiceAppModule',
        N'Example module instance with both web and service apps',
        1,
        400);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @ServiceModuleId,
        ModuleInstanceKey = N'example_serviceapp_module',
        DisplayName = N'Example ServiceAppModule',
        Description = N'Example module instance with both web and service apps',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleInstanceId = @ServiceModuleInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateModuleInstances
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'example_serviceapp_module'
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
        @ServiceModuleId,
        N'example_serviceapp_module',
        N'Example ServiceAppModule',
        N'Example module instance with both web and service apps',
        400);
END

SELECT @ServiceTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'example_serviceapp_module';

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @ServiceWebAppInstanceId)
BEGIN
    INSERT INTO omp.AppInstances(
        AppInstanceId,
        ModuleInstanceId,
        HostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        RoutePath,
        InstallationName,
        IsEnabled,
        IsAllowed,
        DesiredState,
        SortOrder)
    VALUES(
        @ServiceWebAppInstanceId,
        @ServiceModuleInstanceId,
        @SampleHostId,
        @ServiceWebAppId,
        N'example_serviceapp_module_webapp',
        N'Example ServiceAppModule',
        N'Primary web app instance for the example HostAppModule',
        N'ExampleServiceAppModule',
        N'webapp',
        1,
        1,
        1,
        400);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @ServiceModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @ServiceWebAppId,
        AppInstanceKey = N'example_serviceapp_module_webapp',
        DisplayName = N'Example ServiceAppModule',
        Description = N'Primary web app instance for the example HostAppModule',
        RoutePath = N'ExampleServiceAppModule',
        InstallationName = N'webapp',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @ServiceWebAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @ServiceTemplateModuleInstanceId
      AND AppInstanceKey = N'example_serviceapp_module_webapp'
)
BEGIN
    INSERT INTO omp.InstanceTemplateAppInstances(
        InstanceTemplateModuleInstanceId,
        InstanceTemplateHostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        RoutePath,
        InstallationName,
        DesiredState,
        SortOrder)
    VALUES(
        @ServiceTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @ServiceWebAppId,
        N'example_serviceapp_module_webapp',
        N'Example ServiceAppModule',
        N'Primary web app instance for the example HostAppModule',
        N'ExampleServiceAppModule',
        N'webapp',
        1,
        400);
END

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @ServiceAppInstanceId)
BEGIN
    INSERT INTO omp.AppInstances(
        AppInstanceId,
        ModuleInstanceId,
        HostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        InstallPath,
        InstallationName,
        ArtifactId,
        ConfigId,
        ExpectedLogin,
        ExpectedClientHostName,
        ExpectedClientIp,
        IsEnabled,
        IsAllowed,
        DesiredState,
        SortOrder)
    VALUES(
        @ServiceAppInstanceId,
        @ServiceModuleInstanceId,
        @SampleHostId,
        @ServiceAppId,
        N'example_serviceapp_module_service',
        N'Example Service Worker',
        N'Primary service app instance for the example HostAppModule',
        N'C:\Program Files\OpenModulePlatform\ServiceApps\ExampleServiceAppModule',
        N'default',
        @ServiceArtifactId,
        @InitialServiceConfigId,
        N'REPLACE_ME\\service-account',
        N'REPLACE_ME_HOST',
        N'REPLACE_ME_IP',
        1,
        1,
        1,
        401);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @ServiceModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @ServiceAppId,
        AppInstanceKey = N'example_serviceapp_module_service',
        DisplayName = N'Example Service Worker',
        Description = N'Primary service app instance for the example HostAppModule',
        InstallPath = N'C:\Program Files\OpenModulePlatform\ServiceApps\ExampleServiceAppModule',
        InstallationName = N'default',
        ArtifactId = @ServiceArtifactId,
        ConfigId = @InitialServiceConfigId,
        ExpectedLogin = N'REPLACE_ME\\service-account',
        ExpectedClientHostName = N'REPLACE_ME_HOST',
        ExpectedClientIp = N'REPLACE_ME_IP',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 401,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @ServiceAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @ServiceTemplateModuleInstanceId
      AND AppInstanceKey = N'example_serviceapp_module_service'
)
BEGIN
    INSERT INTO omp.InstanceTemplateAppInstances(
        InstanceTemplateModuleInstanceId,
        InstanceTemplateHostId,
        AppId,
        AppInstanceKey,
        DisplayName,
        Description,
        InstallPath,
        InstallationName,
        DesiredArtifactId,
        DesiredConfigId,
        ExpectedLogin,
        ExpectedClientHostName,
        ExpectedClientIp,
        DesiredState,
        SortOrder)
    VALUES(
        @ServiceTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @ServiceAppId,
        N'example_serviceapp_module_service',
        N'Example Service Worker',
        N'Primary service app instance for the example HostAppModule',
        N'C:\Program Files\OpenModulePlatform\ServiceApps\ExampleServiceAppModule',
        N'default',
        @ServiceArtifactId,
        @InitialServiceConfigId,
        N'REPLACE_ME\\service-account',
        N'REPLACE_ME_HOST',
        N'REPLACE_ME_IP',
        1,
        401);
END

-------------------------------------------------------------------------------
-- Seed sample jobs for the service-backed example
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp_example_serviceapp_module.Jobs)
BEGIN
    INSERT INTO omp_example_serviceapp_module.Jobs(RequestType, PayloadJson, Status, RequestedUtc, RequestedBy)
    VALUES
        (N'sample.run', N'{"message":"hello from OMP"}', 0, SYSUTCDATETIME(), N'install-script'),
        (N'sample.run', N'{"message":"second sample job"}', 0, SYSUTCDATETIME(), N'install-script');
END
GO
