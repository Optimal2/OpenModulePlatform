// File: OpenModulePlatform.Portal/Options/ArtifactUploadOptions.cs
namespace OpenModulePlatform.Portal.Options;

/// <summary>
/// Controls the Portal admin upload surface for immutable HostAgent artifacts.
/// </summary>
public sealed class ArtifactUploadOptions
{
    public const string SectionName = "ArtifactUpload";

    public const long DefaultMaxUploadBytes = 512L * 1024L * 1024L;

    /// <summary>
    /// Central artifact repository root. This should normally match HostAgent:CentralArtifactRoot.
    /// </summary>
    public string ArtifactStoreRoot { get; set; } = string.Empty;

    /// <summary>
    /// Optional folder containing module definition JSON files available for later Portal import.
    /// </summary>
    public string AvailableModuleDefinitionsRoot { get; set; } = string.Empty;

    /// <summary>
    /// Optional folder containing standard artifact package zip files available for later Portal import.
    /// </summary>
    public string AvailableArtifactsRoot { get; set; } = string.Empty;

    /// <summary>
    /// Optional folder containing host configuration JSON files available for later Portal import.
    /// </summary>
    public string AvailableHostConfigurationsRoot { get; set; } = string.Empty;

    /// <summary>
    /// Optional folder containing host-specific config overlay JSON files or zip packages.
    /// </summary>
    public string AvailableConfigOverlaysRoot { get; set; } = string.Empty;

    /// <summary>
    /// Optional writable temp folder used for ASP.NET Core multipart upload buffering.
    /// </summary>
    public string TempRoot { get; set; } = string.Empty;

    /// <summary>
    /// Maximum accepted zip upload size in bytes.
    /// </summary>
    public long MaxUploadBytes { get; set; } = DefaultMaxUploadBytes;
}
