-- File: sql/SQL_Install_OpenModulePlatform_Examples.sql
/*
OpenModulePlatform example install script.

Repository release line: 0.1.0

Run this script after SQL_Install_OpenModulePlatform.sql.

This script creates the example module schemas and registers:
- two web-only example modules (Razor Pages and Blazor Server)
- one classic service-backed example module
- one manager-driven worker example module
- module instances and app instances for the default OMP instance
- template topology rows for the default instance template
- sample worker-capable app instances on the sample host
- sample jobs for the worker-backed examples

Important:
- These example rows are intended for the first public beta release and local evaluation scenarios.
- The classic service-backed example still uses deliberate identity placeholders to demonstrate the legacy service model.
- The manager-driven worker example is seeded for the additive worker runtime track and uses a generic AppWorkerDefinitions mapping.
- Replace placeholder install paths and artifacts with real published outputs before running the examples.
*/
USE [OpenModulePlatform];
GO

-------------------------------------------------------------------------------
-- Example schemas
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_webapp_module')
    EXEC('CREATE SCHEMA [omp_example_webapp_module]');
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_webapp_blazor_module')
    EXEC('CREATE SCHEMA [omp_example_webapp_blazor_module]');
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_serviceapp_module')
    EXEC('CREATE SCHEMA [omp_example_serviceapp_module]');
GO


IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_workerapp_module')
    EXEC('CREATE SCHEMA [omp_example_workerapp_module]');
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
-- Example WebAppBlazorModule tables
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp_example_webapp_blazor_module.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp_blazor_module.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL,
        ConfigJson nvarchar(max) NOT NULL,
        Comment nvarchar(400) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleWebBlazor_Config_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_webapp_blazor_module.Configurations WHERE VersionNo = 0)
BEGIN
    INSERT INTO omp_example_webapp_blazor_module.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"sampleMode": true, "ui": "blazor"}', N'Initial example blazor web configuration', N'install-script');
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

IF NOT EXISTS (SELECT 1 FROM omp_iframe_module.urls WHERE [id] = 1)
BEGIN
    INSERT INTO omp_iframe_module.urls([id], [url], [displayname], [allowed_roles], [enabled])
    VALUES(1, N'/', N'Portal', NULL, 1);
END
ELSE
BEGIN
    UPDATE omp_iframe_module.urls
    SET [url] = N'/',
        [displayname] = N'Portal',
        [allowed_roles] = NULL,
        [enabled] = 1
    WHERE [id] = 1;
END
GO

IF NOT EXISTS (SELECT 1 FROM omp_iframe_module.urls WHERE [id] = 2)
BEGIN
    INSERT INTO omp_iframe_module.urls([id], [url], [displayname], [allowed_roles], [enabled])
    VALUES(2, N'/ExampleWebAppModule', N'Example Web App Module', NULL, 1);
END
ELSE
BEGIN
    UPDATE omp_iframe_module.urls
    SET [url] = N'/ExampleWebAppModule',
        [displayname] = N'Example Web App Module',
        [allowed_roles] = NULL,
        [enabled] = 1
    WHERE [id] = 2;
END
GO

IF NOT EXISTS (SELECT 1 FROM omp_iframe_module.urls WHERE [id] = 3)
BEGIN
    INSERT INTO omp_iframe_module.urls([id], [url], [displayname], [allowed_roles], [enabled])
    VALUES(3, N'/ExampleServiceAppModule', N'Example Service App Module', NULL, 1);
END
ELSE
BEGIN
    UPDATE omp_iframe_module.urls
    SET [url] = N'/ExampleServiceAppModule',
        [displayname] = N'Example Service App Module',
        [allowed_roles] = NULL,
        [enabled] = 1
    WHERE [id] = 3;
END
GO

DECLARE @IFrameDefaultUrlSetId int;
DECLARE @IFramePortalUrlSetId int;
DECLARE @IFrameExamplesUrlSetId int;

