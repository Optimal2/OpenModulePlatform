// File: OpenModulePlatform.Web.ExampleServiceAppModule/Pages/HostInstallations/Index.cshtml.cs
using OpenModulePlatform.Web.ExampleServiceAppModule.Services;
using OpenModulePlatform.Web.ExampleServiceAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleServiceAppModule.Pages.HostInstallations;

public sealed class IndexModel : ExampleServiceAppModulePageModel
{
    private readonly ExampleServiceAppModuleAdminRepository _repo;

    public IndexModel(IOptions<WebAppOptions> options, RbacService rbac, ExampleServiceAppModuleAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<HostInstallationRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Host installations");
        Rows = await _repo.GetHostInstallationsAsync(ct);
        return Page();
    }
}
