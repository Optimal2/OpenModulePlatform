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

    protected ContentWebAppModulePageModel(
        IOptions<WebAppOptions> options,
        IOptions<ContentWebAppModuleOptions> contentOptions,
        RbacService rbac)
        : base(options, rbac)
    {
        _contentOptions = contentOptions;
    }

    protected ContentWebAppModuleOptions ContentOptions => _contentOptions.Value;

    protected Guid AppInstanceId => ContentOptions.AppInstanceId;

    protected Task<IActionResult?> RequireViewAsync(CancellationToken ct)
        => RequireAnyAsync(
            ct,
            ContentWebAppModulePermissions.View,
            ContentWebAppModulePermissions.Manage);

    protected Task<IActionResult?> RequireManageAsync(CancellationToken ct)
        => RequireAnyAsync(ct, ContentWebAppModulePermissions.Manage);

    protected async Task SetContentTitlesAsync(string? pageTitle, CancellationToken ct)
    {
        SetTitles(pageTitle);
        var permissions = await GetUserPermissionsAsync(ct);
        ViewData["CanManageContent"] = permissions.Contains(ContentWebAppModulePermissions.Manage);
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
}
