using System.Reflection;

namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class HostAgentProcessContext
{
    private readonly object _lock = new();

    private string _runtimeMode;

    private string? _rpcPipeName;

    public HostAgentProcessContext(
        string serviceName,
        string version,
        string runtimeMode,
        string? takeoverFromServiceName)
    {
        ServiceName = Clean(serviceName, "OMP.HostAgent");
        Version = Clean(version, ResolveAssemblyVersion());
        _runtimeMode = NormalizeMode(runtimeMode);
        TakeoverFromServiceName = string.IsNullOrWhiteSpace(takeoverFromServiceName)
            ? null
            : takeoverFromServiceName.Trim();
        ProcessId = Environment.ProcessId;
        StartedUtc = DateTimeOffset.UtcNow;
    }

    public string ServiceName { get; }

    public string Version { get; }

    public string? TakeoverFromServiceName { get; }

    public int ProcessId { get; }

    public DateTimeOffset StartedUtc { get; }

    public string RuntimeMode
    {
        get
        {
            lock (_lock)
            {
                return _runtimeMode;
            }
        }
    }

    /// <summary>
    /// The named pipe this process currently serves HostAgent RPC on, or null while RPC is
    /// disabled or not started. Published to <c>omp.HostAgentRuntimeStates.RpcPipeName</c>.
    /// </summary>
    public string? RpcPipeName
    {
        get
        {
            lock (_lock)
            {
                return _rpcPipeName;
            }
        }
    }

    public bool IsQuiesceRequested { get; private set; }

    public void SetRpcPipeName(string? pipeName)
    {
        lock (_lock)
        {
            _rpcPipeName = string.IsNullOrWhiteSpace(pipeName) ? null : pipeName.Trim();
        }
    }

    public void RequestQuiesce()
    {
        lock (_lock)
        {
            IsQuiesceRequested = true;
            _runtimeMode = HostAgentRuntimeMode.Quiescing;
        }
    }

    public void MarkQuiesced()
    {
        lock (_lock)
        {
            IsQuiesceRequested = true;
            _runtimeMode = HostAgentRuntimeMode.Quiesced;
        }
    }

    public void MarkNormal()
    {
        lock (_lock)
        {
            IsQuiesceRequested = false;
            _runtimeMode = HostAgentRuntimeMode.Normal;
        }
    }

    public void MarkFailed()
    {
        lock (_lock)
        {
            _runtimeMode = HostAgentRuntimeMode.Failed;
        }
    }

    private static string NormalizeMode(string? value)
    {
        var mode = string.IsNullOrWhiteSpace(value)
            ? HostAgentRuntimeMode.Normal
            : value.Trim();

        return mode.Equals(HostAgentRuntimeMode.Takeover, StringComparison.OrdinalIgnoreCase)
            ? HostAgentRuntimeMode.Takeover
            : HostAgentRuntimeMode.Normal;
    }

    private static string Clean(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string ResolveAssemblyVersion()
    {
        var assembly = typeof(HostAgentProcessContext).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Trim();
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
