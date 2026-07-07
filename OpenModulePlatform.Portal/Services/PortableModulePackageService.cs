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
/// Imports and exports portable deployment packages, including universal module
/// packages and legacy module-definition-plus-artifact package bundles.
/// </summary>
public sealed class PortableModulePackageService
{
    private const int BufferSize = 1024 * 128;
    private const int MaxDefinitionBytes = 1024 * 1024 * 5;
    private const string ModuleDefinitionFolder = "module-definitions";
    private const string ArtifactsFolder = "artifacts";
    private const string HostConfigurationsFolder = "host-configs";
    private const string ConfigOverlaysFolder = "config-overlays";
    private const string WidgetsFolder = "widgets";
    private const string WidgetDataFolder = "widget-data";

    private static readonly Regex MetadataTokenPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._+-]*$",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly OmpAdminRepository _repo;
    private readonly ArtifactUploadOptions _options;
    private readonly PortalDashboardWidgetPackageService _widgets;
    private readonly PortalWidgetRuntimeDataPackageService _widgetRuntimeData;

    public PortableModulePackageService(
        OmpAdminRepository repo,
        IOptions<ArtifactUploadOptions> options,
        PortalDashboardWidgetPackageService widgets,
        PortalWidgetRuntimeDataPackageService widgetRuntimeData)
    {
        _repo = repo;
        _options = options.Value;
        _widgets = widgets;
        _widgetRuntimeData = widgetRuntimeData;
    }

    public IReadOnlyList<AvailablePortableModulePackage> GetAvailablePackages()
    {
        var definitionsRoot = ResolveOptionalRoot(_options.AvailableModuleDefinitionsRoot);
        var artifactsRoot = ResolveOptionalRoot(_options.AvailableArtifactsRoot);
        var artifactFiles = EnumerateLibraryFiles(artifactsRoot, "*.zip")
                .Select(TryReadArtifactPackageFile)
                .Where(static item => item is not null)
                .Select(static item => item!)
                .ToList();

        var definitions = EnumerateLibraryFiles(definitionsRoot, "*.json")
            .Select(static path => new AvailableModuleDefinitionFile(path, TryReadDefinitionSummary(path)))
            .Where(static file => file.Summary is not null)
            .GroupBy(static file => file.Summary!.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static file => file.Summary!.DefinitionVersion, ArtifactVersionComparer.Instance)
                .ThenBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(static file => file.Summary!.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (definitions.Count == 0)
        {
            return [];
        }

        var packages = new List<AvailablePortableModulePackage>();
        foreach (var definitionFile in definitions)
        {
            var definition = definitionFile.Summary!;

            // The shared available library may intentionally contain historical
            // artifact files. The operator-facing package row should represent
            // the latest installable package for each artifact slot.
            var artifacts = SelectLatestArtifactFilesBySlot(artifactFiles
                .Where(file => file.ModuleKey.Equals(definition.ModuleKey, StringComparison.OrdinalIgnoreCase))
                .Where(file => IsCompatibleWithDefinition(file, definition.CompatibleArtifacts)));

            packages.Add(new AvailablePortableModulePackage(
                definition.ModuleKey,
                definition.DefinitionVersion,
                Path.GetFileName(definitionFile.Path),
                artifacts));
        }

        return packages;
    }

    private static IReadOnlyList<string> EnumerateLibraryFiles(string root, string searchPattern)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(root, searchPattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The package library is optional on this page. Operators should be
            // able to import uploaded files even when the shared library is
            // temporarily unavailable or the IIS runtime account lacks access.
            return [];
        }
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
            ? SelectLatestArtifactPathsBySlot(Directory
                .EnumerateFiles(artifactsRoot, $"{definition.ModuleKey}__*.zip", SearchOption.TopDirectoryOnly)
                .Select(static path => (Path: path, Artifact: TryReadArtifactPackageFile(path)))
                .Where(item => item.Artifact is not null
                    && IsCompatibleWithDefinition(item.Artifact, definition.CompatibleArtifacts))
                .Select(static item => (item.Path, Artifact: item.Artifact!)))
            : [];

        return await ImportAsync(definition, artifactPaths, options, quickImportState: null, ct);
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

                return await ImportAsync(definition, packagePaths, options, quickImportState: null, ct);
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

            return await ImportAsync(uploadedDefinition, packageUploadPaths, options, quickImportState: null, ct);
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

    public async Task<UniversalPackageImportResult> ImportUniversalPackageUploadAsync(
        IFormFile? packageFile,
        PortableModulePackageImportOptions options,
        bool replaceExistingConfigObjects,
        CancellationToken ct)
    {
        if (packageFile is null || packageFile.Length == 0)
        {
            throw new InvalidOperationException("Select one universal module package zip.");
        }

        if (!packageFile.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Universal module package uploads must be .zip files.");
        }

        var tempRoot = CreateTempRoot("portal-universal-module-package-upload");
        try
        {
            var packagePath = Path.Join(tempRoot, Path.GetFileName(packageFile.FileName));
            await CopyUploadToFileAsync(packageFile, packagePath, _options.MaxUploadBytes, ct);
            return await ImportUniversalPackageFileAsync(
                packagePath,
                options,
                replaceExistingConfigObjects,
                selectedItemPaths: null,
                ct);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    public async Task<UniversalPackagePreviewResult> StageUniversalPackageUploadForPreviewAsync(
        IFormFile? packageFile,
        CancellationToken ct)
    {
        if (packageFile is null || packageFile.Length == 0)
        {
            throw new InvalidOperationException("Select one universal module package zip.");
        }

        if (!packageFile.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Universal module package uploads must be .zip files.");
        }

        CleanupOldStagedUniversalPackages();
        var token = $"{Guid.NewGuid():N}.zip";
        var packagePath = Path.Join(GetUniversalPackageStagingRoot(), token);
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        await CopyUploadToFileAsync(packageFile, packagePath, _options.MaxUploadBytes, ct);

        try
        {
            var preview = PreviewUniversalPackageFile(packagePath, token);
            return preview;
        }
        catch
        {
            TryDelete(packagePath);
            throw;
        }
    }

    public async Task<UniversalPackageImportResult> ImportStagedUniversalPackageAsync(
        string token,
        IReadOnlyList<string> selectedItemPaths,
        PortableModulePackageImportOptions options,
        bool replaceExistingConfigObjects,
        CancellationToken ct)
    {
        var packagePath = ResolveStagedUniversalPackagePath(token);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("The staged universal module package was not found. Upload and preview the package again.", token);
        }

        if (selectedItemPaths.Count == 0)
        {
            throw new InvalidOperationException("Select at least one universal package item to import.");
        }

        try
        {
            return await ImportUniversalPackageFileAsync(
                packagePath,
                options,
                replaceExistingConfigObjects,
                selectedItemPaths
                    .Select(NormalizeUniversalPackageSelectionPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                ct);
        }
        finally
        {
            TryDelete(packagePath);
        }
    }

    private UniversalPackagePreviewResult PreviewUniversalPackageFile(
        string packagePath,
        string token)
    {
        var extractionRoot = CreateTempRoot("portal-universal-module-package-preview");
        try
        {
            var package = new UniversalModulePackageReader().ExtractToDirectory(packagePath, extractionRoot);
            var items = package.Items
                .Select(static item => new UniversalPackagePreviewItem(
                    item.Kind.ToString(),
                    item.Path,
                    item.SourceName))
                .ToArray();
            return new UniversalPackagePreviewResult(
                token,
                package.SourceName,
                package.PackageKey,
                package.PackageVersion,
                package.DisplayName,
                package.TargetHostProfile,
                items);
        }
        finally
        {
            TryDelete(extractionRoot);
        }
    }

    private async Task<UniversalPackageImportResult> ImportUniversalPackageFileAsync(
        string packagePath,
        PortableModulePackageImportOptions options,
        bool replaceExistingConfigObjects,
        IReadOnlySet<string>? selectedItemPaths,
        CancellationToken ct)
    {
        var extractionRoot = CreateTempRoot("portal-universal-module-package-extract");
        try
        {
            var package = new UniversalModulePackageReader().ExtractToDirectory(packagePath, extractionRoot);
            var packageItems = selectedItemPaths is null
                ? package.Items
                : package.Items.Where(item => selectedItemPaths.Contains(item.Path)).ToArray();
            var quickImportState = options.QuickImport
                ? await QuickImportState.CreateAsync(_repo, ct)
                : null;
            var results = new List<UniversalPackageImportItemResult>();
            var processedArtifactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var artifactItems = packageItems
                .Where(static item => item.Kind == UniversalModulePackageItemKind.ArtifactPackage)
                .ToArray();

            foreach (var item in packageItems.Where(static item => item.Kind == UniversalModulePackageItemKind.ModuleDefinition))
            {
                try
                {
                    var definition = await ReadDefinitionAsync(
                        item.ExtractedPath,
                        item.SourceName,
                        ct,
                        package.ExtractionRoot);
                    if (quickImportState?.TryGetModuleDefinitionSkipMessage(definition, out var skipMessage) == true)
                    {
                        results.Add(new UniversalPackageImportItemResult(
                            "module-definition",
                            item.Path,
                            "Skipped",
                            skipMessage));
                        continue;
                    }

                    var matchingArtifactItems = artifactItems
                        .Where(artifactItem => TryReadArtifactPackageFile(artifactItem.ExtractedPath) is { } artifact
                            && artifact.ModuleKey.Equals(definition.ModuleKey, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var importResult = await ImportAsync(
                        definition,
                        matchingArtifactItems.Select(static artifactItem => artifactItem.ExtractedPath).ToArray(),
                        options,
                        quickImportState,
                        ct);

                    foreach (var artifactItem in matchingArtifactItems)
                    {
                        processedArtifactPaths.Add(artifactItem.ExtractedPath);
                    }

                    results.Add(new UniversalPackageImportItemResult(
                        "module-definition",
                        item.Path,
                        importResult.Applied ? "Applied" : "Stored",
                        $"Module {importResult.ModuleKey} {importResult.DefinitionVersion}; artifacts imported or replaced: {importResult.ImportedArtifactCount}; failed artifacts: {importResult.FailedArtifactCount}."));

                    foreach (var artifactResult in importResult.Artifacts)
                    {
                        results.Add(new UniversalPackageImportItemResult(
                            "artifact-package",
                            artifactResult.FileName,
                            artifactResult.Status,
                            artifactResult.Message));
                    }
                }
                catch (Exception ex) when (IsExpectedUniversalImportFailure(ex))
                {
                    results.Add(new UniversalPackageImportItemResult(
                        "module-definition",
                        item.Path,
                        "Failed",
                        ex.Message));
                }
            }

            foreach (var item in artifactItems.Where(item => !processedArtifactPaths.Contains(item.ExtractedPath)))
            {
                results.Add(await ImportUniversalArtifactItemAsync(item, options, quickImportState, ct));
            }

            foreach (var item in packageItems.Where(static item => item.Kind == UniversalModulePackageItemKind.HostConfiguration))
            {
                results.Add(await ImportUniversalHostConfigurationItemAsync(item, replaceExistingConfigObjects, quickImportState, ct));
            }

            foreach (var item in packageItems.Where(static item => item.Kind == UniversalModulePackageItemKind.ConfigOverlay))
            {
                results.Add(await ImportUniversalConfigOverlayItemAsync(item, replaceExistingConfigObjects, quickImportState, ct));
            }

            foreach (var item in packageItems.Where(static item => item.Kind == UniversalModulePackageItemKind.DashboardWidget))
            {
                results.Add(await ImportUniversalWidgetItemAsync(item, options, ct));
            }

            foreach (var item in packageItems.Where(static item => item.Kind == UniversalModulePackageItemKind.WidgetRuntimeData))
            {
                results.Add(await ImportUniversalWidgetRuntimeDataItemAsync(item, ct));
            }

            foreach (var item in packageItems.Where(static item => item.Kind == UniversalModulePackageItemKind.Unknown))
            {
                results.Add(new UniversalPackageImportItemResult(
                    "unknown",
                    item.Path,
                    "Skipped",
                    "The item kind is not supported."));
            }

            return new UniversalPackageImportResult(
                package.SourceName,
                package.PackageKey,
                package.PackageVersion,
                package.TargetHostProfile,
                results);
        }
        finally
        {
            TryDelete(extractionRoot);
        }
    }

    private async Task<UniversalPackageImportItemResult> ImportUniversalArtifactItemAsync(
        PortableUniversalModulePackageItem item,
        PortableModulePackageImportOptions options,
        QuickImportState? quickImportState,
        CancellationToken ct)
    {
        try
        {
            var artifact = TryReadArtifactPackageFile(item.ExtractedPath);
            if (artifact is null)
            {
                return new UniversalPackageImportItemResult(
                    "artifact-package",
                    item.Path,
                    "Failed",
                    "The artifact package filename does not follow the standard module__app__packageType__target__version.zip format.");
            }

            if (quickImportState?.TryGetArtifactSkipMessage(artifact, out var skipMessage) == true)
            {
                return new UniversalPackageImportItemResult(
                    "artifact-package",
                    item.Path,
                    "Skipped",
                    skipMessage);
            }

            var result = await ImportArtifactPackageAsync(artifact, item.ExtractedPath, options, ct);
            return new UniversalPackageImportItemResult(
                "artifact-package",
                item.Path,
                result.Status,
                result.Message);
        }
        catch (Exception ex) when (IsExpectedUniversalImportFailure(ex))
        {
            return new UniversalPackageImportItemResult("artifact-package", item.Path, "Failed", ex.Message);
        }
    }

    private async Task<UniversalPackageImportItemResult> ImportUniversalHostConfigurationItemAsync(
        PortableUniversalModulePackageItem item,
        bool replaceExisting,
        QuickImportState? quickImportState,
        CancellationToken ct)
    {
        try
        {
            var reader = new ConfigOverlayPackageReader();
            var hostConfiguration = await reader.ReadHostConfigurationAsync(
                item.ExtractedPath,
                item.SourceName,
                ct);
            if (quickImportState?.TryGetHostConfigurationSkipMessage(hostConfiguration, out var skipMessage) == true)
            {
                return new UniversalPackageImportItemResult(
                    "host-configuration",
                    item.Path,
                    "Skipped",
                    skipMessage);
            }

            var result = await _repo.SaveImportedHostConfigurationAsync(
                hostConfiguration,
                replaceExisting,
                ct);
            return new UniversalPackageImportItemResult(
                "host-configuration",
                item.Path,
                result.WasIdentical ? "Skipped" : result.Replaced ? "Replaced" : result.Created ? "Imported" : "Updated",
                $"Host {hostConfiguration.HostKey} {hostConfiguration.ConfigurationVersion}.");
        }
        catch (Exception ex) when (IsExpectedUniversalImportFailure(ex))
        {
            return new UniversalPackageImportItemResult("host-configuration", item.Path, "Failed", ex.Message);
        }
    }

    private async Task<UniversalPackageImportItemResult> ImportUniversalConfigOverlayItemAsync(
        PortableUniversalModulePackageItem item,
        bool replaceExisting,
        QuickImportState? quickImportState,
        CancellationToken ct)
    {
        try
        {
            var reader = new ConfigOverlayPackageReader();
            var overlay = await reader.ReadConfigOverlayAsync(
                item.ExtractedPath,
                item.SourceName,
                ct);
            if (quickImportState?.TryGetConfigOverlaySkipMessage(overlay, out var skipMessage) == true)
            {
                return new UniversalPackageImportItemResult(
                    "config-overlay",
                    item.Path,
                    "Skipped",
                    skipMessage);
            }

            var result = await _repo.SaveImportedConfigOverlayAsync(
                overlay,
                replaceExisting,
                ct);
            var message = $"Overlay {overlay.HostKey}/{overlay.OverlayKey} {overlay.OverlayVersion}; configuration files: {overlay.ConfigurationFiles.Count}.";
            if (overlay.SqlScriptCount > 0)
            {
                message = AppendWarning(message, ConfigOverlayPackageReader.ConfigOverlaySqlScriptsWarning);
            }

            return new UniversalPackageImportItemResult(
                "config-overlay",
                item.Path,
                result.WasIdentical ? "Skipped" : result.Replaced ? "Replaced" : result.Created ? "Imported" : "Updated",
                message);
        }
        catch (Exception ex) when (IsExpectedUniversalImportFailure(ex))
        {
            return new UniversalPackageImportItemResult("config-overlay", item.Path, "Failed", ex.Message);
        }
    }

    private async Task<UniversalPackageImportItemResult> ImportUniversalWidgetItemAsync(
        PortableUniversalModulePackageItem item,
        PortableModulePackageImportOptions options,
        CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(item.ExtractedPath);
            var result = await _widgets.ImportAsync(
                stream,
                item.SourceName,
                options.ReplaceExistingDashboardWidgets,
                options.QuickImport,
                ct);
            var status = result.CreatedCount + result.UpdatedCount > 0
                ? "Imported"
                : "Skipped";
            return new UniversalPackageImportItemResult(
                "dashboard-widget",
                item.Path,
                status,
                $"Created: {result.CreatedCount}; updated: {result.UpdatedCount}; skipped: {result.SkippedCount}; permission rows: {result.PermissionRowCount}.");
        }
        catch (Exception ex) when (IsExpectedUniversalImportFailure(ex))
        {
            return new UniversalPackageImportItemResult("dashboard-widget", item.Path, "Failed", ex.Message);
        }
    }

    private async Task<UniversalPackageImportItemResult> ImportUniversalWidgetRuntimeDataItemAsync(
        PortableUniversalModulePackageItem item,
        CancellationToken ct)
    {
        try
        {
            var result = await _widgetRuntimeData.ImportAsync(item.ExtractedPath, ct);
            return new UniversalPackageImportItemResult(
                "widget-data",
                item.Path,
                "Imported",
                $"Data documents: {result.DataDocumentCount}; binary rows inserted: {result.InsertedBinaryDataCount}; binary rows reused: {result.ReusedBinaryDataCount}.");
        }
        catch (Exception ex) when (IsExpectedUniversalImportFailure(ex))
        {
            return new UniversalPackageImportItemResult("widget-data", item.Path, "Failed", ex.Message);
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
                var universalItems = new JsonArray();
                var definitionEntryName = $"{ModuleDefinitionFolder}/{SanitizePathSegment(moduleKey)}.module-definition.json";
                var definitionEntry = archive.CreateEntry(
                    definitionEntryName,
                    CompressionLevel.Optimal);
                await using (var entryStream = definitionEntry.Open())
                await using (var writer = new StreamWriter(entryStream, new UTF8Encoding(false)))
                {
                    await writer.WriteAsync(definition.DefinitionJson.AsMemory(), ct);
                }

                universalItems.Add(CreateUniversalPackageItem("module-definition", definitionEntryName, definition.DefinitionVersion));

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
                    var artifactEntryName = $"{ArtifactsFolder}/{artifactFileName}";
                    archive.CreateEntryFromFile(
                        artifactPackagePath,
                        artifactEntryName,
                        CompressionLevel.Optimal);
                    universalItems.Add(CreateUniversalPackageItem("artifact-package", artifactEntryName, artifact.Version));
                    File.Delete(artifactPackagePath);
                }

                await WriteUniversalPackageManifestAsync(
                    archive,
                    moduleKey,
                    definition.DefinitionVersion,
                    $"Module package for {moduleKey}",
                    universalItems,
                    ct);
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

    public async Task<PackageLibraryExportResult> ExportModulePackageToLibraryAsync(
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

        var definitionsRoot = RequireConfiguredRoot(_options.AvailableModuleDefinitionsRoot, "AvailableModuleDefinitionsRoot");
        var artifactsRoot = RequireConfiguredRoot(_options.AvailableArtifactsRoot, "AvailableArtifactsRoot");
        Directory.CreateDirectory(definitionsRoot);
        Directory.CreateDirectory(artifactsRoot);

        var definitionFileName = $"{SanitizePathSegment(moduleKey)}.module-definition.json";
        var definitionPath = ResolveUnderRoot(definitionsRoot, definitionFileName);
        await File.WriteAllTextAsync(definitionPath, definition.DefinitionJson, Utf8NoBom, ct);

        var artifactRows = await _repo.GetModuleArtifactPackagesAsync(moduleKey, includeAllArtifactVersions, ct);
        var exportedArtifacts = 0;
        var skippedArtifacts = 0;
        var messages = new List<string>();

        foreach (var artifact in artifactRows)
        {
            PortableModulePackageExportResult? export = null;
            try
            {
                export = await ExportArtifactPackageAsync(artifact.ArtifactId, ct);
                var targetPath = ResolveUnderRoot(artifactsRoot, export.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? artifactsRoot);
                File.Copy(export.PackagePath, targetPath, overwrite: true);
                exportedArtifacts++;
            }
            catch (InvalidOperationException ex)
            {
                skippedArtifacts++;
                messages.Add($"{artifact.AppKey} {artifact.PackageType} {artifact.Version}: {ex.Message}");
            }
            finally
            {
                if (export is not null)
                {
                    TryDelete(export.PackagePath);
                }
            }
        }

        return new PackageLibraryExportResult(
            moduleKey,
            definition.DefinitionVersion,
            definitionFileName,
            artifactRows.Count,
            exportedArtifacts,
            skippedArtifacts,
            messages);
    }

    public async Task<PortableModulePackageExportResult> ExportUniversalPackageAsync(
        UniversalPackageExportRequest request,
        CancellationToken ct)
    {
        var packageKey = string.IsNullOrWhiteSpace(request.PackageKey)
            ? "omp-universal"
            : request.PackageKey.Trim();
        var packageVersion = string.IsNullOrWhiteSpace(request.PackageVersion)
            ? DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)
            : request.PackageVersion.Trim();
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? "OpenModulePlatform universal package"
            : request.DisplayName.Trim();
        var targetHostProfile = NullIfWhiteSpace(request.TargetHostProfile);
        var tempRoot = CreateTempRoot("portal-universal-package-export");
        var downloadRoot = Path.Join(Path.GetTempPath(), "OpenModulePlatform", "portal-universal-package-downloads");
        Directory.CreateDirectory(downloadRoot);

        var packageFileName = $"{SanitizePathSegment(packageKey)}__{SanitizePathSegment(packageVersion)}.zip";
        var packagePath = Path.Join(downloadRoot, $"{Guid.NewGuid():N}-{packageFileName}");

        try
        {
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var items = new JsonArray();
                var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var exportedArtifactIds = new HashSet<int>();

                foreach (var moduleKey in request.ModuleKeys
                             .Where(static key => !string.IsNullOrWhiteSpace(key))
                             .Select(static key => key.Trim())
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))
                {
                    var definition = await _repo.GetAppliedModuleDefinitionDocumentAsync(moduleKey, ct)
                        ?? throw new InvalidOperationException($"No applied module definition exists for module '{moduleKey}'.");
                    if (string.IsNullOrWhiteSpace(definition.DefinitionJson))
                    {
                        throw new InvalidOperationException($"Applied module definition '{moduleKey}' has no JSON content to export.");
                    }

                    var definitionEntryName = $"{ModuleDefinitionFolder}/{SanitizePathSegment(moduleKey)}.module-definition.json";
                    await AddTextEntryAsync(archive, usedEntryNames, definitionEntryName, definition.DefinitionJson, ct);
                    items.Add(CreateUniversalPackageItem("module-definition", definitionEntryName, definition.DefinitionVersion));

                    if (request.IncludeArtifactsForSelectedModules)
                    {
                        var moduleArtifacts = await _repo.GetModuleArtifactPackagesAsync(
                            moduleKey,
                            request.IncludeAllArtifactVersions,
                            ct);
                        foreach (var artifact in moduleArtifacts.Where(artifact => !exportedArtifactIds.Contains(artifact.ArtifactId)))
                        {
                            exportedArtifactIds.Add(artifact.ArtifactId);
                            if (await AddArtifactPackageEntryAsync(
                                    archive,
                                    usedEntryNames,
                                    artifact.ArtifactId,
                                    artifact.ModuleKey,
                                    artifact.AppKey,
                                    artifact.PackageType,
                                    artifact.TargetName,
                                    artifact.Version,
                                    artifact.RelativePath,
                                    tempRoot,
                                    skipMissingPayload: true,
                                    ct) is { } artifactEntryName)
                            {
                                items.Add(CreateUniversalPackageItem("artifact-package", artifactEntryName, artifact.Version));
                            }
                        }
                    }
                }

                foreach (var artifactId in request.ArtifactIds.Distinct().Order())
                {
                    if (!exportedArtifactIds.Add(artifactId))
                    {
                        continue;
                    }

                    var artifact = await _repo.GetArtifactAsync(artifactId, ct)
                        ?? throw new InvalidOperationException($"Artifact {artifactId} was not found.");
                    var artifactEntryName = await AddArtifactPackageEntryAsync(
                        archive,
                        usedEntryNames,
                        artifact.ArtifactId,
                        artifact.ModuleKey,
                        artifact.AppKey,
                        artifact.PackageType,
                        artifact.TargetName,
                        artifact.Version,
                        artifact.RelativePath,
                        tempRoot,
                        skipMissingPayload: false,
                        ct);
                    if (artifactEntryName is not null)
                    {
                        items.Add(CreateUniversalPackageItem("artifact-package", artifactEntryName, artifact.Version));
                    }
                }

                foreach (var documentId in request.HostConfigurationDocumentIds.Distinct().Order())
                {
                    var row = await _repo.GetHostConfigurationJsonAsync(documentId, ct)
                        ?? throw new InvalidOperationException($"Host configuration {documentId} was not found.");
                    var entryName = BuildHostSpecificEntryName(
                        HostConfigurationsFolder,
                        row.HostKey,
                        $"{row.HostKey}__host-config__{row.ConfigurationVersion}.json");
                    await AddTextEntryAsync(archive, usedEntryNames, entryName, row.Json, ct);
                    items.Add(CreateUniversalPackageItem("host-config", entryName, row.ConfigurationVersion));
                }

                foreach (var documentId in request.ConfigOverlayDocumentIds.Distinct().Order())
                {
                    var row = await _repo.GetConfigOverlayJsonAsync(documentId, ct)
                        ?? throw new InvalidOperationException($"Config overlay {documentId} was not found.");
                    var entryName = BuildHostSpecificEntryName(
                        ConfigOverlaysFolder,
                        row.HostKey,
                        $"{row.HostKey}__{row.OverlayKey}__overlay__{row.OverlayVersion}.json");
                    await AddTextEntryAsync(archive, usedEntryNames, entryName, row.Json, ct);
                    items.Add(CreateUniversalPackageItem("config-overlay", entryName, row.OverlayVersion));
                }

                if (request.WidgetIds.Count > 0)
                {
                    var widgets = await _widgets.ExportWidgetsAsync(request.WidgetIds, ct);
                    var entryName = $"{WidgetsFolder}/{Path.GetFileName(widgets.FileName)}";
                    await AddBytesEntryAsync(archive, usedEntryNames, entryName, widgets.Content, ct);
                    items.Add(CreateUniversalPackageItem("dashboard-widget", entryName, widgets.PackageVersion));

                    if (request.IncludeWidgetRuntimeData)
                    {
                        var runtimeData = await _widgetRuntimeData.ExportAsync(request.WidgetIds, widgets.PackageVersion, ct);
                        if (runtimeData is not null)
                        {
                            var runtimeDataEntryName = $"{WidgetDataFolder}/{Path.GetFileName(runtimeData.FileName)}";
                            AddFileEntry(archive, usedEntryNames, runtimeData.PackagePath, runtimeDataEntryName);
                            items.Add(CreateUniversalPackageItem("widget-data", runtimeDataEntryName, runtimeData.PackageVersion));
                            TryDelete(Path.GetDirectoryName(runtimeData.PackagePath)!);
                        }
                    }
                }

                if (items.Count == 0)
                {
                    throw new InvalidOperationException("Select at least one object to export.");
                }

                await WriteUniversalPackageManifestAsync(
                    archive,
                    packageKey,
                    packageVersion,
                    displayName,
                    items,
                    ct,
                    request.Description,
                    targetHostProfile);
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
        QuickImportState? quickImportState,
        CancellationToken ct)
    {
        var saveResult = await _repo.SaveModuleDefinitionDocumentAsync(
            definition,
            options.ReplaceExistingModuleDefinition,
            ct);
        var appliedDefinition = await _repo.GetAppliedModuleDefinitionDocumentAsync(definition.ModuleKey, ct);
        var keepNewerAppliedDefinition = appliedDefinition is not null
            && ArtifactVersionComparer.Compare(appliedDefinition.DefinitionVersion, definition.DefinitionVersion) > 0;

        var applied = false;
        var repairCount = 0;
        if (options.ApplyModuleDefinition && !keepNewerAppliedDefinition)
        {
            if (options.ExecuteSqlRepairs && RequiresPreApplySqlRepairs(definition))
            {
                var repairResult = await _repo.ExecuteModuleDefinitionSqlRepairsAsync(
                    saveResult.ModuleDefinitionDocumentId,
                    ct);
                repairCount += repairResult.ExecutedCount;
            }

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
                repairCount += repairResult.ExecutedCount;
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

            if (quickImportState?.TryGetArtifactSkipMessage(plan.Identity, out var quickSkipMessage) == true)
            {
                artifactResults.Add(new PortableModulePackageArtifactImportResult(
                    Path.GetFileName(plan.Path),
                    "Skipped",
                    quickSkipMessage,
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

    private static bool RequiresPreApplySqlRepairs(ModuleDefinitionDocumentEditData definition)
    {
        // Platform core schema changes may be required by the apply step itself.
        // Run embedded idempotent repairs before applying that definition so old
        // installations can bridge schema gaps such as newly introduced columns.
        return string.Equals(definition.ModuleKey, "omp_core", StringComparison.OrdinalIgnoreCase);
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
            ValidateModuleDefinitionRequirement(package, compatibility);
            var contentHash = await ComputeDirectorySha256Async(package.ArtifactContentPath, ct);

            var existingIdentity = await _repo.FindArtifactByIdentityAsync(
                app.AppId,
                identity.Version,
                identity.PackageType,
                identity.TargetName,
                ct);

            if (existingIdentity is not null)
            {
                var existingMetadataHashMatches = string.Equals(
                    existingIdentity.Sha256,
                    contentHash,
                    StringComparison.OrdinalIgnoreCase);
                var existingMetadataHashMissing = string.IsNullOrWhiteSpace(existingIdentity.Sha256);
                if (!existingMetadataHashMatches && !existingMetadataHashMissing)
                {
                    if (!options.ReplaceExistingArtifacts)
                    {
                        return new PortableModulePackageArtifactImportResult(
                            identity.FileName,
                            "Failed",
                            $"An artifact with the same identity already exists with different content. Artifact: {app.AppKey} {identity.Version} ({identity.PackageType}, {identity.TargetName}). Existing SHA-256: {existingIdentity.Sha256}. Incoming SHA-256: {contentHash}. Bump the component version, rebuild the universal package, and re-import.",
                            existingIdentity.ArtifactId);
                    }
                }
                else
                {
                    var existingRelativePathSource = existingIdentity.RelativePath
                        ?? BuildRelativePath(compatibility, identity.TargetName, identity.PackageType, identity.Version);
                    var existingRelativePath = NormalizeRelativePath(existingRelativePathSource)
                        ?? throw new InvalidOperationException("The existing artifact relative path is invalid.");
                    finalPath = ResolveUnderRoot(storeRoot, existingRelativePath);

                    var restoredMissingContent = false;
                    if (File.Exists(finalPath))
                    {
                        return new PortableModulePackageArtifactImportResult(
                            identity.FileName,
                            "Failed",
                            $"The existing artifact path is a file, not a directory: {existingRelativePath}",
                            existingIdentity.ArtifactId);
                    }

                    if (Directory.Exists(finalPath))
                    {
                        var existingContentHash = await ComputeDirectorySha256Async(finalPath, ct);
                        if (!string.Equals(existingContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                        {
                            var mismatchMessage = existingMetadataHashMissing
                                ? $"The existing artifact metadata has no content hash, and the artifact store path contains different content. Artifact: {app.AppKey} {identity.Version} ({identity.PackageType}, {identity.TargetName}). Path: {existingRelativePath}. Existing content SHA-256: {existingContentHash}. Incoming SHA-256: {contentHash}. Bump the component version, rebuild the universal package, and re-import."
                                : $"The existing artifact metadata matches this package, but the artifact store path contains different content. Artifact: {app.AppKey} {identity.Version} ({identity.PackageType}, {identity.TargetName}). Path: {existingRelativePath}. Expected SHA-256 (metadata): {existingIdentity.Sha256}. Actual SHA-256 (disk): {existingContentHash}. Bump the component version, rebuild the universal package, and re-import.";
                            return new PortableModulePackageArtifactImportResult(
                                identity.FileName,
                                "Failed",
                                mismatchMessage,
                                existingIdentity.ArtifactId);
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                        MoveFileOrDirectory(package.ArtifactContentPath, finalPath);
                        movedArtifactToFinal = true;
                        restoredMissingContent = true;
                    }

                    var completedMissingMetadata = false;
                    if (existingMetadataHashMissing)
                    {
                        try
                        {
                            await _repo.SaveArtifactAsync(
                                new ArtifactEditData
                                {
                                    ArtifactId = existingIdentity.ArtifactId,
                                    AppId = app.AppId,
                                    Version = identity.Version,
                                    PackageType = identity.PackageType,
                                    TargetName = identity.TargetName,
                                    RelativePath = existingRelativePath,
                                    Sha256 = contentHash,
                                    IsEnabled = true
                                },
                                ct);
                            completedMissingMetadata = true;
                        }
                        catch (SqlException ex)
                        {
                            if (movedArtifactToFinal)
                            {
                                TryDelete(finalPath);
                            }

                            return new PortableModulePackageArtifactImportResult(
                                identity.FileName,
                                "Failed",
                                $"The existing artifact metadata row could not be completed: {ex.Message}",
                                existingIdentity.ArtifactId);
                        }
                    }

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

                    var status = configurationFileCount > 0 || restoredMissingContent || completedMissingMetadata
                        ? "Updated"
                        : "Skipped";
                    var message = configurationFileCount > 0
                        ? $"The same artifact identity and content already exists. Updated {configurationFileCount} configuration file row(s)."
                        : completedMissingMetadata
                            ? "The artifact identity already existed without a stored content hash. Completed its metadata from this package."
                            : "The same artifact identity and content already exists.";
                    if (restoredMissingContent)
                    {
                        message = AppendWarning(
                            message,
                            $"Restored missing artifact files below the artifact store path: {existingRelativePath}");
                    }

                    return new PortableModulePackageArtifactImportResult(
                        identity.FileName,
                        status,
                        AppendWarning(
                            message,
                            existingWarning),
                        existingIdentity.ArtifactId);
                }
            }

            if (existingIdentity is not null && !options.ReplaceExistingArtifacts)
            {
                return new PortableModulePackageArtifactImportResult(
                    identity.FileName,
                    "Failed",
                    $"An artifact with the same identity already exists with different content. Artifact: {app.AppKey} {identity.Version} ({identity.PackageType}, {identity.TargetName}). Existing SHA-256: {existingIdentity.Sha256}. Incoming SHA-256: {contentHash}. Bump the component version, rebuild the universal package, and re-import.",
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
                        $"The target artifact path already exists with different content. Artifact: {app.AppKey} {identity.Version} ({identity.PackageType}, {identity.TargetName}). Path: {relativePath}. Existing SHA-256: {existingContentHash}. Incoming SHA-256: {contentHash}. Bump the component version, rebuild the universal package, and re-import.",
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

    private static IReadOnlyList<ArtifactPackageFile> SelectLatestArtifactFilesBySlot(IEnumerable<ArtifactPackageFile> artifacts)
    {
        return artifacts
            .GroupBy(static artifact => BuildArtifactSlotKey(artifact), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static artifact => artifact.Version, ArtifactVersionComparer.Instance)
                .ThenBy(static artifact => artifact.FileName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(static artifact => artifact.AppKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static artifact => artifact.PackageType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static artifact => artifact.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> SelectLatestArtifactPathsBySlot(
        IEnumerable<(string Path, ArtifactPackageFile Artifact)> artifacts)
    {
        return artifacts
            .GroupBy(static artifact => BuildArtifactSlotKey(artifact.Artifact), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static artifact => artifact.Artifact.Version, ArtifactVersionComparer.Instance)
                .ThenBy(static artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(static artifact => artifact.Artifact.AppKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static artifact => artifact.Artifact.PackageType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static artifact => artifact.Artifact.TargetName, StringComparer.OrdinalIgnoreCase)
            .Select(static artifact => artifact.Path)
            .ToList();
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

    private static JsonObject CreateUniversalPackageItem(string kind, string path, string? version = null)
    {
        var item = new JsonObject
        {
            ["kind"] = kind,
            ["path"] = path
        };
        if (!string.IsNullOrWhiteSpace(version))
        {
            item["version"] = version.Trim();
        }

        return item;
    }

    private async Task<string?> AddArtifactPackageEntryAsync(
        ZipArchive archive,
        HashSet<string> usedEntryNames,
        int artifactId,
        string moduleKey,
        string appKey,
        string packageType,
        string? targetName,
        string version,
        string? relativePath,
        string tempRoot,
        bool skipMissingPayload,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || string.IsNullOrWhiteSpace(targetName))
        {
            if (skipMissingPayload)
            {
                return null;
            }

            throw new InvalidOperationException($"Artifact {artifactId} cannot be exported because its payload path or target name is missing.");
        }

        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        if (normalizedRelativePath is null)
        {
            if (skipMissingPayload)
            {
                return null;
            }

            throw new InvalidOperationException($"Artifact {artifactId} has an invalid relative payload path.");
        }

        var storeRoot = RequireConfiguredRoot(_options.ArtifactStoreRoot, "ArtifactStoreRoot");
        var payloadPath = ResolveUnderRoot(storeRoot, normalizedRelativePath);
        if (!Directory.Exists(payloadPath))
        {
            if (skipMissingPayload)
            {
                return null;
            }

            throw new InvalidOperationException($"Artifact payload path does not exist: '{payloadPath}'.");
        }

        var artifactPackagePath = Path.Join(
            tempRoot,
            $"{Guid.NewGuid():N}.artifact.zip");
        var configurationFiles = await _repo.GetArtifactConfigurationFileContentsAsync(artifactId, ct);
        new ArtifactPackageWriter().CreateFromPayloadDirectory(
            payloadPath,
            artifactPackagePath,
            configurationFiles
                .Select(file => new ArtifactPackageConfigurationFile(file.RelativePath, file.FileContent))
                .ToArray());

        var artifactFileName = CreateArtifactPackageFileName(
            moduleKey,
            appKey,
            packageType,
            targetName,
            version);
        var artifactEntryName = $"{ArtifactsFolder}/{artifactFileName}";
        AddFileEntry(archive, usedEntryNames, artifactPackagePath, artifactEntryName);
        File.Delete(artifactPackagePath);
        return artifactEntryName;
    }

    private static async Task AddTextEntryAsync(
        ZipArchive archive,
        HashSet<string> usedEntryNames,
        string entryName,
        string content,
        CancellationToken ct)
        => await AddBytesEntryAsync(
            archive,
            usedEntryNames,
            entryName,
            Utf8NoBom.GetBytes(content),
            ct);

    private static async Task AddBytesEntryAsync(
        ZipArchive archive,
        HashSet<string> usedEntryNames,
        string entryName,
        byte[] content,
        CancellationToken ct)
    {
        var normalizedEntryName = NormalizeUniversalPackageSelectionPath(entryName);
        if (!usedEntryNames.Add(normalizedEntryName))
        {
            return;
        }

        var entry = archive.CreateEntry(normalizedEntryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(content.AsMemory(), ct);
    }

    private static void AddFileEntry(
        ZipArchive archive,
        HashSet<string> usedEntryNames,
        string sourcePath,
        string entryName)
    {
        var normalizedEntryName = NormalizeUniversalPackageSelectionPath(entryName);
        if (!usedEntryNames.Add(normalizedEntryName))
        {
            return;
        }

        archive.CreateEntryFromFile(
            sourcePath,
            normalizedEntryName,
            CompressionLevel.Optimal);
    }

    private static string BuildHostSpecificEntryName(
        string objectFolder,
        string hostKey,
        string fileName)
        => $"{objectFolder}/{SanitizePathSegment(hostKey)}/{Path.GetFileName(fileName)}";

    private static async Task WriteUniversalPackageManifestAsync(
        ZipArchive archive,
        string packageKey,
        string packageVersion,
        string displayName,
        JsonArray items,
        CancellationToken ct,
        string? description = null,
        string? targetHostProfile = null)
    {
        var manifest = new JsonObject
        {
            ["formatVersion"] = 1,
            ["objectType"] = "universal-module-package",
            ["packageKey"] = packageKey,
            ["packageVersion"] = packageVersion,
            ["displayName"] = displayName,
            ["description"] = NullIfWhiteSpace(description),
            ["targetHostProfile"] = NullIfWhiteSpace(targetHostProfile),
            ["items"] = items
        };

        var entry = archive.CreateEntry(
            UniversalModulePackageReader.ManifestEntryName,
            CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(manifest.ToJsonString(JsonOptions).AsMemory(), ct);
    }

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

    private static string NormalizeUniversalPackageSelectionPath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("Universal package item paths must be relative paths.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (segments.Length == 0
            || segments.Any(static segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("Universal package item paths must stay inside the package.");
        }

        return string.Join('/', segments);
    }

    private static string GetUniversalPackageStagingRoot()
    {
        var root = Path.Join(Path.GetTempPath(), "OpenModulePlatform", "portal-universal-package-staging");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveStagedUniversalPackagePath(string token)
    {
        var cleanToken = Path.GetFileName(token);
        if (string.IsNullOrWhiteSpace(cleanToken)
            || !cleanToken.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The staged universal package token is invalid.");
        }

        var root = GetUniversalPackageStagingRoot();
        var path = Path.GetFullPath(Path.Join(root, cleanToken));
        var normalizedRoot = Path.EndsInDirectorySeparator(root)
            ? Path.GetFullPath(root)
            : Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The staged universal package token is invalid.");
        }

        return path;
    }

    private static void CleanupOldStagedUniversalPackages()
    {
        var root = GetUniversalPackageStagingRoot();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                if (age > TimeSpan.FromHours(6))
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Staging cleanup is best-effort; explicit import still validates the selected token.
        }
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

    private static void ValidateModuleDefinitionRequirement(
        ArtifactPackageExtractionResult package,
        ArtifactCompatibilitySlot compatibility)
    {
        if (string.IsNullOrWhiteSpace(package.MinModuleDefinitionVersion))
        {
            return;
        }

        if (ArtifactVersionComparer.Compare(compatibility.DefinitionVersion, package.MinModuleDefinitionVersion) < 0)
        {
            throw new InvalidOperationException(
                $"Artifact package requires module definition '{compatibility.ModuleKey}' version {package.MinModuleDefinitionVersion} or later. " +
                $"The currently applied definition is {compatibility.DefinitionVersion}.");
        }
    }

    private static bool IsExpectedUniversalImportFailure(Exception exception)
        => exception is InvalidOperationException
            or InvalidDataException
            or JsonException
            or IOException
            or SqlException
            or UnauthorizedAccessException
            or System.Security.SecurityException;

    private static string BuildArtifactSlotKey(ArtifactPackageFile artifact)
        => string.Join('\u001f', artifact.ModuleKey, artifact.AppKey, artifact.PackageType, artifact.TargetName);

    private static string BuildArtifactActivationKey(ArtifactPackageFile artifact)
        => string.Join('\u001f', BuildArtifactSlotKey(artifact), artifact.Version);

    private static bool IsVersionInRange(string version, string? minVersion, string? maxVersion)
    {
        if (!string.IsNullOrWhiteSpace(minVersion)
            && ArtifactVersionComparer.Compare(version, minVersion) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(maxVersion)
            && ArtifactVersionComparer.Compare(version, maxVersion) > 0)
        {
            return false;
        }

        return true;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string AppendWarning(string current, string next)
        => string.IsNullOrWhiteSpace(current) ? next : current + " " + next;

    private sealed record AvailableModuleDefinitionFile(
        string Path,
        ModuleDefinitionSummary? Summary);

    private sealed record ModuleDefinitionSummary(
        string ModuleKey,
        string DefinitionVersion,
        IReadOnlyList<ModuleDefinitionCompatibilityEditData> CompatibleArtifacts);

    private sealed record ArtifactImportPlan(
        string Path,
        ArtifactPackageFile? Identity,
        string? SkipMessage);

    private sealed class QuickImportState
    {
        private readonly Dictionary<string, string> _moduleDefinitionVersions;
        private readonly Dictionary<string, string> _artifactVersions;
        private readonly Dictionary<string, string> _hostConfigurationVersions;
        private readonly Dictionary<string, string> _configOverlayVersions;

        private QuickImportState(
            Dictionary<string, string> moduleDefinitionVersions,
            Dictionary<string, string> artifactVersions,
            Dictionary<string, string> hostConfigurationVersions,
            Dictionary<string, string> configOverlayVersions)
        {
            _moduleDefinitionVersions = moduleDefinitionVersions;
            _artifactVersions = artifactVersions;
            _hostConfigurationVersions = hostConfigurationVersions;
            _configOverlayVersions = configOverlayVersions;
        }

        public static async Task<QuickImportState> CreateAsync(OmpAdminRepository repo, CancellationToken ct)
        {
            var moduleDefinitionVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in await repo.GetModuleDefinitionDocumentsAsync(ct))
            {
                AddLatest(moduleDefinitionVersions, BuildKey(row.ModuleKey), row.DefinitionVersion);
            }

            var artifactVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in await repo.GetArtifactsAsync(ct))
            {
                AddLatest(
                    artifactVersions,
                    BuildKey(row.ModuleKey, row.AppKey, row.PackageType, row.TargetName),
                    row.Version);
            }

            var hostConfigurationVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in await repo.GetHostConfigurationDocumentsAsync(ct))
            {
                AddLatest(hostConfigurationVersions, BuildKey(row.HostKey), row.ConfigurationVersion);
            }

            var configOverlayVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in await repo.GetConfigOverlayDocumentsAsync(ct))
            {
                AddLatest(configOverlayVersions, BuildKey(row.HostKey, row.OverlayKey), row.OverlayVersion);
            }

            return new QuickImportState(
                moduleDefinitionVersions,
                artifactVersions,
                hostConfigurationVersions,
                configOverlayVersions);
        }

        public bool TryGetModuleDefinitionSkipMessage(ModuleDefinitionDocumentEditData definition, out string message)
            => TryGetSkipMessage(
                _moduleDefinitionVersions,
                BuildKey(definition.ModuleKey),
                definition.DefinitionVersion,
                $"module definition {definition.ModuleKey}",
                out message);

        public bool TryGetArtifactSkipMessage(ArtifactPackageFile artifact, out string message)
            => TryGetSkipMessage(
                _artifactVersions,
                BuildKey(artifact.ModuleKey, artifact.AppKey, artifact.PackageType, artifact.TargetName),
                artifact.Version,
                $"artifact {artifact.ModuleKey}/{artifact.AppKey}/{artifact.PackageType}/{artifact.TargetName}",
                out message);

        public bool TryGetHostConfigurationSkipMessage(PortableHostConfigurationDocument hostConfiguration, out string message)
            => TryGetSkipMessage(
                _hostConfigurationVersions,
                BuildKey(hostConfiguration.HostKey),
                hostConfiguration.ConfigurationVersion,
                $"host configuration {hostConfiguration.HostKey}",
                out message);

        public bool TryGetConfigOverlaySkipMessage(PortableConfigOverlayDocument overlay, out string message)
            => TryGetSkipMessage(
                _configOverlayVersions,
                BuildKey(overlay.HostKey, overlay.OverlayKey),
                overlay.OverlayVersion,
                $"config overlay {overlay.HostKey}/{overlay.OverlayKey}",
                out message);

        private static bool TryGetSkipMessage(
            IReadOnlyDictionary<string, string> installedVersions,
            string key,
            string packageVersion,
            string label,
            out string message)
        {
            if (installedVersions.TryGetValue(key, out var installedVersion)
            && ArtifactVersionComparer.Compare(installedVersion, packageVersion) >= 0)
            {
                message = $"Quick import skipped {label} because package version {packageVersion} is not newer than installed version {installedVersion}.";
                return true;
            }

            message = string.Empty;
            return false;
        }

        private static void AddLatest(Dictionary<string, string> versions, string key, string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return;
            }

        if (!versions.TryGetValue(key, out var currentVersion)
            || ArtifactVersionComparer.Compare(version, currentVersion) > 0)
            {
                versions[key] = version;
            }
        }

        private static string BuildKey(params string?[] parts)
            => string.Join('\u001f', parts.Select(NormalizeKeyPart));

        private static string NormalizeKeyPart(string? value)
            => value?.Trim().ToUpperInvariant() ?? string.Empty;
    }

}

public sealed record PortableModulePackageImportOptions(
    bool ApplyModuleDefinition,
    bool ExecuteSqlRepairs,
    bool AllowTemporaryIncompatibleArtifacts,
    bool ReplaceExistingModuleDefinition,
    bool ReplaceExistingArtifacts,
    bool ReplaceExistingDashboardWidgets,
    bool CopyConfigurationFilesFromPreviousVersion,
    bool UseArtifactsImmediately,
    bool QuickImport = false);

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

public sealed record PackageLibraryExportResult(
    string ModuleKey,
    string DefinitionVersion,
    string ModuleDefinitionFileName,
    int ArtifactCount,
    int ExportedArtifactCount,
    int SkippedArtifactCount,
    IReadOnlyList<string> Messages);

public sealed record UniversalPackageExportRequest(
    string PackageKey,
    string PackageVersion,
    string DisplayName,
    string? Description,
    string? TargetHostProfile,
    IReadOnlyList<string> ModuleKeys,
    bool IncludeArtifactsForSelectedModules,
    bool IncludeAllArtifactVersions,
    IReadOnlyList<int> ArtifactIds,
    IReadOnlyList<int> HostConfigurationDocumentIds,
    IReadOnlyList<int> ConfigOverlayDocumentIds,
    IReadOnlyList<int> WidgetIds,
    bool IncludeWidgetRuntimeData);

public sealed record UniversalPackagePreviewResult(
    string Token,
    string SourceName,
    string? PackageKey,
    string? PackageVersion,
    string? DisplayName,
    string? TargetHostProfile,
    IReadOnlyList<UniversalPackagePreviewItem> Items);

public sealed record UniversalPackagePreviewItem(
    string Kind,
    string Path,
    string SourceName);

public sealed record UniversalPackageImportResult(
    string SourceName,
    string? PackageKey,
    string? PackageVersion,
    string? TargetHostProfile,
    IReadOnlyList<UniversalPackageImportItemResult> Items)
{
    public int ImportedCount => Items.Count(static item =>
        item.Status is "Applied" or "Stored" or "Imported" or "Replaced" or "Updated");

    public int FailedCount => Items.Count(static item => item.Status == "Failed");

    public int SkippedCount => Items.Count(static item => item.Status == "Skipped");
}

public sealed record UniversalPackageImportItemResult(
    string Kind,
    string Path,
    string Status,
    string? Message);
