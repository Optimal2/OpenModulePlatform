using System.Data.Common;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.WindowsService.Services;

public sealed class HostAgentRpcHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly HostAgentEngine _engine;
    private readonly ILogger<HostAgentRpcHostedService> _logger;

    public HostAgentRpcHostedService(
        IOptionsMonitor<HostAgentSettings> settings,
        HostAgentEngine engine,
        ILogger<HostAgentRpcHostedService> logger)
    {
        _settings = settings;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.EnableRpc)
        {
            _logger.LogInformation("HostAgent RPC is disabled.");
            return;
        }

        var pipeName = settings.ResolveRpcPipeName();
        _logger.LogInformation("HostAgent RPC named pipe started. PipeName={PipeName}", pipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 8,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(pipe, stoppingToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (IOException ex)
            {
                await DisposePipeAfterAcceptFailureAsync(pipe, ex);
            }
            catch (ObjectDisposedException ex)
            {
                await DisposePipeAfterAcceptFailureAsync(pipe, ex);
            }
            catch (InvalidOperationException ex)
            {
                await DisposePipeAfterAcceptFailureAsync(pipe, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                await DisposePipeAfterAcceptFailureAsync(pipe, ex);
            }
        }
    }

    private async Task DisposePipeAfterAcceptFailureAsync(NamedPipeServerStream pipe, Exception exception)
    {
        await pipe.DisposeAsync();
        _logger.LogError(exception, "HostAgent RPC accept loop failed.");
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken serviceCancellationToken)
    {
        await using var ownedPipe = pipe;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(serviceCancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.CurrentValue.RpcRequestTimeoutSeconds)));

        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

            var requestJson = await reader.ReadLineAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(requestJson))
            {
                await WriteResponseAsync(writer, HostAgentRpcResponse.Failed("Empty RPC request."), timeoutCts.Token);
                return;
            }

            var request = JsonSerializer.Deserialize<HostAgentRpcRequest>(requestJson, JsonOptions);
            if (request is null)
            {
                await WriteResponseAsync(writer, HostAgentRpcResponse.Failed("Invalid RPC request JSON."), timeoutCts.Token);
                return;
            }

            var response = await ExecuteRequestAsync(request, timeoutCts.Token);
            await WriteResponseAsync(writer, response, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!serviceCancellationToken.IsCancellationRequested)
        {
            LogRequestFailure(ex);
        }
        catch (IOException ex)
        {
            LogRequestFailure(ex);
        }
        catch (DbException ex)
        {
            LogRequestFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogRequestFailure(ex);
        }
        catch (JsonException ex)
        {
            LogRequestFailure(ex);
        }
        catch (InvalidOperationException ex)
        {
            LogRequestFailure(ex);
        }
    }

    private void LogRequestFailure(Exception exception)
    {
        _logger.LogError(exception, "HostAgent RPC request failed.");
    }

    private async Task<HostAgentRpcResponse> ExecuteRequestAsync(
        HostAgentRpcRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Operation, "ensureArtifact", StringComparison.OrdinalIgnoreCase))
        {
            return HostAgentRpcResponse.Failed($"Unsupported HostAgent RPC operation '{request.Operation}'.");
        }

        if (request.ArtifactId <= 0)
        {
            return HostAgentRpcResponse.Failed("ArtifactId must be greater than zero.");
        }

        var result = await _engine.EnsureArtifactByIdAsync(
            request.ArtifactId,
            request.DesiredLocalPath,
            cancellationToken);

        return HostAgentRpcResponse.FromProvisioningResult(result);
    }

    private static async Task WriteResponseAsync(
        StreamWriter writer,
        HostAgentRpcResponse response,
        CancellationToken cancellationToken)
    {
        var responseJson = JsonSerializer.Serialize(response, JsonOptions);
        await writer.WriteLineAsync(responseJson.AsMemory(), cancellationToken);
    }
}
