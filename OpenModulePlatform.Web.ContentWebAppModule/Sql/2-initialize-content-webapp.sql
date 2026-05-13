-- File: OpenModulePlatform.Web.ContentWebAppModule/Sql/2-initialize-content-webapp.sql
/*
Seeds default values and OMP registration rows for the Content Web App module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-content-webapp.sql
*/
USE [OpenModulePlatform];
GO

DECLARE @InstanceId uniqueidentifier;
DECLARE @InstanceTemplateId int;
DECLARE @SampleHostId uniqueidentifier;
DECLARE @SampleTemplateHostId int;
DECLARE @PortalAdminsRoleId int;
DECLARE @ContentModuleId int;
DECLARE @ContentModuleInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111231';
DECLARE @ContentTemplateModuleInstanceId int;
DECLARE @ContentAppId int;
DECLARE @ContentAppInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111232';
DECLARE @SeedHomeContentId uniqueidentifier = '11111111-1111-1111-1111-111111111233';
DECLARE @SeedModuleStatusContentId uniqueidentifier = '11111111-1111-1111-1111-111111111234';
DECLARE @HomeContentId uniqueidentifier;
DECLARE @ContentViewPermissionId int;
DECLARE @ContentManagePermissionId int;

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

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'ContentWebAppModule.Manage')
    INSERT INTO omp.Permissions(Name, Description)
    VALUES(N'ContentWebAppModule.Manage', N'Create and edit Content Web App pages');

SELECT @ContentViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ContentWebAppModule.View';
SELECT @ContentManagePermissionId = PermissionId FROM omp.Permissions WHERE Name = N'ContentWebAppModule.Manage';

IF @ContentViewPermissionId IS NOT NULL
BEGIN
    UPDATE omp.Permissions
    SET Description = N'Legacy broad read permission. Content read access is page-level through omp_content.content_role_access.'
    WHERE PermissionId = @ContentViewPermissionId;

    DELETE FROM omp.RolePermissions
    WHERE PermissionId = @ContentViewPermissionId;
END

IF EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'content_webapp')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'Content Web App',
        ModuleType = N'WebAppModule',
        SchemaName = N'omp_content',
        Description = N'First-party OMP module for database-backed HTML, Markdown, and server report content pages',
        IsEnabled = 1,
        SortOrder = 330,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleKey = N'content_webapp';
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
        N'content_webapp',
        N'Content Web App',
        N'WebAppModule',
        N'omp_content',
        N'First-party OMP module for database-backed HTML, Markdown, and server report content pages',
        1,
        330);
END

SELECT @ContentModuleId = ModuleId FROM omp.Modules WHERE ModuleKey = N'content_webapp';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @ContentModuleId AND AppKey = N'content_webapp_webapp')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'Content',
        AppType = N'WebApp',
        Description = N'Web app definition for database-backed OMP content pages',
        IsEnabled = 1,
        SortOrder = 330,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @ContentModuleId AND AppKey = N'content_webapp_webapp';
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
        @ContentModuleId,
        N'content_webapp_webapp',
        N'Content',
        N'WebApp',
        N'Web app definition for database-backed OMP content pages',
        1,
        330);
END

SELECT @ContentAppId = AppId FROM omp.Apps WHERE ModuleId = @ContentModuleId AND AppKey = N'content_webapp_webapp';

DELETE ap
FROM omp.AppPermissions ap
WHERE ap.AppId = @ContentAppId
  AND ap.PermissionId IN (@ContentViewPermissionId, @ContentManagePermissionId);

IF @PortalAdminsRoleId IS NOT NULL
   AND @ContentManagePermissionId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePermissions
       WHERE RoleId = @PortalAdminsRoleId
         AND PermissionId = @ContentManagePermissionId
   )
    INSERT INTO omp.RolePermissions(RoleId, PermissionId)
    VALUES(@PortalAdminsRoleId, @ContentManagePermissionId);

IF NOT EXISTS (SELECT 1 FROM omp.ModuleInstances WHERE ModuleInstanceId = @ContentModuleInstanceId)
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
        @ContentModuleInstanceId,
        @InstanceId,
        @ContentModuleId,
        N'content_webapp',
        N'Content Web App',
        N'Database-backed content page module instance for the default OMP instance',
        1,
        330);
END
ELSE
BEGIN
    UPDATE omp.ModuleInstances
    SET InstanceId = @InstanceId,
        ModuleId = @ContentModuleId,
        ModuleInstanceKey = N'content_webapp',
        DisplayName = N'Content Web App',
        Description = N'Database-backed content page module instance for the default OMP instance',
        IsEnabled = 1,
        SortOrder = 330,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleInstanceId = @ContentModuleInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateModuleInstances
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'content_webapp'
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
        @ContentModuleId,
        N'content_webapp',
        N'Content Web App',
        N'Database-backed content page module instance in the default template',
        330);
END
ELSE
BEGIN
    UPDATE omp.InstanceTemplateModuleInstances
    SET ModuleId = @ContentModuleId,
        DisplayName = N'Content Web App',
        Description = N'Database-backed content page module instance in the default template',
        SortOrder = 330
    WHERE InstanceTemplateId = @InstanceTemplateId
      AND ModuleInstanceKey = N'content_webapp';
