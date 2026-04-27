// File: OpenModulePlatform.Web.ExampleWorkerAppModule/ViewModels/AppInstanceRow.cs
namespace OpenModulePlatform.Web.ExampleWorkerAppModule.ViewModels;

public sealed class AppInstanceRow
{
    public Guid AppInstanceId { get; set; }
    public Guid? HostId { get; set; }
    public string? HostKey { get; set; }
    public string AppInstanceKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? InstallationName { get; set; }
    public string? RoutePath { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string? LastLogin { get; set; }
    public string? LastClientHostName { get; set; }
    public string? LastClientIp { get; set; }
    public byte VerificationStatus { get; set; }
    public DateTime? LastVerifiedUtc { get; set; }
    public bool IsAllowed { get; set; }
    public byte DesiredState { get; set; }
    public int? ConfigId { get; set; }
    public int? ArtifactId { get; set; }
    public string? ArtifactVersion { get; set; }
    public string? ArtifactTargetName { get; set; }
    public string? WorkerTypeKey { get; set; }
    public string? PluginRelativePath { get; set; }
    public string? RuntimeKind { get; set; }
    public byte? ObservedState { get; set; }
    public int? ProcessId { get; set; }
    public DateTime? RuntimeStartedUtc { get; set; }
    public DateTime? RuntimeLastSeenUtc { get; set; }
    public DateTime? LastExitUtc { get; set; }
    public int? LastExitCode { get; set; }
    public string? StatusMessage { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
