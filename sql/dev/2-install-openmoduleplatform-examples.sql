-- File: sql/dev/2-install-openmoduleplatform-examples.sql
/*
OpenModulePlatform dev convenience initialization script.

Seeds the OMP core data plus the Portal, iFrame, and example module default
rows for quick local and test environment setup.
*/

-------------------------------------------------------------------------------
-- Included from sql/2-initialize-openmoduleplatform.sql
-------------------------------------------------------------------------------
-- File: sql/2-initialize-openmoduleplatform.sql
/*
OpenModulePlatform core initialization script.

Seeds the default OMP instance, bootstrap RBAC placeholders, baseline host
rows, and shared structural values that live in the omp schema.

Prerequisite:
- Run 1-setup-openmoduleplatform.sql first.

Portal, iframe, and example modules are initialized separately from their own
module sql folders.
*/
USE [OpenModulePlatform];
GO

-------------------------------------------------------------------------------
-- Seed baseline instance, templates, host, and structural placeholders
-------------------------------------------------------------------------------
DECLARE @DefaultInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111111';
DECLARE @DefaultPortalModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111112';
DECLARE @DefaultPortalAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111113';
DECLARE @DefaultHostId uniqueidentifier = '11111111-1111-1111-1111-111111111121';
DECLARE @PortalModuleId int;
DECLARE @PortalAppId int;
DECLARE @PortalViewPermissionId int;
DECLARE @PortalAdminPermissionId int;
DECLARE @PortalAdminsRoleId int;
DECLARE @DefaultInstanceTemplateId int;
DECLARE @DefaultHostTemplateId int;
DECLARE @DefaultTemplateHostId int;
DECLARE @DefaultTemplatePortalModuleInstanceId int;
DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = N'REPLACE_ME\\UserOrGroup';

IF NOT EXISTS (SELECT 1 FROM omp.InstanceTemplates WHERE TemplateKey = N'default')
BEGIN
    INSERT INTO omp.InstanceTemplates(TemplateKey, DisplayName, Description)
    VALUES(N'default', N'Default Instance Template', N'Minimal baseline template for an OMP instance');
END

IF NOT EXISTS (SELECT 1 FROM omp.HostTemplates WHERE TemplateKey = N'default-host')
BEGIN
    INSERT INTO omp.HostTemplates(TemplateKey, DisplayName, Description)
    VALUES(N'default-host', N'Default Host Template', N'Minimal baseline host template for development and examples');
END

SELECT @DefaultInstanceTemplateId = InstanceTemplateId FROM omp.InstanceTemplates WHERE TemplateKey = N'default';
SELECT @DefaultHostTemplateId = HostTemplateId FROM omp.HostTemplates WHERE TemplateKey = N'default-host';

IF NOT EXISTS (SELECT 1 FROM omp.Instances WHERE InstanceId = @DefaultInstanceId)
BEGIN
    INSERT INTO omp.Instances(
        InstanceId,
        InstanceKey,
        DisplayName,
        Description,
        InstanceTemplateId)
    VALUES(
        @DefaultInstanceId,
        N'default',
        N'Default Instance',
        N'Default OMP instance seeded by the install script',
        @DefaultInstanceTemplateId);
END
ELSE
BEGIN
    UPDATE omp.Instances
    SET InstanceKey = N'default',
        DisplayName = N'Default Instance',
        Description = N'Default OMP instance seeded by the install script',
        InstanceTemplateId = @DefaultInstanceTemplateId,
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceId = @DefaultInstanceId;
END

IF NOT EXISTS (SELECT 1 FROM omp.Hosts WHERE HostId = @DefaultHostId)
BEGIN
    INSERT INTO omp.Hosts(HostId, InstanceId, HostKey, DisplayName, BaseUrl, Environment, OsFamily, Architecture)
    VALUES(@DefaultHostId, @DefaultInstanceId, N'sample-host', N'Sample Host', NULL, N'Development', N'Windows', N'x64');
