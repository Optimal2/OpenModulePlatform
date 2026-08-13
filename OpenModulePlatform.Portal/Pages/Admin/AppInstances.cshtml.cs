// File: OpenModulePlatform.Portal/Pages/Admin/AppInstances.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Localization;
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
        // R8-P5-7: this told every failure the same story. A deadlock, a lock timeout or
        // an application THROW all reached the operator as "delete dependent rows first",
        // which is advice that does not apply and hides the one thing that would have
        // helped -- for a THROW, its own message.
        catch (SqlException ex)
        {
            StatusMessage = T(PortalTextLocalizer.DescribeSqlError(
                ex,
                "The runtime row could not be deleted.",
                duplicateMessage: null,
                foreignKeyMessage: "The runtime row could not be deleted. Delete or update dependent rows first."));
        }

        return RedirectToPage("/Admin/AppInstances");
    }
}
