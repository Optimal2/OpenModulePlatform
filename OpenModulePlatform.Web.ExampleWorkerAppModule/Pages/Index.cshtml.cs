// File: OpenModulePlatform.Web.ExampleWorkerAppModule/Pages/Index.cshtml.cs
using OpenModulePlatform.Web.ExampleWorkerAppModule.Services;
using OpenModulePlatform.Web.ExampleWorkerAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleWorkerAppModule.Pages;

public sealed class IndexModel : ExampleWorkerAppModulePageModel
{
    private readonly ExampleWorkerAppModuleAdminRepository _repo;

    public IndexModel(IOptions<WebAppOptions> options, RbacService rbac, ExampleWorkerAppModuleAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public OverviewRow Overview { get; private set; } = new();

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Overview");
        Overview = await _repo.GetOverviewAsync(ct);
        return Page();
    }
}