END
ELSE
BEGIN
    UPDATE omp.Hosts
    SET InstanceId = @DefaultInstanceId,
        HostKey = N'sample-host',
        DisplayName = N'Sample Host',
        BaseUrl = NULL,
        Environment = N'Development',
        OsFamily = N'Windows',
        Architecture = N'x64',
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE HostId = @DefaultHostId;
END

IF NOT EXISTS (SELECT 1 FROM omp.InstanceTemplateHosts WHERE InstanceTemplateId = @DefaultInstanceTemplateId AND HostKey = N'sample-host')
BEGIN
    INSERT INTO omp.InstanceTemplateHosts(InstanceTemplateId, HostTemplateId, HostKey, DisplayName, Environment, SortOrder)
    VALUES(@DefaultInstanceTemplateId, @DefaultHostTemplateId, N'sample-host', N'Sample Host', N'Development', 100);
END

SELECT @DefaultTemplateHostId = InstanceTemplateHostId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateId = @DefaultInstanceTemplateId AND HostKey = N'sample-host';

IF NOT EXISTS (SELECT 1 FROM omp.HostDeploymentAssignments WHERE HostId = @DefaultHostId AND HostTemplateId = @DefaultHostTemplateId)
BEGIN
    INSERT INTO omp.HostDeploymentAssignments(HostId, HostTemplateId, AssignedBy, IsActive)
    VALUES(@DefaultHostId, @DefaultHostTemplateId, N'install-script', 1);
END

-------------------------------------------------------------------------------
-- Seed baseline administrative placeholder role
-------------------------------------------------------------------------------
DECLARE @PortalAdminsRoleId int;
DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = N'REPLACE_ME\\UserOrGroup';

IF NOT EXISTS (SELECT 1 FROM omp.Roles WHERE Name = N'PortalAdmins')
    INSERT INTO omp.Roles(Name, Description) VALUES(N'PortalAdmins', N'Administrative bootstrap role for OMP modules and portal');

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';

/*
Bootstrap administrative principal row.

Replace the placeholder principal below before you try to sign in to OMP Portal
or other OMP modules that rely on the shared PortalAdmins bootstrap role.
Examples:
- DOMAIN\your.user
- DOMAIN\OMP Portal Admins
*/
IF EXISTS (SELECT 1 FROM omp.RolePrincipals WHERE RoleId = @PortalAdminsRoleId AND PrincipalType = N'User')
BEGIN
    UPDATE omp.RolePrincipals
    SET Principal = @BootstrapPortalAdminPrincipal
    WHERE RoleId = @PortalAdminsRoleId AND PrincipalType = N'User';
END
ELSE
BEGIN
    INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
    VALUES(@PortalAdminsRoleId, N'User', @BootstrapPortalAdminPrincipal);
END
GO


-------------------------------------------------------------------------------
-- Included from OpenModulePlatform.Portal/sql/2-initialize-omp-portal.sql
-------------------------------------------------------------------------------
-- File: OpenModulePlatform.Portal/sql/2-initialize-omp-portal.sql
/*
Seeds default values and OMP registration rows for the OMP Portal.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-omp-portal.sql
*/
USE [OpenModulePlatform];
GO

-- Seed OMP Portal definitions and instance rows
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'OMP.Portal.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'OMP.Portal.View', N'Read access to the OMP Portal');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'OMP.Portal.Admin')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'OMP.Portal.Admin', N'Administrative access to the OMP Portal');

SELECT @PortalViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'OMP.Portal.View';
SELECT @PortalAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'OMP.Portal.Admin';

IF NOT EXISTS (SELECT 1 FROM omp.Roles WHERE Name = N'PortalAdmins')
    INSERT INTO omp.Roles(Name, Description) VALUES(N'PortalAdmins', N'Administrators for the OMP Portal');

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';

IF NOT EXISTS (SELECT 1 FROM omp.RolePermissions WHERE RoleId = @PortalAdminsRoleId AND PermissionId = @PortalViewPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId) VALUES(@PortalAdminsRoleId, @PortalViewPermissionId);

