using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Pages.Account;

public sealed class SettingsModel : OmpSecurePageModel<PortalResource>
{
    public const string UserTab = "user";
    public const string PortalTab = "portal";

    private readonly PortalUserSettingsService _settings;

    public SettingsModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        PortalUserSettingsService settings)
        : base(options, rbac)
    {
        _settings = settings;
    }

    [BindProperty]
    public UserInputModel UserInput { get; set; } = new();

    [BindProperty]
    public PortalInputModel PortalInput { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public string ActiveTab { get; private set; } = UserTab;

    public bool IsPortalAdmin { get; private set; }

    public bool HasOmpUser { get; private set; }

    public bool CanCreateOmpUser { get; private set; }

    public async Task<IActionResult> OnGet(string? tab, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            if (GetCurrentAdProviderUserKeys().Count == 0)
            {
                return Forbid();
            }

            LoadSelfServiceAccountState();
            UserInput.DisplayName = SuggestedDisplayName();
            return Page();
        }

        var guard = await LoadAsync(userId, tab, loadUserInput: true, loadPortalInput: true, ct);
        return guard ?? Page();
    }

    public async Task<IActionResult> OnPostUser(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var guard = await LoadAsync(userId, UserTab, loadUserInput: false, loadPortalInput: true, ct);
        if (guard is not null)
        {
            return guard;
        }

        UserInput.DisplayName = UserInput.DisplayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(UserInput.DisplayName))
        {
            ModelState.AddModelError(
                $"{nameof(UserInput)}.{nameof(UserInputModel.DisplayName)}",
                T("Display name is required."));
        }

        if (UserInput.DisplayName.Length > PortalUserSettingsService.DisplayNameMaxLength)
        {
            ModelState.AddModelError(
                $"{nameof(UserInput)}.{nameof(UserInputModel.DisplayName)}",
                T("Display name must be 200 characters or fewer."));
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var updated = await _settings.UpdateDisplayNameAsync(userId, UserInput.DisplayName, ct);
        if (!updated)
        {
            return Forbid();
        }

        await RefreshDisplayNameClaimAsync(UserInput.DisplayName);
        StatusMessage = T("Settings saved.");
        return RedirectToSettings(UserTab);
    }

    public async Task<IActionResult> OnPostCreateAccount(CancellationToken ct)
    {
        if (TryGetCurrentUserId(out _))
        {
            return RedirectToSettings(UserTab);
        }

        LoadSelfServiceAccountState();
        var providerUserKeys = GetCurrentAdProviderUserKeys();
        if (providerUserKeys.Count == 0)
        {
            ModelState.AddModelError(string.Empty, T("The current sign-in is not an AD sign-in that can be linked."));
            return Page();
        }

        UserInput.DisplayName = UserInput.DisplayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(UserInput.DisplayName))
        {
            ModelState.AddModelError(
                $"{nameof(UserInput)}.{nameof(UserInputModel.DisplayName)}",
                T("Display name is required."));
        }

        if (UserInput.DisplayName.Length > PortalUserSettingsService.DisplayNameMaxLength)
        {
            ModelState.AddModelError(
                $"{nameof(UserInput)}.{nameof(UserInputModel.DisplayName)}",
                T("Display name must be 200 characters or fewer."));
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _settings.CreateSelfServiceAdAccountAsync(
            UserInput.DisplayName,
            providerUserKeys,
            ct);

        switch (result.Status)
        {
            case CreateSelfServiceAdAccountStatus.Created when result.UserId is int userId:
                await RefreshAccountClaimsAsync(UserInput.DisplayName, userId);
                StatusMessage = T("OMP user account created.");
                return RedirectToSettings(UserTab);

            case CreateSelfServiceAdAccountStatus.ProviderUnavailable:
                ModelState.AddModelError(string.Empty, T("The AD authentication provider is not available."));
                return Page();

            case CreateSelfServiceAdAccountStatus.MissingProviderKeys:
                ModelState.AddModelError(string.Empty, T("The current sign-in is not an AD sign-in that can be linked."));
                return Page();

            case CreateSelfServiceAdAccountStatus.AlreadyLinkedToAnotherUser:
                ModelState.AddModelError(string.Empty, T("This AD account is already linked to an OMP user. Sign out and sign in again."));
                return Page();

            default:
                ModelState.AddModelError(string.Empty, T("The OMP user account could not be created."));
                return Page();
        }
    }

    public async Task<IActionResult> OnPostPortal(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var guard = await LoadAsync(userId, PortalTab, loadUserInput: true, loadPortalInput: false, ct);
        if (guard is not null)
        {
            return guard;
        }

        if (!IsPortalAdmin)
        {
            return Forbid();
        }

        await _settings.UpsertAdminMetricsCollapsedAsync(userId, PortalInput.AdminMetricsCollapsed, ct);
        StatusMessage = T("Settings saved.");
        return RedirectToSettings(PortalTab);
    }

    public bool IsActiveTab(string tab)
        => string.Equals(ActiveTab, tab, StringComparison.OrdinalIgnoreCase);

    private async Task<IActionResult?> LoadAsync(
        int userId,
        string? tab,
        bool loadUserInput,
        bool loadPortalInput,
        CancellationToken ct)
    {
        SetTitles("Settings");
        ActiveTab = NormalizeTab(tab);
        HasOmpUser = true;
        CanCreateOmpUser = false;

        var settings = await _settings.GetAccountSettingsAsync(userId, ct);
        if (settings is null)
        {
            return Forbid();
        }

        if (loadUserInput)
        {
            UserInput.DisplayName = settings.DisplayName;
        }

        if (loadPortalInput)
        {
            PortalInput.AdminMetricsCollapsed = settings.AdminMetricsCollapsed;
        }

        var permissions = await GetUserPermissionsAsync(ct);
        IsPortalAdmin = permissions.Contains(OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = IsPortalAdmin;

        return null;
    }

    private void LoadSelfServiceAccountState()
    {
        SetTitles("Settings");
        ActiveTab = UserTab;
        HasOmpUser = false;
        CanCreateOmpUser = true;
        IsPortalAdmin = false;
        ViewData["IsPortalAdmin"] = false;
    }

    private static string NormalizeTab(string? tab)
        => string.Equals(tab, PortalTab, StringComparison.OrdinalIgnoreCase)
            ? PortalTab
            : UserTab;

    private LocalRedirectResult RedirectToSettings(string tab)
        => LocalRedirect($"/account/settings?tab={Uri.EscapeDataString(tab)}");

    private bool TryGetCurrentUserId(out int userId)
    {
        var userIdClaim = User.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(userIdClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId);
    }

    private async Task RefreshDisplayNameClaimAsync(string displayName)
        => await RefreshAccountClaimsAsync(displayName, userId: null);

    private async Task RefreshAccountClaimsAsync(string displayName, int? userId)
    {
        var sourceIdentity = User.Identities.FirstOrDefault(identity =>
            string.Equals(identity.AuthenticationType, OmpAuthDefaults.AuthenticationScheme, StringComparison.Ordinal));

        if (sourceIdentity is null)
        {
            return;
        }

        var claims = sourceIdentity.Claims
            .Where(claim =>
                claim.Type != ClaimTypes.Name &&
                claim.Type != ActiveRoleCookie.ClaimType &&
                (!userId.HasValue || claim.Type != OmpAuthDefaults.UserIdClaimType))
            .ToList();

        claims.Insert(0, new Claim(ClaimTypes.Name, displayName));
        if (userId is int createdUserId)
        {
            claims.Add(new Claim(
                OmpAuthDefaults.UserIdClaimType,
                createdUserId.ToString(CultureInfo.InvariantCulture)));
        }

        var identity = new ClaimsIdentity(
            claims,
            OmpAuthDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        await HttpContext.SignInAsync(
            OmpAuthDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }

    private IReadOnlyList<string> GetCurrentAdProviderUserKeys()
    {
        var provider = User.FindFirstValue(OmpAuthDefaults.ProviderClaimType);
        if (!string.Equals(provider, "AD", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddProviderKey(keys, User.FindFirstValue(OmpAuthDefaults.ProviderUserKeyClaimType));

        foreach (var claim in User.FindAll(OmpAuthDefaults.PrincipalClaimType))
        {
            if (!TryParsePrincipalClaim(claim.Value, out var principalType, out var principal))
            {
                continue;
            }

            if (!string.Equals(principalType, "ADUser", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(principalType, "User", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsSidLike(principal))
            {
                AddProviderKey(keys, "sid:" + principal);
            }
            else
            {
                AddProviderKey(keys, "name:" + principal);
                AddProviderKey(keys, principal);
            }
        }

        return keys.ToArray();
    }

    private string SuggestedDisplayName()
    {
        var name = User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        return User.FindFirstValue(ClaimTypes.Name)?.Trim() ?? string.Empty;
    }

    private static void AddProviderKey(HashSet<string> keys, string? providerUserKey)
    {
        providerUserKey = providerUserKey?.Trim();
        if (!string.IsNullOrWhiteSpace(providerUserKey))
        {
            keys.Add(providerUserKey);
        }
    }

    private static bool TryParsePrincipalClaim(
        string? value,
        out string principalType,
        out string principal)
    {
        principalType = string.Empty;
        principal = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        principalType = parts[0];
        principal = parts[1];
        return true;
    }

    private static bool IsSidLike(string value)
        => value.StartsWith("S-", StringComparison.OrdinalIgnoreCase);

    public sealed class UserInputModel
    {
        [Required]
        [StringLength(PortalUserSettingsService.DisplayNameMaxLength)]
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class PortalInputModel
    {
        public bool AdminMetricsCollapsed { get; set; }
    }
}
