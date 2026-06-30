using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class HostResourceCollector
{
    private const string IisAppPoolCpuKeyPrefix = "iis.apppool.";
    private const string IisAppPoolMemoryKeyPrefix = "iis.apppool.memory.";
    private const string ServiceCpuKeyPrefix = "service.";
    private const string ServiceMemoryKeyPrefix = "service.memory.";
    private const string ServiceStateKeyPrefix = "service.state.";

    private sealed record ProcessTelemetryTarget(
        int ProcessId,
        string CpuSampleKey,
        string MemorySampleKey);

    private readonly record struct ProcessTelemetrySnapshot(
        TimeSpan TotalProcessorTime,
        long WorkingSetBytes,
        DateTime SampledUtc);

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ILogger<HostResourceCollector> _logger;
    private readonly Lock _stateLock = new();
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public HostResourceCollector(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ILogger<HostResourceCollector> logger)
    {
        _settings = settings;
        _repository = repository;
        _logger = logger;
    }

    public async Task CollectAndPersistAsync(Guid hostId, CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue.ResourceTelemetry;
        if (!settings.Enabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var sampleInterval = TimeSpan.FromSeconds(settings.SampleIntervalSeconds);

        lock (_stateLock)
        {
            if (now - _lastSampleUtc < sampleInterval)
            {
                return;
            }
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(15, settings.SampleIntervalSeconds / 2));

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var samples = new List<HostResourceSample>();
            await Task.Run(() => CollectSamples(settings, samples, linkedCts.Token), linkedCts.Token);

            if (samples.Count == 0)
            {
                return;
            }

            await _repository.UpsertHostResourceSamplesAsync(
                hostId,
                samples,
                settings.BucketMinutes,
                linkedCts.Token);

            lock (_stateLock)
            {
                _lastSampleUtc = now;
            }

            _logger.LogInformation(
                "Persisted host resource telemetry samples. HostId={HostId}, Count={Count}",
                hostId,
                samples.Count);

            await PruneAsync(hostId, settings, linkedCts.Token);
        }
        catch (OperationCanceledException ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(
                ex,
                "Host resource telemetry collection or persistence timed out and was cancelled. HostId={HostId}, TimeoutSeconds={TimeoutSeconds}",
                hostId,
                timeout.TotalSeconds);
        }
        catch (SqlException ex)
        {
            LogCollectionFailure(ex, hostId);
        }
        catch (InvalidOperationException ex)
        {
            LogCollectionFailure(ex, hostId);
        }
        catch (IOException ex)
        {
            LogCollectionFailure(ex, hostId);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogCollectionFailure(ex, hostId);
        }
        catch (ManagementException ex)
        {
            LogCollectionFailure(ex, hostId);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            LogCollectionFailure(ex, hostId);
        }
        catch (TimeoutException ex)
        {
            LogCollectionFailure(ex, hostId);
        }
    }

    private async Task PruneAsync(Guid hostId, HostResourceTelemetrySettings settings, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var pruneInterval = TimeSpan.FromSeconds(settings.PruneIntervalSeconds);

        lock (_stateLock)
        {
            if (now - _lastPruneUtc < pruneInterval)
            {
                return;
            }
        }

        try
        {
            var deleted = await _repository.PruneHostResourceSamplesAsync(settings.RetainHours, cancellationToken);

            lock (_stateLock)
            {
                _lastPruneUtc = now;
            }

            _logger.LogInformation(
                "Pruned host resource telemetry samples. HostId={HostId}, Deleted={Deleted}, RetainHours={RetainHours}",
                hostId,
                deleted,
                settings.RetainHours);
        }
        catch (SqlException ex)
        {
            LogPruneFailure(ex, hostId);
        }
        catch (InvalidOperationException ex)
        {
            LogPruneFailure(ex, hostId);
        }
        catch (TimeoutException ex)
        {
            LogPruneFailure(ex, hostId);
        }
    }

    private void LogCollectionFailure(Exception ex, Guid hostId)
    {
        // Resource telemetry is best-effort operational data. A collector failure
        // should be logged without stopping the HostAgent desired-state loop.
        _logger.LogError(
            ex,
            "Host resource telemetry collection or persistence failed. HostId={HostId}",
            hostId);
    }

    private void LogPruneFailure(Exception ex, Guid hostId)
    {
        // Pruning is maintenance cleanup; keep collection alive and retry on the
        // next prune interval if the repository or database is temporarily unavailable.
        _logger.LogError(
            ex,
            "Failed to prune host resource telemetry samples. HostId={HostId}",
            hostId);
    }

    private void CollectSamples(HostResourceTelemetrySettings settings, List<HostResourceSample> samples, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var targets = new List<ProcessTelemetryTarget>();

        if (settings.CollectIisAppPools)
        {
            CollectIisAppPoolTargets(settings, targets, samples, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (settings.CollectServiceProcesses)
        {
            CollectServiceProcessTargets(settings, targets, samples, cancellationToken);
        }

        CollectProcessTargetSamples(settings, targets, samples, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private void CollectIisAppPoolTargets(
        HostResourceTelemetrySettings settings,
        List<ProcessTelemetryTarget> targets,
        List<HostResourceSample> samples,
        CancellationToken cancellationToken)
    {
        var appCmdPath = GetAppCmdPath();
        if (appCmdPath is null)
        {
            _logger.LogDebug("IIS appcmd.exe was not found; skipping IIS app pool telemetry collection.");
            return;
        }

        string[] appPoolLines;
        string[] workerProcessLines;
        try
        {
            var appPoolResult = HostAgentProcessRunner.Run(appCmdPath, ["list", "apppool"], TimeSpan.FromSeconds(15));
            if (appPoolResult.ExitCode != 0)
            {
                _logger.LogWarning(
                    "appcmd.exe list apppool returned a non-zero exit code. ExitCode={ExitCode}, StdErr={StdErr}",
                    appPoolResult.ExitCode,
                    appPoolResult.StdErr);
                return;
            }

            var workerProcessResult = HostAgentProcessRunner.Run(appCmdPath, ["list", "wp"], TimeSpan.FromSeconds(15));
            if (workerProcessResult.ExitCode != 0)
            {
                _logger.LogWarning(
                    "appcmd.exe list wp returned a non-zero exit code. ExitCode={ExitCode}, StdErr={StdErr}",
                    workerProcessResult.ExitCode,
                    workerProcessResult.StdErr);
                return;
            }

            appPoolLines = SplitOutput(appPoolResult.StdOut);
            workerProcessLines = SplitOutput(workerProcessResult.StdOut);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to run appcmd.exe for IIS app pool telemetry collection.");
            return;
        }

        var activeWorkerProcesses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in workerProcessLines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (processId, appPoolName) = ParseAppCmdListWpLine(line);
            if (processId == 0 || string.IsNullOrWhiteSpace(appPoolName))
            {
                continue;
            }

            activeWorkerProcesses[appPoolName] = processId;
        }

        var appPoolNamePrefix = _settings.CurrentValue.IisAppPoolNamePrefix.Trim();
        foreach (var line in appPoolLines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var appPoolName = ParseAppCmdListAppPoolLine(line);
            if (string.IsNullOrWhiteSpace(appPoolName)
                || (!string.IsNullOrEmpty(appPoolNamePrefix)
                    && !appPoolName.StartsWith(appPoolNamePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var normalizedPoolName = NormalizeKeySegment(appPoolName);
            var cpuSampleKey = $"{IisAppPoolCpuKeyPrefix}{normalizedPoolName}";
            var memorySampleKey = $"{IisAppPoolMemoryKeyPrefix}{normalizedPoolName}";

            try
            {
                if (activeWorkerProcesses.TryGetValue(appPoolName, out var processId))
                {
                    targets.Add(new ProcessTelemetryTarget(
                        processId,
                        cpuSampleKey,
                        memorySampleKey));
                }
                else
                {
                    var sampledUtc = DateTime.UtcNow;
                    samples.Add(new HostResourceSample
                    {
                        SampleKey = cpuSampleKey,
                        SampleValue = 0,
                        SampledUtc = sampledUtc
                    });
                    samples.Add(new HostResourceSample
                    {
                        SampleKey = memorySampleKey,
                        SampleValue = 0,
                        SampledUtc = sampledUtc
                    });
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Process or app pool data disappeared between enumeration and target creation.
            }

            if (HasReachedTargetSampleCapacity(targets.Count, settings.MaxSamplesPerCycle))
            {
                _logger.LogWarning(
                    "Host resource telemetry target limit reached while collecting IIS app pools. Limit={Limit}",
                    settings.MaxSamplesPerCycle);
                break;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private void CollectServiceProcessTargets(
        HostResourceTelemetrySettings settings,
        List<ProcessTelemetryTarget> targets,
        List<HostResourceSample> samples,
        CancellationToken cancellationToken)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ProcessId, PathName, State FROM Win32_Service");

            foreach (ManagementObject service in searcher.Get())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var serviceName = service["Name"]?.ToString() ?? string.Empty;
                    var pathName = service["PathName"]?.ToString() ?? string.Empty;
                    var state = service["State"]?.ToString() ?? string.Empty;
                    var processIdValue = service["ProcessId"];
                    var processId = processIdValue is null
                        ? 0
                        : Convert.ToInt32(processIdValue, CultureInfo.InvariantCulture);

                    if (string.IsNullOrWhiteSpace(serviceName)
                        || !IsServiceTelemetryTarget(serviceName, pathName, _settings.CurrentValue.ServicesRoot))
                    {
                        continue;
                    }

                    var normalizedServiceName = NormalizeKeySegment(serviceName);
                    var isRunning = string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);
                    var sampledUtc = DateTime.UtcNow;

                    // State sample: 1 = running, 0 = stopped (or any non-running state).
                    // This lets the Portal distinguish stopped services from idle-but-running ones.
                    samples.Add(new HostResourceSample
                    {
                        SampleKey = $"{ServiceStateKeyPrefix}{normalizedServiceName}",
                        SampleValue = isRunning ? 1 : 0,
                        SampledUtc = sampledUtc
                    });

                    if (isRunning && processId > 0)
                    {
                        targets.Add(new ProcessTelemetryTarget(
                            processId,
                            $"{ServiceCpuKeyPrefix}{normalizedServiceName}",
                            $"{ServiceMemoryKeyPrefix}{normalizedServiceName}"));
                    }
                    else
                    {
                        // Stopped OMP-owned services are still represented with 0 CPU and 0 RAM
                        // so the resource monitor does not hide them.
                        samples.Add(new HostResourceSample
                        {
                            SampleKey = $"{ServiceCpuKeyPrefix}{normalizedServiceName}",
                            SampleValue = 0,
                            SampledUtc = sampledUtc
                        });
                        samples.Add(new HostResourceSample
                        {
                            SampleKey = $"{ServiceMemoryKeyPrefix}{normalizedServiceName}",
                            SampleValue = 0,
                            SampledUtc = sampledUtc
                        });
                    }
                }
                catch (ArgumentException)
                {
                    // Process exited between enumeration and sampling.
                }
                catch (InvalidOperationException)
                {
                    // Process has exited.
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    _logger.LogDebug(
                        ex,
                        "Access denied while sampling Windows service process.");
                }
                finally
                {
                    service.Dispose();
                }

                if (samples.Count >= settings.MaxSamplesPerCycle)
                {
                    _logger.LogWarning(
                        "Host resource telemetry sample limit reached while collecting service processes. Limit={Limit}",
                        settings.MaxSamplesPerCycle);
                    break;
                }
            }
        }
        catch (ManagementException ex)
        {
            _logger.LogWarning(
                ex,
                "WMI query failed for Windows service process telemetry collection.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(
                ex,
                "Access denied while querying Windows service processes for telemetry collection.");
        }
    }

    private void CollectProcessTargetSamples(
        HostResourceTelemetrySettings settings,
        IReadOnlyList<ProcessTelemetryTarget> targets,
        List<HostResourceSample> samples,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var startUtc = DateTime.UtcNow;
        var uniqueProcessIds = targets
            .Select(static target => target.ProcessId)
            .Distinct()
            .ToArray();
        var startSnapshots = CaptureProcessSnapshots(uniqueProcessIds);

        try
        {
            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(settings.SampleWindowSeconds)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var endUtc = DateTime.UtcNow;
        var endSnapshots = CaptureProcessSnapshots(uniqueProcessIds);
        var elapsed = (endUtc - startUtc).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        var processorCount = Math.Max(1, Environment.ProcessorCount);
        foreach (var target in targets)
        {
            if (!startSnapshots.TryGetValue(target.ProcessId, out var start)
                || !endSnapshots.TryGetValue(target.ProcessId, out var end))
            {
                continue;
            }

            var cpuSeconds = (end.TotalProcessorTime - start.TotalProcessorTime).TotalSeconds;
            var cpuPercent = cpuSeconds / (elapsed * processorCount) * 100.0;
            cpuPercent = Math.Clamp(cpuPercent, 0, 100);
            var memoryMb = end.WorkingSetBytes / (1024.0 * 1024.0);

            samples.Add(new HostResourceSample
            {
                SampleKey = target.CpuSampleKey,
                SampleValue = cpuPercent,
                SampledUtc = end.SampledUtc
            });

            samples.Add(new HostResourceSample
            {
                SampleKey = target.MemorySampleKey,
                SampleValue = memoryMb,
                SampledUtc = end.SampledUtc
            });

            if (samples.Count >= settings.MaxSamplesPerCycle)
            {
                _logger.LogWarning(
                    "Host resource telemetry sample limit reached while collecting process samples. Limit={Limit}",
                    settings.MaxSamplesPerCycle);
                break;
            }
        }
    }

    private static Dictionary<int, ProcessTelemetrySnapshot> CaptureProcessSnapshots(IEnumerable<int> processIds)
    {
        var snapshots = new Dictionary<int, ProcessTelemetrySnapshot>();
        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    continue;
                }

                snapshots[processId] = new ProcessTelemetrySnapshot(
                    process.TotalProcessorTime,
                    process.WorkingSet64,
                    DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                // Processes can exit or become inaccessible between target discovery and sampling.
            }
        }

        return snapshots;
    }

    private static bool IsServiceTelemetryTarget(string serviceName, string pathName, string servicesRoot)
    {
        if (serviceName.StartsWith("OMP.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(servicesRoot) || string.IsNullOrWhiteSpace(pathName))
        {
            return false;
        }

        return TryResolveContainedServicePath(pathName, servicesRoot, out _);
    }

    private static bool HasReachedTargetSampleCapacity(int targetCount, int maxSamplesPerCycle)
    {
        if (targetCount <= 0)
        {
            return false;
        }

        var maxTargets = Math.Max(1, (maxSamplesPerCycle / 2) + (maxSamplesPerCycle % 2));
        return targetCount >= maxTargets;
    }

    private static bool TryResolveContainedServicePath(string pathName, string servicesRoot, out string executablePath)
    {
        executablePath = string.Empty;
        if (!TryExtractServiceExecutablePath(pathName, out var candidatePath))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(servicesRoot.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedPath = Path.GetFullPath(candidatePath);

            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            executablePath = normalizedPath;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryExtractServiceExecutablePath(string pathName, out string executablePath)
    {
        executablePath = string.Empty;
        var trimmed = pathName.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed[0] == '"')
        {
            var closingQuoteIndex = trimmed.IndexOf('"', 1);
            if (closingQuoteIndex <= 1)
            {
                return false;
            }

            executablePath = trimmed[1..closingQuoteIndex];
            return !string.IsNullOrWhiteSpace(executablePath);
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            executablePath = trimmed[..(exeIndex + 4)];
            return !string.IsNullOrWhiteSpace(executablePath);
        }

        var firstSpaceIndex = trimmed.IndexOf(' ');
        executablePath = firstSpaceIndex > 0
            ? trimmed[..firstSpaceIndex]
            : trimmed;
        return !string.IsNullOrWhiteSpace(executablePath);
    }

    private static (int ProcessId, string? AppPoolName) ParseAppCmdListWpLine(string line)
    {
        // Expected format: WP "1234" (applicationPool:AppPoolName)
        if (string.IsNullOrWhiteSpace(line))
        {
            return (0, null);
        }

        var match = AppCmdWpRegex().Match(line);
        if (!match.Success)
        {
            return (0, null);
        }

        if (!int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
        {
            return (0, null);
        }

        var appPoolName = match.Groups[2].Value.Trim();
        return (processId, appPoolName);
    }

    [GeneratedRegex(@"^WP\s+""(\d+)""\s+\(applicationPool:([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex AppCmdWpRegex();

    private static string? ParseAppCmdListAppPoolLine(string line)
    {
        // Expected format: APPPOOL "AppPoolName" (MgdVersion:,MgdMode:Integrated,state:Started)
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = AppCmdAppPoolRegex().Match(line);
        return match.Success
            ? match.Groups[1].Value.Trim()
            : null;
    }

    [GeneratedRegex(@"^APPPOOL\s+""([^""]+)""\s+\(", RegexOptions.IgnoreCase)]
    private static partial Regex AppCmdAppPoolRegex();

    private static string? GetAppCmdPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var appCmdPath = Path.Join(windowsDirectory, "System32", "inetsrv", "appcmd.exe");
        return File.Exists(appCmdPath) ? appCmdPath : null;
    }

    private static string[] SplitOutput(string value)
        => value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeKeySegment(string value)
    {
        var normalized = new string(value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')
            .ToArray());

        return normalized.Trim('_');
    }
}
