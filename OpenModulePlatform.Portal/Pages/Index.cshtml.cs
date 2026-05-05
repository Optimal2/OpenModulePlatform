// File: OpenModulePlatform.Portal/Pages/Index.cshtml.cs
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.Extensions.Options;
using SharedRbacService = OpenModulePlatform.Web.Shared.Services.RbacService;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Pages;

/// <summary>
/// Portal start page showing the app catalog available to the current user.
/// </summary>
public sealed class IndexModel : OmpPageModel<PortalResource>
{
    private readonly AppCatalogService _catalog;
    private readonly SharedRbacService _rbac;
    private readonly OmpAdminRepository _repo;
    private readonly PortalUserSettingsService _userSettings;

    public IndexModel(
        IOptions<WebAppOptions> options,
        AppCatalogService catalog,
        SharedRbacService rbac,
        OmpAdminRepository repo,
        PortalUserSettingsService userSettings)
        : base(options)
    {
        _catalog = catalog;
        _rbac = rbac;
        _repo = repo;
        _userSettings = userSettings;
    }

    public IReadOnlyList<PortalAppEntry> Apps { get; private set; } = [];

    public bool IsPortalAdmin { get; private set; }

    public bool AdminMetricsCollapsed { get; private set; }

    public OverviewMetrics Metrics { get; private set; } = new();

    public async Task OnGet(CancellationToken ct)
    {
        SetTitles();

        var permissions = await _rbac.GetUserPermissionsAsync(User, ct);
        IsPortalAdmin = permissions.Contains(OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = IsPortalAdmin;

        if (IsPortalAdmin)
        {
            Metrics = await _repo.GetOverviewAsync(ct);

            if (TryGetCurrentUserId(out var userId))
            {
                var settings = await _userSettings.GetForUserAsync(userId, ct);
                AdminMetricsCollapsed = settings.AdminMetricsCollapsed;
            }
        }

        var allApps = await _catalog.GetEnabledWebAppsAsync(ct);
        Apps = _catalog.FilterByPermissions(allApps, permissions);
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        var userIdClaim = User.FindFirstValue(OpenModulePlatform.Web.Shared.Security.OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(userIdClaim, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out userId);
    }
}
