// File: OpenModulePlatform.Web.ExampleWebAppModule/Pages/Configurations/Index.cshtml.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Services;
using OpenModulePlatform.Web.ExampleWebAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleWebAppModule.Pages.Configurations;

public sealed class IndexModel : ExampleWebAppModulePageModel
{
    private readonly ExampleWebAppModuleAdminRepository _repo;

    public IndexModel(IOptions<WebAppOptions> options, RbacService rbac, ExampleWebAppModuleAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<ConfigurationRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Configurations");
        Rows = await _repo.GetConfigurationsAsync(ct);
        return Page();
    }
}
