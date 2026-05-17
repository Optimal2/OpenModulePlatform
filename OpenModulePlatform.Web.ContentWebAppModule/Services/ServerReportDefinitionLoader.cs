// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ServerReportDefinitionLoader.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed partial class ServerReportDefinitionLoader
{
    private const string PackagedReportsPath = "ContentReports";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IWebHostEnvironment _environment;
    private readonly IOptions<ContentWebAppModuleOptions> _options;
    private readonly ILogger<ServerReportDefinitionLoader> _logger;

    public ServerReportDefinitionLoader(
        IWebHostEnvironment environment,
        IOptions<ContentWebAppModuleOptions> options,
        ILogger<ServerReportDefinitionLoader> logger)
    {
        _environment = environment;
        _options = options;
        _logger = logger;
    }

    public IReadOnlyList<string> ListReportKeys()
    {
        return GetReportsDirectories()
            .Where(Directory.Exists)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(IsValidReportKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public bool IsValidReportKey(string? reportKey)
        => IsValidReportKey(reportKey, requireValue: true);

    public bool ReportExists(string? reportKey)
    {
        if (!IsValidReportKey(reportKey))
        {
            return false;
        }

        return TryResolveExistingReportPath(reportKey!) is not null;
    }

    public async Task<ServerReportDefinition> LoadAsync(string? reportKey, CancellationToken ct)
    {
        if (!IsValidReportKey(reportKey))
        {
            throw new ServerReportException("The server report key is invalid.");
        }

        var reportPath = TryResolveExistingReportPath(reportKey!);
        if (reportPath is null)
        {
            throw new ServerReportException("The server report definition was not found.");
        }

        try
        {
            await using var stream = File.OpenRead(reportPath);
            var definition = await JsonSerializer.DeserializeAsync<ServerReportDefinition>(stream, JsonOptions, ct)
                ?? throw new ServerReportException("The server report definition is empty.");

            ValidateDefinition(definition);
            return definition;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid server report JSON: {ReportPath}", reportPath);
            throw new ServerReportException("The server report definition is not valid JSON.");
        }
    }

    private string? TryResolveExistingReportPath(string reportKey)
    {
        foreach (var directory in GetReportsDirectories())
        {
            var fullPath = ResolveReportPath(directory, reportKey);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private IReadOnlyList<string> GetReportsDirectories()
    {
        var configuredDirectory = GetConfiguredReportsDirectory();
        var packagedDirectory = Path.GetFullPath(Path.Join(_environment.ContentRootPath, PackagedReportsPath));

        // Runtime App_Data reports intentionally stay first. They are local data
        // and can override a packaged report with the same key without editing
        // the immutable app artifact.
        return new[] { configuredDirectory, packagedDirectory }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string GetConfiguredReportsDirectory()
    {
        var configuredPath = _options.Value.ServerReportsPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = "App_Data/ContentReports";
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.GetFullPath(Path.Join(_environment.ContentRootPath, configuredPath));
    }

    private static string ResolveReportPath(string directory, string reportKey)
    {
        var fileName = $"{reportKey}.json";
        var fullDirectory = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(Path.Join(fullDirectory, fileName));
        var directoryPrefix = fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ServerReportException("The server report key is invalid.");
        }

        return fullPath;
    }

    private static void ValidateDefinition(ServerReportDefinition definition)
    {
        if (definition.Queries.Count == 0)
        {
            throw new ServerReportException("The server report definition has no queries.");
        }

        foreach (var query in definition.Queries)
        {
            if (string.IsNullOrWhiteSpace(query.Sql))
            {
                throw new ServerReportException("A server report query is missing SQL.");
            }

            if (!string.IsNullOrWhiteSpace(query.Renderer)
                && !string.Equals(query.Renderer, "table", StringComparison.OrdinalIgnoreCase))
            {
                throw new ServerReportException("A server report query uses an unsupported renderer.");
            }
        }
    }

    private static bool IsValidReportKey(string? reportKey, bool requireValue = false)
    {
        if (string.IsNullOrWhiteSpace(reportKey))
        {
            return !requireValue;
        }

        return ReportKeyRegex().IsMatch(reportKey.Trim());
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReportKeyRegex();
}

public sealed class ServerReportException : Exception
{
    public ServerReportException(string message)
        : base(message)
    {
    }
}
