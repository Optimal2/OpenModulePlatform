// File: OpenModulePlatform.Portal/Models/PortalModels.cs
using OpenModulePlatform.Web.Shared.Navigation;

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

    public string? ContextName { get; set; }

    public string? Description { get; set; }

    public string? LogoUrl { get; set; }

    public string? IconKey { get; set; }

    public string TargetHref { get; set; } = string.Empty;

    public string? TargetEntryKey { get; set; }

    public bool IsPinned { get; set; }

    public bool IsHidden { get; set; }

    public bool IsNavigationFavorite { get; set; }

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

    public string FullDisplayName => string.IsNullOrWhiteSpace(ContextName)
        ? DisplayName
        : $"{ContextName} / {DisplayName}";
}

/// <summary>
/// Portal Entry list rendered inside a dashboard widget.
/// </summary>
public sealed record DashboardPortalEntryWidgetList(
    IReadOnlyList<PortalEntry> Entries,
    bool IsFavoritesList,
    bool ShowSearch = false,
    string? SectionTitle = null);

/// <summary>
/// Combined favorite and general Portal Entry list rendered inside a dashboard widget.
/// </summary>
public sealed record DashboardPortalEntryComboWidgetList(
    IReadOnlyList<PortalEntry> FavoriteEntries,
    IReadOnlyList<PortalEntry> AllEntries);

/// <summary>
/// Portal navbar links rendered inside a dashboard widget.
/// </summary>
public sealed record DashboardNavbarWidgetList(IReadOnlyList<DashboardNavbarSection> Sections);

/// <summary>
/// One navbar section rendered inside the dashboard navbar widget.
/// </summary>
public sealed record DashboardNavbarSection(string TextKey, IReadOnlyList<DashboardNavbarLink> Links);

/// <summary>
/// One navbar destination rendered inside the dashboard navbar widget.
/// </summary>
public sealed record DashboardNavbarLink(string TextKey, string Href);

/// <summary>
/// Content pages rendered inside a dashboard widget.
/// </summary>
public sealed record DashboardContentPageWidgetList(IReadOnlyList<DashboardContentPageLink> Pages);

/// <summary>
/// Personal notifications rendered inside a dashboard widget.
/// </summary>
public sealed record DashboardNotificationFeedWidget(
    IReadOnlyList<PortalTopBarNotification> Notifications,
    string RecentUrl,
    string MarkReadUrl,
    bool CanUseNotifications);

/// <summary>
/// One readable Content page destination for the dashboard.
/// </summary>
public sealed record DashboardContentPageLink(
    Guid ContentId,
    string Title,
    string Slug,
    string Href,
    string? AppDisplayName,
    string ContentType);

/// <summary>
/// Role list rendered inside a dashboard widget.
/// </summary>
public sealed record DashboardRoleWidgetList(IReadOnlyList<DashboardRoleOption> Roles);

/// <summary>
/// One selectable user role rendered inside the dashboard role widget.
/// </summary>
public sealed record DashboardRoleOption(int RoleId, string Name, string? Description, bool IsActive);

/// <summary>
/// Per-user dashboard behavior preferences.
/// </summary>
public sealed record DashboardPreferences(bool AlignToGrid, bool ExpandedCanvas, bool HasCustomLayout);

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
/// Widget definition that can be placed on the Portal dashboard.
/// </summary>
public sealed class DashboardWidgetDefinition
{
    public int WidgetId { get; set; }

    public string WidgetKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string WidgetType { get; set; } = string.Empty;

    public string? Payload { get; set; }

    public string? ModuleKey { get; set; }

    public string? Author { get; set; }

    public DateTime ModifiedUtc { get; set; }
}

/// <summary>
/// A widget instance placed on one user's Portal dashboard.
/// </summary>
public sealed class DashboardActiveWidget
{
    public long UserActiveWidgetId { get; set; }

    public int WidgetId { get; set; }

    public string WidgetTitle { get; set; } = string.Empty;

    public string WidgetType { get; set; } = string.Empty;

    public string? Payload { get; set; }

