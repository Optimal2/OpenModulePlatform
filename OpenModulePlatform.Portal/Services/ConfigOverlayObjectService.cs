// File: OpenModulePlatform.Portal/Services/ConfigOverlayObjectService.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Options;
using System.Text.Json;

namespace OpenModulePlatform.Portal.Services;

public sealed class ConfigOverlayObjectService
{
    private const int BufferSize = 1024 * 128;

    private readonly OmpAdminRepository _repo;
    private readonly ArtifactUploadOptions _options;
    private readonly ConfigOverlayPackageReader _reader = new();

    public ConfigOverlayObjectService(
        OmpAdminRepository repo,
        IOptions<ArtifactUploadOptions> options)
    {
        _repo = repo;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<AvailableHostConfigurationObject>> GetAvailableHostConfigurationsAsync(
        CancellationToken ct)
    {
        var root = ResolveOptionalRoot(_options.AvailableHostConfigurationsRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var rows = new List<AvailableHostConfigurationObject>();
        foreach (var path in EnumerateObjectFilesOrEmpty(root))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var kind = ConfigOverlayPackageReader.DetectFileKind(path);
                if (kind is not (PortableConfigObjectKind.HostConfiguration or PortableConfigObjectKind.HostConfigurationPackage))
                {
                    continue;
                }

                var document = await _reader.ReadHostConfigurationAsync(path, Path.GetFileName(path), ct);
                rows.Add(new AvailableHostConfigurationObject(
                    document.HostKey,
                    document.ConfigurationVersion,
                    Path.GetFileName(path)));
            }
            catch (IOException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
            catch (UnauthorizedAccessException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
            catch (InvalidDataException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
            catch (JsonException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
            catch (InvalidOperationException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
        }

        return rows
            .OrderBy(static row => row.HostKey, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(static row => row.ConfigurationVersion, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<AvailableConfigOverlayObject>> GetAvailableConfigOverlaysAsync(CancellationToken ct)
    {
        var root = ResolveOptionalRoot(_options.AvailableConfigOverlaysRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var rows = new List<AvailableConfigOverlayObject>();
        foreach (var path in EnumerateObjectFilesOrEmpty(root))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var kind = ConfigOverlayPackageReader.DetectFileKind(path);
                if (kind is not (PortableConfigObjectKind.ConfigOverlay or PortableConfigObjectKind.ConfigOverlayPackage))
                {
                    continue;
                }

                var document = await _reader.ReadConfigOverlayAsync(path, Path.GetFileName(path), ct);
                rows.Add(new AvailableConfigOverlayObject(
                    document.OverlayKey,
                    document.HostKey,
                    document.OverlayVersion,
                    document.ModuleKey,
                    document.AppKey,
                    document.ArtifactVersion,
                    Path.GetFileName(path)));
            }
            catch (IOException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
            catch (UnauthorizedAccessException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
            catch (InvalidDataException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
            catch (JsonException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
            catch (InvalidOperationException)
            {
                // Library scans ignore invalid files. Explicit imports still report detailed validation errors.
            }
        }

        return rows
            .OrderBy(static row => row.HostKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.AppKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.OverlayKey, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(static row => row.OverlayVersion, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ConfigObjectImportResult> ImportHostConfigurationFromLibraryAsync(
        string fileName,
        bool replaceExisting,
        CancellationToken ct)
    {
        var root = RequireConfiguredRoot(_options.AvailableHostConfigurationsRoot, "AvailableHostConfigurationsRoot");
        var path = ResolveChildFile(root, fileName);
        return await ImportHostConfigurationFileAsync(path, replaceExisting, ct);
    }

    public async Task<ConfigObjectImportResult> ImportConfigOverlayFromLibraryAsync(
        string fileName,
        bool replaceExisting,
        CancellationToken ct)
    {
        var root = RequireConfiguredRoot(_options.AvailableConfigOverlaysRoot, "AvailableConfigOverlaysRoot");
        var path = ResolveChildFile(root, fileName);
        return await ImportConfigOverlayFileAsync(path, replaceExisting, ct);
    }

    public async Task<ConfigObjectImportResult> ImportHostConfigurationUploadAsync(
        IFormFile file,
        bool replaceExisting,
        CancellationToken ct)
    {
        var path = await CopyUploadAsync(file, "portal-host-config-upload", ct);
        try
        {
            return await ImportHostConfigurationFileAsync(path, replaceExisting, ct);
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(path));
        }
    }

    public async Task<ConfigObjectImportResult> ImportConfigOverlayUploadAsync(
        IFormFile file,
        bool replaceExisting,
        CancellationToken ct)
    {
        var path = await CopyUploadAsync(file, "portal-config-overlay-upload", ct);
        try
        {
            return await ImportConfigOverlayFileAsync(path, replaceExisting, ct);
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(path));
        }
    }

    private async Task<ConfigObjectImportResult> ImportHostConfigurationFileAsync(
        string path,
        bool replaceExisting,
        CancellationToken ct)
    {
        var document = await _reader.ReadHostConfigurationAsync(path, Path.GetFileName(path), ct);
        var result = await _repo.SaveImportedHostConfigurationAsync(document, replaceExisting, ct);
        return new ConfigObjectImportResult(
            "host configuration",
            $"{document.HostKey} {document.ConfigurationVersion}",
            result.DocumentId,
            result.Created,
            result.Replaced,
            result.WasIdentical,
            0);
    }

    private async Task<ConfigObjectImportResult> ImportConfigOverlayFileAsync(
        string path,
        bool replaceExisting,
        CancellationToken ct)
    {
        var document = await _reader.ReadConfigOverlayAsync(path, Path.GetFileName(path), ct);
        var result = await _repo.SaveImportedConfigOverlayAsync(document, replaceExisting, ct);
        return new ConfigObjectImportResult(
            "config overlay",
            $"{document.OverlayKey} {document.OverlayVersion} ({document.HostKey})",
            result.DocumentId,
            result.Created,
            result.Replaced,
            result.WasIdentical,
            document.ConfigurationFiles.Count);
    }

    private async Task<string> CopyUploadAsync(IFormFile file, string prefix, CancellationToken ct)
    {
        if (file.Length <= 0)
        {
            throw new InvalidOperationException("Select a JSON or zip file to import.");
        }

        if (file.Length > _options.MaxUploadBytes)
        {
            throw new InvalidOperationException($"The uploaded file exceeds the limit of {_options.MaxUploadBytes} bytes.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Config object uploads must be .json or .zip files.");
        }

        var root = Path.Join(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Join(root, Path.GetFileName(file.FileName));
        await using var source = file.OpenReadStream();
        await using var target = File.Create(path);
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > _options.MaxUploadBytes)
            {
                throw new InvalidOperationException($"The uploaded file exceeds the limit of {_options.MaxUploadBytes} bytes.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return path;
    }

    private static IEnumerable<string> EnumerateObjectFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> EnumerateObjectFilesOrEmpty(string root)
    {
        try
        {
            return EnumerateObjectFiles(root).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Library scans are optional for the import/export page. Explicit
            // imports still validate paths and report detailed errors.
            return [];
        }
    }

    private static string ResolveOptionalRoot(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value.Trim());

    private static string RequireConfiguredRoot(string? value, string settingName)
    {
        var root = ResolveOptionalRoot(value);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException($"ArtifactUpload:{settingName} is not configured or does not exist.");
        }

        return root;
    }

    private static string ResolveChildFile(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Select a file from the package library.");
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, Path.GetFileName(fileName)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, comparison) || !File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected config object was not found in the configured library.", fileName);
        }

        return fullPath;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Temporary cleanup is best-effort; upload validation has already completed or failed.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary cleanup is best-effort; upload validation has already completed or failed.
        }
    }

}

public sealed record AvailableHostConfigurationObject(
    string HostKey,
    string ConfigurationVersion,
    string FileName);

public sealed record AvailableConfigOverlayObject(
    string OverlayKey,
    string HostKey,
    string OverlayVersion,
    string? ModuleKey,
    string? AppKey,
    string? ArtifactVersion,
    string FileName);

public sealed record ConfigObjectImportResult(
    string ObjectKind,
    string DisplayName,
    int DocumentId,
    bool Created,
    bool Replaced,
    bool WasIdentical,
    int ConfigurationFileCount);
