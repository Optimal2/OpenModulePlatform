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
                .Where(file => IsCompatibleWithDefinition(file, definition.CompatibleArtifacts))
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
        var definition = await ReadDefinitionAsync(
            definitionPath,
            Path.GetFileName(definitionPath),
            ct,
            definitionsRoot);

        var artifactPaths = Directory.Exists(artifactsRoot)
            ? Directory.EnumerateFiles(artifactsRoot, $"{definition.ModuleKey}__*.zip", SearchOption.TopDirectoryOnly)
                .Where(path => TryReadArtifactPackageFile(path) is { } artifact
                    && IsCompatibleWithDefinition(artifact, definition.CompatibleArtifacts))
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
                var bundlePath = Path.Join(tempRoot, Path.GetFileName(bundleFile.FileName));
                await CopyUploadToFileAsync(bundleFile, bundlePath, _options.MaxUploadBytes, ct);
                var extractedRoot = Path.Join(tempRoot, "bundle");
                ZipFile.ExtractToDirectory(bundlePath, extractedRoot, overwriteFiles: true);

                var definitionPath = FindSingleModuleDefinitionFile(extractedRoot);
                var definition = await ReadDefinitionAsync(
                    definitionPath,
                    Path.GetFileName(definitionPath),
                    ct,
                    extractedRoot);
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

            var definitionUploadPath = Path.Join(tempRoot, Path.GetFileName(definitionFile.FileName));
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

                var packagePath = Path.Join(tempRoot, "artifacts", Path.GetFileName(file.FileName));
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

    public async Task<IReadOnlyList<PortableModulePackageArtifactImportResult>> ImportArtifactUploadsAsync(
        IReadOnlyList<IFormFile> artifactFiles,
        PortableModulePackageImportOptions options,
        CancellationToken ct)
    {
        if (artifactFiles.Count == 0 || artifactFiles.All(static file => file.Length == 0))
        {
            throw new InvalidOperationException("Select at least one artifact package zip.");
        }

        var tempRoot = CreateTempRoot("portal-artifact-package-upload");
        try
        {
            var packageUploadPaths = new List<string>();
            foreach (var file in artifactFiles.Where(static file => file.Length > 0))
            {
                if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Artifact package uploads must be .zip files.");
                }

                var packagePath = Path.Join(tempRoot, Path.GetFileName(file.FileName));
                await CopyUploadToFileAsync(file, packagePath, _options.MaxUploadBytes, ct);
                packageUploadPaths.Add(packagePath);
            }

            var plans = packageUploadPaths
                .Select(path => new ArtifactImportPlan(path, TryReadArtifactPackageFile(path), null))
                .ToArray();
            var activationKeys = options.UseArtifactsImmediately
                ? SelectLatestActivationKeys(plans)
                : [];

            var results = new List<PortableModulePackageArtifactImportResult>();
            foreach (var plan in plans)
            {
                if (plan.Identity is null)
                {
                    results.Add(new PortableModulePackageArtifactImportResult(
                        Path.GetFileName(plan.Path),
                        "Failed",
                        "The artifact package filename does not follow the standard module__app__packageType__target__version.zip format.",
                        null));
                    continue;
                }

                var activateArtifact = activationKeys.Contains(BuildArtifactActivationKey(plan.Identity));
                var artifactOptions = options with { UseArtifactsImmediately = activateArtifact };
                var result = await ImportArtifactPackageAsync(plan.Identity, plan.Path, artifactOptions, ct);
                if (options.UseArtifactsImmediately
                    && !activateArtifact
                    && result.Status is "Imported" or "Replaced" or "Skipped")
                {
                    result = result with
                    {
                        Message = AppendWarning(
                            result.Message ?? string.Empty,
                            "The artifact was kept as a historical package and was not selected because a newer artifact for the same app slot was uploaded in the same batch.")
                    };
                }

                results.Add(result);
            }

            return results;
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
        var downloadRoot = Path.Join(Path.GetTempPath(), "OpenModulePlatform", "portal-module-package-downloads");
        Directory.CreateDirectory(downloadRoot);
        var packagePath = Path.Join(downloadRoot, $"{Guid.NewGuid():N}-{packageFileName}");

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
                    var artifactPackagePath = Path.Join(
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

    public async Task<PortableModulePackageExportResult> ExportArtifactPackageAsync(
        int artifactId,
        CancellationToken ct)
    {
        var artifact = await _repo.GetArtifactAsync(artifactId, ct)
            ?? throw new InvalidOperationException("The selected artifact was not found.");
        if (string.IsNullOrWhiteSpace(artifact.RelativePath))
        {
            throw new InvalidOperationException("The selected artifact has no relative payload path.");
        }

        if (string.IsNullOrWhiteSpace(artifact.TargetName))
        {
            throw new InvalidOperationException("The selected artifact has no target name and cannot be exported as a standard artifact package.");
        }

        var normalizedRelativePath = NormalizeRelativePath(artifact.RelativePath);
        if (normalizedRelativePath is null)
        {
            throw new InvalidOperationException("The selected artifact has an invalid relative payload path.");
        }

        var storeRoot = RequireConfiguredRoot(_options.ArtifactStoreRoot, "ArtifactStoreRoot");
        var payloadPath = ResolveUnderRoot(storeRoot, normalizedRelativePath);
        if (!Directory.Exists(payloadPath))
        {
            throw new InvalidOperationException($"Artifact payload path does not exist: '{payloadPath}'.");
        }

        var packageFileName = CreateArtifactPackageFileName(
            artifact.ModuleKey,
            artifact.AppKey,
            artifact.PackageType,
            artifact.TargetName,
            artifact.Version);
        var downloadRoot = Path.Join(Path.GetTempPath(), "OpenModulePlatform", "portal-artifact-package-downloads");
        Directory.CreateDirectory(downloadRoot);
        var packagePath = Path.Join(downloadRoot, $"{Guid.NewGuid():N}-{packageFileName}");

        try
        {
            var configurationFiles = await _repo.GetArtifactConfigurationFileContentsAsync(artifact.ArtifactId, ct);
            new ArtifactPackageWriter().CreateFromPayloadDirectory(
                payloadPath,
                packagePath,
                configurationFiles
                    .Select(file => new ArtifactPackageConfigurationFile(file.RelativePath, file.FileContent))
                    .ToArray());

            return new PortableModulePackageExportResult(packagePath, packageFileName);
        }
        catch
        {
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
        var appliedDefinition = await _repo.GetAppliedModuleDefinitionDocumentAsync(definition.ModuleKey, ct);
        var keepNewerAppliedDefinition = appliedDefinition is not null
            && CompareArtifactVersions(appliedDefinition.DefinitionVersion, definition.DefinitionVersion) > 0;

        var applied = false;
        var repairCount = 0;
        if (options.ApplyModuleDefinition && !keepNewerAppliedDefinition)
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

        var plans = CreateArtifactImportPlans(definition, artifactPaths);
        var activationKeys = SelectLatestActivationKeys(plans);
        var artifactResults = new List<PortableModulePackageArtifactImportResult>();
        foreach (var plan in plans)
        {
            if (plan.Identity is null || !string.IsNullOrWhiteSpace(plan.SkipMessage))
            {
                artifactResults.Add(new PortableModulePackageArtifactImportResult(
                    Path.GetFileName(plan.Path),
                    "Skipped",
                    plan.SkipMessage ?? "The artifact package filename does not follow the standard module__app__packageType__target__version.zip format.",
                    null));
                continue;
            }

            var activateArtifact = options.UseArtifactsImmediately
                && activationKeys.Contains(BuildArtifactActivationKey(plan.Identity));
            var artifactOptions = options with { UseArtifactsImmediately = activateArtifact };
            var result = await ImportArtifactPackageAsync(plan.Identity, plan.Path, artifactOptions, ct);
            if (options.UseArtifactsImmediately
                && !activateArtifact
                && result.Status is "Imported" or "Replaced" or "Skipped")
            {
                result = result with
                {
                    Message = AppendWarning(
                        result.Message ?? string.Empty,
                        "The artifact was kept as a historical package and was not selected because a newer compatible artifact for the same app slot exists in this module package.")
                };
            }

            artifactResults.Add(result);
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
            return new PortableModulePackageArtifactImportResult(identity.FileName, "Skipped", ex.Message, null);
        }

        var storeRoot = RequireConfiguredRoot(_options.ArtifactStoreRoot, "ArtifactStoreRoot");
        var tempRoot = CreateTempRoot("portal-artifact-package-import");
        var stagingPath = Path.Join(tempRoot, "artifact");
        var backupPath = Path.Join(tempRoot, "backup");
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
                var existingWarning = string.Empty;
                var configurationFileCount = 0;
                if (package.ConfigurationFiles.Count > 0)
                {
                    try
                    {
                        configurationFileCount = await _repo.ReplaceArtifactConfigurationFilesAsync(
                            existingIdentity.ArtifactId,
                            package.ConfigurationFiles,
                            ct);
                    }
                    catch (SqlException ex)
                    {
                        existingWarning = AppendWarning(
                            existingWarning,
                            $"The existing artifact content matched, but configuration files from the package could not be saved: {ex.Message}");
                    }
                }

                if (options.UseArtifactsImmediately)
                {
                    try
                    {
                        await _repo.ApplyArtifactToMatchingApplicationsAsync(existingIdentity.ArtifactId, ct);
                    }
                    catch (SqlException ex)
                    {
                        existingWarning = AppendWarning(
                            existingWarning,
                            $"The existing artifact was found, but it could not be selected as desired version automatically: {ex.Message}");
                    }
                }

                return new PortableModulePackageArtifactImportResult(
                    identity.FileName,
                    configurationFileCount > 0 ? "Updated" : "Skipped",
                    AppendWarning(
                        configurationFileCount > 0
                            ? $"The same artifact identity and content already exists. Updated {configurationFileCount} configuration file row(s)."
                            : "The same artifact identity and content already exists.",
                        existingWarning),
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

                MoveFileOrDirectory(package.ArtifactContentPath, finalPath);
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
                }
                else if (movedArtifactToFinal)
                {
                    TryDelete(finalPath);
                }

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
        CancellationToken ct,
        string? externalSqlRoot = null)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxDefinitionBytes)
        {
            throw new InvalidOperationException($"The module definition exceeds the limit of {MaxDefinitionBytes} bytes.");
        }

        var jsonText = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
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

    private static IReadOnlyList<ArtifactImportPlan> CreateArtifactImportPlans(
        ModuleDefinitionDocumentEditData definition,
        IReadOnlyList<string> artifactPaths)
    {
        var plans = new List<ArtifactImportPlan>();
        foreach (var artifactPath in artifactPaths)
        {
            var identity = TryReadArtifactPackageFile(artifactPath);
            if (identity is null)
            {
                plans.Add(new ArtifactImportPlan(
                    artifactPath,
                    null,
                    "The artifact package filename does not follow the standard module__app__packageType__target__version.zip format."));
                continue;
            }

            if (!identity.ModuleKey.Equals(definition.ModuleKey, StringComparison.OrdinalIgnoreCase))
            {
                plans.Add(new ArtifactImportPlan(
                    artifactPath,
                    identity,
                    $"The artifact belongs to module '{identity.ModuleKey}', not '{definition.ModuleKey}'."));
                continue;
            }

            if (!IsCompatibleWithDefinition(identity, definition.CompatibleArtifacts))
            {
                plans.Add(new ArtifactImportPlan(
                    artifactPath,
                    identity,
                    $"Artifact version {identity.Version} is not compatible with module definition '{definition.ModuleKey}' version {definition.DefinitionVersion}."));
                continue;
            }

            plans.Add(new ArtifactImportPlan(artifactPath, identity, null));
        }

        return plans;
    }

    private static HashSet<string> SelectLatestActivationKeys(IReadOnlyList<ArtifactImportPlan> plans)
    {
        return plans
            .Where(static plan => plan.Identity is not null && string.IsNullOrWhiteSpace(plan.SkipMessage))
            .GroupBy(static plan => BuildArtifactSlotKey(plan.Identity!), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static plan => plan.Identity!.Version, ArtifactVersionComparer.Instance)
                .First())
            .Select(static plan => BuildArtifactActivationKey(plan.Identity!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                : new ModuleDefinitionSummary(moduleKey, definitionVersion, ReadCompatibleArtifacts(root));
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
        if (RuntimeConfigurationFiles.IsRuntimeConfigurationFileName(fileName))
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
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
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
        var path = Path.Join(Path.GetTempPath(), "OpenModulePlatform", name, Guid.NewGuid().ToString("N"));
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
            MoveFileOrDirectory(finalPath, backupPath);
        }
        else if (File.Exists(finalPath))
        {
            MoveFileOrDirectory(finalPath, backupPath);
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
                MoveFileOrDirectory(backupPath, finalPath);
            }
            else
            {
                MoveFileOrDirectory(backupPath, finalPath);
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

    private static void MoveFileOrDirectory(string source, string destination)
    {
        if (File.Exists(source))
        {
            MoveFile(source, destination);
            return;
        }

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Source path was not found: {source}");
        }

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
            File.Move(source, destination);
            return;
        }

        File.Copy(source, destination, overwrite: false);
        File.Delete(source);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var relativeDirectory in Directory
            .EnumerateDirectories(source, "*", SearchOption.AllDirectories)
            .Select(directory => Path.GetRelativePath(source, directory)))
        {
            Directory.CreateDirectory(Path.Join(destination, relativeDirectory));
        }

        foreach (var relativeFile in Directory
            .EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(source, file)))
        {
            var targetFile = Path.Join(destination, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(Path.Join(source, relativeFile), targetFile, overwrite: false);
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

    private static JsonNode? GetJsonObjectProperty(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        return obj.FirstOrDefault(property => property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)).Value;
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

    private static bool IsCompatibleWithDefinition(
        ArtifactPackageFile artifact,
        IReadOnlyList<ModuleDefinitionCompatibilityEditData> compatibility)
        => compatibility.Any(slot =>
            slot.AppKey.Equals(artifact.AppKey, StringComparison.OrdinalIgnoreCase)
            && slot.PackageType.Equals(artifact.PackageType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(slot.TargetName ?? string.Empty, artifact.TargetName, StringComparison.OrdinalIgnoreCase)
            && IsVersionInRange(artifact.Version, slot.MinArtifactVersion, slot.MaxArtifactVersion));

    private static string BuildArtifactSlotKey(ArtifactPackageFile artifact)
        => string.Join('\u001f', artifact.ModuleKey, artifact.AppKey, artifact.PackageType, artifact.TargetName);

    private static string BuildArtifactActivationKey(ArtifactPackageFile artifact)
        => string.Join('\u001f', BuildArtifactSlotKey(artifact), artifact.Version);

    private static bool IsVersionInRange(string version, string? minVersion, string? maxVersion)
    {
        if (!string.IsNullOrWhiteSpace(minVersion)
            && CompareArtifactVersions(version, minVersion) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(maxVersion)
            && CompareArtifactVersions(version, maxVersion) > 0)
        {
            return false;
        }

        return true;
    }

    private static int CompareArtifactVersions(string left, string right)
    {
        if (TryParseComparableVersion(left, out var leftVersion)
            && TryParseComparableVersion(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseComparableVersion(string? value, out Version version)
    {
        var text = value?.Trim() ?? string.Empty;
        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            text = text[..suffixIndex];
        }

        return Version.TryParse(text, out version!);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string AppendWarning(string current, string next)
        => string.IsNullOrWhiteSpace(current) ? next : current + " " + next;

    private sealed record ModuleDefinitionSummary(
        string ModuleKey,
        string DefinitionVersion,
        IReadOnlyList<ModuleDefinitionCompatibilityEditData> CompatibleArtifacts);

    private sealed record ArtifactImportPlan(
        string Path,
        ArtifactPackageFile? Identity,
        string? SkipMessage);

    private sealed class ArtifactVersionComparer : IComparer<string>
    {
        public static readonly ArtifactVersionComparer Instance = new();

        public int Compare(string? x, string? y)
            => CompareArtifactVersions(x ?? string.Empty, y ?? string.Empty);
    }
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
    public int ImportedArtifactCount => Artifacts.Count(item => item.Status is "Imported" or "Replaced" or "Updated");

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
