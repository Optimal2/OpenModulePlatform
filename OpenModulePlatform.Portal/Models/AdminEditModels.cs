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

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePath { get; set; }

    public string? Sha256 { get; set; }

    public bool IsEnabled { get; set; }
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
/// Existing artifact row used when duplicate content is detected during upload.
/// </summary>
public sealed class ArtifactDuplicateInfo
{
    public int ArtifactId { get; set; }

    public string AppKey { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }
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
