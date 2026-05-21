// File: OpenModulePlatform.Portal/Pages/Admin/DashboardWidgets.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.Text.Json;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Imports and exports portable Portal dashboard widget definitions.
/// </summary>
public sealed class DashboardWidgetsModel : OmpPortalPageModel
{
    private const int MaxWidgetJsonBytes = 1024 * 1024;

    private readonly PortalDashboardWidgetPackageService _widgets;

    public DashboardWidgetsModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        PortalDashboardWidgetPackageService widgets)
        : base(options, rbac)
    {
        _widgets = widgets;
    }

    [BindProperty]
    public UploadInputModel UploadInput { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<DashboardWidgetAdminRow> Rows { get; private set; } = [];

    public IReadOnlyList<string> ModuleKeys { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Dashboard widgets");
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostImport(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Dashboard widgets");
        ValidateUpload();
        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            await using var stream = UploadInput.JsonFile!.OpenReadStream();
            var result = await _widgets.ImportAsync(stream, UploadInput.JsonFile.FileName, ct);
            StatusMessage = string.Format(
                T("Imported dashboard widgets. Created: {0}. Updated: {1}. Permission rows: {2}."),
                result.CreatedCount,
                result.UpdatedCount,
                result.PermissionRowCount);

            return RedirectToPage("/Admin/DashboardWidgets");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or SqlException)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnGetExport(string? moduleKey, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var result = await _widgets.ExportAsync(moduleKey, ct);
            return File(result.Content, "application/json; charset=utf-8", result.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(T(ex.Message));
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await _widgets.GetWidgetsAsync(null, ct);
        ModuleKeys = Rows
            .Select(static row => row.ModuleKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ValidateUpload()
    {
        if (UploadInput.JsonFile is null || UploadInput.JsonFile.Length == 0)
        {
            ModelState.AddModelError(nameof(UploadInput.JsonFile), T("Select one dashboard widget JSON file."));
            return;
        }

        if (!UploadInput.JsonFile.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(UploadInput.JsonFile), T("The uploaded dashboard widget file must be a .json file."));
        }

        if (UploadInput.JsonFile.Length > MaxWidgetJsonBytes)
        {
            ModelState.AddModelError(
                nameof(UploadInput.JsonFile),
                string.Format(
                    T("The uploaded dashboard widget file exceeds the configured limit of {0} bytes."),
                    MaxWidgetJsonBytes));
        }
    }

    public sealed class UploadInputModel
    {
        public IFormFile? JsonFile { get; set; }
    }
}
