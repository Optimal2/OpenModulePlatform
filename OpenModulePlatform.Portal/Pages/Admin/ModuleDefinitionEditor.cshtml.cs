// File: OpenModulePlatform.Portal/Pages/Admin/ModuleDefinitionEditor.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Hosts the standalone module definition editor for Portal administrators.
/// </summary>
public sealed class ModuleDefinitionEditorModel : OmpPortalPageModel
{
    public ModuleDefinitionEditorModel(IOptions<WebAppOptions> options, RbacService rbac)
        : base(options, rbac)
    {
    }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Module definition editor");
        return Page();
    }
}