IF NOT EXISTS (SELECT 1 FROM omp.RolePermissions WHERE RoleId = @PortalAdminsRoleId AND PermissionId = @PortalAdminPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId) VALUES(@PortalAdminsRoleId, @PortalAdminPermissionId);

/*
Bootstrap portal administrator row.

Replace the placeholder principal below before you try to sign in to OMP Portal.
Examples:
- DOMAIN\your.user
- DOMAIN\OMP Portal Admins
*/
IF EXISTS (SELECT 1 FROM omp.RolePrincipals WHERE RoleId = @PortalAdminsRoleId AND PrincipalType = N'User')
BEGIN
    UPDATE omp.RolePrincipals
    SET Principal = @BootstrapPortalAdminPrincipal
    WHERE RoleId = @PortalAdminsRoleId AND PrincipalType = N'User';
END
ELSE
BEGIN
    INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
    VALUES(@PortalAdminsRoleId, N'User', @BootstrapPortalAdminPrincipal);
END

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'omp_portal')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'OMP Portal',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_portal',
        Description = N'Core portal web app for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 100,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'omp_portal';
END
ELSE
BEGIN
    INSERT INTO omp.Modules(ModuleKey, DisplayName, ModuleType, SchemaName, Description, IsEnabled, SortOrder)
    VALUES(N'omp_portal', N'OMP Portal', N'WebAppModule', N'omp_portal', N'Core portal web app for OpenModulePlatform', 1, 100);
END

SELECT @PortalModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'omp_portal';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @PortalModuleId AND AppKey = N'omp_portal')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'OMP Portal',
        AppType = N'Portal',
        Description = N'Primary OMP portal web application',
        IsEnabled = 1,
        SortOrder = 100,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @PortalModuleId AND AppKey = N'omp_portal';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(@PortalModuleId, N'omp_portal', N'OMP Portal', N'Portal', N'Primary OMP portal web application', 1, 100);
END

SELECT @PortalAppId = AppId FROM omp.Apps WHERE ModuleId = @PortalModuleId AND AppKey = N'omp_portal';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @PortalAppId AND PermissionId = @PortalViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@PortalAppId, @PortalViewPermissionId, 0);

IF NOT EXISTS (SELECT 1 FROM omp.ModuleInstances WHERE ModuleInstanceId = @DefaultPortalModuleInstanceId)
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
        @DefaultPortalModuleInstanceId,
        @DefaultInstanceId,
        @PortalModuleId,
        N'omp_portal',
        N'OMP Portal',
        N'Portal app instance for the default OMP instance',
        1,
        100);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @DefaultInstanceId,
        ModuleId = @PortalModuleId,
        ModuleInstanceKey = N'omp_portal',
        DisplayName = N'OMP Portal',
        Description = N'Portal app instance for the default OMP instance',
        IsEnabled = 1,
        SortOrder = 100,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleInstanceId = @DefaultPortalModuleInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateModuleInstances
    WHERE InstanceTemplateId = @DefaultInstanceTemplateId
      AND ModuleInstanceKey = N'omp_portal'
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
        @DefaultInstanceTemplateId,
        @PortalModuleId,
        N'omp_portal',
        N'OMP Portal',
        N'Portal app instance for the default template',
        100);
END

