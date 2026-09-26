// File: OpenModulePlatform.Web.ExampleWebAppModule/Pages/Widgets/Overview.cshtml.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Services;
using OpenModulePlatform.Web.ExampleWebAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleWebAppModule.Pages.Widgets;

/// <summary>
/// HTML fragment for the example module's <c>module-fragment</c> dashboard widget,
/// declared in <c>examples/WebAppModule/widgets/example_webapp-widgets.json</c>.
/// </summary>
/// <remarks>
/// The Portal requests this page server-side with the user's OMP cookies and inserts
/// the sanitized markup into the widget. The page enforces the same permission as the
/// module overview; a denied request answers with a redirect or error status, which
/// the Portal shows as its neutral placeholder.
/// </remarks>
public sealed class OverviewModel : ExampleWebAppModulePageModel
{
    private readonly ExampleWebAppModuleAdminRepository _repo;

    public OverviewModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        ExampleWebAppModuleAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public OverviewRow Overview { get; private set; } = new();

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        Overview = await _repo.GetOverviewAsync(ct);
        return Page();
    }
}
