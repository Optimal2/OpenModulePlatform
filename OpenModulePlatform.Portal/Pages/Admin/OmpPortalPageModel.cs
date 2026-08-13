// File: OpenModulePlatform.Portal/Pages/Admin/OmpPortalPageModel.cs
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Base class for Portal admin pages.
/// It centralizes the permission check for OMP.Portal.Admin so page models stay focused on their own data flow.
/// </summary>
/// <remarks>
/// The base is closed over PortalResource, not the SharedResource default. Without the type
/// argument every T("...") in every Portal admin page model read SharedResource (154 sv-SE
/// entries) while the Swedish text sat in PortalResource (2104 entries) -- so 295 keys had a
/// finished translation that was never read, and the operator saw the English key text. That
/// is invisible to a resx review, because the translations are there and look complete; the
/// only symptom is an English admin UI. PortalLocalizer below was added as a workaround and is
/// used on three pages; it still works, and now agrees with T() (R8-P5-1).
/// </remarks>
public abstract class OmpPortalPageModel : OmpSecurePageModel<PortalResource>
{
    protected OmpPortalPageModel(IOptions<WebAppOptions> options, RbacService rbac)
        : base(options, rbac)
    {
    }

    /// <summary>
    /// Portal resource localizer. The admin pages' <c>T()</c> helper targets
    /// SharedResource, so page-specific texts (including localized exception
    /// display via <see cref="PortalTextLocalizer"/>) resolve through this
    /// localizer instead.
    /// </summary>
    protected IStringLocalizer<PortalResource> PortalLocalizer =>
        HttpContext.RequestServices.GetRequiredService<IStringLocalizer<PortalResource>>();

    protected async Task<IActionResult?> RequirePortalAdminAsync(CancellationToken ct)
    {
        var result = await RequireAnyAsync(ct, OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = result is null;
        return result;
    }
}
