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

    public async Task<IActionResult> OnGet(string? tab, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
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
    {
        var sourceIdentity = User.Identities.FirstOrDefault(identity =>
            string.Equals(identity.AuthenticationType, OmpAuthDefaults.AuthenticationScheme, StringComparison.Ordinal));

        if (sourceIdentity is null)
        {
            return;
        }

        var claims = sourceIdentity.Claims
            .Where(claim => claim.Type != ClaimTypes.Name && claim.Type != ActiveRoleCookie.ClaimType)
            .ToList();

        claims.Insert(0, new Claim(ClaimTypes.Name, displayName));

        var identity = new ClaimsIdentity(
            claims,
            OmpAuthDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        await HttpContext.SignInAsync(
            OmpAuthDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }

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
