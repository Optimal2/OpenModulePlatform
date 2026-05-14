-- File: sql/1-setup-openmoduleplatform.sql
/*
OpenModulePlatform core setup script.

Creates the neutral OMP core schema, tables, constraints, and account model that
are required for the platform itself to function. This script creates only
objects under the omp schema.

Run 2-initialize-openmoduleplatform.sql after this script to seed the default
OMP instance, bootstrap RBAC placeholders, and baseline structural rows.
Portal, content, iframe, and example modules are installed separately from their
own module sql folders.
*/
USE [OpenModulePlatform];
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_PADDING ON;
GO
SET ANSI_WARNINGS ON;
GO
SET CONCAT_NULL_YIELDS_NULL ON;
GO
SET ARITHABORT ON;
GO
SET NUMERIC_ROUNDABORT OFF;
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

IF OBJECT_ID(N'omp.RolePrincipals', N'U') IS NOT NULL
BEGIN
    -- Schema setup owns stored data migrations. The initialize script repeats
    -- this small legacy cleanup for standalone initialization reruns.
    DELETE legacy
    FROM omp.RolePrincipals legacy
    WHERE legacy.PrincipalType = N'User'
      AND EXISTS
      (
          SELECT 1
          FROM omp.RolePrincipals currentPrincipal
          WHERE currentPrincipal.RoleId = legacy.RoleId
            AND currentPrincipal.PrincipalType = N'ADUser'
            AND currentPrincipal.Principal = legacy.Principal
      );

    UPDATE omp.RolePrincipals
    SET PrincipalType = N'ADUser'
    WHERE PrincipalType = N'User';
END
GO

IF OBJECT_ID(N'omp.RolePrincipals', N'U') IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM sys.check_constraints
       WHERE name = N'CK_omp_RolePrincipals_NoBootstrapPlaceholders'
         AND parent_object_id = OBJECT_ID(N'omp.RolePrincipals')
   )
BEGIN
    -- Defense in depth for deployment mistakes. The bootstrap scripts should
    -- replace these source-controlled placeholders before execution, and this
    -- constraint prevents them from being persisted if another path bypasses
    -- script validation.
    ALTER TABLE omp.RolePrincipals WITH CHECK
    ADD CONSTRAINT CK_omp_RolePrincipals_NoBootstrapPlaceholders
    CHECK
    (
        Principal NOT IN
        (
            N'__BOOTSTRAP_PORTAL_ADMIN_PRINCIPAL__',
            N'REPLACE_ME\UserOrGroup'
        )
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
-- Operational template model
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
        InstanceTemplateId int NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_Instances_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Instances_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Instances_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_Instances_InstanceKey UNIQUE(InstanceKey)
    );
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_omp_Instances_InstanceTemplate'
)
BEGIN
    ALTER TABLE omp.Instances
    ADD CONSTRAINT FK_omp_Instances_InstanceTemplate
        FOREIGN KEY(InstanceTemplateId) REFERENCES omp.InstanceTemplates(InstanceTemplateId);
END
GO

IF OBJECT_ID(N'omp.Modules', N'U') IS NULL
BEGIN
    CREATE TABLE omp.Modules
    (
        ModuleId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ModuleKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        ModuleType nvarchar(50) NOT NULL,
        SchemaName nvarchar(128) NOT NULL,
        Description nvarchar(500) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_Modules_IsEnabled DEFAULT(1),
        SortOrder int NOT NULL CONSTRAINT DF_omp_Modules_SortOrder DEFAULT(0),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Modules_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Modules_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_Modules_ModuleKey UNIQUE(ModuleKey)
    );
END
GO

