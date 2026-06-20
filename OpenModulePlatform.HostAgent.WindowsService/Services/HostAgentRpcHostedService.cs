using System.Data.Common;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.WindowsService.Services;

[SupportedOSPlatform("windows")]
public sealed class HostAgentRpcHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxRequestLineLength = 32 * 1024;
    private static readonly TimeSpan TakeoverRpcDelay = TimeSpan.FromSeconds(1);

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly HostAgentEngine _engine;
    private readonly HostAgentProcessContext _process;
    private readonly ILogger<HostAgentRpcHostedService> _logger;

    public HostAgentRpcHostedService(
        IOptionsMonitor<HostAgentSettings> settings,
        HostAgentEngine engine,
        HostAgentProcessContext process,
        ILogger<HostAgentRpcHostedService> logger)
    {
        _settings = settings;
        _engine = engine;
        _process = process;
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

        if (_process.RuntimeMode.Equals(HostAgentRuntimeMode.Takeover, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "HostAgent RPC startup is delayed until takeover completes. ServiceName={ServiceName}",
                _process.ServiceName);
        }

        while (!stoppingToken.IsCancellationRequested
               && _process.RuntimeMode.Equals(HostAgentRuntimeMode.Takeover, StringComparison.OrdinalIgnoreCase)
               && !_process.IsQuiesceRequested)
        {
            await Task.Delay(TakeoverRpcDelay, stoppingToken);
        }

        if (stoppingToken.IsCancellationRequested || _process.IsQuiesceRequested)
        {
            return;
        }

        var pipeName = settings.ResolveRpcPipeName();
        var pipeSecurity = CreatePipeSecurity(settings);
        _logger.LogInformation("HostAgent RPC named pipe started. PipeName={PipeName}", pipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 8,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity);
                await pipe.WaitForConnectionAsync(stoppingToken);
                var callerName = GetClientUserName(pipe);
                _logger.LogInformation(
                    "HostAgent RPC client connected. PipeName={PipeName}, Caller={Caller}",
                    pipeName,
                    callerName ?? "unknown");
                var connectedPipe = pipe;
                _ = Task.Run(() => HandleClientAsync(connectedPipe, pipeName, callerName, stoppingToken), CancellationToken.None);
                pipe = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                await DelayAfterAcceptFailureAsync(ex, stoppingToken);
            }
            catch (ObjectDisposedException ex)
            {
                await DelayAfterAcceptFailureAsync(ex, stoppingToken);
            }
            catch (InvalidOperationException ex)
            {
                await DelayAfterAcceptFailureAsync(ex, stoppingToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                await DelayAfterAcceptFailureAsync(ex, stoppingToken);
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync();
                }
            }
        }
    }

    private async Task DelayAfterAcceptFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "HostAgent RPC accept loop failed.");
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        string pipeName,
        string? callerName,
        CancellationToken serviceCancellationToken)
    {
        await using var ownedPipe = pipe;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(serviceCancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.CurrentValue.RpcRequestTimeoutSeconds)));
        string? requestedBy = null;

        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

            var requestJson = await ReadRequestLineAsync(reader, timeoutCts.Token);
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

            requestedBy = NormalizeRequestedBy(request.RequestedBy);
            _logger.LogInformation(
                "HostAgent RPC request received. PipeName={PipeName}, Operation={Operation}, Caller={Caller}, RequestedBy={RequestedBy}",
                pipeName,
                request.Operation,
                callerName ?? "unknown",
                requestedBy ?? "unknown");

            var response = await ExecuteRequestAsync(request, timeoutCts.Token);
            await WriteResponseAsync(writer, response, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!serviceCancellationToken.IsCancellationRequested)
        {
            LogRequestFailure(ex, callerName, requestedBy);
        }
        catch (IOException ex)
        {
            LogRequestFailure(ex, callerName, requestedBy);
        }
        catch (DbException ex)
        {
            LogRequestFailure(ex, callerName, requestedBy);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogRequestFailure(ex, callerName, requestedBy);
        }
        catch (JsonException ex)
        {
            LogRequestFailure(ex, callerName, requestedBy);
        }
        catch (InvalidOperationException ex)
        {
            LogRequestFailure(ex, callerName, requestedBy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRequestFailure(ex, callerName, requestedBy);
        }
    }

    private void LogRequestFailure(Exception exception, string? callerName, string? requestedBy)
    {
        _logger.LogError(
            exception,
            "HostAgent RPC request failed. Caller={Caller}, RequestedBy={RequestedBy}",
            callerName ?? "unknown",
            requestedBy ?? "unknown");
    }

    private PipeSecurity CreatePipeSecurity(HostAgentSettings settings)
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in GetDefaultRpcClientSids())
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }

        foreach (var accountName in GetConfiguredRpcClientAccounts(settings))
        {
            try
            {
                var sid = (SecurityIdentifier)new NTAccount(accountName).Translate(typeof(SecurityIdentifier));
                pipeSecurity.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            }
            catch (IdentityNotMappedException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Skipped unresolved HostAgent RPC client account. AccountName={AccountName}",
                    accountName);
            }
        }

        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            pipeSecurity.AddAccessRule(
                new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
        }

        return pipeSecurity;
    }

    private static string? GetClientUserName(NamedPipeServerStream pipe)
    {
        try
        {
            return pipe.GetImpersonationUserName();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Client identity is diagnostic only; authorization is enforced by the pipe ACL.
            return null;
        }
    }

    private static IEnumerable<string> GetConfiguredRpcClientAccounts(HostAgentSettings settings)
    {
        return settings.RpcAllowedClientAccounts
            .Concat(settings.RpcAllowedClientServiceNames
                .Select(static serviceName => serviceName.Trim())
                .Where(static serviceName => !string.IsNullOrWhiteSpace(serviceName))
                .Select(static serviceName => $@"NT SERVICE\{serviceName}"))
            .Select(static accountName => accountName.Trim())
            .Where(static accountName => !string.IsNullOrWhiteSpace(accountName))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? NormalizeRequestedBy(string? requestedBy)
    {
        return string.IsNullOrWhiteSpace(requestedBy)
            ? null
            : requestedBy.Trim();
    }

    private static IEnumerable<SecurityIdentifier> GetDefaultRpcClientSids()
    {
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
    }

    private async Task<HostAgentRpcResponse> ExecuteRequestAsync(
        HostAgentRpcRequest request,
        CancellationToken cancellationToken)
    {
        if (string.Equals(request.Operation, "quiesce", StringComparison.OrdinalIgnoreCase))
        {
            _process.RequestQuiesce();
            return HostAgentRpcResponse.Succeeded($"Quiesce accepted by {_process.ServiceName}.");
        }

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

    private static async Task<string?> ReadRequestLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[256];
        var builder = new StringBuilder();

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    return builder.ToString();
                }

                if (character == '\r')
                {
                    continue;
                }

                if (builder.Length >= MaxRequestLineLength)
                {
                    throw new InvalidOperationException(
                        $"RPC request exceeds the maximum allowed length of {MaxRequestLineLength} characters.");
                }

                builder.Append(character);
            }
        }
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
