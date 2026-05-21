// File: OpenModulePlatform.Portal/Models/AdminEditModels.cs
namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Generic select-list item used by Portal edit forms.
/// </summary>
public sealed class OptionItem
{
    public string Value { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Editable instance fields exposed by the Portal admin UI.
/// </summary>
public sealed class InstanceEditData
{
    public Guid InstanceId { get; set; }

    public string InstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int? InstanceTemplateId { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Editable host fields exposed by the Portal admin UI.
/// </summary>
public sealed class HostEditData
{
    public Guid HostId { get; set; }

    public Guid InstanceId { get; set; }

    public string HostKey { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? BaseUrl { get; set; }

    public string? Environment { get; set; }

    public string? OsFamily { get; set; }

    public string? OsVersion { get; set; }

    public string? Architecture { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Editable module definition fields.
/// </summary>
public sealed class ModuleEditData
{
    public int ModuleId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ModuleType { get; set; } = string.Empty;

    public string SchemaName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsEnabled { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Editable module-instance fields.
/// </summary>
public sealed class ModuleInstanceEditData
{
    public Guid ModuleInstanceId { get; set; }

    public Guid InstanceId { get; set; }

    public int ModuleId { get; set; }

    public string ModuleInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsEnabled { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Editable app-definition fields.
/// </summary>
public sealed class AppEditData
{
    public int AppId { get; set; }

    public int ModuleId { get; set; }

    public string AppKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AppType { get; set; } = string.Empty;

    public bool AllowMultipleActiveInstances { get; set; }

    public string? Description { get; set; }

    public bool IsEnabled { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Editable artifact metadata.
/// </summary>
public sealed class ArtifactEditData
{
    public int ArtifactId { get; set; }

    public int AppId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePath { get; set; }

    public string? Sha256 { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Editable text configuration file that HostAgent materializes for an artifact.
/// </summary>
public sealed class ArtifactConfigurationFileEditData
{
    public int ArtifactConfigurationFileId { get; set; }

    public int ArtifactId { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    public string FileContent { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Result from copying artifact-owned configuration files from an older artifact.
/// </summary>
public sealed class ArtifactConfigurationFileCopyResult
{
    public int SourceArtifactId { get; set; }

    public string SourceVersion { get; set; } = string.Empty;

    public int CopiedCount { get; set; }
}

/// <summary>
/// App-definition option with stable keys used by artifact filename metadata parsing.
/// </summary>
public sealed class ArtifactAppOption
{
    public int AppId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Artifact slot allowed by the currently applied module definition.
/// </summary>
public sealed class ArtifactCompatibilitySlot
{
    public string ModuleKey { get; set; } = string.Empty;

    public string DefinitionVersion { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePathTemplate { get; set; }

    public string? MinArtifactVersion { get; set; }

    public string? MaxArtifactVersion { get; set; }
}

/// <summary>
/// Parsed module definition document ready to store in OMP.
/// </summary>
public sealed class ModuleDefinitionDocumentEditData
{
    public int ModuleDefinitionDocumentId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string DefinitionVersion { get; set; } = string.Empty;

    public int FormatVersion { get; set; }

    public string DefinitionJson { get; set; } = string.Empty;

    public string DefinitionSha256 { get; set; } = string.Empty;

    public string? SourceName { get; set; }

    public IReadOnlyList<ModuleDefinitionCompatibilityEditData> CompatibleArtifacts { get; set; } = [];
}

/// <summary>
/// Artifact compatibility row parsed from a module definition document.
/// </summary>
public sealed class ModuleDefinitionCompatibilityEditData
{
    public string AppKey { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePathTemplate { get; set; }

    public string? MinArtifactVersion { get; set; }

    public string? MaxArtifactVersion { get; set; }
}

/// <summary>
/// Result from saving a module definition document.
/// </summary>
public sealed class ModuleDefinitionSaveResult
{
    public int ModuleDefinitionDocumentId { get; set; }

    public bool Created { get; set; }

    public bool Replaced { get; set; }

    public bool WasIdentical { get; set; }
}

/// <summary>
/// Result from applying a module definition document.
/// </summary>
public sealed class ModuleDefinitionApplyResult
{
    public bool Applied { get; set; }

    public IReadOnlyList<ModuleDefinitionArtifactReferenceRow> IncompatibleReferences { get; set; } = [];
}

/// <summary>
/// Validation state for one portable SQL script in a module definition document.
/// </summary>
public sealed class ModuleDefinitionSqlCheckRow
{
    public string Key { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public int Order { get; set; }

    public string Execution { get; set; } = string.Empty;

    public string? Path { get; set; }

    public string? Source { get; set; }

    public string ScriptSha256 { get; set; } = string.Empty;

    public bool HasInlineSql { get; set; }

    public bool IsSafe { get; set; }

    public bool HasSuccessfulExecution { get; set; }

    public bool NeedsExecution { get; set; }

    public bool CanExecute { get; set; }

    public string Status { get; set; } = string.Empty;

    public string StatusMessage { get; set; } = string.Empty;

    public DateTime? LastCompletedUtc { get; set; }

    public string? LastExecutionStatus { get; set; }

    public string? LastErrorMessage { get; set; }

    public IReadOnlyList<string> MissingRequiredObjects { get; set; } = [];
}

/// <summary>
/// Summary returned after executing module definition repair SQL.
/// </summary>
public sealed class ModuleDefinitionSqlRepairResult
{
    public int ExecutedCount { get; set; }

    public IReadOnlyList<ModuleDefinitionSqlCheckRow> RemainingProblems { get; set; } = [];
}

/// <summary>
/// Aggregated integrity state for one module definition document.
/// </summary>
public sealed class ModuleDefinitionIntegritySummaryRow
{
    public int ModuleDefinitionDocumentId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string DefinitionVersion { get; set; } = string.Empty;

    public bool IsApplied { get; set; }

    public string OverallStatus { get; set; } = "neutral";

    public string OverallStatusLabel { get; set; } = "Stored";

    public string MetadataStatus { get; set; } = "neutral";

    public string MetadataStatusLabel { get; set; } = "Not checked";

    public string DatabaseStatus { get; set; } = "neutral";

    public string DatabaseStatusLabel { get; set; } = "Not checked";

    public string SqlStatus { get; set; } = "neutral";

    public string SqlStatusLabel { get; set; } = "Not checked";

    public string DependencyStatus { get; set; } = "neutral";

    public string DependencyStatusLabel { get; set; } = "Not checked";

    public string ArtifactStatus { get; set; } = "neutral";

    public string ArtifactStatusLabel { get; set; } = "Not checked";

    public string SummaryLabel { get; set; } = "Stored definition";

    public int MissingMetadataCount { get; set; }

    public int MissingRequiredObjectCount { get; set; }

    public int RepairableSqlScriptCount { get; set; }

    public int NotRecordedSqlScriptCount { get; set; }

    public int SqlReviewCount { get; set; }

    public int RequiredDependencyIssueCount { get; set; }

    public int OptionalDependencyIssueCount { get; set; }

    public int IncompatibleArtifactReferenceCount { get; set; }

    public IReadOnlyList<string> Messages { get; set; } = [];
}

/// <summary>
/// Result from checking one declared module-definition dependency.
/// </summary>
public sealed class ModuleDefinitionDependencyCheckRow
{
    public string ModuleKey { get; set; } = string.Empty;

    public string? MinDefinitionVersion { get; set; }

    public string? MaxDefinitionVersion { get; set; }

    public bool IsRequired { get; set; }

    public string? AppliedDefinitionVersion { get; set; }

    public string Status { get; set; } = "neutral";

    public string StatusLabel { get; set; } = "Not checked";

    public string? Reason { get; set; }
}

/// <summary>
/// Existing artifact row used when duplicate content is detected during upload.
/// </summary>
public sealed class ArtifactDuplicateInfo
{
    public int ArtifactId { get; set; }

    public int AppId { get; set; }

    public string AppKey { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePath { get; set; }

    public string? Sha256 { get; set; }
}

/// <summary>
/// Result from selecting an artifact as the desired version for matching apps.
/// </summary>
public sealed class ArtifactApplicationResult
{
    public int TemplateAppRowsUpdated { get; set; }

    public int AppInstanceRowsUpdated { get; set; }

    public int WorkerInstanceRowsUpdated { get; set; }

    public int TotalRowsUpdated => TemplateAppRowsUpdated + AppInstanceRowsUpdated + WorkerInstanceRowsUpdated;
}

/// <summary>
/// Artifact option with owning app context for app-instance artifact selection.
/// </summary>
public sealed class ArtifactSelectionOption
{
    public string Value { get; set; } = string.Empty;

    public int AppId { get; set; }

    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Template context for a concrete app instance that is managed by HostAgent materialization.
/// Admins should change the template row, not the materialized runtime row.
/// </summary>
public sealed class TemplateManagedAppInstanceInfo
{
    public int InstanceTemplateAppInstanceId { get; set; }

    public int InstanceTemplateId { get; set; }

    public string InstanceTemplateKey { get; set; } = string.Empty;

    public string InstanceTemplateDisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Editable desired app-instance row stored on an instance template.
/// HostAgent copies these values to concrete app instances during materialization.
/// </summary>
public sealed class InstanceTemplateAppInstanceEditData
{
    public int InstanceTemplateAppInstanceId { get; set; }

    public int InstanceTemplateId { get; set; }

    public int InstanceTemplateModuleInstanceId { get; set; }

    public int? InstanceTemplateHostId { get; set; }

    public int AppId { get; set; }

    public string AppInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? RoutePath { get; set; }

    public string? PublicUrl { get; set; }

    public string? InstallPath { get; set; }

    public string? InstallationName { get; set; }

    public int? DesiredArtifactId { get; set; }

    public int? DesiredConfigId { get; set; }

    public string? ExpectedLogin { get; set; }

    public string? ExpectedClientHostName { get; set; }

    public string? ExpectedClientIp { get; set; }

    public bool IsEnabled { get; set; }

    public bool IsAllowed { get; set; }

    public byte DesiredState { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Editable host row stored on an instance template.
/// HostAgent materializes this into concrete host metadata when a matching host reports in.
/// </summary>
public sealed class InstanceTemplateHostEditData
{
    public int InstanceTemplateHostId { get; set; }

    public int InstanceTemplateId { get; set; }

    public int HostTemplateId { get; set; }

    public string HostKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Environment { get; set; }

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Editable module-instance row stored on an instance template.
/// Desired app rows attach to this template module slot.
/// </summary>
public sealed class InstanceTemplateModuleEditData
{
    public int InstanceTemplateModuleInstanceId { get; set; }

    public int InstanceTemplateId { get; set; }

    public int ModuleId { get; set; }

    public string ModuleInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Editable runtime/app-instance fields.
/// This is the manual fallback surface for app instances that are not managed by an instance template.
/// </summary>
public sealed class AppInstanceEditData
{
    public Guid AppInstanceId { get; set; }

    public Guid ModuleInstanceId { get; set; }

    public Guid? HostId { get; set; }

    public int AppId { get; set; }

    public string AppInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? RoutePath { get; set; }

    public string? PublicUrl { get; set; }

    public string? InstallPath { get; set; }

    public string? InstallationName { get; set; }

    public int? ArtifactId { get; set; }

    public int? ConfigId { get; set; }

    public string? ExpectedLogin { get; set; }

    public string? ExpectedClientHostName { get; set; }

    public string? ExpectedClientIp { get; set; }

    public bool IsEnabled { get; set; }

    public bool IsAllowed { get; set; }

    public byte DesiredState { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Minimal context for validating that a module instance belongs to the intended instance and module definition.
/// </summary>
public sealed class ModuleInstanceContext
{
    public Guid ModuleInstanceId { get; set; }

    public Guid InstanceId { get; set; }

    public int ModuleId { get; set; }

    public string InstanceKey { get; set; } = string.Empty;

    public string ModuleInstanceKey { get; set; } = string.Empty;
}

/// <summary>
/// Minimal host context used when validating host placement.
/// </summary>
public sealed class HostContext
{
    public Guid HostId { get; set; }

    public Guid InstanceId { get; set; }

    public string HostKey { get; set; } = string.Empty;
}

/// <summary>
/// Minimal app-definition context used when validating definition-to-instance consistency.
/// </summary>
public sealed class AppDefinitionContext
{
    public int AppId { get; set; }

    public int ModuleId { get; set; }

    public string AppKey { get; set; } = string.Empty;

    public string AppType { get; set; } = string.Empty;

    public bool AllowMultipleActiveInstances { get; set; }
}

/// <summary>
/// Minimal context for validating desired app rows on instance templates.
/// </summary>
public sealed class InstanceTemplateModuleContext
{
    public int InstanceTemplateModuleInstanceId { get; set; }

    public int InstanceTemplateId { get; set; }

    public int ModuleId { get; set; }
}

/// <summary>
/// Minimal context for validating desired host placement on instance templates.
/// </summary>
public sealed class InstanceTemplateHostContext
{
    public int InstanceTemplateHostId { get; set; }

    public int InstanceTemplateId { get; set; }
}

/// <summary>
/// Editable worker-runtime metadata stored per app definition.
/// </summary>
public sealed class AppWorkerDefinitionEditData
{
    public int AppId { get; set; }

    public string RuntimeKind { get; set; } = string.Empty;

    public string WorkerTypeKey { get; set; } = string.Empty;

    public string PluginRelativePath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }
}
