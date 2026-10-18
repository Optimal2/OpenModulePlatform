namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class ArtifactConfigurationFileDescriptor
{
    public int ArtifactConfigurationFileId { get; init; }

    public int ArtifactId { get; init; }

    public string RelativePath { get; init; } = string.Empty;

    public string FileContent { get; init; } = string.Empty;

    /// <summary>
    /// True when this resolved file stands alone because a config overlay supplied it and the
    /// artifact owns no file at the same relative path. HostAgent records such files in the
    /// applied-overlay-file inventory and, on the already-applied fast path, reconciles them
    /// (removes them) when the overlay stops supplying them. A path the artifact owns -- or the
    /// built-in appsettings.json, which <see cref="Services.ArtifactConfigurationFileWriter"/>
    /// rebuilds without this flag -- is never overlay-owned and never reconciled for removal.
    /// </summary>
    public bool IsOverlayOwnedWholeFile { get; init; }
}
