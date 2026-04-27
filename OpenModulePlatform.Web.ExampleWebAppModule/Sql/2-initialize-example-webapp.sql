-- File: OpenModulePlatform.Web.ExampleWebAppModule/sql/2-initialize-example-webapp.sql
/*
Seeds default values and OMP registration rows for the example Web App module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-example-webapp.sql
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_webapp.Configurations WHERE VersionNo = 0)
BEGIN
    INSERT INTO omp_example_webapp.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"sampleMode": true}', N'Initial example web configuration', N'install-script');
END
GO

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

SELECT @InstanceId = InstanceId, @InstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceKey = N'default';

IF @InstanceId IS NULL
    THROW 50000, 'Default OMP instance not found. Run the core SQL setup/init scripts first.', 1;

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';
SELECT @SampleHostId = HostId FROM omp.Hosts WHERE InstanceId = @InstanceId AND HostKey = N'sample-host';
SELECT @SampleTemplateHostId = InstanceTemplateHostId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateId = @InstanceTemplateId
  AND HostKey = N'sample-host';

-- Example WebApp registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleWebAppModule.View', N'Read access to the Example WebApp');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'ExampleWebAppModule.Admin',
        N'Administrative access to the Example WebApp');

SELECT @WebViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.View';
SELECT @WebAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWebAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_webapp')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example WebApp',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_example_webapp',
        Description = N'Web-only example app for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 300,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'example_webapp';
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
        N'example_webapp',
        N'Example WebApp',
        N'WebAppModule',
        N'omp_example_webapp',
        N'Web-only example app for OpenModulePlatform',
        1,
        300);
END

SELECT @WebModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'example_webapp';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @WebModuleId AND AppKey = N'example_webapp_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example WebApp',
        AppType = N'WebApp',
        Description = N'Web app definition for the web-only example module',
        IsEnabled = 1,
        SortOrder = 300,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @WebModuleId AND AppKey = N'example_webapp_webapp';
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
        N'example_webapp_webapp',
        N'Example WebApp',
        N'WebApp',
        N'Web app definition for the web-only example module',
        1,
        300);
END

SELECT @WebAppId = AppId FROM omp.Apps WHERE ModuleId = @WebModuleId AND AppKey = N'example_webapp_webapp';

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
        N'example_webapp',
        N'Example WebApp',
        N'Web-only example module instance',
        1,
        300);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @WebModuleId,
        ModuleInstanceKey = N'example_webapp',
        DisplayName = N'Example WebApp',
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
      AND ModuleInstanceKey = N'example_webapp'
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
        N'example_webapp',
        N'Example WebApp',
        N'Web-only example module instance in the default template',
        300);
END

SELECT @WebTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'example_webapp';

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
        N'example_webapp_webapp',
        N'Example WebApp',
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
        AppInstanceKey = N'example_webapp_webapp',
        DisplayName = N'Example WebApp',
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
      AND AppInstanceKey = N'example_webapp_webapp'
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
        N'example_webapp_webapp',
        N'Example WebApp',
        N'Primary web app instance for the example WebAppModule',
        N'ExampleWebAppModule',
        N'webapp',
        1,
        300);
END

GO