IF OBJECT_ID(N'omp.ModuleInstances', N'U') IS NULL
BEGIN
    CREATE TABLE omp.ModuleInstances
    (
        ModuleInstanceId uniqueidentifier NOT NULL CONSTRAINT PK_omp_ModuleInstances PRIMARY KEY,
        InstanceId uniqueidentifier NOT NULL,
        ModuleId int NOT NULL,
        ModuleInstanceKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_ModuleInstances_IsEnabled DEFAULT(1),
        SortOrder int NOT NULL CONSTRAINT DF_omp_ModuleInstances_SortOrder DEFAULT(0),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleInstances_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleInstances_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_ModuleInstances_Instance FOREIGN KEY(InstanceId) REFERENCES omp.Instances(InstanceId),
        CONSTRAINT FK_omp_ModuleInstances_Module FOREIGN KEY(ModuleId) REFERENCES omp.Modules(ModuleId),
        CONSTRAINT UQ_omp_ModuleInstances_Instance_ModuleInstanceKey UNIQUE(InstanceId, ModuleInstanceKey)
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

IF OBJECT_ID(N'omp.Hosts', N'U') IS NULL
BEGIN
    CREATE TABLE omp.Hosts
    (
        HostId uniqueidentifier NOT NULL CONSTRAINT PK_omp_Hosts PRIMARY KEY,
        InstanceId uniqueidentifier NOT NULL,
        HostKey nvarchar(128) NOT NULL,
        DisplayName nvarchar(200) NULL,
        BaseUrl nvarchar(300) NULL,
        Environment nvarchar(100) NULL,
        OsFamily nvarchar(50) NULL,
        OsVersion nvarchar(100) NULL,
        Architecture nvarchar(50) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_Hosts_IsEnabled DEFAULT(1),
        LastSeenUtc datetime2(3) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Hosts_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Hosts_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_Hosts_Instance FOREIGN KEY(InstanceId) REFERENCES omp.Instances(InstanceId),
        CONSTRAINT UQ_omp_Hosts_Instance_HostKey UNIQUE(InstanceId, HostKey)
    );
END
GO


IF OBJECT_ID(N'omp.AppInstances', N'U') IS NULL
BEGIN
    CREATE TABLE omp.AppInstances
    (
        AppInstanceId uniqueidentifier NOT NULL CONSTRAINT PK_omp_AppInstances PRIMARY KEY,
        ModuleInstanceId uniqueidentifier NOT NULL,
        HostId uniqueidentifier NULL,
        AppId int NOT NULL,
        AppInstanceKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        RoutePath nvarchar(256) NULL,
        PublicUrl nvarchar(500) NULL,
        InstallPath nvarchar(500) NULL,
        InstallationName nvarchar(150) NULL,
        ArtifactId int NULL,
        ConfigId int NULL,
        ExpectedLogin nvarchar(256) NULL,
        ExpectedClientHostName nvarchar(128) NULL,
        ExpectedClientIp nvarchar(64) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_AppInstances_IsEnabled DEFAULT(1),
        IsAllowed bit NOT NULL CONSTRAINT DF_omp_AppInstances_IsAllowed DEFAULT(1),
        DesiredState tinyint NOT NULL CONSTRAINT DF_omp_AppInstances_DesiredState DEFAULT(1),
        VerificationStatus tinyint NOT NULL CONSTRAINT DF_omp_AppInstances_VerificationStatus DEFAULT(0),
        LastSeenUtc datetime2(3) NULL,
        LastLogin nvarchar(256) NULL,
        LastClientHostName nvarchar(128) NULL,
        LastClientIp nvarchar(64) NULL,
        LastVerifiedUtc datetime2(3) NULL,
        SortOrder int NOT NULL CONSTRAINT DF_omp_AppInstances_SortOrder DEFAULT(0),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_AppInstances_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_AppInstances_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_AppInstances_ModuleInstance FOREIGN KEY(ModuleInstanceId) REFERENCES omp.ModuleInstances(ModuleInstanceId),
        CONSTRAINT FK_omp_AppInstances_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_AppInstances_App FOREIGN KEY(AppId) REFERENCES omp.Apps(AppId),
        CONSTRAINT FK_omp_AppInstances_Artifact FOREIGN KEY(ArtifactId) REFERENCES omp.Artifacts(ArtifactId),
        CONSTRAINT UQ_omp_AppInstances_ModuleInstance_AppInstanceKey UNIQUE(ModuleInstanceId, AppInstanceKey)
    );
END
GO

IF OBJECT_ID(N'omp.AppWorkerDefinitions', N'U') IS NULL
BEGIN
    CREATE TABLE omp.AppWorkerDefinitions
    (
        AppId int NOT NULL CONSTRAINT PK_omp_AppWorkerDefinitions PRIMARY KEY,
        RuntimeKind nvarchar(100) NOT NULL,
        WorkerTypeKey nvarchar(200) NOT NULL,
        PluginRelativePath nvarchar(400) NOT NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_AppWorkerDefinitions_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_AppWorkerDefinitions_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_AppWorkerDefinitions_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_AppWorkerDefinitions_App FOREIGN KEY(AppId) REFERENCES omp.Apps(AppId)
    );
END
GO

IF OBJECT_ID(N'omp.AppInstanceRuntimeStates', N'U') IS NULL
BEGIN
    CREATE TABLE omp.AppInstanceRuntimeStates
    (
        AppInstanceId uniqueidentifier NOT NULL CONSTRAINT PK_omp_AppInstanceRuntimeStates PRIMARY KEY,
        RuntimeKind nvarchar(100) NOT NULL,
        WorkerTypeKey nvarchar(200) NOT NULL,
        ObservedState tinyint NOT NULL CONSTRAINT DF_omp_AppInstanceRuntimeStates_ObservedState DEFAULT(0),
        ProcessId int NULL,
        StartedUtc datetime2(3) NULL,
        LastSeenUtc datetime2(3) NULL,
        LastExitUtc datetime2(3) NULL,
        LastExitCode int NULL,
        StatusMessage nvarchar(500) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_AppInstanceRuntimeStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_AppInstanceRuntimeStates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_AppInstanceRuntimeStates_AppInstance FOREIGN KEY(AppInstanceId) REFERENCES omp.AppInstances(AppInstanceId)
    );
END
GO

-------------------------------------------------------------------------------
-- Host-local artifact provisioning and worker process instances
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.HostArtifactRequirements', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostArtifactRequirements
    (
        HostArtifactRequirementId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_omp_HostArtifactRequirements PRIMARY KEY,
        HostId uniqueidentifier NOT NULL,
        ArtifactId int NOT NULL,
        RequirementKey nvarchar(200) NOT NULL,
        DesiredLocalPath nvarchar(500) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_HostArtifactRequirements_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostArtifactRequirements_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostArtifactRequirements_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_HostArtifactRequirements_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_HostArtifactRequirements_Artifact FOREIGN KEY(ArtifactId) REFERENCES omp.Artifacts(ArtifactId),
        CONSTRAINT UQ_omp_HostArtifactRequirements_Host_Requirement UNIQUE(HostId, RequirementKey)
    );
END
GO

IF OBJECT_ID(N'omp.HostArtifactStates', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostArtifactStates
    (
        HostId uniqueidentifier NOT NULL,
        ArtifactId int NOT NULL,
        ProvisioningState tinyint NOT NULL CONSTRAINT DF_omp_HostArtifactStates_ProvisioningState DEFAULT(0),
        LocalPath nvarchar(500) NULL,
        ContentSha256 nvarchar(128) NULL,
        LastCheckedUtc datetime2(3) NULL,
        LastProvisionedUtc datetime2(3) NULL,
        LastError nvarchar(max) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostArtifactStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostArtifactStates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_omp_HostArtifactStates PRIMARY KEY(HostId, ArtifactId),
        CONSTRAINT FK_omp_HostArtifactStates_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_HostArtifactStates_Artifact FOREIGN KEY(ArtifactId) REFERENCES omp.Artifacts(ArtifactId)
    );
END
GO

IF OBJECT_ID(N'omp.WorkerInstances', N'U') IS NULL
BEGIN
    CREATE TABLE omp.WorkerInstances
    (
        WorkerInstanceId uniqueidentifier NOT NULL CONSTRAINT PK_omp_WorkerInstances PRIMARY KEY,
        AppInstanceId uniqueidentifier NOT NULL,
        HostId uniqueidentifier NULL,
        ArtifactId int NULL,
        WorkerInstanceKey nvarchar(150) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        ConfigurationJson nvarchar(max) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_WorkerInstances_IsEnabled DEFAULT(1),
        IsAllowed bit NOT NULL CONSTRAINT DF_omp_WorkerInstances_IsAllowed DEFAULT(1),
        DesiredState tinyint NOT NULL CONSTRAINT DF_omp_WorkerInstances_DesiredState DEFAULT(1),
        SortOrder int NOT NULL CONSTRAINT DF_omp_WorkerInstances_SortOrder DEFAULT(0),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_WorkerInstances_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_WorkerInstances_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_WorkerInstances_AppInstance FOREIGN KEY(AppInstanceId) REFERENCES omp.AppInstances(AppInstanceId),
        CONSTRAINT FK_omp_WorkerInstances_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_WorkerInstances_Artifact FOREIGN KEY(ArtifactId) REFERENCES omp.Artifacts(ArtifactId),
        CONSTRAINT UQ_omp_WorkerInstances_AppInstance_Key UNIQUE(AppInstanceId, WorkerInstanceKey)
    );
END
GO

IF OBJECT_ID(N'omp.WorkerInstanceRuntimeStates', N'U') IS NULL
BEGIN
    CREATE TABLE omp.WorkerInstanceRuntimeStates
    (
        WorkerInstanceId uniqueidentifier NOT NULL CONSTRAINT PK_omp_WorkerInstanceRuntimeStates PRIMARY KEY,
        AppInstanceId uniqueidentifier NOT NULL,
        WorkerInstanceKey nvarchar(150) NULL,
        RuntimeKind nvarchar(100) NOT NULL,
        WorkerTypeKey nvarchar(200) NOT NULL,
        ObservedState tinyint NOT NULL CONSTRAINT DF_omp_WorkerInstanceRuntimeStates_ObservedState DEFAULT(0),
        ProcessId int NULL,
        StartedUtc datetime2(3) NULL,
        LastSeenUtc datetime2(3) NULL,
        LastExitUtc datetime2(3) NULL,
        LastExitCode int NULL,
        StatusMessage nvarchar(500) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_WorkerInstanceRuntimeStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_WorkerInstanceRuntimeStates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_WorkerInstanceRuntimeStates_WorkerInstance FOREIGN KEY(WorkerInstanceId) REFERENCES omp.WorkerInstances(WorkerInstanceId),
        CONSTRAINT FK_omp_WorkerInstanceRuntimeStates_AppInstance FOREIGN KEY(AppInstanceId) REFERENCES omp.AppInstances(AppInstanceId)
    );
END
GO

-------------------------------------------------------------------------------
-- Template topology model
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.InstanceTemplateHosts', N'U') IS NULL
BEGIN
    CREATE TABLE omp.InstanceTemplateHosts
    (
        InstanceTemplateHostId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        InstanceTemplateId int NOT NULL,
        HostTemplateId int NOT NULL,
        HostKey nvarchar(128) NOT NULL,
        DisplayName nvarchar(200) NULL,
        Environment nvarchar(100) NULL,
        SortOrder int NOT NULL CONSTRAINT DF_omp_InstanceTemplateHosts_SortOrder DEFAULT(0),
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_InstanceTemplateHosts_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplateHosts_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplateHosts_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_InstanceTemplateHosts_InstanceTemplate
            FOREIGN KEY(InstanceTemplateId)
            REFERENCES omp.InstanceTemplates(InstanceTemplateId),
        CONSTRAINT FK_omp_InstanceTemplateHosts_HostTemplate
            FOREIGN KEY(HostTemplateId)
            REFERENCES omp.HostTemplates(HostTemplateId),
        CONSTRAINT UQ_omp_InstanceTemplateHosts_Template_HostKey UNIQUE(InstanceTemplateId, HostKey)
    );
END
GO

IF OBJECT_ID(N'omp.InstanceTemplateModuleInstances', N'U') IS NULL
BEGIN
    CREATE TABLE omp.InstanceTemplateModuleInstances
    (
        InstanceTemplateModuleInstanceId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        InstanceTemplateId int NOT NULL,
        ModuleId int NOT NULL,
        ModuleInstanceKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        SortOrder int NOT NULL CONSTRAINT DF_omp_InstanceTemplateModuleInstances_SortOrder DEFAULT(0),
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_InstanceTemplateModuleInstances_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplateModuleInstances_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplateModuleInstances_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_InstanceTemplateModuleInstances_InstanceTemplate
            FOREIGN KEY(InstanceTemplateId)
            REFERENCES omp.InstanceTemplates(InstanceTemplateId),
        CONSTRAINT FK_omp_InstanceTemplateModuleInstances_Module
            FOREIGN KEY(ModuleId)
            REFERENCES omp.Modules(ModuleId),
        CONSTRAINT UQ_omp_InstanceTemplateModuleInstances_Template_ModuleInstanceKey UNIQUE(InstanceTemplateId, ModuleInstanceKey)
    );
END
GO

IF OBJECT_ID(N'omp.InstanceTemplateAppInstances', N'U') IS NULL
BEGIN
    CREATE TABLE omp.InstanceTemplateAppInstances
    (
        InstanceTemplateAppInstanceId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        InstanceTemplateModuleInstanceId int NOT NULL,
        InstanceTemplateHostId int NULL,
        AppId int NOT NULL,
        AppInstanceKey nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(500) NULL,
        RoutePath nvarchar(256) NULL,
        PublicUrl nvarchar(500) NULL,
        InstallPath nvarchar(500) NULL,
        InstallationName nvarchar(150) NULL,
        DesiredArtifactId int NULL,
        DesiredConfigId int NULL,
        ExpectedLogin nvarchar(256) NULL,
        ExpectedClientHostName nvarchar(128) NULL,
        ExpectedClientIp nvarchar(64) NULL,
        DesiredState tinyint NOT NULL CONSTRAINT DF_omp_InstanceTemplateAppInstances_DesiredState DEFAULT(1),
        SortOrder int NOT NULL CONSTRAINT DF_omp_InstanceTemplateAppInstances_SortOrder DEFAULT(0),
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_InstanceTemplateAppInstances_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplateAppInstances_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplateAppInstances_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_InstanceTemplateAppInstances_ModuleInstance
            FOREIGN KEY(InstanceTemplateModuleInstanceId)
            REFERENCES omp.InstanceTemplateModuleInstances(InstanceTemplateModuleInstanceId),
        CONSTRAINT FK_omp_InstanceTemplateAppInstances_Host
            FOREIGN KEY(InstanceTemplateHostId)
            REFERENCES omp.InstanceTemplateHosts(InstanceTemplateHostId),
        CONSTRAINT FK_omp_InstanceTemplateAppInstances_App
            FOREIGN KEY(AppId)
            REFERENCES omp.Apps(AppId),
        CONSTRAINT FK_omp_InstanceTemplateAppInstances_Artifact
            FOREIGN KEY(DesiredArtifactId)
            REFERENCES omp.Artifacts(ArtifactId),
        CONSTRAINT UQ_omp_InstanceTemplateAppInstances_ModuleInstance_AppInstanceKey
            UNIQUE(InstanceTemplateModuleInstanceId, AppInstanceKey)
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

-------------------------------------------------------------------------------
-- Accounts
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.users', N'U') IS NULL
BEGIN
    CREATE TABLE omp.users
    (
        user_id int IDENTITY(1,1) NOT NULL,

        -- User-facing name. This is intentionally not unique and should be used
        -- together with user_id in administrative screens when users need to be
        -- distinguished from each other.
        display_name nvarchar(200) NOT NULL,

        -- Integer status instead of physical deletion. Suggested initial values:
        -- 1 = active, 2 = disabled, 3 = deleted/reserved. The application owns
        -- the final enum mapping.
        account_status int NOT NULL CONSTRAINT DF_omp_users_account_status DEFAULT(1),

        -- Last successful login/authentication resolve for this OMP user. This
        -- is intended for support/admin troubleshooting, not online presence.
        last_login_at datetime2(3) NULL,

        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_users_created_at DEFAULT SYSUTCDATETIME(),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_users_updated_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_users PRIMARY KEY(user_id)
    );
END
GO

-------------------------------------------------------------------------------
-- Authentication providers
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.auth_providers', N'U') IS NULL
BEGIN
    CREATE TABLE omp.auth_providers
    (
        provider_id int IDENTITY(1,1) NOT NULL,

        -- Human-readable provider name shown in administration and diagnostics.
        -- Provider-specific code decides how each provider is handled.
        display_name nvarchar(200) NOT NULL,

        -- Allows an authentication provider to be disabled from the database
        -- without deleting provider metadata or existing account links.
        is_enabled bit NOT NULL CONSTRAINT DF_omp_auth_providers_is_enabled DEFAULT(1),

        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_auth_providers_updated_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_auth_providers PRIMARY KEY(provider_id),
        CONSTRAINT UQ_omp_auth_providers_display_name UNIQUE(display_name)
    );
END
GO

-------------------------------------------------------------------------------
-- User-to-authentication mapping
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.user_auth', N'U') IS NULL
BEGIN
    CREATE TABLE omp.user_auth
    (
        user_auth_id int IDENTITY(1,1) NOT NULL,
        user_id int NOT NULL,
        provider_id int NOT NULL,

        -- Provider-specific stable subject/user key. This is intended for
        -- identifiers such as DOMAIN\user, Entra object id, OIDC sub, or local
        -- login name. Do not store OAuth access tokens or refresh tokens here.
        -- The wide column allows federated identity keys longer than a Windows
        -- login while lookups use provider_user_hash for index efficiency.
        provider_user_key nvarchar(1000) NOT NULL,
        provider_user_hash AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), provider_user_key))) PERSISTED,

        -- Last time this linked provider identity was successfully used.
        last_used_at datetime2(3) NULL,
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_user_auth_created_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_user_auth PRIMARY KEY(user_auth_id),
        CONSTRAINT FK_omp_user_auth_user FOREIGN KEY(user_id) REFERENCES omp.users(user_id),
        CONSTRAINT FK_omp_user_auth_provider FOREIGN KEY(provider_id) REFERENCES omp.auth_providers(provider_id),
        CONSTRAINT UQ_omp_user_auth_provider_key UNIQUE(provider_id, provider_user_hash)
    );
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE name = N'UQ_omp_user_auth_user_provider_key'
      AND parent_object_id = OBJECT_ID(N'omp.user_auth')
)
BEGIN
    ALTER TABLE omp.user_auth DROP CONSTRAINT UQ_omp_user_auth_user_provider_key;
