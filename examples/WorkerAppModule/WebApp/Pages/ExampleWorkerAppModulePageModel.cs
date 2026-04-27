// File: OpenModulePlatform.Web.ExampleWorkerAppModule/Pages/ExampleWorkerAppModulePageModel.cs
using OpenModulePlatform.Web.ExampleWorkerAppModule.Localization;
using OpenModulePlatform.Web.ExampleWorkerAppModule.Security;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleWorkerAppModule.Pages;

/// <summary>
/// Base page model for the worker-manager-backed example module UI.
/// </summary>
public abstract class ExampleWorkerAppModulePageModel : OmpSecurePageModel<ExampleWorkerAppModuleResource>
{
    protected ExampleWorkerAppModulePageModel(
        IOptions<WebAppOptions> options,
        RbacService rbac)
        : base(options, rbac)
    {
    }

    protected Task<IActionResult?> RequireViewAsync(CancellationToken ct)
        => RequireAnyAsync(
            ct,
            ExampleWorkerAppModulePermissions.View,
            ExampleWorkerAppModulePermissions.Admin);

    protected Task<IActionResult?> RequireAdminAsync(CancellationToken ct)
        => RequireAnyAsync(ct, ExampleWorkerAppModulePermissions.Admin);
}
