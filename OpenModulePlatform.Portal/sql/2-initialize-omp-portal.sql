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
DECLARE @DefaultTemplateHostId int;
DECLARE @DefaultTemplatePortalModuleInstanceId int;
DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = N'REPLACE_ME\UserOrGroup';

SELECT @DefaultInstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceId = @DefaultInstanceId;

IF @DefaultInstanceTemplateId IS NULL
    THROW 50000, 'Default OMP instance not found. Run the core SQL setup/init scripts first.', 1;

SELECT @DefaultTemplateHostId = InstanceTemplateHostId
FROM omp.InstanceTemplateHosts
WHERE InstanceTemplateId = @DefaultInstanceTemplateId
  AND HostKey = N'sample-host';

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'OMP.Portal.View')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'OMP.Portal.View', N'Read access to the OMP Portal');

IF NOT EXISTS (SELECT 1 FROM omp.Permissions WHERE Name = N'OMP.Portal.Admin')
    INSERT INTO omp.Permissions(Name, Description) VALUES(N'OMP.Portal.Admin', N'Administrative access to the OMP Portal');

SELECT @PortalViewPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'OMP.Portal.View';
SELECT @PortalAdminPermissionId = PermissionId FROM omp.Permissions WHERE Name = N'OMP.Portal.Admin';
SELECT @PortalAdminsRoleId = RoleId FROM omp.Roles WHERE Name = N'PortalAdmins';

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM omp.RolePermissions WHERE RoleId = @PortalAdminsRoleId AND PermissionId = @PortalViewPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId) VALUES(@PortalAdminsRoleId, @PortalViewPermissionId);

IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM omp.RolePermissions WHERE RoleId = @PortalAdminsRoleId AND PermissionId = @PortalAdminPermissionId)
    INSERT INTO omp.RolePermissions(RoleId, PermissionId) VALUES(@PortalAdminsRoleId, @PortalAdminPermissionId);

/*
Bootstrap portal administrator row.

Replace the placeholder principal below before you try to sign in to OMP Portal.
Examples:
- DOMAIN\your.user
- DOMAIN\OMP Portal Admins
*/
IF @PortalAdminsRoleId IS NOT NULL AND EXISTS (SELECT 1 FROM omp.RolePrincipals WHERE RoleId = @PortalAdminsRoleId AND PrincipalType = N'User')
BEGIN
    UPDATE omp.RolePrincipals
    SET Principal = @BootstrapPortalAdminPrincipal
    WHERE RoleId = @PortalAdminsRoleId AND PrincipalType = N'User';
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
