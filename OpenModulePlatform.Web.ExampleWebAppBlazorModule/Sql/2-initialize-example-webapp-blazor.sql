-- File: OpenModulePlatform.Web.ExampleWebAppBlazorModule/sql/2-initialize-example-webapp-blazor.sql
/*
Seeds default values and OMP registration rows for the example Web App Blazor module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-example-webapp-blazor.sql
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_webapp_blazor.Configurations WHERE VersionNo = 0)
BEGIN
    INSERT INTO omp_example_webapp_blazor.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"sampleMode": true, "ui": "blazor"}', N'Initial example blazor web configuration', N'install-script');
END
GO

DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @SampleHostId uniqueidentifier;
DECLARE @SampleTemplateHostId int;
DECLARE @PortalAdminsRoleId int;
DECLARE @WebBlazorModuleId int;
DECLARE @WebBlazorModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111211';
DECLARE @WebBlazorTemplateModuleInstanceId int;
DECLARE @WebBlazorAppId int;
DECLARE @WebBlazorAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111212';
DECLARE @WebBlazorViewPermissionId int;
DECLARE @WebBlazorAdminPermissionId int;

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

-- Example WebApp Blazor registration
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

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_webapp_blazor')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example WebApp Blazor Module',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_example_webapp_blazor',
        Description = N'Blazor Server web-only example module for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'example_webapp_blazor';
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
        N'example_webapp_blazor',
        N'Example WebApp Blazor Module',
        N'WebAppModule',
        N'omp_example_webapp_blazor',
        N'Blazor Server web-only example module for OpenModulePlatform',
        1,
        310);
END

SELECT @WebBlazorModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'example_webapp_blazor';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @WebBlazorModuleId AND AppKey = N'example_webapp_blazor_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example WebApp Blazor Module',
        AppType = N'WebApp',
        Description = N'Web app definition for the Blazor web-only example module',
        IsEnabled = 1,
        SortOrder = 310,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @WebBlazorModuleId AND AppKey = N'example_webapp_blazor_webapp';
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
        N'example_webapp_blazor_webapp',
        N'Example WebApp Blazor Module',
        N'WebApp',
        N'Web app definition for the Blazor web-only example module',
        1,
        310);
END

SELECT @WebBlazorAppId = AppId FROM omp.Apps WHERE ModuleId = @WebBlazorModuleId AND AppKey = N'example_webapp_blazor_webapp';

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
        N'example_webapp_blazor',
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
        ModuleInstanceKey = N'example_webapp_blazor',
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
      AND ModuleInstanceKey = N'example_webapp_blazor'
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
        N'example_webapp_blazor',
        N'Example WebApp Blazor Module',
        N'Blazor Server web-only example module instance in the default template',
        310);
END

SELECT @WebBlazorTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'example_webapp_blazor';

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
        N'example_webapp_blazor_webapp',
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
        AppInstanceKey = N'example_webapp_blazor_webapp',
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
      AND AppInstanceKey = N'example_webapp_blazor_webapp'
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
        N'example_webapp_blazor_webapp',
        N'Example WebApp Blazor Module',
        N'Primary web app instance for the Blazor example WebAppModule',
        N'ExampleWebAppBlazorModule',
        N'webapp',
        1,
        310);
END

GO
