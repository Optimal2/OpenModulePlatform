using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Builds the shared portal top bar using the same access logic as the Portal start page.
/// </summary>
public sealed class PortalTopBarService
{
    private const string PortalAdminPermission = "OMP.Portal.Admin";
    private const string ContentManagePermission = "ContentWebAppModule.Manage";
    private const string PortalSettingCategory = "Portal";
    private const string TopbarDropdownsOpenOnHoverSetting = "TopbarDropdownsOpenOnHover";
    private const byte IntValueKind = 1;
    private const string AppEntryPrefix = "app:";
    private const string AppEntrySuffix = ":home";
    public const string ToggleFavoritePath = "/navigation/favorites/toggle";

    private readonly SqlConnectionFactory _db;
    private readonly RbacService _rbac;
    private readonly CultureSelectionService _cultureSelectionService;
    private readonly IOptions<OmpAuthOptions> _authOptions;
    private readonly OmpBrandingService _brandingService;
    private readonly NotificationService _notifications;
    private readonly MessageService _messages;
    private readonly BannerService _banners;
    private readonly ILogger<PortalTopBarService> _log;

    public PortalTopBarService(
        SqlConnectionFactory db,
        RbacService rbac,
        CultureSelectionService cultureSelectionService,
        IOptions<OmpAuthOptions> authOptions,
        OmpBrandingService brandingService,
        NotificationService notifications,
        MessageService messages,
        BannerService banners,
        ILogger<PortalTopBarService> log)
    {
        _db = db;
        _rbac = rbac;
        _cultureSelectionService = cultureSelectionService;
        _authOptions = authOptions;
        _brandingService = brandingService;
        _notifications = notifications;
        _messages = messages;
        _banners = banners;
        _log = log;
    }

    public async Task<PortalTopBarModel> CreateAsync(
        WebAppOptions options,
        HttpRequest request,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var topBarOptions = options.PortalTopBar ?? new PortalTopBarOptions();
        if (!topBarOptions.Enabled)
        {
            return PortalTopBarModel.Hidden;
        }

        var branding = await _brandingService.GetBrandingAsync(ct);
        var portalLink = new PortalTopBarLink(
            branding.PlatformName,
            PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, "/"));

        var cultureSelection = _cultureSelectionService.Resolve(options, request);

