using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Builds the shared portal top bar using the same access logic as the Portal start page.
/// </summary>
public sealed class PortalTopBarService
{
    private const string PortalAdminPermission = "OMP.Portal.Admin";

    private readonly SqlConnectionFactory _db;
    private readonly RbacService _rbac;
    private readonly CultureSelectionService _cultureSelectionService;
    private readonly ILogger<PortalTopBarService> _log;

    public PortalTopBarService(
        SqlConnectionFactory db,
        RbacService rbac,
        CultureSelectionService cultureSelectionService,
        ILogger<PortalTopBarService> log)
    {
        _db = db;
        _rbac = rbac;
        _cultureSelectionService = cultureSelectionService;
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

        var portalLink = new PortalTopBarLink(
            "Portal",
            PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, "/"));

        var cultureSelection = _cultureSelectionService.Resolve(options, request);

        try
        {
            var roleContext = await _rbac.GetUserRoleContextAsync(user, ct);
            var permissions = roleContext.EffectivePermissions;
            var apps = await GetEnabledWebAppsAsync(ct);
            var isPortalAdmin = permissions.Contains(PortalAdminPermission);
            var currentUserName = user.Identity?.IsAuthenticated == true
                ? user.Identity?.Name
                : null;

            var moduleLinks = apps
                .Where(app => HasAccess(app, permissions))
                .Select(app => new PortalTopBarLink(app.DisplayName, ResolveHref(request, app)!))
                .Where(link => !string.IsNullOrWhiteSpace(link.Href))
                .ToArray();

            return CreateModel(topBarOptions, portalLink, cultureSelection, currentUserName, roleContext, isPortalAdmin, moduleLinks, options);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to build portal top bar dynamically. Falling back to a portal-only top bar.");

            return CreateModel(topBarOptions, portalLink, cultureSelection, user.Identity?.IsAuthenticated == true ? user.Identity?.Name : null, UserRoleContext.Empty, false, Array.Empty<PortalTopBarLink>(), options);
        }
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

        var portalLink = new PortalTopBarLink(
            "Portal",
            PortalTopBarModelFactory.CombinePortalHref(topBarOptions.PortalBaseUrl, "/"));

        var cultureSelection = _cultureSelectionService.ResolveFromCurrentCulture(options);

        try
        {
            var roleContext = await _rbac.GetUserRoleContextAsync(user, ct);
            var permissions = roleContext.EffectivePermissions;
            var apps = await GetEnabledWebAppsAsync(ct);
            var isPortalAdmin = permissions.Contains(PortalAdminPermission);
            var currentUserName = user.Identity?.IsAuthenticated == true
                ? user.Identity?.Name
                : null;

            var moduleLinks = apps
                .Where(app => HasAccess(app, permissions))
                .Select(app => new PortalTopBarLink(app.DisplayName, ResolveHref(currentUri, app)!))
                .Where(link => !string.IsNullOrWhiteSpace(link.Href))
                .ToArray();

            return CreateModel(topBarOptions, portalLink, cultureSelection, currentUserName, roleContext, isPortalAdmin, moduleLinks, options);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to build portal top bar dynamically. Falling back to a portal-only top bar.");

            return CreateModel(topBarOptions, portalLink, cultureSelection, user.Identity?.IsAuthenticated == true ? user.Identity?.Name : null, UserRoleContext.Empty, false, Array.Empty<PortalTopBarLink>(), options);
        }
    }

    private static PortalTopBarModel CreateModel(
        PortalTopBarOptions topBarOptions,
        PortalTopBarLink portalLink,
        CultureSelectionResult cultureSelection,
        string? currentUserName,
        UserRoleContext roleContext,
        bool isPortalAdmin,
        IReadOnlyList<PortalTopBarLink> moduleLinks,
        WebAppOptions options)
        => new()
        {
            IsVisible = true,
            PortalLink = portalLink,
            ModuleLinks = moduleLinks,
            Links = [portalLink, .. moduleLinks],
            IsPortalAdmin = isPortalAdmin,
            PortalAdminLinks = isPortalAdmin
                ? CreatePortalAdminLinks(topBarOptions.PortalBaseUrl)
                : Array.Empty<PortalTopBarLink>(),
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
            PortalAdminToggleTextKey = "Admin",
            LanguageToggleTextKey = "Language"
        };

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

    private static IReadOnlyList<PortalTopBarLink> CreatePortalAdminLinks(string portalBaseUrl) =>
    [
        new("Admin", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/overview")),
        new("Instances", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/instances")),
        new("Hosts", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/hosts")),
        new("Modules", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/modules")),
        new("Apps", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/apps")),
        new("Artifacts", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/artifacts")),
        new("Automation", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/automation")),
        new("RBAC", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/rbac"))
    ];

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
            return string.IsNullOrWhiteSpace(basePath) ? "/" : $"{authority}{basePath}";
        }

        return null;
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
       ap.RequireAll
FROM omp.AppInstances ai
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
LEFT JOIN omp.Hosts h ON h.HostId = ai.HostId
LEFT JOIN omp.AppPermissions ap ON ap.AppId = a.AppId
LEFT JOIN omp.Permissions p ON p.PermissionId = ap.PermissionId
WHERE ai.IsEnabled = 1
  AND ai.IsAllowed = 1
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
                    RequireAll = !rdr.IsDBNull(10) && rdr.GetBoolean(10)
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

        var hostKey = Clean(app.HostKey);
        if (string.IsNullOrWhiteSpace(hostKey) || HostMatchesCurrentRequest(request, hostKey))
        {
            return request.GetPublicBaseUrl();
        }

        if (Uri.TryCreate(hostKey, UriKind.Absolute, out var absoluteHostKey))
        {
            return absoluteHostKey.GetLeftPart(UriPartial.Authority);
        }

        return null;
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

    private static bool HostMatchesCurrentRequest(HttpRequest request, string hostKey)
    {
        return string.Equals(hostKey, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostKey, request.Host.Value, StringComparison.OrdinalIgnoreCase);
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

    private sealed class TopBarAppEntry
    {
        public Guid AppInstanceId { get; init; }
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
}
