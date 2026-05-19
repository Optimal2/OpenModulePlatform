// File: OpenModulePlatform.Portal/Pages/Admin/ModuleDefinitions.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Lists versioned module definition documents.
/// </summary>
public sealed class ModuleDefinitionsModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;

    public ModuleDefinitionsModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<ModuleDefinitionDocumentRow> Rows { get; private set; } = [];

    public IReadOnlyList<ModuleDefinitionIntegritySummaryRow> IntegrityRows { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Module definition versions");
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRepairAllSql(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var result = await _repo.ExecuteAppliedModuleDefinitionSqlRepairsAsync(ct);
            StatusMessage = result.ExecutedCount == 0
                ? T("No executable module definition SQL repairs were needed.")
                : string.Format(
                    T("Executed {0} module definition SQL repair scripts across active module definitions."),
                    result.ExecutedCount);
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(string.Empty, T($"The module definition SQL repair failed: {ex.Message}"));
            SetTitles("Module definition versions");
            await LoadAsync(ct);
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            SetTitles("Module definition versions");
            await LoadAsync(ct);
            return Page();
        }

        return RedirectToPage("/Admin/ModuleDefinitions");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await _repo.GetModuleDefinitionDocumentsAsync(ct);
        IntegrityRows = await _repo.GetModuleDefinitionIntegritySummariesAsync(ct);
    }
}
