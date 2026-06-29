using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
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
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Host resource telemetry collection or persistence failed. HostId={HostId}",
                hostId);
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
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to prune host resource telemetry samples. HostId={HostId}",
                hostId);
        }
    }

    private void CollectSamples(HostResourceTelemetrySettings settings, List<HostResourceSample> samples, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (settings.CollectIisAppPools)
        {
            CollectIisAppPoolSamples(settings, samples, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (settings.CollectServiceProcesses)
        {
            CollectServiceProcessSamples(settings, samples, cancellationToken);
        }
    }

    [SupportedOSPlatform("windows")]
    private void CollectIisAppPoolSamples(HostResourceTelemetrySettings settings, List<HostResourceSample> samples, CancellationToken cancellationToken)
    {
        var appCmdPath = GetAppCmdPath();
        if (appCmdPath is null)
        {
            _logger.LogDebug("IIS appcmd.exe was not found; skipping IIS app pool telemetry collection.");
            return;
        }

        string[] outputLines;
        try
        {
            var result = HostAgentProcessRunner.Run(appCmdPath, ["list", "wp"], TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "appcmd.exe list wp returned a non-zero exit code. ExitCode={ExitCode}, StdErr={StdErr}",
                    result.ExitCode,
                    result.StdErr);
                return;
            }

            outputLines = SplitOutput(result.StdOut);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to run appcmd.exe list wp for IIS app pool telemetry collection.");
            return;
        }

        foreach (var line in outputLines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (processId, appPoolName) = ParseAppCmdListWpLine(line);
            if (processId == 0 || string.IsNullOrWhiteSpace(appPoolName))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                var sampleTime = DateTime.UtcNow;
                var memoryMb = process.WorkingSet64 / (1024.0 * 1024.0);
                var cpuPercent = SampleCpuPercent(process, settings.SampleWindowSeconds, cancellationToken);
                var normalizedPoolName = NormalizeKeySegment(appPoolName);

                samples.Add(new HostResourceSample
                {
                    SampleKey = $"{IisAppPoolCpuKeyPrefix}{normalizedPoolName}",
                    SampleValue = cpuPercent,
                    SampledUtc = sampleTime
                });

                samples.Add(new HostResourceSample
                {
                    SampleKey = $"{IisAppPoolMemoryKeyPrefix}{normalizedPoolName}",
                    SampleValue = memoryMb,
                    SampledUtc = sampleTime
                });
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
                    "Access denied while sampling IIS worker process. ProcessId={ProcessId}, AppPoolName={AppPoolName}",
                    processId,
                    appPoolName);
            }

            if (samples.Count >= settings.MaxSamplesPerCycle)
            {
                _logger.LogWarning(
                    "Host resource telemetry sample limit reached while collecting IIS app pools. Limit={Limit}",
                    settings.MaxSamplesPerCycle);
                break;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private void CollectServiceProcessSamples(HostResourceTelemetrySettings settings, List<HostResourceSample> samples, CancellationToken cancellationToken)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ProcessId, State FROM Win32_Service WHERE State = 'Running'");

            foreach (ManagementObject service in searcher.Get())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var serviceName = service["Name"]?.ToString() ?? string.Empty;
                    var processId = Convert.ToInt32(service["ProcessId"], CultureInfo.InvariantCulture);

                    if (processId <= 0 || string.IsNullOrWhiteSpace(serviceName))
                    {
                        continue;
                    }

                    using var process = Process.GetProcessById(processId);
                    var sampleTime = DateTime.UtcNow;
                    var memoryMb = process.WorkingSet64 / (1024.0 * 1024.0);
                    var cpuPercent = SampleCpuPercent(process, settings.SampleWindowSeconds, cancellationToken);
                    var normalizedServiceName = NormalizeKeySegment(serviceName);

                    samples.Add(new HostResourceSample
                    {
                        SampleKey = $"{ServiceCpuKeyPrefix}{normalizedServiceName}",
                        SampleValue = cpuPercent,
                        SampledUtc = sampleTime
                    });

                    samples.Add(new HostResourceSample
                    {
                        SampleKey = $"{ServiceMemoryKeyPrefix}{normalizedServiceName}",
                        SampleValue = memoryMb,
                        SampledUtc = sampleTime
                    });
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

    private static double SampleCpuPercent(Process process, int windowSeconds, CancellationToken cancellationToken)
    {
        var startCpu = process.TotalProcessorTime;
        var startUtc = DateTime.UtcNow;

        try
        {
            Task.Delay(TimeSpan.FromSeconds(windowSeconds), cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        if (process.HasExited)
        {
            return 0;
        }

        var endCpu = process.TotalProcessorTime;
        var elapsed = (DateTime.UtcNow - startUtc).TotalSeconds;
        if (elapsed <= 0)
        {
            return 0;
        }

        var cpuSeconds = (endCpu - startCpu).TotalSeconds;
        var cpuPercent = cpuSeconds / (elapsed * Environment.ProcessorCount) * 100.0;
        return Math.Clamp(cpuPercent, 0, 100 * Environment.ProcessorCount);
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
