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
        SortOrder int NOT NULL CONSTRAINT DF_omp_InstanceTemplates_SortOrder DEFAULT(0),
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_InstanceTemplates_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_InstanceTemplates_TemplateKey UNIQUE(TemplateKey)
    );
END
GO

IF COL_LENGTH(N'omp.InstanceTemplates', N'SortOrder') IS NULL
BEGIN
    ALTER TABLE omp.InstanceTemplates
        ADD SortOrder int NOT NULL CONSTRAINT DF_omp_InstanceTemplates_SortOrder DEFAULT(0) WITH VALUES;
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
        SortOrder int NOT NULL CONSTRAINT DF_omp_HostTemplates_SortOrder DEFAULT(0),
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_HostTemplates_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostTemplates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostTemplates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_HostTemplates_TemplateKey UNIQUE(TemplateKey)
    );
END
GO

IF COL_LENGTH(N'omp.HostTemplates', N'SortOrder') IS NULL
BEGIN
    ALTER TABLE omp.HostTemplates
        ADD SortOrder int NOT NULL CONSTRAINT DF_omp_HostTemplates_SortOrder DEFAULT(0) WITH VALUES;
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
        AllowMultipleActiveInstances bit NOT NULL CONSTRAINT DF_omp_Apps_AllowMultipleActiveInstances DEFAULT(0),
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

IF COL_LENGTH(N'omp.Apps', N'AllowMultipleActiveInstances') IS NULL
BEGIN
    ALTER TABLE omp.Apps
        ADD AllowMultipleActiveInstances bit NOT NULL
            CONSTRAINT DF_omp_Apps_AllowMultipleActiveInstances DEFAULT(0) WITH VALUES;
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

IF OBJECT_ID(N'omp.ModuleDefinitionDocuments', N'U') IS NULL
BEGIN
    CREATE TABLE omp.ModuleDefinitionDocuments
    (
        ModuleDefinitionDocumentId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_ModuleDefinitionDocuments PRIMARY KEY,
        ModuleKey nvarchar(100) NOT NULL,
        DefinitionVersion nvarchar(50) NOT NULL,
        FormatVersion int NOT NULL CONSTRAINT DF_omp_ModuleDefinitionDocuments_FormatVersion DEFAULT(1),
        DefinitionJson nvarchar(max) NOT NULL,
        DefinitionSha256 nvarchar(128) NOT NULL,
        SourceName nvarchar(400) NULL,
        IsApplied bit NOT NULL CONSTRAINT DF_omp_ModuleDefinitionDocuments_IsApplied DEFAULT(0),
        AppliedUtc datetime2(3) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionDocuments_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionDocuments_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_ModuleDefinitionDocuments_Module_Version UNIQUE(ModuleKey, DefinitionVersion),
        CONSTRAINT CK_omp_ModuleDefinitionDocuments_DefinitionJson CHECK(ISJSON(DefinitionJson) = 1)
    );
END
GO

IF OBJECT_ID(N'omp.ModuleDefinitionArtifactCompatibility', N'U') IS NULL
BEGIN
    CREATE TABLE omp.ModuleDefinitionArtifactCompatibility
    (
        ModuleDefinitionArtifactCompatibilityId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_ModuleDefinitionArtifactCompatibility PRIMARY KEY,
        ModuleDefinitionDocumentId int NOT NULL,
        AppKey nvarchar(100) NOT NULL,
        PackageType nvarchar(50) NOT NULL,
        TargetName nvarchar(100) NULL,
        RelativePathTemplate nvarchar(400) NULL,
        MinArtifactVersion nvarchar(50) NULL,
        MaxArtifactVersion nvarchar(50) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionArtifactCompatibility_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_ModuleDefinitionArtifactCompatibility_Document
            FOREIGN KEY(ModuleDefinitionDocumentId)
            REFERENCES omp.ModuleDefinitionDocuments(ModuleDefinitionDocumentId)
            ON DELETE CASCADE,
        CONSTRAINT UQ_omp_ModuleDefinitionArtifactCompatibility_Target
            UNIQUE(ModuleDefinitionDocumentId, AppKey, PackageType, TargetName)
    );
END
GO

IF COL_LENGTH(N'omp.ModuleDefinitionArtifactCompatibility', N'RelativePathTemplate') IS NULL
BEGIN
    ALTER TABLE omp.ModuleDefinitionArtifactCompatibility
        ADD RelativePathTemplate nvarchar(400) NULL;
END
GO

IF OBJECT_ID(N'omp.ModuleDefinitionSqlExecutions', N'U') IS NULL
BEGIN
    CREATE TABLE omp.ModuleDefinitionSqlExecutions
    (
        ModuleDefinitionSqlExecutionId bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_ModuleDefinitionSqlExecutions PRIMARY KEY,
        ModuleDefinitionDocumentId int NOT NULL,
        ScriptKey nvarchar(100) NOT NULL,
        ScriptPhase nvarchar(50) NOT NULL,
        ScriptOrder int NOT NULL,
        ScriptSha256 nvarchar(128) NOT NULL,
        ExecutionStatus nvarchar(30) NOT NULL,
        StartedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionSqlExecutions_StartedUtc DEFAULT SYSUTCDATETIME(),
        CompletedUtc datetime2(3) NULL,
        ErrorMessage nvarchar(max) NULL,
        ExecutedBy nvarchar(256) NULL CONSTRAINT DF_omp_ModuleDefinitionSqlExecutions_ExecutedBy DEFAULT SUSER_SNAME(),
        CONSTRAINT FK_omp_ModuleDefinitionSqlExecutions_Document
            FOREIGN KEY(ModuleDefinitionDocumentId)
            REFERENCES omp.ModuleDefinitionDocuments(ModuleDefinitionDocumentId)
            ON DELETE CASCADE,
        CONSTRAINT CK_omp_ModuleDefinitionSqlExecutions_Status
            CHECK(ExecutionStatus IN (N'Running', N'Succeeded', N'Failed'))
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.ModuleDefinitionSqlExecutions')
      AND name = N'IX_omp_ModuleDefinitionSqlExecutions_Latest'
)
BEGIN
    CREATE INDEX IX_omp_ModuleDefinitionSqlExecutions_Latest
        ON omp.ModuleDefinitionSqlExecutions(ModuleDefinitionDocumentId, ScriptKey, ScriptSha256, StartedUtc DESC)
        INCLUDE(ExecutionStatus, CompletedUtc);
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

IF OBJECT_ID(N'omp.ArtifactConfigurationFiles', N'U') IS NULL
BEGIN
    CREATE TABLE omp.ArtifactConfigurationFiles
    (
        ArtifactConfigurationFileId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_ArtifactConfigurationFiles PRIMARY KEY,
        ArtifactId int NOT NULL,
        RelativePath nvarchar(400) NOT NULL,
        FileContent nvarchar(max) NOT NULL CONSTRAINT DF_omp_ArtifactConfigurationFiles_FileContent DEFAULT(N''),
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_ArtifactConfigurationFiles_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ArtifactConfigurationFiles_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ArtifactConfigurationFiles_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_ArtifactConfigurationFiles_Artifact
            FOREIGN KEY(ArtifactId) REFERENCES omp.Artifacts(ArtifactId),
        CONSTRAINT UQ_omp_ArtifactConfigurationFiles_Artifact_Path
            UNIQUE(ArtifactId, RelativePath)
    );
END
GO

