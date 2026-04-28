namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class HostAgentRpcResponse
{
    public bool Success { get; set; }

    public byte State { get; set; }

    public string? LocalPath { get; set; }

    public string? ContentSha256 { get; set; }

    public string? ErrorMessage { get; set; }

    public static HostAgentRpcResponse FromProvisioningResult(ArtifactProvisioningResult result)
    {
        return new HostAgentRpcResponse
        {
            Success = result.IsSuccess,
            State = result.State,
            LocalPath = result.LocalPath,
            ContentSha256 = result.ContentHash,
            ErrorMessage = result.ErrorMessage
        };
    }

    public static HostAgentRpcResponse Failed(string message)
    {
        return new HostAgentRpcResponse
        {
            Success = false,
            State = ArtifactProvisioningState.Failed,
            ErrorMessage = message
        };
    }
}