IF EXISTS (SELECT 1 FROM omp_iframe_module.url_sets WHERE [set_key] = N'default')
BEGIN
    UPDATE omp_iframe_module.url_sets
    SET [displayname] = N'Default',
        [enabled] = 1
    WHERE [set_key] = N'default';
END
ELSE
BEGIN
    INSERT INTO omp_iframe_module.url_sets([set_key], [displayname], [enabled])
    VALUES(N'default', N'Default', 1);
END

SELECT @IFrameDefaultUrlSetId = [id]
FROM omp_iframe_module.url_sets
WHERE [set_key] = N'default';

IF EXISTS (SELECT 1 FROM omp_iframe_module.url_sets WHERE [set_key] = N'portal')
BEGIN
    UPDATE omp_iframe_module.url_sets
    SET [displayname] = N'Portal',
        [enabled] = 1
    WHERE [set_key] = N'portal';
END
ELSE
BEGIN
    INSERT INTO omp_iframe_module.url_sets([set_key], [displayname], [enabled])
    VALUES(N'portal', N'Portal', 1);
END

SELECT @IFramePortalUrlSetId = [id]
FROM omp_iframe_module.url_sets
WHERE [set_key] = N'portal';

IF EXISTS (SELECT 1 FROM omp_iframe_module.url_sets WHERE [set_key] = N'examples')
BEGIN
    UPDATE omp_iframe_module.url_sets
    SET [displayname] = N'Examples',
        [enabled] = 1
    WHERE [set_key] = N'examples';
END
ELSE
BEGIN
    INSERT INTO omp_iframe_module.url_sets([set_key], [displayname], [enabled])
    VALUES(N'examples', N'Examples', 1);
END

SELECT @IFrameExamplesUrlSetId = [id]
FROM omp_iframe_module.url_sets
WHERE [set_key] = N'examples';

DELETE FROM omp_iframe_module.url_set_urls
WHERE [url_set_id] IN (@IFrameDefaultUrlSetId, @IFramePortalUrlSetId, @IFrameExamplesUrlSetId);

INSERT INTO omp_iframe_module.url_set_urls([url_set_id], [url_id], [sort_order])
VALUES
    (@IFrameDefaultUrlSetId, 1, 10),
    (@IFrameDefaultUrlSetId, 2, 20),
    (@IFrameDefaultUrlSetId, 3, 30),
    (@IFramePortalUrlSetId, 1, 10),
    (@IFrameExamplesUrlSetId, 2, 10),
    (@IFrameExamplesUrlSetId, 3, 20);
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_serviceapp_module.Configurations WHERE VersionNo = 0)
BEGIN
    /*
    scanBatchSize is intentionally seeded as 1 for the canonical example dataset.
    This keeps the sample worker behavior deterministic and easy to observe during local evaluation,
    screenshots, demos and troubleshooting. It is not a production recommendation.
    Increase this value in the service configuration for real environments after validation.
    */
    INSERT INTO omp_example_serviceapp_module.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"scanBatchSize": 1, "sampleMode": true}', N'Initial example service configuration', N'install-script');
END
GO


-------------------------------------------------------------------------------
-- Example WorkerAppModule tables
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp_example_workerapp_module.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_workerapp_module.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL,
        ConfigJson nvarchar(max) NOT NULL,
        Comment nvarchar(400) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleWorker_Config_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NULL
    );
END
GO