        try
        {
            var roleContext = await _rbac.GetUserRoleContextAsync(user, ct);
            var permissions = roleContext.EffectivePermissions;
            var apps = await GetEnabledWebAppsAsync(ct);
            var accessibleApps = apps
                .Where(app => HasAccess(app, permissions))
                .ToDictionary(app => app.AppInstanceId);
            var portalEntries = await GetPortalEntryRowsAsync(ct);
            var isPortalAdmin = permissions.Contains(PortalAdminPermission);
            var currentUserName = user.Identity?.IsAuthenticated == true
                ? user.Identity?.Name
                : null;

            var navigationGroups = BuildPortalEntryNavigationGroups(
                portalEntries,
                apps,
                accessibleApps,
                permissions,
                GetPortalBasePath(options),
                row => ResolvePortalEntryHref(request, row, accessibleApps));
            var userId = TryGetOmpUserId(user);
            var dropdownsOpenOnHover = true;
            IReadOnlyList<FavoriteRef> favorites = [];
            IReadOnlyList<PortalTopBarNotification> notifications = [];
            IReadOnlyList<PortalTopBarBanner> banners = [];
            var unreadNotificationCount = 0;
            var unreadMessageCount = 0;
            if (user.Identity?.IsAuthenticated == true)
            {
                banners = await _banners.GetActiveForRolesAsync(GetBannerRoleIds(roleContext), 3, ct);
            }

            if (userId is int resolvedUserId)
            {
                dropdownsOpenOnHover = await GetTopbarDropdownsOpenOnHoverAsync(resolvedUserId, ct);
                favorites = await GetFavoriteRefsAsync(resolvedUserId, ct);
                notifications = await _notifications.GetRecentForUserAsync(resolvedUserId, 10, ct);
                unreadNotificationCount = await _notifications.GetUnreadCountAsync(resolvedUserId, ct);
                unreadMessageCount = await _messages.GetUnreadConversationCountAsync(resolvedUserId, ct);
            }

            ApplyFavorites(navigationGroups, favorites);
            var navigationEntries = FlattenNavigationGroups(navigationGroups);

            return CreateModel(
                topBarOptions,
                portalLink,
                cultureSelection,
                currentUserName,
                roleContext,
                isPortalAdmin,
                moduleLinks: Array.Empty<PortalTopBarLink>(),
                navigationGroups,
                BuildFavoriteEntries(navigationEntries),
                canUsePersistentFavorites: userId.HasValue,
                favoriteToggleUrl: BuildRequestEndpointHref(request, ToggleFavoritePath),
                notifications,
                banners,
                canUseNotifications: userId.HasValue,
                unreadNotificationCount,
                notificationMarkReadUrl: BuildRequestEndpointHref(request, NotificationService.MarkReadPath),
                notificationMarkAllReadUrl: BuildRequestEndpointHref(request, NotificationService.MarkAllReadPath),
                notificationRecentUrl: BuildRequestEndpointHref(request, NotificationService.RecentPath),
                notificationsUrl: PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, "/notifications"),
                canUseMessages: userId.HasValue,
                unreadMessageCount,
                messagesUrl: PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, "/messages"),
                dropdownsOpenOnHover,
                options,
                GetLogoutUrl(),
                GetLoginUrl());
        }
        catch (SqlException ex)
        {
            _log.LogWarning(ex, "Failed to build portal top bar dynamically from the database. Falling back to a portal-only top bar.");
            return CreateFallbackModel(topBarOptions, portalLink, cultureSelection, user, options, GetLogoutUrl());
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Failed to build portal top bar dynamically from the database. Falling back to a portal-only top bar.");
            return CreateFallbackModel(topBarOptions, portalLink, cultureSelection, user, options, GetLogoutUrl());
        }
    }

    public async Task<bool> CanAccessReturnUrlAsync(
        WebAppOptions options,
        string? returnUrl,
        IReadOnlySet<string> permissions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
        {
            return false;
        }

        var path = ExtractPath(returnUrl);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = NormalizeAbsolutePath(path);
        if (string.Equals(normalizedPath, "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var portalBasePath = GetPortalBasePath(options);
        if (IsPortalAdminPath(normalizedPath, portalBasePath))
        {
            return permissions.Contains(PortalAdminPermission);
        }

        var apps = await GetEnabledWebAppsAsync(ct);
        var app = FindBestMatchingApp(apps, normalizedPath);
        if (app is not null)
        {
            if (!HasAccess(app, permissions))
            {
                return false;
            }

            var appRelativePath = GetAppRelativePath(normalizedPath, app);
            if (!RequiresElevatedAccess(appRelativePath))
            {
                return true;
            }

            return HasInferredAdminAccess(app, permissions);
        }

        if (IsPortalRootPath(normalizedPath, portalBasePath))
        {
            return true;
        }

        return !string.Equals(portalBasePath, "/", StringComparison.OrdinalIgnoreCase)
            && IsPathMatch(normalizedPath, portalBasePath);
    }

    public async Task<PortalFavoriteToggleResult> ToggleFavoriteAsync(
        WebAppOptions options,
        HttpRequest request,
        ClaimsPrincipal user,
        string? entryKey,
        Guid? appInstanceId,
        CancellationToken ct)
    {
        var userId = TryGetOmpUserId(user);
        if (userId is null || string.IsNullOrWhiteSpace(entryKey))
        {
            return new PortalFavoriteToggleResult(false, false, null);
        }

        var roleContext = await _rbac.GetUserRoleContextAsync(user, ct);
        var apps = await GetEnabledWebAppsAsync(ct);
        var accessibleApps = apps
            .Where(app => HasAccess(app, roleContext.EffectivePermissions))
            .ToDictionary(app => app.AppInstanceId);
        var portalEntries = await GetPortalEntryRowsAsync(ct);
        var groups = BuildPortalEntryNavigationGroups(
            portalEntries,
            apps,
            accessibleApps,
            roleContext.EffectivePermissions,
            GetPortalBasePath(options),
            row => ResolvePortalEntryHref(request, row, accessibleApps));
        var entries = FlattenNavigationGroups(groups);
        var entry = entries.FirstOrDefault(candidate =>
            string.Equals(candidate.EntryKey, entryKey.Trim(), StringComparison.Ordinal)
            && (!appInstanceId.HasValue || candidate.AppInstanceId == appInstanceId));

        if (entry is null || !entry.IsFavoritable)
        {
            return new PortalFavoriteToggleResult(false, false, null);
        }

        var isFavorite = await ToggleFavoriteRowAsync(userId.Value, entry, ct);
        entry.IsFavorite = isFavorite;
        return new PortalFavoriteToggleResult(true, isFavorite, entry);
    }

    public async Task<PortalTopBarModel> CreateAsync(
        WebAppOptions options,
        Uri currentUri,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var topBarOptions = options.PortalTopBar ?? new PortalTopBarOptions();
        if (!topBarOptions.Enabled)
        {
            return PortalTopBarModel.Hidden;
        }

        var branding = await _brandingService.GetBrandingAsync(ct);
        var portalLink = new PortalTopBarLink(
            branding.PlatformName,
            PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, "/"));

        var cultureSelection = _cultureSelectionService.ResolveFromCurrentCulture(options);

        try
        {
            var roleContext = await _rbac.GetUserRoleContextAsync(user, ct);
            var permissions = roleContext.EffectivePermissions;
            var apps = await GetEnabledWebAppsAsync(ct);
            var accessibleApps = apps
                .Where(app => HasAccess(app, permissions))
                .ToDictionary(app => app.AppInstanceId);
            var portalEntries = await GetPortalEntryRowsAsync(ct);
            var isPortalAdmin = permissions.Contains(PortalAdminPermission);
            var currentUserName = user.Identity?.IsAuthenticated == true
                ? user.Identity?.Name
                : null;

            var navigationGroups = BuildPortalEntryNavigationGroups(
                portalEntries,
                apps,
                accessibleApps,
                permissions,
                GetPortalBasePath(options),
                row => ResolvePortalEntryHref(currentUri, row, accessibleApps));
            var userId = TryGetOmpUserId(user);
            var dropdownsOpenOnHover = true;
            IReadOnlyList<FavoriteRef> favorites = [];
            IReadOnlyList<PortalTopBarNotification> notifications = [];
            IReadOnlyList<PortalTopBarBanner> banners = [];
            var unreadNotificationCount = 0;
            var unreadMessageCount = 0;
            if (user.Identity?.IsAuthenticated == true)
            {
                banners = await _banners.GetActiveForRolesAsync(GetBannerRoleIds(roleContext), 3, ct);
            }

            if (userId is int resolvedUserId)
            {
                dropdownsOpenOnHover = await GetTopbarDropdownsOpenOnHoverAsync(resolvedUserId, ct);
                favorites = await GetFavoriteRefsAsync(resolvedUserId, ct);
                notifications = await _notifications.GetRecentForUserAsync(resolvedUserId, 10, ct);
                unreadNotificationCount = await _notifications.GetUnreadCountAsync(resolvedUserId, ct);
                unreadMessageCount = await _messages.GetUnreadConversationCountAsync(resolvedUserId, ct);
            }

            ApplyFavorites(navigationGroups, favorites);
            var navigationEntries = FlattenNavigationGroups(navigationGroups);

            return CreateModel(
                topBarOptions,
                portalLink,
                cultureSelection,
                currentUserName,
                roleContext,
                isPortalAdmin,
                moduleLinks: Array.Empty<PortalTopBarLink>(),
                navigationGroups,
                BuildFavoriteEntries(navigationEntries),
                canUsePersistentFavorites: userId.HasValue,
                favoriteToggleUrl: BuildUriEndpointHref(currentUri, ToggleFavoritePath),
                notifications,
                banners,
                canUseNotifications: userId.HasValue,
                unreadNotificationCount,
                notificationMarkReadUrl: BuildUriEndpointHref(currentUri, NotificationService.MarkReadPath),
                notificationMarkAllReadUrl: BuildUriEndpointHref(currentUri, NotificationService.MarkAllReadPath),
                notificationRecentUrl: BuildUriEndpointHref(currentUri, NotificationService.RecentPath),
                notificationsUrl: PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, "/notifications"),
                canUseMessages: userId.HasValue,
                unreadMessageCount,
                messagesUrl: PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, "/messages"),
                dropdownsOpenOnHover,
                options,
                GetLogoutUrl(),
                GetLoginUrl());
        }
        catch (SqlException ex)
        {
            _log.LogWarning(ex, "Failed to build portal top bar dynamically from the database. Falling back to a portal-only top bar.");
            return CreateFallbackModel(topBarOptions, portalLink, cultureSelection, user, options, GetLogoutUrl());
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Failed to build portal top bar dynamically from the database. Falling back to a portal-only top bar.");
            return CreateFallbackModel(topBarOptions, portalLink, cultureSelection, user, options, GetLogoutUrl());
        }
    }

    /// <summary>
    /// Builds the fail-safe portal-only model used when dynamic app discovery cannot be completed.
    /// </summary>
    /// <remarks>
    /// The top bar should stay usable even when the OMP database is temporarily unavailable.
    /// In that case the shared UI deliberately hides dynamic module links and role data instead of
    /// surfacing an error to every page that renders the component.
    /// </remarks>
    private static PortalTopBarModel CreateFallbackModel(
        PortalTopBarOptions topBarOptions,
        PortalTopBarLink portalLink,
        CultureSelectionResult cultureSelection,
        ClaimsPrincipal user,
        WebAppOptions options,
        string logoutUrl)
        => CreateModel(
            topBarOptions,
            portalLink,
            cultureSelection,
            user.Identity?.IsAuthenticated == true ? user.Identity?.Name : null,
            UserRoleContext.Empty,
            isPortalAdmin: false,
            moduleLinks: Array.Empty<PortalTopBarLink>(),
            navigationGroups: Array.Empty<PortalTopBarNavigationGroup>(),
            favoriteEntries: Array.Empty<PortalTopBarNavigationEntry>(),
            canUsePersistentFavorites: false,
            favoriteToggleUrl: ToggleFavoritePath,
            notifications: Array.Empty<PortalTopBarNotification>(),
            banners: Array.Empty<PortalTopBarBanner>(),
            canUseNotifications: false,
            unreadNotificationCount: 0,
            notificationMarkReadUrl: NotificationService.MarkReadPath,
            notificationMarkAllReadUrl: NotificationService.MarkAllReadPath,
            notificationRecentUrl: NotificationService.RecentPath,
            notificationsUrl: "/notifications",
            canUseMessages: false,
            unreadMessageCount: 0,
            messagesUrl: "/messages",
            dropdownsOpenOnHover: true,
            options,
            logoutUrl,
            OmpAuthDefaults.LoginPath);

    private static IReadOnlyList<int> GetBannerRoleIds(UserRoleContext roleContext)
        => roleContext.AvailableRoles
            .Select(role => role.RoleId)
            .Concat(roleContext.EffectiveRoleIds)
            .Distinct()
            .ToArray();

    private static PortalTopBarModel CreateModel(
        PortalTopBarOptions topBarOptions,
        PortalTopBarLink portalLink,
        CultureSelectionResult cultureSelection,
        string? currentUserName,
        UserRoleContext roleContext,
        bool isPortalAdmin,
        IReadOnlyList<PortalTopBarLink> moduleLinks,
        IReadOnlyList<PortalTopBarNavigationGroup> navigationGroups,
        IReadOnlyList<PortalTopBarNavigationEntry> favoriteEntries,
        bool canUsePersistentFavorites,
        string favoriteToggleUrl,
        IReadOnlyList<PortalTopBarNotification> notifications,
        IReadOnlyList<PortalTopBarBanner> banners,
        bool canUseNotifications,
        int unreadNotificationCount,
        string notificationMarkReadUrl,
        string notificationMarkAllReadUrl,
        string notificationRecentUrl,
        string notificationsUrl,
        bool canUseMessages,
        int unreadMessageCount,
        string messagesUrl,
        bool dropdownsOpenOnHover,
        WebAppOptions options,
        string logoutUrl,
        string loginUrl)
    {
        var portalAdminSections = isPortalAdmin
            ? PortalAdminNavigation.CreateSections(relativePath => PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, relativePath))
            : Array.Empty<PortalAdminMenuSection>();

        return new()
        {
            IsVisible = true,
            PortalLink = portalLink,
            ModuleLinks = moduleLinks,
            NavigationGroups = navigationGroups,
            FavoriteEntries = favoriteEntries,
            CanUsePersistentFavorites = canUsePersistentFavorites,
            FavoriteToggleUrl = favoriteToggleUrl,
            Notifications = notifications,
            Banners = banners,
            CanUseNotifications = canUseNotifications,
            UnreadNotificationCount = unreadNotificationCount,
            NotificationMarkReadUrl = notificationMarkReadUrl,
            NotificationMarkAllReadUrl = notificationMarkAllReadUrl,
            NotificationRecentUrl = notificationRecentUrl,
            NotificationsUrl = notificationsUrl,
            CanUseMessages = canUseMessages,
            UnreadMessageCount = unreadMessageCount,
            MessagesUrl = messagesUrl,
            Links = [portalLink, .. moduleLinks],
            IsPortalAdmin = isPortalAdmin,
            PortalAdminSections = portalAdminSections,
            PortalAdminLinks = portalAdminSections
                .SelectMany(section => section.Items.Select(item => new PortalTopBarLink(item.TextKey, item.Href)))
                .ToArray(),
            LanguageOptions = CreateLanguageOptions(options, cultureSelection),
            PreferredCulture = cultureSelection.PreferredCulture,
            EffectiveCulture = cultureSelection.EffectiveCulture,
            PreferredCultureDisplayText = cultureSelection.PreferredCultureDisplayText,
            EffectiveCultureDisplayText = cultureSelection.EffectiveCultureDisplayText,
            IsCultureFallback = cultureSelection.IsFallback,
            CurrentUserName = currentUserName,
            AvailableRoles = roleContext.AvailableRoles,
            ActiveRoleId = roleContext.ActiveRoleId,
            ActiveRoleName = roleContext.ActiveRoleName,
            OverflowToggleTextKey = "More",
            AllModulesToggleTextKey = "All modules",
            FavoritesToggleTextKey = "Favorites",
            NavigationFilterPlaceholderTextKey = "Search modules",
            NoFavoritesTextKey = "No favorites",
            NotificationsToggleTextKey = "Notifications",
            MessagesToggleTextKey = "Messages",
            NoNotificationsTextKey = "No notifications",
            MarkAllNotificationsReadTextKey = "Mark all as read",
            ViewAllNotificationsTextKey = "View all notifications",
            AddFavoriteTextKey = "Add favorite",
            RemoveFavoriteTextKey = "Remove favorite",
            PortalAdminToggleTextKey = "Admin",
            LanguageToggleTextKey = "Language",
            LogoutUrl = logoutUrl,
            SettingsUrl = PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, PortalTopBarModel.DefaultSettingsPath),
            ShortcutsEnabled = options.TopbarShortcuts?.Enabled == true,
            AllModulesShortcut = options.TopbarShortcuts?.AllModules ?? "m",
            FavoritesShortcut = options.TopbarShortcuts?.Favorites ?? "f",
            DropdownsOpenOnHover = dropdownsOpenOnHover,
            SessionStatusCheckEnabled = options.SessionStatusCheck?.Enabled != false,
            SessionStatusUrl = PortalTopBarModel.DefaultSessionStatusPath,
            SessionLoginUrl = loginUrl,
            SessionStatusVisibleIntervalSeconds = PositiveOrDefault(options.SessionStatusCheck?.VisibleIntervalSeconds, 60),
            SessionStatusHiddenIntervalSeconds = PositiveOrDefault(options.SessionStatusCheck?.HiddenIntervalSeconds, 180)
        };
    }

    private static IReadOnlyList<PortalTopBarNavigationGroup> BuildPortalEntryNavigationGroups(
        IReadOnlyList<TopBarPortalEntryRow> rows,
        IReadOnlyList<TopBarAppEntry> apps,
        IReadOnlyDictionary<Guid, TopBarAppEntry> accessibleApps,
        IReadOnlySet<string> permissions,
        string portalBasePath,
        Func<TopBarPortalEntryRow, string?> resolveHref)
    {
        var nodes = rows
            .Where(row => row.IsEnabled)
            .Select(row =>
            {
                var href = resolveHref(row);
                var canAccess = CanAccessPortalEntry(row, href, apps, accessibleApps, permissions, portalBasePath);
                return new TopBarPortalEntryNode(row, canAccess ? href : null, canAccess);
            })
            .ToArray();

        var enabledEntryIds = nodes.Select(node => node.Row.PortalEntryId).ToHashSet();
        var rowsById = rows.ToDictionary(row => row.PortalEntryId);
        var disabledAncestorIds = new HashSet<int>(
            rows
                .Where(row => !row.IsEnabled)
                .Select(row => row.PortalEntryId));
        nodes = nodes
            .Where(node => !HasDisabledAncestor(node.Row, rowsById, disabledAncestorIds))
            .ToArray();

        var childrenByParentId = nodes
            .Where(node => node.Row.ParentEntryId.HasValue && enabledEntryIds.Contains(node.Row.ParentEntryId.Value))
            .GroupBy(node => node.Row.ParentEntryId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Row.DefaultSortOrder).ThenBy(node => node.Row.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());

        var groups = new List<PortalTopBarNavigationGroup>();
        foreach (var node in nodes
            .Where(node => !node.Row.ParentEntryId.HasValue || !enabledEntryIds.Contains(node.Row.ParentEntryId.Value))
            .OrderBy(node => node.Row.DefaultSortOrder)
            .ThenBy(node => node.Row.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var childNodes = childrenByParentId.TryGetValue(node.Row.PortalEntryId, out var children)
                ? children.Where(child => child.CanAccess).ToArray()
                : [];

            if (!node.CanAccess && childNodes.Length == 0)
            {
                continue;
            }

            groups.Add(new PortalTopBarNavigationGroup
            {
                GroupKey = node.Row.EntryKey,
                Title = node.Row.DisplayName,
                EntryKey = node.Row.EntryKey,
                AppInstanceId = GetPortalEntryAppInstanceId(node.Row),
                Href = node.Href ?? string.Empty,
                SortOrder = node.Row.DefaultSortOrder,
                IsFavoritable = node.CanAccess && !string.IsNullOrWhiteSpace(node.Href),
                SearchText = BuildPortalEntrySearchText(node.Row, node.Href),
                Entries = childNodes
                    .Select(child => ToNavigationEntry(child, node.Row))
                    .ToArray()
            });
        }

        return groups;
    }

    private static bool HasDisabledAncestor(
        TopBarPortalEntryRow row,
        IReadOnlyDictionary<int, TopBarPortalEntryRow> rowsById,
        IReadOnlySet<int> disabledEntryIds)
    {
        var visited = new HashSet<int> { row.PortalEntryId };
        var parentId = row.ParentEntryId;
        while (parentId.HasValue)
        {
            if (disabledEntryIds.Contains(parentId.Value))
            {
                return true;
            }

            if (!visited.Add(parentId.Value) || !rowsById.TryGetValue(parentId.Value, out var parent))
            {
                return false;
            }

            parentId = parent.ParentEntryId;
        }

        return false;
    }

    private static IReadOnlyList<PortalTopBarNavigationEntry> FlattenNavigationGroups(
        IReadOnlyList<PortalTopBarNavigationGroup> groups)
    {
        var entries = new List<PortalTopBarNavigationEntry>();
        foreach (var group in groups)
        {
            if (group.IsFavoritable && !string.IsNullOrWhiteSpace(group.EntryKey))
            {
                entries.Add(new PortalTopBarNavigationEntry
                {
                    EntryKey = group.EntryKey,
                    AppInstanceId = group.AppInstanceId,
                    GroupKey = group.GroupKey,
                    GroupTitle = string.Empty,
                    TextKey = group.Title,
                    Href = group.Href,
                    SortOrder = group.SortOrder,
                    IsFavoritable = group.IsFavoritable,
                    IsFavorite = group.IsFavorite,
                    SearchText = group.SearchText
                });
            }

            entries.AddRange(group.Entries);
        }

        return entries;
    }

    private static PortalTopBarNavigationEntry ToNavigationEntry(
        TopBarPortalEntryNode node,
        TopBarPortalEntryRow parent)
        => new()
        {
            EntryKey = node.Row.EntryKey,
            AppInstanceId = GetPortalEntryAppInstanceId(node.Row),
            GroupKey = parent.EntryKey,
            GroupTitle = parent.DisplayName,
            TextKey = node.Row.DisplayName,
            Href = node.Href ?? string.Empty,
            SortOrder = node.Row.DefaultSortOrder,
            IsFavoritable = node.CanAccess && !string.IsNullOrWhiteSpace(node.Href),
            SearchText = BuildPortalEntrySearchText(node.Row, node.Href)
        };

    private static bool CanAccessPortalEntry(
        TopBarPortalEntryRow row,
        string? href,
        IReadOnlyList<TopBarAppEntry> apps,
        IReadOnlyDictionary<Guid, TopBarAppEntry> accessibleApps,
        IReadOnlySet<string> permissions,
        string portalBasePath)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        if (row.SourceAppInstanceId.HasValue)
        {
            return accessibleApps.ContainsKey(row.SourceAppInstanceId.Value);
        }

        if (TryParseAppInstanceId(row.TargetEntryKey, out var targetAppInstanceId))
        {
            return accessibleApps.ContainsKey(targetAppInstanceId);
        }

        return CanAccessTargetHref(href, apps, accessibleApps, permissions, portalBasePath);
    }

    private static bool CanAccessTargetHref(
        string href,
        IReadOnlyList<TopBarAppEntry> apps,
        IReadOnlyDictionary<Guid, TopBarAppEntry> accessibleApps,
        IReadOnlySet<string> permissions,
        string portalBasePath)
    {
        var path = href;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
        {
            path = absoluteUri.AbsolutePath;
        }

        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        var normalizedPath = NormalizeAbsolutePath(path);
        if (IsPortalAdminPath(normalizedPath, portalBasePath))
        {
            return permissions.Contains(PortalAdminPermission);
        }

        var app = FindBestMatchingApp(apps, normalizedPath);
        if (app is not null)
        {
            if (!accessibleApps.ContainsKey(app.AppInstanceId))
            {
                return false;
            }

            var appRelativePath = GetAppRelativePath(normalizedPath, app);
            return !RequiresElevatedAccess(appRelativePath)
                || HasInferredAdminAccess(app, permissions);
        }

        return IsPortalRootPath(normalizedPath, portalBasePath)
            || !string.Equals(portalBasePath, "/", StringComparison.OrdinalIgnoreCase)
            && IsPathMatch(normalizedPath, portalBasePath);
    }

    private static Guid? GetPortalEntryAppInstanceId(TopBarPortalEntryRow row)
    {
        if (row.SourceAppInstanceId.HasValue)
        {
            return row.SourceAppInstanceId.Value;
        }

        return TryParseAppInstanceId(row.TargetEntryKey, out var targetAppInstanceId)
            ? targetAppInstanceId
            : null;
    }

    private static string BuildPortalEntrySearchText(TopBarPortalEntryRow row, string? href)
        => $"{row.DisplayName} {row.Description} {href} {row.EntryKey} {row.TargetEntryKey}".Trim();

    private static void ApplyFavorites(
        IReadOnlyList<PortalTopBarNavigationGroup> groups,
        IReadOnlyList<FavoriteRef> favorites)
    {
        foreach (var group in groups)
        {
            group.IsFavorite = favorites.Any(favorite =>
                string.Equals(favorite.EntryKey, group.EntryKey, StringComparison.Ordinal)
                && (!favorite.AppInstanceId.HasValue || favorite.AppInstanceId == group.AppInstanceId));

            foreach (var entry in group.Entries)
            {
                entry.IsFavorite = favorites.Any(favorite =>
                    string.Equals(favorite.EntryKey, entry.EntryKey, StringComparison.Ordinal)
                    && (!favorite.AppInstanceId.HasValue || favorite.AppInstanceId == entry.AppInstanceId));
            }
        }
    }

    private static IReadOnlyList<PortalTopBarNavigationEntry> BuildFavoriteEntries(
        IReadOnlyList<PortalTopBarNavigationEntry> entries)
        => entries
            .Where(entry => entry.IsFavorite)
            .OrderBy(entry => entry.GroupTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.TextKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsContentWebApp(TopBarAppEntry app)
        => string.Equals(app.AppKey, "content_webapp_webapp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(app.AppInstanceKey, "content_webapp_webapp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(app.RoutePath?.Trim('/'), "content", StringComparison.OrdinalIgnoreCase);

    private static int? TryGetOmpUserId(ClaimsPrincipal user)
    {
        var claimValue = user.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(claimValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : null;
    }

    private async Task<bool> GetTopbarDropdownsOpenOnHoverAsync(int userId, CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(COALESCE(v.setting_value, d.default_int_value, 1) AS bit)
FROM omp_portal.user_setting_definitions d
LEFT JOIN omp_portal.user_setting_int_values v
    ON v.user_setting_definition_id = d.user_setting_definition_id
   AND v.user_id = @user_id
WHERE d.setting_category = @setting_category
  AND d.setting_name = @setting_name
  AND d.value_kind = @value_kind
  AND d.is_enabled = 1;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@setting_category", SqlDbType.NVarChar, 100).Value = PortalSettingCategory;
        cmd.Parameters.Add("@setting_name", SqlDbType.NVarChar, 200).Value = TopbarDropdownsOpenOnHoverSetting;
        cmd.Parameters.Add("@value_kind", SqlDbType.TinyInt).Value = IntValueKind;

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is bool openOnHover)
        {
            return openOnHover;
        }

        return true;
    }

    private string GetLogoutUrl()
        => string.IsNullOrWhiteSpace(_authOptions.Value.LogoutPath)
            ? OmpAuthDefaults.LogoutPath
            : _authOptions.Value.LogoutPath;

    private string GetLoginUrl()
        => string.IsNullOrWhiteSpace(_authOptions.Value.LoginPath)
            ? OmpAuthDefaults.LoginPath
            : _authOptions.Value.LoginPath;

    private static int PositiveOrDefault(int? value, int fallback)
        => value is > 0 ? value.Value : fallback;

    private static IReadOnlyList<PortalTopBarCultureOption> CreateLanguageOptions(
        WebAppOptions options,
        CultureSelectionResult cultureSelection)
    {
        var cultures = (options.SupportedCultures ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Trim())
            .ToArray();

        if (cultures.Length == 0)
        {
            cultures = [cultureSelection.EffectiveCulture];
        }

        return cultures
            .Select(c => new PortalTopBarCultureOption(c, ToCultureDisplayText(c), string.Equals(c, cultureSelection.EffectiveCulture, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }


    private static bool HasAccess(TopBarAppEntry app, IReadOnlySet<string> permissions)
    {
        if (app.RequiredPermissions.Count == 0)
        {
            return true;
        }

        return app.RequireAll
            ? app.RequiredPermissions.All(permissions.Contains)
            : app.RequiredPermissions.Any(permissions.Contains);
    }

    private static TopBarAppEntry? FindBestMatchingApp(
        IReadOnlyList<TopBarAppEntry> apps,
        string normalizedPath)
    {
        return apps
            .Where(app => !IsPortalApp(app))
            .Select(app => new
            {
                App = app,
                Prefix = GetAppPathPrefix(app)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Prefix) && IsPathMatch(normalizedPath, x.Prefix!))
            .OrderByDescending(x => x.Prefix!.Length)
            .Select(x => x.App)
            .FirstOrDefault();
    }

    private static string GetAppRelativePath(string normalizedPath, TopBarAppEntry app)
    {
        var prefix = GetAppPathPrefix(app);
        if (string.IsNullOrWhiteSpace(prefix) || string.Equals(normalizedPath, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        var remainder = normalizedPath[prefix.Length..].TrimStart('/');
        return string.IsNullOrWhiteSpace(remainder)
            ? "/"
            : $"/{remainder}";
    }

    private static string? GetAppPathPrefix(TopBarAppEntry app)
    {
        var routePath = Clean(app.RoutePath);
        if (string.IsNullOrWhiteSpace(routePath) || Uri.TryCreate(routePath, UriKind.Absolute, out _))
        {
            return null;
        }

        return NormalizeAbsolutePath($"/{routePath.Trim('/')}");
    }

    private static bool RequiresElevatedAccess(string appRelativePath)
    {
        if (string.IsNullOrWhiteSpace(appRelativePath) || string.Equals(appRelativePath, "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return appRelativePath.Contains("/edit", StringComparison.OrdinalIgnoreCase)
            || appRelativePath.Contains("/admin", StringComparison.OrdinalIgnoreCase)
            || appRelativePath.Contains("/create", StringComparison.OrdinalIgnoreCase)
            || appRelativePath.Contains("/new", StringComparison.OrdinalIgnoreCase)
            || appRelativePath.Contains("/delete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasInferredAdminAccess(TopBarAppEntry app, IReadOnlySet<string> permissions)
    {
        var candidatePermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var permission in app.RequiredPermissions.Where(permission => permission.EndsWith(".View", StringComparison.OrdinalIgnoreCase)))
        {
            candidatePermissions.Add($"{permission[..^5]}.Admin");
        }

        if (!string.IsNullOrWhiteSpace(app.AppKey))
        {
            candidatePermissions.Add($"{app.AppKey}.Admin");
        }

        if (!string.IsNullOrWhiteSpace(app.AppInstanceKey))
        {
            candidatePermissions.Add($"{app.AppInstanceKey}.Admin");
        }

        if (IsContentWebApp(app))
        {
            candidatePermissions.Add(ContentManagePermission);
        }

        return candidatePermissions.Count == 0 || candidatePermissions.Any(permissions.Contains);
    }

    private static string GetPortalBasePath(WebAppOptions options)
    {
        var portalBaseUrl = options.PortalTopBar?.PortalBaseUrl;
        if (string.IsNullOrWhiteSpace(portalBaseUrl))
        {
            return "/";
        }

        if (Uri.TryCreate(portalBaseUrl, UriKind.Absolute, out var absolutePortalUri))
        {
            return NormalizeAbsolutePath(absolutePortalUri.AbsolutePath);
        }

        return portalBaseUrl.StartsWith("/", StringComparison.Ordinal)
            ? NormalizeAbsolutePath(portalBaseUrl)
            : "/";
    }

    private static bool IsPortalRootPath(string normalizedPath, string portalBasePath)
        => string.Equals(normalizedPath, NormalizeAbsolutePath(portalBasePath), StringComparison.OrdinalIgnoreCase);

    private static bool IsPortalAdminPath(string normalizedPath, string portalBasePath)
        => IsPathMatch(normalizedPath, CombineRelativePath(portalBasePath, "admin"));

    private static bool IsPathMatch(string normalizedPath, string normalizedPrefix)
    {
        if (string.Equals(normalizedPrefix, "/", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath.StartsWith("/", StringComparison.Ordinal);
        }

        return string.Equals(normalizedPath, normalizedPrefix, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith($"{normalizedPrefix}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineRelativePath(string basePath, string relativePath)
    {
        var normalizedBasePath = NormalizeAbsolutePath(basePath);
        var trimmedRelativePath = relativePath.Trim('/');
        if (string.IsNullOrWhiteSpace(trimmedRelativePath))
        {
            return normalizedBasePath;
        }

        return string.Equals(normalizedBasePath, "/", StringComparison.Ordinal)
            ? $"/{trimmedRelativePath}"
            : $"{normalizedBasePath}/{trimmedRelativePath}";
    }

    private static string NormalizeAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalizedPath = path.Trim();
        var questionMarkIndex = normalizedPath.IndexOf('?');
        var hashIndex = normalizedPath.IndexOf('#');
        var queryIndex = questionMarkIndex >= 0 && hashIndex >= 0
            ? Math.Min(questionMarkIndex, hashIndex)
            : Math.Max(questionMarkIndex, hashIndex);
        if (queryIndex >= 0)
        {
            normalizedPath = normalizedPath[..queryIndex];
        }

        normalizedPath = normalizedPath.Trim('/');
        return string.IsNullOrWhiteSpace(normalizedPath)
            ? "/"
            : $"/{normalizedPath}";
    }

    private static string ExtractPath(string returnUrl)
    {
        var questionMarkIndex = returnUrl.IndexOf('?');
        var hashIndex = returnUrl.IndexOf('#');
        var queryIndex = questionMarkIndex >= 0 && hashIndex >= 0
            ? Math.Min(questionMarkIndex, hashIndex)
            : Math.Max(questionMarkIndex, hashIndex);
        return queryIndex >= 0
            ? returnUrl[..queryIndex]
            : returnUrl;
    }

    private static string ToCultureDisplayText(string culture)
    {
        if (culture.StartsWith("sv", StringComparison.OrdinalIgnoreCase))
        {
            return "Swedish";
        }

        if (culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "English";
        }

        try
        {
            return CultureInfo.GetCultureInfo(culture).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }

    private async Task<IReadOnlyList<TopBarPortalEntryRow>> GetPortalEntryRowsAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
IF OBJECT_ID(N'omp_portal.portal_entries', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS int) AS portal_entry_id,
           CAST(NULL AS int) AS parent_entry_id,
           CAST(NULL AS nvarchar(200)) AS entry_key,
           CAST(NULL AS nvarchar(200)) AS display_name,
           CAST(NULL AS nvarchar(1000)) AS description,
           CAST(NULL AS nvarchar(600)) AS target_url,
           CAST(NULL AS nvarchar(200)) AS target_entry_key,
           CAST(NULL AS uniqueidentifier) AS source_app_instance_id,
           CAST(NULL AS bit) AS is_enabled,
           CAST(NULL AS int) AS default_sort_order
    WHERE 1 = 0;
END
ELSE
BEGIN
    SELECT portal_entry_id,
           parent_entry_id,
           entry_key,
           display_name,
           description,
           target_url,
           target_entry_key,
           source_app_instance_id,
           is_enabled,
           default_sort_order
    FROM omp_portal.portal_entries
    ORDER BY COALESCE(parent_entry_id, portal_entry_id),
             CASE WHEN parent_entry_id IS NULL THEN 0 ELSE 1 END,
             default_sort_order,
             display_name;
END";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<TopBarPortalEntryRow>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new TopBarPortalEntryRow
            {
                PortalEntryId = rdr.GetInt32(0),
                ParentEntryId = rdr.IsDBNull(1) ? null : rdr.GetInt32(1),
                EntryKey = rdr.GetString(2),
                DisplayName = rdr.GetString(3),
                Description = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                TargetUrl = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                TargetEntryKey = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                SourceAppInstanceId = rdr.IsDBNull(7) ? null : rdr.GetGuid(7),
                IsEnabled = rdr.GetBoolean(8),
                DefaultSortOrder = rdr.GetInt32(9)
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<FavoriteRef>> GetFavoriteRefsAsync(int userId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
IF OBJECT_ID(N'omp_portal.user_navigation_favorites', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS nvarchar(200)) AS entry_key,
           CAST(NULL AS uniqueidentifier) AS app_instance_id,
           CAST(NULL AS int) AS sort_order
    WHERE 1 = 0;
END
ELSE
BEGIN
    SELECT entry_key,
           app_instance_id,
           sort_order
    FROM omp_portal.user_navigation_favorites
    WHERE user_id = @user_id
    ORDER BY COALESCE(sort_order, 2147483647),
             created_at,
             entry_key;
END";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<FavoriteRef>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new FavoriteRef(
                rdr.GetString(0),
                rdr.IsDBNull(1) ? null : rdr.GetGuid(1),
                rdr.IsDBNull(2) ? null : rdr.GetInt32(2)));
        }

        return rows;
    }

    private async Task<bool> ToggleFavoriteRowAsync(
        int userId,
        PortalTopBarNavigationEntry entry,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
IF EXISTS
(
    SELECT 1
    FROM omp_portal.user_navigation_favorites
    WHERE user_id = @user_id
      AND entry_key = @entry_key
)
BEGIN
    DELETE FROM omp_portal.user_navigation_favorites
    WHERE user_id = @user_id
      AND entry_key = @entry_key;

    SELECT CAST(0 AS bit);
END
ELSE
BEGIN
    INSERT INTO omp_portal.user_navigation_favorites(user_id, entry_key, app_instance_id, sort_order)
    VALUES(@user_id, @entry_key, @app_instance_id, @sort_order);

    SELECT CAST(1 AS bit);
END";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@entry_key", SqlDbType.NVarChar, 200).Value = entry.EntryKey;
        cmd.Parameters.Add("@app_instance_id", SqlDbType.UniqueIdentifier).Value =
            entry.AppInstanceId.HasValue ? entry.AppInstanceId.Value : DBNull.Value;
        cmd.Parameters.Add("@sort_order", SqlDbType.Int).Value = entry.SortOrder;

        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> HostBaseUrlColumnExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN COL_LENGTH('omp.Hosts', 'BaseUrl') IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static string? ResolveHref(HttpRequest request, TopBarAppEntry app)
    {
        var routePath = Clean(app.RoutePath);
        if (!string.IsNullOrWhiteSpace(routePath))
        {
            if (Uri.TryCreate(routePath, UriKind.Absolute, out var absoluteRoute))
            {
                return absoluteRoute.ToString();
            }

            var hostRoot = ResolveHostRoot(request, app);
            return string.IsNullOrWhiteSpace(hostRoot)
                ? null
                : CombineHostRootAndRoute(hostRoot, routePath);
        }

        var publicUrl = Clean(app.PublicUrl);
        if (!string.IsNullOrWhiteSpace(publicUrl))
        {
            return publicUrl;
        }

        if (IsPortalApp(app))
        {
            var portalPath = request.PathBase.HasValue ? request.PathBase.Value.ToString() : string.Empty;
            return $"{request.GetPublicBaseUrl().TrimEnd('/')}{portalPath}";
        }

        return null;
    }

    private static string? ResolvePortalEntryHref(
        HttpRequest request,
        TopBarPortalEntryRow row,
        IReadOnlyDictionary<Guid, TopBarAppEntry> accessibleApps)
    {
        if (row.SourceAppInstanceId is Guid sourceAppInstanceId
            && accessibleApps.TryGetValue(sourceAppInstanceId, out var sourceApp))
        {
            return ResolveHref(request, sourceApp);
        }

        if (TryParseAppInstanceId(row.TargetEntryKey, out var targetAppInstanceId)
            && accessibleApps.TryGetValue(targetAppInstanceId, out var targetApp))
        {
            return ResolveHref(request, targetApp);
        }

        return ResolveTargetUrl(request, row.TargetUrl);
    }

    private static string? ResolvePortalEntryHref(
        Uri currentUri,
        TopBarPortalEntryRow row,
        IReadOnlyDictionary<Guid, TopBarAppEntry> accessibleApps)
    {
        if (row.SourceAppInstanceId is Guid sourceAppInstanceId
            && accessibleApps.TryGetValue(sourceAppInstanceId, out var sourceApp))
        {
            return ResolveHref(currentUri, sourceApp);
        }

        if (TryParseAppInstanceId(row.TargetEntryKey, out var targetAppInstanceId)
            && accessibleApps.TryGetValue(targetAppInstanceId, out var targetApp))
        {
            return ResolveHref(currentUri, targetApp);
        }

        return ResolveTargetUrl(currentUri, row.TargetUrl);
    }

    private static string? ResolveTargetUrl(HttpRequest request, string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return null;
        }

        var trimmed = targetUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return $"{request.GetPublicBaseUrl().TrimEnd('/')}/{trimmed.TrimStart('/')}";
    }

    private static string BuildRequestEndpointHref(HttpRequest request, string endpointPath)
    {
        var pathBase = request.PathBase.HasValue
            ? request.PathBase.Value!.TrimEnd('/')
            : string.Empty;

        return string.IsNullOrWhiteSpace(pathBase)
            ? endpointPath
            : $"{pathBase}{endpointPath}";
    }

    private static string? ResolveHref(Uri currentUri, TopBarAppEntry app)
    {
        var routePath = Clean(app.RoutePath);
        if (!string.IsNullOrWhiteSpace(routePath))
        {
            if (Uri.TryCreate(routePath, UriKind.Absolute, out var absoluteRoute))
            {
                return absoluteRoute.ToString();
            }

            var hostRoot = ResolveHostRoot(currentUri, app);
            return string.IsNullOrWhiteSpace(hostRoot)
                ? null
                : CombineHostRootAndRoute(hostRoot, routePath);
        }

        var publicUrl = Clean(app.PublicUrl);
        if (!string.IsNullOrWhiteSpace(publicUrl))
        {
            return publicUrl;
        }

        if (IsPortalApp(app))
        {
            var authority = currentUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            var currentPath = currentUri.AbsolutePath;
            var firstSlash = currentPath.IndexOf('/', 1);
            var basePath = firstSlash > 0 ? currentPath[..firstSlash] : string.Empty;
            return string.IsNullOrWhiteSpace(basePath) ? authority : $"{authority}{basePath}";
        }

        return null;
    }

    private static string? ResolveTargetUrl(Uri currentUri, string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return null;
        }

        var trimmed = targetUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return $"{currentUri.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/{trimmed.TrimStart('/')}";
    }

    private static string BuildUriEndpointHref(Uri currentUri, string endpointPath)
    {
        var firstSegment = currentUri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        var basePath = string.IsNullOrWhiteSpace(firstSegment)
            ? string.Empty
            : $"/{firstSegment}";

        return string.IsNullOrWhiteSpace(basePath)
            ? endpointPath
            : $"{basePath.TrimEnd('/')}{endpointPath}";
    }

    private async Task<IReadOnlyList<TopBarAppEntry>> GetEnabledWebAppsAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasHostBaseUrl = await HostBaseUrlColumnExistsAsync(conn, ct);
        var hostBaseUrlSelect = hasHostBaseUrl
            ? "h.BaseUrl"
            : "CAST(NULL AS nvarchar(300)) AS BaseUrl";

        var sql = $@"
SELECT ai.AppInstanceId,
       ai.AppInstanceKey,
       a.AppKey,
       ai.DisplayName,
       ai.RoutePath,
       ai.PublicUrl,
       h.HostKey,
       {hostBaseUrlSelect},
       ai.SortOrder,
       p.Name AS PermissionName,
       ap.RequireAll,
       m.ModuleKey,
       mi.ModuleInstanceKey,
       mi.DisplayName AS ModuleInstanceDisplayName,
       m.DisplayName AS ModuleDisplayName
FROM omp.AppInstances ai
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
INNER JOIN omp.Modules m ON m.ModuleId = mi.ModuleId
LEFT JOIN omp.Hosts h ON h.HostId = ai.HostId
LEFT JOIN omp.AppPermissions ap ON ap.AppId = a.AppId
LEFT JOIN omp.Permissions p ON p.PermissionId = ap.PermissionId
WHERE ai.IsEnabled = 1
  AND ai.IsAllowed = 1
  AND mi.IsEnabled = 1
  AND m.IsEnabled = 1
  AND a.AppType = N'WebApp'
ORDER BY ai.SortOrder,
         ai.DisplayName;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var map = new Dictionary<Guid, TopBarAppEntry>();
        while (await rdr.ReadAsync(ct))
        {
            var appInstanceId = rdr.GetGuid(0);
            if (!map.TryGetValue(appInstanceId, out var entry))
            {
                entry = new TopBarAppEntry
                {
                    AppInstanceId = appInstanceId,
                    AppInstanceKey = rdr.GetString(1),
                    AppKey = rdr.GetString(2),
                    DisplayName = rdr.GetString(3),
                    RoutePath = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    PublicUrl = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    HostKey = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    HostBaseUrl = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    SortOrder = rdr.GetInt32(8),
                    RequireAll = !rdr.IsDBNull(10) && rdr.GetBoolean(10),
                    ModuleKey = rdr.GetString(11),
                    ModuleInstanceKey = rdr.GetString(12),
                    ModuleDisplayName = rdr.IsDBNull(13) ? rdr.GetString(14) : rdr.GetString(13)
                };

                map[appInstanceId] = entry;
            }
            else
            {
                entry.RequireAll = entry.RequireAll || (!rdr.IsDBNull(10) && rdr.GetBoolean(10));
            }

            if (!rdr.IsDBNull(9))
            {
                entry.RequiredPermissions.Add(rdr.GetString(9));
            }
        }

        return map.Values
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPortalApp(TopBarAppEntry app)
    {
        return string.Equals(app.AppKey, "omp_portal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(app.AppInstanceKey, "omp_portal", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveHostRoot(HttpRequest request, TopBarAppEntry app)
    {
        var hostBaseUrl = Clean(app.HostBaseUrl);
        if (!string.IsNullOrWhiteSpace(hostBaseUrl)
            && Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var absoluteBaseUrl))
        {
            return absoluteBaseUrl.GetLeftPart(UriPartial.Authority);
        }

        return request.GetPublicBaseUrl();
    }

    private static string? ResolveHostRoot(Uri currentUri, TopBarAppEntry app)
    {
        var hostBaseUrl = Clean(app.HostBaseUrl);
        if (!string.IsNullOrWhiteSpace(hostBaseUrl)
            && Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var absoluteBaseUrl))
        {
            return absoluteBaseUrl.GetLeftPart(UriPartial.Authority);
        }

        var authority = currentUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return authority;
    }

    private static string CombineHostRootAndRoute(string hostRoot, string routePath)
    {
        var normalizedHostRoot = hostRoot.Trim().TrimEnd('/');
        var trimmedRoute = routePath.Trim();
        var preserveTrailingSlash = trimmedRoute.EndsWith('/');
        var normalizedRoute = trimmedRoute.Trim('/');

        if (string.IsNullOrEmpty(normalizedRoute))
        {
            return normalizedHostRoot + "/";
        }

        return preserveTrailingSlash
            ? $"{normalizedHostRoot}/{normalizedRoute}/"
            : $"{normalizedHostRoot}/{normalizedRoute}";
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseAppInstanceId(string? targetEntryKey, out Guid appInstanceId)
    {
        appInstanceId = default;
        if (string.IsNullOrWhiteSpace(targetEntryKey)
            || !targetEntryKey.StartsWith(AppEntryPrefix, StringComparison.OrdinalIgnoreCase)
            || !targetEntryKey.EndsWith(AppEntrySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var idText = targetEntryKey[AppEntryPrefix.Length..^AppEntrySuffix.Length];
        return Guid.TryParseExact(idText, "N", out appInstanceId);
    }

    private sealed class TopBarPortalEntryRow
    {
        public int PortalEntryId { get; init; }

        public int? ParentEntryId { get; init; }

        public string EntryKey { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string? Description { get; init; }

        public string? TargetUrl { get; init; }

        public string? TargetEntryKey { get; init; }

        public Guid? SourceAppInstanceId { get; init; }

        public bool IsEnabled { get; init; }

        public int DefaultSortOrder { get; init; }
    }

    private sealed record TopBarPortalEntryNode(
        TopBarPortalEntryRow Row,
        string? Href,
        bool CanAccess);

    private sealed class TopBarAppEntry
    {
        public Guid AppInstanceId { get; init; }
        public string ModuleKey { get; init; } = string.Empty;
        public string ModuleInstanceKey { get; init; } = string.Empty;
        public string ModuleDisplayName { get; init; } = string.Empty;
        public string AppInstanceKey { get; init; } = string.Empty;
        public string AppKey { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? RoutePath { get; init; }
        public string? PublicUrl { get; init; }
        public string? HostKey { get; init; }
        public string? HostBaseUrl { get; init; }
        public int SortOrder { get; init; }
        public bool RequireAll { get; set; }
        public HashSet<string> RequiredPermissions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record FavoriteRef(string EntryKey, Guid? AppInstanceId, int? SortOrder);
}

public sealed record PortalFavoriteToggleResult(
    bool Success,
    bool IsFavorite,
    PortalTopBarNavigationEntry? Entry);
