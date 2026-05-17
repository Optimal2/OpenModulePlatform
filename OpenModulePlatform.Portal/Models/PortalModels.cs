// File: OpenModulePlatform.Portal/Models/PortalModels.cs
namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Catalog entry shown on the Portal start page.
/// </summary>
public sealed class PortalAppEntry
{
    public Guid AppInstanceId { get; set; }

    public string AppInstanceKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? RoutePath { get; set; }

    public string? PublicUrl { get; set; }

    public string? HostKey { get; set; }

    public string? HostBaseUrl { get; set; }

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public bool RequireAll { get; set; }

    public List<string> RequiredPermissions { get; } = [];
}

/// <summary>
/// Visual entry rendered on the Portal start page.
/// </summary>
public sealed class PortalEntry
{
    public int PortalEntryId { get; set; }

    public int? ParentEntryId { get; set; }

    public string EntryKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? LogoUrl { get; set; }

    public string? IconKey { get; set; }

    public string TargetHref { get; set; } = string.Empty;

    public string? TargetEntryKey { get; set; }

    public bool IsPinned { get; set; }

    public bool IsHidden { get; set; }

    public int? UserSortOrder { get; set; }

    public int DefaultSortOrder { get; set; }

    public string LogoFallbackText
    {
        get
        {
            var trimmed = DisplayName.Trim();
            return string.IsNullOrWhiteSpace(trimmed)
                ? "?"
                : trimmed[..1].ToUpperInvariant();
        }
    }
}

/// <summary>
/// Admin list row for Portal Entries.
/// </summary>
public sealed class PortalEntryAdminRow
{
    public int PortalEntryId { get; set; }

    public int? ParentEntryId { get; set; }

    public string? ParentDisplayName { get; set; }

    public string EntryKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? LogoUrl { get; set; }

    public string? IconKey { get; set; }

    public string? TargetUrl { get; set; }

    public string? TargetEntryKey { get; set; }

    public Guid? SourceAppInstanceId { get; set; }

    public bool IsEnabled { get; set; }

    public int DefaultSortOrder { get; set; }
}

/// <summary>
/// Editable fields for creating a Portal Entry from the admin UI.
/// </summary>
public sealed class PortalEntryCreateData
{
    public int? ParentEntryId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? LogoUrl { get; set; }

    public string? IconKey { get; set; }

    public string? TargetUrl { get; set; }

    public string? TargetEntryKey { get; set; }

    public bool IsEnabled { get; set; }

    public int DefaultSortOrder { get; set; }
}

/// <summary>
/// Editable fields for updating a Portal Entry from the admin UI.
/// </summary>
public sealed class PortalEntryEditData
{
    public int PortalEntryId { get; set; }

    public int? ParentEntryId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? LogoUrl { get; set; }

    public string? IconKey { get; set; }

    public string? TargetUrl { get; set; }

    public string? TargetEntryKey { get; set; }

    public bool IsEnabled { get; set; }

    public int DefaultSortOrder { get; set; }
}

/// <summary>
/// Minimal layout update for the topbar Portal Entry tree.
/// </summary>
public sealed class PortalEntryLayoutUpdate
{
    public int PortalEntryId { get; set; }

    public int? ParentEntryId { get; set; }

