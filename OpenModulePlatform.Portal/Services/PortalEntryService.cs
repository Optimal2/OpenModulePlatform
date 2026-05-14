// File: OpenModulePlatform.Portal/Services/PortalEntryService.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Builds and persists the personalized Portal Entries list.
/// </summary>
public sealed class PortalEntryService
{
    private const string AppEntryPrefix = "app:";
    private const string AppEntrySuffix = ":home";
    private static readonly Regex EntryKeyUnsafeCharacters = new("[^a-z0-9._-]+", RegexOptions.Compiled);

    private readonly SqlConnectionFactory _db;
    private readonly AppCatalogService _catalog;

    public PortalEntryService(SqlConnectionFactory db, AppCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<PortalEntry>> GetEntriesAsync(
        HttpRequest request,
        int? userId,
        IReadOnlySet<string> permissions,
        bool includeHidden,
        CancellationToken ct)
    {
        var rows = await GetRowsAsync(userId, ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var allApps = await _catalog.GetEnabledWebAppsAsync(ct);
        var accessibleApps = _catalog.FilterByPermissions(allApps, permissions)
            .ToDictionary(app => app.AppInstanceId);

        var entries = new List<PortalEntry>();
        foreach (var row in rows)
        {
            if (!row.IsEnabled || (!includeHidden && row.IsHidden))
            {
                continue;
            }

            var href = ResolveTargetHref(request, row, accessibleApps);
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            entries.Add(new PortalEntry
            {
                PortalEntryId = row.PortalEntryId,
                EntryKey = row.EntryKey,
                DisplayName = row.DisplayName,
                Description = row.Description,
                LogoUrl = row.LogoUrl,
                IconKey = row.IconKey,
                TargetHref = href,
                TargetEntryKey = row.TargetEntryKey,
                IsPinned = row.IsPinned,
                IsHidden = row.IsHidden,
                UserSortOrder = row.SortOrder,
                DefaultSortOrder = row.DefaultSortOrder
            });
        }

        return entries;
    }

    public async Task<IReadOnlyList<PortalEntryAdminRow>> GetAdminRowsAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT portal_entry_id,
       entry_key,
       display_name,
       description,
       logo_url,
       icon_key,
       target_url,
       target_entry_key,
       source_app_instance_id,
       is_enabled,
       default_sort_order
FROM omp_portal.portal_entries
ORDER BY default_sort_order,
         display_name;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<PortalEntryAdminRow>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PortalEntryAdminRow
            {
                PortalEntryId = rdr.GetInt32(0),
                EntryKey = rdr.GetString(1),
                DisplayName = rdr.GetString(2),
                Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                LogoUrl = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                IconKey = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                TargetUrl = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                TargetEntryKey = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                SourceAppInstanceId = rdr.IsDBNull(8) ? null : rdr.GetGuid(8),
                IsEnabled = rdr.GetBoolean(9),
                DefaultSortOrder = rdr.GetInt32(10)
            });
        }

