// File: OpenModulePlatform.Portal/Services/PortableModulePackageService.cs
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Options;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Imports and exports portable module packages: one module definition JSON plus
/// one or more standard artifact package zips.
/// </summary>
public sealed class PortableModulePackageService
{
    private const int BufferSize = 1024 * 128;
    private const int MaxDefinitionBytes = 1024 * 1024 * 5;
    private const string ModuleDefinitionFolder = "module-definition";
    private const string ArtifactsFolder = "artifacts";

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

    private readonly OmpAdminRepository _repo;
    private readonly ArtifactUploadOptions _options;

    public PortableModulePackageService(
        OmpAdminRepository repo,
        IOptions<ArtifactUploadOptions> options)
    {
        _repo = repo;
        _options = options.Value;
    }

    public IReadOnlyList<AvailablePortableModulePackage> GetAvailablePackages()
    {
        var definitionsRoot = ResolveOptionalRoot(_options.AvailableModuleDefinitionsRoot);
        var artifactsRoot = ResolveOptionalRoot(_options.AvailableArtifactsRoot);
        var artifactFiles = Directory.Exists(artifactsRoot)
            ? Directory.EnumerateFiles(artifactsRoot, "*.zip", SearchOption.TopDirectoryOnly)
                .Select(TryReadArtifactPackageFile)
                .Where(static item => item is not null)
                .Select(static item => item!)
                .ToList()
            : [];

        if (!Directory.Exists(definitionsRoot))
        {
            return [];
        }

        var packages = new List<AvailablePortableModulePackage>();
        foreach (var path in Directory.EnumerateFiles(definitionsRoot, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
        {
            var definition = TryReadDefinitionSummary(path);
            if (definition is null)
            {
                continue;
            }

            var artifacts = artifactFiles
                .Where(file => file.ModuleKey.Equals(definition.ModuleKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static file => file.AppKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static file => file.PackageType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static file => file.TargetName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static file => file.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();

            packages.Add(new AvailablePortableModulePackage(
                definition.ModuleKey,
                definition.DefinitionVersion,
                Path.GetFileName(path),
                artifacts));
        }

        return packages;
    }

    public async Task<PortableModulePackageImportResult> ImportFromLibraryAsync(
        string moduleDefinitionFileName,
        PortableModulePackageImportOptions options,
        CancellationToken ct)
    {
        var definitionsRoot = RequireConfiguredRoot(_options.AvailableModuleDefinitionsRoot, "AvailableModuleDefinitionsRoot");
        var artifactsRoot = RequireConfiguredRoot(_options.AvailableArtifactsRoot, "AvailableArtifactsRoot");
        var definitionPath = ResolveChildFile(definitionsRoot, moduleDefinitionFileName, ".json");
        var definition = await ReadDefinitionAsync(definitionPath, Path.GetFileName(definitionPath), ct);

        var artifactPaths = Directory.Exists(artifactsRoot)
            ? Directory.EnumerateFiles(artifactsRoot, $"{definition.ModuleKey}__*.zip", SearchOption.TopDirectoryOnly)
                .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        return await ImportAsync(definition, artifactPaths, options, ct);
    }

    public async Task<PortableModulePackageImportResult> ImportUploadsAsync(
        IFormFile? bundleFile,
        IFormFile? definitionFile,
        IReadOnlyList<IFormFile> artifactFiles,
        PortableModulePackageImportOptions options,
        CancellationToken ct)
    {
        var tempRoot = CreateTempRoot("portal-module-package-upload");

        try
        {
            if (bundleFile is not null && bundleFile.Length > 0)
            {
                var bundlePath = Path.Combine(tempRoot, Path.GetFileName(bundleFile.FileName));
                await CopyUploadToFileAsync(bundleFile, bundlePath, _options.MaxUploadBytes, ct);
                var extractedRoot = Path.Combine(tempRoot, "bundle");
                ZipFile.ExtractToDirectory(bundlePath, extractedRoot, overwriteFiles: true);

                var definitionPath = FindSingleModuleDefinitionFile(extractedRoot);
                var definition = await ReadDefinitionAsync(definitionPath, Path.GetFileName(definitionPath), ct);
                var packagePaths = Directory.EnumerateFiles(extractedRoot, "*.zip", SearchOption.AllDirectories)
                    .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(bundlePath), StringComparison.OrdinalIgnoreCase))
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return await ImportAsync(definition, packagePaths, options, ct);
            }

            if (definitionFile is null || definitionFile.Length == 0)
            {
                throw new InvalidOperationException("Select either one portable module package zip or one module definition JSON file.");
            }

            var definitionUploadPath = Path.Combine(tempRoot, Path.GetFileName(definitionFile.FileName));
            await CopyUploadToFileAsync(definitionFile, definitionUploadPath, MaxDefinitionBytes, ct);
            var uploadedDefinition = await ReadDefinitionAsync(
                definitionUploadPath,
                Path.GetFileName(definitionFile.FileName),
                ct);

            var packageUploadPaths = new List<string>();
            foreach (var file in artifactFiles.Where(static file => file.Length > 0))
            {
                if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Artifact package uploads must be .zip files.");
                }

                var packagePath = Path.Combine(tempRoot, "artifacts", Path.GetFileName(file.FileName));
                await CopyUploadToFileAsync(file, packagePath, _options.MaxUploadBytes, ct);
                packageUploadPaths.Add(packagePath);
            }

            return await ImportAsync(uploadedDefinition, packageUploadPaths, options, ct);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    public async Task<PortableModulePackageExportResult> ExportModulePackageAsync(
        string moduleKey,
        bool includeAllArtifactVersions,
        CancellationToken ct)
    {
        var definition = await _repo.GetAppliedModuleDefinitionDocumentAsync(moduleKey, ct)
            ?? throw new InvalidOperationException($"No applied module definition exists for module '{moduleKey}'.");
        if (string.IsNullOrWhiteSpace(definition.DefinitionJson))
        {
            throw new InvalidOperationException($"Applied module definition '{moduleKey}' has no JSON content to export.");
        }

        var artifactRows = await _repo.GetModuleArtifactPackagesAsync(moduleKey, includeAllArtifactVersions, ct);
        var storeRoot = RequireConfiguredRoot(_options.ArtifactStoreRoot, "ArtifactStoreRoot");
        var tempRoot = CreateTempRoot("portal-module-package-export");
        var packageFileName = $"{SanitizePathSegment(moduleKey)}__module-package__{SanitizePathSegment(definition.DefinitionVersion)}.zip";
        var downloadRoot = Path.Combine(Path.GetTempPath(), "OpenModulePlatform", "portal-module-package-downloads");
        Directory.CreateDirectory(downloadRoot);
        var packagePath = Path.Combine(downloadRoot, $"{Guid.NewGuid():N}-{packageFileName}");

        try
        {
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var definitionEntry = archive.CreateEntry(
                    $"{ModuleDefinitionFolder}/{SanitizePathSegment(moduleKey)}.module-definition.json",
                    CompressionLevel.Optimal);
                await using (var entryStream = definitionEntry.Open())
                await using (var writer = new StreamWriter(entryStream, new UTF8Encoding(false)))
                {
                    await writer.WriteAsync(definition.DefinitionJson.AsMemory(), ct);
                }

                foreach (var artifact in artifactRows)
                {
                    if (string.IsNullOrWhiteSpace(artifact.RelativePath)
                        || string.IsNullOrWhiteSpace(artifact.TargetName))
                    {
                        continue;
                    }

                    var normalizedRelativePath = NormalizeRelativePath(artifact.RelativePath);
                    if (normalizedRelativePath is null)
                    {
                        continue;
                    }

                    var payloadPath = ResolveUnderRoot(storeRoot, normalizedRelativePath);
                    if (!Directory.Exists(payloadPath))
                    {
                        continue;
                    }

                    var configurationFiles = await _repo.GetArtifactConfigurationFileContentsAsync(artifact.ArtifactId, ct);
                    var artifactPackagePath = Path.Combine(
                        tempRoot,
                        $"{Guid.NewGuid():N}.artifact.zip");

                    new ArtifactPackageWriter().CreateFromPayloadDirectory(
                        payloadPath,
                        artifactPackagePath,
                        configurationFiles
                            .Select(file => new ArtifactPackageConfigurationFile(file.RelativePath, file.FileContent))
                            .ToArray());

                    var artifactFileName = CreateArtifactPackageFileName(
                        artifact.ModuleKey,
                        artifact.AppKey,
                        artifact.PackageType,
                        artifact.TargetName,
                        artifact.Version);
                    archive.CreateEntryFromFile(
                        artifactPackagePath,
                        $"{ArtifactsFolder}/{artifactFileName}",
                        CompressionLevel.Optimal);
                    File.Delete(artifactPackagePath);
                }
            }

            TryDelete(tempRoot);
            return new PortableModulePackageExportResult(packagePath, packageFileName);
        }
        catch
        {
            TryDelete(tempRoot);
            TryDelete(packagePath);
            throw;
        }
    }

    private async Task<PortableModulePackageImportResult> ImportAsync(
        ModuleDefinitionDocumentEditData definition,
        IReadOnlyList<string> artifactPaths,
        PortableModulePackageImportOptions options,
        CancellationToken ct)
    {
        var saveResult = await _repo.SaveModuleDefinitionDocumentAsync(
            definition,
            options.ReplaceExistingModuleDefinition,
            ct);

        var applied = false;
        var repairCount = 0;
        if (options.ApplyModuleDefinition)
        {
            var applyResult = await _repo.ApplyModuleDefinitionDocumentAsync(
                saveResult.ModuleDefinitionDocumentId,
                options.AllowTemporaryIncompatibleArtifacts,
                ct);
            applied = applyResult.Applied;

            if (applied && options.ExecuteSqlRepairs)
            {
                var repairResult = await _repo.ExecuteModuleDefinitionSqlRepairsAsync(
                    saveResult.ModuleDefinitionDocumentId,
                    ct);
                repairCount = repairResult.ExecutedCount;
            }
        }

        var artifactResults = new List<PortableModulePackageArtifactImportResult>();
        foreach (var artifactPath in artifactPaths)
        {
            if (TryReadArtifactPackageFile(artifactPath) is not { } identity)
            {
                artifactResults.Add(new PortableModulePackageArtifactImportResult(
                    Path.GetFileName(artifactPath),
                    "Skipped",
                    "The artifact package filename does not follow the standard module__app__packageType__target__version.zip format.",
                    null));
                continue;
            }

            if (!identity.ModuleKey.Equals(definition.ModuleKey, StringComparison.OrdinalIgnoreCase))
            {
                artifactResults.Add(new PortableModulePackageArtifactImportResult(
                    Path.GetFileName(artifactPath),
                    "Skipped",
                    $"The artifact belongs to module '{identity.ModuleKey}', not '{definition.ModuleKey}'.",
                    null));
                continue;
            }

            artifactResults.Add(await ImportArtifactPackageAsync(identity, artifactPath, options, ct));
        }

        return new PortableModulePackageImportResult(
            definition.ModuleKey,
            definition.DefinitionVersion,
            saveResult.ModuleDefinitionDocumentId,
            applied,
            repairCount,
            artifactResults);
    }

    private async Task<PortableModulePackageArtifactImportResult> ImportArtifactPackageAsync(
        ArtifactPackageFile identity,
        string packagePath,
        PortableModulePackageImportOptions options,
        CancellationToken ct)
    {
        var appOptions = await _repo.GetArtifactAppOptionsAsync(ct);
        var app = appOptions.FirstOrDefault(option =>
            option.ModuleKey.Equals(identity.ModuleKey, StringComparison.OrdinalIgnoreCase)
            && option.AppKey.Equals(identity.AppKey, StringComparison.OrdinalIgnoreCase));
        if (app is null)
        {
            return new PortableModulePackageArtifactImportResult(
                identity.FileName,
                "Failed",
                $"The module/app '{identity.ModuleKey}/{identity.AppKey}' is not registered. Apply the module definition and execute SQL repairs first.",
                null);
        }

        ArtifactCompatibilitySlot compatibility;
        try
        {
            compatibility = await _repo.RequireCompatibleArtifactSlotAsync(
                app.AppId,
                identity.Version,
                identity.PackageType,
                identity.TargetName,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return new PortableModulePackageArtifactImportResult(identity.FileName, "Failed", ex.Message, null);
        }

        var storeRoot = RequireConfiguredRoot(_options.ArtifactStoreRoot, "ArtifactStoreRoot");
        var tempRoot = CreateTempRoot("portal-artifact-package-import");
        var stagingPath = Path.Combine(tempRoot, "artifact");
        var backupPath = Path.Combine(tempRoot, "backup");
        var movedExistingToBackup = false;
        var movedArtifactToFinal = false;
        var adoptedExistingContent = false;
        var finalPath = string.Empty;

        try
        {
            var package = new ArtifactPackageExtractor(ValidateArtifactEntryIsNotRuntimeConfiguration)
                .Extract(packagePath, stagingPath);
            var contentHash = await ComputeDirectorySha256Async(package.ArtifactContentPath, ct);

            var existingIdentity = await _repo.FindArtifactByIdentityAsync(
                app.AppId,
                identity.Version,
                identity.PackageType,
                identity.TargetName,
                ct);

            if (existingIdentity is not null
                && string.Equals(existingIdentity.Sha256, contentHash, StringComparison.OrdinalIgnoreCase))
            {
                return new PortableModulePackageArtifactImportResult(
                    identity.FileName,
                    "Skipped",
                    "The same artifact identity and content already exists.",
                    existingIdentity.ArtifactId);
            }

            if (existingIdentity is not null && !options.ReplaceExistingArtifacts)
            {
                return new PortableModulePackageArtifactImportResult(
                    identity.FileName,
                    "Failed",
                    "An artifact with the same identity already exists with different content.",
                    existingIdentity.ArtifactId);
            }

            var relativePathSource = existingIdentity?.RelativePath
                ?? BuildRelativePath(compatibility, identity.TargetName, identity.PackageType, identity.Version);
            var relativePath = NormalizeRelativePath(relativePathSource)
                ?? throw new InvalidOperationException("The resolved artifact relative path is invalid.");
            finalPath = ResolveUnderRoot(storeRoot, relativePath);

            if (existingIdentity is null && (Directory.Exists(finalPath) || File.Exists(finalPath)))
            {
                if (File.Exists(finalPath))
                {
                    return new PortableModulePackageArtifactImportResult(
                        identity.FileName,
                        "Failed",
                        $"The target artifact path already exists as a file: {relativePath}",
                        null);
                }

                var existingContentHash = await ComputeDirectorySha256Async(finalPath, ct);
                if (!string.Equals(existingContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return new PortableModulePackageArtifactImportResult(
                        identity.FileName,
                        "Failed",
                        $"The target artifact path already exists with different content: {relativePath}",
                        null);
                }

                adoptedExistingContent = true;
            }

            if (!adoptedExistingContent)
            {
                if (existingIdentity is null)
                {
                    var duplicate = await _repo.FindArtifactBySha256Async(contentHash, ct);
                    if (duplicate is not null)
                    {
                        return new PortableModulePackageArtifactImportResult(
                            identity.FileName,
                            "Skipped",
                            $"Identical extracted content already exists as artifact {duplicate.ArtifactId}.",
                            duplicate.ArtifactId);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (existingIdentity is not null && (Directory.Exists(finalPath) || File.Exists(finalPath)))
                {
                    MoveExistingArtifactToBackup(finalPath, backupPath);
                    movedExistingToBackup = true;
                }

                Directory.Move(package.ArtifactContentPath, finalPath);
                movedArtifactToFinal = true;
            }

            int artifactId;
            try
            {
                artifactId = await _repo.SaveArtifactAsync(
                    new ArtifactEditData
                    {
                        ArtifactId = existingIdentity?.ArtifactId ?? 0,
                        AppId = app.AppId,
                        Version = identity.Version,
                        PackageType = identity.PackageType,
                        TargetName = identity.TargetName,
                        RelativePath = relativePath,
                        Sha256 = contentHash,
                        IsEnabled = true
                    },
                    ct);
            }
            catch (SqlException ex)
            {
                if (movedExistingToBackup)
                {
                    RestoreExistingArtifactBackup(backupPath, finalPath);
                    movedExistingToBackup = false;
                }
                else if (movedArtifactToFinal)
                {
                    TryDelete(finalPath);
                }

                movedArtifactToFinal = false;
                return new PortableModulePackageArtifactImportResult(
                    identity.FileName,
                    "Failed",
                    $"The artifact metadata could not be saved: {ex.Message}",
                    null);
            }

            var warning = adoptedExistingContent
                ? "Existing artifact files were adopted because the target path already contained identical content."
                : string.Empty;
            if (package.ConfigurationFiles.Count > 0)
            {
                try
                {
                    await _repo.ReplaceArtifactConfigurationFilesAsync(artifactId, package.ConfigurationFiles, ct);
                }
                catch (SqlException ex)
                {
                    warning = AppendWarning(
                        warning,
                        $"The artifact was imported, but configuration files from the package could not be saved: {ex.Message}");
                }
            }
            else if (existingIdentity is null && options.CopyConfigurationFilesFromPreviousVersion)
            {
                try
                {
                    await _repo.CopyConfigurationFilesFromLatestPreviousArtifactAsync(
                        artifactId,
                        app.AppId,
                        identity.PackageType,
                        identity.TargetName,
                        ct);
                }
                catch (SqlException ex)
                {
                    warning = AppendWarning(
                        warning,
                        $"The artifact was imported, but previous configuration files could not be copied: {ex.Message}");
                }
            }

            if (options.UseArtifactsImmediately)
            {
                try
                {
                    await _repo.ApplyArtifactToMatchingApplicationsAsync(artifactId, ct);
                }
                catch (SqlException ex)
                {
                    warning = AppendWarning(
                        warning,
                        $"The artifact was imported, but it could not be selected as desired version automatically: {ex.Message}");
                }
            }

            if (movedExistingToBackup)
            {
                TryDelete(backupPath);
                movedExistingToBackup = false;
            }

            return new PortableModulePackageArtifactImportResult(
                identity.FileName,
                existingIdentity is null ? "Imported" : "Replaced",
                string.IsNullOrWhiteSpace(warning) ? null : warning,
                artifactId);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (movedExistingToBackup && !string.IsNullOrWhiteSpace(finalPath))
            {
                RestoreExistingArtifactBackup(backupPath, finalPath);
            }
            else if (movedArtifactToFinal && !string.IsNullOrWhiteSpace(finalPath))
            {
                TryDelete(finalPath);
            }

            return new PortableModulePackageArtifactImportResult(identity.FileName, "Failed", ex.Message, null);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private static async Task<ModuleDefinitionDocumentEditData> ReadDefinitionAsync(
        string path,
        string sourceName,
        CancellationToken ct)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxDefinitionBytes)
        {
            throw new InvalidOperationException($"The module definition exceeds the limit of {MaxDefinitionBytes} bytes.");
        }

        var jsonText = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        var root = JsonNode.Parse(jsonText)
            ?? throw new InvalidOperationException("The module definition JSON file is empty.");

        var moduleKey = GetJsonStringProperty(root, "moduleKey");
        var definitionVersion = GetJsonStringProperty(root, "definitionVersion");
        if (string.IsNullOrWhiteSpace(moduleKey) || string.IsNullOrWhiteSpace(definitionVersion))
        {
            throw new InvalidOperationException("Module definition JSON must contain moduleKey and definitionVersion.");
        }

        var normalizedJson = root.ToJsonString(JsonOptions);
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedJson))).ToLowerInvariant();

        return new ModuleDefinitionDocumentEditData
        {
            ModuleKey = moduleKey,
            DefinitionVersion = definitionVersion,
            FormatVersion = GetJsonIntProperty(root, "formatVersion", 1),
            DefinitionJson = normalizedJson,
            DefinitionSha256 = sha256,
            SourceName = Truncate(sourceName, 400),
            CompatibleArtifacts = ReadCompatibleArtifacts(root)
        };
    }

    private static IReadOnlyList<ModuleDefinitionCompatibilityEditData> ReadCompatibleArtifacts(JsonNode root)
    {
        if (GetJsonObjectProperty(root, "compatibleArtifacts") is not JsonArray items)
        {
            return [];
        }

        var entries = new List<ModuleDefinitionCompatibilityEditData>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var appKey = GetJsonStringProperty(item, "appKey");
            var packageType = GetJsonStringProperty(item, "packageType");
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(packageType))
            {
                throw new InvalidOperationException("Each compatibleArtifacts item must contain appKey and packageType.");
            }

            entries.Add(new ModuleDefinitionCompatibilityEditData
            {
                AppKey = appKey,
                PackageType = packageType,
                TargetName = NullIfWhiteSpace(GetJsonStringProperty(item, "targetName")),
                RelativePathTemplate = NullIfWhiteSpace(GetJsonStringProperty(item, "relativePathTemplate")),
                MinArtifactVersion = NullIfWhiteSpace(GetJsonStringProperty(item, "minVersion")),
                MaxArtifactVersion = NullIfWhiteSpace(GetJsonStringProperty(item, "maxVersion"))
            });
        }

        return entries;
    }

    private static string FindSingleModuleDefinitionFile(string root)
    {
        var candidates = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(path => TryReadDefinitionSummary(path) is not null)
            .ToList();

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException("The package zip contains no module definition JSON file."),
            _ => throw new InvalidOperationException("The package zip contains multiple module definition JSON files.")
        };
    }

