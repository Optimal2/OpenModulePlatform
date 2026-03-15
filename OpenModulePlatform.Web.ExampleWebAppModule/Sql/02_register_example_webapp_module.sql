-- File: OpenModulePlatform.Web.ExampleWebAppModule/Sql/02_register_example_webapp_module.sql
USE [OpenModulePlatform];
GO

DECLARE @InstanceKey nvarchar(100) = N'default';
DECLARE @ModuleKey nvarchar(100) = N'example_webapp_module';
DECLARE @AppKey nvarchar(100) = N'example_webapp_module_webapp';
DECLARE @InstanceId uniqueidentifier;
DECLARE @ModuleId int;
DECLARE @AppId int;
DECLARE @ViewPermissionId int;
DECLARE @AdminPermissionId int;

SELECT @InstanceId = InstanceId FROM omp.Instances WHERE InstanceKey = @InstanceKey;
IF @InstanceId IS NULL
    THROW 50000, 'Default OMP instance not found. Run SQL_Install_OpenModulePlatform.sql first.', 1;

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleWebAppModule.View', N'Read access to Example WebAppModule');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleWebAppModule.Admin', N'Administrative access to Example WebAppModule');

SELECT @ViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.View';
SELECT @AdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE InstanceId = @InstanceId AND ModuleKey = @ModuleKey)
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example WebAppModule',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_example_webapp_module',
        BasePath = N'ExampleWebAppModule',
        Description = N'Web-only example module for OMP',
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceId = @InstanceId AND ModuleKey = @ModuleKey;
END
ELSE
BEGIN
    INSERT INTO omp.Modules(InstanceId, ModuleKey, DisplayName, ModuleType, SchemaName, BasePath, Description, IsEnabled, SortOrder)
    VALUES(@InstanceId, @ModuleKey, N'Example WebAppModule', N'WebAppModule', N'omp_example_webapp_module', N'ExampleWebAppModule', N'Web-only example module for OMP', 1, 300);
END

SELECT @ModuleId = ModuleId FROM omp.Modules WHERE InstanceId = @InstanceId AND ModuleKey = @ModuleKey;

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ModuleId AND AppKey = @AppKey)
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example WebAppModule',
        AppType = N'WebApp',
        RouteBasePath = N'ExampleWebAppModule',
        Description = N'Example web app for the OMP web-only module sample',
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ModuleId AND AppKey = @AppKey;
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, RouteBasePath, Description, IsEnabled, SortOrder)
    VALUES(@ModuleId, @AppKey, N'Example WebAppModule', N'WebApp', N'ExampleWebAppModule', N'Example web app for the OMP web-only module sample', 1, 300);
END

SELECT @AppId = AppId FROM omp.Apps WHERE ModuleId = @ModuleId AND AppKey = @AppKey;

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @AppId AND PermissionId = @ViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@AppId, @ViewPermissionId, 0);

IF NOT EXISTS (SELECT 1 FROM omp.RolePermissions rp INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId WHERE r.Name = N'PortalAdmins' AND rp.PermissionId = @ViewPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    SELECT RoleId, @ViewPermissionId FROM omp.Roles WHERE Name = N'PortalAdmins';

IF NOT EXISTS (SELECT 1 FROM omp.RolePermissions rp INNER JOIN omp.Roles r ON r.RoleId = rp.RoleId WHERE r.Name = N'PortalAdmins' AND rp.PermissionId = @AdminPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    SELECT RoleId, @AdminPermissionId FROM omp.Roles WHERE Name = N'PortalAdmins';
GO
