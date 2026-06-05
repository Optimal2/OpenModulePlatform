// File: OpenModulePlatform.Portal/Pages/Admin/DashboardWidgets.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Manages Portal dashboard widget definitions.
/// </summary>
public sealed class DashboardWidgetsModel : OmpPortalPageModel
{
    private readonly PortalDashboardWidgetPackageService _widgets;

    public DashboardWidgetsModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        PortalDashboardWidgetPackageService widgets)
        : base(options, rbac)
    {
        _widgets = widgets;
    }

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<DashboardWidgetAdminRow> Rows { get; private set; } = [];

    public DashboardWidgetAdminRow? EditDescriptionRow { get; private set; }

    public async Task<IActionResult> OnGet(int? editWidgetId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Dashboard widgets");
        await LoadAsync(ct, editWidgetId);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateDescription(int widgetId, string? description, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Dashboard widgets");
        try
        {
            await _widgets.UpdateWidgetDescriptionAsync(widgetId, description, ct);
            StatusMessage = T("Dashboard widget description updated.");
            return RedirectToPage("/Admin/DashboardWidgets");
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct, widgetId);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSetEnabled(int widgetId, bool isEnabled, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await _widgets.SetWidgetEnabledAsync(widgetId, isEnabled, ct);
        StatusMessage = isEnabled
            ? T("Dashboard widget enabled.")
            : T("Dashboard widget disabled.");

        return RedirectToPage("/Admin/DashboardWidgets");
    }

    private async Task LoadAsync(CancellationToken ct, int? editWidgetId = null)
    {
        Rows = await _widgets.GetWidgetsAsync(null, ct);
        if (editWidgetId.HasValue)
        {
            EditDescriptionRow = Rows.FirstOrDefault(row => row.WidgetId == editWidgetId.Value);
        }
    }
}
