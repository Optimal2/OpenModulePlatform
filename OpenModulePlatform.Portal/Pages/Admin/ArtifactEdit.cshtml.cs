// File: OpenModulePlatform.Portal/Pages/Admin/ArtifactEdit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Edits artifact metadata for an app definition.
/// The page keeps package identity explicit so later deployment automation can reason about artifacts reliably.
/// </summary>
public sealed class ArtifactEditModel : OmpPortalPageModel
{
    private static readonly Regex ShaPattern = new(
        "^[A-Fa-f0-9]{64}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;
    private readonly ArtifactUploadOptions _uploadOptions;

    public ArtifactEditModel(
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

    public IReadOnlyList<OptionItem> AppOptions { get; private set; } = [];

    public IReadOnlyList<ArtifactConfigurationFileRow> ConfigurationFiles { get; private set; } = [];

    public IReadOnlyList<OptionItem> PackageTypeOptions => ArtifactPackageTypes.CreateOptions(T);

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.ArtifactId == 0;

    public async Task<IActionResult> OnGet(int? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles(id.HasValue ? "Edit artifact" : "Create artifact");

        if (!id.HasValue)
        {
            Input.PackageType = "web-app";
            Input.IsEnabled = true;
            return Page();
        }

        var row = await _repo.GetArtifactAsync(id.Value, ct);
        if (row is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            ArtifactId = row.ArtifactId,
            AppId = row.AppId,
            Version = row.Version,
            PackageType = row.PackageType,
            TargetName = row.TargetName,
            RelativePath = row.RelativePath,
            Sha256 = row.Sha256,
            IsEnabled = row.IsEnabled
        };
        ConfigurationFiles = await _repo.GetArtifactConfigurationFilesAsync(row.ArtifactId, ct);

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
        SetTitles(IsCreate ? "Create artifact" : "Edit artifact");

        ValidateInput();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveArtifactAsync(
                new ArtifactEditData
                {
                    ArtifactId = Input.ArtifactId,
                    AppId = Input.AppId,
                    Version = Input.Version.Trim(),
                    PackageType = Input.PackageType,
                    TargetName = Clean(Input.TargetName),
                    RelativePath = Clean(Input.RelativePath),
                    Sha256 = Clean(Input.Sha256),
                    IsEnabled = Input.IsEnabled
                },
                ct);

            StatusMessage = IsCreate ? T("Artifact created.") : T("Artifact updated.");
            return RedirectToPage("/Admin/ArtifactEdit", new { id });
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The artifact could not be saved.")));

            return Page();
        }
    }

    public async Task<IActionResult> OnPostDelete(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var artifactId = Input.ArtifactId;
        ModelState.Clear();

        try
        {
            await _repo.DeleteArtifactAsync(artifactId, ct);
            StatusMessage = T("Artifact deleted.");
            return RedirectToPage("/Admin/Artifacts");
        }
        catch (SqlException ex)
        {
            await LoadArtifactForEditAsync(artifactId, ct);
            SetTitles("Edit artifact");
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The artifact could not be deleted.")));

            return Page();
        }
    }

    public async Task<IActionResult> OnGetDownloadPackage(int id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var row = await _repo.GetArtifactAsync(id, ct);
        if (row is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(row.ModuleKey)
            || string.IsNullOrWhiteSpace(row.AppKey)
            || string.IsNullOrWhiteSpace(row.TargetName)
            || string.IsNullOrWhiteSpace(row.RelativePath))
        {
            return BadRequest(T("Artifact metadata is incomplete and cannot be exported as a standard package."));
        }

        var storeRoot = ResolveArtifactStoreRoot();
        if (storeRoot is null)
        {
            return BadRequest(T("ArtifactUpload:ArtifactStoreRoot is not configured. Set it to the same root used by HostAgent:CentralArtifactRoot."));
        }

        var relativePath = NormalizeRelativePath(row.RelativePath);
        if (relativePath is null)
        {
            return BadRequest(T("Artifact relative path is invalid."));
        }

        var payloadPath = ResolveUnderRoot(storeRoot, relativePath);
        if (!Directory.Exists(payloadPath))
        {
            return NotFound(T("Artifact payload folder was not found in the artifact store."));
        }

        var configurationFiles = await _repo.GetArtifactConfigurationFileContentsAsync(row.ArtifactId, ct);
        var packageFileName = CreateArtifactPackageFileName(
            row.ModuleKey,
            row.AppKey,
            row.PackageType,
            row.TargetName,
            row.Version);
        var tempPackagePath = Path.Join(
            Path.GetTempPath(),
            "OpenModulePlatform",
            "ArtifactPackageExports",
            $"{Guid.NewGuid():N}.zip");

        var packageConfigurationFiles = configurationFiles
            .Select(file => new ArtifactPackageConfigurationFile(file.RelativePath, file.FileContent))
            .ToArray();

        new ArtifactPackageWriter().CreateFromPayloadDirectory(
            payloadPath,
            tempPackagePath,
            packageConfigurationFiles);

        Response.OnCompleted(static state =>
        {
            TryDeleteTemporaryFile((string)state);
            return Task.CompletedTask;
        }, tempPackagePath);

        return PhysicalFile(tempPackagePath, "application/zip", packageFileName);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        AppOptions = await _repo.GetAppOptionsAsync(ct);
        if (Input.ArtifactId > 0)
        {
            ConfigurationFiles = await _repo.GetArtifactConfigurationFilesAsync(Input.ArtifactId, ct);
        }
    }

    private async Task LoadArtifactForEditAsync(int artifactId, CancellationToken ct)
    {
        await LoadAsync(ct);

        var row = await _repo.GetArtifactAsync(artifactId, ct);
        if (row is null)
        {
            Input.ArtifactId = artifactId;
            return;
        }

        Input = new InputModel
        {
            ArtifactId = row.ArtifactId,
            AppId = row.AppId,
            Version = row.Version,
            PackageType = row.PackageType,
            TargetName = row.TargetName,
            RelativePath = row.RelativePath,
            Sha256 = row.Sha256,
            IsEnabled = row.IsEnabled
        };
        ConfigurationFiles = await _repo.GetArtifactConfigurationFilesAsync(row.ArtifactId, ct);
    }

    private void ValidateInput()
    {
        if (Input.AppId <= 0)
        {
            ModelState.AddModelError(nameof(Input.AppId), T("Select an app."));
        }

        if (string.IsNullOrWhiteSpace(Input.Version))
        {
            ModelState.AddModelError(nameof(Input.Version), T("Version is required."));
        }

        if (!string.IsNullOrWhiteSpace(Input.Sha256) && !ShaPattern.IsMatch(Input.Sha256))
        {
            ModelState.AddModelError(
                nameof(Input.Sha256), T("SHA-256 must be 64 hexadecimal characters."));
        }
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string? ResolveArtifactStoreRoot()
    {
        if (string.IsNullOrWhiteSpace(_uploadOptions.ArtifactStoreRoot))
        {
            return null;
        }

        return Path.GetFullPath(_uploadOptions.ArtifactStoreRoot.Trim());
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
            throw new InvalidOperationException("The artifact path escapes the configured artifact store root.");
        }

        return fullPath;
    }

    private static string CreateArtifactPackageFileName(
        string moduleKey,
        string appKey,
        string packageType,
        string targetName,
        string version)
        => $"{moduleKey}__{appKey}__{packageType}__{targetName}__{version}.zip";

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for a temporary export file; a later temp cleanup can remove it if the file is still locked.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for a temporary export file; failing the completed response would not help the operator.
        }
    }

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number == 547
            ? "This artifact is still referenced by desired app, worker, HostAgent, or host artifact requirement rows. Update those references first, or disable the artifact instead of deleting it."
            : fallback;

    public sealed class InputModel
    {
        public int ArtifactId { get; set; }

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

        [StringLength(100)]
        [Display(Name = "Target name")]
        public string? TargetName { get; set; }

        [StringLength(400)]
        [Display(Name = "Relative path")]
        public string? RelativePath { get; set; }

        [StringLength(128)]
        [Display(Name = "SHA-256")]
        public string? Sha256 { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;
    }
}