END
GO

-------------------------------------------------------------------------------
-- Local password authentication provider data
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.auth_provider_lpwd', N'U') IS NULL
BEGIN
    CREATE TABLE omp.auth_provider_lpwd
    (
        -- Login name for the local password provider. This is provider-specific
        -- auth data, not the internal OMP user identity.
        user_name nvarchar(256) NOT NULL,

        -- Stores only a password hash. Raw passwords must never be stored here.
        password_hash nvarchar(1000) NOT NULL,

        CONSTRAINT PK_omp_auth_provider_lpwd PRIMARY KEY(user_name)
    );
END
GO

-------------------------------------------------------------------------------
-- OMP configuration settings
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.config_setting_definitions', N'U') IS NULL
BEGIN
    CREATE TABLE omp.config_setting_definitions
    (
        ConfigSettingId int IDENTITY(1,1) NOT NULL,
        ConfigCategory nvarchar(100) NOT NULL,
        ConfigSetting nvarchar(200) NOT NULL,
        Description nvarchar(1000) NULL,
        SortOrder int NOT NULL CONSTRAINT DF_omp_config_setting_definitions_SortOrder DEFAULT(0),
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_config_setting_definitions_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_config_setting_definitions_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_config_setting_definitions_UpdatedUtc DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_config_setting_definitions PRIMARY KEY(ConfigSettingId),
        CONSTRAINT UQ_omp_config_setting_definitions_key UNIQUE(ConfigCategory, ConfigSetting)
    );
