namespace OpenModulePlatform.Portal.Models;

public sealed class HostConfigurationDocumentRow
{
    public int HostConfigurationDocumentId { get; init; }

    public string HostKey { get; init; } = string.Empty;

    public string ConfigurationVersion { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public string ConfigurationSha256 { get; init; } = string.Empty;

    public string? SourceName { get; init; }

    public bool IsActive { get; init; }

    public DateTime UpdatedUtc { get; init; }
}

public sealed class ConfigOverlayDocumentRow
{
    public int ConfigOverlayDocumentId { get; init; }

    public string OverlayKey { get; init; } = string.Empty;

    public string OverlayVersion { get; init; } = string.Empty;

    public string HostKey { get; init; } = string.Empty;

    public string? ModuleKey { get; init; }

    public string? ModuleDefinitionVersion { get; init; }

    public string? AppKey { get; init; }

    public string? PackageType { get; init; }

    public string? TargetName { get; init; }

    public string? ArtifactVersion { get; init; }

    public string OverlaySha256 { get; init; } = string.Empty;

    public string? SourceName { get; init; }

    public int ConfigurationFileCount { get; init; }

    public bool IsEnabled { get; init; }

    public DateTime UpdatedUtc { get; init; }
}
