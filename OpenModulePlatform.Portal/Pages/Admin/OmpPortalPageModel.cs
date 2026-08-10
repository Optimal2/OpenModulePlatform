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
public abstract class OmpPortalPageModel : OmpSecurePageModel
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