    private static ModuleDefinitionSummary? TryReadDefinitionSummary(string path)
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
                : new ModuleDefinitionSummary(moduleKey, definitionVersion);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ArtifactPackageFile? TryReadArtifactPackageFile(string path)
    {
        var fileName = Path.GetFileName(path);
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var parts = name.Split("__", StringSplitOptions.None);
        if (parts.Length != 5
            || parts.Any(part => part.Length == 0 || !MetadataTokenPattern.IsMatch(part)))
        {
            return null;
        }

        return new ArtifactPackageFile(fileName, parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    private static async Task<string> ComputeDirectorySha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relative);
            sha.TransformBlock(relativeBytes, 0, relativeBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);

            await using var stream = File.OpenRead(file);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static async Task CopyUploadToFileAsync(
        IFormFile file,
        string path,
        long maxBytes,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var totalBytes = 0L;
        var effectiveMaxBytes = maxBytes > 0 ? maxBytes : ArtifactUploadOptions.DefaultMaxUploadBytes;
        var buffer = new byte[BufferSize];

        await using var source = file.OpenReadStream();
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            totalBytes += read;
            if (totalBytes > effectiveMaxBytes)
            {
                throw new InvalidOperationException($"The uploaded file exceeds the configured limit of {effectiveMaxBytes} bytes.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static void ValidateArtifactEntryIsNotRuntimeConfiguration(string normalizedEntryName)
    {
        var fileName = normalizedEntryName.Split('/').LastOrDefault() ?? string.Empty;
        if (string.Equals(fileName, "appsettings.json", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)
               && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || RuntimeConfigurationFileNames.Any(name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"The artifact zip contains runtime configuration file '{normalizedEntryName}'. Put runtime configuration in the artifact package configuration-files section instead.");
        }
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

    private static string CreateArtifactPackageFileName(
        string moduleKey,
        string appKey,
        string packageType,
        string targetName,
        string version)
        => $"{moduleKey}__{appKey}__{packageType}__{targetName}__{version}.zip";

    private static string? NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0)
        {
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            return null;
        }

        return string.Join('/', segments);
    }

    private static string ResolveUnderRoot(string rootPath, string relativePath)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, comparison))
        {
            throw new InvalidOperationException("The path escapes the configured root.");
        }

        return fullPath;
    }

