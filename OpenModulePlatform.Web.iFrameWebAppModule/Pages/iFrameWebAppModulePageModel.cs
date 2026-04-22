using OpenModulePlatform.Web.iFrameWebAppModule.Localization;
using OpenModulePlatform.Web.iFrameWebAppModule.Security;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.iFrameWebAppModule.Pages;

public abstract class iFrameWebAppModulePageModel : OmpSecurePageModel<IFrameWebAppModuleResource>
{
    protected iFrameWebAppModulePageModel(
        IOptions<WebAppOptions> options,
        RbacService rbac)
        : base(options, rbac)
    {
    }

    protected Task<IActionResult?> RequireViewAsync(CancellationToken ct)
        => RequireAnyAsync(
            ct,
            IFrameWebAppModulePermissions.View,
            IFrameWebAppModulePermissions.Admin);

    protected Task<IActionResult?> RequireAdminAsync(CancellationToken ct)
        => RequireAnyAsync(ct, IFrameWebAppModulePermissions.Admin);
}
