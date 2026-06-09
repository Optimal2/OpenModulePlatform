// File: OpenModulePlatform.Portal/Pages/Admin/Rbac/Role.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Principal;
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

    private static readonly Regex OmpUserPrincipalLabelPattern = new(
        @"\(id:\s*(\d+)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

    [BindProperty]
    public string? ReturnUrl { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public string OriginalName { get; private set; } = string.Empty;

    public string OriginalDescription { get; private set; } = string.Empty;

    public bool IsCreate => Input.RoleId == 0;

    public IReadOnlyList<RolePermissionRow> Permissions { get; private set; } = [];

    public IReadOnlyList<RolePrincipalRow> Principals { get; private set; } = [];

    public IReadOnlyList<OptionItem> AvailablePermissionOptions { get; private set; } = [];

    public IReadOnlyList<PrincipalTypeOptionItem> PrincipalTypeOptions =>
    [
        new() { Value = "OmpUser", Label = T("OMP user") },
        new() { Value = "ADUser", Label = T("AD user") },
        new() { Value = "ADGroup", Label = T("AD group") }
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
            SetOriginalValues(null, null);
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

        SetOriginalValues(role.Name, role.Description);
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

            StatusMessage = IsCreate ? T("Role created.") : T("Role updated.");
            return RedirectAfterSave(roleId);
        }
        catch (SqlException ex)
        {
            await ReloadExistingRoleStateAsync(ct);
            SetTitles(IsCreate ? "Create role" : "Edit role");
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The role could not be saved.")));

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
            return RedirectToSecurityRoles();
        }

        if (permissionId <= 0)
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(string.Empty, T("Select a permission to add."));
            return Page();
        }

        await _repo.AddPermissionToRoleAsync(Input.RoleId, permissionId, ct);
        StatusMessage = T("Permission added to role.");
        return RedirectToSecurityRole(Input.RoleId);
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
            return RedirectToSecurityRoles();
        }

        await _repo.RemovePermissionFromRoleAsync(Input.RoleId, permissionId, ct);
        StatusMessage = T("Permission removed from role.");
        return RedirectToSecurityRole(Input.RoleId);
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
            return RedirectToSecurityRoles();
        }

        principalType = Clean(principalType) ?? string.Empty;
        principal = Clean(principal) ?? string.Empty;

        var normalizedPrincipal = await NormalizePrincipalAsync(principalType, principal, ct);
        if (!string.IsNullOrWhiteSpace(normalizedPrincipal.ErrorMessage))
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(string.Empty, await ApplyBrandingAsync(T(normalizedPrincipal.ErrorMessage), ct));
            NewPrincipal = principal;
            return Page();
        }

        principal = normalizedPrincipal.Principal ?? principal;

        if (string.IsNullOrWhiteSpace(principalType) || string.IsNullOrWhiteSpace(principal))
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(
                string.Empty, T("Select a principal type and enter a principal value."));
            NewPrincipal = principal;
            return Page();
        }

        if (principal.Length > 256)
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(string.Empty, T("Principal is too long."));
            NewPrincipal = principal;
            return Page();
        }

        var added = await _repo.AddPrincipalToRoleAsync(Input.RoleId, principalType, principal, ct);
        StatusMessage = added
            ? T("Principal added to role.")
            : T("Principal is already assigned to role.");
        return RedirectToSecurityRole(Input.RoleId);
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
            return RedirectToSecurityRoles();
        }

        await _repo.RemovePrincipalFromRoleAsync(Input.RoleId, principalType, principal, ct);
        StatusMessage = T("Principal removed from role.");
        return RedirectToSecurityRole(Input.RoleId);
    }

    /// <summary>
    /// Returns lightweight autocomplete suggestions for known role principals.
    /// </summary>
    public async Task<IActionResult> OnGetPrincipalSuggestions(string principalType, string? term, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        principalType = Clean(principalType) ?? string.Empty;
        term = Clean(term) ?? string.Empty;

        if (string.Equals(principalType, "OmpUser", StringComparison.OrdinalIgnoreCase))
        {
            var suggestions = await _repo.SearchOmpUserPrincipalSuggestionsAsync(term, 12, ct);
            return new JsonResult(suggestions);
        }

        if (string.Equals(principalType, "ADUser", StringComparison.OrdinalIgnoreCase))
        {
            var suggestions = await _repo.SearchAdUserPrincipalSuggestionsAsync(term, 12, ct);
            return new JsonResult(suggestions);
        }

        if (string.Equals(principalType, "ADGroup", StringComparison.OrdinalIgnoreCase))
        {
            var suggestions = await _repo.SearchAdGroupPrincipalSuggestionsAsync(term, 12, ct);
            return new JsonResult(suggestions);
        }

        return new JsonResult(Array.Empty<PrincipalSuggestion>());
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
            return RedirectToSecurityRoles();
        }

        try
        {
            await _repo.DeleteRoleAsync(Input.RoleId, ct);
            StatusMessage = T("Role deleted.");
            return RedirectToSecurityRoles();
        }
        catch (SqlException ex)
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The role could not be deleted.")));

            return Page();
        }
    }

    private async Task ReloadExistingRoleStateAsync(CancellationToken ct)
    {
        if (IsCreate)
        {
            SetOriginalValues(null, null);
            return;
        }

        await LoadOriginalValuesAsync(ct);
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
                nameof(Input.Name), T("Use letters, digits, dash, underscore or dot. Keep the role name stable."));
        }
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<NormalizedPrincipalResult> NormalizePrincipalAsync(
        string principalType,
        string principal,
        CancellationToken ct)
    {
        if (!IsSupportedPrincipalType(principalType))
        {
            return new NormalizedPrincipalResult(null, "Select a supported principal type.");
        }

        if (string.Equals(principalType, "ADGroup", StringComparison.OrdinalIgnoreCase))
        {
            return new NormalizedPrincipalResult(NormalizeAdGroupPrincipal(principal));
        }

        if (string.Equals(principalType, "ADUser", StringComparison.OrdinalIgnoreCase))
        {
            if (await _repo.IsAdUserPrincipalLinkedToOmpUserAsync(principal, ct))
            {
                return new NormalizedPrincipalResult(
                    null,
                    "This AD account is already linked to an OMP user. Assign the role to the OMP user instead.");
            }

            return new NormalizedPrincipalResult(principal);
        }

        if (!string.Equals(principalType, "OmpUser", StringComparison.OrdinalIgnoreCase))
        {
            return new NormalizedPrincipalResult(principal);
        }

        if (!TryParseOmpUserPrincipal(principal, out var userId))
        {
            return new NormalizedPrincipalResult(null, "Select an OMP user from the suggestions or enter a valid user ID.");
        }

        if (!await _repo.OmpUserExistsAsync(userId, ct))
        {
            return new NormalizedPrincipalResult(null, "Select an OMP user from the suggestions or enter a valid user ID.");
        }

        return new NormalizedPrincipalResult(userId.ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsSupportedPrincipalType(string principalType)
        => string.Equals(principalType, "OmpUser", StringComparison.OrdinalIgnoreCase)
            || string.Equals(principalType, "ADUser", StringComparison.OrdinalIgnoreCase)
            || string.Equals(principalType, "ADGroup", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAdGroupPrincipal(string principal)
    {
        principal = principal.Trim();
        if (principal.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ||
            !OperatingSystem.IsWindows())
        {
            return principal;
        }

        foreach (var candidate in GetAdGroupPrincipalCandidates(principal))
        {
            try
            {
                if (new NTAccount(candidate).Translate(typeof(SecurityIdentifier)) is SecurityIdentifier sid)
                {
                    return sid.Value;
                }
            }
            catch (Exception ex) when (
                ex is IdentityNotMappedException or UnauthorizedAccessException or ArgumentException)
            {
                // Keep the entered value. Domain groups can still match by translated
                // DOMAIN\Group names in the Windows login token when SID lookup is not
                // available from this host.
            }
        }

        return principal;
    }

    private static IEnumerable<string> GetAdGroupPrincipalCandidates(string principal)
    {
        yield return principal;

        if (!principal.Contains('\\') && !string.IsNullOrWhiteSpace(Environment.MachineName))
        {
            yield return Environment.MachineName + "\\" + principal;
        }
    }

    private static bool TryParseOmpUserPrincipal(string value, out int userId)
    {
        value = value.Trim();
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId) &&
            userId > 0)
        {
            return true;
        }

        var match = OmpUserPrincipalLabelPattern.Match(value);
        return match.Success &&
            int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId) &&
            userId > 0;
    }

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "A role with the same name already exists.",
            547 => "Delete or update dependent rows first.",
            _ => fallback
        };

    private async Task LoadOriginalValuesAsync(CancellationToken ct)
    {
        if (Input.RoleId <= 0)
        {
            SetOriginalValues(null, null);
            return;
        }

        var role = await _repo.GetRoleAsync(Input.RoleId, ct);
        SetOriginalValues(role?.Name, role?.Description);
    }

    private void SetOriginalValues(string? name, string? description)
    {
        OriginalName = name ?? string.Empty;
        OriginalDescription = description ?? string.Empty;
    }

    private RedirectResult RedirectToSecurityRoles()
        => Redirect("/admin/security/roles");

    private RedirectResult RedirectToSecurityRole(int roleId)
        => Redirect($"/admin/security/role?roleId={roleId}");

    private IActionResult RedirectAfterSave(int roleId)
    {
        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToSecurityRole(roleId);
    }

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

    public sealed class PrincipalTypeOptionItem
    {
        public string Value { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public bool Disabled { get; set; }
    }

    private sealed record NormalizedPrincipalResult(string? Principal, string? ErrorMessage = null);
}