IF OBJECT_ID(N'omp_example_workerapp_module.Jobs', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_workerapp_module.Jobs
    (
        JobId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        RequestType nvarchar(100) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        Status tinyint NOT NULL,
        Attempts int NOT NULL CONSTRAINT DF_ExampleWorker_Jobs_Attempts DEFAULT(0),
        RequestedUtc datetime2(3) NOT NULL,
        RequestedBy nvarchar(256) NULL,
        ClaimedByAppInstanceId uniqueidentifier NULL,
        ClaimedUtc datetime2(3) NULL,
        CompletedUtc datetime2(3) NULL,
        ResultJson nvarchar(max) NULL,
        LastError nvarchar(max) NULL,
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ExampleWorker_Jobs_UpdatedUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID(N'omp_example_workerapp_module.JobExecutions', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_workerapp_module.JobExecutions
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

IF NOT EXISTS (SELECT 1 FROM omp_example_workerapp_module.Configurations WHERE VersionNo = 0)
BEGIN
    INSERT INTO omp_example_workerapp_module.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"scanBatchSize": 1, "sampleMode": true, "runtime": "worker-manager"}', N'Initial example worker configuration', N'install-script');
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

/*
Deterministic example GUID strategy:
- These GUID values are intentionally hard-coded so the example dataset is idempotent across repeated installs.
- The reserved 11111111-1111-1111-1111-1111111112xx/3xx ranges make the example rows easy to recognise in docs, support, and local debugging.
- Do not re-use this reserved range for additional custom example data. If you add more examples, allocate a new documented range or use NEWID() for non-idempotent test data.
- The example install script is designed to seed one canonical sample dataset, not to merge multiple independently-authored example datasets into the same instance.
*/
DECLARE @WebModuleId int;
DECLARE @WebModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111201';
DECLARE @WebTemplateModuleInstanceId int;
DECLARE @WebAppId int;
DECLARE @WebAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111202';
DECLARE @WebViewPermissionId int;
DECLARE @WebAdminPermissionId int;

DECLARE @WebBlazorModuleId int;
DECLARE @WebBlazorModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111211';
DECLARE @WebBlazorTemplateModuleInstanceId int;
DECLARE @WebBlazorAppId int;
DECLARE @WebBlazorAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111212';
DECLARE @WebBlazorViewPermissionId int;
DECLARE @WebBlazorAdminPermissionId int;

DECLARE @IFrameModuleId int;
DECLARE @IFrameModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111221';
DECLARE @IFrameTemplateModuleInstanceId int;
DECLARE @IFrameAppId int;
DECLARE @IFrameAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111222';
DECLARE @IFrameViewPermissionId int;
DECLARE @IFrameAdminPermissionId int;

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


DECLARE @WorkerModuleId int;
DECLARE @WorkerModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111321';
DECLARE @WorkerTemplateModuleInstanceId int;
DECLARE @WorkerWebAppId int;
DECLARE @WorkerWebAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111322';
DECLARE @WorkerAppId int;
DECLARE @WorkerAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111323';
DECLARE @WorkerViewPermissionId int;
DECLARE @WorkerAdminPermissionId int;
DECLARE @InitialWorkerConfigId int;
DECLARE @WorkerArtifactId int;

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

SELECT TOP (1) @InitialWorkerConfigId = ConfigId
FROM omp_example_workerapp_module.Configurations
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
-- Example WebAppBlazorModule registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppBlazorModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleWebAppBlazorModule.View', N'Read access to the Example WebApp Blazor Module');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppBlazorModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'ExampleWebAppBlazorModule.Admin',
        N'Administrative access to the Example WebApp Blazor Module');

SELECT @WebBlazorViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppBlazorModule.View';
SELECT @WebBlazorAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppBlazorModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_webapp_blazor_module')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example WebApp Blazor Module',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_example_webapp_blazor_module',
        Description = N'Blazor Server web-only example module for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'example_webapp_blazor_module';
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
        N'example_webapp_blazor_module',
        N'Example WebApp Blazor Module',
        N'WebAppModule',
        N'omp_example_webapp_blazor_module',
        N'Blazor Server web-only example module for OpenModulePlatform',
        1,
        310);
END

SELECT @WebBlazorModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'example_webapp_blazor_module';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @WebBlazorModuleId AND AppKey = N'example_webapp_blazor_module_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example WebApp Blazor Module',
        AppType = N'WebApp',
        Description = N'Web app definition for the Blazor web-only example module',
        IsEnabled = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @WebBlazorModuleId AND AppKey = N'example_webapp_blazor_module_webapp';
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
        @WebBlazorModuleId,
        N'example_webapp_blazor_module_webapp',
        N'Example WebApp Blazor Module',
        N'WebApp',
        N'Web app definition for the Blazor web-only example module',
        1,
        310);
END

