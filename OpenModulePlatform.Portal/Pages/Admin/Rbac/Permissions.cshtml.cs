// File: OpenModulePlatform.Portal/Pages/Admin/Rbac/Permissions.cshtml.cs
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin.Rbac;

public sealed class PermissionsModel : Pages.Admin.OmpPortalPageModel
{
    private readonly RbacAdminRepository _repo;

    public PermissionsModel(IOptions<WebAppOptions> options, RbacService rbac, RbacAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<PermissionRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Permissions");
        Rows = await _repo.GetPermissionsAsync(ct);
        return Page();
    }
}
