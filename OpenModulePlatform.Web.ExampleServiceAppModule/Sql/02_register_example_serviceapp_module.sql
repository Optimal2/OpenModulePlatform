-- File: OpenModulePlatform.Web.ExampleServiceAppModule/Sql/02_register_example_serviceapp_module.sql
USE [OpenModulePlatform];
GO

DECLARE @InstanceKey nvarchar(100) = N'default';
DECLARE @ModuleKey nvarchar(100) = N'example_serviceapp_module';
DECLARE @PortalAppKey nvarchar(100) = N'example_serviceapp_module_webapp';
DECLARE @ServiceAppKey nvarchar(100) = N'example_serviceapp_module_service';
DECLARE @SampleHostInstallationId uniqueidentifier = '11111111-1111-1111-1111-111111111101';
DECLARE @InstanceId uniqueidentifier;
DECLARE @ModuleId int;
DECLARE @PortalAppId int;
DECLARE @ServiceAppId int;
DECLARE @ViewPermissionId int;
DECLARE @AdminPermissionId int;
DECLARE @DefaultHostId uniqueidentifier;
DECLARE @InitialConfigId int;
DECLARE @ServiceArtifactId int;

SELECT @InstanceId = InstanceId FROM omp.Instances WHERE InstanceKey = @InstanceKey;
IF @InstanceId IS NULL
    THROW 50000, 'Default OMP instance not found. Run SQL_Install_OpenModulePlatform.sql first.', 1;

SELECT @DefaultHostId = HostId FROM omp.Hosts WHERE InstanceId = @InstanceId AND Hostname = N'default-host';
SELECT TOP (1) @InitialConfigId = ConfigId FROM omp_example_serviceapp_module.Configurations WHERE VersionNo = 0 ORDER BY ConfigId DESC;

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleServiceAppModule.View', N'Read access to Example ServiceAppModule');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleServiceAppModule.Admin', N'Administrative access to Example ServiceAppModule');

SELECT @ViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View';
SELECT @AdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE InstanceId = @InstanceId AND ModuleKey = @ModuleKey)
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example ServiceAppModule',
        ModuleType = N'ServiceAppModule',
        SchemaName = N'omp_example_serviceapp_module',
        BasePath = N'ExampleServiceAppModule',
        Description = N'Combined web app + service app example module for OMP',
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceId = @InstanceId AND ModuleKey = @ModuleKey;
END
ELSE
BEGIN
    INSERT INTO omp.Modules(InstanceId, ModuleKey, DisplayName, ModuleType, SchemaName, BasePath, Description, IsEnabled, SortOrder)
    VALUES(@InstanceId, @ModuleKey, N'Example ServiceAppModule', N'ServiceAppModule', N'omp_example_serviceapp_module', N'ExampleServiceAppModule', N'Combined web app + service app example module for OMP', 1, 400);
END

SELECT @ModuleId = ModuleId FROM omp.Modules WHERE InstanceId = @InstanceId AND ModuleKey = @ModuleKey;

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ModuleId AND AppKey = @PortalAppKey)
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example ServiceAppModule',
        AppType = N'WebApp',
        RouteBasePath = N'ExampleServiceAppModule',
        Description = N'Web app for the example service module',
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ModuleId AND AppKey = @PortalAppKey;
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, RouteBasePath, Description, IsEnabled, SortOrder)
    VALUES(@ModuleId, @PortalAppKey, N'Example ServiceAppModule', N'WebApp', N'ExampleServiceAppModule', N'Web app for the example service module', 1, 400);
END

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ModuleId AND AppKey = @ServiceAppKey)
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example Service Worker',
        AppType = N'ServiceApp',
        RouteBasePath = NULL,
        Description = N'Service app for the example service module',
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ModuleId AND AppKey = @ServiceAppKey;
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, RouteBasePath, Description, IsEnabled, SortOrder)
    VALUES(@ModuleId, @ServiceAppKey, N'Example Service Worker', N'ServiceApp', NULL, N'Service app for the example service module', 1, 401);
END

SELECT @PortalAppId = AppId FROM omp.Apps WHERE ModuleId = @ModuleId AND AppKey = @PortalAppKey;
SELECT @ServiceAppId = AppId FROM omp.Apps WHERE ModuleId = @ModuleId AND AppKey = @ServiceAppKey;

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @PortalAppId AND PermissionId = @ViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@PortalAppId, @ViewPermissionId, 0);

IF NOT EXISTS (SELECT 1 FROM omp.RolePermissions rp INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId WHERE r.Name = N'PortalAdmins' AND rp.PermissionId = @ViewPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    SELECT RoleId, @ViewPermissionId FROM omp.Roles WHERE Name = N'PortalAdmins';

IF NOT EXISTS (SELECT 1 FROM omp.RolePermissions rp INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId WHERE r.Name = N'PortalAdmins' AND rp.PermissionId = @AdminPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    SELECT RoleId, @AdminPermissionId FROM omp.Roles WHERE Name = N'PortalAdmins';

IF NOT EXISTS (SELECT 1 FROM omp.Artifacts WHERE AppId = @ServiceAppId AND Version = N'1.0.0')
    INSERT INTO omp.Artifacts(AppId, Version, PackageType, TargetName, RelativePath, IsEnabled)
    VALUES(@ServiceAppId, N'1.0.0', N'folder', N'win-x64', N'publish/ExampleServiceAppModule', 1);

SELECT TOP (1) @ServiceArtifactId = ArtifactId FROM omp.Artifacts WHERE AppId = @ServiceAppId ORDER BY ArtifactId DESC;

IF @DefaultHostId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM omp.HostInstallations WHERE HostInstallationId = @SampleHostInstallationId)
BEGIN
    INSERT INTO omp.HostInstallations(HostInstallationId, HostId, AppId, InstallationName, ArtifactId, ConfigId, IsAllowed, DesiredState, VerificationStatus, CreatedUtc, UpdatedUtc)
    VALUES(@SampleHostInstallationId, @DefaultHostId, @ServiceAppId, N'default', @ServiceArtifactId, @InitialConfigId, 1, 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
END
GO