IF OBJECT_ID(N'omp.HostConfigurationDocuments', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostConfigurationDocuments
    (
        HostConfigurationDocumentId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_HostConfigurationDocuments PRIMARY KEY,
        HostKey nvarchar(128) NOT NULL,
        ConfigurationVersion nvarchar(50) NOT NULL,
        FormatVersion int NOT NULL CONSTRAINT DF_omp_HostConfigurationDocuments_FormatVersion DEFAULT(1),
        ConfigurationJson nvarchar(max) NOT NULL,
        ConfigurationSha256 nvarchar(128) NOT NULL,
        DisplayName nvarchar(200) NULL,
        Description nvarchar(500) NULL,
        SourceName nvarchar(400) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_omp_HostConfigurationDocuments_IsActive DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostConfigurationDocuments_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostConfigurationDocuments_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_HostConfigurationDocuments_Host_Version
            UNIQUE(HostKey, ConfigurationVersion),
        CONSTRAINT CK_omp_HostConfigurationDocuments_Json
            CHECK(ISJSON(ConfigurationJson) = 1)
    );
END
GO

IF OBJECT_ID(N'omp.ConfigOverlayDocuments', N'U') IS NULL
BEGIN
    CREATE TABLE omp.ConfigOverlayDocuments
    (
        ConfigOverlayDocumentId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_ConfigOverlayDocuments PRIMARY KEY,
        OverlayKey nvarchar(200) NOT NULL,
        OverlayVersion nvarchar(50) NOT NULL,
        HostKey nvarchar(128) NOT NULL,
        ModuleKey nvarchar(100) NULL,
        ModuleDefinitionVersion nvarchar(50) NULL,
        AppKey nvarchar(100) NULL,
        PackageType nvarchar(50) NULL,
        TargetName nvarchar(100) NULL,
        ArtifactVersion nvarchar(50) NULL,
        FormatVersion int NOT NULL CONSTRAINT DF_omp_ConfigOverlayDocuments_FormatVersion DEFAULT(1),
        OverlayJson nvarchar(max) NOT NULL,
        OverlaySha256 nvarchar(128) NOT NULL,
        SourceName nvarchar(400) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_ConfigOverlayDocuments_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ConfigOverlayDocuments_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ConfigOverlayDocuments_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_ConfigOverlayDocuments_Key_Host_Version
            UNIQUE(OverlayKey, HostKey, OverlayVersion),
        CONSTRAINT CK_omp_ConfigOverlayDocuments_Json
            CHECK(ISJSON(OverlayJson) = 1)
    );
END
GO

IF OBJECT_ID(N'omp.ConfigOverlayConfigurationFiles', N'U') IS NULL
BEGIN
    CREATE TABLE omp.ConfigOverlayConfigurationFiles
    (
        ConfigOverlayConfigurationFileId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_ConfigOverlayConfigurationFiles PRIMARY KEY,
        ConfigOverlayDocumentId int NOT NULL,
        RelativePath nvarchar(400) NOT NULL,
        FileContent nvarchar(max) NOT NULL CONSTRAINT DF_omp_ConfigOverlayConfigurationFiles_FileContent DEFAULT(N''),
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_ConfigOverlayConfigurationFiles_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ConfigOverlayConfigurationFiles_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ConfigOverlayConfigurationFiles_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_ConfigOverlayConfigurationFiles_Document
            FOREIGN KEY(ConfigOverlayDocumentId)
            REFERENCES omp.ConfigOverlayDocuments(ConfigOverlayDocumentId)
            ON DELETE CASCADE,
        CONSTRAINT UQ_omp_ConfigOverlayConfigurationFiles_Document_Path
            UNIQUE(ConfigOverlayDocumentId, RelativePath)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.ConfigOverlayDocuments')
      AND name = N'IX_omp_ConfigOverlayDocuments_Match'
)
BEGIN
    CREATE INDEX IX_omp_ConfigOverlayDocuments_Match
        ON omp.ConfigOverlayDocuments(HostKey, ModuleKey, AppKey, PackageType, TargetName, ArtifactVersion, IsEnabled)
        INCLUDE(OverlayVersion, ModuleDefinitionVersion, UpdatedUtc);
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
        TargetHostTemplateId int NULL,
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

IF COL_LENGTH(N'omp.AppInstances', N'TargetHostTemplateId') IS NULL
BEGIN
    ALTER TABLE omp.AppInstances
        ADD TargetHostTemplateId int NULL;
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

IF OBJECT_ID(N'omp.HostAppDeploymentStates', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostAppDeploymentStates
    (
        HostId uniqueidentifier NOT NULL,
        AppInstanceId uniqueidentifier NOT NULL,
        ArtifactId int NULL,
        DeploymentState tinyint NOT NULL CONSTRAINT DF_omp_HostAppDeploymentStates_DeploymentState DEFAULT(0),
        SourceLocalPath nvarchar(500) NULL,
        TargetPath nvarchar(500) NULL,
        RuntimeName nvarchar(200) NULL,
        ContentSha256 nvarchar(128) NULL,
        CredentialAutomationMode nvarchar(40) NULL,
        DesiredRuntimeIdentity nvarchar(256) NULL,
        ActualRuntimeIdentity nvarchar(256) NULL,
        IdentityCheckStatus nvarchar(40) NULL,
        IdentityRepairRequestedUtc datetime2(3) NULL,
        IdentityRepairRequestedBy nvarchar(256) NULL,
        LastCheckedUtc datetime2(3) NULL,
        LastAppliedUtc datetime2(3) NULL,
        LastError nvarchar(4000) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAppDeploymentStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAppDeploymentStates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_omp_HostAppDeploymentStates PRIMARY KEY(HostId, AppInstanceId),
        CONSTRAINT FK_omp_HostAppDeploymentStates_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_HostAppDeploymentStates_AppInstance FOREIGN KEY(AppInstanceId) REFERENCES omp.AppInstances(AppInstanceId),
        CONSTRAINT FK_omp_HostAppDeploymentStates_Artifact FOREIGN KEY(ArtifactId) REFERENCES omp.Artifacts(ArtifactId)
    );
END
GO

IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'CredentialAutomationMode') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD CredentialAutomationMode nvarchar(40) NULL;
END
GO

IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'DesiredRuntimeIdentity') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD DesiredRuntimeIdentity nvarchar(256) NULL;
END
GO

IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'ActualRuntimeIdentity') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD ActualRuntimeIdentity nvarchar(256) NULL;
END
GO

IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'IdentityCheckStatus') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD IdentityCheckStatus nvarchar(40) NULL;
END
GO

IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'IdentityRepairRequestedUtc') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD IdentityRepairRequestedUtc datetime2(3) NULL;
END
GO

IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'IdentityRepairRequestedBy') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD IdentityRepairRequestedBy nvarchar(256) NULL;
END
GO

