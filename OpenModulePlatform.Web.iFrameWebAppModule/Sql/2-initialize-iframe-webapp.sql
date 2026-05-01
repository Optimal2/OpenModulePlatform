-- File: OpenModulePlatform.Web.iFrameWebAppModule/sql/2-initialize-iframe-webapp.sql
/*
Seeds default values and OMP registration rows for the iFrame Web App module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-iframe-webapp.sql
*/
USE [OpenModulePlatform];
GO

-- Seed default iframe targets and sets
-------------------------------------------------------------------------------
SET IDENTITY_INSERT omp_iframe.urls ON;
GO

IF NOT EXISTS (SELECT 1 FROM omp_iframe.urls WHERE [id] = 1)
BEGIN
    INSERT INTO omp_iframe.urls([id], [url], [displayname], [allowed_roles], [enabled])
    VALUES(1, N'/', N'Portal', NULL, 1);
END
ELSE
BEGIN
    UPDATE omp_iframe.urls
    SET [url] = N'/',
        [displayname] = N'Portal',
        [allowed_roles] = NULL,
        [enabled] = 1
    WHERE [id] = 1;
END
GO

IF NOT EXISTS (SELECT 1 FROM omp_iframe.urls WHERE [id] = 2)
BEGIN
    INSERT INTO omp_iframe.urls([id], [url], [displayname], [allowed_roles], [enabled])
    VALUES(2, N'/ExampleWebAppModule', N'Example Web App Module', NULL, 1);
END
ELSE
BEGIN
    UPDATE omp_iframe.urls
    SET [url] = N'/ExampleWebAppModule',
        [displayname] = N'Example Web App Module',
        [allowed_roles] = NULL,
        [enabled] = 1
    WHERE [id] = 2;
END
GO

IF NOT EXISTS (SELECT 1 FROM omp_iframe.urls WHERE [id] = 3)
BEGIN
    INSERT INTO omp_iframe.urls([id], [url], [displayname], [allowed_roles], [enabled])
    VALUES(3, N'/ExampleServiceAppModule', N'Example Service App Module', NULL, 1);
END
ELSE
BEGIN
    UPDATE omp_iframe.urls
    SET [url] = N'/ExampleServiceAppModule',
        [displayname] = N'Example Service App Module',
        [allowed_roles] = NULL,
        [enabled] = 1
    WHERE [id] = 3;
END
GO

SET IDENTITY_INSERT omp_iframe.urls OFF;
GO

DECLARE @IFrameDefaultUrlSetId int;
DECLARE @IFramePortalUrlSetId int;
DECLARE @IFrameExamplesUrlSetId int;

IF EXISTS (SELECT 1 FROM omp_iframe.url_sets WHERE [set_key] = N'default')
BEGIN
    UPDATE omp_iframe.url_sets
    SET [displayname] = N'Default',
        [enabled] = 1
    WHERE [set_key] = N'default';
END
ELSE
BEGIN
    INSERT INTO omp_iframe.url_sets([set_key], [displayname], [enabled])
    VALUES(N'default', N'Default', 1);
END

SELECT @IFrameDefaultUrlSetId = [id]
FROM omp_iframe.url_sets
WHERE [set_key] = N'default';

IF EXISTS (SELECT 1 FROM omp_iframe.url_sets WHERE [set_key] = N'portal')
BEGIN
    UPDATE omp_iframe.url_sets
    SET [displayname] = N'Portal',
        [enabled] = 1
    WHERE [set_key] = N'portal';
END
ELSE
BEGIN
    INSERT INTO omp_iframe.url_sets([set_key], [displayname], [enabled])
    VALUES(N'portal', N'Portal', 1);
END

SELECT @IFramePortalUrlSetId = [id]
FROM omp_iframe.url_sets
WHERE [set_key] = N'portal';

IF EXISTS (SELECT 1 FROM omp_iframe.url_sets WHERE [set_key] = N'examples')
BEGIN
    UPDATE omp_iframe.url_sets
    SET [displayname] = N'Examples',
        [enabled] = 1
    WHERE [set_key] = N'examples';
END
ELSE
BEGIN
    INSERT INTO omp_iframe.url_sets([set_key], [displayname], [enabled])
    VALUES(N'examples', N'Examples', 1);
END

SELECT @IFrameExamplesUrlSetId = [id]
FROM omp_iframe.url_sets
WHERE [set_key] = N'examples';

