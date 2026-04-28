namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class ArtifactProvisioningResult
{
    public byte State { get; init; }

    public string LocalPath { get; init; } = string.Empty;

    public string? ContentHash { get; init; }

    public string? ErrorMessage { get; init; }

    public bool IsSuccess => State == ArtifactProvisioningState.Succeeded;

    public static ArtifactProvisioningResult Succeeded(string localPath, string? contentHash)
    {
        return new ArtifactProvisioningResult
        {
            State = ArtifactProvisioningState.Succeeded,
            LocalPath = localPath,
            ContentHash = contentHash
        };
    }

    public static ArtifactProvisioningResult Failed(byte state, string localPath, string message)
    {
        return new ArtifactProvisioningResult
        {
            State = state,
            LocalPath = localPath,
            ErrorMessage = message
        };
    }
}
