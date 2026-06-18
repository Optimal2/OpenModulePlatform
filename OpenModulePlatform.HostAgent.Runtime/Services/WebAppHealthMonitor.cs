using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class WebAppHealthMonitor
{
    public const string PortalHealthHttpClientName = "PortalHealth";

    public const string PortalHealthAllowInvalidTlsHttpClientName = "PortalHealthAllowInvalidTls";

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ILogger<WebAppHealthMonitor> _logger;

    public WebAppHealthMonitor(
        IOptionsMonitor<HostAgentSettings> settings,
        IHttpClientFactory httpClientFactory,
        OmpHostArtifactRepository repository,
        ILogger<WebAppHealthMonitor> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _repository = repository;
        _logger = logger;
    }

    public async Task<WebAppHealthProbeResult?> ProbePortalAsync(
        Guid hostId,
        bool recycleIfUnhealthy,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var healthSettings = settings.PortalHealthCheck;
        if (!settings.DeployWebApps || !healthSettings.Enabled)
        {
            return null;
        }

        var result = await ExecutePortalProbeAsync(settings, cancellationToken);
        result = await _repository.UpsertWebAppHealthStateAsync(hostId, result, cancellationToken);

        var shouldRecycle = recycleIfUnhealthy
            || ShouldAutoRecycle(settings, result);
        if (shouldRecycle && !result.IsHealthy)
        {
            var message = RecycleAppPool(result.AppPoolName);
            await _repository.RecordWebAppHealthActionAsync(
                hostId,
                result.HealthKey,
                message,
                cancellationToken);
            result.LastActionUtc = DateTime.UtcNow;
            _logger.LogWarning(
                "Recycled web app application pool after failed health probe. HealthKey={HealthKey}, AppPoolName={AppPoolName}, Message={Message}",
                result.HealthKey,
                result.AppPoolName,
                message);
        }

        return result;
    }

    public async Task<RecycleWebAppAppPoolJobResult> RecyclePortalAppPoolAsync(
        Guid hostId,
        string? healthKey,
        string? appPoolName,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var resolvedHealthKey = string.IsNullOrWhiteSpace(healthKey)
            ? settings.PortalHealthCheck.HealthKey
            : healthKey.Trim();
        var resolvedAppPoolName = string.IsNullOrWhiteSpace(appPoolName)
            ? ResolvePortalAppPoolName(settings)
            : appPoolName.Trim();
        var message = RecycleAppPool(resolvedAppPoolName);

        await _repository.RecordWebAppHealthActionAsync(
            hostId,
            resolvedHealthKey,
            message,
            cancellationToken);

        return new RecycleWebAppAppPoolJobResult
        {
            HealthKey = resolvedHealthKey,
            AppPoolName = resolvedAppPoolName,
            Message = message
        };
    }

    public CollectWebAppLogsJobResult CollectPortalLogTail(string? healthKey, int maxLines)
    {
        var settings = _settings.CurrentValue;
        var resolvedHealthKey = string.IsNullOrWhiteSpace(healthKey)
            ? settings.PortalHealthCheck.HealthKey
            : healthKey.Trim();
        var logDirectory = string.IsNullOrWhiteSpace(settings.PortalPhysicalPath)
            ? Path.Join(AppContext.BaseDirectory, "logs")
            : Path.Join(settings.PortalPhysicalPath.Trim(), "logs");

        if (!Directory.Exists(logDirectory))
        {
            return new CollectWebAppLogsJobResult
            {
                HealthKey = resolvedHealthKey,
                LogDirectory = logDirectory,
                Message = "Portal log directory does not exist."
            };
        }

        FileInfo? logFile;
        try
        {
            logFile = Directory
                .EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CollectWebAppLogsJobResult
            {
                HealthKey = resolvedHealthKey,
                LogDirectory = logDirectory,
                Message = $"Could not enumerate Portal log files: {ex.Message}"
            };
        }

        if (logFile is null)
        {
            return new CollectWebAppLogsJobResult
            {
                HealthKey = resolvedHealthKey,
                LogDirectory = logDirectory,
                Message = "No Portal log files were found."
            };
        }

        try
        {
            var content = ReadTail(logFile.FullName, Math.Clamp(maxLines, 20, 500));
            return new CollectWebAppLogsJobResult
            {
                HealthKey = resolvedHealthKey,
                LogDirectory = logDirectory,
                LogFile = logFile.FullName,
                LineCount = content.LineCount,
                Content = content.Text,
                Message = $"Read {content.LineCount} line(s) from the latest Portal log file."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CollectWebAppLogsJobResult
            {
                HealthKey = resolvedHealthKey,
                LogDirectory = logDirectory,
                LogFile = logFile.FullName,
                Message = $"Could not read Portal log file: {ex.Message}"
            };
        }
    }

    private async Task<WebAppHealthProbeResult> ExecutePortalProbeAsync(
        HostAgentSettings settings,
        CancellationToken cancellationToken)
    {
        var healthSettings = settings.PortalHealthCheck;
        var probeUrl = BuildPortalHealthUrl(settings);
        var appPoolName = ResolvePortalAppPoolName(settings);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(healthSettings.TimeoutSeconds, 1, 120));

        using var client = _httpClientFactory.CreateClient(healthSettings.AllowInvalidTlsCertificate
            ? PortalHealthAllowInvalidTlsHttpClientName
            : PortalHealthHttpClientName);
        client.Timeout = timeout;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            var hostHeader = ResolveHostHeader(settings);
            if (!string.IsNullOrWhiteSpace(hostHeader))
            {
                request.Headers.Host = hostHeader;
            }

            using var response = await client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var isHealthy = (int)response.StatusCode is >= 200 and < 400;

            return new WebAppHealthProbeResult
            {
                HealthKey = CleanHealthKey(healthSettings.HealthKey),
                DisplayName = CleanDisplayName(healthSettings.DisplayName),
                ProbeUrl = probeUrl.ToString(),
                AppPoolName = appPoolName,
                Status = isHealthy ? WebAppHealthStatuses.Healthy : WebAppHealthStatuses.Unhealthy,
                HttpStatusCode = (int)response.StatusCode,
                ResponseSummary = Summarize(text),
                Error = isHealthy ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim()
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new WebAppHealthProbeResult
            {
                HealthKey = CleanHealthKey(healthSettings.HealthKey),
                DisplayName = CleanDisplayName(healthSettings.DisplayName),
                ProbeUrl = probeUrl.ToString(),
                AppPoolName = appPoolName,
                Status = WebAppHealthStatuses.Unhealthy,
                Error = ex.Message
            };
        }
    }

    private static bool ShouldAutoRecycle(
        HostAgentSettings settings,
        WebAppHealthProbeResult result)
    {
        var healthSettings = settings.PortalHealthCheck;
        if (!healthSettings.AutoRecycleAppPool
            || result.IsHealthy
            || result.ConsecutiveFailures < healthSettings.FailureThreshold)
        {
            return false;
        }

        if (!result.LastActionUtc.HasValue)
        {
            return true;
        }

        return result.LastActionUtc.Value <= DateTime.UtcNow.AddMinutes(-healthSettings.AutoRecycleCooldownMinutes);
    }

    private static Uri BuildPortalHealthUrl(HostAgentSettings settings)
    {
        var healthSettings = settings.PortalHealthCheck;
        var scheme = string.IsNullOrWhiteSpace(healthSettings.Scheme)
            ? CleanScheme(settings.IisBindingProtocol)
            : CleanScheme(healthSettings.Scheme);
        var host = ResolvePortalHealthHostName(settings);
        var port = healthSettings.Port.GetValueOrDefault(settings.IisBindingPort);
        var path = healthSettings.Path.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var builder = new UriBuilder(scheme, host, port, path);
        if ((scheme == "http" && port == 80) || (scheme == "https" && port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    private static string ResolveHostHeader(HostAgentSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PortalHealthCheck.HostHeader))
        {
            return settings.PortalHealthCheck.HostHeader.Trim();
        }

        return string.Empty;
    }

    private static string ResolvePortalHealthHostName(HostAgentSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PortalHealthCheck.HostName))
        {
            return settings.PortalHealthCheck.HostName.Trim();
        }

        return settings.ResolveHostKey();
    }

    private static string ResolvePortalAppPoolName(HostAgentSettings settings)
        => BuildIisAppPoolName(settings, "portal");

    private static string BuildIisAppPoolName(HostAgentSettings settings, string value)
    {
        var prefix = string.IsNullOrWhiteSpace(settings.IisAppPoolNamePrefix)
            ? string.Empty
            : settings.IisAppPoolNamePrefix.Trim();
        var normalized = new string(value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')
            .ToArray());
        normalized = normalized.Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "app";
        }

        var name = prefix + normalized;
        return name.Length <= 80 ? name : name[..80].TrimEnd('_', '.', '-');
    }

    private static string RecycleAppPool(string appPoolName)
    {
        if (string.IsNullOrWhiteSpace(appPoolName))
        {
            throw new InvalidOperationException("IIS application pool name is required.");
        }

        var state = GetAppPoolState(appPoolName);
        if (string.Equals(state, "Started", StringComparison.OrdinalIgnoreCase))
        {
            RunAppCmd("recycle", "apppool", $"/apppool.name:{appPoolName}");
            return $"Recycled IIS application pool '{appPoolName}'.";
        }

        RunAppCmd("start", "apppool", $"/apppool.name:{appPoolName}");
        return $"Started IIS application pool '{appPoolName}' because its state was '{state ?? "unknown"}'.";
    }

    private static string? GetAppPoolState(string appPoolName)
    {
        var output = RunAppCmd("list", "apppool", $"/name:{appPoolName}");
        var text = string.Join('\n', output);
        const string marker = "state:";
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = text.IndexOfAny([',', ')'], start);
        if (end < 0)
        {
            end = text.Length;
        }

        return text[start..end].Trim();
    }

    private static string[] RunAppCmd(params string[] arguments)
    {
        var result = RunProcess(GetAppCmdPath(), arguments);
        if (result.ExitCode == 0)
        {
            return SplitOutput(result.StdOut);
        }

        var message = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        throw new InvalidOperationException(
            $"appcmd.exe failed with exit code {result.ExitCode}: {message.Trim()}");
    }

    private static string GetAppCmdPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var appCmdPath = Path.Join(windowsDirectory, "System32", "inetsrv", "appcmd.exe");
        if (!File.Exists(appCmdPath))
        {
            throw new FileNotFoundException($"IIS appcmd.exe was not found: '{appCmdPath}'.", appCmdPath);
        }

        return appCmdPath;
    }

    private static ProcessResult RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var result = HostAgentProcessRunner.Run(fileName, arguments);
        return new ProcessResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    private static string[] SplitOutput(string value)
        => value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static LogTailResult ReadTail(string path, int maxLines)
    {
        const int maxBytes = 128 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytesToRead = (int)Math.Min(stream.Length, maxBytes);
        if (bytesToRead == 0)
        {
            return new LogTailResult(string.Empty, 0);
        }

        stream.Seek(-bytesToRead, SeekOrigin.End);
        var buffer = new byte[bytesToRead];
        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = stream.Read(buffer, totalRead, bytesToRead - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, totalRead);
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var selected = lines
            .Skip(Math.Max(0, lines.Length - maxLines))
            .Select(static line => line.Length <= 2000 ? line : line[..2000])
            .ToArray();
        return new LogTailResult(string.Join(Environment.NewLine, selected), selected.Length);
    }

    private static string CleanScheme(string? value)
        => string.Equals(value?.Trim(), "https", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";

    private static string CleanHealthKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? "portal" : value.Trim();

    private static string CleanDisplayName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "OMP Portal" : value.Trim();

    private static string? Summarize(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 1000 ? text : text[..1000];
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

    private sealed record LogTailResult(string Text, int LineCount);
}
