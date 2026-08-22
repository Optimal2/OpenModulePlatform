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
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Permissions_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_Permissions_Name UNIQUE(Name)
    );
END
GO

IF COL_LENGTH(N'omp.Permissions', N'UpdatedUtc') IS NULL
BEGIN
    ALTER TABLE omp.Permissions
        ADD UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Permissions_UpdatedUtc DEFAULT SYSUTCDATETIME() WITH VALUES;
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
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Roles_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_Roles_Name UNIQUE(Name)
    );
END
GO

IF COL_LENGTH(N'omp.Roles', N'UpdatedUtc') IS NULL
BEGIN
    ALTER TABLE omp.Roles
        ADD UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_Roles_UpdatedUtc DEFAULT SYSUTCDATETIME() WITH VALUES;
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

IF OBJECT_ID(N'omp.NormalizeCompatibilityToken', N'FN') IS NULL
    EXEC(N'CREATE FUNCTION omp.NormalizeCompatibilityToken(@Value nvarchar(4000)) RETURNS nvarchar(4000) AS BEGIN RETURN N''''; END');
GO

ALTER FUNCTION omp.NormalizeCompatibilityToken
(
    @Value nvarchar(4000)
)
RETURNS nvarchar(4000)
AS
BEGIN
    RETURN UPPER(LTRIM(RTRIM(ISNULL(@Value, N''))));
END
GO

IF OBJECT_ID(N'omp.NormalizeAppTypeCompatibilityToken', N'FN') IS NULL
    EXEC(N'CREATE FUNCTION omp.NormalizeAppTypeCompatibilityToken(@Value nvarchar(4000)) RETURNS nvarchar(4000) AS BEGIN RETURN N''''; END');
GO

ALTER FUNCTION omp.NormalizeAppTypeCompatibilityToken
(
    @Value nvarchar(4000)
)
RETURNS nvarchar(4000)
AS
BEGIN
    -- AppType values may arrive in canonical form such as ServiceApp or in a
    -- package-style spelling such as service-app. Runtime compatibility checks
    -- compare the normalized token so existing stored AppType values do not
    -- need a disruptive rename.
    RETURN REPLACE(omp.NormalizeCompatibilityToken(@Value), N'-', N'');
END
GO

DECLARE @PortalModuleKey nvarchar(100) = N'omp_portal';
DECLARE @PortalAppKey nvarchar(100) = N'omp_portal';

UPDATE app
SET AppType =
        CASE
            WHEN normalized.AppType = N'WEB'
                 AND module.ModuleKey = @PortalModuleKey
                 AND app.AppKey = @PortalAppKey
                THEN N'Portal'
            WHEN normalized.AppType = N'WEB'
                THEN N'WebApp'
            WHEN normalized.AppType = N'SERVICE'
                THEN N'ServiceApp'
            ELSE app.AppType
        END,
    UpdatedUtc = SYSUTCDATETIME()
FROM omp.Apps app
LEFT JOIN omp.Modules module ON module.ModuleId = app.ModuleId
CROSS APPLY
(
    SELECT omp.NormalizeCompatibilityToken(app.AppType) AS AppType
) normalized
WHERE (normalized.AppType = N'WEB'
       OR normalized.AppType = N'SERVICE')
  AND app.AppType <>
        CASE
            WHEN normalized.AppType = N'WEB'
                 AND module.ModuleKey = @PortalModuleKey
                 AND app.AppKey = @PortalAppKey
                THEN N'Portal'
            WHEN normalized.AppType = N'WEB'
                THEN N'WebApp'
            WHEN normalized.AppType = N'SERVICE'
                THEN N'ServiceApp'
            ELSE app.AppType
        END;
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

IF OBJECT_ID(N'omp.ModuleDefinitionConsistentArtifactSets', N'U') IS NULL
BEGIN
    CREATE TABLE omp.ModuleDefinitionConsistentArtifactSets
    (
        ModuleDefinitionConsistentArtifactSetId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_ModuleDefinitionConsistentArtifactSets PRIMARY KEY,
        ModuleDefinitionDocumentId int NOT NULL,
        SetKey nvarchar(100) NOT NULL,
        Description nvarchar(500) NULL,
        VersionMatchRule nvarchar(50) NOT NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionConsistentArtifactSets_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_ModuleDefinitionConsistentArtifactSets_Document
            FOREIGN KEY(ModuleDefinitionDocumentId)
            REFERENCES omp.ModuleDefinitionDocuments(ModuleDefinitionDocumentId)
            ON DELETE CASCADE,
        CONSTRAINT UQ_omp_ModuleDefinitionConsistentArtifactSets_SetKey
            UNIQUE(ModuleDefinitionDocumentId, SetKey)
    );
END
GO

IF OBJECT_ID(N'omp.ModuleDefinitionConsistentArtifactSetMembers', N'U') IS NULL
BEGIN
    CREATE TABLE omp.ModuleDefinitionConsistentArtifactSetMembers
    (
        ModuleDefinitionConsistentArtifactSetMemberId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_ModuleDefinitionConsistentArtifactSetMembers PRIMARY KEY,
        ModuleDefinitionConsistentArtifactSetId int NOT NULL,
        AppKey nvarchar(100) NOT NULL,
        PackageType nvarchar(50) NOT NULL,
        TargetName nvarchar(100) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_ModuleDefinitionConsistentArtifactSetMembers_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_ModuleDefinitionConsistentArtifactSetMembers_Set
            FOREIGN KEY(ModuleDefinitionConsistentArtifactSetId)
            REFERENCES omp.ModuleDefinitionConsistentArtifactSets(ModuleDefinitionConsistentArtifactSetId)
            ON DELETE CASCADE,
        CONSTRAINT UQ_omp_ModuleDefinitionConsistentArtifactSetMembers_Member
            UNIQUE(ModuleDefinitionConsistentArtifactSetId, AppKey, PackageType, TargetName)
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
    -- PackageType normally maps to an executable runtime contract such as
    -- web-app, service-app, worker, host-agent, or worker-host. Some module
    -- definitions also publish metadata-only compatibility artifacts, for
    -- example channel-type. Those artifacts may be stored and referenced by
    -- module-owned metadata, but runtime binding triggers reject them for
    -- AppInstances, WorkerInstances, and template app instance artifacts.
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

-- Artifact identity is (AppId, Version, PackageType, TargetName). This block
-- sits outside the create-guard above so existing databases also receive the
-- index when the module definition is re-applied. SQL Server treats NULL key
-- values as equal in unique indexes, so one unfiltered index also blocks
-- duplicate NULL-target identities. On databases that already contain
-- duplicate identities this fails loudly at apply time; that is the intended
-- pre-deploy cleanup signal.
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.Artifacts')
      AND name = N'UX_omp_Artifacts_App_Version_Package_Target'
)
BEGIN
    CREATE UNIQUE INDEX UX_omp_Artifacts_App_Version_Package_Target
        ON omp.Artifacts(AppId, Version, PackageType, TargetName);
END
GO

IF OBJECT_ID(N'omp.IsArtifactPackageCompatibleWithAppType', N'FN') IS NULL
    EXEC(N'CREATE FUNCTION omp.IsArtifactPackageCompatibleWithAppType(@PackageType nvarchar(50), @AppType nvarchar(50)) RETURNS bit AS BEGIN RETURN 0; END');
GO

ALTER FUNCTION omp.IsArtifactPackageCompatibleWithAppType
(
    @PackageType nvarchar(50),
    @AppType nvarchar(50)
)
RETURNS bit
AS
BEGIN
    DECLARE @NormalizedPackageType nvarchar(50) = omp.NormalizeCompatibilityToken(@PackageType);
    -- AppType values are stored as Portal/WebApp/ServiceApp/HostAgent/WorkerHost,
    -- while PackageType values use manifest names such as web-app and service-app.
    -- Strip hyphens here so older AppType aliases and package-style spellings are
    -- accepted without changing the canonical stored AppType values.
    DECLARE @NormalizedAppType nvarchar(50) = omp.NormalizeAppTypeCompatibilityToken(@AppType);

    RETURN
    (
        CASE
            WHEN @NormalizedPackageType = N'WEB-APP'
                 AND @NormalizedAppType IN (N'PORTAL', N'WEBAPP', N'WEB')
                THEN 1
            WHEN @NormalizedPackageType = N'SERVICE-APP'
                 AND @NormalizedAppType = N'SERVICEAPP'
                THEN 1
            WHEN @NormalizedPackageType = N'WORKER'
                 AND @NormalizedAppType = N'WORKER'
                THEN 1
            WHEN @NormalizedPackageType = N'HOST-AGENT'
                 AND @NormalizedAppType = N'HOSTAGENT'
                THEN 1
            WHEN @NormalizedPackageType = N'WORKER-HOST'
                 AND @NormalizedAppType = N'WORKERHOST'
                THEN 1
            ELSE 0
        END
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
        -- Pristine content as last delivered by an artifact package or deployment
        -- profile. NULL means unknown lineage (legacy row or operator-created row).
        -- Operator edits change FileContent only; import compares FileContent with
        -- this baseline to preserve operator edits across version imports.
        PackageFileContent nvarchar(max) NULL,
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

IF COL_LENGTH(N'omp.ArtifactConfigurationFiles', N'PackageFileContent') IS NULL
BEGIN
    ALTER TABLE omp.ArtifactConfigurationFiles ADD
        PackageFileContent nvarchar(max) NULL;
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

-- At most one enabled config overlay document per overlay key and host, no
-- matter which code path (or manual SQL) wrote the rows. Application save
-- paths already enforce keep-history semantics; this filtered unique index is
-- the database-level backstop for that invariant. The block sits outside the
-- create-guard above so existing databases also receive the index when the
-- module definition is re-applied. On databases that already contain
-- duplicate enabled rows this fails loudly at apply time; that is the
-- intended pre-deploy cleanup signal.
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.ConfigOverlayDocuments')
      AND name = N'UX_omp_ConfigOverlayDocuments_Enabled_Key_Host'
)
BEGIN
    CREATE UNIQUE INDEX UX_omp_ConfigOverlayDocuments_Enabled_Key_Host
        ON omp.ConfigOverlayDocuments(OverlayKey, HostKey)
        WHERE IsEnabled = 1;
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
        -- R12-F2. Which artifact the running process was actually STARTED from, written
        -- by WorkerManager at process start. Everything else in this table describes that
        -- a process exists; without these two columns nothing anywhere recorded WHICH
        -- build it is, so a worker running 0.3.108 against a catalogue value of 0.3.110
        -- still produced "Converged: True". Deliberately NOT a foreign key to
        -- omp.Artifacts: this is a historical witness of what ran, and artifact rows are
        -- subject to retention -- a witness that blocks cleanup, or disappears with it,
        -- is not a witness.
        RuntimeArtifactId int NULL,
        RuntimeArtifactVersion nvarchar(50) NULL,
        -- The worker-host (WorkerProcessHost) build the same process was launched with.
        -- It is a second artifact and a second version, resolved separately from the
        -- worker's own, and it had exactly the same hole: omp_workerprocesshost is one of
        -- the three desired app instances no check could see the running version of.
        RuntimeHostArtifactId int NULL,
        RuntimeHostArtifactVersion nvarchar(50) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_AppInstanceRuntimeStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_AppInstanceRuntimeStates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_AppInstanceRuntimeStates_AppInstance FOREIGN KEY(AppInstanceId) REFERENCES omp.AppInstances(AppInstanceId)
    );
END
GO

-- R12-F2. See the column comment above; added idempotently for databases created
-- before the runtime version witness existed.
IF COL_LENGTH(N'omp.AppInstanceRuntimeStates', N'RuntimeArtifactId') IS NULL
BEGIN
    ALTER TABLE omp.AppInstanceRuntimeStates ADD RuntimeArtifactId int NULL;
