// File: OpenModulePlatform.Portal/Pages/Admin/Overview.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class OverviewModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;

    public OverviewModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public OverviewMetrics Metrics { get; private set; } = new();

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Overview");
        Metrics = await _repo.GetOverviewAsync(ct);
        return Page();
    }
}
