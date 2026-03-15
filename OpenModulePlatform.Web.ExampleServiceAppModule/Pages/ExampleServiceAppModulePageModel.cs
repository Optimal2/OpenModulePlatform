// File: OpenModulePlatform.Web.ExampleServiceAppModule/Pages/ExampleServiceAppModulePageModel.cs
using OpenModulePlatform.Web.ExampleServiceAppModule.Security;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleServiceAppModule.Pages;

public abstract class ExampleServiceAppModulePageModel : OmpSecurePageModel
{
    protected ExampleServiceAppModulePageModel(IOptions<WebAppOptions> options, RbacService rbac)
        : base(options, rbac)
    {
    }

    protected Task<IActionResult?> RequireViewAsync(CancellationToken ct)
        => RequireAnyAsync(ct, ExampleServiceAppModulePermissions.View, ExampleServiceAppModulePermissions.Admin);

    protected Task<IActionResult?> RequireAdminAsync(CancellationToken ct)
        => RequireAnyAsync(ct, ExampleServiceAppModulePermissions.Admin);
}