SELECT @WebBlazorAppId = AppId FROM omp.Apps WHERE ModuleId = @WebBlazorModuleId AND AppKey = N'example_webapp_blazor_module_webapp';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @WebBlazorAppId AND PermissionId = @WebBlazorViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@WebBlazorAppId, @WebBlazorViewPermissionId, 0);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @WebBlazorViewPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @WebBlazorViewPermissionId);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @WebBlazorAdminPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @WebBlazorAdminPermissionId);

IF NOT EXISTS (SELECT 1 FROM omp.ModuleInstances WHERE ModuleInstanceId = @WebBlazorModuleInstanceId)
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
        @WebBlazorModuleInstanceId,
        @InstanceId,
        @WebBlazorModuleId,
        N'example_webapp_blazor_module',
        N'Example WebApp Blazor Module',
        N'Blazor Server web-only example module instance',
        1,
        310);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @WebBlazorModuleId,
        ModuleInstanceKey = N'example_webapp_blazor_module',
        DisplayName = N'Example WebApp Blazor Module',
        Description = N'Blazor Server web-only example module instance',
        IsEnabled = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleInstanceId = @WebBlazorModuleInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateModuleInstances
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'example_webapp_blazor_module'
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
        @WebBlazorModuleId,
        N'example_webapp_blazor_module',
        N'Example WebApp Blazor Module',
        N'Blazor Server web-only example module instance in the default template',
        310);
END

SELECT @WebBlazorTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'example_webapp_blazor_module';

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @WebBlazorAppInstanceId)
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
        @WebBlazorAppInstanceId,
        @WebBlazorModuleInstanceId,
        @SampleHostId,
        @WebBlazorAppId,
        N'example_webapp_blazor_module_webapp',
        N'Example WebApp Blazor Module',
        N'Primary web app instance for the Blazor example WebAppModule',
        N'ExampleWebAppBlazorModule',
        N'webapp',
        1,
        1,
        1,
        310);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @WebBlazorModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @WebBlazorAppId,
        AppInstanceKey = N'example_webapp_blazor_module_webapp',
        DisplayName = N'Example WebApp Blazor Module',
        Description = N'Primary web app instance for the Blazor example WebAppModule',
        RoutePath = N'ExampleWebAppBlazorModule',
        InstallationName = N'webapp',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @WebBlazorAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @WebBlazorTemplateModuleInstanceId
      AND AppInstanceKey = N'example_webapp_blazor_module_webapp'
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
        @WebBlazorTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @WebBlazorAppId,
        N'example_webapp_blazor_module_webapp',
        N'Example WebApp Blazor Module',
        N'Primary web app instance for the Blazor example WebAppModule',
        N'ExampleWebAppBlazorModule',
        N'webapp',
        1,
        310);
END

-------------------------------------------------------------------------------
-- iFrame Web App Module registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'IFrameWebAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'IFrameWebAppModule.View', N'Read access to the iFrame Web App Module');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'IFrameWebAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'IFrameWebAppModule.Admin',
        N'Administrative access to the iFrame Web App Module');

SELECT @IFrameViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'IFrameWebAppModule.View';
SELECT @IFrameAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'IFrameWebAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'iframe_webapp_module')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'iFrame Web App Module',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_iframe_module',
        Description = N'Razor wrapper module that renders three database-backed iframe targets inside an OMP shell',
        IsEnabled = 1,
        SortOrder = 320,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'iframe_webapp_module';
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
        N'iframe_webapp_module',
        N'iFrame Web App Module',
        N'WebAppModule',
        N'omp_iframe_module',
        N'Razor wrapper module that renders three database-backed iframe targets inside an OMP shell',
        1,
        320);
END

SELECT @IFrameModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'iframe_webapp_module';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @IFrameModuleId AND AppKey = N'iframe_webapp_module_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'iFrame Web App Module',
        AppType = N'WebApp',
        Description = N'Web app definition for the iFrame proof-of-concept wrapper module',
        IsEnabled = 1,
        SortOrder = 320,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @IFrameModuleId AND AppKey = N'iframe_webapp_module_webapp';
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
        @IFrameModuleId,
        N'iframe_webapp_module_webapp',
        N'iFrame Web App Module',
        N'WebApp',
        N'Web app definition for the iFrame proof-of-concept wrapper module',
        1,
        320);