SELECT @DefaultTemplatePortalModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @DefaultInstanceTemplateId
  AND ModuleInstanceKey = N'omp_portal';

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @DefaultPortalAppInstanceId)
BEGIN
    INSERT INTO omp.AppInstances(
        AppInstanceId, ModuleInstanceId, HostId, AppId, AppInstanceKey, DisplayName, Description,
        RoutePath, InstallationName, IsEnabled, IsAllowed, DesiredState, SortOrder)
    VALUES(
        @DefaultPortalAppInstanceId, @DefaultPortalModuleInstanceId, @DefaultHostId, @PortalAppId, N'omp_portal', N'OMP Portal',
        N'Primary OMP portal app instance for the default OMP instance', N'', N'portal', 1, 1, 1, 100);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @DefaultPortalModuleInstanceId,
        HostId = @DefaultHostId,
        AppId = @PortalAppId,
        AppInstanceKey = N'omp_portal',
        DisplayName = N'OMP Portal',
        Description = N'Primary OMP portal app instance for the default OMP instance',
        RoutePath = N'',
        InstallationName = N'portal',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 100,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @DefaultPortalAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @DefaultTemplatePortalModuleInstanceId
      AND AppInstanceKey = N'omp_portal'
)
BEGIN
    INSERT INTO omp.InstanceTemplateAppInstances(
        InstanceTemplateModuleInstanceId, InstanceTemplateHostId, AppId, AppInstanceKey, DisplayName, Description,
        RoutePath, InstallationName, DesiredState, SortOrder)
    VALUES(
        @DefaultTemplatePortalModuleInstanceId, @DefaultTemplateHostId, @PortalAppId, N'omp_portal', N'OMP Portal',
        N'Primary OMP portal app instance for the default template', N'', N'portal', 1, 100);
END
GO


-------------------------------------------------------------------------------
-- Included from OpenModulePlatform.Web.iFrameWebAppModule/sql/2-initialize-iframe-webapp.sql
-------------------------------------------------------------------------------
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


-------------------------------------------------------------------------------
-- Included from examples/WebAppModule/Sql/2-initialize-example-webapp.sql
-------------------------------------------------------------------------------
-- File: examples/WebAppModule/Sql/2-initialize-example-webapp.sql
/*
Seeds default values and OMP registration rows for the example Web App module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-ExampleWebAppModule.sql
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


-------------------------------------------------------------------------------
-- Included from examples/WebAppBlazorModule/Sql/2-initialize-example-webapp-blazor.sql
-------------------------------------------------------------------------------
-- File: examples/WebAppBlazorModule/Sql/2-initialize-example-webapp-blazor.sql
/*
Seeds default values and OMP registration rows for the example Web App Blazor module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-ExampleWebAppBlazorModule.sql
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


-------------------------------------------------------------------------------
-- Included from examples/ServiceAppModule/WebApp/Sql/2-initialize-example-serviceapp.sql
-------------------------------------------------------------------------------
-- File: examples/ServiceAppModule/WebApp/Sql/2-initialize-example-serviceapp.sql
/*
Seeds default values and OMP registration rows for the example Service App module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-ExampleServiceAppModule.sql
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_serviceapp.Configurations WHERE VersionNo = 0)
BEGIN
    /*
    scanBatchSize is intentionally seeded as 1 for the canonical example dataset.
    This keeps the sample worker behavior deterministic and easy to observe during local evaluation,
    screenshots, demos and troubleshooting. It is not a production recommendation.
    Increase this value in the service configuration for real environments after validation.
    */
    INSERT INTO omp_example_serviceapp.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"scanBatchSize": 1, "sampleMode": true}', N'Initial example service configuration', N'install-script');
END
GO

DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @SampleHostId uniqueidentifier;
DECLARE @SampleTemplateHostId int;
DECLARE @PortalAdminsRoleId int;
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
    THROW 50000, 'Default OMP instance not found. Run the core SQL setup/init scripts first.', 1;

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';
SELECT @SampleHostId = HostId FROM omp.Hosts WHERE InstanceId = @InstanceId AND HostKey = N'sample-host';
SELECT @SampleTemplateHostId = InstanceTemplateHostId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateId = @InstanceTemplateId
  AND HostKey = N'sample-host';
SELECT TOP (1) @InitialServiceConfigId = ConfigId
FROM omp_example_serviceapp.Configurations
WHERE VersionNo = 0
ORDER BY ConfigId DESC;

-- Example ServiceApp registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleServiceAppModule.View', N'Read access to the Example ServiceApp');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'ExampleServiceAppModule.Admin',
        N'Administrative access to the Example ServiceApp');

SELECT @ServiceViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.View';
SELECT @ServiceAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleServiceAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_serviceapp')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example ServiceApp',
        ModuleType = N'HostAppModule',
        SchemaName = N'omp_example_serviceapp',
        Description = N'Combined web app and service app example for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'example_serviceapp';
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
        N'example_serviceapp',
        N'Example ServiceApp',
        N'HostAppModule',
        N'omp_example_serviceapp',
        N'Combined web app and service app example for OpenModulePlatform',
        1,
        400);
