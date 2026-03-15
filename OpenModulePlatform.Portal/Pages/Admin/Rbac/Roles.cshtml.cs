// File: OpenModulePlatform.Portal/Pages/Admin/Rbac/Roles.cshtml.cs
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin.Rbac;

public sealed class RolesModel : Pages.Admin.OmpPortalPageModel
{
    private readonly RbacAdminRepository _repo;

    public RolesModel(IOptions<WebAppOptions> options, RbacService rbac, RbacAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<RoleRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Roles");
        Rows = await _repo.GetRolesAsync(ct);
        return Page();
    }
}
