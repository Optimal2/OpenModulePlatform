using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
        return $"service:{serviceName ?? processName}";
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
            // R7-F5. Both the result collection and every ManagementObject in it hold
            // unmanaged WMI resources and are IDisposable. The old LINQ chain leaked all
            // of them to the finalizer on every call; enumerate explicitly and dispose
            // each object, the same pattern the orphan scan already uses.
            using var services = searcher.Get();
            foreach (ManagementObject service in services)
            {
                using (service)
                {
                    var serviceName = ReadWindowsServiceName(service);
                    if (!string.IsNullOrWhiteSpace(serviceName))
                    {
                        return serviceName.Trim();
                    }
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

}

/// <summary>
/// Memoizes successful ensureArtifact results per artifact identity and version.
/// </summary>
/// <remarks>
/// With the host artifact cache disabled, every reconcile cycle asked the HostAgent to
/// ensure every worker's artifact again, although an artifact id + version names immutable
/// content and the answer -- the local path it lives at -- cannot change while the files are
/// still there. On a host with many workers that was a steady stream of identical RPCs over
/// the HostAgent's shared pipe, every RefreshSeconds, forever.
///
/// Only a proven-stable answer is kept: a successful response with a local path. A failure,
/// a timeout, an exception or a null response is never memoized, so a HostAgent outage is
/// retried on the next cycle exactly as before. A memoized answer is dropped again when the
/// caller's validity check says its files are gone, when the catalog stops listing that
/// artifact version (<see cref="RetainOnly"/>), or when the version changes (it is part of
/// the key). Concurrent callers for the same key share one in-flight RPC, so the
/// per-worker fan-out of the first cycle no longer issues N identical calls either.
/// </remarks>
internal sealed class HostAgentEnsureArtifactCache
{
    private readonly ConcurrentDictionary<(int ArtifactId, string? ArtifactVersion), Lazy<Task<HostAgentEnsureArtifactResponse?>>> _entries = new();

    public async Task<HostAgentEnsureArtifactResponse?> GetOrEnsureAsync(
        int artifactId,
        string? artifactVersion,
        Func<CancellationToken, Task<HostAgentEnsureArtifactResponse?>> ensureArtifact,
        Func<HostAgentEnsureArtifactResponse, bool> isStillValid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensureArtifact);
        ArgumentNullException.ThrowIfNull(isStillValid);

        var key = (artifactId, artifactVersion);
        if (_entries.TryGetValue(key, out var existing)
            && existing.IsValueCreated
            && existing.Value.IsCompletedSuccessfully
            && existing.Value.Result is { } memoized
            && !isStillValid(memoized))
        {
            _entries.TryRemove(KeyValuePair.Create(key, existing));
        }

        var entry = _entries.GetOrAdd(
            key,
            _ => new Lazy<Task<HostAgentEnsureArtifactResponse?>>(
                () => ensureArtifact(cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        HostAgentEnsureArtifactResponse? response;
        try
        {
            response = await entry.Value;
        }
        catch
        {
            _entries.TryRemove(KeyValuePair.Create(key, entry));
            throw;
        }

        if (!IsStable(response))
        {
            _entries.TryRemove(KeyValuePair.Create(key, entry));
        }

        return response;
    }

    /// <summary>
    /// Drops every memoized artifact the catalog no longer asks for.
    /// </summary>
    public void RetainOnly(IEnumerable<(int ArtifactId, string? ArtifactVersion)> liveKeys)
    {
        var live = liveKeys.ToHashSet();
        foreach (var key in _entries.Keys.Where(key => !live.Contains(key)))
        {
            _entries.TryRemove(key, out _);
        }
    }

    private static bool IsStable(HostAgentEnsureArtifactResponse? response)
        => response is { Success: true } && !string.IsNullOrWhiteSpace(response.LocalPath);
}