END

SELECT @ContentTemplateModuleInstanceId = InstanceTemplateModuleInstanceId
FROM omp.InstanceTemplateModuleInstances
WHERE InstanceTemplateId = @InstanceTemplateId
  AND ModuleInstanceKey = N'content_webapp';

IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = @ContentAppInstanceId)
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
        @ContentAppInstanceId,
        @ContentModuleInstanceId,
        @SampleHostId,
        @ContentAppId,
        N'content_webapp_webapp',
        N'Content',
        N'Primary web app instance for database-backed OMP content pages',
        N'content',
        N'content-webapp',
        1,
        1,
        1,
        330);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @ContentModuleInstanceId,
        HostId = @SampleHostId,
        AppId = @ContentAppId,
        AppInstanceKey = N'content_webapp_webapp',
        DisplayName = N'Content',
        Description = N'Primary web app instance for database-backed OMP content pages',
        RoutePath = N'content',
        InstallationName = N'content-webapp',
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 330,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @ContentAppInstanceId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances
    WHERE InstanceTemplateModuleInstanceId = @ContentTemplateModuleInstanceId
      AND AppInstanceKey = N'content_webapp_webapp'
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
        @ContentTemplateModuleInstanceId,
        @SampleTemplateHostId,
        @ContentAppId,
        N'content_webapp_webapp',
        N'Content',
        N'Primary web app instance for database-backed OMP content pages',
        N'content',
        N'content-webapp',
        1,
        330);
END
ELSE
BEGIN
    UPDATE omp.InstanceTemplateAppInstances
    SET InstanceTemplateHostId = @SampleTemplateHostId,
        AppId = @ContentAppId,
        DisplayName = N'Content',
        Description = N'Primary web app instance for database-backed OMP content pages',
        RoutePath = N'content',
        InstallationName = N'content-webapp',
        DesiredState = 1,
        SortOrder = 330
    WHERE InstanceTemplateModuleInstanceId = @ContentTemplateModuleInstanceId
      AND AppInstanceKey = N'content_webapp_webapp';
END

SELECT @HomeContentId = content_id
FROM omp_content.contents
WHERE app_instance_id = @ContentAppInstanceId
  AND slug = N'home';

IF @HomeContentId IS NOT NULL
BEGIN
    UPDATE omp_content.contents
    SET is_enabled = 1,
        sort_order = COALESCE(sort_order, 10),
        updated_by = N'install-script',
        updated_at = SYSUTCDATETIME()
    WHERE content_id = @HomeContentId;
END
ELSE IF EXISTS
(
    SELECT 1
    FROM omp_content.contents
    WHERE content_id = @SeedHomeContentId
)
BEGIN
    SET @HomeContentId = @SeedHomeContentId;

    UPDATE omp_content.contents
    SET app_instance_id = @ContentAppInstanceId,
        slug = N'home',
        is_enabled = 1,
        sort_order = COALESCE(sort_order, 10),
        updated_by = N'install-script',
        updated_at = SYSUTCDATETIME()
    WHERE content_id = @HomeContentId;
END
ELSE
BEGIN
    INSERT INTO omp_content.contents(
        content_id,
        app_instance_id,
        slug,
        title,
        content_type,
        body,
        is_enabled,
        sort_order,
        created_by,
        updated_by)
    VALUES(
        @SeedHomeContentId,
        @ContentAppInstanceId,
        N'home',
        N'Content home',
        N'markdown',
        N'# Content home' + CHAR(13) + CHAR(10) + CHAR(13) + CHAR(10) + N'This page is managed by the Content Web App module.',
        1,
        10,
        N'install-script',
        N'install-script');

    SET @HomeContentId = @SeedHomeContentId;
END

IF NOT EXISTS
(
    SELECT 1
    FROM omp_content.contents
    WHERE app_instance_id = @ContentAppInstanceId
      AND slug = N'module-status'
)
BEGIN
    INSERT INTO omp_content.contents(
        content_id,
        app_instance_id,
        slug,
        title,
        content_type,
        body,
        server_report_key,
        is_enabled,
        sort_order,
        created_by,
        updated_by)
    VALUES(
        @SeedModuleStatusContentId,
        @ContentAppInstanceId,
        N'module-status',
        N'Module status',
        N'server_report',
        N'',
        N'module-status',
        1,
        20,
        N'install-script',
        N'install-script');
END

IF @PortalAdminsRoleId IS NOT NULL
BEGIN
    INSERT INTO omp_content.content_role_access(content_id, role_id, can_read, can_write)
    SELECT c.content_id,
           @PortalAdminsRoleId,
           1,
           1
    FROM omp_content.contents c
    WHERE c.app_instance_id = @ContentAppInstanceId
      AND NOT EXISTS
      (
          SELECT 1
          FROM omp_content.content_role_access a
          WHERE a.content_id = c.content_id
            AND a.role_id = @PortalAdminsRoleId
      );
END
GO