    public int OffsetTop { get; set; }

    public int OffsetLeft { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int OrderPriority { get; set; }

    public string? Title { get; set; }

    public int? IntData { get; set; }

    public string? StringData { get; set; }

    public int ContentScale { get; set; }

    public bool HideTitlebarWhenViewing { get; set; }

    public string EffectiveTitle => string.IsNullOrWhiteSpace(Title) ? WidgetTitle : Title.Trim();
}

/// <summary>
/// Client-provided dashboard layout update for one active widget.
/// </summary>
public sealed class DashboardWidgetLayoutUpdate
{
    public long UserActiveWidgetId { get; set; }

    public int WidgetId { get; set; }

    public int OffsetTop { get; set; }

    public int OffsetLeft { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int OrderPriority { get; set; }

    public string? Title { get; set; }

    public int? IntData { get; set; }

    public string? StringData { get; set; }

    public int ContentScale { get; set; }

    public bool HideTitlebarWhenViewing { get; set; }
}

/// <summary>
/// Result returned after saving a dashboard layout with new draft widgets.
/// </summary>
public sealed record DashboardWidgetSaveResult(IReadOnlyList<DashboardWidgetSaveCreatedItem> CreatedWidgets);

/// <summary>
/// Server-assigned id for a dashboard widget that was created as a client draft.
/// </summary>
public sealed record DashboardWidgetSaveCreatedItem(long TemporaryUserActiveWidgetId, long UserActiveWidgetId);

/// <summary>
/// Admin-facing widget definition row including portable import/export metadata.
/// </summary>
public sealed class DashboardWidgetAdminRow
{
    public int WidgetId { get; set; }

    public string WidgetKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string WidgetType { get; set; } = string.Empty;

    public string? Payload { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string? Author { get; set; }

    public DateTime ModifiedUtc { get; set; }

    public List<string> PermissionNames { get; } = [];

    public List<string> RoleNames { get; } = [];
}

/// <summary>
/// Result from importing a portable dashboard widget document.
/// </summary>
public sealed class DashboardWidgetImportResult
{
    public int CreatedCount { get; set; }

    public int UpdatedCount { get; set; }

    public int PermissionRowCount { get; set; }
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

    public int? InstanceTemplateAppInstanceId { get; set; }

    public int? InstanceTemplateId { get; set; }

    public string InstanceKey { get; set; } = string.Empty;

    public string ModuleInstanceKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string AppInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? HostKey { get; set; }

    public string? TargetHostTemplateKey { get; set; }

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

    public string? TargetHostTemplateKey { get; set; }

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

