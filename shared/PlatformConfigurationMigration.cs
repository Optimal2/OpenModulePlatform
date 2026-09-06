using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace OpenModulePlatform.ModuleDefinitions;

// Platform-owned migration, deliberately outside portable module SQL.
internal static class PlatformConfigurationMigration
{
    internal static async Task ApplyForDefinitionAsync(SqlConnection connection, string definitionJson, string scriptKey, CancellationToken cancellationToken, int commandTimeoutSeconds = 3600)
    {
        using var definition = JsonDocument.Parse(definitionJson);
        if (scriptKey == "setup-core-schema"
            && definition.RootElement.GetProperty("moduleKey").GetString() == "omp_core")
            await ApplyAsync(connection, cancellationToken, commandTimeoutSeconds);
    }

    internal static async Task ApplyAsync(SqlConnection connection, CancellationToken cancellationToken, int commandTimeoutSeconds)
    {
        // Upgrade existing owned tables before portable setup. Fresh installations
        // create these columns directly; schema upgrades must not depend on the
        // overlay deduplication prerequisites below.
        await using var schema = new SqlCommand(SchemaMigrationSql, connection) { CommandTimeout = commandTimeoutSeconds };
        await schema.ExecuteNonQueryAsync(cancellationToken);

        const string prerequisiteSql = """
SELECT CASE WHEN COL_LENGTH(N'omp.ConfigOverlayDocuments', N'OverlayVersion') IS NOT NULL
    AND COL_LENGTH(N'omp.ConfigOverlayDocuments', N'IsEnabled') IS NOT NULL
    AND COL_LENGTH(N'omp.ConfigOverlayDocuments', N'UpdatedUtc') IS NOT NULL
    AND COL_LENGTH(N'omp.ConfigOverlayDocuments', N'OverlayKey') IS NOT NULL
    AND COL_LENGTH(N'omp.ConfigOverlayDocuments', N'HostKey') IS NOT NULL
    AND COL_LENGTH(N'omp.ConfigOverlayDocuments', N'ConfigOverlayDocumentId') IS NOT NULL
    THEN 1 ELSE 0 END;
""";
        await using var prerequisite = new SqlCommand(prerequisiteSql, connection) { CommandTimeout = commandTimeoutSeconds };
        if (Convert.ToInt32(await prerequisite.ExecuteScalarAsync(cancellationToken)) != 1) return;
        await using var command = new SqlCommand(MigrationSql, connection) { CommandTimeout = commandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal const string SchemaMigrationSql = """
IF OBJECT_ID(N'omp.ArtifactConfigurationFiles', N'U') IS NOT NULL
    AND COL_LENGTH(N'omp.ArtifactConfigurationFiles', N'PackageFileContent') IS NULL
BEGIN
    ALTER TABLE omp.ArtifactConfigurationFiles ADD PackageFileContent nvarchar(max) NULL;
END;
IF OBJECT_ID(N'omp.ConfigOverlayConfigurationFiles', N'U') IS NOT NULL
    AND COL_LENGTH(N'omp.ConfigOverlayConfigurationFiles', N'MergeMode') IS NULL
BEGIN
    ALTER TABLE omp.ConfigOverlayConfigurationFiles ADD MergeMode nvarchar(20) NULL;
END;
""";

    // Preserve the runtime winner: highest semantic overlay version, latest update,
    // then highest row id. Existing clean data is unchanged on repeated imports.
    internal const string MigrationSql = """
;WITH ranked_enabled AS
(
    SELECT d.ConfigOverlayDocumentId,
           ROW_NUMBER() OVER
           (
               PARTITION BY d.OverlayKey, d.HostKey
               ORDER BY TRY_CAST(PARSENAME(nv.NormalizedVersion, 4) AS bigint) DESC,
                        TRY_CAST(PARSENAME(nv.NormalizedVersion, 3) AS bigint) DESC,
                        TRY_CAST(PARSENAME(nv.NormalizedVersion, 2) AS bigint) DESC,
                        TRY_CAST(PARSENAME(nv.NormalizedVersion, 1) AS bigint) DESC,
                        d.UpdatedUtc DESC,
                        d.ConfigOverlayDocumentId DESC
           ) AS rn
    FROM omp.ConfigOverlayDocuments d
    CROSS APPLY
    (
        SELECT CASE
                   WHEN d.OverlayVersion LIKE N'%[^0-9.]%' THEN NULL
                   WHEN LEN(d.OverlayVersion) - LEN(REPLACE(d.OverlayVersion, N'.', N'')) = 0 THEN d.OverlayVersion + N'.0.0.0'
                   WHEN LEN(d.OverlayVersion) - LEN(REPLACE(d.OverlayVersion, N'.', N'')) = 1 THEN d.OverlayVersion + N'.0.0'
                   WHEN LEN(d.OverlayVersion) - LEN(REPLACE(d.OverlayVersion, N'.', N'')) = 2 THEN d.OverlayVersion + N'.0'
                   WHEN LEN(d.OverlayVersion) - LEN(REPLACE(d.OverlayVersion, N'.', N'')) = 3 THEN d.OverlayVersion
                   ELSE NULL
               END AS NormalizedVersion
    ) nv
    WHERE d.IsEnabled = 1
)
UPDATE d
SET IsEnabled = 0,
    UpdatedUtc = SYSUTCDATETIME()
FROM omp.ConfigOverlayDocuments d
INNER JOIN ranked_enabled ON ranked_enabled.ConfigOverlayDocumentId = d.ConfigOverlayDocumentId
WHERE ranked_enabled.rn > 1;
""";
}
