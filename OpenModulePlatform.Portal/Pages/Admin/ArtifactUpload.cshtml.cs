// File: OpenModulePlatform.Portal/Pages/Admin/ArtifactUpload.cshtml.cs
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Uploads a single immutable artifact zip into the central HostAgent artifact store.
/// </summary>
public sealed class ArtifactUploadModel : OmpPortalPageModel
{
    private const string FilenameFormat = "moduleKey__appKey__packageType__targetName__version.zip";
    private const int HashBufferSize = 1024 * 128;

    private static readonly Regex MetadataTokenPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._+-]*$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;
    private readonly ArtifactUploadOptions _uploadOptions;

    public ArtifactUploadModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo,
        IOptions<ArtifactUploadOptions> uploadOptions)
        : base(options, rbac)
    {
        _repo = repo;
        _uploadOptions = uploadOptions.Value;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<ArtifactAppOption> AppOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> PackageTypeOptions => ArtifactPackageTypes.CreateOptions(T);

    public string MetadataFilenameFormat => FilenameFormat;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles("Upload artifact");
        Input.PackageType = "web-app";

        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles("Upload artifact");

        var filenameMetadata = ApplyFilenameMetadataIfUseful();
        ValidateInput(filenameMetadata);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var storeRoot = ResolveArtifactStoreRoot();
        if (storeRoot is null)
        {
            return Page();
        }

        var version = Input.Version.Trim();
        var packageType = Input.PackageType.Trim();
        var targetName = Clean(Input.TargetName);
        ArtifactCompatibilitySlot compatibility;
        try
        {
            compatibility = await _repo.RequireCompatibleArtifactSlotAsync(
                Input.AppId,
                version,
                packageType,
                targetName,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }

        var relativePathSource = string.IsNullOrWhiteSpace(compatibility.RelativePathTemplate)
            ? FillBlank(Input.RelativePath, BuildRelativePath(compatibility, targetName ?? string.Empty, packageType, version))
            : BuildRelativePath(compatibility, targetName ?? string.Empty, packageType, version);
        var relativePath = NormalizeRelativePath(relativePathSource);
        if (relativePath is null)
        {
            ModelState.AddModelError(
                nameof(Input.RelativePath),
                T("Relative path must stay inside the artifact store and cannot contain rooted paths or parent directory segments."));
            return Page();
        }

        var tempRoot = Path.Join(storeRoot, ".portal-upload-staging");
        Directory.CreateDirectory(tempRoot);

        var tempZipPath = Path.Join(tempRoot, $"{Guid.NewGuid():N}.zip");
        var stagingPath = Path.Join(tempRoot, $"artifact-{Guid.NewGuid():N}");
        var finalPath = ResolveUnderRoot(storeRoot, relativePath);
        var backupPath = Path.Join(tempRoot, $"artifact-backup-{Guid.NewGuid():N}");
        var movedExistingToBackup = false;

        try
        {
            await CopyUploadToTempZipAsync(Input.ZipFile!, tempZipPath, ct);
            var package = new ArtifactPackageExtractor(ValidateArtifactEntryIsNotRuntimeConfiguration)
                .Extract(tempZipPath, stagingPath);

            var contentHash = await ComputeDirectorySha256Async(package.ArtifactContentPath, ct);
            var existingIdentity = await _repo.FindArtifactByIdentityAsync(
                Input.AppId,
                version,
                packageType,
                targetName,
                ct);

            if (existingIdentity is not null)
            {
                if (string.Equals(existingIdentity.Sha256, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    if (package.ConfigurationFiles.Count > 0)
                    {
                        int configurationFileCount;
                        try
                        {
                            configurationFileCount = await _repo.ReplaceArtifactConfigurationFilesAsync(
                                existingIdentity.ArtifactId,
                                package.ConfigurationFiles,
                                ct);
                        }
                        catch (SqlException ex)
                        {
                            ModelState.AddModelError(
                                string.Empty,
                                T($"Configuration files from the artifact package could not be saved: {ex.Message}"));
                            return Page();
                        }

                        ArtifactApplicationResult? existingApplicationResult = null;
                        if (Input.UseArtifactImmediately)
                        {
                            try
                            {
                                existingApplicationResult = await _repo.ApplyArtifactToMatchingApplicationsAsync(
                                    existingIdentity.ArtifactId,
                                    ct);
                            }
                            catch (SqlException)
                            {
                                StatusMessage = BuildExistingArtifactConfigurationStatusMessage(
                                    configurationFileCount,
                                    null,
                                    applyWasRequested: false)
                                    + " "
                                    + T("The artifact could not be selected as the desired version automatically. Choose it from the installation template page.");
                                return RedirectToPage("/Admin/ArtifactEdit", new { id = existingIdentity.ArtifactId });
                            }
                        }

                        StatusMessage = BuildExistingArtifactConfigurationStatusMessage(
                            configurationFileCount,
                            existingApplicationResult,
                            Input.UseArtifactImmediately);
                        return RedirectToPage("/Admin/ArtifactEdit", new { id = existingIdentity.ArtifactId });
                    }

                    ModelState.AddModelError(
                        string.Empty,
                        T("An artifact with the same app, package type, target, version, and content already exists. No upload is needed."));
                    return Page();
                }

                if (!Input.ReplaceExistingArtifact)
                {
                    ModelState.AddModelError(
                        nameof(Input.ReplaceExistingArtifact),
                        T("An artifact with the same app, package type, target, and version already exists, but the uploaded content is different. Confirm replacement to overwrite the existing artifact content."));
                    return Page();
                }

                relativePath = NormalizeRelativePath(existingIdentity.RelativePath) ?? relativePath;
                finalPath = ResolveUnderRoot(storeRoot, relativePath);
            }
            else
            {
                var duplicate = await _repo.FindArtifactBySha256Async(contentHash, ct);
                if (duplicate is not null)
                {
                    ModelState.AddModelError(
                        string.Empty,
                        string.Format(
                            T("An artifact with identical extracted content already exists: {0} {1} ({2})."),
                            duplicate.AppKey,
                            duplicate.Version,
                            duplicate.PackageType));
                    return Page();
                }
            }

            if (existingIdentity is null && (Directory.Exists(finalPath) || System.IO.File.Exists(finalPath)))
            {
                ModelState.AddModelError(
                    nameof(Input.RelativePath),
                    T("The target artifact path already exists. Choose a new immutable version path."));
                return Page();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (existingIdentity is not null && (Directory.Exists(finalPath) || System.IO.File.Exists(finalPath)))
            {
                MoveExistingArtifactToBackup(finalPath, backupPath);
                movedExistingToBackup = true;
            }

            MoveFileOrDirectory(package.ArtifactContentPath, finalPath);

            var artifactData = new ArtifactEditData
            {
                ArtifactId = existingIdentity?.ArtifactId ?? 0,
                AppId = Input.AppId,
                Version = version,
                PackageType = packageType,
                TargetName = targetName,
                RelativePath = relativePath,
                Sha256 = contentHash,
                IsEnabled = true
            };

            var artifactId = 0;
            try
            {
                artifactId = await _repo.SaveArtifactAsync(artifactData, ct);
            }
            catch
            {
                TryDelete(finalPath);
                if (movedExistingToBackup)
                {
                    RestoreExistingArtifactBackup(backupPath, finalPath);
                    movedExistingToBackup = false;
                }

                throw;
            }

            var packagedConfigurationFileCount = 0;
            if (package.ConfigurationFiles.Count > 0)
            {
                try
                {
                    packagedConfigurationFileCount = await _repo.ReplaceArtifactConfigurationFilesAsync(
                        artifactId,
                        package.ConfigurationFiles,
                        ct);
                }
                catch (SqlException)
                {
                    StatusMessage = T("Artifact uploaded and registered.")
                        + " "
                        + T("Configuration files from the artifact package could not be saved. Review the artifact edit page before deploying this version.");

                    return RedirectToPage("/Admin/ArtifactEdit", new { id = artifactId });
                }
            }

            if (movedExistingToBackup)
            {
                TryDelete(backupPath);
                movedExistingToBackup = false;
            }

            ArtifactConfigurationFileCopyResult? copyResult = null;
            if (Input.CopyConfigurationFilesFromPreviousVersion
                && existingIdentity is null
                && package.ConfigurationFiles.Count == 0)
            {
                try
                {
                    copyResult = await _repo.CopyConfigurationFilesFromLatestPreviousArtifactAsync(
                        artifactId,
                        artifactData.AppId,
                        artifactData.PackageType,
                        artifactData.TargetName,
                        ct);
                }
                catch (SqlException)
                {
                    StatusMessage = T("Artifact uploaded and registered.")
                        + " "
                        + T("Configuration files could not be copied automatically. Add them from the artifact edit page.");

                    return RedirectToPage("/Admin/ArtifactEdit", new { id = artifactId });
                }
            }

            ArtifactApplicationResult? applicationResult = null;
            if (Input.UseArtifactImmediately)
            {
                try
                {
                    applicationResult = await _repo.ApplyArtifactToMatchingApplicationsAsync(artifactId, ct);
                }
                catch (SqlException)
                {
                    StatusMessage = T("Artifact uploaded and registered.")
                        + " "
                        + T("The artifact could not be selected as the desired version automatically. Choose it from the installation template page.");

                    return RedirectToPage("/Admin/ArtifactEdit", new { id = artifactId });
                }
            }

            StatusMessage = BuildUploadStatusMessage(
                copyResult,
                Input.CopyConfigurationFilesFromPreviousVersion,
                packagedConfigurationFileCount,
                applicationResult,
                Input.UseArtifactImmediately,
                existingIdentity is not null);
            return RedirectToPage("/Admin/ArtifactEdit", new { id = artifactId });
        }
        catch (InvalidDataException ex)
        {
            ModelState.AddModelError(nameof(Input.ZipFile), T($"The uploaded zip could not be read: {ex.Message}"));
            return Page();
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(string.Empty, T($"The artifact metadata could not be saved: {ex.Message}"));
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }
        catch (IOException ex)
        {
            ModelState.AddModelError(string.Empty, T($"The artifact could not be stored: {ex.Message}"));
            return Page();
        }
        catch (UnauthorizedAccessException ex)
        {
            ModelState.AddModelError(string.Empty, T($"The artifact store could not be accessed: {ex.Message}"));
            return Page();
        }
        finally
        {
            TryDelete(tempZipPath);
            TryDelete(stagingPath);

            if (movedExistingToBackup)
            {
                RestoreExistingArtifactBackup(backupPath, finalPath);
            }
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        AppOptions = await _repo.GetArtifactAppOptionsAsync(ct);
    }

    private FilenameMetadata? ApplyFilenameMetadataIfUseful()
    {
        if (Input.ZipFile is null)
        {
            return null;
        }

        var metadata = TryParseFilenameMetadata(Input.ZipFile.FileName);
        if (metadata is null)
        {
            return null;
        }

        var app = AppOptions.FirstOrDefault(
            option => option.ModuleKey.Equals(metadata.ModuleKey, StringComparison.OrdinalIgnoreCase)
                && option.AppKey.Equals(metadata.AppKey, StringComparison.OrdinalIgnoreCase));

        if (Input.AppId <= 0 && app is not null)
        {
            Input.AppId = app.AppId;
        }

        Input.PackageType = FillBlank(Input.PackageType, metadata.PackageType);
        Input.TargetName = FillBlank(Input.TargetName, metadata.TargetName);
        Input.Version = FillBlank(Input.Version, metadata.Version);
        Input.RelativePath = FillBlank(
            Input.RelativePath,
            BuildDefaultRelativePath(metadata.TargetName, metadata.PackageType, metadata.Version));

        return metadata;
    }

    private void ValidateInput(FilenameMetadata? filenameMetadata)
    {
        if (Input.ZipFile is null || Input.ZipFile.Length == 0)
        {
            ModelState.AddModelError(nameof(Input.ZipFile), T("Select one artifact zip file."));
        }
        else if (!Input.ZipFile.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(Input.ZipFile), T("The uploaded artifact must be a .zip file."));
        }
        else if (Input.ZipFile.Length > GetMaxUploadBytes())
        {
            ModelState.AddModelError(
                nameof(Input.ZipFile),
                T($"The uploaded zip exceeds the configured limit of {GetMaxUploadBytes()} bytes."));
        }

        if (Input.AppId <= 0)
        {
            if (filenameMetadata is not null)
            {
                ModelState.AddModelError(
                    nameof(Input.AppId),
                    string.Format(
                        T("The filename references module '{0}' and app '{1}', but that enabled app definition was not found. Register the module/app first or select an existing app."),
                        filenameMetadata.ModuleKey,
                        filenameMetadata.AppKey));
            }
            else
            {
                ModelState.AddModelError(nameof(Input.AppId), T("Select an app."));
            }
        }

        if (string.IsNullOrWhiteSpace(Input.Version))
        {
            ModelState.AddModelError(nameof(Input.Version), T("Version is required."));
        }

        if (string.IsNullOrWhiteSpace(Input.PackageType))
        {
            ModelState.AddModelError(nameof(Input.PackageType), T("Package type is required."));
        }

        if (string.IsNullOrWhiteSpace(Input.TargetName))
        {
            ModelState.AddModelError(nameof(Input.TargetName), T("Target name is required."));
        }
    }

    private string? ResolveArtifactStoreRoot()
    {
        if (string.IsNullOrWhiteSpace(_uploadOptions.ArtifactStoreRoot))
        {
            ModelState.AddModelError(
                string.Empty,
                T("ArtifactUpload:ArtifactStoreRoot is not configured. Set it to the same root used by HostAgent:CentralArtifactRoot."));
            return null;
        }

        try
        {
            var root = Path.GetFullPath(_uploadOptions.ArtifactStoreRoot.Trim());
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ModelState.AddModelError(
                string.Empty,
                T($"ArtifactUpload:ArtifactStoreRoot could not be used: {ex.Message}"));
            return null;
        }
    }

    private async Task CopyUploadToTempZipAsync(IFormFile file, string tempZipPath, CancellationToken ct)
    {
        var maxBytes = GetMaxUploadBytes();
        var totalBytes = 0L;
        var buffer = new byte[HashBufferSize];

        await using var source = file.OpenReadStream();
        await using var target = new FileStream(tempZipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            totalBytes += read;
            if (totalBytes > maxBytes)
            {
                throw new InvalidOperationException($"The uploaded zip exceeds the configured limit of {maxBytes} bytes.");
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
                $"The artifact zip contains runtime configuration file '{normalizedEntryName}'. Upload this file from the artifact edit page instead so HostAgent can manage it outside the immutable artifact.");
        }
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

            var separator = new byte[] { 0 };
            sha.TransformBlock(separator, 0, separator.Length, null, 0);

            await using var stream = System.IO.File.OpenRead(file);
            var buffer = new byte[HashBufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
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

    private static FilenameMetadata? TryParseFilenameMetadata(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase) is false)
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
            // Service components often expose a friendly "-backend" target
            // while the artifact store keeps the package-kind folder as
            // "backend" for compatibility with existing installers.
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

    private static string FillBlank(string? current, string replacement)
        => string.IsNullOrWhiteSpace(current) ? replacement : current.Trim();

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string BuildUploadStatusMessage(
        ArtifactConfigurationFileCopyResult? copyResult,
        bool copyWasRequested,
        int packagedConfigurationFileCount,
        ArtifactApplicationResult? applicationResult,
        bool applyWasRequested,
        bool replacedExistingArtifact)
    {
        var message = replacedExistingArtifact
            ? T("Existing artifact content replaced and metadata updated.")
            : T("Artifact uploaded and registered.");

        if (replacedExistingArtifact)
        {
            if (packagedConfigurationFileCount > 0)
            {
                message += " " + string.Format(
                    T("Registered {0} configuration file(s) from the artifact package."),
                    packagedConfigurationFileCount);
            }
            else
            {
                message += " " + T("Existing artifact configuration file rows were preserved.");
            }
        }
        else if (packagedConfigurationFileCount > 0)
        {
            message += " " + string.Format(
                T("Registered {0} configuration file(s) from the artifact package."),
                packagedConfigurationFileCount);
        }
        else if (copyWasRequested)
        {
            if (copyResult is null || copyResult.CopiedCount == 0)
            {
                message += " " + T("No previous artifact configuration files were found to copy.");
            }
            else
            {
                message += " " + string.Format(
                    T("Copied {0} configuration file(s) from artifact version {1}."),
                    copyResult.CopiedCount,
                    copyResult.SourceVersion);
            }
        }

        if (applyWasRequested)
        {
            var updatedRows = applicationResult?.TotalRowsUpdated ?? 0;
            message += " " + string.Format(
                T("Selected this artifact as desired version for {0} matching app row(s)."),
                updatedRows);
        }

        return message;
    }

    private string BuildExistingArtifactConfigurationStatusMessage(
        int packagedConfigurationFileCount,
        ArtifactApplicationResult? applicationResult,
        bool applyWasRequested)
    {
        var message = T("The artifact content already existed, so the immutable payload was left unchanged.");
        message += " " + string.Format(
            T("Updated {0} configuration file row(s) from the artifact package."),
            packagedConfigurationFileCount);

        if (applyWasRequested)
        {
            var updatedRows = applicationResult?.TotalRowsUpdated ?? 0;
            message += " " + string.Format(
                T("Selected this artifact as desired version for {0} matching app row(s)."),
                updatedRows);
        }

        return message;
    }

    private static void MoveExistingArtifactToBackup(string finalPath, string backupPath)
    {
        TryDelete(backupPath);

        if (Directory.Exists(finalPath))
        {
            MoveFileOrDirectory(finalPath, backupPath);
        }
        else if (System.IO.File.Exists(finalPath))
        {
            MoveFileOrDirectory(finalPath, backupPath);
        }
    }

    private static void RestoreExistingArtifactBackup(string backupPath, string finalPath)
    {
        try
        {
            if (!Directory.Exists(backupPath) && !System.IO.File.Exists(backupPath))
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
            // The database row is only updated after the new folder has been
            // moved into place. Backup restoration is best effort if a later
            // filesystem failure leaves the original content in staging.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort restoration after a failed replacement.
        }
    }

    private long GetMaxUploadBytes()
        => _uploadOptions.MaxUploadBytes > 0
            ? _uploadOptions.MaxUploadBytes
            : ArtifactUploadOptions.DefaultMaxUploadBytes;

    private static void MoveFileOrDirectory(string source, string destination)
    {
        if (System.IO.File.Exists(source))
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
            System.IO.File.Move(source, destination);
            return;
        }

        System.IO.File.Copy(source, destination, overwrite: false);
        System.IO.File.Delete(source);
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
            System.IO.File.Copy(Path.Join(source, relativeFile), targetFile, overwrite: false);
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
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup after a failed upload.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup after a failed upload.
        }
    }

    private sealed record FilenameMetadata(
        string ModuleKey,
        string AppKey,
        string PackageType,
        string TargetName,
        string Version);

    public sealed class InputModel
    {
        [Required]
        [Display(Name = "Artifact zip")]
        public IFormFile? ZipFile { get; set; }

        [Required]
        [Display(Name = "App")]
        public int AppId { get; set; }

        [Required]
        [StringLength(50)]
        public string Version { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        [Display(Name = "Package type")]
        public string PackageType { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        [Display(Name = "Target name")]
        public string? TargetName { get; set; }

        [Required]
        [StringLength(400)]
        [Display(Name = "Relative path")]
        public string? RelativePath { get; set; }

        [Display(Name = "Copy configuration files from previous version")]
        public bool CopyConfigurationFilesFromPreviousVersion { get; set; }

        [Display(Name = "Use this version immediately")]
        public bool UseArtifactImmediately { get; set; } = true;

        [Display(Name = "Replace existing artifact with same identity")]
        public bool ReplaceExistingArtifact { get; set; }
    }
}
