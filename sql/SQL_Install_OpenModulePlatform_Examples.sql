-- File: sql/SQL_Install_OpenModulePlatform_Examples.sql
/*
OpenModulePlatform example install script.

This script is intended to be run after SQL_Install_OpenModulePlatform.sql.
It installs the example WebAppModule and HostAppModule included in this
repository and seeds a small amount of sample data so the portal and service
sample can be exercised immediately.
*/
USE [OpenModulePlatform];
GO

IF OBJECT_ID(N'omp.Instances', N'U') IS NULL
    THROW 50000, 'OMP core schema not found. Run SQL_Install_OpenModulePlatform.sql first.', 1;
GO

-------------------------------------------------------------------------------
-- Example WebAppModule schema
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_webapp_module')
    EXEC('CREATE SCHEMA [omp_example_webapp_module]');
GO

IF OBJECT_ID(N'omp_example_webapp_module.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_webapp_module.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL CONSTRAINT DF_ExampleWeb_Config_VersionNo DEFAULT(0),
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
    VALUES(0, N'{"featureFlag": true, "displayMode": "sample"}', N'Initial example web module configuration', SUSER_SNAME());
END
GO

-------------------------------------------------------------------------------
-- Example HostAppModule schema
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp_example_serviceapp_module')
    EXEC('CREATE SCHEMA [omp_example_serviceapp_module]');
GO

IF OBJECT_ID(N'omp_example_serviceapp_module.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE omp_example_serviceapp_module.Configurations
    (
        ConfigId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        VersionNo int NOT NULL CONSTRAINT DF_ExampleService_Config_VersionNo DEFAULT(0),
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
        ClaimedByHostInstallationId uniqueidentifier NULL,
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
        HostInstallationId uniqueidentifier NOT NULL,
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
    VALUES(0, N'{"scanBatchSize": 1, "sampleMode": true}', N'Initial example service configuration', SUSER_SNAME());
END
GO

-------------------------------------------------------------------------------
-- Registration for example modules and apps
-------------------------------------------------------------------------------
DECLARE @InstanceId uniqueidentifier;
DECLARE @PortalAdminsRoleId int;
DECLARE @DefaultHostId uniqueidentifier;

DECLARE @WebModuleId int;
DECLARE @WebAppId int;
DECLARE @WebViewPermissionId int;
DECLARE @WebAdminPermissionId int;

DECLARE @ServiceModuleId int;
DECLARE @ServiceWebAppId int;
DECLARE @ServiceAppId int;
DECLARE @ServiceViewPermissionId int;
DECLARE @ServiceAdminPermissionId int;
DECLARE @InitialServiceConfigId int;
DECLARE @ServiceArtifactId int;
DECLARE @SampleHostInstallationId uniqueidentifier = '11111111-1111-1111-1111-111111111101';

SELECT @InstanceId = InstanceId FROM omp.Instances WHERE InstanceKey = N'default';
IF @InstanceId IS NULL
    THROW 50000, 'Default OMP instance not found. Run SQL_Install_OpenModulePlatform.sql first.', 1;

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';
SELECT @DefaultHostId = HostId FROM omp.Hosts WHERE InstanceId = @InstanceId AND Hostname = N'default-host';
SELECT TOP (1) @InitialServiceConfigId = ConfigId FROM omp_example_serviceapp_module.Configurations WHERE VersionNo = 0 ORDER BY ConfigId DESC;

-------------------------------------------------------------------------------
-- Example WebAppModule registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleWebAppModule.View', N'Read access to the Example WebAppModule');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleWebAppModule.Admin', N'Administrative access to the Example WebAppModule');

SELECT @WebViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.View';
SELECT @WebAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE InstanceId = @InstanceId AND ModuleKey = N'example_webapp_module')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example WebAppModule',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_example_webapp_module',
        BasePath = N'ExampleWebAppModule',
        Description = N'Web-only example module for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 300,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceId = @InstanceId AND ModuleKey = N'example_webapp_module';
END
ELSE
BEGIN
    INSERT INTO omp.Modules(InstanceId, ModuleKey, DisplayName, ModuleType, SchemaName, BasePath, Description, IsEnabled, SortOrder)
    VALUES(@InstanceId, N'example_webapp_module', N'Example WebAppModule', N'WebAppModule', N'omp_example_webapp_module', N'ExampleWebAppModule', N'Web-only example module for OpenModulePlatform', 1, 300);
END

SELECT @WebModuleId = ModuleId FROM omp.Modules WHERE InstanceId = @InstanceId AND ModuleKey = N'example_webapp_module';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @WebModuleId AND AppKey = N'example_webapp_module_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example WebAppModule',
        AppType = N'WebApp',
        RouteBasePath = N'ExampleWebAppModule',
        Description = N'Web app for the web-only example module',
        IsEnabled = 1,
        SortOrder = 300,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @WebModuleId AND AppKey = N'example_webapp_module_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, RouteBasePath, Description, IsEnabled, SortOrder)
    VALUES(@WebModuleId, N'example_webapp_module_webapp', N'Example WebAppModule', N'WebApp', N'ExampleWebAppModule', N'Web app for the web-only example module', 1, 300);
END

SELECT @WebAppId = AppId FROM omp.Apps WHERE ModuleId = @WebModuleId AND AppKey = N'example_webapp_module_webapp';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @WebAppId AND PermissionId = @WebViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@WebAppId, @WebViewPermissionId, 0);