END
GO

IF COL_LENGTH(N'omp.config_setting_definitions', N'Description') IS NULL
BEGIN
    ALTER TABLE omp.config_setting_definitions ADD Description nvarchar(1000) NULL;
END
GO

IF COL_LENGTH(N'omp.config_setting_definitions', N'SortOrder') IS NULL
BEGIN
    ALTER TABLE omp.config_setting_definitions
        ADD SortOrder int NOT NULL CONSTRAINT DF_omp_config_setting_definitions_SortOrder DEFAULT(0) WITH VALUES;
END
GO

IF COL_LENGTH(N'omp.config_setting_definitions', N'IsEnabled') IS NULL
BEGIN
    ALTER TABLE omp.config_setting_definitions
        ADD IsEnabled bit NOT NULL CONSTRAINT DF_omp_config_setting_definitions_IsEnabled DEFAULT(1) WITH VALUES;
END
GO

IF COL_LENGTH(N'omp.config_setting_definitions', N'CreatedUtc') IS NULL
BEGIN
    ALTER TABLE omp.config_setting_definitions
        ADD CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_config_setting_definitions_CreatedUtc DEFAULT SYSUTCDATETIME() WITH VALUES;
END
GO

IF COL_LENGTH(N'omp.config_setting_definitions', N'UpdatedUtc') IS NULL
BEGIN
    ALTER TABLE omp.config_setting_definitions
        ADD UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_config_setting_definitions_UpdatedUtc DEFAULT SYSUTCDATETIME() WITH VALUES;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE [type] = N'PK'
      AND parent_object_id = OBJECT_ID(N'omp.config_setting_definitions')
)
BEGIN
    ALTER TABLE omp.config_setting_definitions
        ADD CONSTRAINT PK_omp_config_setting_definitions PRIMARY KEY(ConfigSettingId);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE name = N'UQ_omp_config_setting_definitions_key'
      AND parent_object_id = OBJECT_ID(N'omp.config_setting_definitions')
)
BEGIN
    ALTER TABLE omp.config_setting_definitions
        ADD CONSTRAINT UQ_omp_config_setting_definitions_key UNIQUE(ConfigCategory, ConfigSetting);
