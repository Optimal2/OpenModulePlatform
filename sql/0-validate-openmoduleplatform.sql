SET NOCOUNT ON;

DECLARE @Missing int = 0;

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
        (N'omp', N'users', N'profile_image_file_name'),
        (N'omp', N'users', N'profile_image_storage_key'),
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

SELECT
    CAST(CASE WHEN @Missing = 0 THEN 1 ELSE 0 END AS bit) AS IsHealthy,
    CASE
        WHEN @Missing = 0 THEN N'Core OMP storage is healthy.'
        ELSE CONCAT(N'Core OMP storage is missing ', @Missing, N' required object(s). Run SQL repair for the omp_core module.')
    END AS Message;
