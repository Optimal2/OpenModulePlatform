// File: OpenModulePlatform.Web.ContentWebAppModule/Services/HtmlContentFileLoader.cs
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Options;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed partial class HtmlContentFileLoader
{
    private const string PackagedHtmlFilesPath = "ContentPages";
    private const string SharedDataRootDirectoryName = "Data";
    private const string SharedDataHtmlFilesDirectoryName = "ContentPages";

    private readonly IWebHostEnvironment _environment;
    private readonly IOptions<ContentWebAppModuleOptions> _options;

    public HtmlContentFileLoader(
        IWebHostEnvironment environment,
        IOptions<ContentWebAppModuleOptions> options)
    {
        _environment = environment;
        _options = options;
    }

    public IReadOnlyList<string> ListHtmlFileKeys()
    {
        return GetHtmlFileDirectories()
            .Where(Directory.Exists)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.htm*", SearchOption.TopDirectoryOnly))
            .Where(static path => IsSupportedExtension(Path.GetExtension(path)))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(IsValidHtmlFileKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public bool IsValidHtmlFileKey(string? htmlFileKey)
        => IsValidHtmlFileKey(htmlFileKey, requireValue: true);

    public bool HtmlFileExists(string? htmlFileKey)
    {
        if (!IsValidHtmlFileKey(htmlFileKey))
        {
            return false;
        }

        return TryResolveExistingHtmlFilePath(htmlFileKey!) is not null;
    }

    public async Task<string> LoadAsync(string? htmlFileKey, CancellationToken ct)
    {
        if (!IsValidHtmlFileKey(htmlFileKey))
        {
            throw new HtmlContentFileException("The HTML file key is invalid.");
        }

        var path = TryResolveExistingHtmlFilePath(htmlFileKey!);
        if (path is null)
        {
            throw new HtmlContentFileException("The HTML file was not found.");
        }

        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    private string? TryResolveExistingHtmlFilePath(string htmlFileKey)
    {
        foreach (var directory in GetHtmlFileDirectories())
        {
            foreach (var extension in new[] { ".html", ".htm" })
            {
                var fullPath = ResolveHtmlFilePath(directory, htmlFileKey, extension);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private IReadOnlyList<string> GetHtmlFileDirectories()
    {
        var configuredDirectory = GetConfiguredHtmlFilesDirectory();
        var packagedDirectory = Path.GetFullPath(Path.Join(_environment.ContentRootPath, PackagedHtmlFilesPath));
        var sharedDataRootDirectory = TryGetSharedDataRootDirectory(configuredDirectory);

        // Runtime/shared files intentionally stay first. They can live on a
        // shared UNC path for multi-server deployments and override packaged
        // HTML without changing the immutable app artifact. The shared Data
        // root fallback supports deployments that keep HTML and report JSON
        // side by side under one shared directory.
        return new[] { configuredDirectory, sharedDataRootDirectory, packagedDirectory }
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Select(static directory => directory!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string GetConfiguredHtmlFilesDirectory()
    {
        var configuredPath = _options.Value.HtmlFilesPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = "App_Data/ContentPages";
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.GetFullPath(Path.Join(_environment.ContentRootPath, configuredPath));
    }

    private static string? TryGetSharedDataRootDirectory(string configuredDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(configuredDirectory));
        if (!directory.Name.Equals(SharedDataHtmlFilesDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parent = directory.Parent;
        return parent is not null
            && parent.Name.Equals(SharedDataRootDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? parent.FullName
            : null;
    }

    private static string ResolveHtmlFilePath(string directory, string htmlFileKey, string extension)
    {
        var fileName = $"{htmlFileKey}{extension}";
        var fullDirectory = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(Path.Join(fullDirectory, fileName));
        var directoryPrefix = fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new HtmlContentFileException("The HTML file key is invalid.");
        }

        return fullPath;
    }

    private static bool IsSupportedExtension(string? extension)
        => extension is not null
            && (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase));

    private static bool IsValidHtmlFileKey(string? htmlFileKey, bool requireValue = false)
    {
        if (string.IsNullOrWhiteSpace(htmlFileKey))
        {
            return !requireValue;
        }

        return HtmlFileKeyRegex().IsMatch(htmlFileKey.Trim());
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlFileKeyRegex();
}

public sealed class HtmlContentFileException : Exception
{
    public HtmlContentFileException(string message)
        : base(message)
    {
    }
}