IF OBJECT_ID(N'omp.HostAgentDesiredStates', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostAgentDesiredStates
    (
        HostId uniqueidentifier NOT NULL
            CONSTRAINT PK_omp_HostAgentDesiredStates PRIMARY KEY,
        ArtifactId int NOT NULL,
        ServiceNamePrefix nvarchar(160) NULL,
        InstallRoot nvarchar(500) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_omp_HostAgentDesiredStates_IsEnabled DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentDesiredStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentDesiredStates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_HostAgentDesiredStates_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_HostAgentDesiredStates_Artifact FOREIGN KEY(ArtifactId) REFERENCES omp.Artifacts(ArtifactId)
    );
END
GO

IF OBJECT_ID(N'omp.HostAgentRuntimeStates', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostAgentRuntimeStates
    (
        HostId uniqueidentifier NOT NULL,
        ServiceName nvarchar(200) NOT NULL,
        Version nvarchar(50) NULL,
        ArtifactId int NULL,
        InstallPath nvarchar(500) NULL,
        ProcessId int NULL,
        RuntimeMode nvarchar(40) NOT NULL CONSTRAINT DF_omp_HostAgentRuntimeStates_RuntimeMode DEFAULT(N'Normal'),
        IsActive bit NOT NULL CONSTRAINT DF_omp_HostAgentRuntimeStates_IsActive DEFAULT(0),
        TakeoverFromServiceName nvarchar(200) NULL,
        LastSeenUtc datetime2(3) NULL,
        QuiesceRequestedUtc datetime2(3) NULL,
        QuiescedUtc datetime2(3) NULL,
        StatusMessage nvarchar(1000) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentRuntimeStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentRuntimeStates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_omp_HostAgentRuntimeStates PRIMARY KEY(HostId, ServiceName),
        CONSTRAINT FK_omp_HostAgentRuntimeStates_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_HostAgentRuntimeStates_Artifact FOREIGN KEY(ArtifactId) REFERENCES omp.Artifacts(ArtifactId),
        CONSTRAINT CK_omp_HostAgentRuntimeStates_Mode CHECK(RuntimeMode IN (N'Normal', N'Takeover', N'Quiescing', N'Quiesced', N'Failed'))
    );
END
GO

IF OBJECT_ID(N'omp.HostAgentLeases', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostAgentLeases
    (
        HostId uniqueidentifier NOT NULL
            CONSTRAINT PK_omp_HostAgentLeases PRIMARY KEY,
        ServiceName nvarchar(200) NOT NULL,
        LeaseToken uniqueidentifier NOT NULL,
        RuntimeMode nvarchar(40) NOT NULL,
        LeaseUntilUtc datetime2(3) NOT NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentLeases_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentLeases_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_HostAgentLeases_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId)
    );
END
GO

IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostAgentJobs
    (
        HostAgentJobId bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_HostAgentJobs PRIMARY KEY,
        HostId uniqueidentifier NOT NULL,
        JobType nvarchar(100) NOT NULL,
        PayloadJson nvarchar(max) NULL,
        Status tinyint NOT NULL CONSTRAINT DF_omp_HostAgentJobs_Status DEFAULT(0),
        RequestedBy nvarchar(256) NULL,
        RequestedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentJobs_RequestedUtc DEFAULT SYSUTCDATETIME(),
        ClaimedByServiceName nvarchar(200) NULL,
        ClaimedUtc datetime2(3) NULL,
        LeaseUntilUtc datetime2(3) NULL,
        StartedUtc datetime2(3) NULL,
        CompletedUtc datetime2(3) NULL,
        AttemptCount int NOT NULL CONSTRAINT DF_omp_HostAgentJobs_AttemptCount DEFAULT(0),
        MaxAttempts int NOT NULL CONSTRAINT DF_omp_HostAgentJobs_MaxAttempts DEFAULT(3),
        ResultJson nvarchar(max) NULL,
        LastError nvarchar(max) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentJobs_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentJobs_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_HostAgentJobs_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT CK_omp_HostAgentJobs_Status CHECK(Status IN (0, 1, 2, 3, 4, 5))
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.HostAgentJobs')
      AND name = N'IX_omp_HostAgentJobs_Host_Status'
)
BEGIN
    CREATE INDEX IX_omp_HostAgentJobs_Host_Status
        ON omp.HostAgentJobs(HostId, Status, RequestedUtc, HostAgentJobId);
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
        TargetHostTemplateId int NULL,
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
        IsAllowed bit NOT NULL CONSTRAINT DF_omp_InstanceTemplateAppInstances_IsAllowed DEFAULT(1),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplateAppInstances_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_InstanceTemplateAppInstances_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_InstanceTemplateAppInstances_ModuleInstance
            FOREIGN KEY(InstanceTemplateModuleInstanceId)
            REFERENCES omp.InstanceTemplateModuleInstances(InstanceTemplateModuleInstanceId),
        CONSTRAINT FK_omp_InstanceTemplateAppInstances_Host
            FOREIGN KEY(InstanceTemplateHostId)
            REFERENCES omp.InstanceTemplateHosts(InstanceTemplateHostId),
        CONSTRAINT FK_omp_InstanceTemplateAppInstances_TargetHostTemplate
            FOREIGN KEY(TargetHostTemplateId)
            REFERENCES omp.HostTemplates(HostTemplateId),
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

IF COL_LENGTH(N'omp.InstanceTemplateAppInstances', N'TargetHostTemplateId') IS NULL
BEGIN
    ALTER TABLE omp.InstanceTemplateAppInstances
        ADD TargetHostTemplateId int NULL;
END
GO

IF COL_LENGTH(N'omp.InstanceTemplateAppInstances', N'IsAllowed') IS NULL
BEGIN
    ALTER TABLE omp.InstanceTemplateAppInstances
        ADD IsAllowed bit NOT NULL CONSTRAINT DF_omp_InstanceTemplateAppInstances_IsAllowed DEFAULT(1) WITH VALUES;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_omp_AppInstances_TargetHostTemplate'
      AND parent_object_id = OBJECT_ID(N'omp.AppInstances')
)
BEGIN
    ALTER TABLE omp.AppInstances
        ADD CONSTRAINT FK_omp_AppInstances_TargetHostTemplate
            FOREIGN KEY(TargetHostTemplateId)
            REFERENCES omp.HostTemplates(HostTemplateId);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_omp_InstanceTemplateAppInstances_TargetHostTemplate'
      AND parent_object_id = OBJECT_ID(N'omp.InstanceTemplateAppInstances')
)
BEGIN
    ALTER TABLE omp.InstanceTemplateAppInstances
        ADD CONSTRAINT FK_omp_InstanceTemplateAppInstances_TargetHostTemplate
            FOREIGN KEY(TargetHostTemplateId)
            REFERENCES omp.HostTemplates(HostTemplateId);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_omp_AppInstances_OneHostPlacement'
      AND parent_object_id = OBJECT_ID(N'omp.AppInstances')
)
BEGIN
    ALTER TABLE omp.AppInstances
        ADD CONSTRAINT CK_omp_AppInstances_OneHostPlacement
            CHECK (HostId IS NULL OR TargetHostTemplateId IS NULL);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_omp_InstanceTemplateAppInstances_OneHostPlacement'
      AND parent_object_id = OBJECT_ID(N'omp.InstanceTemplateAppInstances')
)
BEGIN
    ALTER TABLE omp.InstanceTemplateAppInstances
        ADD CONSTRAINT CK_omp_InstanceTemplateAppInstances_OneHostPlacement
            CHECK (InstanceTemplateHostId IS NULL OR TargetHostTemplateId IS NULL);
END
GO

-------------------------------------------------------------------------------
-- App placement invariants
-------------------------------------------------------------------------------
IF EXISTS
(
    SELECT 1
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    WHERE ai.HostId IS NOT NULL
      AND ai.TargetHostTemplateId IS NULL
      AND ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND ai.DesiredState = 1
      AND a.AppType IN (N'Portal', N'WebApp')
      AND a.AllowMultipleActiveInstances = 0
    GROUP BY ai.ModuleInstanceId, ai.HostId, ai.AppId
    HAVING COUNT(1) > 1
)
BEGIN
    THROW 51050, 'Duplicate active host-specific web app instances exist. Keep only one active desired row per module instance, web app definition and host.', 1;
END
GO

IF EXISTS
(
    SELECT 1
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    WHERE ai.HostId IS NULL
      AND ai.TargetHostTemplateId IS NULL
      AND ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND ai.DesiredState = 1
      AND a.AppType IN (N'Portal', N'WebApp')
      AND a.AllowMultipleActiveInstances = 0
    GROUP BY ai.ModuleInstanceId, ai.AppId
    HAVING COUNT(1) > 1
)
BEGIN
    THROW 51051, 'Duplicate active host-neutral web app instances exist. Keep only one active desired host-neutral row per module instance and web app definition.', 1;
END
GO

IF EXISTS
(
    SELECT 1
    FROM omp.AppInstances ai
    INNER JOIN omp.Apps a ON a.AppId = ai.AppId
    WHERE ai.HostId IS NULL
      AND ai.TargetHostTemplateId IS NOT NULL
      AND ai.IsEnabled = 1
      AND ai.IsAllowed = 1
      AND ai.DesiredState = 1
      AND a.AppType IN (N'Portal', N'WebApp')
      AND a.AllowMultipleActiveInstances = 0
    GROUP BY ai.ModuleInstanceId, ai.TargetHostTemplateId, ai.AppId
    HAVING COUNT(1) > 1
)
BEGIN
    THROW 51058, 'Duplicate active host-role web app instances exist. Keep only one active desired row per module instance, web app definition and host role.', 1;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.AppInstances')
      AND name = N'UX_omp_AppInstances_Active_Module_Host_App'
)
BEGIN
    DROP INDEX UX_omp_AppInstances_Active_Module_Host_App ON omp.AppInstances;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.AppInstances')
      AND name = N'UX_omp_AppInstances_Active_Module_HostNeutral_App'
)
BEGIN
    DROP INDEX UX_omp_AppInstances_Active_Module_HostNeutral_App ON omp.AppInstances;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.AppInstances')
      AND name = N'IX_omp_AppInstances_Active_Module_Host_App'
      AND ISNULL(filter_definition, N'') NOT LIKE N'%TargetHostTemplateId%'
)
BEGIN
    DROP INDEX IX_omp_AppInstances_Active_Module_Host_App ON omp.AppInstances;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.AppInstances')
      AND name = N'IX_omp_AppInstances_Active_Module_Host_App'
)
BEGIN
    CREATE INDEX IX_omp_AppInstances_Active_Module_Host_App
        ON omp.AppInstances(ModuleInstanceId, HostId, AppId, AppInstanceKey)
        WHERE HostId IS NOT NULL
          AND TargetHostTemplateId IS NULL
          AND IsEnabled = 1
          AND IsAllowed = 1
          AND DesiredState = 1;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.AppInstances')
      AND name = N'IX_omp_AppInstances_Active_Module_HostNeutral_App'
      AND ISNULL(filter_definition, N'') NOT LIKE N'%TargetHostTemplateId%'
)
BEGIN
    DROP INDEX IX_omp_AppInstances_Active_Module_HostNeutral_App ON omp.AppInstances;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.AppInstances')
      AND name = N'IX_omp_AppInstances_Active_Module_HostNeutral_App'
)
BEGIN
    CREATE INDEX IX_omp_AppInstances_Active_Module_HostNeutral_App
        ON omp.AppInstances(ModuleInstanceId, AppId, AppInstanceKey)
        WHERE HostId IS NULL
          AND TargetHostTemplateId IS NULL
          AND IsEnabled = 1
          AND IsAllowed = 1
          AND DesiredState = 1;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.AppInstances')
      AND name = N'IX_omp_AppInstances_Active_Module_HostRole_App'
)
BEGIN
    CREATE INDEX IX_omp_AppInstances_Active_Module_HostRole_App
        ON omp.AppInstances(ModuleInstanceId, TargetHostTemplateId, AppId, AppInstanceKey)
        WHERE TargetHostTemplateId IS NOT NULL
          AND IsEnabled = 1
          AND IsAllowed = 1
          AND DesiredState = 1;
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

