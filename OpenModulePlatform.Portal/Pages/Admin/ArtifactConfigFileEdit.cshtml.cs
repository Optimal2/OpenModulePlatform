// File: OpenModulePlatform.Portal/Pages/Admin/ArtifactConfigFileEdit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Edits artifact-owned configuration files that HostAgent materializes after deployment.
/// </summary>
public sealed class ArtifactConfigFileEditModel : OmpPortalPageModel
{
    private const long MaxConfigurationFileBytes = 1024 * 1024;

    private readonly OmpAdminRepository _repo;

    public ArtifactConfigFileEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    [Display(Name = "Configuration file")]
    public IFormFile? UploadedFile { get; set; }

    public ArtifactEditData? Artifact { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.ArtifactConfigurationFileId == 0;

    public async Task<IActionResult> OnGet(int? id, int? artifactId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles(id.HasValue ? "Edit artifact configuration file" : "Create artifact configuration file");

        if (!id.HasValue)
        {
            if (!artifactId.HasValue)
            {
                return BadRequest();
            }

            var artifact = await _repo.GetArtifactAsync(artifactId.Value, ct);
            if (artifact is null)
            {
                return NotFound();
            }

            Artifact = artifact;
            Input.ArtifactId = artifact.ArtifactId;
            Input.IsEnabled = true;
            return Page();
        }

        var row = await _repo.GetArtifactConfigurationFileAsync(id.Value, ct);
        if (row is null)
        {
            return NotFound();
        }

        Artifact = await _repo.GetArtifactAsync(row.ArtifactId, ct);
        if (Artifact is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            ArtifactConfigurationFileId = row.ArtifactConfigurationFileId,
            ArtifactId = row.ArtifactId,
            RelativePath = row.RelativePath,
            FileContent = row.FileContent,
            IsEnabled = row.IsEnabled
        };

        return Page();
    }

    public async Task<IActionResult> OnPostMetadata(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles(IsCreate ? "Create artifact configuration file" : "Edit artifact configuration file");
        Artifact = await _repo.GetArtifactAsync(Input.ArtifactId, ct);
        if (Artifact is null)
        {
            return NotFound();
        }

        ValidateInput();
        if (!ModelState.IsValid)
        {
            Input.FileContent = await GetExistingFileContentOrEmptyAsync(Input.ArtifactConfigurationFileId, ct);
            return Page();
        }

        try
        {
            var id = await _repo.SaveArtifactConfigurationFileAsync(
                new ArtifactConfigurationFileEditData
                {
                    ArtifactConfigurationFileId = Input.ArtifactConfigurationFileId,
                    ArtifactId = Input.ArtifactId,
                    RelativePath = NormalizeRelativePath(Input.RelativePath),
                    FileContent = await GetExistingFileContentOrEmptyAsync(Input.ArtifactConfigurationFileId, ct),
                    IsEnabled = Input.IsEnabled
                },
                ct);

            StatusMessage = IsCreate
                ? T("Artifact configuration file created.")
                : T("Artifact configuration file updated.");

            return RedirectToPage("/Admin/ArtifactConfigFileEdit", new { id });
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The artifact configuration file could not be saved.")));

            return Page();
        }
    }

    public async Task<IActionResult> OnPostUploadContent(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit artifact configuration file");
        var row = await LoadExistingRowForContentUpdateAsync(ct);
        if (row is null)
        {
            return Page();
        }

        if (UploadedFile is null)
        {
            ModelState.AddModelError(nameof(UploadedFile), T("Select a file to upload."));
            return Page();
        }

        if (UploadedFile.Length > MaxConfigurationFileBytes)
        {
            ModelState.AddModelError(
                nameof(UploadedFile),
                T($"The uploaded configuration file is too large. The maximum size is {MaxConfigurationFileBytes} bytes."));

            return Page();
        }

        try
        {
            row.FileContent = await ReadUploadedTextFileAsync(UploadedFile, ct);
            await _repo.SaveArtifactConfigurationFileAsync(row, ct);
            StatusMessage = T("Artifact configuration file content updated.");
            return RedirectToPage("/Admin/ArtifactConfigFileEdit", new { id = row.ArtifactConfigurationFileId });
        }
        catch (DecoderFallbackException)
        {
            ModelState.AddModelError(nameof(UploadedFile), T("The uploaded configuration file must be valid UTF-8 text."));
            return Page();
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The artifact configuration file could not be saved.")));

            return Page();
        }
    }

    public async Task<IActionResult> OnPostContent(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit artifact configuration file");
        var row = await LoadExistingRowForContentUpdateAsync(ct);
        if (row is null)
        {
            return Page();
        }

        try
        {
            row.FileContent = Input.FileContent ?? string.Empty;
            await _repo.SaveArtifactConfigurationFileAsync(row, ct);
            StatusMessage = T("Artifact configuration file content updated.");
            return RedirectToPage("/Admin/ArtifactConfigFileEdit", new { id = row.ArtifactConfigurationFileId });
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The artifact configuration file could not be saved.")));

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

        try
        {
            await _repo.DeleteArtifactConfigurationFileAsync(Input.ArtifactConfigurationFileId, ct);
            StatusMessage = T("Artifact configuration file deleted.");
            return RedirectToPage("/Admin/ArtifactEdit", new { id = artifactId });
        }
        catch (SqlException ex)
        {
            Artifact = await _repo.GetArtifactAsync(Input.ArtifactId, ct);
            SetTitles("Edit artifact configuration file");
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The artifact configuration file could not be deleted.")));

            return Page();
        }
    }

    private void ValidateInput()
    {
        if (Input.ArtifactId <= 0)
        {
            ModelState.AddModelError(nameof(Input.ArtifactId), T("Artifact is required."));
        }

        if (string.IsNullOrWhiteSpace(Input.RelativePath))
        {
            ModelState.AddModelError(nameof(Input.RelativePath), T("Relative path is required."));
        }
        else if (!IsSafeRelativePath(Input.RelativePath))
        {
            ModelState.AddModelError(
                nameof(Input.RelativePath),
                T("Relative path must stay below the deployed app root and must not contain rooted paths or parent directory segments."));
        }
    }

    private async Task<string> GetExistingFileContentOrEmptyAsync(int artifactConfigurationFileId, CancellationToken ct)
    {
        if (artifactConfigurationFileId <= 0)
        {
            return string.Empty;
        }

        var row = await _repo.GetArtifactConfigurationFileAsync(artifactConfigurationFileId, ct);
        return row?.FileContent ?? string.Empty;
    }

    private async Task<ArtifactConfigurationFileEditData?> LoadExistingRowForContentUpdateAsync(CancellationToken ct)
    {
        if (Input.ArtifactConfigurationFileId <= 0)
        {
            return NotFoundRow();
        }

        var row = await _repo.GetArtifactConfigurationFileAsync(Input.ArtifactConfigurationFileId, ct);
        if (row is null)
        {
            return NotFoundRow();
        }

        Artifact = await _repo.GetArtifactAsync(row.ArtifactId, ct);
        if (Artifact is null)
        {
            return NotFoundRow();
        }

        Input = new InputModel
        {
            ArtifactConfigurationFileId = row.ArtifactConfigurationFileId,
            ArtifactId = row.ArtifactId,
            RelativePath = row.RelativePath,
            FileContent = row.FileContent,
            IsEnabled = row.IsEnabled
        };

        return row;

        ArtifactConfigurationFileEditData? NotFoundRow()
        {
            ModelState.AddModelError(string.Empty, T("Artifact configuration file was not found."));
            return null;
        }
    }

    private static async Task<string> ReadUploadedTextFileAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);

        return await reader.ReadToEndAsync(ct);
    }

    private static string NormalizeRelativePath(string relativePath)
        => relativePath.Trim().Replace('\\', '/').Trim('/');

    private static bool IsSafeRelativePath(string value)
    {
        var normalized = NormalizeRelativePath(value);
        if (normalized.Length == 0
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.IndexOf('\0') >= 0
            || Path.IsPathRooted(normalized))
        {
            return false;
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0
            && segments.All(segment => segment is not "." and not "..")
            && segments.All(segment => segment.IndexOfAny(invalidFileNameChars) < 0);
    }

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "This artifact already has a configuration file with the same relative path.",
            547 => "Delete or update dependent rows first.",
            _ => fallback
        };

    public sealed class InputModel
    {
        public int ArtifactConfigurationFileId { get; set; }

        public int ArtifactId { get; set; }

        [StringLength(400)]
        [Display(Name = "Relative path")]
        public string RelativePath { get; set; } = string.Empty;

        [Display(Name = "File content")]
        public string? FileContent { get; set; } = string.Empty;

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;
    }
}