    public string ModuleKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePath { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// Artifact row with enough identity information to export a portable artifact package.
/// </summary>
public sealed class ModuleArtifactPackageRow
{
    public int ArtifactId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePath { get; set; }

    public bool IsEnabled { get; set; }
}

/// <summary>
/// Lightweight artifact configuration file row for admin lists.
/// </summary>
public sealed class ArtifactConfigurationFileRow
{
    public int ArtifactConfigurationFileId { get; set; }

    public int ArtifactId { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Versioned module definition document shown in admin lists.
/// </summary>
public sealed class ModuleDefinitionDocumentRow
{
    public int ModuleDefinitionDocumentId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string DefinitionVersion { get; set; } = string.Empty;

    public int FormatVersion { get; set; }

    public string DefinitionSha256 { get; set; } = string.Empty;

    public string? DefinitionJson { get; set; }

    public string? SourceName { get; set; }

    public bool IsApplied { get; set; }

    public DateTime? AppliedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public int CompatibleArtifactSlotCount { get; set; }
}

/// <summary>
/// Artifact slot stored from a module definition document.
/// </summary>
public sealed class ModuleDefinitionCompatibilityRow
{
    public int ModuleDefinitionArtifactCompatibilityId { get; set; }

    public string AppKey { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePathTemplate { get; set; }

    public string? MinArtifactVersion { get; set; }

    public string? MaxArtifactVersion { get; set; }
}

/// <summary>
/// Current desired/runtime artifact reference checked before a module definition is applied.
/// </summary>
public sealed class ModuleDefinitionArtifactReferenceRow
{
    public string ReferenceKind { get; set; } = string.Empty;

    public string ReferenceKey { get; set; } = string.Empty;

    public int ArtifactId { get; set; }

    public string AppKey { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }
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

    public string? TargetHostTemplateKey { get; set; }

    public string AppKey { get; set; } = string.Empty;

    public string AppType { get; set; } = string.Empty;

    public string AppInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? RoutePath { get; set; }

    public string? InstallationName { get; set; }

    public int? ArtifactId { get; set; }

    public string? ArtifactVersion { get; set; }

    public int? LatestArtifactId { get; set; }

    public string? LatestArtifactVersion { get; set; }

    public bool HasNewerArtifact => LatestArtifactId.HasValue
        && !string.IsNullOrWhiteSpace(LatestArtifactVersion);

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

/// <summary>
/// Latest HostAgent app deployment state for a materialized app on a host.
/// </summary>
public sealed class HostAppDeploymentStateRow
{
    public Guid HostId { get; set; }

    public Guid AppInstanceId { get; set; }

    public string HostKey { get; set; } = string.Empty;

    public string AppInstanceKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ArtifactVersion { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public byte DeploymentState { get; set; }

    public string? TargetPath { get; set; }

    public string? RuntimeName { get; set; }

    public DateTime? LastCheckedUtc { get; set; }

    public DateTime? LastAppliedUtc { get; set; }

    public string? LastError { get; set; }

    public string? CredentialAutomationMode { get; set; }

    public string? DesiredRuntimeIdentity { get; set; }

    public string? ActualRuntimeIdentity { get; set; }

    public string? IdentityCheckStatus { get; set; }

    public DateTime? IdentityRepairRequestedUtc { get; set; }

    public string? IdentityRepairRequestedBy { get; set; }

    public bool CanRequestIdentityRepair =>
        string.Equals(PackageType, "service-app", StringComparison.OrdinalIgnoreCase)
        && string.Equals(CredentialAutomationMode, "PortalAdminApproved", StringComparison.OrdinalIgnoreCase)
        && !IdentityRepairRequestedUtc.HasValue
        && string.Equals(IdentityCheckStatus, "WaitingForPortalAdminApproval", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Latest HostAgent artifact provisioning state for a host-local artifact cache entry.
/// </summary>
public sealed class HostArtifactStateRow
{
    public string HostKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string ArtifactVersion { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public byte ProvisioningState { get; set; }

    public string? LocalPath { get; set; }

    public DateTime? LastCheckedUtc { get; set; }

    public DateTime? LastProvisionedUtc { get; set; }

    public string? LastError { get; set; }

    public bool IsCurrentlyDesired { get; set; }
}

/// <summary>
/// HostAgent self-upgrade state for one host.
/// </summary>
public sealed class HostAgentUpgradeRow
{
    public Guid HostId { get; set; }

    public string HostKey { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public int? DesiredArtifactId { get; set; }

    public string? DesiredVersion { get; set; }

    public string? DesiredRelativePath { get; set; }

    public string? ServiceNamePrefix { get; set; }

    public string? InstallRoot { get; set; }

    public bool DesiredIsEnabled { get; set; }

    public DateTime? DesiredUpdatedUtc { get; set; }

    public string? CurrentServiceName { get; set; }

    public string? CurrentVersion { get; set; }

    public string? CurrentInstallPath { get; set; }

    public string? RuntimeMode { get; set; }

    public bool CurrentIsActive { get; set; }

    public string? TakeoverFromServiceName { get; set; }

    public DateTime? RuntimeLastSeenUtc { get; set; }

    public string? RuntimeStatusMessage { get; set; }
}

/// <summary>
/// HostAgent artifact option that can be selected as desired runtime version.
/// </summary>
public sealed class HostAgentArtifactOption
{
    public int ArtifactId { get; set; }

    public string Version { get; set; } = string.Empty;

    public string? RelativePath { get; set; }

    public DateTime CreatedUtc { get; set; }
}
