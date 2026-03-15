// File: OpenModulePlatform.Web.ExampleServiceAppModule/ViewModels/HostInstallationRow.cs
namespace OpenModulePlatform.Web.ExampleServiceAppModule.ViewModels;

public sealed class HostInstallationRow
{
    public Guid HostInstallationId { get; set; }
    public Guid HostId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string InstallationName { get; set; } = string.Empty;
    public DateTime? LastSeenUtc { get; set; }
    public string? LastLogin { get; set; }
    public string? LastClientHostName { get; set; }
    public string? LastClientIp { get; set; }
    public string ExpectedLogin { get; set; } = string.Empty;
    public string? ExpectedHostName { get; set; }
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
