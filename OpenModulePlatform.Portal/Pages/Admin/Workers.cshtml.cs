// File: OpenModulePlatform.Portal/Pages/Admin/Workers.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class WorkersModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;

    public WorkersModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<AppWorkerDefinitionRow> AppWorkerRows { get; private set; } = [];

    public IReadOnlyList<AppWorkerRuntimeRow> RuntimeRows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Workers");
        AppWorkerRows = await _repo.GetAppWorkerDefinitionsAsync(ct);
        RuntimeRows = await _repo.GetAppWorkerRuntimeAsync(ct);
        return Page();
    }

    public string GetObservedStateLabel(byte observedState)
        => observedState switch
        {
            1 => T("Starting"),
            2 => T("Running"),
            3 => T("Stopping"),
            4 => T("Stopped"),
            5 => T("Failed"),
            _ => T("Unknown")
        };

    public string GetDesiredStateLabel(byte desiredState, bool isAllowed)
    {
        if (!isAllowed)
        {
            return T("Blocked");
        }

        return desiredState switch
        {
            1 => T("Should run"),
            2 => T("Should stop"),
            _ => T("Unmanaged")
        };
    }
}