END
GO

IF COL_LENGTH(N'omp.AppInstanceRuntimeStates', N'RuntimeArtifactVersion') IS NULL
BEGIN
    ALTER TABLE omp.AppInstanceRuntimeStates ADD RuntimeArtifactVersion nvarchar(50) NULL;
END
GO

IF COL_LENGTH(N'omp.AppInstanceRuntimeStates', N'RuntimeHostArtifactId') IS NULL
BEGIN
    ALTER TABLE omp.AppInstanceRuntimeStates ADD RuntimeHostArtifactId int NULL;
END
GO

IF COL_LENGTH(N'omp.AppInstanceRuntimeStates', N'RuntimeHostArtifactVersion') IS NULL
BEGIN
    ALTER TABLE omp.AppInstanceRuntimeStates ADD RuntimeHostArtifactVersion nvarchar(50) NULL;
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
        -- Non-blocking diagnostic warnings (e.g. OmpAuth config deviations) that do not affect deployment state.
        LastWarning nvarchar(4000) NULL,
        -- Effective OmpAuth settings extracted from the merged appsettings.json at deployment time, used for cross-app consistency comparison.
        EffectiveOmpAuthCookieName nvarchar(200) NULL,
        EffectiveOmpAuthApplicationName nvarchar(200) NULL,
        EffectiveOmpAuthDataProtectionKeyPath nvarchar(400) NULL,
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

-- Non-blocking diagnostic warnings (e.g. OmpAuth config deviations) that do not affect deployment state.
IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'LastWarning') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD LastWarning nvarchar(4000) NULL;
END
GO

-- Effective OmpAuth settings extracted from the merged appsettings.json at deployment time, used for cross-app consistency comparison.
IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'EffectiveOmpAuthCookieName') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD EffectiveOmpAuthCookieName nvarchar(200) NULL;
END
GO

IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'EffectiveOmpAuthApplicationName') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD EffectiveOmpAuthApplicationName nvarchar(200) NULL;
END
GO

IF COL_LENGTH(N'omp.HostAppDeploymentStates', N'EffectiveOmpAuthDataProtectionKeyPath') IS NULL
BEGIN
    ALTER TABLE omp.HostAppDeploymentStates ADD EffectiveOmpAuthDataProtectionKeyPath nvarchar(400) NULL;
END
GO

IF OBJECT_ID(N'omp.WebAppHealthStates', N'U') IS NULL
BEGIN
    CREATE TABLE omp.WebAppHealthStates
    (
        HostId uniqueidentifier NOT NULL,
        HealthKey nvarchar(200) NOT NULL,
        AppInstanceId uniqueidentifier NULL,
        AppKey nvarchar(200) NULL,
        DisplayName nvarchar(200) NULL,
        ProbeUrl nvarchar(1000) NULL,
        AppPoolName nvarchar(200) NULL,
        Status tinyint NOT NULL CONSTRAINT DF_omp_WebAppHealthStates_Status DEFAULT(0),
        HttpStatusCode int NULL,
        ConsecutiveFailures int NOT NULL CONSTRAINT DF_omp_WebAppHealthStates_ConsecutiveFailures DEFAULT(0),
        LastProbeUtc datetime2(3) NULL,
        LastSuccessUtc datetime2(3) NULL,
        LastFailureUtc datetime2(3) NULL,
        LastActionUtc datetime2(3) NULL,
        LastActionMessage nvarchar(1000) NULL,
        ResponseSummary nvarchar(1000) NULL,
        LastError nvarchar(4000) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_WebAppHealthStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_WebAppHealthStates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_omp_WebAppHealthStates PRIMARY KEY(HostId, HealthKey),
        CONSTRAINT FK_omp_WebAppHealthStates_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_WebAppHealthStates_AppInstance FOREIGN KEY(AppInstanceId) REFERENCES omp.AppInstances(AppInstanceId),
        CONSTRAINT CK_omp_WebAppHealthStates_Status CHECK(Status IN (0, 1, 2, 3))
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.WebAppHealthStates')
      AND name = N'IX_omp_WebAppHealthStates_Status'
)
BEGIN
    CREATE INDEX IX_omp_WebAppHealthStates_Status
        ON omp.WebAppHealthStates(Status, LastProbeUtc DESC)
        INCLUDE(HostId, HealthKey, AppKey, DisplayName, AppPoolName, ConsecutiveFailures);
END
GO

-------------------------------------------------------------------------------
-- Host resource telemetry
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.HostResourceSamples', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostResourceSamples
    (
        HostId uniqueidentifier NOT NULL,
        SampleBucketUtc datetime2(3) NOT NULL,
        SampleKey nvarchar(100) NOT NULL,
        SampleValue float NOT NULL,
        SampleCount int NOT NULL CONSTRAINT DF_omp_HostResourceSamples_SampleCount DEFAULT(1),
        FirstSampledUtc datetime2(3) NOT NULL,
        LastSampledUtc datetime2(3) NOT NULL,
        MinValue float NULL,
        MaxValue float NULL,
        CONSTRAINT PK_omp_HostResourceSamples PRIMARY KEY(HostId, SampleBucketUtc, SampleKey),
        CONSTRAINT FK_omp_HostResourceSamples_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId)
    );
END
GO

IF OBJECT_ID(N'omp.HostResourceLatest', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostResourceLatest
    (
        HostId uniqueidentifier NOT NULL,
        SampleKey nvarchar(100) NOT NULL,
        SampleValue float NOT NULL,
        SampleCount int NOT NULL CONSTRAINT DF_omp_HostResourceLatest_SampleCount DEFAULT(1),
        FirstSampledUtc datetime2(3) NOT NULL,
        LastSampledUtc datetime2(3) NOT NULL,
        MinValue float NULL,
        MaxValue float NULL,
        CONSTRAINT PK_omp_HostResourceLatest PRIMARY KEY(HostId, SampleKey),
        CONSTRAINT FK_omp_HostResourceLatest_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.HostResourceSamples')
      AND name = N'IX_omp_HostResourceSamples_Host_Key_Bucket'
)
BEGIN
    CREATE INDEX IX_omp_HostResourceSamples_Host_Key_Bucket
        ON omp.HostResourceSamples(HostId, SampleKey, SampleBucketUtc DESC)
        INCLUDE(SampleValue, SampleCount, MinValue, MaxValue);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.HostResourceSamples')
      AND name = N'IX_omp_HostResourceSamples_Bucket_Retention'
)
BEGIN
    CREATE INDEX IX_omp_HostResourceSamples_Bucket_Retention
        ON omp.HostResourceSamples(SampleBucketUtc);
END
GO

IF OBJECT_ID(N'omp.PruneHostResourceSamples', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE omp.PruneHostResourceSamples AS BEGIN SET NOCOUNT ON; END');
GO

-- Daily host resource history.
--
-- The hourly table is retained for a week, which answers "was the server busy on
-- Tuesday" and nothing about a trend. Usage on this platform grows over an autumn, so
-- the question that actually gets asked -- did CPU or memory climb *because* more people
-- started using it -- needs months of history. Simply raising the hourly retention grows
-- a table written every 60 seconds per measurement point linearly; rolling up into days
-- keeps the history and bounds the size, which is what the application performance tables
-- already do.
IF OBJECT_ID(N'omp.HostResourceSamplesDaily', N'U') IS NULL
BEGIN
    CREATE TABLE omp.HostResourceSamplesDaily
    (
        HostId uniqueidentifier NOT NULL,
        SampleDateUtc date NOT NULL,
        SampleKey nvarchar(100) NOT NULL,
        SampleCount bigint NOT NULL,
        TotalValue float NOT NULL,
        MinValue float NULL,
        MaxValue float NULL,
        FirstSampledUtc datetime2(3) NOT NULL,
        LastSampledUtc datetime2(3) NOT NULL,
        CONSTRAINT PK_omp_HostResourceSamplesDaily PRIMARY KEY(HostId, SampleDateUtc, SampleKey),
        CONSTRAINT FK_omp_HostResourceSamplesDaily_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId)
    );
END
GO

ALTER PROCEDURE omp.PruneHostResourceSamples
    @RetainHours int = 168,
    @RetainDays int = 400
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RetainHours < 1
        SET @RetainHours = 1;

    IF @RetainDays < 1
        SET @RetainDays = 1;

    DECLARE @cutoffUtc datetime2(3) = DATEADD(hour, -@RetainHours, SYSUTCDATETIME());
    DECLARE @deleted int;

    -- Fold into the daily table and drop the hourly rows in ONE transaction. Doing the
    -- two separately means a crash between them silently discards the week of detail
    -- without having preserved the summary -- and nobody notices, because the only
    -- symptom is a hole in a chart nobody is looking at yet.
    BEGIN TRANSACTION;

    MERGE omp.HostResourceSamplesDaily WITH (HOLDLOCK) AS target
    USING
    (
        SELECT HostId,
               CAST(SampleBucketUtc AS date) AS SampleDateUtc,
               SampleKey,
               SUM(CAST(SampleCount AS bigint)) AS SampleCount,
               SUM(SampleValue * SampleCount) AS TotalValue,
               MIN(MinValue) AS MinValue,
               MAX(MaxValue) AS MaxValue,
               MIN(FirstSampledUtc) AS FirstSampledUtc,
               MAX(LastSampledUtc) AS LastSampledUtc
        FROM omp.HostResourceSamples
        WHERE SampleBucketUtc < @cutoffUtc
        GROUP BY HostId, CAST(SampleBucketUtc AS date), SampleKey
    ) AS source
    ON target.HostId = source.HostId
       AND target.SampleDateUtc = source.SampleDateUtc
       AND target.SampleKey = source.SampleKey
    WHEN MATCHED THEN
        UPDATE SET
            SampleCount = target.SampleCount + source.SampleCount,
            TotalValue = target.TotalValue + source.TotalValue,
            MinValue = CASE WHEN source.MinValue < target.MinValue OR target.MinValue IS NULL THEN source.MinValue ELSE target.MinValue END,
            MaxValue = CASE WHEN source.MaxValue > target.MaxValue OR target.MaxValue IS NULL THEN source.MaxValue ELSE target.MaxValue END,
            FirstSampledUtc = CASE WHEN source.FirstSampledUtc < target.FirstSampledUtc THEN source.FirstSampledUtc ELSE target.FirstSampledUtc END,
            LastSampledUtc = CASE WHEN source.LastSampledUtc > target.LastSampledUtc THEN source.LastSampledUtc ELSE target.LastSampledUtc END
    WHEN NOT MATCHED THEN
        INSERT(HostId, SampleDateUtc, SampleKey, SampleCount, TotalValue, MinValue, MaxValue, FirstSampledUtc, LastSampledUtc)
        VALUES(source.HostId, source.SampleDateUtc, source.SampleKey, source.SampleCount, source.TotalValue, source.MinValue, source.MaxValue, source.FirstSampledUtc, source.LastSampledUtc);

    DELETE FROM omp.HostResourceSamples
    WHERE SampleBucketUtc < @cutoffUtc;

    SET @deleted = @@ROWCOUNT;

    DELETE FROM omp.HostResourceSamplesDaily
    WHERE SampleDateUtc < CAST(DATEADD(day, -@RetainDays, SYSUTCDATETIME()) AS date);

    COMMIT TRANSACTION;

    SELECT @deleted AS DeletedSampleCount;
END
GO

