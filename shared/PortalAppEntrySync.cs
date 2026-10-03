using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.ModuleDefinitions;

/// <summary>Insert-only app home reconciliation shared by Portal and HostAgent imports.</summary>
internal static class PortalAppEntrySync
{
    // Keep eligibility, keys and defaults aligned with PortalEntryService and
    // sql/3-sync-omp-portal-entries.sql. Existing rows belong to the administrator.
    private const string CandidatesSql = """
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
        """;

    internal static async Task<int> InsertMissingAsync(
        SqlConnection connection, Action<int>? onAttempt, CancellationToken ct)
    {
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        // Probe and writes share a transaction. Keep the table references in a later
        // command so SQL Server does not compile them before Portal is installed.
        await using var probe = new SqlCommand(
            "SELECT OBJECT_ID(N'omp_portal.portal_entries', N'U');", connection, transaction);
        if (await probe.ExecuteScalarAsync(ct) is null or DBNull)
        {
            onAttempt?.Invoke(0);
            await transaction.CommitAsync(ct);
            return 0;
        }

        // Count missing entries before attempting the write so a failed MERGE still
        // has useful diagnostics. Retain key-range locks until commit: a concurrent
        // insert/full sync cannot invalidate these target gaps between count and MERGE.
        await using var count = new SqlCommand(CandidatesSql + """

            SELECT COUNT(*) FROM app_entries source
            WHERE NOT EXISTS (
                SELECT 1 FROM omp_portal.portal_entries target WITH (UPDLOCK, HOLDLOCK)
                WHERE target.entry_key = source.entry_key);
            """, connection, transaction);
        onAttempt?.Invoke(Convert.ToInt32(await count.ExecuteScalarAsync(ct)));

        await using var merge = new SqlCommand(CandidatesSql + """

            MERGE omp_portal.portal_entries WITH (HOLDLOCK) AS target
            USING app_entries AS source
                ON target.entry_key = source.entry_key
            WHEN NOT MATCHED BY TARGET THEN
                INSERT(entry_key, display_name, description, target_entry_key, source_app_instance_id, is_enabled, default_sort_order)
                VALUES(source.entry_key, source.display_name, source.description, source.target_entry_key, source.source_app_instance_id, 1, source.default_sort_order);
            """, connection, transaction);
        var inserted = await merge.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return inserted;
    }
}
