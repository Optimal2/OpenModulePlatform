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
DECLARE @DefaultHostId uniqueidentifier = '11111111-1111-1111-1111-111111111121';
DECLARE @DefaultInstanceTemplateId int;
DECLARE @DefaultHostTemplateId int;
DECLARE @DefaultTemplateHostId int;
DECLARE @PortalAdminsRoleId int;
DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = N'REPLACE_ME\UserOrGroup';

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