        return rows;
    }

    public async Task<PortalEntryAdminRow?> GetAdminRowAsync(int portalEntryId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT portal_entry_id,
       entry_key,
       display_name,
       description,
       logo_url,
       icon_key,
       target_url,
       target_entry_key,
       source_app_instance_id,
       is_enabled,
       default_sort_order
FROM omp_portal.portal_entries
WHERE portal_entry_id = @portal_entry_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new PortalEntryAdminRow
        {
            PortalEntryId = rdr.GetInt32(0),
            EntryKey = rdr.GetString(1),
            DisplayName = rdr.GetString(2),
            Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            LogoUrl = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            IconKey = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            TargetUrl = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            TargetEntryKey = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            SourceAppInstanceId = rdr.IsDBNull(8) ? null : rdr.GetGuid(8),
            IsEnabled = rdr.GetBoolean(9),
            DefaultSortOrder = rdr.GetInt32(10)
        };
    }

    public async Task<int> CreateAsync(PortalEntryCreateData data, CancellationToken ct)
    {
        var entryKey = await CreateUniqueEntryKeyAsync(data.DisplayName, ct);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
INSERT INTO omp_portal.portal_entries
(
    entry_key,
    display_name,
    description,
    logo_url,
    icon_key,
    target_url,
    target_entry_key,
    is_enabled,
    default_sort_order
)
OUTPUT INSERTED.portal_entry_id
VALUES
(
    @entry_key,
    @display_name,
    @description,
    @logo_url,
    @icon_key,
    @target_url,
    @target_entry_key,
    @is_enabled,
    @default_sort_order
);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@entry_key", SqlDbType.NVarChar, 200).Value = entryKey;
        cmd.Parameters.Add("@display_name", SqlDbType.NVarChar, 200).Value = data.DisplayName.Trim();
        cmd.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value = Clean(data.Description) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@logo_url", SqlDbType.NVarChar, 600).Value = Clean(data.LogoUrl) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@icon_key", SqlDbType.NVarChar, 100).Value = Clean(data.IconKey) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@target_url", SqlDbType.NVarChar, 600).Value = Clean(data.TargetUrl) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@target_entry_key", SqlDbType.NVarChar, 200).Value = Clean(data.TargetEntryKey) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = data.IsEnabled;
        cmd.Parameters.Add("@default_sort_order", SqlDbType.Int).Value = data.DefaultSortOrder;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<bool> UpdateAsync(PortalEntryEditData data, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
UPDATE omp_portal.portal_entries
SET display_name = @display_name,
    description = @description,
    logo_url = @logo_url,
    icon_key = @icon_key,
    target_url = @target_url,
    target_entry_key = @target_entry_key,
    is_enabled = @is_enabled,
    default_sort_order = @default_sort_order,
    updated_at = SYSUTCDATETIME()
WHERE portal_entry_id = @portal_entry_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = data.PortalEntryId;
        cmd.Parameters.Add("@display_name", SqlDbType.NVarChar, 200).Value = data.DisplayName.Trim();
        cmd.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value = Clean(data.Description) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@logo_url", SqlDbType.NVarChar, 600).Value = Clean(data.LogoUrl) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@icon_key", SqlDbType.NVarChar, 100).Value = Clean(data.IconKey) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@target_url", SqlDbType.NVarChar, 600).Value = Clean(data.TargetUrl) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@target_entry_key", SqlDbType.NVarChar, 200).Value = Clean(data.TargetEntryKey) ?? (object)DBNull.Value;
        cmd.Parameters.Add("@is_enabled", SqlDbType.Bit).Value = data.IsEnabled;
        cmd.Parameters.Add("@default_sort_order", SqlDbType.Int).Value = data.DefaultSortOrder;
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task SetPinnedAsync(int userId, int portalEntryId, bool isPinned, CancellationToken ct)
    {
        var sortOrder = isPinned
            ? await GetNextPinnedSortOrderAsync(userId, ct)
            : (int?)null;

        const string sql = @"
MERGE omp_portal.portal_user_entry_state AS target
USING (SELECT @user_id AS user_id, @portal_entry_id AS portal_entry_id) AS source
    ON target.user_id = source.user_id
   AND target.portal_entry_id = source.portal_entry_id
WHEN MATCHED THEN
    UPDATE SET is_pinned = @is_pinned,
               sort_order = @sort_order,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(user_id, portal_entry_id, is_pinned, is_hidden, sort_order, updated_at)
    VALUES(@user_id, @portal_entry_id, @is_pinned, 0, @sort_order, SYSUTCDATETIME());";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
        cmd.Parameters.Add("@is_pinned", SqlDbType.Bit).Value = isPinned;
        cmd.Parameters.Add("@sort_order", SqlDbType.Int).Value = sortOrder.HasValue ? sortOrder.Value : DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetHiddenAsync(int userId, int portalEntryId, bool isHidden, CancellationToken ct)
    {
        const string sql = @"
MERGE omp_portal.portal_user_entry_state AS target
USING (SELECT @user_id AS user_id, @portal_entry_id AS portal_entry_id) AS source
    ON target.user_id = source.user_id
   AND target.portal_entry_id = source.portal_entry_id
WHEN MATCHED THEN
    UPDATE SET is_hidden = @is_hidden,
               is_pinned = CASE WHEN @is_hidden = 1 THEN 0 ELSE is_pinned END,
               sort_order = CASE WHEN @is_hidden = 1 THEN NULL ELSE sort_order END,
               updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(user_id, portal_entry_id, is_pinned, is_hidden, sort_order, updated_at)
    VALUES(@user_id, @portal_entry_id, 0, @is_hidden, NULL, SYSUTCDATETIME());";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryId;
        cmd.Parameters.Add("@is_hidden", SqlDbType.Bit).Value = isHidden;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdatePinnedSortOrderAsync(int userId, IReadOnlyList<int> portalEntryIds, CancellationToken ct)
    {
        if (portalEntryIds.Count == 0)
        {
            return;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            const string sql = @"
UPDATE omp_portal.portal_user_entry_state
SET sort_order = @sort_order,
    updated_at = SYSUTCDATETIME()
WHERE user_id = @user_id
  AND portal_entry_id = @portal_entry_id
  AND is_pinned = 1
  AND is_hidden = 0;";

            for (var i = 0; i < portalEntryIds.Count; i++)
            {
                await using var cmd = new SqlCommand(sql, conn, tx);
                cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
                cmd.Parameters.Add("@portal_entry_id", SqlDbType.Int).Value = portalEntryIds[i];
                cmd.Parameters.Add("@sort_order", SqlDbType.Int).Value = (i + 1) * 10;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<int> GetNextPinnedSortOrderAsync(int userId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT COALESCE(MAX(sort_order), 0) + 10
FROM omp_portal.portal_user_entry_state
WHERE user_id = @user_id
  AND is_pinned = 1
  AND is_hidden = 0;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string> CreateUniqueEntryKeyAsync(string displayName, CancellationToken ct)
    {
        var baseKey = "custom:" + ToKeySlug(displayName);
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        for (var i = 0; i < 100; i++)
        {
            var candidate = i == 0
                ? baseKey
                : $"{baseKey}-{i + 1}";

            const string sql = "SELECT COUNT(1) FROM omp_portal.portal_entries WHERE entry_key = @entry_key;";
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@entry_key", SqlDbType.NVarChar, 200).Value = candidate;
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (count == 0)
            {
                return candidate;
            }
        }

        return $"{baseKey}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
    }

    private static string ToKeySlug(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        var slug = EntryKeyUnsafeCharacters.Replace(builder.ToString().Normalize(NormalizationForm.FormC), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "portal-entry" : slug[..Math.Min(slug.Length, 180)];
    }

    private async Task<IReadOnlyList<PortalEntryRow>> GetRowsAsync(int? userId, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT pe.portal_entry_id,
       pe.entry_key,
       pe.display_name,
       pe.description,
       pe.logo_url,
       pe.icon_key,
       pe.target_url,
       pe.target_entry_key,
       pe.source_app_instance_id,
       pe.is_enabled,
       pe.default_sort_order,
       COALESCE(us.is_pinned, CAST(0 AS bit)) AS is_pinned,
       COALESCE(us.is_hidden, CAST(0 AS bit)) AS is_hidden,
       us.sort_order
FROM omp_portal.portal_entries pe
LEFT JOIN omp_portal.portal_user_entry_state us
    ON us.portal_entry_id = pe.portal_entry_id
   AND us.user_id = @user_id
ORDER BY pe.default_sort_order,
         pe.display_name;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId.HasValue ? userId.Value : DBNull.Value;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<PortalEntryRow>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PortalEntryRow
            {
                PortalEntryId = rdr.GetInt32(0),
                EntryKey = rdr.GetString(1),
                DisplayName = rdr.GetString(2),
                Description = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                LogoUrl = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                IconKey = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                TargetUrl = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                TargetEntryKey = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                SourceAppInstanceId = rdr.IsDBNull(8) ? null : rdr.GetGuid(8),
                IsEnabled = rdr.GetBoolean(9),
                DefaultSortOrder = rdr.GetInt32(10),
                IsPinned = rdr.GetBoolean(11),
                IsHidden = rdr.GetBoolean(12),
                SortOrder = rdr.IsDBNull(13) ? null : rdr.GetInt32(13)
            });
        }

        return rows;
    }

    private static string? ResolveTargetHref(
        HttpRequest request,
        PortalEntryRow row,
        IReadOnlyDictionary<Guid, PortalAppEntry> accessibleApps)
    {
        if (row.SourceAppInstanceId is Guid sourceAppInstanceId
            && accessibleApps.TryGetValue(sourceAppInstanceId, out var sourceApp))
        {
            return AppLinkBuilder.ResolveHref(request, sourceApp);
        }

        if (TryParseAppInstanceId(row.TargetEntryKey, out var targetAppInstanceId)
            && accessibleApps.TryGetValue(targetAppInstanceId, out var targetApp))
        {
            return AppLinkBuilder.ResolveHref(request, targetApp);
        }

        return ResolveTargetUrl(request, row.TargetUrl);
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

        var publicRoot = request.GetPublicBaseUrl().TrimEnd('/');
        return $"{publicRoot}/{trimmed.TrimStart('/')}";
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

    private sealed class PortalEntryRow
    {
        public int PortalEntryId { get; set; }

        public string EntryKey { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? LogoUrl { get; set; }

        public string? IconKey { get; set; }

        public string? TargetUrl { get; set; }

        public string? TargetEntryKey { get; set; }

        public Guid? SourceAppInstanceId { get; set; }

        public bool IsEnabled { get; set; }

        public int DefaultSortOrder { get; set; }

        public bool IsPinned { get; set; }

        public bool IsHidden { get; set; }

        public int? SortOrder { get; set; }
    }
}
