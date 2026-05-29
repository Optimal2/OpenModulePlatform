using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// View model for the compact portal shortcut bar shown above module navigation.
/// </summary>
public sealed class PortalTopBarModel
{
    public const string DefaultSettingsPath = "/account/settings";
    public const string DefaultSessionStatusPath = "/auth/session-status";

    public static PortalTopBarModel Hidden { get; } = new()
    {
        IsVisible = false,
        Links = Array.Empty<PortalTopBarLink>(),
        ModuleLinks = Array.Empty<PortalTopBarLink>(),
        NavigationGroups = Array.Empty<PortalTopBarNavigationGroup>(),
        FavoriteEntries = Array.Empty<PortalTopBarNavigationEntry>(),
        Notifications = Array.Empty<PortalTopBarNotification>(),
        PortalAdminLinks = Array.Empty<PortalTopBarLink>(),
        PortalAdminSections = Array.Empty<PortalAdminMenuSection>(),
        LanguageOptions = Array.Empty<PortalTopBarCultureOption>(),
        OverflowToggleTextKey = "More",
        CollapsedToggleTextKey = "Modules",
        AllModulesToggleTextKey = "All modules",
        FavoritesToggleTextKey = "Favorites",
        NavigationFilterPlaceholderTextKey = "Search modules",
        NoFavoritesTextKey = "No favorites",
        NotificationsToggleTextKey = "Notifications",
        NoNotificationsTextKey = "No new notifications",
        MarkAllNotificationsReadTextKey = "Mark all as read",
        AddFavoriteTextKey = "Add favorite",
        RemoveFavoriteTextKey = "Remove favorite",
        PortalAdminToggleTextKey = "Admin",
        LanguageToggleTextKey = "Language",
        PreferredCulture = "en-US",
        EffectiveCulture = "en-US",
        PreferredCultureDisplayText = "English",
        EffectiveCultureDisplayText = "English",
        AvailableRoles = Array.Empty<OpenModulePlatform.Web.Shared.Services.UserRoleOption>(),
        LogoutUrl = OmpAuthDefaults.LogoutPath,
        SettingsUrl = DefaultSettingsPath,
        DropdownsOpenOnHover = true,
        SessionStatusCheckEnabled = true,
        SessionStatusUrl = DefaultSessionStatusPath,
        SessionLoginUrl = OmpAuthDefaults.LoginPath,
        SessionStatusVisibleIntervalSeconds = 60,
        SessionStatusHiddenIntervalSeconds = 180
    };

    public bool IsVisible { get; init; }

    /// <summary>
    /// Combined link collection kept for backwards compatibility.
    /// </summary>
    public IReadOnlyList<PortalTopBarLink> Links { get; init; } = Array.Empty<PortalTopBarLink>();

    public PortalTopBarLink? PortalLink { get; init; }

    public IReadOnlyList<PortalTopBarLink> ModuleLinks { get; init; } = Array.Empty<PortalTopBarLink>();

    public IReadOnlyList<PortalTopBarNavigationGroup> NavigationGroups { get; init; } = Array.Empty<PortalTopBarNavigationGroup>();

    public IReadOnlyList<PortalTopBarNavigationEntry> FavoriteEntries { get; init; } = Array.Empty<PortalTopBarNavigationEntry>();

    public bool CanUsePersistentFavorites { get; init; }

    public string FavoriteToggleUrl { get; init; } = "/navigation/favorites/toggle";

    public IReadOnlyList<PortalTopBarNotification> Notifications { get; init; } = Array.Empty<PortalTopBarNotification>();

    public bool CanUseNotifications { get; init; }

    public int UnreadNotificationCount { get; init; }

    public string NotificationMarkReadUrl { get; init; } = "/notifications/mark-read";

    public string NotificationMarkAllReadUrl { get; init; } = "/notifications/mark-all-read";

    public bool IsPortalAdmin { get; init; }

    public IReadOnlyList<PortalTopBarLink> PortalAdminLinks { get; init; } = Array.Empty<PortalTopBarLink>();

    public IReadOnlyList<PortalAdminMenuSection> PortalAdminSections { get; init; } = Array.Empty<PortalAdminMenuSection>();

    public IReadOnlyList<PortalTopBarCultureOption> LanguageOptions { get; init; } = Array.Empty<PortalTopBarCultureOption>();

    public string PreferredCulture { get; init; } = "en-US";

    public string EffectiveCulture { get; init; } = "en-US";

    public string PreferredCultureDisplayText { get; init; } = "English";

    public string EffectiveCultureDisplayText { get; init; } = "English";

    public bool IsCultureFallback { get; init; }

    public string? CurrentUserName { get; init; }

    public IReadOnlyList<OpenModulePlatform.Web.Shared.Services.UserRoleOption> AvailableRoles { get; init; } = Array.Empty<OpenModulePlatform.Web.Shared.Services.UserRoleOption>();

    public int? ActiveRoleId { get; init; }

    public string? ActiveRoleName { get; init; }

    public string OverflowToggleTextKey { get; init; } = "More";

    public string CollapsedToggleTextKey { get; init; } = "Modules";

    public string AllModulesToggleTextKey { get; init; } = "All modules";

    public string FavoritesToggleTextKey { get; init; } = "Favorites";

    public string NavigationFilterPlaceholderTextKey { get; init; } = "Search modules";

    public string NoFavoritesTextKey { get; init; } = "No favorites";

    public string NotificationsToggleTextKey { get; init; } = "Notifications";

    public string NoNotificationsTextKey { get; init; } = "No new notifications";

    public string MarkAllNotificationsReadTextKey { get; init; } = "Mark all as read";

    public string AddFavoriteTextKey { get; init; } = "Add favorite";

    public string RemoveFavoriteTextKey { get; init; } = "Remove favorite";

    public string PortalAdminToggleTextKey { get; init; } = "Admin";

    public string LanguageToggleTextKey { get; init; } = "Language";

    public string LogoutUrl { get; init; } = OmpAuthDefaults.LogoutPath;

    public string SettingsUrl { get; init; } = DefaultSettingsPath;

    public bool ShortcutsEnabled { get; init; }

    public string AllModulesShortcut { get; init; } = "m";

    public string FavoritesShortcut { get; init; } = "f";

    public bool DropdownsOpenOnHover { get; init; } = true;

    public bool SessionStatusCheckEnabled { get; init; } = true;

    public string SessionStatusUrl { get; init; } = DefaultSessionStatusPath;

    public string SessionLoginUrl { get; init; } = OmpAuthDefaults.LoginPath;

    public int SessionStatusVisibleIntervalSeconds { get; init; } = 60;

    public int SessionStatusHiddenIntervalSeconds { get; init; } = 180;
}
