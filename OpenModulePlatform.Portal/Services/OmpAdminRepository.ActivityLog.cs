// File: OpenModulePlatform.Portal/Services/OmpAdminRepository.ActivityLog.cs
using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.ActivityLog;

namespace OpenModulePlatform.Portal.Services;

/// <summary>A module whose schema has an ActivityLog table the viewer can read.</summary>
public sealed record ActivityLogModule(string ModuleKey, string DisplayName, string SchemaName);

/// <summary>An OMP user for the viewer's user filter.</summary>
public sealed record ActivityLogUser(int UserId, string DisplayName);

public sealed class ActivityLogFilter
{
    public int? UserId { get; init; }

    /// <summary>Module keys to include; empty means every module with a table.</summary>
    public IReadOnlyList<string> ModuleKeys { get; init; } = [];

    public DateTime? FromUtc { get; init; }

    /// <summary>Exclusive upper bound.</summary>
    public DateTime? ToUtc { get; init; }

    /// <summary>Substring matched against the envelope's summary and event key.</summary>
    public string? Text { get; init; }

    public int Take { get; init; } = 200;
}

public sealed class ActivityLogRow
{
    public long ActivityLogId { get; init; }
    public string ModuleKey { get; init; } = string.Empty;
    public string ModuleDisplayName { get; init; } = string.Empty;
    public DateTime LoggedUtc { get; init; }
    public int? OmpUserId { get; init; }
    public string? UserDisplayName { get; init; }
    public string Entry { get; init; } = string.Empty;
}

public sealed partial class OmpAdminRepository
{
    private const int MaxActivityLogTake = 1000;

    /// <summary>
    /// Every enabled module whose schema has an ActivityLog table. Modules opt in
    /// simply by creating the table in their setup SQL; nothing is registered.
    /// </summary>
    public async Task<IReadOnlyList<ActivityLogModule>> GetActivityLogModulesAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT m.ModuleKey, m.DisplayName, m.SchemaName
FROM omp.Modules m
WHERE m.IsEnabled = 1
  AND m.SchemaName IS NOT NULL
  AND m.SchemaName <> N''
  AND OBJECT_ID(QUOTENAME(m.SchemaName) + N'.' + QUOTENAME(N'" + ActivityLogSql.TableName + @"'), N'U') IS NOT NULL
ORDER BY m.SortOrder, m.DisplayName;";

        var rows = new List<ActivityLogModule>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var schema = rdr.GetString(2);
            if (!IsPlainIdentifier(schema))
            {
                continue;
            }

            rows.Add(new ActivityLogModule(rdr.GetString(0), rdr.GetString(1), schema));
        }

        return rows;
    }

    public async Task<IReadOnlyList<ActivityLogUser>> GetActivityLogUsersAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT u.user_id, u.display_name
FROM omp.users u
ORDER BY u.display_name, u.user_id;";

        var rows = new List<ActivityLogUser>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ActivityLogUser(rdr.GetInt32(0), rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1)));
        }

        return rows;
    }

    /// <summary>
    /// The newest entries across the given modules, one UNION ALL over their
    /// ActivityLog tables (the same technique as the artifact retention preview),
    /// each branch cut to the requested size before the union so a busy module
    /// cannot make the query read everything.
    /// </summary>
    public async Task<IReadOnlyList<ActivityLogRow>> SearchActivityLogAsync(
        IReadOnlyList<ActivityLogModule> modules,
        ActivityLogFilter filter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(filter);

        var selected = filter.ModuleKeys.Count == 0
            ? modules
            : modules.Where(m => filter.ModuleKeys.Contains(m.ModuleKey, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            return [];
        }

        var take = Math.Clamp(filter.Take, 1, MaxActivityLogTake);
        var where = new StringBuilder();
        if (filter.UserId.HasValue)
        {
            where.Append(" AND a.OmpUserId = @UserId");
        }

        if (filter.FromUtc.HasValue)
        {
            where.Append(" AND a.LoggedUtc >= @FromUtc");
        }

        if (filter.ToUtc.HasValue)
        {
            where.Append(" AND a.LoggedUtc < @ToUtc");
        }

        var hasText = !string.IsNullOrWhiteSpace(filter.Text);
        if (hasText)
        {
            where.Append(" AND (JSON_VALUE(a.Entry, '$.summary') LIKE @Text ESCAPE '\\' OR JSON_VALUE(a.Entry, '$.event') LIKE @Text ESCAPE '\\')");
        }

        var sql = new StringBuilder();
        sql.Append("SELECT TOP (@Take) x.ActivityLogId, x.ModuleKey, x.LoggedUtc, x.OmpUserId, u.display_name, x.Entry\nFROM (\n");
        for (var i = 0; i < selected.Count; i++)
        {
            var module = selected[i];
            if (i > 0)
            {
                sql.Append("    UNION ALL\n");
            }

            sql.Append("    SELECT TOP (@Take) a.ActivityLogId, @Module").Append(i).Append(" AS ModuleKey, a.LoggedUtc, a.OmpUserId, a.Entry\n");
            sql.Append("    FROM [").Append(module.SchemaName).Append("].[").Append(ActivityLogSql.TableName).Append("] a\n");
            sql.Append("    WHERE 1 = 1").Append(where).Append('\n');
            sql.Append("    ORDER BY a.LoggedUtc DESC, a.ActivityLogId DESC\n");
        }

        sql.Append(") x\nLEFT JOIN omp.users u ON u.user_id = x.OmpUserId\nORDER BY x.LoggedUtc DESC, x.ActivityLogId DESC;");

        var displayNames = selected.ToDictionary(m => m.ModuleKey, m => m.DisplayName, StringComparer.OrdinalIgnoreCase);
        var rows = new List<ActivityLogRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql.ToString(), conn);
        cmd.Parameters.Add(new SqlParameter("@Take", SqlDbType.Int) { Value = take });
        for (var i = 0; i < selected.Count; i++)
        {
            cmd.Parameters.Add(new SqlParameter("@Module" + i, SqlDbType.NVarChar, 128) { Value = selected[i].ModuleKey });
        }

        if (filter.UserId.HasValue)
        {
            cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = filter.UserId.Value });
        }

        if (filter.FromUtc.HasValue)
        {
            cmd.Parameters.Add(new SqlParameter("@FromUtc", SqlDbType.DateTime2) { Value = filter.FromUtc.Value });
        }

        if (filter.ToUtc.HasValue)
        {
            cmd.Parameters.Add(new SqlParameter("@ToUtc", SqlDbType.DateTime2) { Value = filter.ToUtc.Value });
        }

        if (hasText)
        {
            cmd.Parameters.Add(new SqlParameter("@Text", SqlDbType.NVarChar, 400) { Value = "%" + EscapeLike(filter.Text!.Trim()) + "%" });
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var moduleKey = rdr.GetString(1);
            rows.Add(new ActivityLogRow
            {
                ActivityLogId = rdr.GetInt64(0),
                ModuleKey = moduleKey,
                ModuleDisplayName = displayNames.TryGetValue(moduleKey, out var name) ? name : moduleKey,
                LoggedUtc = DateTime.SpecifyKind(rdr.GetDateTime(2), DateTimeKind.Utc),
                OmpUserId = rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
                UserDisplayName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                Entry = rdr.GetString(5)
            });
        }

        return rows;
    }

    private static bool IsPlainIdentifier(string value)
        => !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
