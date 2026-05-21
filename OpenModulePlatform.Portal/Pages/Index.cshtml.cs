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
using System.Text.Json;

namespace OpenModulePlatform.Portal.Pages;

/// <summary>
/// Portal start page showing the user's dashboard.
/// </summary>
public sealed class IndexModel : OmpPageModel<PortalResource>
{
    private readonly PortalDashboardService _dashboard;
    private readonly SharedRbacService _rbac;
    private readonly OmpAdminRepository _repo;
    private readonly PortalUserSettingsService _userSettings;

    public IndexModel(
        IOptions<WebAppOptions> options,
        PortalDashboardService dashboard,
        SharedRbacService rbac,
        OmpAdminRepository repo,
        PortalUserSettingsService userSettings)
        : base(options)
    {
        _dashboard = dashboard;
        _rbac = rbac;
        _repo = repo;
        _userSettings = userSettings;
    }

    public IReadOnlyList<DashboardActiveWidget> ActiveWidgets { get; private set; } = [];

    public IReadOnlyList<DashboardWidgetDefinition> AvailableWidgets { get; private set; } = [];

    public bool CanEditDashboard { get; private set; }

    public bool IsPortalAdmin { get; private set; }

    public bool AdminMetricsCollapsed { get; private set; }

    public OverviewMetrics Metrics { get; private set; } = new();

    public async Task OnGet(bool manage = false, bool fullList = false, CancellationToken ct = default)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAddWidget(int widgetId, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        var widget = await _dashboard.AddWidgetAsync(
            userId,
            widgetId,
            roleContext.EffectiveRoleIds.ToHashSet(),
            roleContext.EffectivePermissions,
            ct);
        if (widget is null)
        {
            return Forbid();
        }

        return new JsonResult(ToDashboardWidgetDto(widget));
    }

    public async Task<IActionResult> OnPostSaveDashboard(string widgetsJson, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var updates = string.IsNullOrWhiteSpace(widgetsJson)
            ? new List<DashboardWidgetLayoutUpdate>()
            : JsonSerializer.Deserialize<List<DashboardWidgetLayoutUpdate>>(
                widgetsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        await _dashboard.UpdateLayoutAsync(userId, updates, ct);
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostRemoveWidget(long userActiveWidgetId, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _dashboard.RemoveWidgetAsync(userId, userActiveWidgetId, ct);
        return new JsonResult(new { ok = true });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        SetTitles();

        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        var permissions = roleContext.EffectivePermissions;
        var roleIds = roleContext.EffectiveRoleIds.ToHashSet();
        IsPortalAdmin = permissions.Contains(OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = IsPortalAdmin;

        var userId = TryGetCurrentUserId(out var resolvedUserId)
            ? resolvedUserId
            : (int?)null;
        CanEditDashboard = userId.HasValue;

        if (IsPortalAdmin)
        {
            Metrics = await _repo.GetOverviewAsync(ct);

            if (userId.HasValue)
            {
                var settings = await _userSettings.GetForUserAsync(userId.Value, ct);
                AdminMetricsCollapsed = settings.AdminMetricsCollapsed;
            }
        }

        if (userId.HasValue)
        {
            ActiveWidgets = await _dashboard.GetActiveWidgetsAsync(userId.Value, roleIds, permissions, ct);
            AvailableWidgets = await _dashboard.GetAvailableWidgetsAsync(roleIds, permissions, ct);
        }
        else
        {
            ActiveWidgets = [];
            AvailableWidgets = [];
        }
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        var userIdClaim = User.FindFirstValue(OpenModulePlatform.Web.Shared.Security.OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(userIdClaim, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out userId);
    }

    private static object ToDashboardWidgetDto(DashboardActiveWidget widget)
        => new
        {
            userActiveWidgetId = widget.UserActiveWidgetId,
            widgetId = widget.WidgetId,
            title = widget.EffectiveTitle,
            widgetType = widget.WidgetType,
            payload = widget.Payload,
            offsetTop = widget.OffsetTop,
            offsetLeft = widget.OffsetLeft,
            width = widget.Width,
            height = widget.Height,
            orderPriority = widget.OrderPriority
        };
}