IF OBJECT_ID(N'omp.TR_AppInstances_ValidateActivePlacement', N'TR') IS NULL
    EXEC(N'CREATE TRIGGER omp.TR_AppInstances_ValidateActivePlacement ON omp.AppInstances AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END');
GO

ALTER TRIGGER omp.TR_AppInstances_ValidateActivePlacement
ON omp.AppInstances
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN omp.Apps a
            ON a.AppId = i.AppId
           AND a.AppType IN (N'Portal', N'WebApp')
           AND a.AllowMultipleActiveInstances = 0
        INNER JOIN omp.AppInstances existing
            ON existing.ModuleInstanceId = i.ModuleInstanceId
           AND existing.AppId = i.AppId
           AND existing.AppInstanceId <> i.AppInstanceId
           AND
           (
               (i.HostId IS NULL AND i.TargetHostTemplateId IS NULL AND existing.HostId IS NULL AND existing.TargetHostTemplateId IS NULL)
               OR (i.HostId IS NOT NULL AND existing.HostId = i.HostId)
               OR (i.TargetHostTemplateId IS NOT NULL AND existing.TargetHostTemplateId = i.TargetHostTemplateId)
           )
        WHERE i.IsEnabled = 1
          AND i.IsAllowed = 1
          AND i.DesiredState = 1
          AND existing.IsEnabled = 1
          AND existing.IsAllowed = 1
          AND existing.DesiredState = 1
    )
    BEGIN
        THROW 51052, 'Only one active desired web app instance is allowed per module instance, web app definition and host placement.', 1;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN omp.Apps a
            ON a.AppId = i.AppId
           AND a.AppType IN (N'Portal', N'WebApp')
           AND a.AllowMultipleActiveInstances = 0
        INNER JOIN omp.AppInstances existing
            ON existing.ModuleInstanceId = i.ModuleInstanceId
           AND existing.AppId = i.AppId
           AND existing.AppInstanceId <> i.AppInstanceId
           AND
           (
               (i.HostId IS NULL AND i.TargetHostTemplateId IS NULL AND (existing.HostId IS NOT NULL OR existing.TargetHostTemplateId IS NOT NULL))
               OR ((i.HostId IS NOT NULL OR i.TargetHostTemplateId IS NOT NULL) AND existing.HostId IS NULL AND existing.TargetHostTemplateId IS NULL)
           )
        WHERE i.IsEnabled = 1
          AND i.IsAllowed = 1
          AND i.DesiredState = 1
          AND existing.IsEnabled = 1
          AND existing.IsAllowed = 1
          AND existing.DesiredState = 1
    )
    BEGIN
        THROW 51053, 'Do not mix active host-neutral and targeted web app instances for the same module instance and web app definition.', 1;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN omp.Apps a
            ON a.AppId = i.AppId
           AND a.AppType IN (N'Portal', N'WebApp')
           AND a.AllowMultipleActiveInstances = 0
        INNER JOIN omp.AppInstances existing
            ON existing.ModuleInstanceId = i.ModuleInstanceId
           AND existing.AppId = i.AppId
           AND existing.AppInstanceId <> i.AppInstanceId
        WHERE i.IsEnabled = 1
          AND i.IsAllowed = 1
          AND i.DesiredState = 1
          AND existing.IsEnabled = 1
          AND existing.IsAllowed = 1
          AND existing.DesiredState = 1
          AND
          (
              (
                  i.HostId IS NOT NULL
                  AND existing.TargetHostTemplateId IS NOT NULL
                  AND EXISTS
                  (
                      SELECT 1
                      FROM omp.HostDeploymentAssignments hda
                      WHERE hda.HostId = i.HostId
                        AND hda.HostTemplateId = existing.TargetHostTemplateId
                        AND hda.IsActive = 1
                  )
              )
              OR
              (
                  i.TargetHostTemplateId IS NOT NULL
                  AND existing.HostId IS NOT NULL
                  AND EXISTS
                  (
                      SELECT 1
                      FROM omp.HostDeploymentAssignments hda
                      WHERE hda.HostId = existing.HostId
                        AND hda.HostTemplateId = i.TargetHostTemplateId
                        AND hda.IsActive = 1
                  )
              )
          )
    )
    BEGIN
        THROW 51059, 'Do not mix active host-role and overlapping host-specific web app instances for the same module instance and web app definition.', 1;
    END;
END
GO

IF EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances tai
    INNER JOIN omp.Apps a ON a.AppId = tai.AppId
    WHERE tai.InstanceTemplateHostId IS NOT NULL
      AND tai.TargetHostTemplateId IS NULL
      AND tai.IsEnabled = 1
      AND tai.IsAllowed = 1
      AND tai.DesiredState = 1
      AND a.AppType IN (N'Portal', N'WebApp')
      AND a.AllowMultipleActiveInstances = 0
    GROUP BY tai.InstanceTemplateModuleInstanceId, tai.InstanceTemplateHostId, tai.AppId
    HAVING COUNT(1) > 1
)
BEGIN
    THROW 51054, 'Duplicate active host-specific template web app rows exist. Keep only one active desired row per template module, web app definition and template host.', 1;
END
GO

IF EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances tai
    INNER JOIN omp.Apps a ON a.AppId = tai.AppId
    WHERE tai.InstanceTemplateHostId IS NULL
      AND tai.TargetHostTemplateId IS NULL
      AND tai.IsEnabled = 1
      AND tai.IsAllowed = 1
      AND tai.DesiredState = 1
      AND a.AppType IN (N'Portal', N'WebApp')
      AND a.AllowMultipleActiveInstances = 0
    GROUP BY tai.InstanceTemplateModuleInstanceId, tai.AppId
    HAVING COUNT(1) > 1
)
BEGIN
    THROW 51055, 'Duplicate active host-neutral template web app rows exist. Keep only one active desired host-neutral row per template module and web app definition.', 1;
END
GO

IF EXISTS
(
    SELECT 1
    FROM omp.InstanceTemplateAppInstances tai
    INNER JOIN omp.Apps a ON a.AppId = tai.AppId
    WHERE tai.InstanceTemplateHostId IS NULL
      AND tai.TargetHostTemplateId IS NOT NULL
      AND tai.IsEnabled = 1
      AND tai.IsAllowed = 1
      AND tai.DesiredState = 1
      AND a.AppType IN (N'Portal', N'WebApp')
      AND a.AllowMultipleActiveInstances = 0
    GROUP BY tai.InstanceTemplateModuleInstanceId, tai.TargetHostTemplateId, tai.AppId
    HAVING COUNT(1) > 1
)
BEGIN
    THROW 51060, 'Duplicate active host-role template web app rows exist. Keep only one active desired row per template module, web app definition and host role.', 1;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.InstanceTemplateAppInstances')
      AND name = N'UX_omp_InstanceTemplateAppInstances_Active_Module_Host_App'
)
BEGIN
    DROP INDEX UX_omp_InstanceTemplateAppInstances_Active_Module_Host_App ON omp.InstanceTemplateAppInstances;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.InstanceTemplateAppInstances')
      AND name = N'UX_omp_InstanceTemplateAppInstances_Active_Module_HostNeutral_App'
)
BEGIN
    DROP INDEX UX_omp_InstanceTemplateAppInstances_Active_Module_HostNeutral_App ON omp.InstanceTemplateAppInstances;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.InstanceTemplateAppInstances')
      AND name = N'IX_omp_InstanceTemplateAppInstances_Active_Module_Host_App'
      AND ISNULL(filter_definition, N'') NOT LIKE N'%TargetHostTemplateId%'
)
BEGIN
    DROP INDEX IX_omp_InstanceTemplateAppInstances_Active_Module_Host_App ON omp.InstanceTemplateAppInstances;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.InstanceTemplateAppInstances')
      AND name = N'IX_omp_InstanceTemplateAppInstances_Active_Module_Host_App'
)
BEGIN
    CREATE INDEX IX_omp_InstanceTemplateAppInstances_Active_Module_Host_App
        ON omp.InstanceTemplateAppInstances(InstanceTemplateModuleInstanceId, InstanceTemplateHostId, AppId, AppInstanceKey)
        WHERE InstanceTemplateHostId IS NOT NULL
          AND TargetHostTemplateId IS NULL
          AND IsEnabled = 1
          AND IsAllowed = 1
          AND DesiredState = 1;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.InstanceTemplateAppInstances')
      AND name = N'IX_omp_InstanceTemplateAppInstances_Active_Module_HostNeutral_App'
      AND ISNULL(filter_definition, N'') NOT LIKE N'%TargetHostTemplateId%'
)
BEGIN
    DROP INDEX IX_omp_InstanceTemplateAppInstances_Active_Module_HostNeutral_App ON omp.InstanceTemplateAppInstances;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.InstanceTemplateAppInstances')
      AND name = N'IX_omp_InstanceTemplateAppInstances_Active_Module_HostRole_App'
)
BEGIN
    CREATE INDEX IX_omp_InstanceTemplateAppInstances_Active_Module_HostRole_App
        ON omp.InstanceTemplateAppInstances(InstanceTemplateModuleInstanceId, TargetHostTemplateId, AppId, AppInstanceKey)
        WHERE TargetHostTemplateId IS NOT NULL
          AND IsEnabled = 1
          AND IsAllowed = 1
          AND DesiredState = 1;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.InstanceTemplateAppInstances')
      AND name = N'IX_omp_InstanceTemplateAppInstances_Active_Module_HostNeutral_App'
)
BEGIN
    CREATE INDEX IX_omp_InstanceTemplateAppInstances_Active_Module_HostNeutral_App
        ON omp.InstanceTemplateAppInstances(InstanceTemplateModuleInstanceId, AppId, AppInstanceKey)
        WHERE InstanceTemplateHostId IS NULL
          AND TargetHostTemplateId IS NULL
          AND IsEnabled = 1
          AND IsAllowed = 1
          AND DesiredState = 1;