END

SELECT @ServiceModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'example_serviceapp';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example ServiceApp',
        AppType = N'WebApp',
        Description = N'Web app definition for the example HostAppModule',
        IsEnabled = 1,
        SortOrder = 400,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @ServiceModuleId,
        N'example_serviceapp_webapp',
        N'Example ServiceApp',
        N'WebApp',
        N'Web app definition for the example HostAppModule',
        1,
        400);
END

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_service')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example Service Worker',
        AppType = N'ServiceApp',
        Description = N'Service app definition for the example HostAppModule',
        IsEnabled = 1,
        SortOrder = 401,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_service';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @ServiceModuleId,
        N'example_serviceapp_service',
        N'Example Service Worker',
        N'ServiceApp',
        N'Service app definition for the example HostAppModule',
        1,
        401);
END

SELECT @ServiceWebAppId = AppId FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_webapp';
SELECT @ServiceAppId = AppId FROM omp.Apps WHERE ModuleId = @ServiceModuleId AND AppKey = N'example_serviceapp_service';

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
        N'example_serviceapp',
        N'Example ServiceApp',
        N'Example module instance with both web and service apps',
        1,
        400);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @ServiceModuleId,
        ModuleInstanceKey = N'example_serviceapp',
        DisplayName = N'Example ServiceApp',
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
      AND ModuleInstanceKey = N'example_serviceapp'
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
        N'example_serviceapp',
        N'Example ServiceApp',
        N'Example module instance with both web and service apps',
        400);
END

SELECT @ServiceTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'example_serviceapp';

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
        N'example_serviceapp_webapp',
        N'Example ServiceApp',
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
        AppInstanceKey = N'example_serviceapp_webapp',
        DisplayName = N'Example ServiceApp',
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
      AND AppInstanceKey = N'example_serviceapp_webapp'
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
        N'example_serviceapp_webapp',
        N'Example ServiceApp',
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
        N'example_serviceapp_service',
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
        AppInstanceKey = N'example_serviceapp_service',
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
      AND AppInstanceKey = N'example_serviceapp_service'
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
        N'example_serviceapp_service',
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
-- Seed sample jobs for the service-backed example
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp_example_serviceapp.Jobs)
BEGIN
    INSERT INTO omp_example_serviceapp.Jobs(RequestType, PayloadJson, Status, RequestedUtc, RequestedBy)
    VALUES
        (N'sample.run', N'{"message":"hello from OMP"}', 0, SYSUTCDATETIME(), N'install-script'),
        (N'sample.run', N'{"message":"second sample job"}', 0, SYSUTCDATETIME(), N'install-script');
END
GO


-------------------------------------------------------------------------------
GO


