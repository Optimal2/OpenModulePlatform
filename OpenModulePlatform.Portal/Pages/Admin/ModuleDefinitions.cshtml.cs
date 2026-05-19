// File: OpenModulePlatform.Portal/Pages/Admin/ModuleDefinitions.cshtml.cs
using Microsoft.AspNetCore.Mvc;
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

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Module definition versions");
        Rows = await _repo.GetModuleDefinitionDocumentsAsync(ct);
        return Page();
    }
}