IF @PortalAdminsRoleId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM omp.RolePermissions WHERE RoleId = @PortalAdminsRoleId AND PermissionId = @WebViewPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId) VALUES(@PortalAdminsRoleId, @WebViewPermissionId);

IF @PortalAdminsRoleId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM omp.RolePermissions WHERE RoleId = @PortalAdminsRoleId AND PermissionId = @WebAdminPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId) VALUES(@PortalAdminsRoleId, @WebAdminPermissionId);

-------------------------------------------------------------------------------
-- Example HostAppModule registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleServiceAppModule.View', N'Read access to the Example ServiceAppModule');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleServiceAppModule.Admin', N'Administrative access to the Example ServiceAppModule');

SELECT @ServiceViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View';
SELECT @ServiceAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE InstanceId = @InstanceId AND ModuleKey = N'example_serviceapp_module')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example ServiceAppModule',
        ModuleType = N'HostAppModule',
        SchemaName = N'omp_example_serviceapp_module',
        BasePath = N'ExampleServiceAppModule',
        Description = N'Combined web app and service app example module for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceId = @InstanceId AND ModuleKey = N'example_serviceapp_module';
END
ELSE
BEGIN
    INSERT INTO omp.Modules(InstanceId, ModuleKey, DisplayName, ModuleType, SchemaName, BasePath, Description, IsEnabled, SortOrder)
    VALUES(@InstanceId, N'example_serviceapp_module', N'Example ServiceAppModule', N'HostAppModule', N'omp_example_serviceapp_module', N'ExampleServiceAppModule', N'Combined web app and service app example module for OpenModulePlatform', 1, 400);
END

SELECT @ServiceModuleId = ModuleId FROM omp.Modules WHERE InstanceId = @InstanceId AND ModuleKey = N'example_serviceapp_module';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example ServiceAppModule',
        AppType = N'WebApp',
        RouteBasePath = N'ExampleServiceAppModule',
        Description = N'Web app for the example HostAppModule',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, RouteBasePath, Description, IsEnabled, SortOrder)
    VALUES(@ServiceModuleId, N'example_serviceapp_module_webapp', N'Example ServiceAppModule', N'WebApp', N'ExampleServiceAppModule', N'Web app for the example HostAppModule', 1, 400);
END

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_service')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example Service Worker',
        AppType = N'ServiceApp',
        RouteBasePath = NULL,
        Description = N'Service app for the example HostAppModule',
        IsEnabled = 1,
        SortOrder = 401,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_service';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, RouteBasePath, Description, IsEnabled, SortOrder)
    VALUES(@ServiceModuleId, N'example_serviceapp_module_service', N'Example Service Worker', N'ServiceApp', NULL, N'Service app for the example HostAppModule', 1, 401);
END

SELECT @ServiceWebAppId = AppId FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_webapp';
SELECT @ServiceAppId = AppId FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_module_service';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @ServiceWebAppId AND PermissionId = @ServiceViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@ServiceWebAppId, @ServiceViewPermissionId, 0);

IF @PortalAdminsRoleId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM omp.RolePermissions WHERE RoleId = @PortalAdminsRoleId AND PermissionId = @ServiceViewPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId) VALUES(@PortalAdminsRoleId, @ServiceViewPermissionId);

IF @PortalAdminsRoleId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM omp.RolePermissions WHERE RoleId = @PortalAdminsRoleId AND PermissionId = @ServiceAdminPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId) VALUES(@PortalAdminsRoleId, @ServiceAdminPermissionId);

IF NOT EXISTS (SELECT 1 FROM omp.Artifacts WHERE AppId = @ServiceAppId AND Version = N'1.0.0' AND PackageType = N'folder' AND TargetName = N'win-x64')
    INSERT INTO omp.Artifacts(AppId, Version, PackageType, TargetName, RelativePath, IsEnabled)
    VALUES(@ServiceAppId, N'1.0.0', N'folder', N'win-x64', N'publish/ExampleServiceAppModule', 1);

SELECT TOP (1) @ServiceArtifactId = ArtifactId FROM omp.Artifacts WHERE AppId = @ServiceAppId ORDER BY ArtifactId DESC;

IF @DefaultHostId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM omp.HostInstallations WHERE HostInstallationId = @SampleHostInstallationId)
BEGIN
    INSERT INTO omp.HostInstallations(HostInstallationId, HostId, AppId, InstallationName, ArtifactId, ConfigId, IsAllowed, DesiredState, VerificationStatus, CreatedUtc, UpdatedUtc)
    VALUES(@SampleHostInstallationId, @DefaultHostId, @ServiceAppId, N'default', @ServiceArtifactId, @InitialServiceConfigId, 1, 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
END
GO

-------------------------------------------------------------------------------
-- Sample jobs for the service module
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp_example_serviceapp_module.Jobs)
BEGIN
    INSERT INTO omp_example_serviceapp_module.Jobs(RequestType, PayloadJson, Status, Attempts, RequestedUtc, RequestedBy)
    VALUES
        (N'sample.echo', N'{"message":"Hello from OpenModulePlatform"}', 0, 0, SYSUTCDATETIME(), SUSER_SNAME()),
        (N'sample.status', N'{"origin":"SQL_Install_OpenModulePlatform_Examples.sql"}', 0, 0, SYSUTCDATETIME(), SUSER_SNAME());
END
GO
