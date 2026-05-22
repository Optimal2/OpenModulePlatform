// File: OpenModulePlatform.Portal/Pages/Admin/BootstrapConfigEditor.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Hosts the standalone bootstrap configuration editor for Portal administrators.
/// </summary>
public sealed class BootstrapConfigEditorModel : OmpPortalPageModel
{
    public BootstrapConfigEditorModel(IOptions<WebAppOptions> options, RbacService rbac)
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

        SetTitles("Bootstrap config editor");
        return Page();
    }
}
