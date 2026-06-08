using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;

namespace OpenModulePlatform.Portal.Services;

public sealed class IFrameAdminService
{
    private readonly SqlConnectionFactory _db;

    public IFrameAdminService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IFrameAdminData> GetAdminDataAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await HasIFrameTablesAsync(conn, ct))
        {
            return new IFrameAdminData(false, [], []);
        }

        var urls = await GetUrlsAsync(conn, ct);
        var sets = await GetUrlSetsAsync(conn, ct);
        return new IFrameAdminData(true, urls, sets);
    }

    public async Task<int> CreateUrlAsync(IFrameUrlCreateRequest request, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await EnsureIFrameTablesAsync(conn, ct);

        const string sql = @"
INSERT INTO omp_iframe.urls([url], [displayname], [allowed_roles], [enabled])
OUTPUT INSERTED.[id]
VALUES(@url, @displayname, @allowed_roles, @enabled);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@url", SqlDbType.NVarChar, 500).Value = request.Url.Trim();
        cmd.Parameters.Add("@displayname", SqlDbType.NVarChar, 200).Value = request.DisplayName.Trim();
        cmd.Parameters.Add("@allowed_roles", SqlDbType.NVarChar, 500).Value =
            string.IsNullOrWhiteSpace(request.AllowedRoles) ? DBNull.Value : request.AllowedRoles.Trim();
        cmd.Parameters.Add("@enabled", SqlDbType.Bit).Value = request.Enabled;

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task SetUrlEnabledAsync(int urlId, bool enabled, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await EnsureIFrameTablesAsync(conn, ct);

        const string sql = "UPDATE omp_iframe.urls SET [enabled] = @enabled WHERE [id] = @id;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = urlId;
        cmd.Parameters.Add("@enabled", SqlDbType.Bit).Value = enabled;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CreateUrlSetAsync(IFrameUrlSetCreateRequest request, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await EnsureIFrameTablesAsync(conn, ct);

        var setKey = request.SetKey.Trim();
        if (await UrlSetKeyExistsAsync(conn, setKey, ct))
        {
            throw new InvalidOperationException("An iFrame URL set with this key already exists.");
        }

        var requestedUrlIds = request.UrlIds
            .Where(urlId => urlId > 0)
            .Distinct()
            .ToArray();

        var validUrlIds = await GetExistingUrlIdsAsync(conn, requestedUrlIds, ct);
        if (validUrlIds.Count != requestedUrlIds.Length)
        {
            throw new InvalidOperationException("Select valid iFrame URLs.");
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            const string insertSetSql = @"
INSERT INTO omp_iframe.url_sets([set_key], [displayname], [enabled])
OUTPUT INSERTED.[id]
VALUES(@set_key, @displayname, @enabled);";

            await using var insertSetCmd = new SqlCommand(insertSetSql, conn, (SqlTransaction)tx);
            insertSetCmd.Parameters.Add("@set_key", SqlDbType.NVarChar, 100).Value = setKey;
            insertSetCmd.Parameters.Add("@displayname", SqlDbType.NVarChar, 200).Value = request.DisplayName.Trim();
            insertSetCmd.Parameters.Add("@enabled", SqlDbType.Bit).Value = request.Enabled;

            var setId = Convert.ToInt32(await insertSetCmd.ExecuteScalarAsync(ct));

            const string insertMemberSql = @"
INSERT INTO omp_iframe.url_set_urls([url_set_id], [url_id], [sort_order])
VALUES(@url_set_id, @url_id, @sort_order);";

            for (var index = 0; index < requestedUrlIds.Length; index++)
            {
                await using var insertMemberCmd = new SqlCommand(insertMemberSql, conn, (SqlTransaction)tx);
                insertMemberCmd.Parameters.Add("@url_set_id", SqlDbType.Int).Value = setId;
                insertMemberCmd.Parameters.Add("@url_id", SqlDbType.Int).Value = requestedUrlIds[index];
                insertMemberCmd.Parameters.Add("@sort_order", SqlDbType.Int).Value = (index + 1) * 10;
                await insertMemberCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return setId;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SetUrlSetEnabledAsync(int urlSetId, bool enabled, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await EnsureIFrameTablesAsync(conn, ct);

        const string sql = "UPDATE omp_iframe.url_sets SET [enabled] = @enabled WHERE [id] = @id;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = urlSetId;
        cmd.Parameters.Add("@enabled", SqlDbType.Bit).Value = enabled;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<IFrameUrlAdminRow>> GetUrlsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT [id],
       [displayname],
       [url],
       [allowed_roles],
       [enabled]
FROM omp_iframe.urls
ORDER BY [displayname], [id];";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<IFrameUrlAdminRow>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new IFrameUrlAdminRow(
                rdr.GetInt32(0),
                rdr.GetString(1),
                rdr.GetString(2),
                rdr.IsDBNull(3) ? null : rdr.GetString(3),
                rdr.GetBoolean(4)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<IFrameUrlSetAdminRow>> GetUrlSetsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string setSql = @"
SELECT [id],
       [set_key],
       [displayname],
       [enabled]
FROM omp_iframe.url_sets
ORDER BY [displayname], [id];";

        await using var setCmd = new SqlCommand(setSql, conn);
        await using var setRdr = await setCmd.ExecuteReaderAsync(ct);

        var sets = new List<IFrameUrlSetAdminRow>();
        while (await setRdr.ReadAsync(ct))
        {
            sets.Add(new IFrameUrlSetAdminRow(
                setRdr.GetInt32(0),
                setRdr.GetString(1),
                setRdr.GetString(2),
                setRdr.GetBoolean(3),
                []));
        }

        await setRdr.CloseAsync();

        const string memberSql = @"
SELECT usu.[url_set_id],
       u.[id],
       u.[displayname],
       u.[url],
       u.[enabled],
       usu.[sort_order]
FROM omp_iframe.url_set_urls usu
INNER JOIN omp_iframe.urls u ON u.[id] = usu.[url_id]
ORDER BY usu.[url_set_id], usu.[sort_order], u.[displayname], u.[id];";

        await using var memberCmd = new SqlCommand(memberSql, conn);
        await using var memberRdr = await memberCmd.ExecuteReaderAsync(ct);

        var membersBySetId = new Dictionary<int, List<IFrameUrlSetMemberRow>>();
        while (await memberRdr.ReadAsync(ct))
        {
            var setId = memberRdr.GetInt32(0);
            if (!membersBySetId.TryGetValue(setId, out var members))
            {
                members = [];
                membersBySetId.Add(setId, members);
            }

            members.Add(new IFrameUrlSetMemberRow(
                memberRdr.GetInt32(1),
                memberRdr.GetString(2),
                memberRdr.GetString(3),
                memberRdr.GetBoolean(4),
                memberRdr.GetInt32(5)));
        }

        return sets
            .Select(row => row with
            {
                Urls = membersBySetId.TryGetValue(row.UrlSetId, out var members)
                    ? members
                    : []
            })
            .ToArray();
    }

    private static async Task<bool> UrlSetKeyExistsAsync(SqlConnection conn, string setKey, CancellationToken ct)
    {
        const string sql = "SELECT COUNT(1) FROM omp_iframe.url_sets WHERE [set_key] = @set_key;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@set_key", SqlDbType.NVarChar, 100).Value = setKey;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<HashSet<int>> GetExistingUrlIdsAsync(SqlConnection conn, IReadOnlyList<int> urlIds, CancellationToken ct)
    {
        if (urlIds.Count == 0)
        {
            return [];
        }

        var parameterNames = urlIds
            .Select((_, index) => "@url_id_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        var sql = "SELECT [id] FROM omp_iframe.urls WHERE [id] IN (" + string.Join(", ", parameterNames) + ");";
        await using var cmd = new SqlCommand(sql, conn);
        for (var index = 0; index < urlIds.Count; index++)
        {
            cmd.Parameters.Add(parameterNames[index], SqlDbType.Int).Value = urlIds[index];
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var ids = new HashSet<int>();
        while (await rdr.ReadAsync(ct))
        {
            ids.Add(rdr.GetInt32(0));
        }

        return ids;
    }

    private static async Task EnsureIFrameTablesAsync(SqlConnection conn, CancellationToken ct)
    {
        if (!await HasIFrameTablesAsync(conn, ct))
        {
            throw new InvalidOperationException("The iFrame URL tables are not installed.");
        }
    }

    private static async Task<bool> HasIFrameTablesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(CASE
    WHEN OBJECT_ID(N'omp_iframe.urls', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp_iframe.url_sets', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp_iframe.url_set_urls', N'U') IS NOT NULL
    THEN 1 ELSE 0 END AS bit);";

        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }
}

public sealed record IFrameAdminData(
    bool HasIFrameTables,
    IReadOnlyList<IFrameUrlAdminRow> Urls,
    IReadOnlyList<IFrameUrlSetAdminRow> UrlSets);

public sealed record IFrameUrlAdminRow(
    int UrlId,
    string DisplayName,
    string Url,
    string? AllowedRoles,
    bool Enabled);

public sealed record IFrameUrlSetAdminRow(
    int UrlSetId,
    string SetKey,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<IFrameUrlSetMemberRow> Urls);

public sealed record IFrameUrlSetMemberRow(
    int UrlId,
    string DisplayName,
    string Url,
    bool Enabled,
    int SortOrder);

public sealed record IFrameUrlCreateRequest(
    string DisplayName,
    string Url,
    string? AllowedRoles,
    bool Enabled);

public sealed record IFrameUrlSetCreateRequest(
    string SetKey,
    string DisplayName,
    IReadOnlyList<int> UrlIds,
    bool Enabled);
