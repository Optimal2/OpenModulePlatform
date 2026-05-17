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
    /// Maximum accepted zip upload size in bytes.
    /// </summary>
    public long MaxUploadBytes { get; set; } = DefaultMaxUploadBytes;
}
