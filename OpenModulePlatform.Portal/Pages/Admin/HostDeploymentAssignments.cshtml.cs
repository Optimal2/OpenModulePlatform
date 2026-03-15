// File: OpenModulePlatform.Portal/Pages/Admin/HostDeploymentAssignments.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class HostDeploymentAssignmentsModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;

    public HostDeploymentAssignmentsModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<HostDeploymentAssignmentRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Host deployment Assignments");
        Rows = await _repo.GetHostDeploymentAssignmentsAsync(ct);
        return Page();
    }
}
