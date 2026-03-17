// File: OpenModulePlatform.Portal/Pages/Admin/Rbac/Role.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Pages.Admin.Rbac;

/// <summary>
/// Edits a role and its related permission and principal assignments.
/// Role-centric editing keeps RBAC administration aligned with how operators typically reason about access.
/// </summary>
public sealed class RoleModel : Pages.Admin.OmpPortalPageModel
{
    private static readonly Regex NamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{1,199}$",
        RegexOptions.Compiled);

    private readonly RbacAdminRepository _repo;

    public RoleModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        RbacAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? NewPrincipal { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.RoleId == 0;

    public IReadOnlyList<RolePermissionRow> Permissions { get; private set; } = [];

    public IReadOnlyList<RolePrincipalRow> Principals { get; private set; } = [];

    public IReadOnlyList<OptionItem> AvailablePermissionOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> PrincipalTypeOptions { get; } =
    [
        new() { Value = "User", Label = "User" },
        new() { Value = "Group", Label = "Group" },
        new() { Value = "ServiceAccount", Label = "ServiceAccount" },
        new() { Value = "Host", Label = "Host" }
    ];

    public async Task<IActionResult> OnGet(int? roleId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (!roleId.HasValue)
        {
            SetTitles("Create role");
            return Page();
        }

        var role = await _repo.GetRoleAsync(roleId.Value, ct);
        if (role is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            RoleId = role.RoleId,
            Name = role.Name,
            Description = role.Description
        };

        await LoadDetailsAsync(ct);
        SetTitles("Edit role");
        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        ValidateRole();
        if (!ModelState.IsValid)
        {
            await ReloadExistingRoleStateAsync(ct);
            SetTitles(IsCreate ? "Create role" : "Edit role");
            return Page();
        }

        try
        {
            var roleId = await _repo.SaveRoleAsync(
                new RoleEditData
                {
                    RoleId = Input.RoleId,
                    Name = Input.Name.Trim(),
                    Description = Clean(Input.Description)
                },
                ct);

            StatusMessage = IsCreate ? "Role created." : "Role updated.";
            return RedirectToPage("/Admin/Rbac/Role", new { roleId });
        }
        catch (SqlException ex)
        {
            await ReloadExistingRoleStateAsync(ct);
            SetTitles(IsCreate ? "Create role" : "Edit role");
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The role could not be saved."));

            return Page();
        }
    }

    public async Task<IActionResult> OnPostAddPermission(int permissionId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (Input.RoleId <= 0)
        {
            return RedirectToPage("/Admin/Rbac/Roles");
        }

        if (permissionId <= 0)
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(string.Empty, "Select a permission to add.");
            return Page();
        }

        await _repo.AddPermissionToRoleAsync(Input.RoleId, permissionId, ct);
        StatusMessage = "Permission added to role.";
        return RedirectToPage("/Admin/Rbac/Role", new { roleId = Input.RoleId });
    }

    public async Task<IActionResult> OnPostRemovePermission(int permissionId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (Input.RoleId <= 0)
        {
            return RedirectToPage("/Admin/Rbac/Roles");
        }

        await _repo.RemovePermissionFromRoleAsync(Input.RoleId, permissionId, ct);
        StatusMessage = "Permission removed from role.";
        return RedirectToPage("/Admin/Rbac/Role", new { roleId = Input.RoleId });
    }

    public async Task<IActionResult> OnPostAddPrincipal(string principalType, string principal, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (Input.RoleId <= 0)
        {
            return RedirectToPage("/Admin/Rbac/Roles");
        }

        principalType = Clean(principalType) ?? string.Empty;
        principal = Clean(principal) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(principalType) || string.IsNullOrWhiteSpace(principal))
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(
                string.Empty,
                "Select a principal type and enter a principal value.");
            NewPrincipal = principal;
            return Page();
        }

        if (principal.Length > 256)
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(string.Empty, "Principal is too long.");
            NewPrincipal = principal;
            return Page();
        }

        await _repo.AddPrincipalToRoleAsync(Input.RoleId, principalType, principal, ct);
        StatusMessage = "Principal added to role.";
        return RedirectToPage("/Admin/Rbac/Role", new { roleId = Input.RoleId });
    }

    public async Task<IActionResult> OnPostRemovePrincipal(string principalType, string principal, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (Input.RoleId <= 0)
        {
            return RedirectToPage("/Admin/Rbac/Roles");
        }

        await _repo.RemovePrincipalFromRoleAsync(Input.RoleId, principalType, principal, ct);
        StatusMessage = "Principal removed from role.";
        return RedirectToPage("/Admin/Rbac/Role", new { roleId = Input.RoleId });
    }

    public async Task<IActionResult> OnPostDelete(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (Input.RoleId <= 0)
        {
            return RedirectToPage("/Admin/Rbac/Roles");
        }

        try
        {
            await _repo.DeleteRoleAsync(Input.RoleId, ct);
            StatusMessage = "Role deleted.";
            return RedirectToPage("/Admin/Rbac/Roles");
        }
        catch (SqlException ex)
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The role could not be deleted."));

            return Page();
        }
    }

    private async Task ReloadExistingRoleStateAsync(CancellationToken ct)
    {
        if (IsCreate)
        {
            return;
        }

        await LoadDetailsAsync(ct);
    }

    private async Task LoadDetailsAsync(CancellationToken ct)
    {
        Permissions = await _repo.GetRolePermissionsAsync(Input.RoleId, ct);
        Principals = await _repo.GetRolePrincipalsAsync(Input.RoleId, ct);

        AvailablePermissionOptions = (await _repo.GetAvailablePermissionsForRoleAsync(Input.RoleId, ct))
            .Select(
                x => new OptionItem
                {
                    Value = x.PermissionId.ToString(),
                    Label = x.Name
                })
            .ToArray();
    }

    private void ValidateRole()
    {
        if (!NamePattern.IsMatch(Input.Name ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.Name),
                "Use letters, digits, dash, underscore or dot. Keep the role name stable.");
        }
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "A role with the same name already exists.",
            547 => "Delete or update dependent rows first.",
            _ => fallback
        };

    public sealed class InputModel
    {
        public int RoleId { get; set; }

        [Required]
        [StringLength(200)]
        [Display(Name = "Role name")]
        public string Name { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }
    }
}