END
GO

IF COL_LENGTH(N'omp.config_settings', N'config_setting_id') IS NOT NULL
   AND COL_LENGTH(N'omp.config_settings', N'ConfigId') IS NULL
BEGIN
    EXEC sp_rename N'omp.config_settings.config_setting_id', N'ConfigId', N'COLUMN';
END
GO

IF COL_LENGTH(N'omp.config_settings', N'category') IS NOT NULL
   AND COL_LENGTH(N'omp.config_settings', N'ConfigCategory') IS NULL
BEGIN
    EXEC sp_rename N'omp.config_settings.category', N'ConfigCategory', N'COLUMN';
END
GO

IF COL_LENGTH(N'omp.config_settings', N'setting') IS NOT NULL
   AND COL_LENGTH(N'omp.config_settings', N'ConfigSetting') IS NULL
BEGIN
    EXEC sp_rename N'omp.config_settings.setting', N'ConfigSetting', N'COLUMN';
END
GO

IF COL_LENGTH(N'omp.config_settings', N'value') IS NOT NULL
   AND COL_LENGTH(N'omp.config_settings', N'ConfigValue') IS NULL
BEGIN
    EXEC sp_rename N'omp.config_settings.value', N'ConfigValue', N'COLUMN';
END
GO

IF COL_LENGTH(N'omp.config_settings', N'user_id') IS NOT NULL
   AND COL_LENGTH(N'omp.config_settings', N'ConfigUsr') IS NULL
