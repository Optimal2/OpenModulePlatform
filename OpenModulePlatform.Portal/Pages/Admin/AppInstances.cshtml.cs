// File: OpenModulePlatform.Portal/Pages/Admin/AppInstances.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class AppInstancesModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;

    public AppInstancesModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<AppInstanceRow> Rows { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("App instances");
        Rows = await _repo.GetAppInstancesAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteRuntime(Guid appInstanceId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            await _repo.DeleteRuntimeAppInstanceRowAsync(appInstanceId, ct);
            StatusMessage = T("Runtime row deleted.");
        }
        catch (SqlException)
        {
            StatusMessage = T("The runtime row could not be deleted. Delete or update dependent rows first.");
        }

        return RedirectToPage("/Admin/AppInstances");
    }
}
