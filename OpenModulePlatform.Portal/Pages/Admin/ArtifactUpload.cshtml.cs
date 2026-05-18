// File: OpenModulePlatform.Portal/Pages/Admin/ArtifactUpload.cshtml.cs
using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
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

        ApplyFilenameMetadataIfUseful();
        ValidateInput();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var storeRoot = ResolveArtifactStoreRoot();
        if (storeRoot is null)
        {
            return Page();
        }

        var relativePath = NormalizeRelativePath(Input.RelativePath);
        if (relativePath is null)
        {
            ModelState.AddModelError(
                nameof(Input.RelativePath),
                T("Relative path must stay inside the artifact store and cannot contain rooted paths or parent directory segments."));
            return Page();
        }

        var tempRoot = Path.Combine(storeRoot, ".portal-upload-staging");
        Directory.CreateDirectory(tempRoot);

        var tempZipPath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.zip");
        var stagingPath = Path.Combine(tempRoot, $"artifact-{Guid.NewGuid():N}");
        var finalPath = ResolveUnderRoot(storeRoot, relativePath);
        var movedToFinal = false;

        try
        {
            await CopyUploadToTempZipAsync(Input.ZipFile!, tempZipPath, ct);
            ExtractValidatedZip(tempZipPath, stagingPath);

            var contentHash = await ComputeDirectorySha256Async(stagingPath, ct);
            var duplicate = await _repo.FindArtifactBySha256Async(contentHash, ct);
            if (duplicate is not null)
            {
                ModelState.AddModelError(
                    string.Empty,
                    T($"An artifact with identical extracted content already exists: {duplicate.AppKey} {duplicate.Version} ({duplicate.PackageType})."));
                return Page();
            }

            if (Directory.Exists(finalPath) || System.IO.File.Exists(finalPath))
            {
                ModelState.AddModelError(
                    nameof(Input.RelativePath),
                    T("The target artifact path already exists. Choose a new immutable version path."));
                return Page();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            Directory.Move(stagingPath, finalPath);
            movedToFinal = true;

            var artifactData = new ArtifactEditData
            {
                AppId = Input.AppId,
                Version = Input.Version.Trim(),
                PackageType = Input.PackageType.Trim(),
                TargetName = Clean(Input.TargetName),
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
                throw;
            }

            ArtifactConfigurationFileCopyResult? copyResult = null;
            if (Input.CopyConfigurationFilesFromPreviousVersion)
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

            StatusMessage = BuildUploadStatusMessage(copyResult, Input.CopyConfigurationFilesFromPreviousVersion);
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
            if (!movedToFinal)
            {
                TryDelete(stagingPath);
            }
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        AppOptions = await _repo.GetArtifactAppOptionsAsync(ct);
    }

    private void ApplyFilenameMetadataIfUseful()
    {
        if (Input.ZipFile is null)
        {
            return;
        }

        var metadata = TryParseFilenameMetadata(Input.ZipFile.FileName);
        if (metadata is null)
        {
            return;
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
    }

    private void ValidateInput()
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
            ModelState.AddModelError(nameof(Input.AppId), T("Select an app."));
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

        if (string.IsNullOrWhiteSpace(Input.RelativePath))
        {
            ModelState.AddModelError(nameof(Input.RelativePath), T("Relative path is required."));
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

    private static void ExtractValidatedZip(string zipPath, string stagingPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var fileCount = 0;

        Directory.CreateDirectory(stagingPath);

        foreach (var entry in archive.Entries)
        {
            var entryName = NormalizeZipEntryName(entry.FullName);
            var entryPath = ResolveZipEntryPath(stagingPath, entryName);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(entryPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            entry.ExtractToFile(entryPath, overwrite: false);
            fileCount++;
        }

        if (fileCount == 0)
        {
            throw new InvalidOperationException("The uploaded zip must contain at least one file.");
        }
    }

    private static string NormalizeZipEntryName(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').Trim();
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The uploaded zip contains an invalid entry path.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            throw new InvalidOperationException("The uploaded zip contains a path that escapes the artifact root.");
        }

        if (normalized.Contains(':', StringComparison.Ordinal) || normalized.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("The uploaded zip contains a rooted or invalid entry path.");
        }

        return string.Join('/', segments);
    }

    private static string ResolveZipEntryPath(string rootPath, string relativeEntryPath)
    {
        var localRelativePath = relativeEntryPath.Replace('/', Path.DirectorySeparatorChar);
        return ResolveUnderRoot(rootPath, localRelativePath);
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

    private static string GetPackagePathSegment(string packageType)
        => packageType.Trim().ToLowerInvariant() switch
        {
            "web-app" => "web",
            "service-app" => "service",
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
        bool copyWasRequested)
    {
        var message = T("Artifact uploaded and registered.");
        if (!copyWasRequested)
        {
            return message;
        }

        if (copyResult is null || copyResult.CopiedCount == 0)
        {
            return message + " " + T("No previous artifact configuration files were found to copy.");
        }

        return message + " " + string.Format(
            T("Copied {0} configuration file(s) from artifact version {1}."),
            copyResult.CopiedCount,
            copyResult.SourceVersion);
    }

    private long GetMaxUploadBytes()
        => _uploadOptions.MaxUploadBytes > 0
            ? _uploadOptions.MaxUploadBytes
            : ArtifactUploadOptions.DefaultMaxUploadBytes;

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
    }
}