BEGIN
    EXEC sp_rename N'omp.config_settings.user_id', N'ConfigUsr', N'COLUMN';
END
GO

IF COL_LENGTH(N'omp.config_settings', N'role_id') IS NOT NULL
   AND COL_LENGTH(N'omp.config_settings', N'ConfigRole') IS NULL
BEGIN
    EXEC sp_rename N'omp.config_settings.role_id', N'ConfigRole', N'COLUMN';
END
GO

IF OBJECT_ID(N'omp.config_settings', N'U') IS NOT NULL
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM sys.key_constraints
        WHERE name = N'UQ_omp_config_settings_scope'
          AND parent_object_id = OBJECT_ID(N'omp.config_settings')
    )
    BEGIN
        ALTER TABLE omp.config_settings DROP CONSTRAINT UQ_omp_config_settings_scope;
    END
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_config_settings_resolve'
      AND object_id = OBJECT_ID(N'omp.config_settings')
)
BEGIN
    DROP INDEX IX_omp_config_settings_resolve ON omp.config_settings;
END
GO

IF OBJECT_ID(N'omp.config_settings', N'U') IS NULL
BEGIN
    CREATE TABLE omp.config_settings
    (
        ConfigId int IDENTITY(1,1) NOT NULL,
        ConfigSettingId int NOT NULL,

        -- Stored as text to allow simple scalar values such as true/false,
        -- numbers, names, JSON, XML, or serialized values when required by
        -- future settings.
        ConfigValue nvarchar(max) NULL,

        -- Optional scope. NULL means instance-wide/default setting. Matching
        -- consumers should resolve by scope rank, then priority:
        -- user > permission > role > global; higher ConfigPriority wins within
        -- the same scope class, and ConfigId is the deterministic final tie
        -- breaker.
        ConfigUsr int NULL,
        ConfigPermission int NULL,
        ConfigRole int NULL,
        ConfigPriority int NOT NULL CONSTRAINT DF_omp_config_settings_ConfigPriority DEFAULT(0),
        ConfigScopeRank AS CONVERT(tinyint,
            CASE
                WHEN ConfigUsr IS NOT NULL THEN 3
                WHEN ConfigPermission IS NOT NULL THEN 2
                WHEN ConfigRole IS NOT NULL THEN 1
                ELSE 0
            END) PERSISTED,

        CONSTRAINT PK_omp_config_settings PRIMARY KEY(ConfigId)
    );