------------------------------------------------------------------------------
-- Durable query cost snapshots.
--
-- sys.dm_exec_query_stats is cleared by a restart and by plan cache eviction, so
-- "which query costs the most in production" is only answerable about the time since
-- the last restart. R11-Q1 and R11-Q2 were found through that view, and only because
-- the machine happened to have been up for a while -- which is luck, not a method.
--
-- This table keeps a periodic snapshot of the heaviest statements so the question can
-- be answered months later, and so a query that became slow can be shown to have
-- become slow rather than always having been.
--
-- Only this database's own statements are captured, and only ever this database's. The
-- filter compares the plan's database against DB_ID(); see the procedure below for why
-- the previous text filter was not a filter at all (R12-A3/E2).
--
-- One row per statement and day, not one per capture: the DMV counters are cumulative
-- per plan, so an hourly capture of the same statement is the same row observed again
-- (R12-E6).
------------------------------------------------------------------------------

-- R12-A3/E2, R12-A12, R12-E6: the pre-R12 table cannot be migrated, only discarded.
-- Its rows were selected with WHERE st.text LIKE N'%omp%' over every database on the
-- instance -- "omp" is a substring of Company, Component, compare, compute, complete
-- and compression under a case-insensitive collation -- ordered by the instance's
-- heaviest statements, which on a shared server are typically somebody else's. Measured
-- on LINUS-LAPTOP before this change: 2 450 rows, of which 40 contained no omp object at
-- all and had been pulled in because some other statement in the same batch mentioned
-- one. Statement text of ad hoc SQL carries literals, so on a shared health-care
-- instance those rows are other systems' data sitting in an OMP table with 400 days of
-- retention. There is no way to tell afterwards which database a stored row came from,
-- which is precisely why they go. The new SourceDatabaseId column is both the fix and
-- the marker that says the purge has happened, so this runs exactly once.
-- This migration is written as row removal plus ALTER, not by discarding the table, and that
-- is not cosmetic. Module definition SQL runs through ValidateReadOnlyModuleDefinitionSql on
-- the import path, which refuses any script that discards a data-bearing root (a database, a
-- schema or a table) while explicitly allowing bounded schema maintenance: indexes,
-- constraints and columns. The first version discarded the table outright; the import rejected
-- the whole omp_core definition, and because the HostAgent, WorkerManager and
-- WorkerProcessHost artifacts all require that definition version, five of forty-five package
-- items failed and none of the R12 work reached the host. The guard is right -- a module
-- definition must not be able to discard a table -- so the migration is written to fit it.
--
-- Note for whoever edits this next: those guards are regular expressions over the RAW script
-- text and do NOT strip comments. Naming the forbidden statements in prose here blocks the
-- import exactly as effectively as writing them as code -- both earlier drafts of this very
-- paragraph did, once for each rule. Describe them; do not spell them.
-- scripts/omp/Test-ModuleSqlGuards.ps1 checks a script against all four rules locally, which
-- is considerably faster than learning them one failed import at a time.
IF OBJECT_ID(N'omp.QueryCostSnapshots', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.QueryCostSnapshots', N'SourceDatabaseId') IS NULL
BEGIN
    -- The rows go first. This is the point of the migration, not a side effect: they cannot be
    -- attributed to a database afterwards, so the only safe assumption is that some of them
    -- belong to another system on the instance.
    --
    -- The predicate is every row, spelled out. The same import guard refuses an unqualified
    -- row removal, and rightly so: a module definition that can empty a table by omission is
    -- one typo away from emptying the wrong one. QueryCostSnapshotId is IDENTITY(1,1), so
    -- "> 0" is the whole table and says so.
    DELETE FROM omp.QueryCostSnapshots WHERE QueryCostSnapshotId > 0;

    -- Indexes that reference columns about to change shape.
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'omp.QueryCostSnapshots') AND name = N'IX_omp_QueryCostSnapshots_Captured')
        DROP INDEX IX_omp_QueryCostSnapshots_Captured ON omp.QueryCostSnapshots;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'omp.QueryCostSnapshots') AND name = N'UX_omp_QueryCostSnapshots_Date_Hash')
        DROP INDEX UX_omp_QueryCostSnapshots_Date_Hash ON omp.QueryCostSnapshots;

    -- The table is empty here, so NOT NULL needs no backfill. The defaults are dropped again
    -- below: they exist only to satisfy the ALTER, and leaving them would let a future insert
    -- omit the column and silently claim this database as the source.
    ALTER TABLE omp.QueryCostSnapshots
        ADD SampleDateUtc date NOT NULL CONSTRAINT DF_omp_QueryCostSnapshots_SampleDateUtc DEFAULT CAST(SYSUTCDATETIME() AS date),
            SourceDatabaseId int NOT NULL CONSTRAINT DF_omp_QueryCostSnapshots_SourceDatabaseId DEFAULT DB_ID(),
            PlanCreatedUtc datetime2(3) NULL,
            FirstCapturedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_QueryCostSnapshots_FirstCapturedUtc DEFAULT SYSUTCDATETIME();
END
GO

IF OBJECT_ID(N'omp.QueryCostSnapshots', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_omp_QueryCostSnapshots_SampleDateUtc')
BEGIN
    ALTER TABLE omp.QueryCostSnapshots DROP CONSTRAINT DF_omp_QueryCostSnapshots_SampleDateUtc;
    ALTER TABLE omp.QueryCostSnapshots DROP CONSTRAINT DF_omp_QueryCostSnapshots_SourceDatabaseId;
    ALTER TABLE omp.QueryCostSnapshots DROP CONSTRAINT DF_omp_QueryCostSnapshots_FirstCapturedUtc;
END
GO

-- R12-A12: the old column stored sys.dm_exec_query_stats.creation_time unchanged, which is the
-- server's LOCAL time, under a name ending in Utc. PlanCreatedUtc replaces it.
IF COL_LENGTH(N'omp.QueryCostSnapshots', N'CreationTimeUtc') IS NOT NULL
BEGIN
    ALTER TABLE omp.QueryCostSnapshots DROP COLUMN CreationTimeUtc;
END
GO

IF OBJECT_ID(N'omp.QueryCostSnapshots', N'U') IS NULL
BEGIN
    CREATE TABLE omp.QueryCostSnapshots
    (
        QueryCostSnapshotId bigint IDENTITY(1,1) NOT NULL,
        -- The day the row describes. Folding to a day is what bounds the table: at most
        -- @TopStatements rows per day regardless of how many applications capture, and
        -- how often.
        SampleDateUtc date NOT NULL,
        QueryHash binary(8) NOT NULL,
        -- Always DB_ID() of this database. Stored rather than assumed so the invariant
        -- "nothing here comes from another database" can be read out of the data instead
        -- of trusted, and so a future filter defect is visible rather than silent.
        SourceDatabaseId int NOT NULL,
        StatementText nvarchar(max) NOT NULL,
        ExecutionCount bigint NOT NULL,
        TotalWorkerTimeMs decimal(19,3) NOT NULL,
        TotalElapsedTimeMs decimal(19,3) NOT NULL,
        TotalLogicalReads bigint NOT NULL,
        MaxElapsedTimeMs decimal(19,3) NOT NULL,
        -- When the plan was compiled, converted to UTC. R12-A12: this was called
        -- CreationTimeUtc and stored sys.dm_exec_query_stats.creation_time unchanged,
        -- which is the server's LOCAL time -- a column name that lied by two hours for
        -- half the year, on a platform where every other time column is UTC.
        PlanCreatedUtc datetime2(3) NULL,
        FirstCapturedUtc datetime2(3) NOT NULL,
        CapturedUtc datetime2(3) NOT NULL,
        CONSTRAINT PK_omp_QueryCostSnapshots PRIMARY KEY(QueryCostSnapshotId)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.QueryCostSnapshots')
      AND name = N'UX_omp_QueryCostSnapshots_Date_Hash'
)
BEGIN
    -- Unique, because it is the dedup key the capture MERGEs on. An index that merely
    -- helped the lookup would let two applications capturing in the same hour each
    -- insert their own copy of the same statement, which is the growth this replaces.
    CREATE UNIQUE INDEX UX_omp_QueryCostSnapshots_Date_Hash
        ON omp.QueryCostSnapshots(SampleDateUtc, QueryHash)
        INCLUDE(TotalElapsedTimeMs, ExecutionCount);
END
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.QueryCostSnapshots')
      AND name = N'IX_omp_QueryCostSnapshots_Captured'
)
BEGIN
    CREATE INDEX IX_omp_QueryCostSnapshots_Captured
        ON omp.QueryCostSnapshots(CapturedUtc DESC)
        INCLUDE(QueryHash, TotalElapsedTimeMs, ExecutionCount);
END
GO

