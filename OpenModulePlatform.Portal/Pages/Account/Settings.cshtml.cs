using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
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
    public const string AdminTab = "admin";

    private enum ExternalUserProvisioningMode
    {
        Manual,
        AutoIfRole,
        AutoIfAuthenticated
    }

    private readonly PortalUserSettingsService _settings;
    private readonly PortalDashboardService _dashboard;
    private readonly RbacService _rbac;
    private readonly UserProfileImageService _profileImages;
    private readonly OmpConfigurationService _configuration;

    public SettingsModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        PortalUserSettingsService settings,
        PortalDashboardService dashboard,
        UserProfileImageService profileImages,
        OmpConfigurationService configuration)
        : base(options, rbac)
    {
        _settings = settings;
        _dashboard = dashboard;
        _rbac = rbac;
        _profileImages = profileImages;
        _configuration = configuration;
    }

    [BindProperty]
    public UserInputModel UserInput { get; set; } = new();

    [BindProperty]
    public PortalInputModel PortalInput { get; set; } = new();

    [BindProperty]
    public IFormFile? ProfileImageUpload { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public string ActiveTab { get; private set; } = UserTab;

    public bool IsPortalAdmin { get; private set; }

    public bool HasOmpUser { get; private set; }

    public bool CanCreateOmpUser { get; private set; }

    public bool SelfRegistrationEnabled { get; private set; } = true;

    public string? OmpUserCreationUnavailableMessage { get; private set; }

    public string? ProfileImageUrl { get; private set; }

    public string? ProfileImageFileName { get; private set; }

    public string ProfileImageInitials { get; private set; } = "?";

    public bool NotificationToastsGloballyEnabled { get; private set; } = true;

    public async Task<IActionResult> OnGet(string? tab, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            if (GetCurrentAdProviderUserKeys().Count == 0)
            {
                return Forbid();
            }

            await LoadSelfServiceAccountStateAsync(ct);
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

    public async Task<IActionResult> OnPostProfileImage(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var guard = await LoadAsync(userId, UserTab, loadUserInput: true, loadPortalInput: true, ct);
        if (guard is not null)
        {
            return guard;
        }

        var result = await _profileImages.SaveProfileImageAsync(userId, ProfileImageUpload, ct);
        switch (result)
        {
            case ProfileImageSaveResult.Saved:
                StatusMessage = T("Profile image saved.");
                return RedirectToSettings(UserTab);

            case ProfileImageSaveResult.TooLarge:
                ModelState.AddModelError(nameof(ProfileImageUpload), T("Image is too large."));
                return Page();

            case ProfileImageSaveResult.InvalidType:
                ModelState.AddModelError(nameof(ProfileImageUpload), T("Invalid image type."));
                return Page();

            case ProfileImageSaveResult.MissingFile:
                ModelState.AddModelError(nameof(ProfileImageUpload), T("Select a profile image file."));
                return Page();

            case ProfileImageSaveResult.StorageUnavailable:
                ModelState.AddModelError(nameof(ProfileImageUpload), T("Profile image storage is unavailable. Contact an administrator."));
                return Page();

            case ProfileImageSaveResult.SchemaUnavailable:
                ModelState.AddModelError(nameof(ProfileImageUpload), T("Profile image storage is not installed. Run core module SQL repair and try again."));
                return Page();

            default:
                return Forbid();
        }
    }

    public async Task<IActionResult> OnPostRemoveProfileImage(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var removed = await _profileImages.RemoveProfileImageAsync(userId, ct);
        if (!removed)
        {
            return Forbid();
        }

        StatusMessage = T("Profile image removed.");
        return RedirectToSettings(UserTab);
    }

    public async Task<IActionResult> OnPostCreateAccount(CancellationToken ct)
    {
        if (TryGetCurrentUserId(out _))
        {
            return RedirectToSettings(UserTab);
        }

        await LoadSelfServiceAccountStateAsync(ct);
        var providerUserKeys = GetCurrentAdProviderUserKeys();
        if (providerUserKeys.Count == 0)
        {
            ModelState.AddModelError(string.Empty, T("The current sign-in is not an AD sign-in that can be linked."));
            return Page();
        }

        if (!SelfRegistrationEnabled)
        {
            ModelState.AddModelError(string.Empty, await TWithBrandingAsync("OMP account creation is disabled.", ct));
            return Page();
        }

        if (!CanCreateOmpUser)
        {
            ModelState.AddModelError(
                string.Empty,
                OmpUserCreationUnavailableMessage
                ?? await TWithBrandingAsync("This sign-in is not eligible to create an OMP account automatically. Contact an administrator if you need an OMP account.", ct));
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
                StatusMessage = await TWithBrandingAsync("OMP user account created.", ct);
                return RedirectToSettings(UserTab);

            case CreateSelfServiceAdAccountStatus.ProviderUnavailable:
                ModelState.AddModelError(string.Empty, T("The AD authentication provider is not available."));
                return Page();

            case CreateSelfServiceAdAccountStatus.MissingProviderKeys:
                ModelState.AddModelError(string.Empty, T("The current sign-in is not an AD sign-in that can be linked."));
                return Page();

            case CreateSelfServiceAdAccountStatus.AlreadyLinkedToAnotherUser:
                ModelState.AddModelError(string.Empty, await TWithBrandingAsync("This AD account is already linked to an OMP user. Sign out and sign in again.", ct));
                return Page();

            default:
                ModelState.AddModelError(string.Empty, await TWithBrandingAsync("The OMP user account could not be created.", ct));
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

        await _settings.UpsertTopbarDropdownsOpenOnHoverAsync(userId, PortalInput.TopbarDropdownsOpenOnHover, ct);
        await _settings.UpsertShowPortalNavbarAsync(userId, PortalInput.ShowPortalNavbar, ct);
        if (NotificationToastsGloballyEnabled)
        {
            await _settings.UpsertNotificationToastsMutedAsync(userId, PortalInput.NotificationToastsMuted, ct);
            await _settings.UpsertNotificationSoundsEnabledAsync(userId, PortalInput.NotificationSoundsEnabled, ct);
        }

        StatusMessage = T("Settings saved.");
        return RedirectToSettings(PortalTab);
    }

    public async Task<IActionResult> OnPostResetDashboard(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _dashboard.ResetDashboardToDefaultAsync(userId, ct);
        StatusMessage = T("Dashboard layout reset to default.");
        return RedirectToSettings(PortalTab);
    }

    public async Task<IActionResult> OnPostClearDashboard(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _dashboard.ClearDashboardAsync(userId, ct);
        StatusMessage = T("Dashboard cleared.");
        return RedirectToSettings(PortalTab);
    }

    public async Task<IActionResult> OnPostSaveDefaultDashboard(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var guard = await LoadAsync(userId, AdminTab, loadUserInput: true, loadPortalInput: true, ct);
        if (guard is not null)
        {
            return guard;
        }

        if (!IsPortalAdmin)
        {
            return Forbid();
        }

        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        var count = await _dashboard.SaveCurrentLayoutAsDefaultAsync(
            userId,
            roleContext.EffectiveRoleIds.ToHashSet(),
            roleContext.EffectivePermissions,
            ct);
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            T("Default dashboard layout saved. Widgets: {0}."),
            count);
        return RedirectToSettings(AdminTab);
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
        SelfRegistrationEnabled = true;
        NotificationToastsGloballyEnabled = await GetNotificationToastsGloballyEnabledAsync(ct);

        var settings = await _settings.GetAccountSettingsAsync(userId, ct);
        if (settings is null)
        {
            return Forbid();
        }

        if (loadUserInput)
        {
            UserInput.DisplayName = settings.DisplayName;
        }

        ProfileImageFileName = settings.ProfileImageFileName;
        ProfileImageUrl = OmpAvatarHelper.BuildUserAvatarPath(userId, settings.ProfileImageStorageKey);
        ProfileImageInitials = OmpAvatarHelper.GetInitials(settings.DisplayName);

        if (loadPortalInput)
        {
            PortalInput.TopbarDropdownsOpenOnHover = settings.TopbarDropdownsOpenOnHover;
            PortalInput.ShowPortalNavbar = settings.ShowPortalNavbar;
            PortalInput.NotificationToastsMuted = settings.NotificationToastsMuted;
            PortalInput.NotificationSoundsEnabled = settings.NotificationSoundsEnabled;
        }

        var permissions = await GetUserPermissionsAsync(ct);
        IsPortalAdmin = permissions.Contains(OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = IsPortalAdmin;
        if (!IsPortalAdmin && string.Equals(ActiveTab, AdminTab, StringComparison.OrdinalIgnoreCase))
        {
            ActiveTab = PortalTab;
        }

        return null;
    }

    private async Task LoadSelfServiceAccountStateAsync(CancellationToken ct)
    {
        var selfRegistrationEnabled = await GetSelfRegistrationEnabledAsync(ct);
        var canCreateOmpUser = false;
        string? unavailableMessage = null;

        if (!selfRegistrationEnabled)
        {
            unavailableMessage = await TWithBrandingAsync("OMP account creation is disabled.", ct);
        }
        else
        {
            var provisioningMode = await GetExternalUserProvisioningModeAsync(ct);
            canCreateOmpUser = provisioningMode switch
            {
                ExternalUserProvisioningMode.Manual => true,
                ExternalUserProvisioningMode.AutoIfRole => (await _rbac.GetUserRoleContextAsync(User, ct)).AvailableRoles.Count > 0,
                ExternalUserProvisioningMode.AutoIfAuthenticated => await IsAuthenticatedUsersProvisioningTriggerAllowedAsync(ct),
                _ => false
            };

            if (!canCreateOmpUser)
            {
                unavailableMessage = await TWithBrandingAsync(
                    "This sign-in is not eligible to create an OMP account automatically. Contact an administrator if you need an OMP account.",
                    ct);
            }
        }

        SetTitles("Settings");
        ActiveTab = UserTab;
        HasOmpUser = false;
        SelfRegistrationEnabled = selfRegistrationEnabled;
        CanCreateOmpUser = selfRegistrationEnabled && canCreateOmpUser;
        OmpUserCreationUnavailableMessage = unavailableMessage;
        IsPortalAdmin = false;
        ProfileImageUrl = null;
        ProfileImageFileName = null;
        ProfileImageInitials = OmpAvatarHelper.GetInitials(SuggestedDisplayName());
        ViewData["IsPortalAdmin"] = false;
    }

    private async Task<bool> GetNotificationToastsGloballyEnabledAsync(CancellationToken ct)
    {
        var value = await _configuration.GetGlobalStringAsync(
            PortalUserSettingsService.NotificationToastsConfigCategory,
            PortalUserSettingsService.NotificationToastsEnabledConfigSetting,
            ct);
        return PortalUserSettingsService.ParseNotificationToastsEnabled(value);
    }

    private async Task<bool> GetSelfRegistrationEnabledAsync(CancellationToken ct)
    {
        var value = await _configuration.GetGlobalStringAsync(
            OmpAuthDefaults.ConfigurationCategory,
            OmpAuthDefaults.SelfRegistrationEnabledSetting,
            ct);
        return OmpAuthDefaults.ParseEnabledConfigValue(value, defaultValue: true);
    }

    private async Task<ExternalUserProvisioningMode> GetExternalUserProvisioningModeAsync(CancellationToken ct)
    {
        var value = await _configuration.GetGlobalStringAsync(
            OmpAuthDefaults.ConfigurationCategory,
            OmpAuthDefaults.ExternalUserProvisioningModeSetting,
            ct);

        var normalized = value?.Trim();
        if (string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeAutoIfRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeIfRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeAutomaticForAuthorizedUsers, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalUserProvisioningMode.AutoIfRole;
        }

        if (string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeAutoIfAuthenticated, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, OmpAuthDefaults.ExternalUserProvisioningModeIfAuthenticated, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalUserProvisioningMode.AutoIfAuthenticated;
        }

        return ExternalUserProvisioningMode.Manual;
    }

    private async Task<bool> IsAuthenticatedUsersProvisioningTriggerAllowedAsync(CancellationToken ct)
    {
        var allowedDomainsValue = await _configuration.GetGlobalStringAsync(
            OmpRbacDefaults.ConfigurationCategory,
            OmpRbacDefaults.AuthenticatedUsersWindowsDomainsSetting,
            ct);

        var allowedDomains = SplitDomainList(allowedDomainsValue);
        if (allowedDomains.Count == 0 || allowedDomains.Contains("*"))
        {
            return true;
        }

        return GetWindowsAccountDomains().Any(allowedDomains.Contains);
    }

    private static HashSet<string> SplitDomainList(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = (value ?? string.Empty).Split(
            [',', ';', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts.Where(part => !string.IsNullOrWhiteSpace(part)))
        {
            result.Add(part);
        }

        return result;
    }

    private static string NormalizeTab(string? tab)
        => string.Equals(tab, AdminTab, StringComparison.OrdinalIgnoreCase)
            ? AdminTab
            : string.Equals(tab, PortalTab, StringComparison.OrdinalIgnoreCase)
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

        var providerUserKey = GetCurrentPlainAdProviderUserKey();
        return string.IsNullOrWhiteSpace(providerUserKey)
            ? []
            : [providerUserKey];
    }

    private string? GetCurrentPlainAdProviderUserKey()
    {
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

            var normalized = NormalizePlainAdProviderUserKey(principal);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return NormalizePlainAdProviderUserKey(User.Identity?.Name)
            ?? NormalizePlainAdProviderUserKey(User.FindFirstValue(OmpAuthDefaults.ProviderUserKeyClaimType))
            ?? NormalizePlainAdProviderUserKey(User.FindFirstValue(ClaimTypes.Name));
    }

    private IEnumerable<string> GetWindowsAccountDomains()
    {
        foreach (var claim in User.FindAll(OmpAuthDefaults.PrincipalClaimType))
        {
            if (!TryParsePrincipalClaim(claim.Value, out var principalType, out var principal))
            {
                continue;
            }

            if (!string.Equals(principalType, "User", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(principalType, "ADUser", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slashIndex = principal.IndexOf('\\', StringComparison.Ordinal);
            if (slashIndex > 0)
            {
                yield return principal[..slashIndex];
            }
        }
    }

    private string SuggestedDisplayName()
    {
        var name = GetCurrentPlainAdProviderUserKey() ?? User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return ToAccountNameOnly(name) ?? string.Empty;
        }

        return ToAccountNameOnly(User.FindFirstValue(ClaimTypes.Name)) ?? string.Empty;
    }

    private static string? NormalizePlainAdProviderUserKey(string? providerUserKey)
    {
        providerUserKey = providerUserKey?.Trim();
        if (string.IsNullOrWhiteSpace(providerUserKey))
        {
            return null;
        }

        if (providerUserKey.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
        {
            providerUserKey = providerUserKey["name:".Length..].Trim();
        }

        if (providerUserKey.StartsWith("sid:", StringComparison.OrdinalIgnoreCase) ||
            IsSidLike(providerUserKey))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(providerUserKey)
            ? null
            : providerUserKey;
    }

    private static string? ToAccountNameOnly(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separatorIndex = value.LastIndexOf('\\');
        if (separatorIndex >= 0 && separatorIndex < value.Length - 1)
        {
            return value[(separatorIndex + 1)..].Trim();
        }

        return value;
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
        public bool TopbarDropdownsOpenOnHover { get; set; } = true;

        public bool ShowPortalNavbar { get; set; } = true;

        public bool NotificationToastsMuted { get; set; }

        public bool NotificationSoundsEnabled { get; set; } = true;
    }
}
