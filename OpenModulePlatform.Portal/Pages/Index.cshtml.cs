// File: OpenModulePlatform.Portal/Pages/Index.cshtml.cs
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SharedRbacService = OpenModulePlatform.Web.Shared.Services.RbacService;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Pages;

/// <summary>
/// Portal start page showing the app catalog available to the current user.
/// </summary>
public sealed class IndexModel : OmpPageModel<PortalResource>
{
    private readonly PortalEntryService _portalEntries;
    private readonly SharedRbacService _rbac;
    private readonly OmpAdminRepository _repo;
    private readonly PortalUserSettingsService _userSettings;

    public IndexModel(
        IOptions<WebAppOptions> options,
        PortalEntryService portalEntries,
        SharedRbacService rbac,
        OmpAdminRepository repo,
        PortalUserSettingsService userSettings)
        : base(options)
    {
        _portalEntries = portalEntries;
        _rbac = rbac;
        _repo = repo;
        _userSettings = userSettings;
    }

    public IReadOnlyList<PortalEntry> PinnedEntries { get; private set; } = [];

    public IReadOnlyList<PortalEntry> AllEntries { get; private set; } = [];

    public bool CanPersonalizeEntries { get; private set; }

    public bool ManageList { get; private set; }

    public bool ShowFullList { get; private set; }

    public bool IsPortalAdmin { get; private set; }

    public bool AdminMetricsCollapsed { get; private set; }

    public OverviewMetrics Metrics { get; private set; } = new();

    public async Task OnGet(bool manage = false, bool fullList = false, CancellationToken ct = default)
    {
        await LoadAsync(manage, fullList, ct);
    }

    public async Task<IActionResult> OnPostPin(int portalEntryId, bool manage, bool fullList, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _portalEntries.SetPinnedAsync(userId, portalEntryId, true, ct);
        return RedirectToPage(new { manage, fullList });
    }

    public async Task<IActionResult> OnPostUnpin(int portalEntryId, bool manage, bool fullList, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _portalEntries.SetPinnedAsync(userId, portalEntryId, false, ct);
        return RedirectToPage(new { manage, fullList });
    }

    public async Task<IActionResult> OnPostHide(int portalEntryId, bool manage, bool fullList, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _portalEntries.SetHiddenAsync(userId, portalEntryId, true, ct);
        return RedirectToPage(new { manage, fullList });
    }

    public async Task<IActionResult> OnPostUnhide(int portalEntryId, bool manage, bool fullList, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _portalEntries.SetHiddenAsync(userId, portalEntryId, false, ct);
        return RedirectToPage(new { manage, fullList });
    }

    public async Task<IActionResult> OnPostSortPinned(string orderedPortalEntryIds, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var ids = (orderedPortalEntryIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        await _portalEntries.UpdatePinnedSortOrderAsync(userId, ids, ct);
        return new JsonResult(new { ok = true });
    }

    private async Task LoadAsync(bool manage, bool fullList, CancellationToken ct)
    {
        SetTitles();

        ManageList = manage;
        ShowFullList = fullList;

        var permissions = await _rbac.GetUserPermissionsAsync(User, ct);
        IsPortalAdmin = permissions.Contains(OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = IsPortalAdmin;

        var userId = TryGetCurrentUserId(out var resolvedUserId)
            ? resolvedUserId
            : (int?)null;
        CanPersonalizeEntries = userId.HasValue;

        if (IsPortalAdmin)
        {
            Metrics = await _repo.GetOverviewAsync(ct);

            if (userId.HasValue)
            {
                var settings = await _userSettings.GetForUserAsync(userId.Value, ct);
                AdminMetricsCollapsed = settings.AdminMetricsCollapsed;
            }
        }

        var entries = await _portalEntries.GetEntriesAsync(Request, userId, permissions, fullList, ct);
        PinnedEntries = entries
            .Where(entry => entry.IsPinned && !entry.IsHidden)
            .OrderBy(entry => entry.UserSortOrder ?? int.MaxValue)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AllEntries = entries
            .Where(entry => entry.IsHidden || !entry.IsPinned)
            .OrderBy(entry => entry.IsHidden)
            .ThenBy(entry => entry.DefaultSortOrder)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        var userIdClaim = User.FindFirstValue(OpenModulePlatform.Web.Shared.Security.OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(userIdClaim, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out userId);
    }
}
