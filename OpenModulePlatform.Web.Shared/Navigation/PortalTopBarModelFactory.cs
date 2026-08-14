using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Notifications;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Builds a basic shared portal shortcut model from configuration.
/// </summary>
/// <remarks>
/// The runtime top bar shown in modules is normally created by <see cref="PortalTopBarService"/>
/// so it can populate module links using the same access logic as the Portal start page.
/// This factory remains as a lightweight configuration-only fallback.
/// </remarks>
public static class PortalTopBarModelFactory
{
    public static PortalTopBarModel Create(WebAppOptions options)
    {
        var topBarOptions = options.PortalTopBar ?? new PortalTopBarOptions();
        var notificationUpdateOptions = PortalTopBarNotificationUpdateOptions.FromWebAppOptions(options.TopBarPolling);

        if (!topBarOptions.Enabled)
        {
            return PortalTopBarModel.Hidden;
        }

        var cultureSelection = new CultureSelectionService().ResolveFromCurrentCulture(options);
        var portalLink = new PortalTopBarLink(OmpBranding.Default.PlatformName, CombinePortalHref(topBarOptions.PortalBaseUrl, "/"));

        return new PortalTopBarModel
        {
            IsVisible = true,
            Links = [portalLink],
            PortalLink = portalLink,
            ModuleLinks = Array.Empty<PortalTopBarLink>(),
            NavigationGroups = Array.Empty<PortalTopBarNavigationGroup>(),
            FavoriteEntries = Array.Empty<PortalTopBarNavigationEntry>(),
            LanguageOptions = (options.SupportedCultures ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(c => c.Trim())
                .Select(c => new PortalTopBarCultureOption(
                    c,
                    DescribeCulture(c),
                    string.Equals(c, cultureSelection.EffectiveCulture, StringComparison.OrdinalIgnoreCase)))
                .ToArray(),
            PreferredCulture = cultureSelection.PreferredCulture,
            EffectiveCulture = cultureSelection.EffectiveCulture,
            PreferredCultureDisplayText = cultureSelection.PreferredCultureDisplayText,
            EffectiveCultureDisplayText = cultureSelection.EffectiveCultureDisplayText,
            IsCultureFallback = cultureSelection.IsFallback,
            AvailableRoles = Array.Empty<OpenModulePlatform.Web.Shared.Services.UserRoleOption>(),
            OverflowToggleTextKey = "More",
            CollapsedToggleTextKey = "Modules",
            AllModulesToggleTextKey = "All modules",
            FavoritesToggleTextKey = "Favorites",
            NavigationFilterPlaceholderTextKey = "Search modules",
            NoFavoritesTextKey = "No favorites",
            AddFavoriteTextKey = "Add favorite",
            RemoveFavoriteTextKey = "Remove favorite",
            LanguageToggleTextKey = "Language",
            LogoutUrl = OmpAuthDefaults.LogoutPath,
            SettingsUrl = CombinePortalHref(topBarOptions.PortalBaseUrl, PortalTopBarModel.DefaultSettingsPath),
            ShortcutsEnabled = options.TopbarShortcuts?.Enabled == true,
            AllModulesShortcut = options.TopbarShortcuts?.AllModules ?? "m",
            FavoritesShortcut = options.TopbarShortcuts?.Favorites ?? "f",
            DropdownsOpenOnHover = true,
            SessionStatusCheckEnabled = options.SessionStatusCheck?.Enabled != false,
            SessionStatusUrl = PortalTopBarModel.DefaultSessionStatusPath,
            SessionLoginUrl = OmpAuthDefaults.LoginPath,
            SessionStatusVisibleIntervalSeconds = PositiveOrDefault(options.SessionStatusCheck?.VisibleIntervalSeconds, 60),
            SessionStatusHiddenIntervalSeconds = PositiveOrDefault(options.SessionStatusCheck?.HiddenIntervalSeconds, 180),
            NotificationUpdateMode = notificationUpdateOptions.Mode,
            NotificationPushUrl = TopBarNotificationHub.Path,
            NotificationPollIntervalSeconds = notificationUpdateOptions.PollIntervalSeconds,
            TopBarPollingEnabled = notificationUpdateOptions.UsesPolling,
            TopBarSummaryUrl = PortalTopBarModel.DefaultTopBarSummaryPath,
            TopBarPollingVisibleIntervalSeconds = notificationUpdateOptions.PollIntervalSeconds,
            TopBarPollingHiddenIntervalSeconds = options.TopBarPolling?.HiddenIntervalSeconds ?? notificationUpdateOptions.PollIntervalSeconds,
            TopBarPollingPushReconnectBaseMs = PositiveOrDefault(options.TopBarPolling?.PushReconnectBaseMs, 2000),
            TopBarPollingPushReconnectMaxMs = PositiveOrDefault(options.TopBarPolling?.PushReconnectMaxMs, 60000),
            ToastPollingVisibleIntervalSeconds = options.ToastPolling?.VisibleIntervalSeconds ?? 60,
            ToastPollingHiddenIntervalSeconds = options.ToastPolling?.HiddenIntervalSeconds ?? 180
        };
    }

    /// <summary>
    /// Delegates to the shared helper so the fallback model builds hrefs the same way the live one
    /// does.
    /// </summary>
    /// <remarks>
    /// R8-P1-6 hardened OmpUrlPathHelper and left this second copy behind -- a fix applied to one
    /// of a pair, which is the exact defect class this round exists to sweep for. Both copies used
    /// Uri.IsWellFormedUriString as the only test, and that is true for "javascript:alert(1)".
    /// </remarks>
    internal static string CombinePortalHref(string portalBaseUrl, string href)
        => OmpUrlPathHelper.CombinePortalHref(portalBaseUrl, href);

    private static int PositiveOrDefault(int? value, int fallback)
        => value is > 0 ? value.Value : fallback;

    /// <summary>
    /// The name to show for a culture in the language picker.
    /// </summary>
    /// <remarks>
    /// This was a hardcoded two-way map: anything starting with "sv" became "Swedish",
    /// anything starting with "en" became "English", and every other culture fell through to
    /// its raw code -- so adding a third language to SupportedCultures put "fr" or "de" in
    /// the menu. Reported by GitHub code quality.
    ///
    /// The BCL already knows every culture's name, so it is asked instead. NativeName rather
    /// than DisplayName on purpose: a language picker is read by someone who wants that
    /// language, and "français" is what a French reader is looking for, not "French". The
    /// name is title-cased using the culture's own casing rules, because languages differ on
    /// whether they capitalise their own name and a menu of mixed casing looks like a bug.
    ///
    /// An unknown or malformed code falls back to the code itself, which is the previous
    /// behaviour for that case and better than throwing inside a layout component.
    /// </remarks>
    private static string DescribeCulture(string culture)
    {
        try
        {
            var info = CultureInfo.GetCultureInfo(culture);
            var nativeName = info.NativeName;
            return string.IsNullOrWhiteSpace(nativeName)
                ? culture
                : info.TextInfo.ToTitleCase(nativeName);
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }
}