-------------------------------------------------------------------------------
-- Included from examples/WorkerAppModule/WebApp/Sql/2-initialize-example-workerapp.sql
-------------------------------------------------------------------------------
-- File: examples/WorkerAppModule/WebApp/Sql/2-initialize-example-workerapp.sql
/*
Seeds default values and OMP registration rows for the example Worker App module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-ExampleWorkerAppModule.sql
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM omp_example_workerapp.Configurations WHERE VersionNo = 0)
BEGIN
    INSERT INTO omp_example_workerapp.Configurations(VersionNo, ConfigJson, Comment, CreatedBy)
    VALUES(0, N'{"scanBatchSize": 1, "sampleMode": true, "runtime": "worker-manager"}', N'Initial example worker configuration', N'install-script');
END
GO

DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @SampleHostId uniqueidentifier;
DECLARE @SampleTemplateHostId int;
DECLARE @PortalAdminsRoleId int;
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
    THROW 50000, 'Default OMP instance not found. Run the core SQL setup/init scripts first.', 1;

SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';
SELECT @SampleHostId = HostId FROM omp.Hosts WHERE InstanceId = @InstanceId AND HostKey = N'sample-host';
SELECT @SampleTemplateHostId = InstanceTemplateHostId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateId = @InstanceTemplateId
  AND HostKey = N'sample-host';
SELECT TOP (1) @InitialWorkerConfigId = ConfigId
FROM omp_example_workerapp.Configurations
WHERE VersionNo = 0
ORDER BY ConfigId DESC;

-- Example WorkerApp registration
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWorkerAppModule.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'ExampleWorkerAppModule.View', N'Read access to the Example WorkerApp');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ExampleWorkerAppModule.Admin')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(
        N'ExampleWorkerAppModule.Admin',
        N'Administrative access to the Example WorkerApp');

SELECT @WorkerViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWorkerAppModule.View';
SELECT @WorkerAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ExampleWorkerAppModule.Admin';

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_workerapp')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Example WorkerApp',
        ModuleType = N'HostAppModule',
        SchemaName = N'omp_example_workerapp',
        Description = N'Combined web app and manager-driven worker example module for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 410,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'example_workerapp';
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
        N'example_workerapp',
        N'Example WorkerApp',
        N'HostAppModule',
        N'omp_example_workerapp',
        N'Combined web app and manager-driven worker example module for OpenModulePlatform',
        1,
        410);
END

SELECT @WorkerModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'example_workerapp';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example WorkerApp',
        AppType = N'WebApp',
        Description = N'Web app definition for the example manager-driven worker module',
        IsEnabled = 1,
        SortOrder = 410,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_webapp';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @WorkerModuleId,
        N'example_workerapp_webapp',
        N'Example WorkerApp',
        N'WebApp',
        N'Web app definition for the example manager-driven worker module',
        1,
        410);
END

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_worker')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Example Managed Worker',
        AppType = N'ServiceApp',
        Description = N'Manager-driven worker app definition for the example worker module',
        IsEnabled = 1,
        SortOrder = 411,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_worker';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, Description, IsEnabled, SortOrder)
    VALUES(
        @WorkerModuleId,
        N'example_workerapp_worker',
        N'Example Managed Worker',
        N'ServiceApp',
        N'Manager-driven worker app definition for the example worker module',
        1,
        411);
END

SELECT @WorkerWebAppId = AppId FROM omp.Apps WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_webapp';
SELECT @WorkerAppId = AppId FROM omp.Apps WHERE ModuleId = @WorkerModuleId AND AppKey = N'example_workerapp_worker';

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
        N'example_workerapp',
        N'Example WorkerApp',
        N'Example module instance with a web app and a manager-driven worker app',
        1,
        410);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @WorkerModuleId,
        ModuleInstanceKey = N'example_workerapp',
        DisplayName = N'Example WorkerApp',
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
      AND ModuleInstanceKey = N'example_workerapp'
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
        N'example_workerapp',
        N'Example WorkerApp',
        N'Example module instance with a web app and a manager-driven worker app',
        410);
END

SELECT @WorkerTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'example_workerapp';

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
        N'example_workerapp_webapp',
        N'Example WorkerApp',
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
        AppInstanceKey = N'example_workerapp_webapp',
        DisplayName = N'Example WorkerApp',
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
      AND AppInstanceKey = N'example_workerapp_webapp'
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
        N'example_workerapp_webapp',
        N'Example WorkerApp',
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
        N'example_workerapp_worker',
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
        AppInstanceKey = N'example_workerapp_worker',
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
      AND AppInstanceKey = N'example_workerapp_worker'
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
        N'example_workerapp_worker',
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
-- Seed sample jobs for the manager-driven worker example
-------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM omp_example_workerapp.Jobs)
BEGIN
    INSERT INTO omp_example_workerapp.Jobs(RequestType, PayloadJson, Status, RequestedUtc, RequestedBy)
    VALUES
        (N'sample.run', N'{"message":"hello from worker manager"}', 0, SYSUTCDATETIME(), N'install-script'),
        (N'sample.run', N'{"message":"second managed worker job"}', 0, SYSUTCDATETIME(), N'install-script');
END
GO
GO

