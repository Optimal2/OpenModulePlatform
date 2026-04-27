// File: OpenModulePlatform.Web.ExampleServiceAppModule/ViewModels/AppInstanceRow.cs
namespace OpenModulePlatform.Web.ExampleServiceAppModule.ViewModels;

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
    public string? ExpectedLogin { get; set; }
    public string? ExpectedClientHostName { get; set; }
    public string? ExpectedClientIp { get; set; }
    public byte VerificationStatus { get; set; }
    public DateTime? LastVerifiedUtc { get; set; }
    public bool IsAllowed { get; set; }
    public byte DesiredState { get; set; }
    public int? ConfigId { get; set; }
    public int? ArtifactId { get; set; }
    public string? ArtifactVersion { get; set; }
    public string? ArtifactTargetName { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
