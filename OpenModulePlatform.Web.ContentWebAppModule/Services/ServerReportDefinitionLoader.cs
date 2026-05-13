// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ServerReportDefinitionLoader.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed partial class ServerReportDefinitionLoader
{
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
        var directory = GetReportsDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(IsValidReportKey)
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

        return File.Exists(ResolveReportPath(reportKey!));
    }

    public async Task<ServerReportDefinition> LoadAsync(string? reportKey, CancellationToken ct)
    {
        if (!IsValidReportKey(reportKey))
        {
            throw new ServerReportException("The server report key is invalid.");
        }

        var reportPath = ResolveReportPath(reportKey!);
        if (!File.Exists(reportPath))
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

    private string ResolveReportPath(string reportKey)
    {
        var directory = GetReportsDirectory();
        var fullPath = Path.GetFullPath(Path.Combine(directory, reportKey + ".json"));
        var directoryPrefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ServerReportException("The server report key is invalid.");
        }

        return fullPath;
    }

    private string GetReportsDirectory()
    {
        var configuredPath = _options.Value.ServerReportsPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = "App_Data/ContentReports";
        }

        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(_environment.ContentRootPath, configuredPath));
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