END

SELECT @IFrameAppId = AppId FROM omp.Apps WHERE ModuleId = @IFrameModuleId AND AppKey = N'iframe_webapp_module_webapp';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @IFrameAppId AND PermissionId = @IFrameViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@IFrameAppId, @IFrameViewPermissionId, 0);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @IFrameViewPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @IFrameViewPermissionId);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @IFrameAdminPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @IFrameAdminPermissionId);

IF NOT EXISTS (SELECT 1 FROM omp.ModuleInstances WHERE ModuleInstanceId = @IFrameModuleInstanceId)
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
        @IFrameModuleInstanceId,
        @InstanceId,
        @IFrameModuleId,
        N'iframe_webapp_module',
        N'iFrame Web App Module',
        N'Proof-of-concept iframe wrapper module instance',
        1,
        320);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @IFrameModuleId,
        ModuleInstanceKey = N'iframe_webapp_module',
        DisplayName = N'iFrame Web App Module',
        Description = N'Proof-of-concept iframe wrapper module instance',
        IsEnabled = 1,
        SortOrder = 320,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleInstanceId = @IFrameModuleInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateModuleInstances
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'iframe_webapp_module'
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
        @IFrameModuleId,
        N'iframe_webapp_module',
        N'iFrame Web App Module',
        N'Proof-of-concept iframe wrapper module instance in the default template',
        320);
END

SELECT @IFrameTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'iframe_webapp_module';

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @IFrameAppInstanceId)
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
        @IFrameAppInstanceId,
        @IFrameModuleInstanceId,
        @SampleHostId,
        @IFrameAppId,
        N'iframe_webapp_module_webapp',
        N'iFrame Web App Module',
        N'Primary web app instance for the iFrame proof-of-concept module',
        N'iFrameWebAppModule',
        N'iframe-webapp',
        1,
        1,
        1,
        320);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @IFrameModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @IFrameAppId,
        AppInstanceKey = N'iframe_webapp_module_webapp',
        DisplayName = N'iFrame Web App Module',
        Description = N'Primary web app instance for the iFrame proof-of-concept module',
        RoutePath = N'iFrameWebAppModule',
        InstallationName = N'iframe-webapp',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 320,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @IFrameAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @IFrameTemplateModuleInstanceId
      AND AppInstanceKey = N'iframe_webapp_module_webapp'
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
        @IFrameTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @IFrameAppId,
        N'iframe_webapp_module_webapp',
        N'iFrame Web App Module',
        N'Primary web app instance for the iFrame proof-of-concept module',
        N'iFrameWebAppModule',
        N'iframe-webapp',
        1,
        320);
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

/*
Resolve the artifact explicitly instead of assuming that the highest ArtifactId is the correct sample artifact.
The example install script seeds a canonical win-x64 folder artifact for version 1.0.0 and should keep resolving that exact row on repeated installs.
*/
SELECT @ServiceArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @ServiceAppId
  AND Version = N'1.0.0'
  AND PackageType = N'folder'
  AND TargetName = N'win-x64';

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
        /*
        Example identity placeholders below are descriptive on purpose so the expected format is obvious.
        Replace them with the real service account, host name and client IP before starting the worker.
        */
        N'DOMAIN\\ServiceAccountName',
        N'hostname.example.com',
        N'192.168.1.100',
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
        ExpectedLogin = N'DOMAIN\\ServiceAccountName',
        ExpectedClientHostName = N'hostname.example.com',
        ExpectedClientIp = N'192.168.1.100',
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
        N'DOMAIN\\ServiceAccountName',
        N'hostname.example.com',
        N'192.168.1.100',
        1,
        401);
END


