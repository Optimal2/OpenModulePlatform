namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class HostAgentRpcRequest
{
    public string Operation { get; set; } = string.Empty;

    public int ArtifactId { get; set; }

    public string? DesiredLocalPath { get; set; }
}
