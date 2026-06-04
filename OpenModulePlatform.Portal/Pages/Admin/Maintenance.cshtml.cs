// File: OpenModulePlatform.Portal/Pages/Admin/Maintenance.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class MaintenanceModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;
    private readonly ArtifactUploadOptions _artifactOptions;

    public MaintenanceModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo,
        IOptions<ArtifactUploadOptions> artifactOptions)
        : base(options, rbac)
    {
        _repo = repo;
        _artifactOptions = artifactOptions.Value;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ArtifactRetentionPreview Preview { get; private set; } = new()
    {
        MaxVersionsToKeep = 5
    };

    public ArtifactRetentionCleanupResult? CleanupResult { get; private set; }

    public IReadOnlyList<HostAgentJobRow> RecentHostAgentJobs { get; private set; } = [];

    public string? ArtifactStoreRoot { get; private set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Maintenance");
        await LoadPageDataAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPreview(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Maintenance");
        await LoadPageDataAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCleanupArtifacts(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Maintenance");
        Preview = await LoadPreviewCoreAsync(ct);

        if (!ModelState.IsValid)
        {
            await LoadRecentHostAgentJobsAsync(ct);
            return Page();
        }

        if (Preview.DeletableCandidateCount == 0)
        {
            ModelState.AddModelError(string.Empty, T("There are no unreferenced old artifact versions to delete."));
            await LoadRecentHostAgentJobsAsync(ct);
            return Page();
        }

        var root = ResolveArtifactStoreRoot();
        if (Preview.Candidates.Any(static row => !row.IsProtected && !string.IsNullOrWhiteSpace(row.RelativePath))
            && root is null)
        {
            ModelState.AddModelError(
                string.Empty,
                T("ArtifactUpload:ArtifactStoreRoot must point to an existing artifact store before artifact payloads can be cleaned up."));
            await LoadRecentHostAgentJobsAsync(ct);
            return Page();
        }

        var deletion = await _repo.DeleteOldArtifactVersionsAsync(
            Input.MaxVersionsToKeep,
            User.Identity?.Name,
            ct);
        var deleted = deletion.DeletedArtifacts;
        var payloadSummary = root is null
            ? new PayloadCleanupSummary()
            : DeletePayloads(root, deleted);

        CleanupResult = new ArtifactRetentionCleanupResult
        {
            DeletedArtifactCount = deleted.Count,
            RemovedPayloadCount = payloadSummary.RemovedCount,
            MissingPayloadCount = payloadSummary.MissingCount,
            HostCacheEntryCount = deletion.HostCacheEntryCount,
            CreatedHostAgentJobCount = deletion.CreatedHostAgentJobCount,
            PayloadErrors = payloadSummary.Errors,
            DeletedArtifacts = deleted
        };

        Preview = await LoadPreviewCoreAsync(ct);
        await LoadRecentHostAgentJobsAsync(ct);
        return Page();
    }

    private async Task LoadPageDataAsync(CancellationToken ct)
    {
        Preview = await LoadPreviewCoreAsync(ct);
        await LoadRecentHostAgentJobsAsync(ct);
    }

    private async Task<ArtifactRetentionPreview> LoadPreviewCoreAsync(CancellationToken ct)
    {
        ArtifactStoreRoot = string.IsNullOrWhiteSpace(_artifactOptions.ArtifactStoreRoot)
            ? null
            : _artifactOptions.ArtifactStoreRoot.Trim();

        if (!ModelState.IsValid)
        {
            return new ArtifactRetentionPreview
            {
                MaxVersionsToKeep = Math.Max(1, Input.MaxVersionsToKeep)
            };
        }

        return await _repo.GetArtifactRetentionPreviewAsync(Input.MaxVersionsToKeep, ct);
    }

    private async Task LoadRecentHostAgentJobsAsync(CancellationToken ct)
    {
        RecentHostAgentJobs = await _repo.GetRecentHostAgentJobsAsync(25, ct);
    }

    public static string FormatHostAgentJobStatus(byte status)
        => status switch
        {
            0 => "Pending",
            1 => "Running",
            2 => "Succeeded",
            3 => "Failed",
            4 => "Warning",
            5 => "Cancelled",
            _ => "Unknown"
        };

    private string? ResolveArtifactStoreRoot()
    {
        if (string.IsNullOrWhiteSpace(_artifactOptions.ArtifactStoreRoot))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(_artifactOptions.ArtifactStoreRoot.Trim());
            if (!Directory.Exists(root))
            {
                return null;
            }

            return root;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static PayloadCleanupSummary DeletePayloads(
        string artifactStoreRoot,
        IReadOnlyList<ArtifactRetentionCandidateRow> deletedArtifacts)
    {
        var summary = new PayloadCleanupSummary();
        var seenPaths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var artifact in deletedArtifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.RelativePath))
            {
                continue;
            }

            if (!TryResolveArtifactPayloadPath(artifactStoreRoot, artifact.RelativePath, out var payloadPath, out var error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    summary.Errors.Add(error);
                }

                continue;
            }

            if (!seenPaths.Add(payloadPath))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(payloadPath))
                {
                    Directory.Delete(payloadPath, recursive: true);
                    summary.RemovedCount++;
                }
                else if (System.IO.File.Exists(payloadPath))
                {
                    System.IO.File.Delete(payloadPath);
                    summary.RemovedCount++;
                }
                else
                {
                    summary.MissingCount++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                summary.Errors.Add($"{artifact.RelativePath}: {ex.Message}");
            }
        }

        return summary;
    }

    private static bool TryResolveArtifactPayloadPath(
        string artifactStoreRoot,
        string relativePath,
        out string payloadPath,
        out string? error)
    {
        payloadPath = string.Empty;
        error = null;

        var normalizedRelativePath = relativePath.Trim().Replace('\\', '/').Trim('/');
        if (normalizedRelativePath.Length == 0
            || normalizedRelativePath.Contains(':', StringComparison.Ordinal)
            || normalizedRelativePath.IndexOf('\0') >= 0)
        {
            error = $"{relativePath}: invalid artifact relative path.";
            return false;
        }

        var segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (segments.Length == 0
            || segments.Any(static segment => segment is "." or "..")
            || segments.Any(segment => segment.IndexOfAny(invalidFileNameChars) >= 0))
        {
            error = $"{relativePath}: invalid artifact relative path.";
            return false;
        }

        var fullRoot = Path.GetFullPath(artifactStoreRoot);
        var fullPath = Path.GetFullPath(Path.Join(fullRoot, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, comparison))
        {
            error = $"{relativePath}: artifact path escapes the configured artifact store root.";
            return false;
        }

        payloadPath = fullPath;
        return true;
    }

    private sealed class PayloadCleanupSummary
    {
        public int RemovedCount { get; set; }

        public int MissingCount { get; set; }

        public List<string> Errors { get; } = [];
    }

    public sealed class InputModel
    {
        [Range(1, 100)]
        [Display(Name = "Versions to keep")]
        public int MaxVersionsToKeep { get; set; } = 5;
    }
}
