// File: OpenModulePlatform.Portal/Pages/Admin/Maintenance.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class MaintenanceModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;

    public MaintenanceModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ArtifactRetentionPreview Preview { get; private set; } = new()
    {
        MaxVersionsToKeep = 5
    };

    public ArtifactRetentionCleanupResult? CleanupResult { get; private set; }

    public IReadOnlyList<HostAgentJobRow> RecentHostAgentJobs { get; private set; } = [];

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

        var deletion = await _repo.DeleteOldArtifactVersionsAsync(
            Input.MaxVersionsToKeep,
            User.Identity?.Name,
            ct);
        var deleted = deletion.DeletedArtifacts;

        CleanupResult = new ArtifactRetentionCleanupResult
        {
            DeletedArtifactCount = deleted.Count,
            ArtifactStoreEntryCount = deletion.ArtifactStoreEntryCount,
            HostCacheEntryCount = deletion.HostCacheEntryCount,
            CreatedHostAgentJobCount = deletion.CreatedHostAgentJobCount,
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

    public sealed class InputModel
    {
        [Range(1, 100)]
        [Display(Name = "Versions to keep")]
        public int MaxVersionsToKeep { get; set; } = 5;
    }
}
