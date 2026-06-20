using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
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
    private readonly Lazy<string> _baseCallerIdentity;

    public HostAgentRpcClient(
        IOptionsMonitor<WorkerManagerSettings> settings,
        ILogger<HostAgentRpcClient> logger)
    {
        _settings = settings;
        _logger = logger;
        _baseCallerIdentity = new Lazy<string>(BuildBaseCallerIdentity, LazyThreadSafetyMode.ExecutionAndPublication);
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
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);

            await pipe.ConnectAsync(timeoutCts.Token);

            var request = new
            {
                operation = "ensureArtifact",
                artifactId,
                desiredLocalPath,
                requestedBy = BuildRequestedBy(settings)
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
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var message = $"HostAgent ensureArtifact RPC timed out after {settings.HostAgentRpc.TimeoutSeconds} second(s).";
            _logger.LogWarning(
                ex,
                "HostAgent ensureArtifact RPC timed out. ArtifactId={ArtifactId}, PipeName={PipeName}, TimeoutSeconds={TimeoutSeconds}",
                artifactId,
                pipeName,
                settings.HostAgentRpc.TimeoutSeconds);

            return new HostAgentEnsureArtifactResponse
            {
                Success = false,
                ErrorMessage = message
            };
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

    private string BuildRequestedBy(WorkerManagerSettings settings)
    {
        return $"{_baseCallerIdentity.Value}; host:{settings.ResolveHostKey()}";
    }

    private static string BuildBaseCallerIdentity()
    {
        var serviceName = TryResolveCurrentServiceName();
        var processName = TryResolveCurrentProcessName();
        var userName = TryResolveCurrentUserName();
        return $"service:{serviceName ?? processName}; user:{userName}; pid:{Environment.ProcessId}";
    }

    private static string? TryResolveCurrentServiceName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Service WHERE ProcessId = {Environment.ProcessId}");
            foreach (var serviceName in searcher.Get()
                         .OfType<ManagementObject>()
                         .Select(ReadWindowsServiceName))
            {
                if (!string.IsNullOrWhiteSpace(serviceName))
                {
                    return serviceName.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsServiceName(ManagementObject instance)
        => instance["Name"]?.ToString();

    private static string TryResolveCurrentProcessName()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            if (!string.IsNullOrWhiteSpace(process.ProcessName))
            {
                return process.ProcessName.Trim();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "unknown-process";
        }

        return "unknown-process";
    }

    private static string TryResolveCurrentUserName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "unknown-user";
        }

        try
        {
            return string.IsNullOrWhiteSpace(WindowsIdentity.GetCurrent().Name)
                ? "unknown-user"
                : WindowsIdentity.GetCurrent().Name;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return "unknown-user";
        }
    }
}
