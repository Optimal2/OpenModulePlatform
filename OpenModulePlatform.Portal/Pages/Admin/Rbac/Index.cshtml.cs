// File: OpenModulePlatform.Portal/Pages/Admin/Rbac/Index.cshtml.cs
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin.Rbac;

public sealed class IndexModel : Pages.Admin.OmpPortalPageModel
{
    private readonly RbacAdminRepository _repo;

    public IndexModel(IOptions<WebAppOptions> options, RbacService rbac, RbacAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public int RoleCount { get; private set; }
    public int PermissionCount { get; private set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("RBAC");
        RoleCount = (await _repo.GetRolesAsync(ct)).Count;
        PermissionCount = (await _repo.GetPermissionsAsync(ct)).Count;
        return Page();
    }
}
