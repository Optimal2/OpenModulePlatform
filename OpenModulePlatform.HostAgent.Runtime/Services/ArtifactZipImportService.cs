using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class ArtifactZipImportService
{
    private const int HashBufferSize = 1024 * 128;
    private const int MaxModuleDefinitionBytes = 1024 * 1024 * 5;

    private static readonly Regex MetadataTokenPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._+-]*$",
        RegexOptions.Compiled);

    private static readonly string[] RuntimeConfigurationFileNames =
    [
        "odv.site.config.js"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly OmpHostArtifactRepository _repository;
    private readonly ILogger<ArtifactZipImportService> _logger;

    public ArtifactZipImportService(
        IOptionsMonitor<HostAgentSettings> settings,
        OmpHostArtifactRepository repository,
        ILogger<ArtifactZipImportService> logger)
    {
        _settings = settings;
        _repository = repository;
        _logger = logger;
    }

    public async Task ImportPendingAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var importSettings = settings.ArtifactZipImport;
        if (!importSettings.IsEnabled)
        {
            return;
        }

        var importPath = Path.GetFullPath(importSettings.ImportPath.Trim());
        var processedPath = Path.GetFullPath(importSettings.ResolveProcessedPath());
        var failedPath = Path.GetFullPath(importSettings.ResolveFailedPath());

        Directory.CreateDirectory(importPath);
        Directory.CreateDirectory(processedPath);
        Directory.CreateDirectory(failedPath);

        var importPaths = Directory.EnumerateFiles(importPath, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(importSettings.MaxFilesPerCycle)
            .ToList();

        foreach (var path in importPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ImportOneAsync(settings, importSettings, path, processedPath, failedPath, cancellationToken);
        }
    }

    private async Task ImportOneAsync(
        HostAgentSettings settings,
        HostAgentArtifactZipImportSettings importSettings,
        string importPath,
        string processedPath,
        string failedPath,
        CancellationToken cancellationToken)
    {
        var storeRoot = Path.GetFullPath(settings.CentralArtifactRoot.Trim());
        var tempRoot = Path.Combine(storeRoot, ".hostagent-import-staging");
        Directory.CreateDirectory(tempRoot);

        var tempImportPath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}{Path.GetExtension(importPath)}");
        var stagingPath = Path.Combine(tempRoot, $"import-{Guid.NewGuid():N}");

        try
        {
            if (!await TryCopyReadyFileAsync(importPath, tempImportPath, cancellationToken))
            {
                return;
            }

            var extension = Path.GetExtension(importPath);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var definition = await ReadModuleDefinitionAsync(
                    tempImportPath,
                    Path.GetFileName(importPath),
                    cancellationToken);
                var result = await ImportModuleDefinitionAsync(definition, cancellationToken);
                _logger.LogInformation(
                    "Imported module definition from HostAgent import folder. File={ImportPath}, Module={ModuleKey}, Version={DefinitionVersion}, DocumentId={DocumentId}, Applied={Applied}, SqlRepairCount={SqlRepairCount}",
                    importPath,
                    result.ModuleKey,
                    result.DefinitionVersion,
                    result.ModuleDefinitionDocumentId,
                    result.Applied,
                    result.SqlRepairCount);
            }
            else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                     && TryParseFilenameMetadata(Path.GetFileName(importPath)) is { } metadata)
            {
                var result = await ImportArtifactPackageAsync(
                    settings,
                    importSettings,
                    metadata,
                    tempImportPath,
                    Path.Combine(stagingPath, "artifact"),
                    cancellationToken);
                _logger.LogInformation(
                    "Imported artifact zip. Zip={ImportPath}, ArtifactId={ArtifactId}, Version={Version}, RelativePath={RelativePath}, AdoptedExistingContent={AdoptedExistingContent}, CopiedConfigurationFiles={CopiedConfigurationFiles}, TemplateRows={TemplateRows}, AppInstanceRows={AppInstanceRows}, WorkerInstanceRows={WorkerInstanceRows}, HostAgentDesiredRows={HostAgentDesiredRows}",
                    importPath,
                    result.ArtifactId,
                    result.Version,
                    result.RelativePath,
                    result.AdoptedExistingContent,
                    result.CopiedConfigurationFileCount,
                    result.TemplateAppRowsUpdated,
                    result.AppInstanceRowsUpdated,
                    result.WorkerInstanceRowsUpdated,
                    result.HostAgentDesiredRowsUpdated);
            }
            else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ImportModulePackageZipAsync(
                    settings,
                    importSettings,
                    tempImportPath,
                    Path.Combine(stagingPath, "module-package"),
                    cancellationToken);
                _logger.LogInformation(
                    "Imported module package from HostAgent import folder. File={ImportPath}, Module={ModuleKey}, Version={DefinitionVersion}, DocumentId={DocumentId}, Applied={Applied}, SqlRepairCount={SqlRepairCount}, ArtifactCount={ArtifactCount}",
                    importPath,
                    result.ModuleKey,
                    result.DefinitionVersion,
                    result.ModuleDefinitionDocumentId,
                    result.Applied,
                    result.SqlRepairCount,
                    result.Artifacts.Count);
            }
            else
            {
                throw new InvalidOperationException(
                    "Unsupported HostAgent import file. Expected .json module definition, standard artifact .zip, or module package .zip.");
            }

            MoveImportFile(importPath, processedPath, null);
        }
        catch (Exception ex) when (IsExpectedImportFailure(ex))
        {
            MoveImportFile(importPath, failedPath, ex.Message);
            _logger.LogWarning(ex, "HostAgent import failed. File={ImportPath}", importPath);
        }
        finally
        {
            TryDelete(tempImportPath);
            TryDelete(stagingPath);
        }
    }

    private async Task<bool> TryCopyReadyFileAsync(
        string importPath,
        string tempImportPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(importPath, FileMode.Open, FileAccess.Read, FileShare.None);
            await using var target = new FileStream(tempImportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "HostAgent import file is not ready. File={ImportPath}", importPath);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "HostAgent import file is not accessible. File={ImportPath}", importPath);
            return false;
        }
    }

    private async Task<ModulePackageImportResult> ImportModulePackageZipAsync(
        HostAgentSettings settings,
        HostAgentArtifactZipImportSettings importSettings,
        string packageZipPath,
        string extractionRoot,
        CancellationToken cancellationToken)
    {
        ExtractZipToDirectory(packageZipPath, extractionRoot);
        var definitionPath = FindSingleModuleDefinitionFile(extractionRoot);
        var definition = await ReadModuleDefinitionAsync(
            definitionPath,
            Path.GetFileName(definitionPath),
            cancellationToken,
            extractionRoot);

        var definitionResult = await ImportModuleDefinitionAsync(definition, cancellationToken);
        var artifactResults = new List<ArtifactZipImportResult>();
        var artifactPaths = Directory.EnumerateFiles(extractionRoot, "*.zip", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var artifactPath in artifactPaths)
        {
            var metadata = TryParseFilenameMetadata(Path.GetFileName(artifactPath))
                ?? throw new InvalidOperationException(
                    $"Module package artifact '{Path.GetFileName(artifactPath)}' does not follow moduleKey__appKey__packageType__targetName__version.zip.");
            if (!metadata.ModuleKey.Equals(definition.ModuleKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Module package artifact '{Path.GetFileName(artifactPath)}' belongs to module '{metadata.ModuleKey}', not '{definition.ModuleKey}'.");
            }

            artifactResults.Add(await ImportArtifactPackageAsync(
                settings,
                importSettings,
                metadata,
                artifactPath,
                Path.Combine(extractionRoot, ".artifact-staging", Guid.NewGuid().ToString("N")),
                cancellationToken));
        }

        return new ModulePackageImportResult(
            definitionResult.ModuleKey,
            definitionResult.DefinitionVersion,
            definitionResult.ModuleDefinitionDocumentId,
            definitionResult.Applied,
            definitionResult.SqlRepairCount,
            artifactResults);
    }

    private async Task<ModuleDefinitionImportResult> ImportModuleDefinitionAsync(
        ModuleDefinitionImportDocument definition,
        CancellationToken cancellationToken)
    {
        var saveResult = await _repository.SaveImportedModuleDefinitionAsync(
            definition,
            replaceExisting: false,
            cancellationToken);
        var applied = await _repository.ApplyImportedModuleDefinitionAsync(
            saveResult.ModuleDefinitionDocumentId,
            cancellationToken);
        var repairs = applied
            ? await _repository.ExecuteImportedModuleDefinitionSqlRepairsAsync(
                saveResult.ModuleDefinitionDocumentId,
                cancellationToken)
            : 0;

        return new ModuleDefinitionImportResult(
            definition.ModuleKey,
            definition.DefinitionVersion,
            saveResult.ModuleDefinitionDocumentId,
            applied,
            repairs);
    }

    private async Task<ArtifactZipImportResult> ImportArtifactPackageAsync(
        HostAgentSettings settings,
        HostAgentArtifactZipImportSettings importSettings,
        FilenameMetadata metadata,
        string packagePath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var storeRoot = Path.GetFullPath(settings.CentralArtifactRoot.Trim());
        string? finalPath = null;
        var movedToFinal = false;
        var artifactRegistered = false;
        var adoptedExistingContent = false;

        try
        {
            var app = await _repository.ResolveArtifactZipImportAppAsync(
                metadata.ModuleKey,
                metadata.AppKey,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"No enabled app was found for module '{metadata.ModuleKey}' and app '{metadata.AppKey}'.");

            var compatibility = await _repository.RequireCompatibleArtifactSlotAsync(
                app.AppId,
                metadata.Version,
                metadata.PackageType,
                metadata.TargetName,
                cancellationToken);

            var relativePath = BuildRelativePath(
                compatibility,
                metadata.TargetName,
                metadata.PackageType,
                metadata.Version);

            var existingIdentity = await _repository.FindImportedArtifactByIdentityAsync(
                app.AppId,
                metadata.Version,
                metadata.PackageType,
                metadata.TargetName,
                cancellationToken);
            if (existingIdentity is not null)
            {
                throw new InvalidOperationException(
                    "An artifact for this app, package type, target, and version already exists: " +
                    $"{existingIdentity.AppKey} {existingIdentity.Version} ({existingIdentity.PackageType}). " +
                    "Use a new version number for changed artifact content.");
            }

            finalPath = ResolveUnderRoot(storeRoot, relativePath);
            var package = new ArtifactPackageExtractor(ValidateArtifactEntryIsNotRuntimeConfiguration)
                .Extract(packagePath, stagingPath);
            var contentHash = await ComputeDirectorySha256Async(package.ArtifactContentPath, cancellationToken);
            if (Directory.Exists(finalPath) || File.Exists(finalPath))
            {
                if (File.Exists(finalPath))
                {
                    throw new InvalidOperationException(
                        $"The target artifact path already exists as a file below the artifact store: {relativePath}.");
                }

                var existingContentHash = await ComputeDirectorySha256Async(finalPath, cancellationToken);
                if (!string.Equals(existingContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The target artifact path already exists with different content below the artifact store: {relativePath}.");
                }

                adoptedExistingContent = true;
            }

            if (!adoptedExistingContent)
            {
                var duplicate = await _repository.FindImportedArtifactBySha256Async(contentHash, cancellationToken);
                if (duplicate is not null)
                {
                    throw new InvalidOperationException(
                        $"An artifact with identical extracted content already exists: {duplicate.AppKey} {duplicate.Version} ({duplicate.PackageType}).");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                MoveDirectory(package.ArtifactContentPath, finalPath);
                movedToFinal = true;
            }

            var artifactId = await _repository.RegisterImportedArtifactAsync(
                app.AppId,
                metadata.Version,
                metadata.PackageType,
                metadata.TargetName,
                relativePath,
                contentHash,
                cancellationToken);

            artifactRegistered = true;
            var copiedConfigurationFiles = 0;
            if (package.ConfigurationFiles.Count > 0)
            {
                copiedConfigurationFiles = await _repository.ReplaceArtifactConfigurationFilesAsync(
                    artifactId,
                    package.ConfigurationFiles,
                    cancellationToken);
            }
            else if (importSettings.CopyConfigurationFilesFromPreviousVersion)
            {
                copiedConfigurationFiles = await _repository.CopyConfigurationFilesFromLatestPreviousArtifactAsync(
                    artifactId,
                    app.AppId,
                    metadata.PackageType,
                    metadata.TargetName,
                    cancellationToken);
            }

            var application = await _repository.ApplyImportedArtifactToMatchingApplicationsAsync(
                artifactId,
                cancellationToken);
            var hostAgentDesiredRows = 0;
            if (string.Equals(metadata.PackageType, "host-agent", StringComparison.OrdinalIgnoreCase))
            {
                hostAgentDesiredRows = await _repository.ApplyImportedHostAgentArtifactToCurrentHostAsync(
                    artifactId,
                    settings.ResolveHostKey(),
                    settings.SelfUpgrade.ServiceNamePrefix,
                    settings.SelfUpgrade.InstallRoot,
                    cancellationToken);
            }

            return new ArtifactZipImportResult(
                artifactId,
                metadata.Version,
                relativePath,
                copiedConfigurationFiles,
                application.TemplateAppRowsUpdated,
                application.AppInstanceRowsUpdated,
                application.WorkerInstanceRowsUpdated,
                hostAgentDesiredRows,
                adoptedExistingContent);
        }
        catch
        {
            if (movedToFinal && !artifactRegistered && !string.IsNullOrWhiteSpace(finalPath))
            {
                TryDelete(finalPath);
            }

            throw;
        }
    }

    private static void ValidateArtifactEntryIsNotRuntimeConfiguration(string normalizedEntryName)
    {
        var fileName = normalizedEntryName.Split('/').LastOrDefault() ?? string.Empty;
        if (IsRuntimeConfigurationFileName(fileName))
        {
            throw new InvalidOperationException(
                $"The artifact zip contains runtime configuration file '{normalizedEntryName}'. Store runtime configuration in artifact configuration file rows instead.");
        }
    }

    private static bool IsRuntimeConfigurationFileName(string fileName)
    {
        if (string.Equals(fileName, "appsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return RuntimeConfigurationFileNames.Any(
            name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ComputeDirectorySha256Async(string path, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relative);
            sha.TransformBlock(relativeBytes, 0, relativeBytes.Length, null, 0);

            var separator = new byte[] { 0 };
            sha.TransformBlock(separator, 0, separator.Length, null, 0);

            await using var stream = File.OpenRead(file);
            var buffer = new byte[HashBufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static async Task<ModuleDefinitionImportDocument> ReadModuleDefinitionAsync(
        string path,
        string sourceName,
        CancellationToken cancellationToken,
        string? externalSqlRoot = null)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxModuleDefinitionBytes)
        {
            throw new InvalidOperationException(
                $"The module definition exceeds the limit of {MaxModuleDefinitionBytes} bytes.");
        }

        var jsonText = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        var root = JsonNode.Parse(jsonText)
            ?? throw new InvalidOperationException("The module definition JSON file is empty.");
        if (!string.IsNullOrWhiteSpace(externalSqlRoot))
        {
            root = ModuleDefinitionPackageNormalizer.NormalizeExternalSqlFiles(
                root,
                path,
                externalSqlRoot);
        }

        var moduleKey = GetJsonStringProperty(root, "moduleKey");
        var definitionVersion = GetJsonStringProperty(root, "definitionVersion");
        if (string.IsNullOrWhiteSpace(moduleKey) || string.IsNullOrWhiteSpace(definitionVersion))
        {
            throw new InvalidOperationException("Module definition JSON must contain moduleKey and definitionVersion.");
        }

        var normalizedJson = root.ToJsonString(JsonOptions);
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedJson))).ToLowerInvariant();
        return new ModuleDefinitionImportDocument(
            moduleKey,
            definitionVersion,
            GetJsonIntProperty(root, "formatVersion", 1),
            normalizedJson,
            sha256,
            Truncate(sourceName, 400),
            ReadCompatibleArtifacts(root));
    }

    private static IReadOnlyList<ModuleDefinitionArtifactCompatibilityEntry> ReadCompatibleArtifacts(JsonNode root)
    {
        if (root["compatibleArtifacts"] is not JsonArray items)
        {
            return [];
        }

        var entries = new List<ModuleDefinitionArtifactCompatibilityEntry>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var appKey = GetJsonStringProperty(item, "appKey");
            var packageType = GetJsonStringProperty(item, "packageType");
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(packageType))
            {
                throw new InvalidOperationException("Each compatibleArtifacts item must contain appKey and packageType.");
            }

            entries.Add(new ModuleDefinitionArtifactCompatibilityEntry(
                appKey,
                packageType,
                NullIfWhiteSpace(GetJsonStringProperty(item, "targetName")),
                NullIfWhiteSpace(GetJsonStringProperty(item, "relativePathTemplate")),
                NullIfWhiteSpace(GetJsonStringProperty(item, "minVersion")),
                NullIfWhiteSpace(GetJsonStringProperty(item, "maxVersion"))));
        }

        return entries;
    }

    private static string FindSingleModuleDefinitionFile(string root)
    {
        var candidates = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(static path => TryReadDefinitionSummary(path) is not null)
            .ToList();

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException("The module package zip contains no module definition JSON file."),
            _ => throw new InvalidOperationException("The module package zip contains multiple module definition JSON files.")
        };
    }

    private static (string ModuleKey, string DefinitionVersion)? TryReadDefinitionSummary(string path)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (root is null)
            {
                return null;
            }

            var moduleKey = GetJsonStringProperty(root, "moduleKey");
            var definitionVersion = GetJsonStringProperty(root, "definitionVersion");
            return string.IsNullOrWhiteSpace(moduleKey) || string.IsNullOrWhiteSpace(definitionVersion)
                ? null
                : (moduleKey, definitionVersion);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FilenameMetadata? TryParseFilenameMetadata(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = name.Split("__", StringSplitOptions.None);
        if (parts.Length != 5
            || parts.Any(part => part.Length == 0 || !MetadataTokenPattern.IsMatch(part)))
        {
            return null;
        }

        return new FilenameMetadata(parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    private static string BuildDefaultRelativePath(string targetName, string packageType, string version)
    {
        var sanitizedTarget = SanitizePathSegment(targetName);
        var packageSegment = GetPackagePathSegment(packageType);
        var rootSegment = sanitizedTarget;
        var typedTargetSuffix = "-" + packageSegment;

        if (packageSegment is "web" or "service"
            && sanitizedTarget.EndsWith(typedTargetSuffix, StringComparison.OrdinalIgnoreCase))
        {
            rootSegment = sanitizedTarget[..^typedTargetSuffix.Length];
        }
        else if (packageSegment == "service"
            && sanitizedTarget.EndsWith("-backend", StringComparison.OrdinalIgnoreCase))
        {
            rootSegment = sanitizedTarget[..^"-backend".Length];
            packageSegment = "backend";
        }

        return $"{rootSegment}/{packageSegment}/{SanitizePathSegment(version)}";
    }

    private static string BuildRelativePath(
        ArtifactCompatibilitySlot compatibility,
        string targetName,
        string packageType,
        string version)
    {
        if (string.IsNullOrWhiteSpace(compatibility.RelativePathTemplate))
        {
            return BuildDefaultRelativePath(targetName, packageType, version);
        }

        return compatibility.RelativePathTemplate.Trim()
            .Replace("{moduleKey}", SanitizePathSegment(compatibility.ModuleKey), StringComparison.OrdinalIgnoreCase)
            .Replace("{appKey}", SanitizePathSegment(compatibility.AppKey), StringComparison.OrdinalIgnoreCase)
            .Replace("{targetName}", SanitizePathSegment(targetName), StringComparison.OrdinalIgnoreCase)
            .Replace("{packageType}", GetPackagePathSegment(packageType), StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", SanitizePathSegment(version), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPackagePathSegment(string packageType)
        => packageType.Trim().ToLowerInvariant() switch
        {
            "web-app" => "web",
            "service-app" => "service",
            "host-agent" => "hostagent",
            "worker-host" => "host",
            "worker" => "worker",
            "worker-plugin" => "worker",
            "channel-type" => "channel-type",
            var value => SanitizePathSegment(value)
        };

    private static string SanitizePathSegment(string value)
    {
        var sanitized = value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }

        return sanitized.Replace(' ', '-');
    }

    private static string ResolveUnderRoot(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Expected a relative artifact path.");
        }

        var fullRoot = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, comparison))
        {
            throw new InvalidOperationException("The artifact path escapes the configured artifact store root.");
        }

        return fullPath;
    }

    private static void MoveImportFile(string importPath, string destinationRoot, string? errorMessage)
    {
        try
        {
            Directory.CreateDirectory(destinationRoot);
            var destination = Path.Combine(
                destinationRoot,
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Path.GetFileName(importPath)}");

            if (File.Exists(importPath))
            {
                MoveFile(importPath, destination);
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                File.WriteAllText(
                    destination + ".error.txt",
                    errorMessage.Trim(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (IOException)
        {
            // The next HostAgent cycle can retry or reclassify a file that
            // could not be moved after processing.
        }
        catch (UnauthorizedAccessException)
        {
            // The next HostAgent cycle can retry or reclassify a file that
            // could not be moved after processing.
        }
    }

    private static bool IsExpectedImportFailure(Exception exception)
        => exception is InvalidOperationException
            or InvalidDataException
            or JsonException
            or IOException
            or SqlException
            or UnauthorizedAccessException;

    private static void ExtractZipToDirectory(string zipPath, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var entryName = NormalizeZipEntryName(entry.FullName);
            var destinationPath = ResolveUnderRoot(destinationRoot, entryName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: false);
        }
    }

    private static string NormalizeZipEntryName(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').Trim();
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The zip package contains an invalid entry path.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0)
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("The zip package contains a path that escapes the package root.");
        }

        return string.Join('/', segments);
    }

    private static void MoveDirectory(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (HaveSameRoot(source, destination))
        {
            Directory.Move(source, destination);
            return;
        }

        try
        {
            CopyDirectory(source, destination);
            Directory.Delete(source, recursive: true);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private static void MoveFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (HaveSameRoot(source, destination))
        {
            File.Move(source, destination, overwrite: false);
            return;
        }

        File.Copy(source, destination, overwrite: false);
        File.Delete(source);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relativeDirectory = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relativeDirectory));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativeFile = Path.GetRelativePath(source, file);
            var targetFile = Path.Combine(destination, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: false);
        }
    }

    private static bool HaveSameRoot(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.GetPathRoot(Path.GetFullPath(first)),
            Path.GetPathRoot(Path.GetFullPath(second)),
            comparison);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup after a failed import.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup after a failed import.
        }
    }

    private static string GetJsonStringProperty(JsonNode node, string propertyName)
    {
        if (node is not JsonObject obj
            || !obj.TryGetPropertyValue(propertyName, out var value)
            || value is null)
        {
            return string.Empty;
        }

        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;
    }

    private static int GetJsonIntProperty(JsonNode node, string propertyName, int defaultValue)
    {
        if (node is not JsonObject obj
            || !obj.TryGetPropertyValue(propertyName, out var value)
            || value is not JsonValue jsonValue)
        {
            return defaultValue;
        }

        if (jsonValue.TryGetValue<int>(out var number))
        {
            return number;
        }

        return jsonValue.TryGetValue<string>(out var text)
            && int.TryParse(text, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private sealed record FilenameMetadata(
        string ModuleKey,
        string AppKey,
        string PackageType,
        string TargetName,
        string Version);
}
