// File: OpenModulePlatform.Portal/Pages/Admin/OmpPortalPageModel.cs
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin;

public abstract class OmpPortalPageModel : OmpSecurePageModel
{
    protected OmpPortalPageModel(IOptions<WebAppOptions> options, RbacService rbac)
        : base(options, rbac)
    {
    }

    public async Task<IActionResult?> RequirePortalAdminAsync(CancellationToken ct)
    {
        var result = await RequireAnyAsync(ct, OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = result is null;
        return result;
    }
}
