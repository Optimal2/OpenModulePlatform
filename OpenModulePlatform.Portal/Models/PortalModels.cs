// File: OpenModulePlatform.Portal/Models/PortalModels.cs
namespace OpenModulePlatform.Portal.Models;

public sealed class PortalAppEntry
{
    public Guid AppInstanceId { get; set; }
    public string AppInstanceKey { get; set; } = string.Empty;
    public string AppKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? RoutePath { get; set; }
    public string? PublicUrl { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool RequireAll { get; set; }
    public List<string> RequiredPermissions { get; } = [];
}

public sealed class OverviewMetrics
{
    public int InstanceCount { get; set; }
    public int ModuleCount { get; set; }
    public int ModuleInstanceCount { get; set; }
    public int AppCount { get; set; }
    public int AppInstanceCount { get; set; }
    public int ArtifactCount { get; set; }
    public int HostCount { get; set; }
    public int InstanceTemplateCount { get; set; }
    public int HostTemplateCount { get; set; }
    public int HostDeploymentAssignmentCount { get; set; }
    public int HostDeploymentCount { get; set; }
}

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

public sealed class HostRow
{
    public Guid HostId { get; set; }
    public string InstanceKey { get; set; } = string.Empty;
    public string HostKey { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Environment { get; set; }
    public string? OsFamily { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime? LastSeenUtc { get; set; }
}

public sealed class InstanceTemplateRow
{
    public int InstanceTemplateId { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
}

public sealed class HostTemplateRow
{
    public int HostTemplateId { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
}

public sealed class HostDeploymentAssignmentRow
{
    public long HostDeploymentAssignmentId { get; set; }
    public string HostKey { get; set; } = string.Empty;
    public string HostTemplateKey { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime AssignedUtc { get; set; }
}

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
