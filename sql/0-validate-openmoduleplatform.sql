SET NOCOUNT ON;

DECLARE @Missing int = 0;
DECLARE @InvalidArtifactBindings int = 0;
DECLARE @MissingProgrammableObjects nvarchar(max) = N'';

IF SCHEMA_ID(N'omp') IS NULL
BEGIN
    SET @Missing = @Missing + 1;
END;

;WITH RequiredTables(SchemaName, TableName) AS
(
    SELECT v.SchemaName, v.TableName
    FROM (VALUES
        (N'omp', N'Permissions'),
        (N'omp', N'Roles'),
        (N'omp', N'RolePermissions'),
        (N'omp', N'RolePrincipals'),
        (N'omp', N'AuditLog'),
        (N'omp', N'InstanceTemplates'),
        (N'omp', N'HostTemplates'),
        (N'omp', N'Instances'),
        (N'omp', N'Modules'),
        (N'omp', N'ModuleInstances'),
        (N'omp', N'Apps'),
        (N'omp', N'AppPermissions'),
        (N'omp', N'ModuleDefinitionDocuments'),
        (N'omp', N'ModuleDefinitionArtifactCompatibility'),
        (N'omp', N'ModuleDefinitionSqlExecutions'),
        (N'omp', N'Artifacts'),
        (N'omp', N'ArtifactConfigurationFiles'),
        (N'omp', N'HostConfigurationDocuments'),
        (N'omp', N'ConfigOverlayDocuments'),
        (N'omp', N'ConfigOverlayConfigurationFiles'),
        (N'omp', N'Hosts'),
        (N'omp', N'AppInstances'),
        (N'omp', N'AppWorkerDefinitions'),
        (N'omp', N'AppInstanceRuntimeStates'),
        (N'omp', N'HostArtifactRequirements'),
        (N'omp', N'HostArtifactStates'),
        (N'omp', N'HostAppDeploymentStates'),
        (N'omp', N'WebAppHealthStates'),
        (N'omp', N'HostResourceSamples'),
        (N'omp', N'HostResourceLatest'),
        (N'omp', N'HostAgentDesiredStates'),
        (N'omp', N'HostAgentRuntimeStates'),
        (N'omp', N'HostAgentLeases'),
        (N'omp', N'HostAgentJobs'),
        (N'omp', N'MaintenanceFindings'),
        (N'omp', N'WorkerInstances'),
        (N'omp', N'WorkerInstanceRuntimeStates'),
        (N'omp', N'InstanceTemplateHosts'),
        (N'omp', N'InstanceTemplateModuleInstances'),
        (N'omp', N'InstanceTemplateAppInstances'),
        (N'omp', N'HostDeploymentAssignments'),
        (N'omp', N'HostDeployments'),
        (N'omp', N'users'),
        (N'omp', N'notifications'),
        (N'omp', N'push_event_outbox'),
        (N'omp', N'banners'),
        (N'omp', N'banner_targets'),
        (N'omp', N'conversations'),
        (N'omp', N'messages'),
        (N'omp', N'conversation_participants'),
        (N'omp', N'message_attachments'),
        (N'omp', N'direct_conversations'),
        (N'omp', N'auth_providers'),
        (N'omp', N'user_auth'),
        (N'omp', N'auth_provider_lpwd'),
        (N'omp', N'config_setting_definitions'),
        (N'omp', N'config_settings')
    ) AS v(SchemaName, TableName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredTables required
WHERE OBJECT_ID(required.SchemaName + N'.' + required.TableName, N'U') IS NULL;

;WITH RequiredColumns(SchemaName, TableName, ColumnName) AS
(
    SELECT v.SchemaName, v.TableName, v.ColumnName
    FROM (VALUES
        (N'omp', N'InstanceTemplates', N'SortOrder'),
        (N'omp', N'HostTemplates', N'SortOrder'),
        (N'omp', N'Permissions', N'UpdatedUtc'),
        (N'omp', N'Roles', N'UpdatedUtc'),
        (N'omp', N'Apps', N'AllowMultipleActiveInstances'),
        (N'omp', N'ModuleDefinitionArtifactCompatibility', N'RelativePathTemplate'),
        (N'omp', N'AppInstances', N'TargetHostTemplateId'),
        (N'omp', N'HostAppDeploymentStates', N'CredentialAutomationMode'),
        (N'omp', N'HostAppDeploymentStates', N'DesiredRuntimeIdentity'),
        (N'omp', N'HostAppDeploymentStates', N'ActualRuntimeIdentity'),
        (N'omp', N'HostAppDeploymentStates', N'IdentityCheckStatus'),
        (N'omp', N'HostAppDeploymentStates', N'IdentityRepairRequestedUtc'),
        (N'omp', N'HostAppDeploymentStates', N'IdentityRepairRequestedBy'),
        (N'omp', N'InstanceTemplateAppInstances', N'TargetHostTemplateId'),
        (N'omp', N'InstanceTemplateAppInstances', N'IsAllowed'),
        (N'omp', N'HostAgentJobs', N'LeaseToken'),
        (N'omp', N'users', N'profile_image_file_name'),
        (N'omp', N'users', N'profile_image_storage_key'),
        (N'omp', N'push_event_outbox', N'event_category'),
        (N'omp', N'push_event_outbox', N'target_type'),
        (N'omp', N'push_event_outbox', N'target_user_id'),
        (N'omp', N'push_event_outbox', N'target_json'),
        (N'omp', N'push_event_outbox', N'payload_json'),
        (N'omp', N'push_event_outbox', N'deduplication_key'),
        (N'omp', N'push_event_outbox', N'correlation_key'),
        (N'omp', N'push_event_outbox', N'status'),
        (N'omp', N'push_event_outbox', N'lease_token'),
        (N'omp', N'push_event_outbox', N'lease_owner'),
        (N'omp', N'push_event_outbox', N'lease_until_utc'),
        (N'omp', N'push_event_outbox', N'retry_count'),
        (N'omp', N'push_event_outbox', N'max_retries'),
        (N'omp', N'push_event_outbox', N'error_message'),
        (N'omp', N'push_event_outbox', N'created_utc'),
        (N'omp', N'push_event_outbox', N'scheduled_utc'),
        (N'omp', N'push_event_outbox', N'dispatched_utc'),
        (N'omp', N'push_event_outbox', N'completed_utc'),
        (N'omp', N'push_event_outbox', N'dead_lettered_utc'),
        (N'omp', N'user_auth', N'auth_status'),
        (N'omp', N'config_setting_definitions', N'Description'),
        (N'omp', N'config_setting_definitions', N'SortOrder'),
        (N'omp', N'config_setting_definitions', N'IsEnabled'),
        (N'omp', N'config_setting_definitions', N'CreatedUtc'),
        (N'omp', N'config_setting_definitions', N'UpdatedUtc'),
        (N'omp', N'config_settings', N'ConfigSettingId'),
        (N'omp', N'config_settings', N'ConfigValue'),
        (N'omp', N'config_settings', N'ConfigPriority'),
        (N'omp', N'config_settings', N'ConfigPermission'),
        (N'omp', N'config_settings', N'ConfigScopeRank')
    ) AS v(SchemaName, TableName, ColumnName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredColumns required
WHERE COL_LENGTH(required.SchemaName + N'.' + required.TableName, required.ColumnName) IS NULL;

;WITH RequiredProgrammableObjects(ObjectType, ObjectName) AS
(
    SELECT v.ObjectType, v.ObjectName
    FROM (VALUES
        (N'FN', N'omp.IsArtifactPackageCompatibleWithAppType'),
        (N'TR', N'omp.TR_AppInstances_ValidateArtifactCompatibility'),
        (N'TR', N'omp.TR_WorkerInstances_ValidateArtifactCompatibility'),
        (N'TR', N'omp.TR_InstanceTemplateAppInstances_ValidateArtifactCompatibility')
    ) AS v(ObjectType, ObjectName)
)
SELECT @Missing = @Missing + COUNT(1)
FROM RequiredProgrammableObjects required
WHERE OBJECT_ID(required.ObjectName, required.ObjectType) IS NULL;

;WITH RequiredProgrammableObjects(ObjectType, ObjectName) AS
(
    SELECT v.ObjectType, v.ObjectName
    FROM (VALUES
        (N'FN', N'omp.IsArtifactPackageCompatibleWithAppType'),
        (N'TR', N'omp.TR_AppInstances_ValidateArtifactCompatibility'),
        (N'TR', N'omp.TR_WorkerInstances_ValidateArtifactCompatibility'),
        (N'TR', N'omp.TR_InstanceTemplateAppInstances_ValidateArtifactCompatibility')
    ) AS v(ObjectType, ObjectName)
)
SELECT @MissingProgrammableObjects =
    CASE
        WHEN @MissingProgrammableObjects = N'' THEN required.ObjectName
        ELSE CONCAT(@MissingProgrammableObjects, N', ', required.ObjectName)
    END
FROM RequiredProgrammableObjects required
WHERE OBJECT_ID(required.ObjectName, required.ObjectType) IS NULL;

IF OBJECT_ID(N'omp.AppInstances', N'U') IS NOT NULL
   AND OBJECT_ID(N'omp.WorkerInstances', N'U') IS NOT NULL
   AND OBJECT_ID(N'omp.InstanceTemplateAppInstances', N'U') IS NOT NULL
   AND OBJECT_ID(N'omp.Apps', N'U') IS NOT NULL
   AND OBJECT_ID(N'omp.Artifacts', N'U') IS NOT NULL
   AND OBJECT_ID(N'omp.IsArtifactPackageCompatibleWithAppType', N'FN') IS NOT NULL
BEGIN
    -- The programmable object check above treats the compatibility function as
    -- part of the core schema, and this validation uses the same rule source as
    -- the runtime triggers and repair logic when the function is present.
    SELECT @InvalidArtifactBindings = COUNT(1)
    FROM
    (
        SELECT 1 AS InvalidBinding
        FROM omp.AppInstances appInstance
        INNER JOIN omp.Apps app
            ON app.AppId = appInstance.AppId
        INNER JOIN omp.Artifacts artifact
            ON artifact.ArtifactId = appInstance.ArtifactId
        WHERE appInstance.ArtifactId IS NOT NULL
          AND
          (
              artifact.AppId <> appInstance.AppId
              OR omp.IsArtifactPackageCompatibleWithAppType(artifact.PackageType, app.AppType) = 0
          )
        UNION ALL
        SELECT 1 AS InvalidBinding
        FROM omp.WorkerInstances workerInstance
        INNER JOIN omp.AppInstances appInstance
            ON appInstance.AppInstanceId = workerInstance.AppInstanceId
        INNER JOIN omp.Apps app
            ON app.AppId = appInstance.AppId
        INNER JOIN omp.Artifacts artifact
            ON artifact.ArtifactId = workerInstance.ArtifactId
        WHERE workerInstance.ArtifactId IS NOT NULL
          AND
          (
              artifact.AppId <> appInstance.AppId
              OR omp.IsArtifactPackageCompatibleWithAppType(artifact.PackageType, app.AppType) = 0
          )
        UNION ALL
        SELECT 1 AS InvalidBinding
        FROM omp.InstanceTemplateAppInstances templateAppInstance
        INNER JOIN omp.Apps app
            ON app.AppId = templateAppInstance.AppId
        INNER JOIN omp.Artifacts artifact
            ON artifact.ArtifactId = templateAppInstance.DesiredArtifactId
        WHERE templateAppInstance.DesiredArtifactId IS NOT NULL
          AND
          (
              artifact.AppId <> templateAppInstance.AppId
              OR omp.IsArtifactPackageCompatibleWithAppType(artifact.PackageType, app.AppType) = 0
          )
    ) invalidBindings;
END;

SELECT
    CAST(CASE WHEN @Missing = 0 AND @InvalidArtifactBindings = 0 THEN 1 ELSE 0 END AS bit) AS IsHealthy,
    CASE
        WHEN @Missing = 0 AND @InvalidArtifactBindings = 0 THEN N'Core OMP storage is healthy.'
        WHEN @Missing > 0 AND @InvalidArtifactBindings > 0
            THEN CONCAT(
                N'Core OMP storage is incomplete and has invalid artifact bindings. ',
                N'Missing objects: ', @Missing, N'. ',
                NULLIF(CONCAT(N'Missing programmable objects: ', @MissingProgrammableObjects, N'. '), N'Missing programmable objects: . '),
                N'Invalid bindings: ', @InvalidArtifactBindings, N'. ',
                N'Run SQL repair, then remediate listed bindings.')
        WHEN @Missing > 0
            THEN CONCAT(
                N'Core OMP storage is missing required object(s). ',
                N'Missing objects: ', @Missing, N'. ',
                NULLIF(CONCAT(N'Missing programmable objects: ', @MissingProgrammableObjects, N'. '), N'Missing programmable objects: . '),
                N'Run SQL repair for the omp_core module.')
        ELSE CONCAT(
            N'Core OMP storage has invalid runtime artifact binding(s). ',
            N'Invalid bindings: ', @InvalidArtifactBindings, N'. ',
            N'Remediate the listed bindings before artifact auto-apply or template changes.')
    END AS Message;

-- Do not THROW or RAISERROR from module validation SQL. The installer/import pipeline expects a
-- read-only result row with IsHealthy and Message so it can show diagnostics without aborting the
-- module-definition reader itself.

IF @InvalidArtifactBindings > 0
BEGIN
    SELECT *
    FROM
    (
        SELECT
            N'omp.AppInstances' AS BindingTable,
            CONVERT(nvarchar(100), appInstance.AppInstanceId) AS RowId,
            app.AppKey,
            app.AppType,
            artifact.ArtifactId,
            artifact.AppId AS ArtifactAppId,
            artifact.PackageType,
            artifact.TargetName,
            artifact.Version,
            CASE
                WHEN artifact.AppId <> appInstance.AppId
                    THEN N'Artifact belongs to a different app.'
                ELSE N'Artifact package type is not compatible with the app type.'
            END AS FailureReason
        FROM omp.AppInstances appInstance
        INNER JOIN omp.Apps app
            ON app.AppId = appInstance.AppId
        INNER JOIN omp.Artifacts artifact
            ON artifact.ArtifactId = appInstance.ArtifactId
        WHERE appInstance.ArtifactId IS NOT NULL
          AND
          (
              artifact.AppId <> appInstance.AppId
              OR omp.IsArtifactPackageCompatibleWithAppType(artifact.PackageType, app.AppType) = 0
          )
        UNION ALL
        SELECT
            N'omp.WorkerInstances' AS BindingTable,
            CONVERT(nvarchar(100), workerInstance.WorkerInstanceId) AS RowId,
            app.AppKey,
            app.AppType,
            artifact.ArtifactId,
            artifact.AppId AS ArtifactAppId,
            artifact.PackageType,
            artifact.TargetName,
            artifact.Version,
            CASE
                WHEN artifact.AppId <> appInstance.AppId
                    THEN N'Artifact belongs to a different app.'
                ELSE N'Artifact package type is not compatible with the app type.'
            END AS FailureReason
        FROM omp.WorkerInstances workerInstance
        INNER JOIN omp.AppInstances appInstance
            ON appInstance.AppInstanceId = workerInstance.AppInstanceId
        INNER JOIN omp.Apps app
            ON app.AppId = appInstance.AppId
        INNER JOIN omp.Artifacts artifact
            ON artifact.ArtifactId = workerInstance.ArtifactId
        WHERE workerInstance.ArtifactId IS NOT NULL
          AND
          (
              artifact.AppId <> appInstance.AppId
              OR omp.IsArtifactPackageCompatibleWithAppType(artifact.PackageType, app.AppType) = 0
          )
        UNION ALL
        SELECT
            N'omp.InstanceTemplateAppInstances' AS BindingTable,
            CONVERT(nvarchar(100), templateAppInstance.InstanceTemplateAppInstanceId) AS RowId,
            app.AppKey,
            app.AppType,
            artifact.ArtifactId,
            artifact.AppId AS ArtifactAppId,
            artifact.PackageType,
            artifact.TargetName,
            artifact.Version,
            CASE
                WHEN artifact.AppId <> templateAppInstance.AppId
                    THEN N'Artifact belongs to a different app.'
                ELSE N'Artifact package type is not compatible with the app type.'
            END AS FailureReason
        FROM omp.InstanceTemplateAppInstances templateAppInstance
        INNER JOIN omp.Apps app
            ON app.AppId = templateAppInstance.AppId
        INNER JOIN omp.Artifacts artifact
            ON artifact.ArtifactId = templateAppInstance.DesiredArtifactId
        WHERE templateAppInstance.DesiredArtifactId IS NOT NULL
          AND
          (
              artifact.AppId <> templateAppInstance.AppId
              OR omp.IsArtifactPackageCompatibleWithAppType(artifact.PackageType, app.AppType) = 0
          )
    ) invalidBindings
    ORDER BY BindingTable, AppKey, RowId;
END;
