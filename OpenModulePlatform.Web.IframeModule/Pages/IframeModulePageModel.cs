using OpenModulePlatform.Web.IframeModule.Localization;
using OpenModulePlatform.Web.IframeModule.Security;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.IframeModule.Pages;

public abstract class IframeModulePageModel : OmpSecurePageModel<IframeModuleResource>
{
    private readonly RbacService _rbac;

    protected IframeModulePageModel(
        IOptions<WebAppOptions> options,
        RbacService rbac)
        : base(options, rbac)
    {
        _rbac = rbac;
    }

    protected Task<IActionResult?> RequireViewAsync(CancellationToken ct)
        => RequireAnyAsync(
            ct,
            IframeModulePermissions.View,
            IframeModulePermissions.Admin);

    protected Task<IActionResult?> RequireAdminAsync(CancellationToken ct)
        => RequireAnyAsync(ct, IframeModulePermissions.Admin);

    protected async Task<string?> GetActiveRoleNameAsync(CancellationToken ct)
    {
        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        return roleContext.ActiveRoleName;
    }
}
