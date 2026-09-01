-- File: examples/WorkerAppModule/Sql/2-initialize-example-workerapp.sql
/*
Seeds default values and OMP registration rows for the example Worker App module.

Prerequisites:
- Run ../../sql/1-setup-openmoduleplatform.sql
- Run ../../sql/2-initialize-openmoduleplatform.sql
- Run 1-setup-example-workerapp.sql
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

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
DECLARE @WorkerInstanceId uniqueidentifier = '11111111-1111-1111-1111-111111111324';
DECLARE @WorkerViewPermissionId int;
DECLARE @WorkerAdminPermissionId int;
DECLARE @InitialWorkerConfigId int;

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
        AppType = N'Worker',
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
        N'Worker',
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

SELECT @WorkerModuleInstanceId = COALESCE(
    (
        SELECT ModuleInstanceId
        FROM omp.ModuleInstances
        WHERE InstanceId = @InstanceId
          AND ModuleInstanceKey = N'example_workerapp'
    ),
    @WorkerModuleInstanceId);

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

SELECT @WorkerWebAppInstanceId = COALESCE(
    (
        SELECT AppInstanceId
        FROM omp.AppInstances
        WHERE ModuleInstanceId = @WorkerModuleInstanceId
          AND AppInstanceKey = N'example_workerapp_webapp'
    ),
    @WorkerWebAppInstanceId);

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
        -- Direct host placement and template placement are mutually exclusive
        -- (CK_omp_AppInstances_OneHostPlacement). A re-run against a live
        -- installation must not fail on -- or steal -- a placement the row
        -- already has (a template-placed row carries TargetHostTemplateId, and
        -- assigning HostId then violates the constraint and aborts the whole
        -- definition import; observed 2026-08-20). Only assign the sample host
        -- when the row is not placed at all.
        HostId = CASE WHEN HostId IS NULL AND TargetHostTemplateId IS NULL THEN @SampleHostId ELSE HostId END,
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

SELECT @WorkerAppInstanceId = COALESCE(
    (
        SELECT AppInstanceId
        FROM omp.AppInstances
        WHERE ModuleInstanceId = @WorkerModuleInstanceId
          AND AppInstanceKey = N'example_workerapp_worker'
    ),
    @WorkerAppInstanceId);

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
        NULL,
        N'default',
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
        -- Placement-preserving for the same reason as the webapp row above
        -- (CK_omp_AppInstances_OneHostPlacement).
        HostId = CASE WHEN HostId IS NULL AND TargetHostTemplateId IS NULL THEN @SampleHostId ELSE HostId END,
        AppId = @WorkerAppId,
        AppInstanceKey = N'example_workerapp_worker',
        DisplayName = N'Example Managed Worker',
        Description = N'Primary manager-driven worker app instance for the example module',
        InstallPath = NULL,
        InstallationName = N'default',
        ConfigId = @InitialWorkerConfigId,
        IsEnabled = 1,
        IsAllowed = 1,
        DesiredState = 1,
        SortOrder = 411,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE AppInstanceId = @WorkerAppInstanceId;
END

MERGE omp.WorkerInstances AS target
USING
(
    SELECT @WorkerInstanceId AS WorkerInstanceId,
           @WorkerAppInstanceId AS AppInstanceId,
           @SampleHostId AS HostId,
           N'example_workerapp_worker_default' AS WorkerInstanceKey,
           N'Example WorkerApp Worker Default' AS DisplayName,
           N'Default manager-driven worker process for the example WorkerApp module.' AS Description,
           CAST(NULL AS nvarchar(max)) AS ConfigurationJson,
           1 AS IsEnabled,
           1 AS IsAllowed,
           1 AS DesiredState,
           411 AS SortOrder
) AS source
ON target.WorkerInstanceId = source.WorkerInstanceId
WHEN MATCHED THEN
    UPDATE SET AppInstanceId = source.AppInstanceId,
               HostId = source.HostId,
               WorkerInstanceKey = source.WorkerInstanceKey,
               DisplayName = source.DisplayName,
               Description = source.Description,
               ConfigurationJson = source.ConfigurationJson,
               IsEnabled = source.IsEnabled,
               IsAllowed = source.IsAllowed,
               DesiredState = source.DesiredState,
               SortOrder = source.SortOrder,
               UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT
    (
        WorkerInstanceId,
        AppInstanceId,
        HostId,
        WorkerInstanceKey,
        DisplayName,
        Description,
        ConfigurationJson,
        IsEnabled,
        IsAllowed,
        DesiredState,
        SortOrder
    )
    VALUES
    (
        source.WorkerInstanceId,
        source.AppInstanceId,
        source.HostId,
        source.WorkerInstanceKey,
        source.DisplayName,
        source.Description,
        source.ConfigurationJson,
        source.IsEnabled,
        source.IsAllowed,
        source.DesiredState,
        source.SortOrder
    );

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
        NULL,
        N'default',
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