END
GO

IF COL_LENGTH(N'omp.config_settings', N'ConfigSettingId') IS NULL
BEGIN
    ALTER TABLE omp.config_settings ADD ConfigSettingId int NULL;
END
GO

IF COL_LENGTH(N'omp.config_settings', N'ConfigCategory') IS NOT NULL
   AND COL_LENGTH(N'omp.config_settings', N'ConfigSetting') IS NOT NULL
BEGIN
    INSERT INTO omp.config_setting_definitions(ConfigCategory, ConfigSetting, Description, SortOrder, IsEnabled)
    SELECT DISTINCT cs.ConfigCategory,
           cs.ConfigSetting,
           NULL,
           1000,
           1
    FROM omp.config_settings cs
    WHERE cs.ConfigSettingId IS NULL
      AND LTRIM(RTRIM(cs.ConfigCategory)) <> N''
      AND LTRIM(RTRIM(cs.ConfigSetting)) <> N''
      AND NOT EXISTS
      (
          SELECT 1
          FROM omp.config_setting_definitions existing
          WHERE existing.ConfigCategory = cs.ConfigCategory
            AND existing.ConfigSetting = cs.ConfigSetting
      );
END
GO

IF COL_LENGTH(N'omp.config_settings', N'ConfigCategory') IS NOT NULL
   AND COL_LENGTH(N'omp.config_settings', N'ConfigSetting') IS NOT NULL
