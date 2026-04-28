namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class HostAgentRpcSettings
{
    public bool Enabled { get; set; }

    public string PipeName { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 60;

    public string ResolvePipeName(string hostKey)
    {
        return string.IsNullOrWhiteSpace(PipeName)
            ? $"OpenModulePlatform.HostAgent.{hostKey}"
            : PipeName.Trim();
    }

    public void Validate()
    {
        if (TimeoutSeconds < 1)
        {
            throw new InvalidOperationException("WorkerManager:HostAgentRpc:TimeoutSeconds must be at least 1.");
        }
    }
}
