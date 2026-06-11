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
    private const int DefaultMaintenanceFindingLimit = 200;
    private const int DefaultRecentHostAgentJobLimit = 25;

    private readonly OmpAdminRepository _repo;

    public MaintenanceModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty(SupportsGet = true)]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public List<long> SelectedMaintenanceFindingIds { get; set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

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

    public async Task<IActionResult> OnGet(int? maxVersionsToKeep, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (maxVersionsToKeep.HasValue)
        {
            Input.MaxVersionsToKeep = maxVersionsToKeep.Value;
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
        Preview = await LoadPreviewCoreAsync(ct);
        await LoadOperationalListsAsync(ct);
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

        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("Queued HostAgent artifact retention cleanup job {0}. The HostAgent will recompute the candidates and perform both database and file cleanup. Current preview shows {1} deletable old versions out of {2} candidates with a keep limit of {3}."),
            jobId,
            Preview.DeletableCandidateCount,
            Preview.CandidateCount,
            Input.MaxVersionsToKeep);

        return RedirectToMaintenancePage();
    }

    public async Task<IActionResult> OnPostQueueMaintenanceScan(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        MaintenanceScanResult = await _repo.QueueMaintenanceScanAsync(User.Identity?.Name, ct);
        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("Queued {0} maintenance scan job(s): one global scan and {1} host scan job(s). Findings appear on this page as HostAgents report back."),
            MaintenanceScanResult.TotalJobCount,
            MaintenanceScanResult.HostJobCount);
        return RedirectToMaintenancePage();
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

        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("Queued cleanup for {0} of {1} selected maintenance finding(s) across {2} HostAgent job(s)."),
            MaintenanceCleanupResult.QueuedFindingCount,
            MaintenanceCleanupResult.SelectedFindingCount,
            MaintenanceCleanupResult.QueuedJobCount);
        return RedirectToMaintenancePage();
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

        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("Ignored {0} maintenance finding(s). They are hidden until a future scan reports them again."),
            IgnoredMaintenanceFindingCount);
        return RedirectToMaintenancePage();
    }

    private IActionResult RedirectToMaintenancePage()
        => RedirectToPage("/Admin/Maintenance", new { maxVersionsToKeep = Input.MaxVersionsToKeep });

    private async Task LoadPageDataAsync(CancellationToken ct)
    {
        Preview = await LoadPreviewCoreAsync(ct);
        await LoadOperationalListsAsync(ct);
    }

    private async Task LoadOperationalListsAsync(CancellationToken ct)
    {
        MaintenanceFindings = await _repo.GetMaintenanceFindingsAsync(DefaultMaintenanceFindingLimit, ct);
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
        RecentHostAgentJobs = await _repo.GetRecentHostAgentJobsAsync(DefaultRecentHostAgentJobLimit, ct);
    }

    public static string FormatHostAgentJobStatus(byte status)
        => status switch
        {
            HostAgentJobStatuses.Pending => "Pending",
            HostAgentJobStatuses.Running => "Running",
            HostAgentJobStatuses.Succeeded => "Succeeded",
            HostAgentJobStatuses.Failed => "Failed",
            HostAgentJobStatuses.Warning => "Warning",
            HostAgentJobStatuses.Cancelled => "Cancelled",
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