BEGIN
    UPDATE cs
       SET ConfigSettingId = def.ConfigSettingId
    FROM omp.config_settings cs
    INNER JOIN omp.config_setting_definitions def
        ON def.ConfigCategory = cs.ConfigCategory
       AND def.ConfigSetting = cs.ConfigSetting
    WHERE cs.ConfigSettingId IS NULL;
END
GO

IF EXISTS
(
    SELECT 1
    FROM omp.config_settings
    WHERE ConfigSettingId IS NULL
)
BEGIN
    THROW 51020, 'omp.config_settings contains rows that could not be mapped to omp.config_setting_definitions.', 1;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'omp.config_settings')
      AND name = N'ConfigSettingId'
      AND is_nullable = 1
)
BEGIN
    ALTER TABLE omp.config_settings ALTER COLUMN ConfigSettingId int NOT NULL;
END
GO

IF COL_LENGTH(N'omp.config_settings', N'ConfigCategory') IS NOT NULL
BEGIN
    ALTER TABLE omp.config_settings DROP COLUMN ConfigCategory;
END
GO

IF COL_LENGTH(N'omp.config_settings', N'ConfigSetting') IS NOT NULL
BEGIN
    ALTER TABLE omp.config_settings DROP COLUMN ConfigSetting;
END
GO

IF COL_LENGTH(N'omp.config_settings', N'ConfigPriority') IS NULL
BEGIN
    ALTER TABLE omp.config_settings
        ADD ConfigPriority int NOT NULL CONSTRAINT DF_omp_config_settings_ConfigPriority DEFAULT(0) WITH VALUES;
END
GO

IF COL_LENGTH(N'omp.config_settings', N'ConfigPermission') IS NULL
BEGIN
    ALTER TABLE omp.config_settings ADD ConfigPermission int NULL;
END
GO

IF COL_LENGTH(N'omp.config_settings', N'ConfigScopeRank') IS NULL
BEGIN
    ALTER TABLE omp.config_settings
        ADD ConfigScopeRank AS CONVERT(tinyint,
            CASE
                WHEN ConfigUsr IS NOT NULL THEN 3
                WHEN ConfigPermission IS NOT NULL THEN 2
                WHEN ConfigRole IS NOT NULL THEN 1
                ELSE 0
            END) PERSISTED;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_omp_config_settings_definition'
      AND parent_object_id = OBJECT_ID(N'omp.config_settings')
)
BEGIN
    ALTER TABLE omp.config_settings
        ADD CONSTRAINT FK_omp_config_settings_definition
        FOREIGN KEY(ConfigSettingId) REFERENCES omp.config_setting_definitions(ConfigSettingId);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE [type] = N'PK'
      AND parent_object_id = OBJECT_ID(N'omp.config_settings')
)
BEGIN
    ALTER TABLE omp.config_settings
        ADD CONSTRAINT PK_omp_config_settings PRIMARY KEY(ConfigId);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_omp_config_settings_user'
      AND parent_object_id = OBJECT_ID(N'omp.config_settings')
)
BEGIN
    ALTER TABLE omp.config_settings
        ADD CONSTRAINT FK_omp_config_settings_user
        FOREIGN KEY(ConfigUsr) REFERENCES omp.users(user_id);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_omp_config_settings_permission'
      AND parent_object_id = OBJECT_ID(N'omp.config_settings')
)
BEGIN
    ALTER TABLE omp.config_settings
        ADD CONSTRAINT FK_omp_config_settings_permission
        FOREIGN KEY(ConfigPermission) REFERENCES omp.Permissions(PermissionId);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_omp_config_settings_role'
      AND parent_object_id = OBJECT_ID(N'omp.config_settings')
)
BEGIN
    ALTER TABLE omp.config_settings
        ADD CONSTRAINT FK_omp_config_settings_role
        FOREIGN KEY(ConfigRole) REFERENCES omp.Roles(RoleId);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE name = N'UQ_omp_config_settings_scope'
      AND parent_object_id = OBJECT_ID(N'omp.config_settings')
)
BEGIN
    ALTER TABLE omp.config_settings
        ADD CONSTRAINT UQ_omp_config_settings_scope
        UNIQUE(ConfigSettingId, ConfigUsr, ConfigPermission, ConfigRole);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_config_settings_resolve'
      AND object_id = OBJECT_ID(N'omp.config_settings')
)
BEGIN
    CREATE INDEX IX_omp_config_settings_resolve
        ON omp.config_settings(ConfigSettingId, ConfigScopeRank DESC, ConfigPriority DESC, ConfigId DESC)
        INCLUDE(ConfigUsr, ConfigPermission, ConfigRole);
END
GO
