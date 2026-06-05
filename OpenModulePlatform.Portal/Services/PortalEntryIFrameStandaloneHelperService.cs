using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Services;

public sealed class PortalEntryIFrameStandaloneHelperService
{
    private readonly SqlConnectionFactory _db;

    public PortalEntryIFrameStandaloneHelperService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<PortalEntryIFrameStandaloneHelperOptions> GetOptionsAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await HasIFrameTablesAsync(conn, ct))
        {
            return new PortalEntryIFrameStandaloneHelperOptions([], []);
        }

        var apps = await GetAppOptionsAsync(conn, ct);
        var urls = await GetUrlOptionsAsync(conn, ct);

        return new PortalEntryIFrameStandaloneHelperOptions(apps, urls);
    }

    public async Task<string?> BuildStandaloneTargetUrlAsync(
        Guid appInstanceId,
        int urlId,
        CancellationToken ct)
    {
        var options = await GetOptionsAsync(ct);
        var app = options.Apps.FirstOrDefault(option => option.AppInstanceId == appInstanceId);
        var url = options.Urls.FirstOrDefault(option => option.UrlId == urlId);

        return app is null || url is null
            ? null
            : BuildStandaloneTargetUrl(app.BasePath, url.UrlId);
    }

    public static PortalEntryIFrameStandaloneSelection? ResolveSelection(
        PortalEntryIFrameStandaloneHelperOptions options,
        string? targetUrl)
    {
        var trimmed = targetUrl?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        foreach (var app in options.Apps)
        {
            var prefix = BuildStandaloneTargetUrlPrefix(app.BasePath);
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = trimmed[prefix.Length..].Trim('/');
            if (int.TryParse(suffix, out var urlId)
                && options.Urls.Any(option => option.UrlId == urlId))
            {
                return new PortalEntryIFrameStandaloneSelection(app.AppInstanceId, urlId);
            }
        }

        return null;
    }

    private static async Task<bool> HasIFrameTablesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(CASE
    WHEN OBJECT_ID(N'omp_iframe.urls', N'U') IS NOT NULL
    THEN 1 ELSE 0 END AS bit);";

        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<IReadOnlyList<PortalEntryIFrameStandaloneAppOption>> GetAppOptionsAsync(
        SqlConnection conn,
        CancellationToken ct)
    {
        const string sql = @"
SELECT ai.AppInstanceId,
       ai.AppInstanceKey,
       ai.DisplayName,
       ai.RoutePath
FROM omp.AppInstances ai
INNER JOIN omp.Apps a ON a.AppId = ai.AppId
INNER JOIN omp.Modules m ON m.ModuleId = a.ModuleId
WHERE ai.IsEnabled = 1
  AND ai.IsAllowed = 1
  AND a.AppType = N'WebApp'
  AND (
      m.ModuleKey = N'iframe_webapp'
      OR a.AppKey = N'iframe_webapp_webapp'
      OR ai.AppInstanceKey = N'iframe_webapp_webapp'
  )
ORDER BY ai.SortOrder,
         ai.DisplayName;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<PortalEntryIFrameStandaloneAppOption>();
        while (await rdr.ReadAsync(ct))
        {
            var routePath = rdr.IsDBNull(3) ? null : rdr.GetString(3);
            var basePath = BuildInternalBasePath(routePath);
            if (basePath is null)
            {
                continue;
            }

            rows.Add(new PortalEntryIFrameStandaloneAppOption(
                rdr.GetGuid(0),
                rdr.GetString(1),
                rdr.GetString(2),
                basePath));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PortalEntryIFrameStandaloneUrlOption>> GetUrlOptionsAsync(
        SqlConnection conn,
        CancellationToken ct)
    {
        const string sql = @"
SELECT u.[id],
       u.[displayname]
FROM omp_iframe.urls u
WHERE u.[enabled] = 1
ORDER BY u.[displayname],
         u.[id];";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<PortalEntryIFrameStandaloneUrlOption>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PortalEntryIFrameStandaloneUrlOption(
                rdr.GetInt32(0),
                rdr.GetString(1)));
        }

        return rows;
    }

    private static string? BuildInternalBasePath(string? routePath)
    {
        var trimmed = routePath?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return null;
        }

        var normalized = trimmed.Trim('/');
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : "/" + normalized;
    }

    private static string BuildStandaloneTargetUrl(string basePath, int urlId)
        => $"{basePath.TrimEnd('/')}/standalone/{urlId}";

    private static string BuildStandaloneTargetUrlPrefix(string basePath)
        => $"{basePath.TrimEnd('/')}/standalone/";
}
