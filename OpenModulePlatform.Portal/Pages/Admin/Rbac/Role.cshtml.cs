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

    public IReadOnlyList<PrincipalSuggestion> OmpUserOptions { get; private set; } = [];

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

    public async Task<IActionResult> OnPostAddPrincipal(
        string principalType,
        string? principal,
        int[]? selectedOmpUserIds,
        CancellationToken ct)
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

        var candidates = BuildPrincipalCandidates(principalType, principal, selectedOmpUserIds);
        if (candidates.Count == 0)
        {
            await LoadDetailsAsync(ct);
            SetTitles("Edit role");
            ModelState.AddModelError(
                string.Empty, T("Select at least one principal to add."));
            NewPrincipal = principal;
            return Page();
        }

        var result = await AddPrincipalCandidatesAsync(candidates, ct);
        StatusMessage = BuildPrincipalBatchStatusMessage(result);
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

        OmpUserOptions = await _repo.GetAvailableOmpUserPrincipalOptionsAsync(Input.RoleId, ct);
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
            return new NormalizedPrincipalResult(null, null, "Select a supported principal type.");
        }

        if (string.Equals(principalType, "ADGroup", StringComparison.OrdinalIgnoreCase))
        {
            return new NormalizedPrincipalResult("ADGroup", NormalizeAdGroupPrincipal(principal));
        }

        if (string.Equals(principalType, "ADUser", StringComparison.OrdinalIgnoreCase))
        {
            var linkedOmpUserId = await _repo.GetLinkedActiveOmpUserIdForAdUserPrincipalAsync(principal, ct);
            if (linkedOmpUserId is int linkedUserId)
            {
                return new NormalizedPrincipalResult(
                    "OmpUser",
                    linkedUserId.ToString(CultureInfo.InvariantCulture));
            }

            return new NormalizedPrincipalResult("ADUser", principal);
        }

        if (!string.Equals(principalType, "OmpUser", StringComparison.OrdinalIgnoreCase))
        {
            return new NormalizedPrincipalResult(principalType, principal);
        }

        if (!TryParseOmpUserPrincipal(principal, out var userId))
        {
            return new NormalizedPrincipalResult(
                null,
                null,
                "Select an OMP user from the suggestions or enter a valid user ID.");
        }

        if (!await _repo.OmpUserExistsAsync(userId, ct))
        {
            return new NormalizedPrincipalResult(
                null,
                null,
                "Select an OMP user from the suggestions or enter a valid user ID.");
        }

        return new NormalizedPrincipalResult("OmpUser", userId.ToString(CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<PrincipalCandidate> BuildPrincipalCandidates(
        string principalType,
        string? principal,
        int[]? selectedOmpUserIds)
    {
        principalType = Clean(principalType) ?? string.Empty;
        var candidates = new List<PrincipalCandidate>();

        if (string.Equals(principalType, "OmpUser", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var userId in selectedOmpUserIds ?? [])
            {
                if (userId > 0)
                {
                    candidates.Add(new PrincipalCandidate("OmpUser", userId.ToString(CultureInfo.InvariantCulture)));
                }
            }

            return candidates;
        }

        foreach (var row in SplitPrincipalLines(principal))
        {
            candidates.Add(new PrincipalCandidate(principalType, row));
        }

        return candidates;
    }

    private async Task<PrincipalBatchResult> AddPrincipalCandidatesAsync(
        IReadOnlyList<PrincipalCandidate> candidates,
        CancellationToken ct)
    {
        var result = new PrincipalBatchResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            NormalizedPrincipalResult normalized;
            try
            {
                normalized = await NormalizePrincipalAsync(candidate.PrincipalType, candidate.Principal, ct);
            }
            catch (SqlException ex)
            {
                result.Fail(candidate.Principal, ToFriendlySqlMessage(ex, "The principal could not be resolved."));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(normalized.ErrorMessage)
                || string.IsNullOrWhiteSpace(normalized.PrincipalType)
                || string.IsNullOrWhiteSpace(normalized.Principal))
            {
                result.Fail(candidate.Principal, normalized.ErrorMessage ?? "The principal could not be resolved.");
                continue;
            }

            if (normalized.Principal.Length > 256)
            {
                result.Fail(candidate.Principal, "Principal is too long.");
                continue;
            }

            var normalizedKey = $"{normalized.PrincipalType}\u001f{normalized.Principal}";
            if (!seen.Add(normalizedKey))
            {
                result.Skipped++;
                continue;
            }

            try
            {
                var added = await _repo.AddPrincipalToRoleAsync(
                    Input.RoleId,
                    normalized.PrincipalType,
                    normalized.Principal,
                    ct);

                if (added)
                {
                    result.Added++;
                }
                else
                {
                    result.Skipped++;
                }
            }
            catch (SqlException ex)
            {
                if (ex.Number is 2601 or 2627)
                {
                    result.Skipped++;
                    continue;
                }

                result.Fail(candidate.Principal, ToFriendlySqlMessage(ex, "The principal could not be added."));
            }
        }

        return result;
    }

    private string BuildPrincipalBatchStatusMessage(PrincipalBatchResult result)
    {
        var parts = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, T("Added {0} principal(s)."), result.Added),
            string.Format(CultureInfo.CurrentCulture, T("Skipped {0} duplicate or already assigned principal(s)."), result.Skipped)
        };

        if (result.Failures.Count > 0)
        {
            var failurePreview = string.Join("; ", result.Failures.Take(5));
            if (result.Failures.Count > 5)
            {
                failurePreview += "; ...";
            }

            parts.Add(string.Format(CultureInfo.CurrentCulture, T("Failed {0}: {1}"), result.Failures.Count, failurePreview));
        }

        return string.Join(" ", parts);
    }

    private static IEnumerable<string> SplitPrincipalLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var line in value.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                yield return trimmed;
            }
        }
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

    private sealed record PrincipalCandidate(string PrincipalType, string Principal);

    private sealed record NormalizedPrincipalResult(string? PrincipalType, string? Principal, string? ErrorMessage = null);

    private sealed class PrincipalBatchResult
    {
        public int Added { get; set; }

        public int Skipped { get; set; }

        public List<string> Failures { get; } = [];

        public void Fail(string principal, string message)
            => Failures.Add($"{principal}: {message}");
    }
}