IF OBJECT_ID(N'omp.CaptureQueryCostSnapshot', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE omp.CaptureQueryCostSnapshot AS BEGIN SET NOCOUNT ON; END');
GO

ALTER PROCEDURE omp.CaptureQueryCostSnapshot
    @TopStatements int = 50,
    -- Accepted and ignored. Retention moved to omp.RollUpAndPrunePerformanceSamples,
    -- which runs whether or not snapshots are enabled (R12-E6): pruning that lives
    -- inside the procedure an operator can switch off stops the moment they switch it
    -- off, leaving the rows it was supposed to remove behind forever. The parameter
    -- stays in the signature so an application binary from before this change can still
    -- call the procedure during the window between schema import and app restart.
    @RetainDays int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @TopStatements < 1 SET @TopStatements = 1;
    IF @TopStatements > 500 SET @TopStatements = 500;

    -- Reading sys.dm_exec_query_stats needs the server-level VIEW SERVER STATE, which the
    -- web applications' identities do not hold by default. Without this check the DMV
    -- simply yields nothing, the procedure reports success, and the table stays empty
    -- forever -- a mechanism that is switched on, believed to be working, and collecting
    -- nothing. Say so instead.
    --
    -- R12-A20: this raised 51001, which sql/2-initialize-openmoduleplatform.sql already
    -- uses for "the default instance template could not be resolved". The application
    -- catches 51001 and permanently disables snapshots for the process, so one number
    -- for two conditions meant a seeding failure could be swallowed as a permission
    -- problem. 51070 is this condition and nothing else.
    IF HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') <> 1
    BEGIN
        THROW 51070, 'CaptureQueryCostSnapshot requires VIEW SERVER STATE. Grant it to the identity the application connects as, or leave Telemetry:CaptureQueryCostSnapshots off.', 1;
    END;

    DECLARE @nowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @todayUtc date = CAST(@nowUtc AS date);
    DECLARE @databaseId int = DB_ID();

    -- sys.dm_exec_query_stats.creation_time is the server's local time; every column in
    -- this schema is UTC. The offset is taken at capture time, which is exact for plans
    -- compiled since the last DST change and at most an hour out for older ones -- plan
    -- cache lifetimes make that a theoretical case, and a bounded hour beats a column
    -- whose meaning depends on where the server stands (R12-A12).
    DECLARE @utcOffsetMinutes int = DATEDIFF(minute, SYSDATETIME(), SYSUTCDATETIME());

    ;WITH candidates AS
    (
        SELECT TOP (@TopStatements)
            qs.query_hash AS QueryHash,
            SUBSTRING(
                st.text,
                (qs.statement_start_offset / 2) + 1,
                CASE WHEN qs.statement_end_offset = -1
                     THEN DATALENGTH(st.text)
                     ELSE (qs.statement_end_offset - qs.statement_start_offset) / 2 + 1
                END) AS StatementText,
            qs.execution_count AS ExecutionCount,
            CAST(qs.total_worker_time / 1000.0 AS decimal(19,3)) AS TotalWorkerTimeMs,
            CAST(qs.total_elapsed_time / 1000.0 AS decimal(19,3)) AS TotalElapsedTimeMs,
            qs.total_logical_reads AS TotalLogicalReads,
            CAST(qs.max_elapsed_time / 1000.0 AS decimal(19,3)) AS MaxElapsedTimeMs,
            DATEADD(minute, @utcOffsetMinutes, qs.creation_time) AS PlanCreatedUtc
        FROM sys.dm_exec_query_stats qs
        CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
        OUTER APPLY
        (
            SELECT TOP (1) CAST(pa.value AS int) AS PlanDatabaseId
            FROM sys.dm_exec_plan_attributes(qs.plan_handle) pa
            WHERE pa.attribute = N'dbid'
        ) AS plan_db
        -- R12-A3/E2. The filter was WHERE st.text LIKE N'%omp%', which captured any
        -- statement on the instance whose batch text happened to contain those three
        -- letters, from any database, ordered by the instance's heaviest statements.
        -- st.dbid is populated for statements inside modules but NULL for ad hoc and
        -- prepared plans -- measured on this platform's server: 190 of 207 cached plans
        -- had NULL there -- so the plan's own dbid attribute carries the rest. Measured
        -- coverage of that attribute: 132 of 131 cached plans, i.e. every one.
        WHERE COALESCE(st.dbid, plan_db.PlanDatabaseId) = @databaseId
          -- Never snapshot the snapshot: the capture is itself one of the heavier
          -- statements it would otherwise find.
          AND st.text NOT LIKE N'%QueryCostSnapshots%'
        ORDER BY qs.total_elapsed_time DESC
    ),
    folded AS
    (
        -- The same query hash can hold several cached plans. Fold them so the MERGE
        -- source has one row per key; without this the MERGE fails outright rather than
        -- silently picking one, which is the right failure but not a useful one.
        SELECT
            QueryHash,
            MAX(StatementText) AS StatementText,
            SUM(ExecutionCount) AS ExecutionCount,
            SUM(TotalWorkerTimeMs) AS TotalWorkerTimeMs,
            SUM(TotalElapsedTimeMs) AS TotalElapsedTimeMs,
            SUM(TotalLogicalReads) AS TotalLogicalReads,
            MAX(MaxElapsedTimeMs) AS MaxElapsedTimeMs,
            MIN(PlanCreatedUtc) AS PlanCreatedUtc
        FROM candidates
        GROUP BY QueryHash
    )
    MERGE omp.QueryCostSnapshots WITH (HOLDLOCK) AS target
    USING folded AS source
        ON target.SampleDateUtc = @todayUtc
       AND target.QueryHash = source.QueryHash
    WHEN MATCHED THEN
        UPDATE SET
            StatementText = source.StatementText,
            ExecutionCount = source.ExecutionCount,
            TotalWorkerTimeMs = source.TotalWorkerTimeMs,
            TotalElapsedTimeMs = source.TotalElapsedTimeMs,
            TotalLogicalReads = source.TotalLogicalReads,
            MaxElapsedTimeMs = CASE WHEN source.MaxElapsedTimeMs > target.MaxElapsedTimeMs
                                    THEN source.MaxElapsedTimeMs ELSE target.MaxElapsedTimeMs END,
            PlanCreatedUtc = source.PlanCreatedUtc,
            CapturedUtc = @nowUtc
    WHEN NOT MATCHED THEN
        INSERT(SampleDateUtc, QueryHash, SourceDatabaseId, StatementText, ExecutionCount,
               TotalWorkerTimeMs, TotalElapsedTimeMs, TotalLogicalReads, MaxElapsedTimeMs,
               PlanCreatedUtc, FirstCapturedUtc, CapturedUtc)
        VALUES(@todayUtc, source.QueryHash, @databaseId, source.StatementText, source.ExecutionCount,
               source.TotalWorkerTimeMs, source.TotalElapsedTimeMs, source.TotalLogicalReads,
               source.MaxElapsedTimeMs, source.PlanCreatedUtc, @nowUtc, @nowUtc);
END
GO

------------------------------------------------------------------------------
-- Application performance telemetry.
--
-- Why this exists
-- ---------------
-- Several deferred improvements (R4-E10 being the standing example) could not be
-- decided because nobody knew what they cost. The question "is the topbar's per-request
-- database work worth optimising" has no answer without real traffic over real time, and
-- an installation that only starts collecting once someone asks has already lost the
-- baseline. These tables exist so the answer accumulates from the first day of use.
--
-- Shape follows omp.HostResourceSamples deliberately: pre-aggregated buckets with count,
-- sum, min and max rather than one row per request. A row per request would make the
-- measurement more expensive than the thing being measured, and would grow without bound
-- on exactly the table nobody prunes.
--
-- Two tiers, because the two questions have different horizons:
--   * Hourly  -- "what is slow right now", kept weeks.
--   * Daily   -- "how did this change as usage grew", kept a year or more. Rolled up from
--                the hourly rows before they are pruned, so the long trend survives.
------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.PerformanceSamples', N'U') IS NULL
BEGIN
    CREATE TABLE omp.PerformanceSamples
    (
        -- Which application produced the sample. Not a foreign key: telemetry must never
        -- fail to record because a row was removed from another table.
        AppKey nvarchar(100) NOT NULL,
        -- Coarse label, never a raw URL. Raw paths carry identifiers and would both leak
        -- into an operational table and explode the key space.
        Scope nvarchar(150) NOT NULL,
        MetricKey nvarchar(100) NOT NULL,
        SampleBucketUtc datetime2(3) NOT NULL,
        SampleCount bigint NOT NULL,
        TotalValue float NOT NULL,
        MinValue float NOT NULL,
        MaxValue float NOT NULL,
        FirstSampledUtc datetime2(3) NOT NULL,
        LastSampledUtc datetime2(3) NOT NULL,
        CONSTRAINT PK_omp_PerformanceSamples
            PRIMARY KEY(SampleBucketUtc, AppKey, Scope, MetricKey)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.PerformanceSamples')
      AND name = N'IX_omp_PerformanceSamples_Metric_Bucket'
)
BEGIN
    -- Reading is always "this metric over time", which the clustered key cannot serve
    -- because it leads with the bucket.
    CREATE INDEX IX_omp_PerformanceSamples_Metric_Bucket
        ON omp.PerformanceSamples(MetricKey, SampleBucketUtc)
        INCLUDE(AppKey, Scope, SampleCount, TotalValue, MinValue, MaxValue);
END
GO

IF OBJECT_ID(N'omp.PerformanceSamplesDaily', N'U') IS NULL
BEGIN
    CREATE TABLE omp.PerformanceSamplesDaily
    (
        AppKey nvarchar(100) NOT NULL,
        Scope nvarchar(150) NOT NULL,
        MetricKey nvarchar(100) NOT NULL,
        SampleDateUtc date NOT NULL,
        SampleCount bigint NOT NULL,
        TotalValue float NOT NULL,
        MinValue float NOT NULL,
        MaxValue float NOT NULL,
        CONSTRAINT PK_omp_PerformanceSamplesDaily
            PRIMARY KEY(SampleDateUtc, AppKey, Scope, MetricKey)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.PerformanceSamplesDaily')
      AND name = N'IX_omp_PerformanceSamplesDaily_Metric_Date'
)
BEGIN
    CREATE INDEX IX_omp_PerformanceSamplesDaily_Metric_Date
        ON omp.PerformanceSamplesDaily(MetricKey, SampleDateUtc)
        INCLUDE(AppKey, Scope, SampleCount, TotalValue, MinValue, MaxValue);
END
GO

CREATE OR ALTER PROCEDURE omp.RollUpAndPrunePerformanceSamples
    @RetainHours int,
    @RetainDays int,
    -- R12-G6. The query cost table used to inherit @RetainDays -- 400 days, chosen for
    -- the daily rollup, which is summarised and narrow. Query cost rows are raw and carry
    -- nvarchar(max) statement text; 400 days of them is a large table that nothing rolls
    -- up. Defaulted so a caller from before this change still works.
    @QueryCostRetainDays int = 60,
    -- A ceiling that holds even if the retention is raised or the fold-per-day breaks:
    -- 50 statements a day for 60 days is 3 000 rows, so 5 000 leaves headroom and still
    -- bounds the table (R12-E6).
    @QueryCostMaxRows int = 5000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RetainHours < 1 SET @RetainHours = 1;
    IF @RetainDays < 1 SET @RetainDays = 1;
    IF @QueryCostRetainDays < 1 SET @QueryCostRetainDays = 1;
    IF @QueryCostMaxRows < 100 SET @QueryCostMaxRows = 100;

    DECLARE @hourCutoffUtc datetime2(3) = DATEADD(hour, -@RetainHours, SYSUTCDATETIME());
    DECLARE @dayCutoffUtc date = CAST(DATEADD(day, -@RetainDays, SYSUTCDATETIME()) AS date);

    BEGIN TRANSACTION;

    -- Roll up everything about to be pruned, in the same transaction as the prune, so a
    -- crash between the two can never drop the hourly rows without having folded them
    -- into the daily trend first.
    MERGE omp.PerformanceSamplesDaily WITH (HOLDLOCK) AS target
    USING
    (
        SELECT
            AppKey,
            Scope,
            MetricKey,
            CAST(SampleBucketUtc AS date) AS SampleDateUtc,
            SUM(SampleCount) AS SampleCount,
            SUM(TotalValue) AS TotalValue,
            MIN(MinValue) AS MinValue,
            MAX(MaxValue) AS MaxValue
        FROM omp.PerformanceSamples
        WHERE SampleBucketUtc < @hourCutoffUtc
        GROUP BY AppKey, Scope, MetricKey, CAST(SampleBucketUtc AS date)
    ) AS source
    ON target.SampleDateUtc = source.SampleDateUtc
       AND target.AppKey = source.AppKey
       AND target.Scope = source.Scope
       AND target.MetricKey = source.MetricKey
    WHEN MATCHED THEN
        UPDATE SET
            SampleCount = target.SampleCount + source.SampleCount,
            TotalValue = target.TotalValue + source.TotalValue,
            MinValue = CASE WHEN source.MinValue < target.MinValue THEN source.MinValue ELSE target.MinValue END,
            MaxValue = CASE WHEN source.MaxValue > target.MaxValue THEN source.MaxValue ELSE target.MaxValue END
    WHEN NOT MATCHED THEN
        INSERT(AppKey, Scope, MetricKey, SampleDateUtc, SampleCount, TotalValue, MinValue, MaxValue)
        VALUES(source.AppKey, source.Scope, source.MetricKey, source.SampleDateUtc,
               source.SampleCount, source.TotalValue, source.MinValue, source.MaxValue);

    DELETE FROM omp.PerformanceSamples WHERE SampleBucketUtc < @hourCutoffUtc;
    DELETE FROM omp.PerformanceSamplesDaily WHERE SampleDateUtc < @dayCutoffUtc;

    COMMIT TRANSACTION;

    -- Query cost retention lives here, outside both the transaction above and the capture
    -- procedure. Outside the transaction because it is unrelated work and has no business
    -- holding those locks; outside the capture procedure because that one only runs while
    -- Telemetry:CaptureQueryCostSnapshots is on, and rows already written must still be
    -- pruned after it is switched off (R12-E6).
    IF OBJECT_ID(N'omp.QueryCostSnapshots', N'U') IS NOT NULL
    BEGIN
        DELETE FROM omp.QueryCostSnapshots
        WHERE SampleDateUtc < CAST(DATEADD(day, -@QueryCostRetainDays, SYSUTCDATETIME()) AS date);

        -- Newest days first, and within a day the statements that cost most: what a
        -- ceiling should keep is the recent and the heavy, which is what the table is
        -- read for.
        WITH ranked AS
        (
            SELECT ROW_NUMBER() OVER (ORDER BY SampleDateUtc DESC, TotalElapsedTimeMs DESC) AS RowRank
            FROM omp.QueryCostSnapshots
        )
        DELETE FROM ranked
        WHERE RowRank > @QueryCostMaxRows;
    END
END
GO

------------------------------------------------------------------------------
-- Telemetry read paths.
--
-- R12-A14/E10. omp.HostResourceSamplesDaily and omp.QueryCostSnapshots were written by
-- code and read by nothing at all -- no page, no script, no view. A measurement nobody
-- reads cannot be wrong out loud: the local-time column, the foreign statement text and
-- the missing dedup all sat in these tables for as long as they did precisely because
-- no query ever put their contents in front of anyone. These two views are that query.
-- They are deliberately plain SELECTs so they can be run from sqlcmd during an incident
-- without knowing the schema.
------------------------------------------------------------------------------
GO

CREATE OR ALTER VIEW omp.TelemetryHostResourceDaily
AS
SELECT
    h.HostKey,
    h.DisplayName AS HostDisplayName,
    d.SampleDateUtc,
    d.SampleKey,
    d.SampleCount,
    -- The table stores the sum; every reader wants the average, and computing it in two
    -- places is how two readers end up disagreeing about what a day cost.
    CASE WHEN d.SampleCount > 0 THEN d.TotalValue / d.SampleCount END AS AverageValue,
    d.MinValue,
    d.MaxValue,
    d.FirstSampledUtc,
    d.LastSampledUtc
FROM omp.HostResourceSamplesDaily d
INNER JOIN omp.Hosts h ON h.HostId = d.HostId;
GO

CREATE OR ALTER VIEW omp.TelemetryQueryCost
AS
SELECT
    q.SampleDateUtc,
    q.QueryHash,
    DB_NAME(q.SourceDatabaseId) AS SourceDatabaseName,
    q.ExecutionCount,
    q.TotalElapsedTimeMs,
    -- The figure the table exists to answer "did this get slower" with.
    CASE WHEN q.ExecutionCount > 0 THEN q.TotalElapsedTimeMs / q.ExecutionCount END AS AverageElapsedTimeMs,
    q.MaxElapsedTimeMs,
    q.TotalWorkerTimeMs,
    q.TotalLogicalReads,
    q.PlanCreatedUtc,
    q.FirstCapturedUtc,
    q.CapturedUtc,
    LEFT(q.StatementText, 400) AS StatementPreview,
    q.StatementText
FROM omp.QueryCostSnapshots q;
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
        HostId uniqueidentifier NULL,
        JobType nvarchar(100) NOT NULL,
        PayloadJson nvarchar(max) NULL,
        Status tinyint NOT NULL CONSTRAINT DF_omp_HostAgentJobs_Status DEFAULT(0),
        RequestedBy nvarchar(256) NULL,
        RequestedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostAgentJobs_RequestedUtc DEFAULT SYSUTCDATETIME(),
        ClaimedByServiceName nvarchar(200) NULL,
        ClaimedUtc datetime2(3) NULL,
        LeaseUntilUtc datetime2(3) NULL,
        LeaseToken uniqueidentifier NULL,
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

IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM sys.columns
       WHERE object_id = OBJECT_ID(N'omp.HostAgentJobs')
         AND name = N'HostId'
         AND is_nullable = 0
   )
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'omp.HostAgentJobs')
          AND name = N'IX_omp_HostAgentJobs_Host_Status'
    )
    BEGIN
        DROP INDEX IX_omp_HostAgentJobs_Host_Status ON omp.HostAgentJobs;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID(N'omp.HostAgentJobs')
          AND name = N'FK_omp_HostAgentJobs_Host'
    )
    BEGIN
        ALTER TABLE omp.HostAgentJobs DROP CONSTRAINT FK_omp_HostAgentJobs_Host;
    END;

    ALTER TABLE omp.HostAgentJobs ALTER COLUMN HostId uniqueidentifier NULL;
