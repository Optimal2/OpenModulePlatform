// File: OpenModulePlatform.Portal/Pages/Admin/Rbac/PermissionEdit.cshtml.cs
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Pages.Admin.Rbac;

public sealed class PermissionEditModel : Pages.Admin.OmpPortalPageModel
{
    private readonly RbacAdminRepository _repo;
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{1,199}$", RegexOptions.Compiled);

    public PermissionEditModel(IOptions<WebAppOptions> options, RbacService rbac, RbacAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    public bool IsCreate => Input.PermissionId == 0;
    public int RoleCount { get; private set; }

    public async Task<IActionResult> OnGet(int? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null) return guard;

        if (id.HasValue)
        {
            var row = await _repo.GetPermissionAsync(id.Value, ct);
            if (row is null) return NotFound();
            Input = new InputModel { PermissionId = row.PermissionId, Name = row.Name, Description = row.Description };
            RoleCount = row.RoleCount;
            SetTitles("Edit permission");
        }
        else
        {
            SetTitles("Create permission");
        }

        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null) return guard;

        ValidatePermission();
        if (!ModelState.IsValid)
        {
            if (!IsCreate)
            {
                var row = await _repo.GetPermissionAsync(Input.PermissionId, ct);
                RoleCount = row?.RoleCount ?? 0;
            }
            SetTitles(IsCreate ? "Create permission" : "Edit permission");
            return Page();
        }

        try
        {
            var id = await _repo.SavePermissionAsync(new PermissionEditData { PermissionId = Input.PermissionId, Name = Input.Name.Trim(), Description = Clean(Input.Description) }, ct);
            StatusMessage = IsCreate ? "Permission created." : "Permission updated.";
            return RedirectToPage("~/admin/rbac/permissionedit", new { id });
        }
        catch (SqlException ex)
        {
            if (!IsCreate)
            {
                var row = await _repo.GetPermissionAsync(Input.PermissionId, ct);
                RoleCount = row?.RoleCount ?? 0;
            }
            SetTitles(IsCreate ? "Create permission" : "Edit permission");
            ModelState.AddModelError(string.Empty, ToFriendlySqlMessage(ex, "The permission could not be saved."));
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDelete(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null) return guard;
        if (Input.PermissionId <= 0) return RedirectToPage("~/admin/rbac/permissions");
        try
        {
            await _repo.DeletePermissionAsync(Input.PermissionId, ct);
            StatusMessage = "Permission deleted.";
            return RedirectToPage("~/admin/rbac/permissions");
        }
        catch (SqlException ex)
        {
            var row = await _repo.GetPermissionAsync(Input.PermissionId, ct);
            RoleCount = row?.RoleCount ?? 0;
            SetTitles("Edit permission");
            ModelState.AddModelError(string.Empty, ToFriendlySqlMessage(ex, "The permission could not be deleted."));
            return Page();
        }
    }

    private void ValidatePermission()
    {
        if (!NamePattern.IsMatch(Input.Name ?? string.Empty))
            ModelState.AddModelError(nameof(Input.Name), "Use letters, digits, dash, underscore or dot. Keep permission names stable.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string ToFriendlySqlMessage(SqlException ex, string fallback) => ex.Number is 2601 or 2627 ? "A permission with the same name already exists." : ex.Number == 547 ? "Delete or update dependent rows first." : fallback;

    public sealed class InputModel
    {
        public int PermissionId { get; set; }
        [Required, StringLength(200), Display(Name = "Permission name")]
        public string Name { get; set; } = string.Empty;
        [StringLength(500)]
        public string? Description { get; set; }
    }
}
