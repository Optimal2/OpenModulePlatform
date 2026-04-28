using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.WorkerManager.WindowsService.Models;

namespace OpenModulePlatform.WorkerManager.WindowsService.Services;

public sealed class HostAgentRpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IOptionsMonitor<WorkerManagerSettings> _settings;
    private readonly ILogger<HostAgentRpcClient> _logger;

    public HostAgentRpcClient(
        IOptionsMonitor<WorkerManagerSettings> settings,
        ILogger<HostAgentRpcClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<HostAgentEnsureArtifactResponse?> EnsureArtifactAsync(
        int artifactId,
        string? desiredLocalPath,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        settings.HostAgentRpc.Validate();

        if (!settings.HostAgentRpc.Enabled)
        {
            return null;
        }

        var hostKey = settings.ResolveHostKey();
        var pipeName = settings.HostAgentRpc.ResolvePipeName(hostKey);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.HostAgentRpc.TimeoutSeconds));

        try
        {
            await using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeoutCts.Token);

            var request = new
            {
                operation = "ensureArtifact",
                artifactId,
                desiredLocalPath
            };

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), timeoutCts.Token);
            var responseJson = await reader.ReadLineAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return new HostAgentEnsureArtifactResponse
                {
                    Success = false,
                    ErrorMessage = "HostAgent returned an empty response."
                };
            }

            return JsonSerializer.Deserialize<HostAgentEnsureArtifactResponse>(responseJson, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "HostAgent ensureArtifact RPC failed. ArtifactId={ArtifactId}, PipeName={PipeName}",
                artifactId,
                pipeName);

            return new HostAgentEnsureArtifactResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