END
GO

IF OBJECT_ID(N'omp.TR_InstanceTemplateAppInstances_ValidateActivePlacement', N'TR') IS NULL
    EXEC(N'CREATE TRIGGER omp.TR_InstanceTemplateAppInstances_ValidateActivePlacement ON omp.InstanceTemplateAppInstances AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END');
GO

ALTER TRIGGER omp.TR_InstanceTemplateAppInstances_ValidateActivePlacement
ON omp.InstanceTemplateAppInstances
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN omp.Apps a
            ON a.AppId = i.AppId
           AND a.AppType IN (N'Portal', N'WebApp')
           AND a.AllowMultipleActiveInstances = 0
        INNER JOIN omp.InstanceTemplateAppInstances existing
            ON existing.InstanceTemplateModuleInstanceId = i.InstanceTemplateModuleInstanceId
           AND existing.AppId = i.AppId
           AND existing.InstanceTemplateAppInstanceId <> i.InstanceTemplateAppInstanceId
           AND
           (
               (i.InstanceTemplateHostId IS NULL AND i.TargetHostTemplateId IS NULL AND existing.InstanceTemplateHostId IS NULL AND existing.TargetHostTemplateId IS NULL)
               OR (i.InstanceTemplateHostId IS NOT NULL AND existing.InstanceTemplateHostId = i.InstanceTemplateHostId)
               OR (i.TargetHostTemplateId IS NOT NULL AND existing.TargetHostTemplateId = i.TargetHostTemplateId)
           )
        WHERE i.IsEnabled = 1
          AND i.IsAllowed = 1
          AND i.DesiredState = 1
          AND existing.IsEnabled = 1
          AND existing.IsAllowed = 1
          AND existing.DesiredState = 1
    )
    BEGIN
        THROW 51056, 'Only one active desired template web app row is allowed per template module, web app definition and host placement.', 1;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN omp.Apps a
            ON a.AppId = i.AppId
           AND a.AppType IN (N'Portal', N'WebApp')
           AND a.AllowMultipleActiveInstances = 0
        INNER JOIN omp.InstanceTemplateAppInstances existing
            ON existing.InstanceTemplateModuleInstanceId = i.InstanceTemplateModuleInstanceId
           AND existing.AppId = i.AppId
           AND existing.InstanceTemplateAppInstanceId <> i.InstanceTemplateAppInstanceId
           AND
           (
               (i.InstanceTemplateHostId IS NULL AND i.TargetHostTemplateId IS NULL AND (existing.InstanceTemplateHostId IS NOT NULL OR existing.TargetHostTemplateId IS NOT NULL))
               OR ((i.InstanceTemplateHostId IS NOT NULL OR i.TargetHostTemplateId IS NOT NULL) AND existing.InstanceTemplateHostId IS NULL AND existing.TargetHostTemplateId IS NULL)
           )
        WHERE i.IsEnabled = 1
          AND i.IsAllowed = 1
          AND i.DesiredState = 1
          AND existing.IsEnabled = 1
          AND existing.IsAllowed = 1
          AND existing.DesiredState = 1
    )
    BEGIN
        THROW 51057, 'Do not mix active host-neutral and targeted template web app rows for the same template module and web app definition.', 1;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        INNER JOIN omp.Apps a
            ON a.AppId = i.AppId
           AND a.AppType IN (N'Portal', N'WebApp')
           AND a.AllowMultipleActiveInstances = 0
        INNER JOIN omp.InstanceTemplateAppInstances existing
            ON existing.InstanceTemplateModuleInstanceId = i.InstanceTemplateModuleInstanceId
           AND existing.AppId = i.AppId
           AND existing.InstanceTemplateAppInstanceId <> i.InstanceTemplateAppInstanceId
        WHERE i.IsEnabled = 1
          AND i.IsAllowed = 1
          AND i.DesiredState = 1
          AND existing.IsEnabled = 1
          AND existing.IsAllowed = 1
          AND existing.DesiredState = 1
          AND
          (
              (
                  i.InstanceTemplateHostId IS NOT NULL
                  AND existing.TargetHostTemplateId IS NOT NULL
                  AND EXISTS
                  (
                      SELECT 1
                      FROM omp.InstanceTemplateHosts ith
                      WHERE ith.InstanceTemplateHostId = i.InstanceTemplateHostId
                        AND ith.HostTemplateId = existing.TargetHostTemplateId
                  )
              )
              OR
              (
                  i.TargetHostTemplateId IS NOT NULL
                  AND existing.InstanceTemplateHostId IS NOT NULL
                  AND EXISTS
                  (
                      SELECT 1
                      FROM omp.InstanceTemplateHosts ith
                      WHERE ith.InstanceTemplateHostId = existing.InstanceTemplateHostId
                        AND ith.HostTemplateId = i.TargetHostTemplateId
                  )
              )
          )
    )
    BEGIN
        THROW 51061, 'Do not mix active host-role and overlapping host-specific template web app rows for the same template module and web app definition.', 1;
    END;
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