    public bool IsEnabled { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Portal overview counts for the main admin landing page.
/// </summary>
public sealed class OverviewMetrics
{
    public int InstanceCount { get; set; }

    public int ModuleCount { get; set; }

    public int ModuleInstanceCount { get; set; }

    public int AppCount { get; set; }

    public int AppInstanceCount { get; set; }

    public int AppWorkerDefinitionCount { get; set; }

    public int AppInstanceRuntimeStateCount { get; set; }

    public int ArtifactCount { get; set; }

    public int HostCount { get; set; }

    public int InstanceTemplateCount { get; set; }

    public int HostTemplateCount { get; set; }

    public int HostDeploymentAssignmentCount { get; set; }

    public int HostDeploymentCount { get; set; }
}

/// <summary>
/// Lightweight instance row for admin lists.
/// </summary>
public sealed class InstanceRow
{
    public Guid InstanceId { get; set; }

    public string InstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? InstanceTemplateKey { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Lightweight module definition row for admin lists.
/// </summary>
public sealed class ModuleRow
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
/// Lightweight module instance row for admin lists.
/// </summary>
public sealed class ModuleInstanceRow
{
    public Guid ModuleInstanceId { get; set; }

    public string InstanceKey { get; set; } = string.Empty;

    public string ModuleKey { get; set; } = string.Empty;

    public string ModuleInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Lightweight app definition row for admin lists.
/// </summary>
public sealed class AppRow
{
    public int AppId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AppType { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsEnabled { get; set; }

    public int SortOrder { get; set; }
}


/// <summary>
/// Worker-plugin runtime metadata configured per app definition.
/// </summary>
public sealed class AppWorkerDefinitionRow
{
    public int AppId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AppType { get; set; } = string.Empty;

    public string RuntimeKind { get; set; } = string.Empty;

    public string WorkerTypeKey { get; set; } = string.Empty;

    public string PluginRelativePath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Runtime-observation row for app instances handled by the worker runtime track.
/// </summary>
public sealed class AppWorkerRuntimeRow
{
    public Guid AppInstanceId { get; set; }

    public string InstanceKey { get; set; } = string.Empty;

    public string ModuleInstanceKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string AppInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? HostKey { get; set; }

    public bool IsAllowed { get; set; }

    public byte DesiredState { get; set; }

    public string RuntimeKind { get; set; } = string.Empty;

    public string WorkerTypeKey { get; set; } = string.Empty;

    public string PluginRelativePath { get; set; } = string.Empty;

    public byte ObservedState { get; set; }

    public int? ProcessId { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? LastSeenUtc { get; set; }

    public DateTime? LastExitUtc { get; set; }

    public int? LastExitCode { get; set; }

    public string? StatusMessage { get; set; }
}

/// <summary>
/// Lightweight app instance row for admin lists.
/// </summary>
public sealed class AppInstanceRow
{
    public Guid AppInstanceId { get; set; }

    public string InstanceKey { get; set; } = string.Empty;

    public string ModuleInstanceKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string AppInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AppType { get; set; } = string.Empty;

    public string? HostKey { get; set; }

    public string? RoutePath { get; set; }

    public string? InstallationName { get; set; }

    public int? ArtifactId { get; set; }

    public string? ArtifactVersion { get; set; }

    public bool IsAllowed { get; set; }

    public byte DesiredState { get; set; }

    public DateTime? LastSeenUtc { get; set; }

    public byte VerificationStatus { get; set; }
}

/// <summary>
/// Lightweight artifact row for admin lists.
/// </summary>
public sealed class ArtifactRow
{
    public int ArtifactId { get; set; }

    public string AppKey { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePath { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// Lightweight host row for admin lists.
/// </summary>
public sealed class HostRow
{
    public Guid HostId { get; set; }

    public string InstanceKey { get; set; } = string.Empty;

    public string HostKey { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string? BaseUrl { get; set; }

    public string? Environment { get; set; }

    public string? OsFamily { get; set; }

    public string? OsVersion { get; set; }

    public string? Architecture { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime? LastSeenUtc { get; set; }
}

/// <summary>
/// Lightweight instance-template row for admin lists.
/// </summary>
public sealed class InstanceTemplateRow
{
    public int InstanceTemplateId { get; set; }

    public string TemplateKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Host placeholder declared by an instance template.
/// HostAgent materialization maps this desired host key to a concrete host row in each instance.
/// </summary>
public sealed class InstanceTemplateHostTopologyRow
{
    public int InstanceTemplateHostId { get; set; }

    public string HostKey { get; set; } = string.Empty;

    public string HostTemplateKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Environment { get; set; }

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Module instance declared by an instance template.
/// </summary>
public sealed class InstanceTemplateModuleTopologyRow
{
    public int InstanceTemplateModuleInstanceId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string ModuleInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Desired app instance declared by an instance template.
/// This is the admin-facing source of truth that HostAgent materializes into concrete app instances.
/// </summary>
public sealed class InstanceTemplateAppTopologyRow
{
    public int InstanceTemplateAppInstanceId { get; set; }

    public string ModuleInstanceKey { get; set; } = string.Empty;

    public string? HostKey { get; set; }

    public string AppKey { get; set; } = string.Empty;

    public string AppInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? RoutePath { get; set; }

    public string? InstallationName { get; set; }

    public string? ArtifactVersion { get; set; }

    public bool IsEnabled { get; set; }

    public bool IsAllowed { get; set; }

    public byte DesiredState { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Lightweight host-template row for admin lists.
/// </summary>
public sealed class HostTemplateRow
{
    public int HostTemplateId { get; set; }

    public string TemplateKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Lightweight deployment-assignment row for admin lists.
/// </summary>
public sealed class HostDeploymentAssignmentRow
{
    public long HostDeploymentAssignmentId { get; set; }

    public string HostKey { get; set; } = string.Empty;

    public string HostTemplateKey { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public string? AssignedBy { get; set; }

    public DateTime AssignedUtc { get; set; }
}

/// <summary>
/// Lightweight host-deployment row for admin lists.
/// </summary>
public sealed class HostDeploymentRow
{
    public long HostDeploymentId { get; set; }

    public string HostKey { get; set; } = string.Empty;

    public string? HostTemplateKey { get; set; }

    public byte Status { get; set; }

    public string? RequestedBy { get; set; }

    public DateTime RequestedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? OutcomeMessage { get; set; }
}
