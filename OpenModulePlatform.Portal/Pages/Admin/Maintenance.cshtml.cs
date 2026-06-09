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

    [BindProperty]
    public List<long> SelectedMaintenanceFindingIds { get; set; } = [];

    public ArtifactRetentionPreview Preview { get; private set; } = new()
    {
        MaxVersionsToKeep = 5
    };

    public ArtifactRetentionCleanupResult? CleanupResult { get; private set; }

    public MaintenanceScanQueueResult? MaintenanceScanResult { get; private set; }

    public MaintenanceCleanupQueueResult? MaintenanceCleanupResult { get; private set; }

    public int IgnoredMaintenanceFindingCount { get; private set; }

    public IReadOnlyList<MaintenanceFindingRow> MaintenanceFindings { get; private set; } = [];

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
            await LoadOperationalListsAsync(ct);
            return Page();
        }

        if (Preview.DeletableCandidateCount == 0)
        {
            ModelState.AddModelError(string.Empty, T("There are no unreferenced old artifact versions to delete."));
            await LoadOperationalListsAsync(ct);
            return Page();
        }

        var jobId = await _repo.QueueArtifactRetentionCleanupAsync(
            Input.MaxVersionsToKeep,
            User.Identity?.Name,
            ct);

        CleanupResult = new ArtifactRetentionCleanupResult
        {
            QueuedHostAgentJobId = jobId,
            MaxVersionsToKeep = Input.MaxVersionsToKeep,
            CandidateCount = Preview.CandidateCount,
            DeletableCandidateCount = Preview.DeletableCandidateCount
        };

        Preview = await LoadPreviewCoreAsync(ct);
        await LoadOperationalListsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostQueueMaintenanceScan(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Maintenance");
        MaintenanceScanResult = await _repo.QueueMaintenanceScanAsync(User.Identity?.Name, ct);
        await LoadPageDataAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCleanupMaintenanceFindings(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Maintenance");
        if (SelectedMaintenanceFindingIds.Count == 0)
        {
            ModelState.AddModelError(string.Empty, T("Select at least one maintenance finding first."));
            await LoadPageDataAsync(ct);
            return Page();
        }

        MaintenanceCleanupResult = await _repo.QueueMaintenanceCleanupAsync(
            SelectedMaintenanceFindingIds,
            User.Identity?.Name,
            ct);

        await LoadPageDataAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostIgnoreMaintenanceFindings(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Maintenance");
        if (SelectedMaintenanceFindingIds.Count == 0)
        {
            ModelState.AddModelError(string.Empty, T("Select at least one maintenance finding first."));
            await LoadPageDataAsync(ct);
            return Page();
        }

        IgnoredMaintenanceFindingCount = await _repo.IgnoreMaintenanceFindingsAsync(
            SelectedMaintenanceFindingIds,
            User.Identity?.Name,
            ct);

        await LoadPageDataAsync(ct);
        return Page();
    }

    private async Task LoadPageDataAsync(CancellationToken ct)
    {
        Preview = await LoadPreviewCoreAsync(ct);
        await LoadOperationalListsAsync(ct);
    }

    private async Task LoadOperationalListsAsync(CancellationToken ct)
    {
        MaintenanceFindings = await _repo.GetMaintenanceFindingsAsync(200, ct);
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

    public static string FormatMaintenanceFindingStatus(byte status)
        => status switch
        {
            MaintenanceFindingStatuses.Open => "Open",
            MaintenanceFindingStatuses.Ignored => "Ignored",
            MaintenanceFindingStatuses.CleanupQueued => "Cleanup queued",
            MaintenanceFindingStatuses.Cleaned => "Cleaned",
            MaintenanceFindingStatuses.Failed => "Failed",
            MaintenanceFindingStatuses.Skipped => "Skipped",
            _ => "Unknown"
        };

    public sealed class InputModel
    {
        [Range(1, 100)]
        [Display(Name = "Versions to keep")]
        public int MaxVersionsToKeep { get; set; } = 5;
    }
}