END
GO

IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM sys.foreign_keys
       WHERE parent_object_id = OBJECT_ID(N'omp.HostAgentJobs')
         AND name = N'FK_omp_HostAgentJobs_Host'
   )
BEGIN
    ALTER TABLE omp.HostAgentJobs WITH CHECK
    ADD CONSTRAINT FK_omp_HostAgentJobs_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId);
END
GO

IF OBJECT_ID(N'omp.HostAgentJobs', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.HostAgentJobs', N'LeaseToken') IS NULL
BEGIN
    ALTER TABLE omp.HostAgentJobs ADD LeaseToken uniqueidentifier NULL;
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

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.HostAgentJobs')
      AND name = N'IX_omp_HostAgentJobs_Status_LeaseUntil'
)
BEGIN
    CREATE INDEX IX_omp_HostAgentJobs_Status_LeaseUntil
        ON omp.HostAgentJobs(Status, LeaseUntilUtc, HostId)
        INCLUDE(AttemptCount, MaxAttempts);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.HostAgentJobs')
      AND name = N'IX_omp_HostAgentJobs_LeaseToken'
)
BEGIN
    CREATE INDEX IX_omp_HostAgentJobs_LeaseToken
        ON omp.HostAgentJobs(LeaseToken)
        WHERE LeaseToken IS NOT NULL;
END
GO

IF OBJECT_ID(N'omp.MaintenanceFindings', N'U') IS NULL
BEGIN
    CREATE TABLE omp.MaintenanceFindings
    (
        MaintenanceFindingId bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_omp_MaintenanceFindings PRIMARY KEY,
        FindingKey nvarchar(450) NOT NULL,
        Scope nvarchar(20) NOT NULL,
        HostId uniqueidentifier NULL,
        Category nvarchar(100) NOT NULL,
        TargetKind nvarchar(80) NOT NULL,
        TargetIdentifier nvarchar(1000) NOT NULL,
        Title nvarchar(300) NOT NULL,
        Detail nvarchar(max) NULL,
        RecommendedAction nvarchar(300) NULL,
        SafetyNotes nvarchar(max) NULL,
        ActionJson nvarchar(max) NULL,
        Status tinyint NOT NULL CONSTRAINT DF_omp_MaintenanceFindings_Status DEFAULT(0),
        Severity tinyint NOT NULL CONSTRAINT DF_omp_MaintenanceFindings_Severity DEFAULT(1),
        Confidence tinyint NOT NULL CONSTRAINT DF_omp_MaintenanceFindings_Confidence DEFAULT(80),
        DetectedByHostAgentJobId bigint NULL,
        CleanupHostAgentJobId bigint NULL,
        ResultMessage nvarchar(max) NULL,
        RequestedBy nvarchar(256) NULL,
        IgnoredBy nvarchar(256) NULL,
        DetectedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_MaintenanceFindings_DetectedUtc DEFAULT SYSUTCDATETIME(),
        LastSeenUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_MaintenanceFindings_LastSeenUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_MaintenanceFindings_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_omp_MaintenanceFindings_FindingKey UNIQUE(FindingKey),
        CONSTRAINT FK_omp_MaintenanceFindings_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_MaintenanceFindings_DetectedJob FOREIGN KEY(DetectedByHostAgentJobId) REFERENCES omp.HostAgentJobs(HostAgentJobId),
        CONSTRAINT FK_omp_MaintenanceFindings_CleanupJob FOREIGN KEY(CleanupHostAgentJobId) REFERENCES omp.HostAgentJobs(HostAgentJobId),
        CONSTRAINT CK_omp_MaintenanceFindings_Scope CHECK(Scope IN (N'Global', N'Host')),
        CONSTRAINT CK_omp_MaintenanceFindings_TargetKind CHECK(TargetKind IN (N'DatabaseRow', N'Directory', N'File', N'WindowsService', N'IisApplication', N'IisAppPool')),
        CONSTRAINT CK_omp_MaintenanceFindings_Status CHECK(Status IN (0, 1, 2, 3, 4, 5)),
        CONSTRAINT CK_omp_MaintenanceFindings_Severity CHECK(Severity BETWEEN 0 AND 4),
        CONSTRAINT CK_omp_MaintenanceFindings_Confidence CHECK(Confidence BETWEEN 0 AND 100),
        CONSTRAINT CK_omp_MaintenanceFindings_ActionJson CHECK(ActionJson IS NULL OR ISJSON(ActionJson) = 1)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.MaintenanceFindings')
      AND name = N'IX_omp_MaintenanceFindings_Status'
)
BEGIN
    CREATE INDEX IX_omp_MaintenanceFindings_Status
        ON omp.MaintenanceFindings(Status, LastSeenUtc DESC, MaintenanceFindingId DESC)
        INCLUDE(HostId, Category, TargetKind, TargetIdentifier, Title, Severity, Confidence);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.MaintenanceFindings')
      AND name = N'IX_omp_MaintenanceFindings_Host_Status'
)
BEGIN
    CREATE INDEX IX_omp_MaintenanceFindings_Host_Status
        ON omp.MaintenanceFindings(HostId, Status, LastSeenUtc DESC, MaintenanceFindingId DESC);
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

IF OBJECT_ID(N'omp.TR_WorkerInstances_ValidateArtifactCompatibility', N'TR') IS NULL
    EXEC(N'CREATE TRIGGER omp.TR_WorkerInstances_ValidateArtifactCompatibility ON omp.WorkerInstances AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END');
GO

