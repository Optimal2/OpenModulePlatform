-- File: sql/SQL_Install_OpenModulePlatform.sql
/*
OpenModulePlatform core install script.

This script creates the neutral OMP core schema and seeds a minimal default instance,
default templates, default host, and the OMP Portal module/app registration.
*/
USE [OpenModulePlatform];
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'omp')
    EXEC('CREATE SCHEMA [omp]');
GO

-------------------------------------------------------------------------------
-- RBAC
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.Permissions', N'U') IS NULL
BEGIN
    CREATE TABLE omp.Permissions
    (
        PermissionId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Name nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Permissions_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_Permissions_Name UNIQUE(Name)
    );
END
GO

IF OBJECT_ID(N'omp.Roles', N'U') IS NULL
BEGIN
    CREATE TABLE omp.Roles
    (
        RoleId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Name nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Roles_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_Roles_Name UNIQUE(Name)
    );
END
GO

IF OBJECT_ID(N'omp.RolePermissions', N'U') IS NULL
BEGIN
    CREATE TABLE omp.RolePermissions
    (
        RoleId int NOT NULL,
        PermissionId int NOT NULL,
        CONSTRAINT PK_omp_RolePermissions PRIMARY KEY(RoleId, PermissionId),
        CONSTRAINT FK_omp_RolePermissions_Role FOREIGN KEY(RoleId) REFERENCES omp.Roles(RoleId),
        CONSTRAINT FK_omp_RolePermissions_Permission FOREIGN KEY(PermissionId) REFERENCES omp.Permissions(PermissionId)
    );
END
GO

IF OBJECT_ID(N'omp.RolePrincipals', N'U') IS NULL
BEGIN
    CREATE TABLE omp.RolePrincipals
    (
        RoleId int NOT NULL,
        PrincipalType nvarchar(50) NOT NULL,
        Principal nvarchar(256) NOT NULL,
        CONSTRAINT PK_omp_RolePrincipals PRIMARY KEY(RoleId, PrincipalType, Principal),
        CONSTRAINT FK_omp_RolePrincipals_Role FOREIGN KEY(RoleId) REFERENCES omp.Roles(RoleId)
    );
END
GO

