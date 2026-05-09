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

    public async Task<IReadOnlyList<ContentPageListRow>> ListPagesAsync(Guid appInstanceId, CancellationToken ct)
    {
        const string sql = @"
SELECT PageId,
       Slug,
       Title,
       IsPublished,
       PublishedAtUtc,
       SortOrder,
       UpdatedAtUtc,
       UpdatedBy
FROM omp_content.Pages
WHERE AppInstanceId = @AppInstanceId
  AND IsDeleted = 0
ORDER BY SortOrder, Slug, Title;";

        var rows = new List<ContentPageListRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AppInstanceId", appInstanceId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ContentPageListRow
            {
                PageId = rdr.GetGuid(0),
                Slug = rdr.GetString(1),
                Title = rdr.GetString(2),
                IsPublished = rdr.GetBoolean(3),
                PublishedAtUtc = rdr.IsDBNull(4) ? null : rdr.GetDateTime(4),
                SortOrder = rdr.GetInt32(5),
                UpdatedAtUtc = rdr.GetDateTime(6),
                UpdatedBy = rdr.IsDBNull(7) ? null : rdr.GetString(7)
            });
        }

        return rows;
    }

    public async Task<ContentPageRenderRow?> GetPublishedPageBySlugAsync(Guid appInstanceId, string slug, CancellationToken ct)
    {
        const string sql = @"
SELECT PageId,
       Slug,
       Title,
       MetaTitle,
       MetaDescription,
       ContentFormat,
       Content,
       UpdatedAtUtc
FROM omp_content.Pages
WHERE AppInstanceId = @AppInstanceId
  AND Slug = @Slug
  AND IsPublished = 1
  AND IsDeleted = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AppInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@Slug", slug);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        return await rdr.ReadAsync(ct) ? ReadRenderRow(rdr) : null;
    }

    public async Task<ContentPageEditRow?> GetPageForEditAsync(Guid appInstanceId, Guid pageId, CancellationToken ct)
    {
        const string sql = @"
SELECT PageId,
       AppInstanceId,
       Slug,
       Title,
       Summary,
       MetaTitle,
       MetaDescription,
       ContentFormat,
       Content,
       IsPublished,
       PublishedAtUtc,
       SortOrder,
       CreatedAtUtc,
       CreatedBy,
       UpdatedAtUtc,
       UpdatedBy
FROM omp_content.Pages
WHERE AppInstanceId = @AppInstanceId
  AND PageId = @PageId
  AND IsDeleted = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AppInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@PageId", pageId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        return await rdr.ReadAsync(ct) ? ReadEditRow(rdr) : null;
    }

    public async Task<IReadOnlyList<ContentPageRevisionRow>> ListRevisionsAsync(Guid pageId, CancellationToken ct)
    {
        const string sql = @"
SELECT RevisionId,
       PageId,
       RevisionNumber,
       Title,
       Slug,
       ContentFormat,
       CreatedAtUtc,
       CreatedBy,
       ChangeNote
FROM omp_content.PageRevisions
WHERE PageId = @PageId
ORDER BY RevisionNumber DESC;";

        var rows = new List<ContentPageRevisionRow>();
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@PageId", pageId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new ContentPageRevisionRow
            {
                RevisionId = rdr.GetGuid(0),
                PageId = rdr.GetGuid(1),
                RevisionNumber = rdr.GetInt32(2),
                Title = rdr.GetString(3),
                Slug = rdr.GetString(4),
                ContentFormat = rdr.GetString(5),
                CreatedAtUtc = rdr.GetDateTime(6),
                CreatedBy = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                ChangeNote = rdr.IsDBNull(8) ? null : rdr.GetString(8)
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
        var pageId = input.PageId == Guid.Empty ? Guid.NewGuid() : input.PageId;
        var slug = ContentSlugNormalizer.Normalize(input.Slug);
        var contentFormat = ContentFormats.Normalize(input.ContentFormat);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        if (await SlugExistsAsync(conn, tx, appInstanceId, slug, pageId, ct))
        {
            throw new InvalidOperationException("A content page with the same slug already exists for this app instance.");
        }

        if (input.PageId == Guid.Empty)
        {
            const string insertSql = @"
INSERT INTO omp_content.Pages(
    PageId,
    AppInstanceId,
    Slug,
    Title,
    Summary,
    MetaTitle,
    MetaDescription,
    ContentFormat,
    Content,
    SortOrder,
    CreatedBy,
    UpdatedBy)
VALUES(
    @PageId,
    @AppInstanceId,
    @Slug,
    @Title,
    @Summary,
    @MetaTitle,
    @MetaDescription,
    @ContentFormat,
    @Content,
    @SortOrder,
    @Actor,
    @Actor);";

            await using var insert = new SqlCommand(insertSql, conn, tx);
            AddPageParameters(insert, appInstanceId, pageId, slug, input, contentFormat, actor);
            await insert.ExecuteNonQueryAsync(ct);
        }
        else
        {
            const string updateSql = @"
UPDATE omp_content.Pages
SET Slug = @Slug,
    Title = @Title,
    Summary = @Summary,
    MetaTitle = @MetaTitle,
    MetaDescription = @MetaDescription,
    ContentFormat = @ContentFormat,
    Content = @Content,
    SortOrder = @SortOrder,
    UpdatedAtUtc = SYSUTCDATETIME(),
    UpdatedBy = @Actor
WHERE PageId = @PageId
  AND AppInstanceId = @AppInstanceId
  AND IsDeleted = 0;";

            await using var update = new SqlCommand(updateSql, conn, tx);
            AddPageParameters(update, appInstanceId, pageId, slug, input, contentFormat, actor);
            var affected = await update.ExecuteNonQueryAsync(ct);
            if (affected == 0)
            {
                throw new InvalidOperationException("The content page no longer exists.");
            }
        }

        await InsertRevisionAsync(conn, tx, pageId, slug, input, contentFormat, actor, ct);
        await tx.CommitAsync(ct);
        return pageId;
    }

    public Task PublishPageAsync(Guid appInstanceId, Guid pageId, string actor, CancellationToken ct)
        => SetPublishedAsync(appInstanceId, pageId, isPublished: true, actor, ct);

    public Task UnpublishPageAsync(Guid appInstanceId, Guid pageId, string actor, CancellationToken ct)
        => SetPublishedAsync(appInstanceId, pageId, isPublished: false, actor, ct);

    public async Task SoftDeletePageAsync(Guid appInstanceId, Guid pageId, string actor, CancellationToken ct)
    {
        const string sql = @"
UPDATE omp_content.Pages
SET IsDeleted = 1,
    IsPublished = 0,
    UpdatedAtUtc = SYSUTCDATETIME(),
    UpdatedBy = @Actor
WHERE AppInstanceId = @AppInstanceId
  AND PageId = @PageId
  AND IsDeleted = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AppInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@PageId", pageId);
        cmd.Parameters.AddWithValue("@Actor", actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task SetPublishedAsync(
        Guid appInstanceId,
        Guid pageId,
        bool isPublished,
        string actor,
        CancellationToken ct)
    {
        const string sql = @"
UPDATE p
SET IsPublished = @IsPublished,
    PublishedAtUtc = CASE WHEN @IsPublished = 1 THEN SYSUTCDATETIME() ELSE NULL END,
    LastPublishedRevisionId = CASE WHEN @IsPublished = 1 THEN latest.RevisionId ELSE p.LastPublishedRevisionId END,
    UpdatedAtUtc = SYSUTCDATETIME(),
    UpdatedBy = @Actor
FROM omp_content.Pages p
OUTER APPLY
(
    SELECT TOP (1) RevisionId
    FROM omp_content.PageRevisions r
    WHERE r.PageId = p.PageId
    ORDER BY r.RevisionNumber DESC
) latest
WHERE p.AppInstanceId = @AppInstanceId
  AND p.PageId = @PageId
  AND p.IsDeleted = 0;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@AppInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@PageId", pageId);
        cmd.Parameters.AddWithValue("@IsPublished", isPublished);
        cmd.Parameters.AddWithValue("@Actor", actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> SlugExistsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid appInstanceId,
        string slug,
        Guid pageId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1)
FROM omp_content.Pages
WHERE AppInstanceId = @AppInstanceId
  AND Slug = @Slug
  AND PageId <> @PageId
  AND IsDeleted = 0;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@AppInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@Slug", slug);
        cmd.Parameters.AddWithValue("@PageId", pageId);

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    private static async Task InsertRevisionAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid pageId,
        string slug,
        ContentPageSaveRequest input,
        string contentFormat,
        string actor,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @RevisionNumber int;
SELECT @RevisionNumber = ISNULL(MAX(RevisionNumber), 0) + 1
FROM omp_content.PageRevisions
WHERE PageId = @PageId;

INSERT INTO omp_content.PageRevisions(
    RevisionId,
    PageId,
    RevisionNumber,
    Title,
    Slug,
    ContentFormat,
    Content,
    CreatedBy,
    ChangeNote)
VALUES(
    NEWID(),
    @PageId,
    @RevisionNumber,
    @Title,
    @Slug,
    @ContentFormat,
    @Content,
    @Actor,
    @ChangeNote);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@PageId", pageId);
        cmd.Parameters.AddWithValue("@Title", input.Title.Trim());
        cmd.Parameters.AddWithValue("@Slug", slug);
        cmd.Parameters.AddWithValue("@ContentFormat", contentFormat);
        cmd.Parameters.AddWithValue("@Content", input.Content);
        cmd.Parameters.AddWithValue("@Actor", actor);
        cmd.Parameters.AddWithValue("@ChangeNote", (object?)Clean(input.ChangeNote) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddPageParameters(
        SqlCommand cmd,
        Guid appInstanceId,
        Guid pageId,
        string slug,
        ContentPageSaveRequest input,
        string contentFormat,
        string actor)
    {
        cmd.Parameters.AddWithValue("@AppInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@PageId", pageId);
        cmd.Parameters.AddWithValue("@Slug", slug);
        cmd.Parameters.AddWithValue("@Title", input.Title.Trim());
        cmd.Parameters.AddWithValue("@Summary", (object?)Clean(input.Summary) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MetaTitle", (object?)Clean(input.MetaTitle) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MetaDescription", (object?)Clean(input.MetaDescription) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ContentFormat", contentFormat);
        cmd.Parameters.AddWithValue("@Content", input.Content);
        cmd.Parameters.AddWithValue("@SortOrder", input.SortOrder);
        cmd.Parameters.AddWithValue("@Actor", actor);
    }

    private static ContentPageRenderRow ReadRenderRow(SqlDataReader rdr)
    {
        return new ContentPageRenderRow
        {
            PageId = rdr.GetGuid(0),
            Slug = rdr.GetString(1),
            Title = rdr.GetString(2),
            MetaTitle = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            MetaDescription = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            ContentFormat = rdr.GetString(5),
            Content = rdr.GetString(6),
            UpdatedAtUtc = rdr.GetDateTime(7)
        };
    }

    private static ContentPageEditRow ReadEditRow(SqlDataReader rdr)
    {
        return new ContentPageEditRow
        {
            PageId = rdr.GetGuid(0),
            AppInstanceId = rdr.GetGuid(1),
            Slug = rdr.GetString(2),
            Title = rdr.GetString(3),
            Summary = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            MetaTitle = rdr.IsDBNull(5) ? null : rdr.GetString(5),
            MetaDescription = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            ContentFormat = rdr.GetString(7),
            Content = rdr.GetString(8),
            IsPublished = rdr.GetBoolean(9),
            PublishedAtUtc = rdr.IsDBNull(10) ? null : rdr.GetDateTime(10),
            SortOrder = rdr.GetInt32(11),
            CreatedAtUtc = rdr.GetDateTime(12),
            CreatedBy = rdr.IsDBNull(13) ? null : rdr.GetString(13),
            UpdatedAtUtc = rdr.GetDateTime(14),
            UpdatedBy = rdr.IsDBNull(15) ? null : rdr.GetString(15)
        };
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
