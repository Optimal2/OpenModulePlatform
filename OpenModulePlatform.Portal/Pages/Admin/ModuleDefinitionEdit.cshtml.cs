// File: OpenModulePlatform.Portal/Pages/Admin/ModuleDefinitionEdit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Reviews and applies one stored module definition document.
/// </summary>
public sealed class ModuleDefinitionEditModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;

    public ModuleDefinitionEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ModuleDefinitionDocumentRow? Definition { get; private set; }

    public IReadOnlyList<ModuleDefinitionCompatibilityRow> CompatibilityRows { get; private set; } = [];

    public IReadOnlyList<ModuleDefinitionArtifactReferenceRow> IncompatibleReferences { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(int id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var result = await LoadAsync(id, ct);
        if (result is not null)
        {
            return result;
        }

        SetTitles("Module definition");
        return Page();
    }

    public async Task<IActionResult> OnPostApply(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var result = await LoadAsync(Input.ModuleDefinitionDocumentId, ct);
        if (result is not null)
        {
            return result;
        }

        SetTitles("Module definition");

        ModuleDefinitionApplyResult applyResult;
        try
        {
            applyResult = await _repo.ApplyModuleDefinitionDocumentAsync(
                Input.ModuleDefinitionDocumentId,
                Input.AllowTemporaryIncompatibleArtifacts,
                ct);
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(string.Empty, T($"The module definition could not be applied: {ex.Message}"));
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }

        if (!applyResult.Applied)
        {
            IncompatibleReferences = applyResult.IncompatibleReferences;
            ModelState.AddModelError(
                nameof(Input.AllowTemporaryIncompatibleArtifacts),
                T("The definition is not compatible with one or more currently selected artifacts. Confirm temporary incompatibility if the matching artifact update is the next step."));
            return Page();
        }

        StatusMessage = applyResult.IncompatibleReferences.Count == 0
            ? T("Module definition applied.")
            : string.Format(
                T("Module definition applied with {0} temporarily incompatible artifact references."),
                applyResult.IncompatibleReferences.Count);

        return RedirectToPage("/Admin/ModuleDefinitionEdit", new { id = Input.ModuleDefinitionDocumentId });
    }

    private async Task<IActionResult?> LoadAsync(int id, CancellationToken ct)
    {
        Definition = await _repo.GetModuleDefinitionDocumentAsync(id, ct);
        if (Definition is null)
        {
            return NotFound();
        }

        Input.ModuleDefinitionDocumentId = Definition.ModuleDefinitionDocumentId;
        CompatibilityRows = await _repo.GetModuleDefinitionCompatibilityAsync(id, ct);
        IncompatibleReferences = await _repo.GetIncompatibleArtifactReferencesForModuleDefinitionAsync(id, ct);
        return null;
    }

    public sealed class InputModel
    {
        public int ModuleDefinitionDocumentId { get; set; }

        public bool AllowTemporaryIncompatibleArtifacts { get; set; }
    }
}