IF OBJECT_ID(N'omp.AuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE omp.AuditLog
    (
        AuditLogId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Actor nvarchar(256) NOT NULL,
        Action nvarchar(200) NOT NULL,
        TargetType nvarchar(100) NOT NULL,
        TargetId nvarchar(200) NOT NULL,
        BeforeJson nvarchar(max) NULL,
        AfterJson nvarchar(max) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_AuditLog_CreatedUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

-------------------------------------------------------------------------------
-- Structural model
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.Instances', N'U') IS NULL
BEGIN
    CREATE TABLE omp.Instances
    (
        InstanceId uniqueidentifier NOT NULL CONSTRAINT PK_omp_Instances PRIMARY KEY,
        InstanceKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_Instances_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Instances_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Instances_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_Instances_InstanceKey UNIQUE(InstanceKey)
    );
END
GO

IF OBJECT_ID(N'omp.Modules', N'U') IS NULL
BEGIN
    CREATE TABLE omp.Modules
    (
        ModuleId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        InstanceId uniqueidentifier NOT NULL,
        ModuleKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        ModuleType nvarchar(50) NOT NULL,
        SchemaName nvarchar(128) NOT NULL,
        BasePath nvarchar(128) NULL,
        Description nvarchar(500) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_Modules_IsEnabled DEFAULT(1),
        SortOrder int NOT NULL CONSTRAINT DF_omp_Modules_SortOrder DEFAULT(0),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Modules_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Modules_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_Modules_Instance FOREIGN KEY(InstanceId) REFERENCES omp.Instances(InstanceId),
        CONSTRAINT UQ_omp_Modules_Instance_ModuleKey UNIQUE(InstanceId, ModuleKey)
    );
END
GO

IF OBJECT_ID(N'omp.Apps', N'U') IS NULL
BEGIN
    CREATE TABLE omp.Apps
    (
        AppId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ModuleId int NOT NULL,
        AppKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        AppType nvarchar(50) NOT NULL,
        RouteBasePath nvarchar(128) NULL,
        Description nvarchar(500) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_Apps_IsEnabled DEFAULT(1),
        SortOrder int NOT NULL CONSTRAINT DF_omp_Apps_SortOrder DEFAULT(0),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Apps_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Apps_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_Apps_Module FOREIGN KEY(ModuleId) REFERENCES omp.Modules(ModuleId),
        CONSTRAINT UQ_omp_Apps_Module_AppKey UNIQUE(ModuleId, AppKey)
    );
END
GO

IF OBJECT_ID(N'omp.AppPermissions', N'U') IS NULL
BEGIN
    CREATE TABLE omp.AppPermissions
    (
        AppId int NOT NULL,
        PermissionId int NOT NULL,
        RequireAll bit NOT NULL CONSTRAINT DF_omp_AppPermissions_RequireAll DEFAULT(0),
        CONSTRAINT PK_omp_AppPermissions PRIMARY KEY(AppId, PermissionId),
        CONSTRAINT FK_omp_AppPermissions_App FOREIGN KEY(AppId) REFERENCES omp.Apps(AppId),
        CONSTRAINT FK_omp_AppPermissions_Permission FOREIGN KEY(PermissionId) REFERENCES omp.Permissions(PermissionId)
    );
END
GO

IF OBJECT_ID(N'omp.Artifacts', N'U') IS NULL
BEGIN
    CREATE TABLE omp.Artifacts
    (
        ArtifactId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        AppId int NOT NULL,
        Version nvarchar(50) NOT NULL,
        PackageType nvarchar(50) NOT NULL,
        TargetName nvarchar(100) NULL,
        RelativePath nvarchar(400) NULL,
        Sha256 nvarchar(128) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_Artifacts_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Artifacts_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Artifacts_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_Artifacts_App FOREIGN KEY(AppId) REFERENCES omp.Apps(AppId)
    );
END
GO

-------------------------------------------------------------------------------
-- Operational model
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.InstanceTemplates', N'U') IS NULL
BEGIN
    CREATE TABLE omp.InstanceTemplates
    (
        InstanceTemplateId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        TemplateKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_InstanceTemplates_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_InstanceTemplates_TemplateKey UNIQUE(TemplateKey)
    );
END
GO

IF OBJECT_ID(N'omp.Hosts', N'U') IS NULL
BEGIN
    CREATE TABLE omp.Hosts
    (
        HostId uniqueidentifier NOT NULL CONSTRAINT PK_omp_Hosts PRIMARY KEY,
        InstanceId uniqueidentifier NOT NULL,
        Hostname nvarchar(128) NOT NULL,
        DisplayName nvarchar(200) NULL,
        Environment nvarchar(100) NULL,
        OsFamily nvarchar(50) NULL,
        OsVersion nvarchar(100) NULL,
        Architecture nvarchar(50) NULL,
        ExpectedLogin nvarchar(256) NOT NULL,
        ExpectedHostName nvarchar(128) NULL,
        ExpectedClientIp nvarchar(64) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_Hosts_IsEnabled DEFAULT(1),
        LastSeenUtc datetime2(3) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Hosts_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Hosts_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_Hosts_Instance FOREIGN KEY(InstanceId) REFERENCES omp.Instances(InstanceId),
        CONSTRAINT UQ_omp_Hosts_Instance_Hostname UNIQUE(InstanceId, Hostname)
    );
END
GO

IF OBJECT_ID(N'omp.HostTemplates', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostTemplates
    (
        HostTemplateId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        TemplateKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_HostTemplates_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostTemplates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostTemplates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_HostTemplates_TemplateKey UNIQUE(TemplateKey)
    );
END
GO

IF OBJECT_ID(N'omp.HostDeploymentAssignments', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostDeploymentAssignments
    (
        HostDeploymentAssignmentId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        HostId uniqueidentifier NOT NULL,
        HostTemplateId int NOT NULL,
        AssignedBy nvarchar(256) NULL,
        AssignedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostDeploymentAssignments_AssignedUtc DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_omp_HostDeploymentAssignments_IsActive DEFAULT(1),
        CONSTRAINT FK_omp_HostDeploymentAssignments_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_HostDeploymentAssignments_HostTemplate FOREIGN KEY(HostTemplateId) REFERENCES omp.HostTemplates(HostTemplateId)
    );
END
GO

IF OBJECT_ID(N'omp.HostDeployments', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostDeployments
    (
        HostDeploymentId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        HostId uniqueidentifier NOT NULL,
        HostTemplateId int NULL,
        RequestedBy nvarchar(256) NULL,
        RequestedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostDeployments_RequestedUtc DEFAULT SYSUTCDATETIME(),
        StartedUtc datetime2(3) NULL,
        CompletedUtc datetime2(3) NULL,
        Status tinyint NOT NULL CONSTRAINT DF_omp_HostDeployments_Status DEFAULT(0),
        OutcomeMessage nvarchar(max) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostDeployments_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostDeployments_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_HostDeployments_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_HostDeployments_HostTemplate FOREIGN KEY(HostTemplateId) REFERENCES omp.HostTemplates(HostTemplateId)
    );
END
GO

IF OBJECT_ID(N'omp.HostInstallations', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostInstallations
    (
        HostInstallationId uniqueidentifier NOT NULL CONSTRAINT PK_omp_HostInstallations PRIMARY KEY,
        HostId uniqueidentifier NOT NULL,
        AppId int NOT NULL,
        InstallationName nvarchar(150) NOT NULL,
        ArtifactId int NULL,
        ConfigId int NULL,
        IsAllowed bit NOT NULL CONSTRAINT DF_omp_HostInstallations_IsAllowed DEFAULT(1),
        DesiredState tinyint NOT NULL CONSTRAINT DF_omp_HostInstallations_DesiredState DEFAULT(1),
        VerificationStatus tinyint NOT NULL CONSTRAINT DF_omp_HostInstallations_VerificationStatus DEFAULT(0),
        LastSeenUtc datetime2(3) NULL,
        LastLogin nvarchar(256) NULL,
        LastClientHostName nvarchar(128) NULL,
        LastClientIp nvarchar(64) NULL,
        LastVerifiedUtc datetime2(3) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostInstallations_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostInstallations_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_HostInstallations_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_HostInstallations_App FOREIGN KEY(AppId) REFERENCES omp.Apps(AppId),
        CONSTRAINT FK_omp_HostInstallations_Artifact FOREIGN KEY(ArtifactId) REFERENCES omp.Artifacts(ArtifactId),
        CONSTRAINT UQ_omp_HostInstallations_Host_App_Installation UNIQUE(HostId, AppId, InstallationName)
    );
END
GO

-------------------------------------------------------------------------------
-- Seed baseline instance and templates
-------------------------------------------------------------------------------
DECLARE @DefaultInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111111';
DECLARE @DefaultHostId uniqueidentifier = '11111111-1111-1111-1111-111111111121';
DECLARE @PortalModuleId int;
DECLARE @PortalAppId int;
DECLARE @PortalViewPermissionId int;
DECLARE @PortalAdminPermissionId int;
DECLARE @PortalAdminsRoleId int;
DECLARE @DefaultHostTemplateId int;

IF NOT EXISTS (SELECT 1 FROM omp.Instances WHERE InstanceId = @DefaultInstanceId)
BEGIN
    INSERT INTO omp.Instances(InstanceId, InstanceKey, DisplayName, Description)
    VALUES(@DefaultInstanceId, N'default', N'Default Instance', N'Default OMP instance seeded by the install script');
END
ELSE
BEGIN
    UPDATE omp.Instances
    SET InstanceKey = N'default',
        DisplayName = N'Default Instance',
        Description = N'Default OMP instance seeded by the install script',
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceId = @DefaultInstanceId;
END

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

SELECT @DefaultHostTemplateId = HostTemplateId FROM omp.HostTemplates WHERE TemplateKey = N'default-host';

IF NOT EXISTS (SELECT 1 FROM omp.Hosts WHERE HostId = @DefaultHostId)
BEGIN
    INSERT INTO omp.Hosts(HostId, InstanceId, Hostname, DisplayName, Environment, OsFamily, Architecture, ExpectedLogin, ExpectedHostName)
    VALUES(@DefaultHostId, @DefaultInstanceId, N'default-host', N'Default Host', N'Development', N'Windows', N'x64', N'DOMAIN\service-user', N'default-host');
END
ELSE
BEGIN
    UPDATE omp.Hosts
    SET InstanceId = @DefaultInstanceId,
        Hostname = N'default-host',
        DisplayName = N'Default Host',
        Environment = N'Development',
        OsFamily = N'Windows',
        Architecture = N'x64',
        ExpectedLogin = N'DOMAIN\service-user',
        ExpectedHostName = N'default-host',
        IsEnabled = 1,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE HostId = @DefaultHostId;
END

IF NOT EXISTS (SELECT 1 FROM omp.HostDeploymentAssignments WHERE HostId = @DefaultHostId AND HostTemplateId = @DefaultHostTemplateId)
BEGIN
    INSERT INTO omp.HostDeploymentAssignments(HostId, HostTemplateId, AssignedBy, IsActive)
    VALUES(@DefaultHostId, @DefaultHostTemplateId, SUSER_SNAME(), 1);
END

-------------------------------------------------------------------------------
-- Seed OMP Portal module and app
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

IF EXISTS (SELECT 1 FROM omp.Modules WHERE InstanceId = @DefaultInstanceId AND ModuleKey = N'omp_portal')
BEGIN
    UPDATE omp.Modules
    SET DisplayName = N'OMP Portal',
        ModuleType = N'PortalModule',
        SchemaName = N'omp',
        BasePath = N'',
        Description = N'Core portal module for OpenModulePlatform',
        IsEnabled = 1,
        SortOrder = 100,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE InstanceId = @DefaultInstanceId AND ModuleKey = N'omp_portal';
END
ELSE
BEGIN
    INSERT INTO omp.Modules(InstanceId, ModuleKey, DisplayName, ModuleType, SchemaName, BasePath, Description, IsEnabled, SortOrder)
    VALUES(@DefaultInstanceId, N'omp_portal', N'OMP Portal', N'PortalModule', N'omp', N'', N'Core portal module for OpenModulePlatform', 1, 100);
END

SELECT @PortalModuleId = ModuleId FROM omp.Modules WHERE InstanceId = @DefaultInstanceId AND ModuleKey = N'omp_portal';

IF EXISTS (SELECT 1 FROM omp.Apps WHERE ModuleId = @PortalModuleId AND AppKey = N'omp_portal')
BEGIN
    UPDATE omp.Apps
    SET DisplayName = N'OMP Portal',
        AppType = N'Portal',
        RouteBasePath = N'',
        Description = N'Primary OMP portal web application',
        IsEnabled = 1,
        SortOrder = 100,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE ModuleId = @PortalModuleId AND AppKey = N'omp_portal';
END
ELSE
BEGIN
    INSERT INTO omp.Apps(ModuleId, AppKey, DisplayName, AppType, RouteBasePath, Description, IsEnabled, SortOrder)
    VALUES(@PortalModuleId, N'omp_portal', N'OMP Portal', N'Portal', N'', N'Primary OMP portal web application', 1, 100);
END

SELECT @PortalAppId = AppId FROM omp.Apps WHERE ModuleId = @PortalModuleId AND AppKey = N'omp_portal';

IF NOT EXISTS (SELECT 1 FROM omp.AppPermissions WHERE AppId = @PortalAppId AND PermissionId = @PortalViewPermissionId)
    INSERT INTO omp.AppPermissions(AppId, PermissionId, RequireAll) VALUES(@PortalAppId, @PortalViewPermissionId, 0);
GO
