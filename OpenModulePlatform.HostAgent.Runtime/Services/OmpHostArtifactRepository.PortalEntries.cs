using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed partial class OmpHostArtifactRepository
{
    /// <summary>
    /// Adds app home entries after module initialization, preserving all existing rows.
    /// Deleting an entry is temporary; administrators must disable it to keep it hidden.
    /// </summary>
    internal async Task<int> InsertMissingPortalAppEntriesAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        // Separate batch: do not compile Portal table references before Portal exists.
        await using var probe = new SqlCommand(
            "SELECT OBJECT_ID(N'omp_portal.portal_entries', N'U');", conn);
        if (await probe.ExecuteScalarAsync(ct) is null or DBNull)
            return 0;

        // Keep the key, eligibility and defaults aligned with the full Portal reset in
        // PortalEntryService and sql/3-sync-omp-portal-entries.sql. HOLDLOCK serializes
        // overlapping inserts from different hosts against the unique entry-key index.
        const string sql = """
            ;WITH app_entries AS
            (
                SELECT N'app:' + LOWER(REPLACE(CONVERT(nvarchar(36), ai.AppInstanceId), N'-', N'')) + N':home' AS entry_key,
                       ai.DisplayName AS display_name,
                       ai.Description AS description,
                       CASE
                           WHEN a.AppType = N'Portal' THEN CAST(NULL AS nvarchar(200))
                           ELSE N'app:' + LOWER(REPLACE(CONVERT(nvarchar(36), ai.AppInstanceId), N'-', N'')) + N':home'
                       END AS target_entry_key,
                       ai.AppInstanceId AS source_app_instance_id,
                       ai.SortOrder AS default_sort_order
                FROM omp.AppInstances ai
                INNER JOIN omp.Apps a ON a.AppId = ai.AppId
                WHERE ai.IsEnabled = 1
                  AND ai.IsAllowed = 1
                  AND a.AppType IN (N'Portal', N'WebApp')
            )
            MERGE omp_portal.portal_entries WITH (HOLDLOCK) AS target
            USING app_entries AS source
                ON target.entry_key = source.entry_key
            WHEN NOT MATCHED BY TARGET THEN
                INSERT(entry_key, display_name, description, target_entry_key, source_app_instance_id, is_enabled, default_sort_order)
                VALUES(source.entry_key, source.display_name, source.description, source.target_entry_key, source.source_app_instance_id, 1, source.default_sort_order);
            """;
        await using var cmd = new SqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
