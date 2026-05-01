// File: OpenModulePlatform.Web.ExampleWebAppModule/Pages/ExampleWebAppModulePageModel.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Localization;
using OpenModulePlatform.Web.ExampleWebAppModule.Security;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleWebAppModule.Pages;

/// <summary>
/// Base page model for the simple web-only example module.
/// </summary>
public abstract class ExampleWebAppModulePageModel : OmpSecurePageModel<ExampleWebAppModuleResource>
{
    protected ExampleWebAppModulePageModel(
        IOptions<WebAppOptions> options,
        RbacService rbac)
        : base(options, rbac)
    {
    }

    protected Task<IActionResult?> RequireViewAsync(CancellationToken ct)
        => RequireAnyAsync(
            ct,
            ExampleWebAppModulePermissions.View,
            ExampleWebAppModulePermissions.Admin);

    protected Task<IActionResult?> RequireAdminAsync(CancellationToken ct)
        => RequireAnyAsync(ct, ExampleWebAppModulePermissions.Admin);
}