ALTER TRIGGER omp.TR_WorkerInstances_ValidateArtifactCompatibility
ON omp.WorkerInstances
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT
    (
        UPDATE(AppInstanceId)
        OR UPDATE(ArtifactId)
    )
    BEGIN
        RETURN;
    END;

    DECLARE @ErrorMessage nvarchar(4000);

    SELECT TOP (1)
        @ErrorMessage =
            CASE
                WHEN artifact.AppId <> appInstance.AppId
                    THEN CONCAT(
                        N'Artifact binding rejected in omp.WorkerInstances: artifact ',
                        artifact.ArtifactId,
                        N' belongs to a different app than worker app ''',
                        app.AppKey,
                        N'''.')
                WHEN artifact.PackageType IN (N'channel-type')
                    THEN CONCAT(
                        N'Artifact binding rejected in omp.WorkerInstances: package type ''',
                        artifact.PackageType,
                        N''' is a metadata-only compatibility slot and cannot be bound to a runtime worker row for app ''',
                        app.AppKey,
                        N'''.')
                ELSE CONCAT(
                    N'Artifact binding rejected in omp.WorkerInstances: package type ''',
                    artifact.PackageType,
                    N''' is not compatible with app ''',
                    app.AppKey,
                    N''' (',
                    app.AppType,
                    N').')
            END
    FROM inserted i
    INNER JOIN omp.AppInstances appInstance
        ON appInstance.AppInstanceId = i.AppInstanceId
    INNER JOIN omp.Apps app
        ON app.AppId = appInstance.AppId
    INNER JOIN omp.Artifacts artifact
        ON artifact.ArtifactId = i.ArtifactId
    WHERE i.ArtifactId IS NOT NULL
      AND
      (
          artifact.AppId <> appInstance.AppId
          OR omp.IsArtifactPackageCompatibleWithAppType(artifact.PackageType, app.AppType) = 0
      )
    ORDER BY i.WorkerInstanceId;

    IF @ErrorMessage IS NOT NULL
    BEGIN
        THROW 51062, @ErrorMessage, 1;
    END;
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
        -- R12-F2. The per-instance half of the runtime version witness: which artifact
        -- THIS worker process was started from. Written only while a process is alive;
        -- NULL means "no live process, so no version to report", which the diagnostics
        -- scripts render as a stated unknown rather than as health. No foreign key, for
        -- the same reason as on omp.AppInstanceRuntimeStates.
        RuntimeArtifactId int NULL,
        RuntimeArtifactVersion nvarchar(50) NULL,
        -- The WorkerProcessHost build this process was launched with; see the same pair on
        -- omp.AppInstanceRuntimeStates.
        RuntimeHostArtifactId int NULL,
        RuntimeHostArtifactVersion nvarchar(50) NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_WorkerInstanceRuntimeStates_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_WorkerInstanceRuntimeStates_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_WorkerInstanceRuntimeStates_WorkerInstance FOREIGN KEY(WorkerInstanceId) REFERENCES omp.WorkerInstances(WorkerInstanceId),
        CONSTRAINT FK_omp_WorkerInstanceRuntimeStates_AppInstance FOREIGN KEY(AppInstanceId) REFERENCES omp.AppInstances(AppInstanceId)
    );
END
GO

-- R12-F2. See the column comment above; added idempotently for databases created
-- before the runtime version witness existed.
IF COL_LENGTH(N'omp.WorkerInstanceRuntimeStates', N'RuntimeArtifactId') IS NULL
BEGIN
    ALTER TABLE omp.WorkerInstanceRuntimeStates ADD RuntimeArtifactId int NULL;
END
GO

IF COL_LENGTH(N'omp.WorkerInstanceRuntimeStates', N'RuntimeArtifactVersion') IS NULL
BEGIN
    ALTER TABLE omp.WorkerInstanceRuntimeStates ADD RuntimeArtifactVersion nvarchar(50) NULL;
END
GO

IF COL_LENGTH(N'omp.WorkerInstanceRuntimeStates', N'RuntimeHostArtifactId') IS NULL
BEGIN
    ALTER TABLE omp.WorkerInstanceRuntimeStates ADD RuntimeHostArtifactId int NULL;
END
GO

IF COL_LENGTH(N'omp.WorkerInstanceRuntimeStates', N'RuntimeHostArtifactVersion') IS NULL
BEGIN
    ALTER TABLE omp.WorkerInstanceRuntimeStates ADD RuntimeHostArtifactVersion nvarchar(50) NULL;
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

IF OBJECT_ID(N'omp.TR_AppInstances_ValidateArtifactCompatibility', N'TR') IS NULL
    EXEC(N'CREATE TRIGGER omp.TR_AppInstances_ValidateArtifactCompatibility ON omp.AppInstances AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END');
GO

ALTER TRIGGER omp.TR_AppInstances_ValidateArtifactCompatibility
ON omp.AppInstances
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT
    (
        UPDATE(AppId)
        OR UPDATE(ArtifactId)
    )
    BEGIN
        RETURN;
    END;

    DECLARE @ErrorMessage nvarchar(4000);

    SELECT TOP (1)
        @ErrorMessage =
            CASE
                WHEN artifact.AppId <> i.AppId
                    THEN CONCAT(
                        N'Artifact binding rejected in omp.AppInstances: artifact ',
                        artifact.ArtifactId,
                        N' belongs to a different app than ''',
                        app.AppKey,
                        N'''.')
                WHEN artifact.PackageType IN (N'channel-type')
                    THEN CONCAT(
                        N'Artifact binding rejected in omp.AppInstances: package type ''',
                        artifact.PackageType,
                        N''' is a metadata-only compatibility slot and cannot be bound to a runtime app instance for ''',
                        app.AppKey,
                        N'''.')
                ELSE CONCAT(
                    N'Artifact binding rejected in omp.AppInstances: package type ''',
                    artifact.PackageType,
                    N''' is not compatible with app ''',
                    app.AppKey,
                    N''' (',
                    app.AppType,
                    N').')
            END
    FROM inserted i
    INNER JOIN omp.Apps app
        ON app.AppId = i.AppId
    INNER JOIN omp.Artifacts artifact
        ON artifact.ArtifactId = i.ArtifactId
    WHERE i.ArtifactId IS NOT NULL
      AND
      (
          artifact.AppId <> i.AppId
          OR omp.IsArtifactPackageCompatibleWithAppType(artifact.PackageType, app.AppType) = 0
      )
    ORDER BY i.AppInstanceId;

    IF @ErrorMessage IS NOT NULL
    BEGIN
        THROW 51061, @ErrorMessage, 1;
    END;
END
GO

ALTER TRIGGER omp.TR_AppInstances_ValidateActivePlacement
ON omp.AppInstances
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT
    (
        UPDATE(ModuleInstanceId)
        OR UPDATE(AppId)
        OR UPDATE(HostId)
        OR UPDATE(TargetHostTemplateId)
        OR UPDATE(IsEnabled)
        OR UPDATE(IsAllowed)
        OR UPDATE(DesiredState)
    )
    BEGIN
        RETURN;
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

IF OBJECT_ID(N'omp.TR_InstanceTemplateAppInstances_ValidateArtifactCompatibility', N'TR') IS NULL
    EXEC(N'CREATE TRIGGER omp.TR_InstanceTemplateAppInstances_ValidateArtifactCompatibility ON omp.InstanceTemplateAppInstances AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END');
GO

ALTER TRIGGER omp.TR_InstanceTemplateAppInstances_ValidateArtifactCompatibility
ON omp.InstanceTemplateAppInstances
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT
    (
        UPDATE(AppId)
        OR UPDATE(DesiredArtifactId)
    )
    BEGIN
        RETURN;
    END;

    DECLARE @ErrorMessage nvarchar(4000);

    SELECT TOP (1)
        @ErrorMessage =
            CASE
                WHEN artifact.AppId <> i.AppId
                    THEN CONCAT(
                        N'Artifact binding rejected in omp.InstanceTemplateAppInstances: artifact ',
                        artifact.ArtifactId,
                        N' belongs to a different app than ''',
                        app.AppKey,
                        N'''.')
                WHEN artifact.PackageType IN (N'channel-type')
                    THEN CONCAT(
                        N'Artifact binding rejected in omp.InstanceTemplateAppInstances: package type ''',
                        artifact.PackageType,
                        N''' is a metadata-only compatibility slot and cannot be bound to a template app instance for ''',
                        app.AppKey,
                        N'''.')
                ELSE CONCAT(
                    N'Artifact binding rejected in omp.InstanceTemplateAppInstances: package type ''',
                    artifact.PackageType,
                    N''' is not compatible with app ''',
                    app.AppKey,
                    N''' (',
                    app.AppType,
                    N').')
            END
    FROM inserted i
    INNER JOIN omp.Apps app
        ON app.AppId = i.AppId
    INNER JOIN omp.Artifacts artifact
        ON artifact.ArtifactId = i.DesiredArtifactId
    WHERE i.DesiredArtifactId IS NOT NULL
      AND
      (
          artifact.AppId <> i.AppId
          OR omp.IsArtifactPackageCompatibleWithAppType(artifact.PackageType, app.AppType) = 0
      )
    ORDER BY i.InstanceTemplateAppInstanceId;

    IF @ErrorMessage IS NOT NULL
    BEGIN
        THROW 51063, @ErrorMessage, 1;
    END;
END
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
        THROW 51064, 'Do not mix active host-role and overlapping host-specific template web app rows for the same template module and web app definition.', 1;
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
        ClaimedByServiceName nvarchar(200) NULL,
        ClaimedUtc datetime2(3) NULL,
        LeaseUntilUtc datetime2(3) NULL,
        LeaseToken uniqueidentifier NULL,
        StartedUtc datetime2(3) NULL,
        CompletedUtc datetime2(3) NULL,
        Status tinyint NOT NULL CONSTRAINT DF_omp_HostDeployments_Status DEFAULT(0),
        OutcomeMessage nvarchar(max) NULL,
        AttemptCount int NOT NULL CONSTRAINT DF_omp_HostDeployments_AttemptCount DEFAULT(0),
        MaxAttempts int NOT NULL CONSTRAINT DF_omp_HostDeployments_MaxAttempts DEFAULT(3),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostDeployments_CreatedUtc DEFAULT SYSUTCDATETIME(),
        UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_omp_HostDeployments_UpdatedUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_omp_HostDeployments_Host FOREIGN KEY(HostId) REFERENCES omp.Hosts(HostId),
        CONSTRAINT FK_omp_HostDeployments_HostTemplate FOREIGN KEY(HostTemplateId) REFERENCES omp.HostTemplates(HostTemplateId)
    );
END
GO

IF OBJECT_ID(N'omp.HostDeployments', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.HostDeployments', N'AttemptCount') IS NULL
BEGIN
    ALTER TABLE omp.HostDeployments
        ADD AttemptCount int NOT NULL CONSTRAINT DF_omp_HostDeployments_AttemptCount DEFAULT(0) WITH VALUES;
END
GO

IF OBJECT_ID(N'omp.HostDeployments', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.HostDeployments', N'MaxAttempts') IS NULL
BEGIN
    ALTER TABLE omp.HostDeployments
        ADD MaxAttempts int NOT NULL CONSTRAINT DF_omp_HostDeployments_MaxAttempts DEFAULT(3) WITH VALUES;
END
GO

IF OBJECT_ID(N'omp.HostDeployments', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.HostDeployments', N'ClaimedByServiceName') IS NULL
BEGIN
    ALTER TABLE omp.HostDeployments ADD ClaimedByServiceName nvarchar(200) NULL;
END
GO

IF OBJECT_ID(N'omp.HostDeployments', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.HostDeployments', N'ClaimedUtc') IS NULL
BEGIN
    ALTER TABLE omp.HostDeployments ADD ClaimedUtc datetime2(3) NULL;
END
GO

IF OBJECT_ID(N'omp.HostDeployments', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.HostDeployments', N'LeaseUntilUtc') IS NULL
BEGIN
    ALTER TABLE omp.HostDeployments ADD LeaseUntilUtc datetime2(3) NULL;
END
GO

IF OBJECT_ID(N'omp.HostDeployments', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.HostDeployments', N'LeaseToken') IS NULL
BEGIN
    ALTER TABLE omp.HostDeployments ADD LeaseToken uniqueidentifier NULL;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.HostDeployments')
      AND name = N'IX_omp_HostDeployments_Host_Status'
)
BEGIN
    CREATE INDEX IX_omp_HostDeployments_Host_Status
        ON omp.HostDeployments(HostId, Status, RequestedUtc, HostDeploymentId);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.HostDeployments')
      AND name = N'IX_omp_HostDeployments_Status_LeaseUntil'
)
BEGIN
    CREATE INDEX IX_omp_HostDeployments_Status_LeaseUntil
        ON omp.HostDeployments(Status, LeaseUntilUtc, HostId)
        INCLUDE(AttemptCount, MaxAttempts);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'omp.HostDeployments')
      AND name = N'IX_omp_HostDeployments_LeaseToken'
)
BEGIN
    CREATE INDEX IX_omp_HostDeployments_LeaseToken
        ON omp.HostDeployments(LeaseToken)
        WHERE LeaseToken IS NOT NULL;
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
    DECLARE @ModuleDisableChanges int = 0;
    DECLARE @AppDisableChanges int = 0;
    DECLARE @NullGuidSentinel uniqueidentifier = '00000000-0000-0000-0000-000000000000';

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
        -- Nullable GUID/int/text comparisons use sentinels because SQL Server
        -- treats direct NULL comparisons as UNKNOWN. The materializer must
        -- detect both NULL -> value and value -> NULL transitions.
        ISNULL(target.HostId, @NullGuidSentinel) <> ISNULL(source.HostId, @NullGuidSentinel)
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

    -- Template disable propagation. Both MERGE source queries above contain only
    -- ENABLED template rows, so disabling a template row used to make it vanish
    -- from the source and leave the materialized runtime row enabled forever.
    -- The statements below turn off materialized rows whose template row still
    -- exists but is disabled, at any level of the chain (template, template
    -- module instance, template app instance).
    --
    -- Scope is the same natural-key chain the Portal uses to recognize
    -- template-managed rows before it blocks direct edits of them: a concrete
    -- row only joins when its instance uses the template and every key matches.
    -- Hand-created rows have keys that do not exist in the template, so they
    -- never join and are never touched. A WHEN NOT MATCHED BY SOURCE branch was
    -- considered and rejected: the app source is filtered by the requesting
    -- host and host-template assignment, so such a branch would turn off rows
    -- that belong to other hosts on every host-scoped run.
    --
    -- Placement filters (host, host template, host assignment activity) are
    -- deliberately NOT part of the disable condition: a disabled template row
    -- means off everywhere, and any in-scope materialization run heals all
    -- placements of it in one pass.
    UPDATE mi
    SET IsEnabled = 0,
        UpdatedUtc = SYSUTCDATETIME()
    FROM omp.ModuleInstances mi
    INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
    INNER JOIN omp.InstanceTemplates it ON it.InstanceTemplateId = i.InstanceTemplateId
    INNER JOIN omp.InstanceTemplateModuleInstances tmi
        ON tmi.InstanceTemplateId = it.InstanceTemplateId
       AND tmi.ModuleId = mi.ModuleId
       AND tmi.ModuleInstanceKey = mi.ModuleInstanceKey
    WHERE mi.IsEnabled = 1
      AND (it.IsEnabled = 0 OR tmi.IsEnabled = 0)
      AND i.IsEnabled = 1
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
      );

    SET @ModuleDisableChanges = @@ROWCOUNT;

    UPDATE ai
    SET IsEnabled = 0,
        UpdatedUtc = SYSUTCDATETIME()
    FROM omp.AppInstances ai
    INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
    INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
    INNER JOIN omp.InstanceTemplates it ON it.InstanceTemplateId = i.InstanceTemplateId
    INNER JOIN omp.InstanceTemplateModuleInstances tmi
        ON tmi.InstanceTemplateId = it.InstanceTemplateId
       AND tmi.ModuleId = mi.ModuleId
       AND tmi.ModuleInstanceKey = mi.ModuleInstanceKey
    INNER JOIN omp.InstanceTemplateAppInstances tai
        ON tai.InstanceTemplateModuleInstanceId = tmi.InstanceTemplateModuleInstanceId
       AND tai.AppInstanceKey = ai.AppInstanceKey
    WHERE ai.IsEnabled = 1
      AND (it.IsEnabled = 0 OR tmi.IsEnabled = 0 OR tai.IsEnabled = 0)
      AND i.IsEnabled = 1
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
      );

    SET @AppDisableChanges = @@ROWCOUNT;

    SELECT
        CAST((SELECT COUNT(1) FROM @ModuleActions) + @ModuleDisableChanges AS int) AS ModuleInstanceChanges,
        CAST((SELECT COUNT(1) FROM @AppActions) + @AppDisableChanges AS int) AS AppInstanceChanges,
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

        -- Session revocation stamp (R7-F10). The sign-in writes this value into
        -- the session cookie and every request compares them; rotating the
        -- stamp (account disabled, password changed) ends existing sessions.
        security_stamp uniqueidentifier NOT NULL CONSTRAINT DF_omp_users_security_stamp DEFAULT NEWID(),

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

-- Session revocation stamp for existing installations (R7-F10). Every account
-- gets its own value, which also ends all sessions that predate the column:
-- their cookies carry no stamp and fail the next validation.
IF OBJECT_ID(N'omp.users', N'U') IS NOT NULL
   AND COL_LENGTH(N'omp.users', N'security_stamp') IS NULL
BEGIN
    ALTER TABLE omp.users
    ADD security_stamp uniqueidentifier NOT NULL
        CONSTRAINT DF_omp_users_security_stamp DEFAULT NEWID() WITH VALUES;
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
-- Durable push event outbox
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.push_event_outbox', N'U') IS NULL
BEGIN
    CREATE TABLE omp.push_event_outbox
    (
        push_event_id bigint IDENTITY(1,1) NOT NULL,

        -- The first supported category is a wake-up event for topbar refresh.
        -- Dispatchers must treat push events as at-least-once notifications and
        -- keep handlers idempotent; persistent user-visible content belongs in
        -- domain tables such as omp.notifications.
        event_category nvarchar(80) NOT NULL,
        target_type nvarchar(40) NOT NULL CONSTRAINT DF_omp_push_event_outbox_target_type DEFAULT(N'user'),
        target_user_id int NULL,
        target_json nvarchar(2048) NOT NULL,
        payload_json nvarchar(max) NULL,
        deduplication_key nvarchar(200) NULL,
        correlation_key nvarchar(200) NULL,
        status nvarchar(40) NOT NULL CONSTRAINT DF_omp_push_event_outbox_status DEFAULT(N'pending'),
        lease_token uniqueidentifier NULL,
        lease_owner nvarchar(200) NULL,
        lease_until_utc datetime2(3) NULL,
        retry_count int NOT NULL CONSTRAINT DF_omp_push_event_outbox_retry_count DEFAULT(0),
        max_retries int NOT NULL CONSTRAINT DF_omp_push_event_outbox_max_retries DEFAULT(5),
        error_message nvarchar(2048) NULL,
        created_utc datetime2(3) NOT NULL CONSTRAINT DF_omp_push_event_outbox_created_utc DEFAULT SYSUTCDATETIME(),
        scheduled_utc datetime2(3) NOT NULL CONSTRAINT DF_omp_push_event_outbox_scheduled_utc DEFAULT SYSUTCDATETIME(),
        dispatched_utc datetime2(3) NULL,
        completed_utc datetime2(3) NULL,
        dead_lettered_utc datetime2(3) NULL,

        CONSTRAINT PK_omp_push_event_outbox PRIMARY KEY(push_event_id),
        CONSTRAINT FK_omp_push_event_outbox_user FOREIGN KEY(target_user_id) REFERENCES omp.users(user_id)
    );
END
GO

IF COL_LENGTH(N'omp.push_event_outbox', N'target_json') IS NULL
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD target_json nvarchar(2048) NULL;
END
GO

UPDATE omp.push_event_outbox
SET target_json = CASE
        WHEN target_type = N'user' AND target_user_id IS NOT NULL
            THEN CONCAT(N'{"kind":"user","ids":["', CONVERT(nvarchar(20), target_user_id), N'"]}')
        WHEN target_type = N'authenticated'
            THEN N'{"kind":"authenticated","ids":[]}'
        WHEN target_type = N'broadcast'
            THEN N'{"kind":"broadcast","ids":[]}'
        ELSE N'{"kind":"broadcast","ids":[]}'
    END
WHERE target_json IS NULL;
GO

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'omp.push_event_outbox')
      AND name = N'target_json'
      AND is_nullable = 1
)
BEGIN
    ALTER TABLE omp.push_event_outbox
        ALTER COLUMN target_json nvarchar(2048) NOT NULL;
END
GO

IF COL_LENGTH(N'omp.push_event_outbox', N'deduplication_key') IS NULL
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD deduplication_key nvarchar(200) NULL;
END
GO

IF COL_LENGTH(N'omp.push_event_outbox', N'correlation_key') IS NULL
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD correlation_key nvarchar(200) NULL;
END
GO

IF COL_LENGTH(N'omp.push_event_outbox', N'lease_owner') IS NULL
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD lease_owner nvarchar(200) NULL;
END
GO

IF COL_LENGTH(N'omp.push_event_outbox', N'completed_utc') IS NULL
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD completed_utc datetime2(3) NULL;
END
GO

IF COL_LENGTH(N'omp.push_event_outbox', N'dead_lettered_utc') IS NULL
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD dead_lettered_utc datetime2(3) NULL;
END
GO

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'omp.push_event_outbox')
      AND name = N'error_message'
      AND max_length = -1
)
BEGIN
    UPDATE omp.push_event_outbox
    SET error_message = LEFT(error_message, 2048)
    WHERE LEN(error_message) > 2048;

    ALTER TABLE omp.push_event_outbox
        ALTER COLUMN error_message nvarchar(2048) NULL;
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_event_category')
BEGIN
    ALTER TABLE omp.push_event_outbox DROP CONSTRAINT CK_omp_push_event_outbox_event_category;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_event_category')
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD CONSTRAINT CK_omp_push_event_outbox_event_category CHECK(LEN(LTRIM(RTRIM(event_category))) > 0);
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_target_type')
BEGIN
    ALTER TABLE omp.push_event_outbox DROP CONSTRAINT CK_omp_push_event_outbox_target_type;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_target_type')
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD CONSTRAINT CK_omp_push_event_outbox_target_type CHECK(target_type IN (N'user', N'role', N'broadcast', N'authenticated', N'app', N'module'));
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_user_target')
BEGIN
    ALTER TABLE omp.push_event_outbox DROP CONSTRAINT CK_omp_push_event_outbox_user_target;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_user_target')
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD CONSTRAINT CK_omp_push_event_outbox_user_target CHECK
        (
            (target_type = N'user' AND target_user_id IS NOT NULL)
            OR
            (target_type <> N'user' AND target_user_id IS NULL)
        );
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_retry')
BEGIN
    ALTER TABLE omp.push_event_outbox DROP CONSTRAINT CK_omp_push_event_outbox_retry;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_retry')
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD CONSTRAINT CK_omp_push_event_outbox_retry CHECK(retry_count >= 0 AND max_retries BETWEEN 0 AND 20);
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_status')
BEGIN
    ALTER TABLE omp.push_event_outbox DROP CONSTRAINT CK_omp_push_event_outbox_status;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_status')
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD CONSTRAINT CK_omp_push_event_outbox_status CHECK(status IN (N'pending', N'processing', N'dispatched', N'failed', N'dead-lettered'));
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_target_json')
BEGIN
    ALTER TABLE omp.push_event_outbox DROP CONSTRAINT CK_omp_push_event_outbox_target_json;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_target_json')
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD CONSTRAINT CK_omp_push_event_outbox_target_json CHECK(ISJSON(target_json) = 1 AND DATALENGTH(target_json) <= 4096);
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_payload_json')
BEGIN
    ALTER TABLE omp.push_event_outbox DROP CONSTRAINT CK_omp_push_event_outbox_payload_json;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_payload_json')
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD CONSTRAINT CK_omp_push_event_outbox_payload_json CHECK(payload_json IS NULL OR (ISJSON(payload_json) = 1 AND DATALENGTH(payload_json) <= 32768));
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_keys')
BEGIN
    ALTER TABLE omp.push_event_outbox DROP CONSTRAINT CK_omp_push_event_outbox_keys;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_omp_push_event_outbox_keys')
BEGIN
    ALTER TABLE omp.push_event_outbox
        ADD CONSTRAINT CK_omp_push_event_outbox_keys CHECK
        (
            (deduplication_key IS NULL OR LEN(LTRIM(RTRIM(deduplication_key))) > 0)
            AND
            (correlation_key IS NULL OR LEN(LTRIM(RTRIM(correlation_key))) > 0)
        );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_push_event_outbox_pending'
      AND object_id = OBJECT_ID(N'omp.push_event_outbox')
)
BEGIN
    CREATE INDEX IX_omp_push_event_outbox_pending
        ON omp.push_event_outbox(status, scheduled_utc, lease_until_utc, push_event_id)
        INCLUDE(event_category, target_type, target_user_id, retry_count, max_retries, lease_token, lease_owner,
                completed_utc, dead_lettered_utc)
        WHERE status IN (N'pending', N'processing');
END
GO

-- R11-Q1. The drain query also filters completed_utc IS NULL AND dead_lettered_utc IS NULL,
-- and neither column was in the index above -- so every candidate row needed a lookup back
-- to the base table just to be discarded. SQL Server's own missing-index DMV ranked this
-- the single highest-impact gap in the database by a factor of ten thousand: 79 401 calls,
-- because this is the notification outbox and it drains continuously.
--
-- The two columns are added as INCLUDE rather than to the filter. Narrowing the filter
-- would shrink the index further, but it would also change which rows it covers, and this
-- index is the one the drain depends on; an INCLUDE removes the lookup without changing
-- what is indexed. Existing installations get it through the rebuild below.
IF EXISTS
(
    SELECT 1
    FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.name = N'IX_omp_push_event_outbox_pending'
      AND i.object_id = OBJECT_ID(N'omp.push_event_outbox')
    GROUP BY i.object_id, i.index_id
    HAVING SUM(CASE WHEN COL_NAME(ic.object_id, ic.column_id) IN (N'completed_utc', N'dead_lettered_utc') THEN 1 ELSE 0 END) < 2
)
BEGIN
    CREATE INDEX IX_omp_push_event_outbox_pending
        ON omp.push_event_outbox(status, scheduled_utc, lease_until_utc, push_event_id)
        INCLUDE(event_category, target_type, target_user_id, retry_count, max_retries, lease_token, lease_owner,
                completed_utc, dead_lettered_utc)
        WHERE status IN (N'pending', N'processing')
        WITH (DROP_EXISTING = ON);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_push_event_outbox_user'
      AND object_id = OBJECT_ID(N'omp.push_event_outbox')
)
BEGIN
    CREATE INDEX IX_omp_push_event_outbox_user
        ON omp.push_event_outbox(target_user_id, created_utc DESC, push_event_id DESC)
        INCLUDE(status, event_category)
        WHERE target_user_id IS NOT NULL;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_push_event_outbox_cleanup'
      AND object_id = OBJECT_ID(N'omp.push_event_outbox')
)
BEGIN
    CREATE INDEX IX_omp_push_event_outbox_cleanup
        ON omp.push_event_outbox(status, created_utc)
        WHERE status IN (N'dispatched', N'failed', N'dead-lettered');
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_omp_push_event_outbox_deduplication_key'
      AND object_id = OBJECT_ID(N'omp.push_event_outbox')
)
BEGIN
    CREATE UNIQUE INDEX UX_omp_push_event_outbox_deduplication_key
        ON omp.push_event_outbox(deduplication_key)
        WHERE deduplication_key IS NOT NULL;
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_push_event_outbox_correlation_key'
      AND object_id = OBJECT_ID(N'omp.push_event_outbox')
)
BEGIN
    CREATE INDEX IX_omp_push_event_outbox_correlation_key
        ON omp.push_event_outbox(correlation_key, created_utc DESC, push_event_id DESC)
        WHERE correlation_key IS NOT NULL;
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
        -- Keep the legacy shared binary-data column name. The same name is
        -- used by widget/media binary tables and repository code; renaming only
        -- message attachments would add compatibility churn without improving
        -- runtime behavior.
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
        -- Link status is intentionally open-ended. Built-in values currently
        -- used by OMP are enabled, disabled and deleted.
        auth_status nvarchar(50) NOT NULL CONSTRAINT DF_omp_user_auth_auth_status DEFAULT(N'enabled'),
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_user_auth_created_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_user_auth PRIMARY KEY(user_auth_id),
        CONSTRAINT FK_omp_user_auth_user FOREIGN KEY(user_id) REFERENCES omp.users(user_id),
        CONSTRAINT FK_omp_user_auth_provider FOREIGN KEY(provider_id) REFERENCES omp.auth_providers(provider_id),
        CONSTRAINT UQ_omp_user_auth_provider_key UNIQUE(provider_id, provider_user_hash)
    );
END
GO

IF COL_LENGTH(N'omp.user_auth', N'auth_status') IS NULL
BEGIN
    ALTER TABLE omp.user_auth
        ADD auth_status nvarchar(50) NOT NULL
            CONSTRAINT DF_omp_user_auth_auth_status DEFAULT(N'enabled') WITH VALUES;
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
-- R7-F12: local password user-name canonicalization (begin)
-------------------------------------------------------------------------------
-- The platform canonicalizes local password user names in application code
-- (trim + invariant lowercase, LocalPasswordIdentity.NormalizeUserName) on
-- every write and every read, and compares them pinned to a binary collation
-- so the database collation cannot redefine matching. Rows written before
-- that rule -- or by hand -- may hold a different casing and would be
-- invisible to the exact canonical lookup; fold them to the canonical form
-- here. The comparisons below are binary on purpose: under a
-- case-insensitive collation 'Alice' = 'alice' would make the row look
-- already canonical and self-match the collision guard. Rows whose canonical
-- form collides with another row (only possible on case-sensitive
-- collations, where the primary key allows both casings) are left untouched:
-- choosing which of two credential sets survives is an operator decision,
-- not a migration decision.
-- Note: LOWER() folds by the database collation, which differs from the
-- invariant-culture fold only outside ASCII; ASCII user names -- the
-- expected case -- fold identically.
UPDATE target
SET user_name = LOWER(LTRIM(RTRIM(target.user_name)))
FROM omp.auth_provider_lpwd target
WHERE target.user_name COLLATE Latin1_General_100_BIN2 <> LOWER(LTRIM(RTRIM(target.user_name)))
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.auth_provider_lpwd existing
      WHERE existing.user_name COLLATE Latin1_General_100_BIN2 = LOWER(LTRIM(RTRIM(target.user_name)))
  );
GO

-- The omp.user_auth link key for the lpwd provider is the same canonical
-- user name; fold legacy link keys the same way, with the same collision
-- guard, so the migrated hash row and its link stay in step.
UPDATE target
SET provider_user_key = LOWER(LTRIM(RTRIM(target.provider_user_key)))
FROM omp.user_auth target
INNER JOIN omp.auth_providers ap ON ap.provider_id = target.provider_id
WHERE ap.display_name = N'lpwd'
  AND target.provider_user_key COLLATE Latin1_General_100_BIN2 <> LOWER(LTRIM(RTRIM(target.provider_user_key)))
  AND NOT EXISTS
  (
      SELECT 1
      FROM omp.user_auth existing
      WHERE existing.provider_id = target.provider_id
        AND existing.provider_user_key COLLATE Latin1_General_100_BIN2 = LOWER(LTRIM(RTRIM(target.provider_user_key)))
  );
GO

-- Report the rows the migration deliberately left behind. After the guarded
-- updates above, a row that is still not in canonical form exists only
-- because its canonical form collides with another row (case-sensitive
-- collations where the primary key allowed both casings); which credential
-- set survives is an operator decision, not a migration decision. Without
-- this line the leftovers were invisible to the operator. Note that the
-- automated module-SQL import path discards PRINT output, so these lines are
-- visible when the script runs through sqlcmd; the same counts can be
-- queried at any time with the predicates below (see
-- docs/AUTHENTICATION_AND_RBAC.md, "Operations and Upgrade Notes").
DECLARE @LpwdCollisionsLeft int;
DECLARE @UserAuthCollisionsLeft int;

SELECT @LpwdCollisionsLeft = COUNT(*)
FROM omp.auth_provider_lpwd
WHERE user_name COLLATE Latin1_General_100_BIN2 <> LOWER(LTRIM(RTRIM(user_name)));

SELECT @UserAuthCollisionsLeft = COUNT(*)
FROM omp.user_auth target
INNER JOIN omp.auth_providers ap ON ap.provider_id = target.provider_id
WHERE ap.display_name = N'lpwd'
  AND target.provider_user_key COLLATE Latin1_General_100_BIN2 <> LOWER(LTRIM(RTRIM(target.provider_user_key)));

PRINT N'R7-F12 user-name canonicalization: ' + CONVERT(nvarchar(20), @LpwdCollisionsLeft)
    + N' omp.auth_provider_lpwd row(s) and ' + CONVERT(nvarchar(20), @UserAuthCollisionsLeft)
    + N' omp.user_auth row(s) left unresolved (case-fold collisions).';

IF @LpwdCollisionsLeft > 0 OR @UserAuthCollisionsLeft > 0
BEGIN
    PRINT N'R7-F12: ACTION REQUIRED -- colliding rows still carry their original casing and'
        + N' are invisible to canonical sign-in. An operator must decide which credential set'
        + N' survives and remove or rename the other row by hand.';
END
GO
-------------------------------------------------------------------------------
-- R7-F12: local password user-name canonicalization (end)
-------------------------------------------------------------------------------

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
        ValidationRegex nvarchar(1000) NULL,
        ExampleValues nvarchar(1000) NULL,
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

IF COL_LENGTH(N'omp.config_setting_definitions', N'ValidationRegex') IS NULL
BEGIN
    ALTER TABLE omp.config_setting_definitions ADD ValidationRegex nvarchar(1000) NULL;
END
GO

IF COL_LENGTH(N'omp.config_setting_definitions', N'ExampleValues') IS NULL
BEGIN
    ALTER TABLE omp.config_setting_definitions ADD ExampleValues nvarchar(1000) NULL;
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
        -- ConfigUsr is an older public schema name kept for compatibility with
        -- existing SQL, services, and portable configuration packages.
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
INSERT INTO omp.config_setting_definitions(ConfigCategory, ConfigSetting, Description, ValidationRegex, ExampleValues, SortOrder, IsEnabled)
SELECT DISTINCT cs.ConfigCategory,
       cs.ConfigSetting,
       NULL,
       N''^[\s\S]*$'',
       N''Custom value'',
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

UPDATE omp.config_setting_definitions
SET ValidationRegex = N'^[\s\S]*$',
    UpdatedUtc = SYSUTCDATETIME()
WHERE ValidationRegex IS NULL;
GO

UPDATE omp.config_setting_definitions
SET ExampleValues = N'Custom value',
    UpdatedUtc = SYSUTCDATETIME()
WHERE ExampleValues IS NULL;
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

-------------------------------------------------------------------------------
-- Link boxes (user-curated links merged into shared LinkBox components)
-------------------------------------------------------------------------------
IF OBJECT_ID(N'omp.link_box_items', N'U') IS NULL
BEGIN
    CREATE TABLE omp.link_box_items
    (
        link_box_item_id bigint IDENTITY(1,1) NOT NULL,
        box_key nvarchar(200) NOT NULL,
        label nvarchar(200) NOT NULL,
        url nvarchar(400) NOT NULL,
        group_key nvarchar(100) NULL,
        sort_order int NOT NULL CONSTRAINT DF_omp_link_box_items_sort_order DEFAULT(0),
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_link_box_items_created_at DEFAULT SYSUTCDATETIME(),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_link_box_items_updated_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_link_box_items PRIMARY KEY(link_box_item_id),
        CONSTRAINT UQ_omp_link_box_items_box_label UNIQUE(box_key, label)
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_omp_link_box_items_box'
      AND object_id = OBJECT_ID(N'omp.link_box_items')
)
BEGIN
    CREATE INDEX IX_omp_link_box_items_box
        ON omp.link_box_items(box_key, sort_order, label)
        INCLUDE(url, group_key);
END
GO

IF OBJECT_ID(N'omp.link_boxes', N'U') IS NULL
BEGIN
    CREATE TABLE omp.link_boxes
    (
        box_key nvarchar(200) NOT NULL,
        title nvarchar(200) NOT NULL,
        required_permission nvarchar(200) NULL,
        sort_order int NOT NULL CONSTRAINT DF_omp_link_boxes_sort_order DEFAULT(0),
        created_at datetime2(3) NOT NULL CONSTRAINT DF_omp_link_boxes_created_at DEFAULT SYSUTCDATETIME(),
        updated_at datetime2(3) NOT NULL CONSTRAINT DF_omp_link_boxes_updated_at DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_omp_link_boxes PRIMARY KEY(box_key)
    );
END
GO

-- No FK from link_box_items.box_key: items may exist for boxes that have not
-- been registered yet (the editor upserts the box row on first use).
IF COL_LENGTH(N'omp.link_box_items', N'required_permission') IS NULL
BEGIN
    ALTER TABLE omp.link_box_items ADD required_permission nvarchar(200) NULL;
END
GO