DELETE FROM omp_iframe.url_set_urls
WHERE [url_set_id] IN (@IFrameDefaultUrlSetId, @IFramePortalUrlSetId, @IFrameExamplesUrlSetId);

INSERT INTO omp_iframe.url_set_urls([url_set_id], [url_id], [sort_order])
VALUES
    (@IFrameDefaultUrlSetId, 1, 10),
    (@IFrameDefaultUrlSetId, 2, 20),
    (@IFrameDefaultUrlSetId, 3, 30),
    (@IFramePortalUrlSetId, 1, 10),
    (@IFrameExamplesUrlSetId, 2, 10),
    (@IFrameExamplesUrlSetId, 3, 20);

-------------------------------------------------------------------------------
-- Register built-in iframe web app module
-------------------------------------------------------------------------------
DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @SampleHostId uniqueidentifier;
DECLARE @SampleTemplateHostId int;
DECLARE @PortalAdminsRoleId int;
DECLARE @IFrameModuleId int;
DECLARE @IFrameModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111221';
DECLARE @IFrameTemplateModuleInstanceId int;
DECLARE @IFrameAppId int;
DECLARE @IFrameAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111222';
DECLARE @IFrameViewPermissionId int;
DECLARE @IFrameAdminPermissionId int;

SELECT @InstanceId = InstanceId, @InstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceKey = N'default';

IF @InstanceId IS NULL
    THROW 50000, 'Default OMP instance not found. Run SQL_Setup_OpenModulePlatform.sql and SQL_Initialize_OpenModulePlatform.sql first.', 1;

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';
SELECT @SampleHostId = HostId FROM omp.Hosts WHERE InstanceId = @InstanceId AND HostKey = N'sample-host';
SELECT @SampleTemplateHostId = InstanceTemplateHostId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateId = @InstanceTemplateId
  AND HostKey = N'sample-host';

-- iFrame Web App registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'IFrameWebAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'IFrameWebAppModule.View', N'Read access to the iFrame Web App');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'IFrameWebAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'IFrameWebAppModule.Admin',
        N'Administrative access to the iFrame Web App');

SELECT @IFrameViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'IFrameWebAppModule.View';
SELECT @IFrameAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'IFrameWebAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'iframe_webapp')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'iFrame Web App',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_iframe',
        Description = N'Razor wrapper app that renders database-backed iframe targets inside an OMP shell',
        IsEnabled = 1,
        SortOrder = 320,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'iframe_webapp';
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
        N'iframe_webapp',
        N'iFrame Web App',
        N'WebAppModule',
        N'omp_iframe',
        N'Razor wrapper app that renders database-backed iframe targets inside an OMP shell',
        1,
        320);
END

SELECT @IFrameModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'iframe_webapp';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @IFrameModuleId AND AppKey = N'iframe_webapp_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'iFrame Web App',
        AppType = N'WebApp',
        Description = N'Web app definition for the iFrame proof-of-concept wrapper module',
        IsEnabled = 1,
        SortOrder = 320,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @IFrameModuleId AND AppKey = N'iframe_webapp_webapp';
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
        N'iframe_webapp_webapp',
        N'iFrame Web App',
        N'WebApp',
        N'Web app definition for the iFrame proof-of-concept wrapper module',
        1,
        320);
END

SELECT @IFrameAppId = AppId FROM omp.Apps WHERE ModuleId = @IFrameModuleId AND AppKey = N'iframe_webapp_webapp';

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
        N'iframe_webapp',
        N'iFrame Web App',
        N'Proof-of-concept iframe wrapper app instance',
        1,
        320);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @IFrameModuleId,
        ModuleInstanceKey = N'iframe_webapp',
        DisplayName = N'iFrame Web App',
        Description = N'Proof-of-concept iframe wrapper app instance',
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
      AND ModuleInstanceKey = N'iframe_webapp'
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
        N'iframe_webapp',
        N'iFrame Web App',
        N'Proof-of-concept iframe wrapper app instance in the default template',
        320);
END

SELECT @IFrameTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'iframe_webapp';

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
        N'iframe_webapp_webapp',
        N'iFrame Web App',
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
        AppInstanceKey = N'iframe_webapp_webapp',
        DisplayName = N'iFrame Web App',
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
      AND AppInstanceKey = N'iframe_webapp_webapp'
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
        N'iframe_webapp_webapp',
        N'iFrame Web App',
        N'Primary web app instance for the iFrame proof-of-concept module',
        N'iFrameWebAppModule',
        N'iframe-webapp',
        1,
        320);
END

GO
