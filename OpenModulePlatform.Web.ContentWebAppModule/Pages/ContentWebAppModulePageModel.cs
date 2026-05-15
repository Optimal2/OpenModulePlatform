// File: OpenModulePlatform.Web.ContentWebAppModule/Pages/ContentWebAppModulePageModel.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Localization;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Security;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;

namespace OpenModulePlatform.Web.ContentWebAppModule.Pages;

public abstract class ContentWebAppModulePageModel : OmpSecurePageModel<ContentWebAppModuleResource>
{
    private readonly IOptions<ContentWebAppModuleOptions> _contentOptions;
    private readonly RbacService _rbac;

    protected ContentWebAppModulePageModel(
        IOptions<WebAppOptions> options,
        IOptions<ContentWebAppModuleOptions> contentOptions,
        RbacService rbac)
        : base(options, rbac)
    {
        _contentOptions = contentOptions;
        _rbac = rbac;
    }

    protected ContentWebAppModuleOptions ContentOptions => _contentOptions.Value;

    protected Guid AppInstanceId => ContentOptions.AppInstanceId;

    protected Task<IActionResult?> RequireManageAsync(CancellationToken ct)
        => RequireAnyAsync(ct, ContentWebAppModulePermissions.Manage);

    protected async Task<ContentAccessContext> GetContentAccessContextAsync(CancellationToken ct)
    {
        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        var roleIds = roleContext.EffectiveRoleIds
            .Distinct()
            .ToArray();

        var canManageAll = CanManageAllContent(roleContext);
        if (!canManageAll)
        {
            foreach (var roleId in roleIds)
            {
                var permissions = await _rbac.GetPermissionsForRoleAsync(User, roleId, ct);
                if (permissions.Contains(ContentWebAppModulePermissions.Manage))
                {
                    canManageAll = true;
                    break;
                }
            }
        }

        return new ContentAccessContext(roleIds, canManageAll);
    }

    protected static bool CanManageAllContent(UserRoleContext roleContext)
        => roleContext.EffectivePermissions.Contains(ContentWebAppModulePermissions.Manage);

    protected async Task SetContentTitlesAsync(string? pageTitle, CancellationToken ct)
    {
        SetTitles(pageTitle);
        var accessContext = await GetContentAccessContextAsync(ct);
        ViewData["CanManageContent"] = accessContext.CanManageAll;
    }

    protected IActionResult? ValidateAppInstanceConfigured()
    {
        if (AppInstanceId != Guid.Empty)
        {
            return null;
        }

        return StatusCode(
            StatusCodes.Status500InternalServerError,
            T("ContentWebAppModule:AppInstanceId is not configured."));
    }

    protected string CurrentUserName()
        => User?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(User.Identity.Name)
            ? User.Identity.Name!
            : "unknown";

    protected sealed record ContentAccessContext(IReadOnlyList<int> RoleIds, bool CanManageAll);
}