    private static string ResolveChildFile(string root, string fileName, string extension)
    {
        var cleanName = Path.GetFileName(fileName);
        if (!cleanName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected a {extension} file.");
        }

        var path = ResolveUnderRoot(root, cleanName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The requested package file was not found.", path);
        }

        return path;
    }

    private static string RequireConfiguredRoot(string path, string optionName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"ArtifactUpload:{optionName} is not configured.");
        }

        var fullPath = Path.GetFullPath(path.Trim());
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static string ResolveOptionalRoot(string path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path.Trim());

    private static string CreateTempRoot(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "OpenModulePlatform", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SanitizePathSegment(string value)
    {
        var sanitized = value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }

        return sanitized.Replace(' ', '-');
    }

    private static void MoveExistingArtifactToBackup(string finalPath, string backupPath)
    {
        TryDelete(backupPath);

        if (Directory.Exists(finalPath))
        {
            Directory.Move(finalPath, backupPath);
        }
        else if (File.Exists(finalPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Move(finalPath, backupPath);
        }
    }

    private static void RestoreExistingArtifactBackup(string backupPath, string finalPath)
    {
        try
        {
            if (!Directory.Exists(backupPath) && !File.Exists(backupPath))
            {
                return;
            }

            TryDelete(finalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, finalPath);
            }
            else
            {
                File.Move(backupPath, finalPath);
            }
        }
        catch (IOException)
        {
            // Best effort restoration after a failed replacement.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort restoration after a failed replacement.
        }
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
            // Best effort cleanup after failed imports/exports.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup after failed imports/exports.
        }
    }

    private static JsonNode? GetJsonObjectProperty(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var property in obj)
        {
            if (property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string GetJsonStringProperty(JsonNode? node, string propertyName)
    {
        var value = GetJsonObjectProperty(node, propertyName);
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;
    }

    private static int GetJsonIntProperty(JsonNode? node, string propertyName, int defaultValue)
    {
        var value = GetJsonObjectProperty(node, propertyName);
        if (value is not JsonValue jsonValue)
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

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string AppendWarning(string current, string next)
        => string.IsNullOrWhiteSpace(current) ? next : current + " " + next;

    private sealed record ModuleDefinitionSummary(string ModuleKey, string DefinitionVersion);
}

public sealed record PortableModulePackageImportOptions(
    bool ApplyModuleDefinition,
    bool ExecuteSqlRepairs,
    bool AllowTemporaryIncompatibleArtifacts,
    bool ReplaceExistingModuleDefinition,
    bool ReplaceExistingArtifacts,
    bool CopyConfigurationFilesFromPreviousVersion,
    bool UseArtifactsImmediately);

public sealed record AvailablePortableModulePackage(
    string ModuleKey,
    string DefinitionVersion,
    string ModuleDefinitionFileName,
    IReadOnlyList<ArtifactPackageFile> ArtifactFiles);

public sealed record ArtifactPackageFile(
    string FileName,
    string ModuleKey,
    string AppKey,
    string PackageType,
    string TargetName,
    string Version);

public sealed record PortableModulePackageImportResult(
    string ModuleKey,
    string DefinitionVersion,
    int ModuleDefinitionDocumentId,
    bool Applied,
    int SqlRepairCount,
    IReadOnlyList<PortableModulePackageArtifactImportResult> Artifacts)
{
    public int ImportedArtifactCount => Artifacts.Count(item => item.Status is "Imported" or "Replaced");

    public int FailedArtifactCount => Artifacts.Count(item => item.Status == "Failed");
}

public sealed record PortableModulePackageArtifactImportResult(
    string FileName,
    string Status,
    string? Message,
    int? ArtifactId);

public sealed record PortableModulePackageExportResult(
    string PackagePath,
    string FileName);