-------------------------------------------------------------------------------
-- Example WorkerAppModule registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWorkerAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleWorkerAppModule.View', N'Read access to the Example WorkerAppModule');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWorkerAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'ExampleWorkerAppModule.Admin',
        N'Administrative access to the Example WorkerAppModule');

SELECT @WorkerViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWorkerAppModule.View';
SELECT @WorkerAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWorkerAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_workerapp_module')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example WorkerAppModule',
        ModuleType = N'HostAppModule',
        SchemaName = N'omp_example_workerapp_module',
        Description = N'Combined web app and manager-driven worker example module for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 410,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'example_workerapp_module';
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
        N'example_workerapp_module',
        N'Example WorkerAppModule',
        N'HostAppModule',
        N'omp_example_workerapp_module',
        N'Combined web app and manager-driven worker example module for OpenModulePlatform',
        1,
        410);
END

SELECT @WorkerModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'example_workerapp_module';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_module_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example WorkerAppModule',
        AppType = N'WebApp',
        Description = N'Web app definition for the example manager-driven worker module',
        IsEnabled = 1,
        SortOrder = 410,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_module_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @WorkerModuleId,
        N'example_workerapp_module_webapp',
        N'Example WorkerAppModule',
        N'WebApp',
        N'Web app definition for the example manager-driven worker module',
        1,
        410);
END

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_module_worker')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example Managed Worker',
        AppType = N'ServiceApp',
        Description = N'Manager-driven worker app definition for the example worker module',
        IsEnabled = 1,
        SortOrder = 411,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_module_worker';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @WorkerModuleId,
        N'example_workerapp_module_worker',
        N'Example Managed Worker',
        N'ServiceApp',
        N'Manager-driven worker app definition for the example worker module',
        1,
        411);
END

SELECT @WorkerWebAppId = AppId FROM omp.Apps WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_module_webapp';
SELECT @WorkerAppId = AppId FROM omp.Apps WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_module_worker';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @WorkerWebAppId AND PermissionId = @WorkerViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@WorkerWebAppId, @WorkerViewPermissionId, 0);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @WorkerViewPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @WorkerViewPermissionId);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @WorkerAdminPermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @WorkerAdminPermissionId);

IF NOT EXISTS
(
    SELECT 1
    FROM omp.Artifacts
    WHERE AppId = @WorkerAppId
      AND Version = N'1.0.0'
      AND PackageType = N'folder'
      AND TargetName = N'win-x64'
)
    INSERT INTO omp.Artifacts(AppId, Version, PackageType, TargetName, RelativePath, IsEnabled)
    VALUES(@WorkerAppId, N'1.0.0', N'folder', N'win-x64', N'publish/ExampleWorkerAppModule', 1);

SELECT @WorkerArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @WorkerAppId
  AND Version = N'1.0.0'
  AND PackageType = N'folder'
  AND TargetName = N'win-x64';

IF EXISTS (SELECT 1 FROM omp.AppWorkerDefinitions WHERE AppId = @WorkerAppId)
BEGIN
    UPDATE omp.AppWorkerDefinitions
    SET RuntimeKind = N'windows-worker-plugin',
        WorkerTypeKey = N'omp.example.workerapp_module',
        PluginRelativePath = N'OpenModulePlatform.Worker.ExampleWorkerAppModule.dll',
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppId = @WorkerAppId;
END
ELSE
BEGIN
    INSERT INTO omp.AppWorkerDefinitions(AppId, RuntimeKind, WorkerTypeKey, PluginRelativePath, IsEnabled)
    VALUES(
        @WorkerAppId,
        N'windows-worker-plugin',
        N'omp.example.workerapp_module',
        N'OpenModulePlatform.Worker.ExampleWorkerAppModule.dll',
        1);
END

