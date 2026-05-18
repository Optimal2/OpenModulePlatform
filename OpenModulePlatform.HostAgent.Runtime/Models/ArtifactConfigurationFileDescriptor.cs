namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class ArtifactConfigurationFileDescriptor
{
    public int ArtifactConfigurationFileId { get; init; }

    public int ArtifactId { get; init; }

    public string RelativePath { get; init; } = string.Empty;

    public string FileContent { get; init; } = string.Empty;
}
