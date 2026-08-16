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
        try
        {
            await RunRpcListenerAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The outermost boundary of a BackgroundService must be broad. Anything that
            // leaves ExecuteAsync reaches .NET's default BackgroundServiceExceptionBehavior
            // .StopHost, which stops the entire HostAgent Windows service -- so a fault in
            // the RPC listener, an optional side channel, would take the convergence loop
            // down with it and leave the machine deploying nothing at all. The listener setup
            // below is not hypothetical: ResolveRpcPipeName runs on operator-supplied
            // settings, CreatePipeSecurity resolves configured account names against the
            // domain, and both sat outside every try. Same defect and same fix as R12-E1 in
            // OmpPerformanceTelemetryHostedService, and as R3-E4 (PushEventDispatcher
            // HostedService) and R5-D1 (HostAgentHostedService) before it: a curated list of
            // exception types on a hosted-service boundary is a list of the failures somebody
            // thought of. OperationCanceledException is deliberately not caught -- it is how
            // a clean shutdown reports itself and it must keep propagating.
            _logger.LogError(
                ex,
                "HostAgent RPC listener stopped after an unhandled failure. RPC is unavailable until the service restarts; the convergence loop is unaffected. ServiceName={ServiceName}",
                _process.ServiceName);
        }
    }

    private async Task RunRpcListenerAsync(CancellationToken stoppingToken)
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed accept must never end the listener, so this catch is the same
                // shape as the outer one: everything except cancellation. The curated list it
                // replaces (IOException, ObjectDisposedException, InvalidOperationException,
                // UnauthorizedAccessException) missed types this very loop can produce --
                // NamedPipeServerStreamAcl.Create throws Win32Exception when the pipe name is
                // already owned by another security descriptor and ArgumentOutOfRangeException
                // on a malformed name, and an unmatched type here escaped ExecuteAsync and
                // stopped the whole service (R12-E1's sibling; see R3-E4 and R5-D1).
                //
                // Retrying forever is deliberate even for a permanent fault: RPC is how
                // WorkerManager asks HostAgent to materialise an artifact, so an unavailable
                // listener that keeps trying beats one that has given up, and the five-second
                // delay bounds the log volume it can produce.
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
        // The pipe is disposed by this declaration whatever happens below, so it stays
        // outside the try -- a using declaration cannot itself throw.
        await using var ownedPipe = pipe;
        string? requestedBy = null;

        try
        {
            // R8-P4-11: the timeout setup used to sit out here with the pipe. This
            // method is started fire-and-forget (Task.Run with no continuation), so an
            // exception before the try had nowhere to go: it became an unobserved task
            // exception, logged by nobody, and the caller saw a pipe that simply closed.
            // Both statements can throw for real reasons -- a disposed service token
            // source during shutdown, and an out-of-range RpcRequestTimeoutSeconds from
            // configuration.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(serviceCancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.CurrentValue.RpcRequestTimeoutSeconds)));

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

            var response = await ExecuteRequestAsync(request, IsPrivilegedCaller(pipe), timeoutCts.Token);
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
        // Only LocalSystem and Administrators by default: the legitimate
        // clients (WorkerManager and its workers) run as LocalSystem, while
        // granting LocalService/NetworkService let any unrelated service under
        // those well-known accounts call the pipe (R3-D3). Deployments that run
        // a client under another account add it via RpcAllowedClientAccounts /
        // RpcAllowedClientServiceNames.
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    }

    // quiesce stops the whole convergence loop, so it must not be reachable by
    // every account the pipe ACL allows - only LocalSystem or an administrator
    // (R3-D1). Non-Windows never reaches this service, so treat that as denied.
    private static bool IsPrivilegedCaller(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var privileged = false;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                privileged = identity.IsSystem
                    || principal.IsInRole(WindowsBuiltInRole.Administrator);
            });
            return privileged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<HostAgentRpcResponse> ExecuteRequestAsync(
        HostAgentRpcRequest request,
        bool isPrivilegedCaller,
        CancellationToken cancellationToken)
    {
        if (string.Equals(request.Operation, "quiesce", StringComparison.OrdinalIgnoreCase))
        {
            if (!isPrivilegedCaller)
            {
                _logger.LogWarning("Rejected HostAgent quiesce RPC from a non-privileged caller.");
                return HostAgentRpcResponse.Failed("Quiesce requires an administrator or LocalSystem caller.");
            }

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
