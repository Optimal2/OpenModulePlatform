// File: OpenModulePlatform.Portal/Pages/Admin/ConfigOverlayEditor.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Hosts the standalone config overlay editor for Portal administrators.
/// </summary>
public sealed class ConfigOverlayEditorModel : OmpPortalPageModel
{
    public ConfigOverlayEditorModel(IOptions<WebAppOptions> options, RbacService rbac)
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

        SetTitles("Config overlay editor");
        return Page();
    }
}
