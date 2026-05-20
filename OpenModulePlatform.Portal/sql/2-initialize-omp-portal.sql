-- File: OpenModulePlatform.Portal/sql/2-initialize-omp-portal.sql
-- IMPORTANT: run scripts/manage-local-install.ps1 with
-- -BootstrapPortalAdminPrincipal for automated local installs, or replace the
-- bootstrap literal below manually with a single-quote-escaped Windows principal.
/*
Seeds default values and OMP registration rows for the OMP Portal.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-omp-portal.sql
- Set @BootstrapPortalAdminPrincipal to the Windows user or group that should
  receive the initial PortalAdmins role. Prefer scripts/manage-local-install.ps1
  for local installs because it escapes the value before running sqlcmd.
*/
USE [OpenModulePlatform];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @DefaultInstanceId uniqueidentifier;
DECLARE @DefaultPortalModuleInstanceId uniqueidentifier;
DECLARE @DefaultPortalAppInstanceId uniqueidentifier;
DECLARE @PortalModuleId int;
DECLARE @PortalAppId int;
DECLARE @PortalArtifactId int;
DECLARE @PortalViewPermissionId int;
DECLARE @PortalAdminPermissionId int;
DECLARE @PortalAdminsRoleId int;
DECLARE @DefaultInstanceTemplateId int;
DECLARE @DefaultTemplatePortalModuleInstanceId int;
DECLARE @ArtifactVersion nvarchar(50) = N'0.3.3';
DECLARE @BootstrapPortalAdminPrincipal nvarchar(256) = N'__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__';

IF @BootstrapPortalAdminPrincipal = N'__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__'
BEGIN
    THROW 51000, 'Set @BootstrapPortalAdminPrincipal before running this script, or use scripts/manage-local-install.ps1 -BootstrapPortalAdminPrincipal "DOMAIN\User" to let the local installer safely patch it. The parameter accepts multiple principals as an array.', 1;
END

SELECT @DefaultInstanceId = InstanceId,
       @DefaultInstanceTemplateId = InstanceTemplateId
FROM omp.Instances
WHERE InstanceKey = N'default';

IF @DefaultInstanceTemplateId IS NULL
    THROW 50000, 'Default OMP instance not found. Run the core SQL setup/init scripts first.', 1;

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
Bootstrap portal administrator rows.

Set @BootstrapPortalAdminPrincipal before you try to sign in to OMP Portal.
Examples:
- DOMAIN\your.user
- DOMAIN\OMP Portal Admins

The core initialization script owns the initial role principal insert. This
script only ensures the configured principal is present and intentionally does
not overwrite existing PortalAdmins principals.

Do not pass the principal through sqlcmd -v. SQLCMD variables are textual
substitution before T-SQL parsing, so values containing SQL metacharacters cannot
be safely validated inside this script after substitution. Use the PowerShell
installer for automated local runs, or manually escape single quotes in the
literal above.
*/
IF @PortalAdminsRoleId IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM omp.RolePrincipals
       WHERE RoleId = @PortalAdminsRoleId
         AND PrincipalType = N'ADUser'
         AND Principal = @BootstrapPortalAdminPrincipal
   )
BEGIN
    INSERT INTO omp.RolePrincipals(RoleId, PrincipalType, Principal)
    VALUES(@PortalAdminsRoleId, N'ADUser', @BootstrapPortalAdminPrincipal);
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

MERGE omp.Artifacts AS target
USING
(
    SELECT @PortalAppId AS AppId,
           @ArtifactVersion AS Version,
           N'web-app' AS PackageType,
           N'omp-portal' AS TargetName,
           N'omp-portal/web/' + @ArtifactVersion AS RelativePath,
           CAST(1 AS bit) AS IsEnabled
) AS source
ON target.AppId = source.AppId
AND target.Version = source.Version
AND target.PackageType = source.PackageType
AND target.TargetName = source.TargetName
WHEN MATCHED THEN
    UPDATE SET RelativePath = source.RelativePath,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (AppId, Version, PackageType, TargetName, RelativePath, IsEnabled)
    VALUES(source.AppId, source.Version, source.PackageType, source.TargetName, source.RelativePath, source.IsEnabled);

SELECT @PortalArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = @PortalAppId
  AND Version = @ArtifactVersion
  AND PackageType = N'web-app'
  AND TargetName = N'omp-portal';

