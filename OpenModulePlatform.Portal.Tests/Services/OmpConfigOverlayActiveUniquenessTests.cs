using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Proves that omp.ConfigOverlayDocuments allows at most one enabled document
/// per (OverlayKey, HostKey) at the database level, even when the insert
/// bypasses the application-level keep-history save paths. The fixture
/// provisions the database by executing the real core setup script, so these
/// tests go red if the filtered unique index is removed from
/// sql/1-setup-openmoduleplatform.sql.
/// </summary>
[Collection(ConfigOverlayActiveUniquenessCollection.CollectionName)]
public sealed class OmpConfigOverlayActiveUniquenessTests
{
    private readonly ConfigOverlayActiveUniquenessTestFixture _fixture;

    public OmpConfigOverlayActiveUniquenessTests(ConfigOverlayActiveUniquenessTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PlatformMigration_UpgradesOwnedColumnsWithoutChangingContent_OnRepeatedUpgrade()
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        // Roll back the entire probe so the collection keeps its original schema and data.
        await using var begin = new SqlCommand("BEGIN TRANSACTION;", connection);
        await begin.ExecuteNonQueryAsync();
        try
        {
            const string legacySql = """
ALTER TABLE omp.ArtifactConfigurationFiles DROP COLUMN PackageFileContent;
ALTER TABLE omp.ConfigOverlayConfigurationFiles DROP COLUMN MergeMode;
INSERT INTO omp.ConfigOverlayDocuments
    (OverlayKey, OverlayVersion, HostKey, OverlayJson, OverlaySha256, IsEnabled)
VALUES (N'column-upgrade-probe', N'1.0.0', N'column-upgrade-host', N'{}', N'probe', 0);
INSERT INTO omp.ConfigOverlayConfigurationFiles (ConfigOverlayDocumentId, RelativePath, FileContent)
VALUES (CONVERT(int, SCOPE_IDENTITY()), N'probe.json', N'operator content');
""";
            await using var legacy = new SqlCommand(legacySql, connection);
            await legacy.ExecuteNonQueryAsync();
            var migration = typeof(OpenModulePlatform.Portal.Services.OmpAdminRepository).Assembly
                .GetType("OpenModulePlatform.ModuleDefinitions.PlatformConfigurationMigration")!;
            var apply = migration.GetMethod("ApplyAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)apply.Invoke(null, [connection, CancellationToken.None, 3600])!;
            await using var columns = new SqlCommand("""
SELECT CASE WHEN COL_LENGTH(N'omp.ArtifactConfigurationFiles', N'PackageFileContent') = -1
    AND COL_LENGTH(N'omp.ConfigOverlayConfigurationFiles', N'MergeMode') = 40
    THEN 1 ELSE 0 END;
""", connection);
            Assert.Equal(1, Convert.ToInt32(await columns.ExecuteScalarAsync()));
            await using var content = new SqlCommand("""
SELECT FileContent FROM omp.ConfigOverlayConfigurationFiles
WHERE RelativePath = N'probe.json' AND MergeMode IS NULL;
""", connection);
            Assert.Equal("operator content", await content.ExecuteScalarAsync());
            await (Task)apply.Invoke(null, [connection, CancellationToken.None, 3600])!;
            Assert.Equal("operator content", await content.ExecuteScalarAsync());
        }
        finally
        {
            await using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", connection);
            await rollback.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PlatformMigration_PreservesSemanticWinnerAndHistory_OnRepeatedUpgrade()
    {
        const string overlayKey = "overlay-platform-migration";
        const string hostKey = "overlay-platform-migration-host";
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var dropIndex = new SqlCommand("DROP INDEX UX_omp_ConfigOverlayDocuments_Enabled_Key_Host ON omp.ConfigOverlayDocuments;", connection);
        await dropIndex.ExecuteNonQueryAsync();
        try
        {
            await _fixture.InsertDocumentAsync(overlayKey, hostKey, "9.0.0", true);
            var winner = await _fixture.InsertDocumentAsync(overlayKey, hostKey, "10.0.0", true);
            await _fixture.InsertDocumentAsync(overlayKey, hostKey, "1.0.0", false);

            // Invoke the production source-linked migration from the Portal assembly.
            var migration = typeof(OpenModulePlatform.Portal.Services.OmpAdminRepository).Assembly
                .GetType("OpenModulePlatform.ModuleDefinitions.PlatformConfigurationMigration")!;
            var apply = migration.GetMethod("ApplyAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)apply.Invoke(null, [connection, CancellationToken.None, 3600])!;
            var first = await _fixture.GetDocumentsAsync(overlayKey, hostKey);
            Assert.Equal(3, first.Count);
            Assert.Equal(winner, Assert.Single(first, row => row.IsEnabled).DocumentId);
            await (Task)apply.Invoke(null, [connection, CancellationToken.None, 3600])!;
            Assert.Equal(first, await _fixture.GetDocumentsAsync(overlayKey, hostKey));
        }
        finally
        {
            // Restore the collection's schema even if an assertion fails.
            await using var cleanup = new SqlCommand("DELETE FROM omp.ConfigOverlayDocuments WHERE OverlayKey = @key; CREATE UNIQUE INDEX UX_omp_ConfigOverlayDocuments_Enabled_Key_Host ON omp.ConfigOverlayDocuments(OverlayKey, HostKey) WHERE IsEnabled = 1;", connection);
            cleanup.Parameters.AddWithValue("@key", overlayKey);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SecondEnabledDocumentForSameKeyAndHost_IsRejectedByUniqueIndex()
    {
        const string overlayKey = "overlay-active-uniqueness";
        const string hostKey = "overlay-active-uniqueness-host";

        await _fixture.InsertDocumentAsync(overlayKey, hostKey, "1.0.0", isEnabled: true);

        // Direct second enabled insert via raw SQL with a new version,
        // bypassing the application-level disable-then-insert flow.
        var ex = await Assert.ThrowsAnyAsync<SqlException>(() => _fixture.InsertDocumentAsync(
            overlayKey,
            hostKey,
            "1.0.1",
            isEnabled: true));

        Assert.True(
            ex.Number is 2601 or 2627,
            $"Expected a unique-index/unique-constraint violation (2601 or 2627), got SqlException {ex.Number}: {ex.Message}");

        Assert.Equal(1, await _fixture.CountDocumentsAsync(overlayKey, hostKey));
    }

    [Fact]
    public async Task DisabledDuplicateDocumentForSameKeyAndHost_IsAllowedAsHistory()
    {
        const string overlayKey = "overlay-active-uniqueness-history";
        const string hostKey = "overlay-active-uniqueness-history-host";

        await _fixture.InsertDocumentAsync(overlayKey, hostKey, "1.0.0", isEnabled: true);

        // Disabled historical rows for the same key and host must remain
        // insertable; only a second enabled row is rejected.
        await _fixture.InsertDocumentAsync(overlayKey, hostKey, "1.0.1", isEnabled: false);
        await _fixture.InsertDocumentAsync(overlayKey, hostKey, "1.0.2", isEnabled: false);

        Assert.Equal(3, await _fixture.CountDocumentsAsync(overlayKey, hostKey));
    }

    [Fact]
    public async Task EnabledDocumentForSameKeyOnDifferentHost_IsAllowed()
    {
        const string overlayKey = "overlay-active-uniqueness-per-host";

        await _fixture.InsertDocumentAsync(overlayKey, "overlay-active-uniqueness-host-a", "1.0.0", isEnabled: true);
        await _fixture.InsertDocumentAsync(overlayKey, "overlay-active-uniqueness-host-b", "1.0.0", isEnabled: true);

        Assert.Equal(1, await _fixture.CountDocumentsAsync(overlayKey, "overlay-active-uniqueness-host-a"));
        Assert.Equal(1, await _fixture.CountDocumentsAsync(overlayKey, "overlay-active-uniqueness-host-b"));
    }
}