IF OBJECT_ID(N'omp.MaterializeInstanceTemplate', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE omp.MaterializeInstanceTemplate AS BEGIN SET NOCOUNT ON; END');
GO

ALTER PROCEDURE omp.MaterializeInstanceTemplate
    @InstanceKey nvarchar(100) = NULL,
    @HostKey nvarchar(128) = NULL,
    @HostTemplateId int = NULL,
    @RequestedBy nvarchar(256) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @InstanceKey = NULLIF(LTRIM(RTRIM(@InstanceKey)), N'');
    SET @HostKey = NULLIF(LTRIM(RTRIM(@HostKey)), N'');
    SET @RequestedBy = NULLIF(LTRIM(RTRIM(@RequestedBy)), N'');

    DECLARE @ModuleActions TABLE(ActionName nvarchar(10) NOT NULL);
    DECLARE @AppActions TABLE(ActionName nvarchar(10) NOT NULL);

    IF @HostKey IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM omp.Hosts h
           INNER JOIN omp.Instances i ON i.InstanceId = h.InstanceId
           WHERE h.HostKey = @HostKey
             AND h.IsEnabled = 1
             AND i.IsEnabled = 1
             AND (@InstanceKey IS NULL OR i.InstanceKey = @InstanceKey)
       )
    BEGIN
        THROW 51030, 'Template materialization host was not found or is disabled.', 1;
    END;

    IF @HostKey IS NOT NULL
       AND @HostTemplateId IS NOT NULL
       AND NOT EXISTS
       (
           SELECT 1
           FROM omp.Hosts h
           INNER JOIN omp.HostDeploymentAssignments hda
               ON hda.HostId = h.HostId
              AND hda.HostTemplateId = @HostTemplateId
              AND hda.IsActive = 1
           WHERE h.HostKey = @HostKey
             AND h.IsEnabled = 1
       )
    BEGIN
        THROW 51031, 'Template materialization host does not have the requested active host template assignment.', 1;
    END;

    ;WITH SourceModules AS
    (
        SELECT
            i.InstanceId,
            tmi.ModuleId,
            tmi.ModuleInstanceKey,
            tmi.DisplayName,
            tmi.Description,
            tmi.SortOrder
        FROM omp.Instances i
        INNER JOIN omp.InstanceTemplates it ON it.InstanceTemplateId = i.InstanceTemplateId
        INNER JOIN omp.InstanceTemplateModuleInstances tmi ON tmi.InstanceTemplateId = it.InstanceTemplateId
        INNER JOIN omp.Modules m ON m.ModuleId = tmi.ModuleId
        WHERE i.IsEnabled = 1
          AND it.IsEnabled = 1
          AND tmi.IsEnabled = 1
          AND m.IsEnabled = 1
          AND (@InstanceKey IS NULL OR i.InstanceKey = @InstanceKey)
          AND
          (
              @HostKey IS NULL
              OR EXISTS
              (
                  SELECT 1
                  FROM omp.Hosts h
                  WHERE h.InstanceId = i.InstanceId
                    AND h.HostKey = @HostKey
                    AND h.IsEnabled = 1
              )
          )
    )
    MERGE omp.ModuleInstances AS target
    USING SourceModules AS source
    ON target.InstanceId = source.InstanceId
    AND target.ModuleInstanceKey = source.ModuleInstanceKey
    WHEN MATCHED AND
    (
        target.ModuleId <> source.ModuleId
        OR target.DisplayName <> source.DisplayName
        OR ISNULL(target.Description, N'') <> ISNULL(source.Description, N'')
        OR target.IsEnabled <> CONVERT(bit, 1)
        OR target.SortOrder <> source.SortOrder
    ) THEN
        UPDATE SET ModuleId = source.ModuleId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   IsEnabled = 1,
                   SortOrder = source.SortOrder,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName, Description, IsEnabled, SortOrder)
        VALUES(NEWID(), source.InstanceId, source.ModuleId, source.ModuleInstanceKey, source.DisplayName, source.Description, 1, source.SortOrder)
    OUTPUT $action INTO @ModuleActions(ActionName);

    ;WITH ConcreteModules AS
    (
        SELECT
            i.InstanceId,
            tmi.InstanceTemplateModuleInstanceId,
            mi.ModuleInstanceId
        FROM omp.Instances i
        INNER JOIN omp.InstanceTemplates it ON it.InstanceTemplateId = i.InstanceTemplateId
        INNER JOIN omp.InstanceTemplateModuleInstances tmi ON tmi.InstanceTemplateId = it.InstanceTemplateId
        INNER JOIN omp.ModuleInstances mi
            ON mi.InstanceId = i.InstanceId
           AND mi.ModuleInstanceKey = tmi.ModuleInstanceKey
        WHERE i.IsEnabled = 1
          AND it.IsEnabled = 1
          AND tmi.IsEnabled = 1
          AND mi.IsEnabled = 1
          AND (@InstanceKey IS NULL OR i.InstanceKey = @InstanceKey)
    ),
    HostMap AS
    (
        SELECT
            i.InstanceId,
            ith.InstanceTemplateHostId,
            ith.HostTemplateId,
            h.HostId
        FROM omp.Instances i
        INNER JOIN omp.InstanceTemplates it ON it.InstanceTemplateId = i.InstanceTemplateId
        INNER JOIN omp.InstanceTemplateHosts ith ON ith.InstanceTemplateId = it.InstanceTemplateId
        INNER JOIN omp.Hosts h
            ON h.InstanceId = i.InstanceId
           AND h.HostKey = ith.HostKey
        INNER JOIN omp.HostDeploymentAssignments hda
            ON hda.HostId = h.HostId
           AND hda.HostTemplateId = ith.HostTemplateId
           AND hda.IsActive = 1
        WHERE i.IsEnabled = 1
          AND it.IsEnabled = 1
          AND ith.IsEnabled = 1
          AND h.IsEnabled = 1
          AND (@InstanceKey IS NULL OR i.InstanceKey = @InstanceKey)
          AND (@HostKey IS NULL OR h.HostKey = @HostKey)
          AND (@HostTemplateId IS NULL OR ith.HostTemplateId = @HostTemplateId)
    ),
    SourceApps AS
    (
        SELECT
            cm.ModuleInstanceId,
            hm.HostId,
            tai.TargetHostTemplateId,
            tai.AppId,
            tai.AppInstanceKey,
            tai.DisplayName,
            tai.Description,
            tai.RoutePath,
            tai.PublicUrl,
            tai.InstallPath,
            tai.InstallationName,
            tai.DesiredArtifactId AS ArtifactId,
            tai.DesiredConfigId AS ConfigId,
            tai.ExpectedLogin,
            tai.ExpectedClientHostName,
            tai.ExpectedClientIp,
            tai.DesiredState,
            tai.SortOrder,
            tai.IsEnabled,
            tai.IsAllowed
        FROM omp.InstanceTemplateAppInstances tai
        INNER JOIN ConcreteModules cm
            ON cm.InstanceTemplateModuleInstanceId = tai.InstanceTemplateModuleInstanceId
        INNER JOIN omp.Apps a ON a.AppId = tai.AppId
        LEFT JOIN HostMap hm
            ON hm.InstanceId = cm.InstanceId
           AND hm.InstanceTemplateHostId = tai.InstanceTemplateHostId
        WHERE tai.IsEnabled = 1
          AND a.IsEnabled = 1
          -- Host-neutral and host-role template apps are logical desired app rows.
          -- They must materialize even when a HostAgent requests only the
          -- concrete host it is currently managing. HostAgent later checks the
          -- current host's active role assignment before deployment.
          AND
          (
              tai.InstanceTemplateHostId IS NULL
              OR hm.HostId IS NOT NULL
          )
    )
    MERGE omp.AppInstances AS target
    USING SourceApps AS source
    ON target.ModuleInstanceId = source.ModuleInstanceId
    AND target.AppInstanceKey = source.AppInstanceKey
    WHEN MATCHED AND
    (
        ISNULL(target.HostId, '00000000-0000-0000-0000-000000000000') <> ISNULL(source.HostId, '00000000-0000-0000-0000-000000000000')
        OR ISNULL(target.TargetHostTemplateId, -1) <> ISNULL(source.TargetHostTemplateId, -1)
        OR target.AppId <> source.AppId
        OR target.DisplayName <> source.DisplayName
        OR ISNULL(target.Description, N'') <> ISNULL(source.Description, N'')
        OR ISNULL(target.RoutePath, N'') <> ISNULL(source.RoutePath, N'')
        OR ISNULL(target.PublicUrl, N'') <> ISNULL(source.PublicUrl, N'')
        OR ISNULL(target.InstallPath, N'') <> ISNULL(source.InstallPath, N'')
        OR ISNULL(target.InstallationName, N'') <> ISNULL(source.InstallationName, N'')
        OR ISNULL(target.ArtifactId, -1) <> ISNULL(source.ArtifactId, -1)
        OR ISNULL(target.ConfigId, -1) <> ISNULL(source.ConfigId, -1)
        OR ISNULL(target.ExpectedLogin, N'') <> ISNULL(source.ExpectedLogin, N'')
        OR ISNULL(target.ExpectedClientHostName, N'') <> ISNULL(source.ExpectedClientHostName, N'')
        OR ISNULL(target.ExpectedClientIp, N'') <> ISNULL(source.ExpectedClientIp, N'')
        OR target.IsEnabled <> source.IsEnabled
        OR target.IsAllowed <> source.IsAllowed
        OR target.DesiredState <> source.DesiredState
        OR target.SortOrder <> source.SortOrder
    ) THEN
        UPDATE SET HostId = source.HostId,
                   TargetHostTemplateId = source.TargetHostTemplateId,
                   AppId = source.AppId,
                   DisplayName = source.DisplayName,
                   Description = source.Description,
                   RoutePath = source.RoutePath,
                   PublicUrl = source.PublicUrl,
                   InstallPath = source.InstallPath,
                   InstallationName = source.InstallationName,
                   ArtifactId = source.ArtifactId,
                   ConfigId = source.ConfigId,
                   ExpectedLogin = source.ExpectedLogin,
                   ExpectedClientHostName = source.ExpectedClientHostName,
                   ExpectedClientIp = source.ExpectedClientIp,
                   IsEnabled = source.IsEnabled,
                   IsAllowed = source.IsAllowed,
                   DesiredState = source.DesiredState,
                   SortOrder = source.SortOrder,
                   UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT(
            AppInstanceId,
            ModuleInstanceId,
            HostId,
            TargetHostTemplateId,
            AppId,
            AppInstanceKey,
            DisplayName,
            Description,
            RoutePath,
            PublicUrl,
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
            NEWID(),
            source.ModuleInstanceId,
            source.HostId,
            source.TargetHostTemplateId,
            source.AppId,
            source.AppInstanceKey,
            source.DisplayName,
            source.Description,
            source.RoutePath,
            source.PublicUrl,
            source.InstallPath,
            source.InstallationName,
            source.ArtifactId,
            source.ConfigId,
            source.ExpectedLogin,
            source.ExpectedClientHostName,
            source.ExpectedClientIp,
            source.IsEnabled,
            source.IsAllowed,
            source.DesiredState,
            source.SortOrder)
    OUTPUT $action INTO @AppActions(ActionName);

    SELECT
        CAST((SELECT COUNT(1) FROM @ModuleActions) AS int) AS ModuleInstanceChanges,
        CAST((SELECT COUNT(1) FROM @AppActions) AS int) AS AppInstanceChanges,
        @InstanceKey AS InstanceKey,
        @HostKey AS HostKey,
        @RequestedBy AS RequestedBy;
