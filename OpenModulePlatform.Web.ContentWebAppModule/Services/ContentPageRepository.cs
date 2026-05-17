// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ContentPageRepository.cs
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed class ContentPageRepository
{
    private readonly SqlConnectionFactory _db;

    public ContentPageRepository(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ContentPageListRow>> ListReadablePagesAsync(
        Guid appInstanceId,
        IReadOnlyCollection<int> roleIds,
        bool canManageAll,
        CancellationToken ct)
    {
        var roleIdParameters = NormalizeRoleIds(roleIds);
        var accessFilter = canManageAll
            ? string.Empty
            : $@"
  AND EXISTS
  (
      SELECT 1
      FROM omp_content.content_role_access a
      WHERE a.content_id = c.content_id
        AND a.role_id IN ({BuildRoleParameterList(roleIdParameters)})
        AND a.can_read = 1
  )";

        var sql = $@"
SELECT c.content_id,
       c.slug,
       c.title,
       c.content_type,
       c.server_report_key,
       c.is_enabled,
       c.sort_order,
       c.created_at,
       c.updated_at,
       c.updated_by,
       AccessSummary = STUFF(
       (
           SELECT N', ' + r.Name +
                  CASE
                      WHEN a.can_write = 1 THEN N' (read/write)'
                      WHEN a.can_read = 1 THEN N' (read)'
                      ELSE N''
                  END
           FROM omp_content.content_role_access a
           INNER JOIN omp.Roles r ON r.RoleId = a.role_id
           WHERE a.content_id = c.content_id
             AND (a.can_read = 1 OR a.can_write = 1)
           ORDER BY r.Name
           FOR XML PATH(''), TYPE
       ).value('.', 'nvarchar(max)'), 1, 2, N'')
FROM omp_content.contents c
WHERE c.app_instance_id = @AppInstanceId
  AND c.is_enabled = 1
{accessFilter}
ORDER BY COALESCE(c.sort_order, 2147483647), c.slug, c.title;";

        var rows = new List<ContentPageListRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@AppInstanceId", appInstanceId);
        AddRoleParameters(cmd, roleIdParameters);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(ReadListRow(rdr));
        }

        return rows;
    }

    public async Task<IReadOnlyList<ContentPageListRow>> ListEditablePagesAsync(
        Guid appInstanceId,
        IReadOnlyCollection<int> roleIds,
        bool canManageAll,
        CancellationToken ct)
    {
        var roleIdParameters = NormalizeRoleIds(roleIds);
        var accessFilter = canManageAll
            ? string.Empty
            : $@"
  AND EXISTS
  (
      SELECT 1
      FROM omp_content.content_role_access a
      WHERE a.content_id = c.content_id
        AND a.role_id IN ({BuildRoleParameterList(roleIdParameters)})
        AND a.can_write = 1
  )";

        var sql = $@"
SELECT c.content_id,
       c.slug,
       c.title,
       c.content_type,
       c.server_report_key,
       c.is_enabled,
       c.sort_order,
       c.created_at,
       c.updated_at,
       c.updated_by,
       AccessSummary = STUFF(
       (
           SELECT N', ' + r.Name +
                  CASE
                      WHEN a.can_write = 1 THEN N' (read/write)'
                      WHEN a.can_read = 1 THEN N' (read)'
                      ELSE N''
                  END
           FROM omp_content.content_role_access a
           INNER JOIN omp.Roles r ON r.RoleId = a.role_id
           WHERE a.content_id = c.content_id
             AND (a.can_read = 1 OR a.can_write = 1)
           ORDER BY r.Name
           FOR XML PATH(''), TYPE
       ).value('.', 'nvarchar(max)'), 1, 2, N'')
FROM omp_content.contents c
WHERE c.app_instance_id = @AppInstanceId
{accessFilter}
ORDER BY COALESCE(c.sort_order, 2147483647), c.slug, c.title;";

        var rows = new List<ContentPageListRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@AppInstanceId", appInstanceId);
        AddRoleParameters(cmd, roleIdParameters);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(ReadListRow(rdr));
        }

        return rows;
    }

    public async Task<ContentPageRenderRow?> GetReadablePageBySlugAsync(
        Guid appInstanceId,
        string slug,
        IReadOnlyCollection<int> roleIds,
        bool canManageAll,
        CancellationToken ct)
    {
        var roleIdParameters = NormalizeRoleIds(roleIds);
        var accessFilter = canManageAll
            ? string.Empty
            : $@"
  AND EXISTS
  (
      SELECT 1
      FROM omp_content.content_role_access a
      WHERE a.content_id = c.content_id
        AND a.role_id IN ({BuildRoleParameterList(roleIdParameters)})
        AND a.can_read = 1
  )";

        var sql = $@"
SELECT c.content_id,
       c.slug,
       c.title,
       c.content_type,
       c.body,
       c.server_report_key,
       c.updated_at
FROM omp_content.contents c
WHERE c.app_instance_id = @AppInstanceId
  AND c.slug = @Slug
  AND c.is_enabled = 1
{accessFilter};";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@AppInstanceId", appInstanceId);
        Add(cmd, "@Slug", slug);
        AddRoleParameters(cmd, roleIdParameters);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        return await rdr.ReadAsync(ct) ? ReadRenderRow(rdr) : null;
    }

    public async Task<ContentPageEditRow?> GetPageForEditAsync(
        Guid appInstanceId,
        Guid contentId,
        IReadOnlyCollection<int> roleIds,
        bool canManageAll,
        CancellationToken ct)
    {
        var roleIdParameters = NormalizeRoleIds(roleIds);
        var accessFilter = canManageAll
            ? string.Empty
            : $@"
  AND EXISTS
  (
      SELECT 1
      FROM omp_content.content_role_access a
      WHERE a.content_id = c.content_id
        AND a.role_id IN ({BuildRoleParameterList(roleIdParameters)})
        AND a.can_write = 1
  )";

        var sql = $@"
SELECT c.content_id,
       c.app_instance_id,
       c.slug,
       c.title,
       c.content_type,
       c.body,
       c.server_report_key,
       c.is_enabled,
       c.sort_order,
       c.created_at,
       c.created_by,
       c.updated_at,
       c.updated_by
FROM omp_content.contents c
WHERE c.app_instance_id = @AppInstanceId
  AND c.content_id = @ContentId
{accessFilter};";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@AppInstanceId", appInstanceId);
        Add(cmd, "@ContentId", contentId);
        AddRoleParameters(cmd, roleIdParameters);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        return await rdr.ReadAsync(ct) ? ReadEditRow(rdr) : null;
    }

    public async Task<bool> ContentExistsAsync(Guid appInstanceId, Guid contentId, CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM omp_content.contents
WHERE app_instance_id = @AppInstanceId
  AND content_id = @ContentId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@AppInstanceId", appInstanceId);
        Add(cmd, "@ContentId", contentId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task<IReadOnlyList<ContentRoleAccessRow>> ListRoleAccessAsync(
        Guid contentId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT r.RoleId,
       r.Name,
       CAST(ISNULL(a.can_read, 0) AS bit) AS can_read,
       CAST(ISNULL(a.can_write, 0) AS bit) AS can_write
FROM omp.Roles r
LEFT JOIN omp_content.content_role_access a
    ON a.role_id = r.RoleId
   AND a.content_id = @ContentId
ORDER BY r.Name,
         r.RoleId;";

        var rows = new List<ContentRoleAccessRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@ContentId", contentId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ContentRoleAccessRow
            {
                RoleId = rdr.GetInt32(0),
                RoleName = rdr.GetString(1),
                CanRead = rdr.GetBoolean(2),
                CanWrite = rdr.GetBoolean(3)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<ContentRoleAccessRow>> ListEmptyRoleAccessAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT r.RoleId,
       r.Name
FROM omp.Roles r
ORDER BY r.Name,
         r.RoleId;";

        var rows = new List<ContentRoleAccessRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ContentRoleAccessRow
            {
                RoleId = rdr.GetInt32(0),
                RoleName = rdr.GetString(1)
            });
        }

        return rows;
    }

    public async Task<Guid> SavePageAsync(
        Guid appInstanceId,
        ContentPageSaveRequest input,
        string actor,
        CancellationToken ct)
    {
        var contentId = input.ContentId == Guid.Empty ? Guid.NewGuid() : input.ContentId;
        var slug = ContentSlugNormalizer.Normalize(input.Slug);
        var contentType = ContentTypes.Normalize(input.ContentType);
        var storageContentType = GetStorageContentType(contentType);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        if (await SlugExistsAsync(conn, tx, appInstanceId, slug, contentId, ct))
        {
            throw new InvalidOperationException("A content page with the same slug already exists for this app instance.");
        }

        if (input.ContentId == Guid.Empty)
        {
            const string insertSql = @"
INSERT INTO omp_content.contents(
    content_id,
    app_instance_id,
    slug,
    title,
    content_type,
    body,
    server_report_key,
    is_enabled,
    sort_order,
    created_by,
    updated_by)
VALUES(
    @ContentId,
    @AppInstanceId,
    @Slug,
    @Title,
    @ContentType,
    @Body,
    @ServerReportKey,
    @IsEnabled,
    @SortOrder,
    @Actor,
    @Actor);";

            await using var insert = new SqlCommand(insertSql, conn, tx);
            AddPageParameters(insert, appInstanceId, contentId, slug, input, contentType, storageContentType, actor);
            await insert.ExecuteNonQueryAsync(ct);
        }
        else
        {
            const string updateSql = @"
UPDATE omp_content.contents
SET slug = @Slug,
    title = @Title,
    content_type = @ContentType,
    body = @Body,
    server_report_key = @ServerReportKey,
    is_enabled = @IsEnabled,
    sort_order = @SortOrder,
    updated_at = SYSUTCDATETIME(),
    updated_by = @Actor
WHERE content_id = @ContentId
  AND app_instance_id = @AppInstanceId;";

            await using var update = new SqlCommand(updateSql, conn, tx);
            AddPageParameters(update, appInstanceId, contentId, slug, input, contentType, storageContentType, actor);
            var affected = await update.ExecuteNonQueryAsync(ct);
            if (affected == 0)
            {
                throw new InvalidOperationException("The content page no longer exists.");
            }
        }

        if (input.SaveRoleAccess)
        {
            await ReplaceRoleAccessAsync(conn, tx, contentId, input.RoleAccesses, ct);
        }

        await tx.CommitAsync(ct);
        return contentId;
    }

    public async Task SetEnabledAsync(
        Guid appInstanceId,
        Guid contentId,
        bool isEnabled,
        string actor,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_content.contents
SET is_enabled = @IsEnabled,
    updated_at = SYSUTCDATETIME(),
    updated_by = @Actor
WHERE app_instance_id = @AppInstanceId
  AND content_id = @ContentId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        Add(cmd, "@AppInstanceId", appInstanceId);
        Add(cmd, "@ContentId", contentId);
        Add(cmd, "@IsEnabled", isEnabled);
        Add(cmd, "@Actor", actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplaceRoleAccessAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid contentId,
        IReadOnlyList<ContentRoleAccessSaveRow> roleAccesses,
        CancellationToken ct)
    {
        const string deleteSql = @"
DELETE FROM omp_content.content_role_access
WHERE content_id = @ContentId;";

        await using (var delete = new SqlCommand(deleteSql, conn, tx))
        {
            Add(delete, "@ContentId", contentId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
INSERT INTO omp_content.content_role_access(content_id, role_id, can_read, can_write)
VALUES(@ContentId, @RoleId, @CanRead, @CanWrite);";

        foreach (var access in roleAccesses.Where(x => x.CanRead || x.CanWrite))
        {
            await using var insert = new SqlCommand(insertSql, conn, tx);
            Add(insert, "@ContentId", contentId);
            Add(insert, "@RoleId", access.RoleId);
            Add(insert, "@CanRead", access.CanRead || access.CanWrite);
            Add(insert, "@CanWrite", access.CanWrite);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> SlugExistsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid appInstanceId,
        string slug,
        Guid contentId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM omp_content.contents
WHERE app_instance_id = @AppInstanceId
  AND slug = @Slug
  AND content_id <> @ContentId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        Add(cmd, "@AppInstanceId", appInstanceId);
        Add(cmd, "@Slug", slug);
        Add(cmd, "@ContentId", contentId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static void AddPageParameters(
        SqlCommand cmd,
        Guid appInstanceId,
        Guid contentId,
        string slug,
        ContentPageSaveRequest input,
        string contentType,
        string storageContentType,
        string actor)
    {
        Add(cmd, "@AppInstanceId", appInstanceId);
        Add(cmd, "@ContentId", contentId);
        Add(cmd, "@Slug", slug);
        Add(cmd, "@Title", input.Title.Trim());
        Add(cmd, "@ContentType", storageContentType);
        Add(cmd, "@Body", input.Body);
        Add(cmd, "@ServerReportKey", NormalizeContentKey(contentType, input));
        Add(cmd, "@IsEnabled", input.IsEnabled);
        Add(cmd, "@SortOrder", input.SortOrder);
        Add(cmd, "@Actor", actor);
    }

    private static ContentPageListRow ReadListRow(SqlDataReader rdr)
    {
        var contentType = rdr.GetString(3);
        var contentKey = rdr.IsDBNull(4) ? null : rdr.GetString(4);

        return new ContentPageListRow
        {
            ContentId = rdr.GetGuid(0),
            Slug = rdr.GetString(1),
            Title = rdr.GetString(2),
            ContentType = GetDisplayContentType(contentType, null, contentKey),
            ServerReportKey = contentKey,
            IsEnabled = rdr.GetBoolean(5),
            SortOrder = rdr.IsDBNull(6) ? null : rdr.GetInt32(6),
            CreatedAtUtc = rdr.GetDateTime(7),
            UpdatedAtUtc = rdr.GetDateTime(8),
            UpdatedBy = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            AccessSummary = rdr.IsDBNull(10) ? null : rdr.GetString(10)
        };
    }

    private static ContentPageRenderRow ReadRenderRow(SqlDataReader rdr)
    {
        var contentType = rdr.GetString(3);
        var body = rdr.GetString(4);
        var contentKey = rdr.IsDBNull(5) ? null : rdr.GetString(5);

        return new ContentPageRenderRow
        {
            ContentId = rdr.GetGuid(0),
            Slug = rdr.GetString(1),
            Title = rdr.GetString(2),
            ContentType = GetDisplayContentType(contentType, body, contentKey),
            Body = body,
            ServerReportKey = contentKey,
            UpdatedAtUtc = rdr.GetDateTime(6)
        };
    }

    private static ContentPageEditRow ReadEditRow(SqlDataReader rdr)
    {
        var contentType = rdr.GetString(4);
        var body = rdr.GetString(5);
        var contentKey = rdr.IsDBNull(6) ? null : rdr.GetString(6);

        return new ContentPageEditRow
        {
            ContentId = rdr.GetGuid(0),
            AppInstanceId = rdr.GetGuid(1),
            Slug = rdr.GetString(2),
            Title = rdr.GetString(3),
            ContentType = GetDisplayContentType(contentType, body, contentKey),
            Body = body,
            ServerReportKey = contentKey,
            IsEnabled = rdr.GetBoolean(7),
            SortOrder = rdr.IsDBNull(8) ? null : rdr.GetInt32(8),
            CreatedAtUtc = rdr.GetDateTime(9),
            CreatedBy = rdr.IsDBNull(10) ? null : rdr.GetString(10),
            UpdatedAtUtc = rdr.GetDateTime(11),
            UpdatedBy = rdr.IsDBNull(12) ? null : rdr.GetString(12)
        };
    }

    private static string? NormalizeContentKey(string contentType, ContentPageSaveRequest input)
    {
        var value = contentType switch
        {
            ContentTypes.ServerReport => input.ServerReportKey,
            ContentTypes.HtmlFile => input.HtmlFileKey,
            _ => null
        };

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string GetStorageContentType(string contentType)
        => contentType == ContentTypes.HtmlFile ? ContentTypes.Html : contentType;

    private static string GetDisplayContentType(string contentType, string? body, string? contentKey)
    {
        var normalized = ContentTypes.Normalize(contentType);

        // File-backed HTML pages are stored as ordinary HTML rows with an empty body
        // so existing installations do not need a schema migration for this UI mode.
        if (normalized == ContentTypes.Html
            && string.IsNullOrWhiteSpace(body)
            && !string.IsNullOrWhiteSpace(contentKey))
        {
            return ContentTypes.HtmlFile;
        }

        return normalized;
    }

    private static void Add(SqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static int[] NormalizeRoleIds(IReadOnlyCollection<int> roleIds)
        => roleIds
            .Where(roleId => roleId > 0)
            .Distinct()
            .Order()
            .ToArray();

    private static string BuildRoleParameterList(IReadOnlyList<int> roleIds)
    {
        if (roleIds.Count == 0)
        {
            return "NULL";
        }

        return string.Join(", ", Enumerable.Range(0, roleIds.Count).Select(i => $"@RoleId{i}"));
    }

    private static void AddRoleParameters(SqlCommand cmd, IReadOnlyList<int> roleIds)
    {
        for (var i = 0; i < roleIds.Count; i++)
        {
            Add(cmd, $"@RoleId{i}", roleIds[i]);
        }
    }
}
