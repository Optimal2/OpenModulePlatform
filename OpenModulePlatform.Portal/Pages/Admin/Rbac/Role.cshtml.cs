// File: OpenModulePlatform.Portal/Pages/Admin/Rbac/Role.cshtml.cs
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin.Rbac;

public sealed class RoleModel : Pages.Admin.OmpPortalPageModel
{
    private readonly RbacAdminRepository _repo;

    public RoleModel(IOptions<WebAppOptions> options, RbacService rbac, RbacAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public RoleRow? Role { get; private set; }
    public IReadOnlyList<RolePermissionRow> Permissions { get; private set; } = [];
    public IReadOnlyList<RolePrincipalRow> Principals { get; private set; } = [];

    public async Task<IActionResult> OnGet(int roleId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Role");
        Role = await _repo.GetRoleAsync(roleId, ct);
        if (Role is not null)
        {
            Permissions = await _repo.GetRolePermissionsAsync(roleId, ct);
            Principals = await _repo.GetRolePrincipalsAsync(roleId, ct);
        }
        return Page();
    }
}
