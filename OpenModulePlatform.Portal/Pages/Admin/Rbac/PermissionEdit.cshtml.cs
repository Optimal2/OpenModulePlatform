// File: OpenModulePlatform.Portal/Pages/Admin/Rbac/PermissionEdit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Pages.Admin.Rbac;

/// <summary>
/// Edits a permission definition in the RBAC catalog.
/// Permission names are expected to be stable because they become part of configuration and application contracts.
/// </summary>
public sealed class PermissionEditModel : Pages.Admin.OmpPortalPageModel
{
    private static readonly Regex NamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{1,199}$",
        RegexOptions.Compiled);

    private readonly RbacAdminRepository _repo;

    public PermissionEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        RbacAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.PermissionId == 0;

    public int RoleCount { get; private set; }

    public async Task<IActionResult> OnGet(int? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (!id.HasValue)
        {
            SetTitles("Create permission");
            return Page();
        }

        var row = await _repo.GetPermissionAsync(id.Value, ct);
        if (row is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            PermissionId = row.PermissionId,
            Name = row.Name,
            Description = row.Description
        };

        RoleCount = row.RoleCount;
        SetTitles("Edit permission");
        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        ValidatePermission();
        if (!ModelState.IsValid)
        {
            await ReloadUsageCountAsync(ct);
            SetTitles(IsCreate ? "Create permission" : "Edit permission");
            return Page();
        }

        try
        {
            var id = await _repo.SavePermissionAsync(
                new PermissionEditData
                {
                    PermissionId = Input.PermissionId,
                    Name = Input.Name.Trim(),
                    Description = Clean(Input.Description)
                },
                ct);

            StatusMessage = IsCreate ? "Permission created." : "Permission updated.";
            return RedirectToPage("/Admin/Rbac/PermissionEdit", new { id });
        }
        catch (SqlException ex)
        {
            await ReloadUsageCountAsync(ct);
            SetTitles(IsCreate ? "Create permission" : "Edit permission");
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The permission could not be saved."));

            return Page();
        }
    }

    public async Task<IActionResult> OnPostDelete(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (Input.PermissionId <= 0)
        {
            return RedirectToPage("/Admin/Rbac/Permissions");
        }

        try
        {
            await _repo.DeletePermissionAsync(Input.PermissionId, ct);
            StatusMessage = "Permission deleted.";
            return RedirectToPage("/Admin/Rbac/Permissions");
        }
        catch (SqlException ex)
        {
            await ReloadUsageCountAsync(ct);
            SetTitles("Edit permission");
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The permission could not be deleted."));

            return Page();
        }
    }

    private async Task ReloadUsageCountAsync(CancellationToken ct)
    {
        if (IsCreate)
        {
            RoleCount = 0;
            return;
        }

        var row = await _repo.GetPermissionAsync(Input.PermissionId, ct);
        RoleCount = row?.RoleCount ?? 0;
    }

    private void ValidatePermission()
    {
        if (!NamePattern.IsMatch(Input.Name ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.Name),
                "Use letters, digits, dash, underscore or dot. Keep permission names stable.");
        }
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "A permission with the same name already exists.",
            547 => "Delete or update dependent rows first.",
            _ => fallback
        };

    public sealed class InputModel
    {
        public int PermissionId { get; set; }

        [Required]
        [StringLength(200)]
        [Display(Name = "Permission name")]
        public string Name { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }
    }
}