END
GO

IF OBJECT_ID(N'omp.RequestHostDeployment', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE omp.RequestHostDeployment AS BEGIN SET NOCOUNT ON; END');
GO

ALTER PROCEDURE omp.RequestHostDeployment
    @HostKey nvarchar(128),
    @HostTemplateKey nvarchar(100) = NULL,
    @RequestedBy nvarchar(256) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @HostKey = NULLIF(LTRIM(RTRIM(@HostKey)), N'');
    SET @HostTemplateKey = NULLIF(LTRIM(RTRIM(@HostTemplateKey)), N'');
    SET @RequestedBy = NULLIF(LTRIM(RTRIM(@RequestedBy)), N'');

    DECLARE @HostId uniqueidentifier;
    DECLARE @HostTemplateId int;

    IF @HostKey IS NULL
        THROW 51040, 'Host deployment request requires HostKey.', 1;

    SELECT @HostId = HostId
    FROM omp.Hosts
    WHERE HostKey = @HostKey
      AND IsEnabled = 1;

    IF @HostId IS NULL
        THROW 51041, 'Host deployment request host was not found or is disabled.', 1;

    IF @HostTemplateKey IS NOT NULL
    BEGIN
        SELECT @HostTemplateId = HostTemplateId
        FROM omp.HostTemplates
        WHERE TemplateKey = @HostTemplateKey
          AND IsEnabled = 1;

        IF @HostTemplateId IS NULL
            THROW 51042, 'Host deployment request host template was not found or is disabled.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM omp.HostDeploymentAssignments
            WHERE HostId = @HostId
              AND HostTemplateId = @HostTemplateId
              AND IsActive = 1
        )
        BEGIN
            THROW 51043, 'Host deployment request host template is not actively assigned to the host.', 1;
        END;
    END;

    INSERT INTO omp.HostDeployments(HostId, HostTemplateId, RequestedBy, Status)
    VALUES(@HostId, @HostTemplateId, @RequestedBy, 0);

    SELECT
        CONVERT(bigint, SCOPE_IDENTITY()) AS HostDeploymentId,
        @HostId AS HostId,
        @HostTemplateId AS HostTemplateId;
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

        -- Optional user-managed profile image metadata. The physical file is
        -- kept in controlled server storage and is served through an
        -- authenticated endpoint.
        profile_image_file_name nvarchar(260) NULL,
        profile_image_storage_key nvarchar(260) NULL,

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

IF OBJECT_ID(N'omp.users', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.users', N'profile_image_file_name') IS NULL
BEGIN
    ALTER TABLE omp.users
    ADD profile_image_file_name nvarchar(260) NULL;
END
GO

IF OBJECT_ID(N'omp.users', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.users', N'profile_image_storage_key') IS NULL
BEGIN
    ALTER TABLE omp.users
    ADD profile_image_storage_key nvarchar(260) NULL;
END
GO

-------------------------------------------------------------------------------
-- Notifications
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.notifications', N'U') IS NULL
BEGIN
    CREATE TABLE omp.notifications
    (
        notification_id bigint IDENTITY(1,1) NOT NULL,

        -- user_id > 0 targets a personal OMP user. user_id = 0 is reserved for
        -- future system-wide notification semantics and is intentionally
        -- validated in application services before personal notifications are created.
        user_id int NOT NULL,

        title nvarchar(200) NOT NULL,
        content nvarchar(1000) NOT NULL,
        destination_url nvarchar(600) NULL,
        level nvarchar(40) NOT NULL CONSTRAINT DF_omp_notifications_level DEFAULT(N'info'),
        caller_key nvarchar(200) NULL,
        caller_display_name nvarchar(200) NULL,
        caller_icon nvarchar(600) NULL,
        status nvarchar(40) NOT NULL CONSTRAINT DF_omp_notifications_status DEFAULT(N'unread'),
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_notifications_created_at DEFAULT SYSUTCDATETIME(),
        expires_at datetime2(3) NULL,
        read_at datetime2(3) NULL,

        CONSTRAINT PK_omp_notifications PRIMARY KEY(notification_id),
        CONSTRAINT CK_omp_notifications_user_id CHECK(user_id >= 0),
        CONSTRAINT CK_omp_notifications_level CHECK(level IN (N'info', N'success', N'warning', N'error')),
        CONSTRAINT CK_omp_notifications_status CHECK(status IN (N'unread', N'read'))
    );
END
GO

IF OBJECT_ID(N'omp.notifications', N'U') IS NOT NULL
BEGIN
    UPDATE omp.notifications
    SET level = N'info',
        status = N'read',
        read_at = COALESCE(read_at, SYSUTCDATETIME()),
        expires_at = COALESCE(expires_at, SYSUTCDATETIME())
    WHERE level = N'banner';

    IF EXISTS
    (
        SELECT 1
        FROM sys.check_constraints
        WHERE name = N'CK_omp_notifications_level'
          AND parent_object_id = OBJECT_ID(N'omp.notifications')
    )
    BEGIN
        ALTER TABLE omp.notifications DROP CONSTRAINT CK_omp_notifications_level;
    END;

    ALTER TABLE omp.notifications WITH CHECK
        ADD CONSTRAINT CK_omp_notifications_level
            CHECK(level IN (N'info', N'success', N'warning', N'error'));
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_notifications_user_status_created'
      AND object_id = OBJECT_ID(N'omp.notifications')
)
BEGIN
    CREATE INDEX IX_omp_notifications_user_status_created
        ON omp.notifications(user_id, status, read_at, created_at DESC)
        INCLUDE(title, content, destination_url, level, caller_key, caller_display_name, caller_icon, expires_at);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_notifications_user_created'
      AND object_id = OBJECT_ID(N'omp.notifications')
)
BEGIN
    CREATE INDEX IX_omp_notifications_user_created
        ON omp.notifications(user_id, created_at DESC, notification_id DESC)
        INCLUDE(title, content, destination_url, level, caller_key, caller_display_name, caller_icon, status, read_at, expires_at);
END
GO

-------------------------------------------------------------------------------
-- Banners
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.banners', N'U') IS NULL
BEGIN
    CREATE TABLE omp.banners
    (
        banner_id bigint IDENTITY(1,1) NOT NULL,
        title nvarchar(200) NOT NULL,
        content nvarchar(1000) NOT NULL,
        status nvarchar(40) NOT NULL CONSTRAINT DF_omp_banners_status DEFAULT(N'active'),
        level int NOT NULL CONSTRAINT DF_omp_banners_level DEFAULT(1),
        starts_at datetime2(3) NULL,
        expires_at datetime2(3) NULL,
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_banners_created_at DEFAULT SYSUTCDATETIME(),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_banners_updated_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_banners PRIMARY KEY(banner_id),
        CONSTRAINT CK_omp_banners_status CHECK(status IN (N'active', N'disabled')),
        CONSTRAINT CK_omp_banners_level CHECK(level IN (1, 2, 3)),
        CONSTRAINT CK_omp_banners_window CHECK(expires_at IS NULL OR starts_at IS NULL OR expires_at > starts_at)
    );
END
GO

