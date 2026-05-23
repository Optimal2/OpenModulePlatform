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
        var tempRoot = Path.Join(storeRoot, ".hostagent-import-staging");
        Directory.CreateDirectory(tempRoot);

        var tempImportPath = Path.Join(tempRoot, $"{Guid.NewGuid():N}{Path.GetExtension(importPath)}");
        var stagingPath = Path.Join(tempRoot, $"import-{Guid.NewGuid():N}");

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
                    Path.Join(stagingPath, "artifact"),
                    cancellationToken);
                _logger.LogInformation(
                    "Imported artifact zip. Zip={ImportPath}, Status={Status}, ArtifactId={ArtifactId}, Version={Version}, RelativePath={RelativePath}, AdoptedExistingContent={AdoptedExistingContent}, CopiedConfigurationFiles={CopiedConfigurationFiles}, TemplateRows={TemplateRows}, AppInstanceRows={AppInstanceRows}, WorkerInstanceRows={WorkerInstanceRows}, HostAgentDesiredRows={HostAgentDesiredRows}, Message={Message}",
                    importPath,
                    result.Status,
                    result.ArtifactId,
                    result.Version,
                    result.RelativePath,
                    result.AdoptedExistingContent,
                    result.CopiedConfigurationFileCount,
                    result.TemplateAppRowsUpdated,
                    result.AppInstanceRowsUpdated,
                    result.WorkerInstanceRowsUpdated,
                    result.HostAgentDesiredRowsUpdated,
                    result.Message);
            }
            else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ImportModulePackageZipAsync(
                    settings,
                    importSettings,
                    tempImportPath,
                    Path.Join(stagingPath, "module-package"),
                    cancellationToken);
                _logger.LogInformation(
                    "Imported module package from HostAgent import folder. File={ImportPath}, Module={ModuleKey}, Version={DefinitionVersion}, DocumentId={DocumentId}, Applied={Applied}, SqlRepairCount={SqlRepairCount}, ImportedArtifacts={ImportedArtifacts}, SkippedArtifacts={SkippedArtifacts}, FailedArtifacts={FailedArtifacts}",
                    importPath,
                    result.ModuleKey,
                    result.DefinitionVersion,
                    result.ModuleDefinitionDocumentId,
                    result.Applied,
                    result.SqlRepairCount,
                    result.ImportedArtifactCount,
                    result.SkippedArtifactCount,
                    result.FailedArtifactCount);
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
        var artifactPaths = Directory.EnumerateFiles(extractionRoot, "*.zip", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var artifactPlans = CreateModulePackageArtifactPlans(definition, artifactPaths);
        var activationKeys = SelectLatestActivationKeys(artifactPlans);
        var artifactResults = new List<ModulePackageArtifactImportResult>();

        foreach (var plan in artifactPlans)
        {
            if (!string.IsNullOrWhiteSpace(plan.FailureMessage))
            {
                artifactResults.Add(new ModulePackageArtifactImportResult(
                    Path.GetFileName(plan.Path),
                    "Failed",
                    plan.FailureMessage,
                    null));
                _logger.LogWarning(
                    "Rejected artifact from HostAgent module package import. File={ArtifactFile}, Reason={Reason}",
                    Path.GetFileName(plan.Path),
                    plan.FailureMessage);
                continue;
            }

            if (plan.Metadata is null || !string.IsNullOrWhiteSpace(plan.SkipMessage))
            {
                artifactResults.Add(new ModulePackageArtifactImportResult(
                    Path.GetFileName(plan.Path),
                    "Skipped",
                    plan.SkipMessage ?? "The artifact package filename does not follow the standard module__app__packageType__target__version.zip format.",
                    null));
                _logger.LogInformation(
                    "Skipped artifact from HostAgent module package import. File={ArtifactFile}, Reason={Reason}",
                    Path.GetFileName(plan.Path),
                    plan.SkipMessage);
                continue;
            }

            var activateArtifact = activationKeys.Contains(BuildArtifactActivationKey(plan.Metadata));
            try
            {
                var artifact = await ImportArtifactPackageAsync(
                    settings,
                    importSettings,
                    plan.Metadata,
                    plan.Path,
                    Path.Join(extractionRoot, ".artifact-staging", Guid.NewGuid().ToString("N")),
                    cancellationToken,
                    allowExistingIdentical: true,
                    applyToMatchingApplications: activateArtifact);

                var status = artifact.Status;
                var message = artifact.Message;
                if (!activateArtifact && status is "Imported" or "Replaced" or "Skipped")
                {
                    message = AppendWarning(
                        message ?? string.Empty,
                        "The artifact was kept as a historical package and was not selected because a newer compatible artifact for the same app slot exists in this module package.");
                }

                artifactResults.Add(new ModulePackageArtifactImportResult(
                    Path.GetFileName(plan.Path),
                    status,
                    message,
                    artifact));
            }
            catch (Exception ex) when (IsExpectedImportFailure(ex))
            {
                var status = IsArtifactCompatibilityFailure(ex) ? "Skipped" : "Failed";
                artifactResults.Add(new ModulePackageArtifactImportResult(
                    Path.GetFileName(plan.Path),
                    status,
                    ex.Message,
                    null));
                if (status == "Skipped")
                {
                    _logger.LogInformation(
                        "Skipped artifact from HostAgent module package import. File={ArtifactFile}, Reason={Reason}",
                        Path.GetFileName(plan.Path),
                        ex.Message);
                }
                else
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to import artifact from HostAgent module package import. File={ArtifactFile}",
                        Path.GetFileName(plan.Path));
                }
            }
        }

        var failedArtifacts = artifactResults
            .Where(static result => result.Status == "Failed")
            .ToList();
        if (failedArtifacts.Count > 0)
        {
            throw new InvalidOperationException(
                "Module package import completed with failed artifact package(s): " +
                string.Join(
                    " | ",
                    failedArtifacts.Select(static result => $"{result.FileName}: {result.Message}")));
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
        var appliedVersion = await _repository.GetAppliedModuleDefinitionVersionAsync(
            definition.ModuleKey,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(appliedVersion)
            && CompareArtifactVersions(appliedVersion, definition.DefinitionVersion) > 0)
        {
            return new ModuleDefinitionImportResult(
                definition.ModuleKey,
                definition.DefinitionVersion,
                saveResult.ModuleDefinitionDocumentId,
                Applied: false,
                SqlRepairCount: 0);
        }

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
        CancellationToken cancellationToken,
        bool allowExistingIdentical = false,
        bool applyToMatchingApplications = true)
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

            finalPath = ResolveUnderRoot(storeRoot, relativePath);
            var package = new ArtifactPackageExtractor(ValidateArtifactEntryIsNotRuntimeConfiguration)
                .Extract(packagePath, stagingPath);
            var contentHash = await ComputeDirectorySha256Async(package.ArtifactContentPath, cancellationToken);

            var existingIdentity = await _repository.FindImportedArtifactByIdentityAsync(
                app.AppId,
                metadata.Version,
                metadata.PackageType,
                metadata.TargetName,
                cancellationToken);
            if (existingIdentity is not null)
            {
                if (allowExistingIdentical
                    && string.Equals(existingIdentity.Sha256, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    var existingApplication = applyToMatchingApplications
                        ? await _repository.ApplyImportedArtifactToMatchingApplicationsAsync(
                            existingIdentity.ArtifactId,
                            cancellationToken)
                        : (TemplateAppRowsUpdated: 0, AppInstanceRowsUpdated: 0, WorkerInstanceRowsUpdated: 0);
                    var existingHostAgentDesiredRows = 0;
                    if (applyToMatchingApplications
                        && string.Equals(metadata.PackageType, "host-agent", StringComparison.OrdinalIgnoreCase))
                    {
                        existingHostAgentDesiredRows = await _repository.ApplyImportedHostAgentArtifactToCurrentHostAsync(
                            existingIdentity.ArtifactId,
                            settings.ResolveHostKey(),
                            settings.SelfUpgrade.ServiceNamePrefix,
                            settings.SelfUpgrade.InstallRoot,
                            cancellationToken);
                    }

                    return new ArtifactZipImportResult(
                        existingIdentity.ArtifactId,
                        existingIdentity.Version,
                        existingIdentity.RelativePath ?? relativePath,
                        0,
                        existingApplication.TemplateAppRowsUpdated,
                        existingApplication.AppInstanceRowsUpdated,
                        existingApplication.WorkerInstanceRowsUpdated,
                        existingHostAgentDesiredRows,
                        AdoptedExistingContent: true,
                        Status: "Skipped",
                        Message: "The same artifact identity and content already exists.");
                }

                throw new InvalidOperationException(
                    "An artifact for this app, package type, target, and version already exists: " +
                    $"{existingIdentity.AppKey} {existingIdentity.Version} ({existingIdentity.PackageType}). " +
                    "Use a new version number for changed artifact content.");
            }

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

            var application = applyToMatchingApplications
                ? await _repository.ApplyImportedArtifactToMatchingApplicationsAsync(
                    artifactId,
                    cancellationToken)
                : (TemplateAppRowsUpdated: 0, AppInstanceRowsUpdated: 0, WorkerInstanceRowsUpdated: 0);
            var hostAgentDesiredRows = 0;
            if (applyToMatchingApplications
                && string.Equals(metadata.PackageType, "host-agent", StringComparison.OrdinalIgnoreCase))
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

    private static IReadOnlyList<ModulePackageArtifactPlan> CreateModulePackageArtifactPlans(
        ModuleDefinitionImportDocument definition,
        IReadOnlyList<string> artifactPaths)
    {
        var plans = new List<ModulePackageArtifactPlan>();
        foreach (var artifactPath in artifactPaths)
        {
            var metadata = TryParseFilenameMetadata(Path.GetFileName(artifactPath));
            if (metadata is null)
            {
                plans.Add(new ModulePackageArtifactPlan(
                    artifactPath,
                    null,
                    null,
                    "The artifact package filename does not follow the standard module__app__packageType__target__version.zip format."));
                continue;
            }

            if (!metadata.ModuleKey.Equals(definition.ModuleKey, StringComparison.OrdinalIgnoreCase))
            {
                plans.Add(new ModulePackageArtifactPlan(
                    artifactPath,
                    metadata,
                    null,
                    $"The artifact belongs to module '{metadata.ModuleKey}', not '{definition.ModuleKey}'."));
                continue;
            }

            if (!IsCompatibleWithDefinition(metadata, definition.CompatibleArtifacts))
            {
                plans.Add(new ModulePackageArtifactPlan(
                    artifactPath,
                    metadata,
                    $"Artifact version {metadata.Version} is not compatible with module definition '{definition.ModuleKey}' version {definition.DefinitionVersion}.",
                    null));
                continue;
            }

            plans.Add(new ModulePackageArtifactPlan(artifactPath, metadata, null, null));
        }

        return plans;
    }

    private static HashSet<string> SelectLatestActivationKeys(IReadOnlyList<ModulePackageArtifactPlan> plans)
    {
        return plans
            .Where(static plan => plan.Metadata is not null && string.IsNullOrWhiteSpace(plan.SkipMessage))
            .GroupBy(static plan => BuildArtifactSlotKey(plan.Metadata!), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static plan => plan.Metadata!.Version, ArtifactVersionComparer.Instance)
                .First())
            .Select(static plan => BuildArtifactActivationKey(plan.Metadata!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsCompatibleWithDefinition(
        FilenameMetadata artifact,
        IReadOnlyList<ModuleDefinitionArtifactCompatibilityEntry> compatibility)
        => compatibility.Any(slot =>
            slot.AppKey.Equals(artifact.AppKey, StringComparison.OrdinalIgnoreCase)
            && slot.PackageType.Equals(artifact.PackageType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(slot.TargetName ?? string.Empty, artifact.TargetName, StringComparison.OrdinalIgnoreCase)
            && IsVersionInRange(artifact.Version, slot.MinArtifactVersion, slot.MaxArtifactVersion));

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

    private static string BuildArtifactSlotKey(FilenameMetadata artifact)
        => string.Join('\u001f', artifact.ModuleKey, artifact.AppKey, artifact.PackageType, artifact.TargetName);

    private static string BuildArtifactActivationKey(FilenameMetadata artifact)
        => string.Join('\u001f', BuildArtifactSlotKey(artifact), artifact.Version);

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
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, relativePath));
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
            var destination = Path.Join(
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

    private static bool IsArtifactCompatibilityFailure(Exception exception)
        => exception is InvalidOperationException
            && (exception.Message.Contains("not compatible with module definition", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("does not allow artifacts", StringComparison.OrdinalIgnoreCase));

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

    private static string AppendWarning(string current, string next)
        => string.IsNullOrWhiteSpace(current) ? next : current + " " + next;

    private sealed record ModulePackageArtifactPlan(
        string Path,
        FilenameMetadata? Metadata,
        string? SkipMessage,
        string? FailureMessage);

    private sealed class ArtifactVersionComparer : IComparer<string>
    {
        public static readonly ArtifactVersionComparer Instance = new();

        public int Compare(string? x, string? y)
            => CompareArtifactVersions(x ?? string.Empty, y ?? string.Empty);
    }

    private sealed record FilenameMetadata(
        string ModuleKey,
        string AppKey,
        string PackageType,
        string TargetName,
        string Version);
}
