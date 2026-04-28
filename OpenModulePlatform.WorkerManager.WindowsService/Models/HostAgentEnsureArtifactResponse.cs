namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class HostAgentEnsureArtifactResponse
{
    public bool Success { get; set; }

    public byte State { get; set; }

    public string? LocalPath { get; set; }

    public string? ContentSha256 { get; set; }

    public string? ErrorMessage { get; set; }
}