IF OBJECT_ID(N'omp.banner_targets', N'U') IS NULL
BEGIN
    CREATE TABLE omp.banner_targets
    (
        banner_target_id bigint IDENTITY(1,1) NOT NULL,
        banner_id bigint NOT NULL,
        target_type nvarchar(40) NOT NULL,
        role_id int NULL,

        CONSTRAINT PK_omp_banner_targets PRIMARY KEY(banner_target_id),
        CONSTRAINT FK_omp_banner_targets_banner FOREIGN KEY(banner_id) REFERENCES omp.banners(banner_id) ON DELETE CASCADE,
        CONSTRAINT FK_omp_banner_targets_role FOREIGN KEY(role_id) REFERENCES omp.Roles(RoleId),
        CONSTRAINT CK_omp_banner_targets_type CHECK(target_type IN (N'global', N'role')),
        CONSTRAINT CK_omp_banner_targets_role CHECK
        (
            (target_type = N'global' AND role_id IS NULL)
            OR (target_type = N'role' AND role_id IS NOT NULL)
        ),
        CONSTRAINT UQ_omp_banner_targets_unique UNIQUE(banner_id, target_type, role_id)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_banners_status_window'
      AND object_id = OBJECT_ID(N'omp.banners')
)
BEGIN
    CREATE INDEX IX_omp_banners_status_window
        ON omp.banners(status, starts_at, expires_at, level DESC, created_at DESC)
        INCLUDE(title, content);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_banner_targets_lookup'
      AND object_id = OBJECT_ID(N'omp.banner_targets')
)
BEGIN
    CREATE INDEX IX_omp_banner_targets_lookup
        ON omp.banner_targets(target_type, role_id, banner_id);
END
GO

-------------------------------------------------------------------------------
-- Messages
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.conversations', N'U') IS NULL
BEGIN
    CREATE TABLE omp.conversations
    (
        conversation_id bigint IDENTITY(1,1) NOT NULL,
        conversation_type nvarchar(40) NOT NULL,
        title nvarchar(200) NULL,
        created_by_user_id int NOT NULL,
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_conversations_created_at DEFAULT SYSUTCDATETIME(),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_conversations_updated_at DEFAULT SYSUTCDATETIME(),
        last_message_at datetime2(3) NULL,

        CONSTRAINT PK_omp_conversations PRIMARY KEY(conversation_id),
        CONSTRAINT FK_omp_conversations_created_by_user FOREIGN KEY(created_by_user_id) REFERENCES omp.users(user_id),
        CONSTRAINT CK_omp_conversations_type CHECK(conversation_type IN (N'direct', N'group'))
    );
END
GO

IF OBJECT_ID(N'omp.messages', N'U') IS NULL
BEGIN
    CREATE TABLE omp.messages
    (
        message_id bigint IDENTITY(1,1) NOT NULL,
        conversation_id bigint NOT NULL,
        sender_user_id int NOT NULL,
        content nvarchar(max) NULL,
        message_type nvarchar(40) NOT NULL CONSTRAINT DF_omp_messages_message_type DEFAULT(N'text'),
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_messages_created_at DEFAULT SYSUTCDATETIME(),
        edited_at datetime2(3) NULL,
        deleted_at datetime2(3) NULL,

        CONSTRAINT PK_omp_messages PRIMARY KEY(message_id),
        CONSTRAINT FK_omp_messages_conversation FOREIGN KEY(conversation_id) REFERENCES omp.conversations(conversation_id) ON DELETE CASCADE,
        CONSTRAINT FK_omp_messages_sender_user FOREIGN KEY(sender_user_id) REFERENCES omp.users(user_id),
        CONSTRAINT CK_omp_messages_type CHECK(message_type IN (N'text', N'system'))
    );
END
GO

IF OBJECT_ID(N'omp.conversation_participants', N'U') IS NULL
BEGIN
    CREATE TABLE omp.conversation_participants
    (
        conversation_id bigint NOT NULL,
        user_id int NOT NULL,
        joined_at datetime2(3) NOT NULL CONSTRAINT DF_omp_conversation_participants_joined_at DEFAULT SYSUTCDATETIME(),
        left_at datetime2(3) NULL,
        last_read_message_id bigint NULL,

        CONSTRAINT PK_omp_conversation_participants PRIMARY KEY(conversation_id, user_id),
        CONSTRAINT FK_omp_conversation_participants_conversation FOREIGN KEY(conversation_id) REFERENCES omp.conversations(conversation_id) ON DELETE CASCADE,
        CONSTRAINT FK_omp_conversation_participants_user FOREIGN KEY(user_id) REFERENCES omp.users(user_id)
    );
END
GO

IF OBJECT_ID(N'omp.message_attachments', N'U') IS NULL
BEGIN
    CREATE TABLE omp.message_attachments
    (
        attachment_id bigint IDENTITY(1,1) NOT NULL,
        message_id bigint NOT NULL,
        file_name nvarchar(260) NOT NULL,
        content_type nvarchar(128) NOT NULL,
        file_size bigint NOT NULL,
        storage_key nvarchar(120) NOT NULL,
        data_value varbinary(max) NOT NULL,
        uploaded_by_user_id int NOT NULL,
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_message_attachments_created_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_message_attachments PRIMARY KEY(attachment_id),
        CONSTRAINT FK_omp_message_attachments_message FOREIGN KEY(message_id) REFERENCES omp.messages(message_id) ON DELETE CASCADE,
        CONSTRAINT FK_omp_message_attachments_uploaded_by_user FOREIGN KEY(uploaded_by_user_id) REFERENCES omp.users(user_id),
        CONSTRAINT UQ_omp_message_attachments_storage_key UNIQUE(storage_key),
        CONSTRAINT CK_omp_message_attachments_file_size CHECK(file_size >= 0)
    );
END
GO

IF OBJECT_ID(N'omp.direct_conversations', N'U') IS NULL
BEGIN
    CREATE TABLE omp.direct_conversations
    (
        user_low_id int NOT NULL,
        user_high_id int NOT NULL,
        conversation_id bigint NOT NULL,

        CONSTRAINT PK_omp_direct_conversations PRIMARY KEY(user_low_id, user_high_id),
        CONSTRAINT FK_omp_direct_conversations_low_user FOREIGN KEY(user_low_id) REFERENCES omp.users(user_id),
        CONSTRAINT FK_omp_direct_conversations_high_user FOREIGN KEY(user_high_id) REFERENCES omp.users(user_id),
        CONSTRAINT FK_omp_direct_conversations_conversation FOREIGN KEY(conversation_id) REFERENCES omp.conversations(conversation_id) ON DELETE CASCADE,
        CONSTRAINT UQ_omp_direct_conversations_conversation UNIQUE(conversation_id),
        CONSTRAINT CK_omp_direct_conversations_order CHECK(user_low_id < user_high_id)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_conversation_participants_user'
      AND object_id = OBJECT_ID(N'omp.conversation_participants')
)
BEGIN
    CREATE INDEX IX_omp_conversation_participants_user
        ON omp.conversation_participants(user_id, left_at, conversation_id)
        INCLUDE(last_read_message_id);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_messages_conversation_message'
      AND object_id = OBJECT_ID(N'omp.messages')
)
BEGIN
    CREATE INDEX IX_omp_messages_conversation_message
        ON omp.messages(conversation_id, message_id DESC)
        INCLUDE(sender_user_id, message_type, created_at, deleted_at);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_conversations_last_message'
      AND object_id = OBJECT_ID(N'omp.conversations')
)
BEGIN
    CREATE INDEX IX_omp_conversations_last_message
        ON omp.conversations(last_message_at DESC, updated_at DESC)
        INCLUDE(conversation_type, title);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_message_attachments_message'
      AND object_id = OBJECT_ID(N'omp.message_attachments')
)
BEGIN
    CREATE INDEX IX_omp_message_attachments_message
        ON omp.message_attachments(message_id)
        INCLUDE(file_name, content_type, file_size, storage_key, uploaded_by_user_id, created_at);
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
    EXEC sp_executesql N'
INSERT INTO omp.config_setting_definitions(ConfigCategory, ConfigSetting, Description, SortOrder, IsEnabled)
SELECT DISTINCT cs.ConfigCategory,
       cs.ConfigSetting,
       NULL,
       1000,
       1
FROM omp.config_settings cs
WHERE cs.ConfigSettingId IS NULL
  AND LTRIM(RTRIM(cs.ConfigCategory)) <> N''''
  AND LTRIM(RTRIM(cs.ConfigSetting)) <> N''''
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.config_setting_definitions existing
      WHERE existing.ConfigCategory = cs.ConfigCategory
        AND existing.ConfigSetting = cs.ConfigSetting
  );';
END
GO

IF COL_LENGTH(N'omp.config_settings', N'ConfigCategory') IS NOT NULL
   AND COL_LENGTH(N'omp.config_settings', N'ConfigSetting') IS NOT NULL
BEGIN
    EXEC sp_executesql N'
UPDATE cs
   SET ConfigSettingId = def.ConfigSettingId
FROM omp.config_settings cs
INNER JOIN omp.config_setting_definitions def
    ON def.ConfigCategory = cs.ConfigCategory
   AND def.ConfigSetting = cs.ConfigSetting
WHERE cs.ConfigSettingId IS NULL;';
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