MERGE omp.ArtifactConfigurationFiles AS target
USING
(
    SELECT @PortalArtifactId AS ArtifactId,
           N'appsettings.json' AS RelativePath,
           N'{
  "Portal": {
    "Title": "OMP Portal",
    "DefaultCulture": "sv-SE",
    "SupportedCultures": [ "sv-SE", "en-US" ],
    "PortalTopBar": {
      "Enabled": true,
      "PortalBaseUrl": "/"
    },
    "TopbarShortcuts": {
      "Enabled": true,
      "AllModules": "m",
      "Favorites": "f"
    },
    "AllowAnonymous": false,
    "UseForwardedHeaders": false,
    "PermissionMode": "Any"
  },
  "ConnectionStrings": {
    "OmpDb": "{{Omp.Json.ConnectionStrings.OmpDb}}"
  },
  "OmpAuth": {
    "CookieName": ".OpenModulePlatform.Auth",
    "LoginPath": "/auth/login",
    "LogoutPath": "/auth/logout",
    "AccessDeniedPath": "/status/403",
    "ApplicationName": "OpenModulePlatform",
    "DataProtectionKeyPath": "{{Omp.Json.HostAgent.WebAppDataProtectionKeyPath}}"
  },
  "ArtifactUpload": {
    "ArtifactStoreRoot": "{{Omp.Json.HostAgent.CentralArtifactRoot}}",
    "MaxUploadBytes": 536870912
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}' AS FileContent,
           CAST(1 AS bit) AS IsEnabled
) AS source
ON target.ArtifactId = source.ArtifactId
AND target.RelativePath = source.RelativePath
WHEN MATCHED THEN
    UPDATE SET FileContent = source.FileContent,
               IsEnabled = source.IsEnabled,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (ArtifactId, RelativePath, FileContent, IsEnabled)
    VALUES(source.ArtifactId, source.RelativePath, source.FileContent, source.IsEnabled);

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @PortalAppId AND PermissionId = @PortalViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@PortalAppId, @PortalViewPermissionId, 0);

SELECT @DefaultPortalModuleInstanceId = ModuleInstanceId
FROM omp.ModuleInstances
WHERE InstanceId = @DefaultInstanceId
  AND ModuleInstanceKey = N'omp_portal';

IF @DefaultPortalModuleInstanceId IS NULL
BEGIN
    SET @DefaultPortalModuleInstanceId = NEWID();

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

SELECT @DefaultPortalAppInstanceId = AppInstanceId
FROM omp.AppInstances
WHERE ModuleInstanceId = @DefaultPortalModuleInstanceId
  AND AppInstanceKey = N'omp_portal';

IF @DefaultPortalAppInstanceId IS NULL
BEGIN
    SET @DefaultPortalAppInstanceId = NEWID();

    INSERT INTO omp.AppInstances(
        AppInstanceId, ModuleInstanceId, HostId, AppId, AppInstanceKey, DisplayName, Description,
        RoutePath, InstallationName, ArtifactId, IsEnabled, IsAllowed, DesiredState, SortOrder)
    VALUES(
        @DefaultPortalAppInstanceId, @DefaultPortalModuleInstanceId, NULL, @PortalAppId, N'omp_portal', N'OMP Portal',
        N'Primary OMP portal app instance for the default OMP instance', N'', N'portal', @PortalArtifactId, 1, 1, 1, 100);
END
ELSE
BEGIN
    UPDATE omp.AppInstances
    SET ModuleInstanceId = @DefaultPortalModuleInstanceId,
        HostId = NULL,
        AppId = @PortalAppId,
        AppInstanceKey = N'omp_portal',
        DisplayName = N'OMP Portal',
        Description = N'Primary OMP portal app instance for the default OMP instance',
        RoutePath = N'',
        InstallationName = N'portal',
        ArtifactId = @PortalArtifactId,
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
        RoutePath, InstallationName, DesiredArtifactId, DesiredState, SortOrder)
    VALUES(
        @DefaultTemplatePortalModuleInstanceId, NULL, @PortalAppId, N'omp_portal', N'OMP Portal',
        N'Primary OMP portal app instance for the default template', N'', N'portal', @PortalArtifactId, 1, 100);
END

UPDATE omp.InstanceTemplateAppInstances
SET InstanceTemplateHostId = NULL,
    DesiredArtifactId = @PortalArtifactId
WHERE InstanceTemplateModuleInstanceId = @DefaultTemplatePortalModuleInstanceId
  AND AppInstanceKey = N'omp_portal';
GO
