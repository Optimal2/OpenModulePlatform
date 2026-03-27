using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<PortalTopBarService> _log;

    public PortalTopBarService(
        SqlConnectionFactory db,
        RbacService rbac,
        ILogger<PortalTopBarService> log)
    {
        _db = db;
        _rbac = rbac;
        _log = log;
    }

    public Task<PortalTopBarModel> CreateAsync(
        WebAppOptions options,
        HttpRequest request,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var currentUri = BuildCurrentUri(request);
        return CreateInternalAsync(options, user, currentUri, request.Host.Host, ct);
    }

    public Task<PortalTopBarModel> CreateAsync(
        WebAppOptions options,
        Uri currentUri,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        return CreateInternalAsync(options, user, currentUri, currentUri.Host, ct);
    }

    private async Task<PortalTopBarModel> CreateInternalAsync(
        WebAppOptions options,
        ClaimsPrincipal user,
        Uri currentUri,
        string currentHost,
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

        try
        {
            var permissions = await _rbac.GetUserPermissionsAsync(user, ct);
            var apps = await GetEnabledWebAppsAsync(ct);
            var isPortalAdmin = permissions.Contains(PortalAdminPermission);

            var moduleLinks = apps
                .Where(app => HasAccess(app, permissions))
                .Select(app => new PortalTopBarLink(app.DisplayName, ResolveHref(currentUri, currentHost, app)!))
                .Where(link => !string.IsNullOrWhiteSpace(link.Href))
                .ToArray();

            return new PortalTopBarModel
            {
                IsVisible = true,
                PortalLink = portalLink,
                ModuleLinks = moduleLinks,
                Links = [portalLink, .. moduleLinks],
                IsPortalAdmin = isPortalAdmin,
                PortalAdminLinks = isPortalAdmin
                    ? CreatePortalAdminLinks(topBarOptions.PortalBaseUrl)
                    : Array.Empty<PortalTopBarLink>(),
                OverflowToggleTextKey = "More",
                PortalAdminToggleTextKey = "Admin",
                CollapsedToggleTextKey = "Modules"
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to build portal top bar dynamically. Falling back to a portal-only top bar.");

            return new PortalTopBarModel
            {
                IsVisible = true,
                PortalLink = portalLink,
                ModuleLinks = Array.Empty<PortalTopBarLink>(),
                Links = [portalLink],
                IsPortalAdmin = false,
                PortalAdminLinks = Array.Empty<PortalTopBarLink>(),
                OverflowToggleTextKey = "More",
                PortalAdminToggleTextKey = "Admin",
                CollapsedToggleTextKey = "Modules"
            };
        }
    }

    private static Uri BuildCurrentUri(HttpRequest request)
    {
        var baseUrl = request.GetPublicBaseUrl();
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
        var path = request.Path.HasValue ? request.Path.Value : string.Empty;
        var query = request.QueryString.HasValue ? request.QueryString.Value : string.Empty;
        return new Uri($"{baseUrl}{pathBase}{path}{query}", UriKind.Absolute);
    }

    private static PortalTopBarLink[] CreatePortalAdminLinks(string portalBaseUrl)
    {
        return
        [
            new PortalTopBarLink("Admin", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/overview")),
            new PortalTopBarLink("Instances", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/instances")),
            new PortalTopBarLink("Hosts", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/hosts")),
            new PortalTopBarLink("Modules", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/modules")),
            new PortalTopBarLink("Apps", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/apps")),
            new PortalTopBarLink("Artifacts", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/artifacts")),
            new PortalTopBarLink("Automation", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/automation")),
            new PortalTopBarLink("RBAC", PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, "/admin/rbac"))
        ];
    }

    private async Task<IReadOnlyList<PortalTopBarAppEntry>> GetEnabledWebAppsAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        var hasHostBaseUrl = await HostBaseUrlColumnExistsAsync(conn, ct);
        var hostBaseUrlSelect = hasHostBaseUrl
            ? "h.BaseUrl"
            : "CAST(NULL AS nvarchar(300)) AS BaseUrl";

        var sql = $@"
SELECT ai.AppInstanceId,
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
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var apps = new Dictionary<Guid, PortalTopBarAppEntry>();
        while (await reader.ReadAsync(ct))
        {
            var appInstanceId = reader.GetGuid(0);
            if (!apps.TryGetValue(appInstanceId, out var entry))
            {
                entry = new PortalTopBarAppEntry
                {
                    AppInstanceId = appInstanceId,
                    DisplayName = reader.GetString(1),
                    RoutePath = reader.IsDBNull(2) ? null : reader.GetString(2),
                    PublicUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                    HostKey = reader.IsDBNull(4) ? null : reader.GetString(4),
                    HostBaseUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                    SortOrder = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    RequireAll = !reader.IsDBNull(8) && reader.GetBoolean(8)
                };
                apps.Add(appInstanceId, entry);
            }

            if (!reader.IsDBNull(7))
            {
                entry.RequiredPermissions.Add(reader.GetString(7));
            }
        }

        return apps.Values.ToArray();
    }

    private static bool HasAccess(PortalTopBarAppEntry app, HashSet<string> permissions)
    {
        if (app.RequiredPermissions.Count == 0)
        {
            return true;
        }

        return app.RequireAll
            ? app.RequiredPermissions.All(permissions.Contains)
            : app.RequiredPermissions.Any(permissions.Contains);
    }

    private static async Task<bool> HostBaseUrlColumnExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN COL_LENGTH('omp.Hosts', 'BaseUrl') IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static string? ResolveHref(Uri currentUri, string currentHost, PortalTopBarAppEntry app)
    {
        var routePath = Clean(app.RoutePath);
        if (!string.IsNullOrWhiteSpace(routePath))
        {
            if (Uri.TryCreate(routePath, UriKind.Absolute, out var absoluteRoute))
            {
                return absoluteRoute.ToString();
            }

            var hostRoot = ResolveHostRoot(currentUri, currentHost, app);
            return string.IsNullOrWhiteSpace(hostRoot)
                ? null
                : CombineHostRootAndRoute(hostRoot, routePath);
        }

        var publicUrl = Clean(app.PublicUrl);
        return string.IsNullOrWhiteSpace(publicUrl) ? null : publicUrl;
    }

    private static string? ResolveHostRoot(Uri currentUri, string currentHost, PortalTopBarAppEntry app)
    {
        var hostBaseUrl = Clean(app.HostBaseUrl);
        if (!string.IsNullOrWhiteSpace(hostBaseUrl)
            && Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var absoluteBaseUrl))
        {
            return absoluteBaseUrl.GetLeftPart(UriPartial.Authority);
        }

        var hostKey = Clean(app.HostKey);
        if (string.IsNullOrWhiteSpace(hostKey) || HostMatchesCurrentRequest(currentHost, hostKey))
        {
            return currentUri.GetLeftPart(UriPartial.Authority);
        }

        if (Uri.TryCreate(hostKey, UriKind.Absolute, out var absoluteHostKey))
        {
            return absoluteHostKey.GetLeftPart(UriPartial.Authority);
        }

        return null;
    }

    private static bool HostMatchesCurrentRequest(string currentHost, string hostKey)
    {
        return string.Equals(hostKey, currentHost, StringComparison.OrdinalIgnoreCase);
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

    private sealed class PortalTopBarAppEntry
    {
        public Guid AppInstanceId { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string? RoutePath { get; init; }
        public string? PublicUrl { get; init; }
        public string? HostKey { get; init; }
        public string? HostBaseUrl { get; init; }
        public int SortOrder { get; init; }
        public bool RequireAll { get; set; }
        public List<string> RequiredPermissions { get; } = [];
    }
}