IF NOT EXISTS (SELECT 1 FROM omp.ModuleInstances WHERE ModuleInstanceId = @WorkerModuleInstanceId)
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
        @WorkerModuleInstanceId,
        @InstanceId,
        @WorkerModuleId,
        N'example_workerapp_module',
        N'Example WorkerAppModule',
        N'Example module instance with a web app and a manager-driven worker app',
        1,
        410);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @WorkerModuleId,
        ModuleInstanceKey = N'example_workerapp_module',
        DisplayName = N'Example WorkerAppModule',
        Description = N'Example module instance with a web app and a manager-driven worker app',
        IsEnabled = 1,
        SortOrder = 410,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleInstanceId = @WorkerModuleInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateModuleInstances
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'example_workerapp_module'
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
        @WorkerModuleId,
        N'example_workerapp_module',
        N'Example WorkerAppModule',
        N'Example module instance with a web app and a manager-driven worker app',
        410);
END

SELECT @WorkerTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'example_workerapp_module';

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @WorkerWebAppInstanceId)
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
        @WorkerWebAppInstanceId,
        @WorkerModuleInstanceId,
        @SampleHostId,
        @WorkerWebAppId,
        N'example_workerapp_module_webapp',
        N'Example WorkerAppModule',
        N'Primary web app instance for the example manager-driven worker module',
        N'ExampleWorkerAppModule',
        N'webapp',
        1,
        1,
        1,
        410);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @WorkerModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @WorkerWebAppId,
        AppInstanceKey = N'example_workerapp_module_webapp',
        DisplayName = N'Example WorkerAppModule',
        Description = N'Primary web app instance for the example manager-driven worker module',
        RoutePath = N'ExampleWorkerAppModule',
        InstallationName = N'webapp',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 410,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @WorkerWebAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @WorkerTemplateModuleInstanceId
      AND AppInstanceKey = N'example_workerapp_module_webapp'
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
        @WorkerTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @WorkerWebAppId,
        N'example_workerapp_module_webapp',
        N'Example WorkerAppModule',
        N'Primary web app instance for the example manager-driven worker module',
        N'ExampleWorkerAppModule',
        N'webapp',
        1,
        410);
END

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @WorkerAppInstanceId)
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
        IsEnabled,
        IsAllowed,
        DesiredState,
        SortOrder)
    VALUES(
        @WorkerAppInstanceId,
        @WorkerModuleInstanceId,
        @SampleHostId,
        @WorkerAppId,
        N'example_workerapp_module_worker',
        N'Example Managed Worker',
        N'Primary manager-driven worker app instance for the example module',
        N'C:\Program Files\OpenModulePlatform\WorkerApps\ExampleWorkerAppModule',
        N'default',
        @WorkerArtifactId,
        @InitialWorkerConfigId,
        1,
        1,
        1,
        411);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @WorkerModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @WorkerAppId,
        AppInstanceKey = N'example_workerapp_module_worker',
        DisplayName = N'Example Managed Worker',
        Description = N'Primary manager-driven worker app instance for the example module',
        InstallPath = N'C:\Program Files\OpenModulePlatform\WorkerApps\ExampleWorkerAppModule',
        InstallationName = N'default',
        ArtifactId = @WorkerArtifactId,
        ConfigId = @InitialWorkerConfigId,
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 411,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @WorkerAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @WorkerTemplateModuleInstanceId
      AND AppInstanceKey = N'example_workerapp_module_worker'
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
        DesiredState,
        SortOrder)
    VALUES(
        @WorkerTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @WorkerAppId,
        N'example_workerapp_module_worker',
        N'Example Managed Worker',
        N'Primary manager-driven worker app instance for the example module',
        N'C:\Program Files\OpenModulePlatform\WorkerApps\ExampleWorkerAppModule',
        N'default',
        @WorkerArtifactId,
        @InitialWorkerConfigId,
        1,
        411);
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


-------------------------------------------------------------------------------
-- Seed sample jobs for the manager-driven worker example
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp_example_workerapp_module.Jobs)
BEGIN
    INSERT INTO omp_example_workerapp_module.Jobs(RequestType, PayloadJson, Status, RequestedUtc, RequestedBy)
    VALUES
        (N'sample.run', N'{"message":"hello from worker manager"}', 0, SYSUTCDATETIME(), N'install-script'),
        (N'sample.run', N'{"message":"second managed worker job"}', 0, SYSUTCDATETIME(), N'install-script');
END
GO
