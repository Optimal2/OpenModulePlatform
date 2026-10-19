using Microsoft.Data.SqlClient;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database provisioned by executing the real
/// core setup script (sql/1-setup-openmoduleplatform.sql), so the quick-links
/// seed tests run the shipped omp.link_boxes / omp.link_box_items schema
/// instead of a hand-maintained minimal DDL that can drift away from production.
/// </summary>
public sealed class PortalQuickLinksSeedTestFixture : IAsyncLifetime
{
    // Per-process name (pid + start time) so concurrent test hosts never share a
    // database; stale copies from crashed runs are swept by the provisioner.
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("PortalQuickLinksSeed");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            builder.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");
        await CoreSetupScript.ApplyAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        // Best-effort cleanup, including opening the connection: a failed open must
        // not surface as an xUnit class-cleanup failure after the tests already ran.
        try
        {
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                $@"
ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [{DatabaseName}];",
                conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            OmpTestCleanupLog.RecordFailure(
                nameof(PortalQuickLinksSeedTestFixture),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
    }

    public async Task ExecuteBatchAsync(string batch)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(batch, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes the quick-links box and its items so a test can re-run the seed
    /// batch from a clean state. The two tests in this fixture share one
    /// database, so each one clears the box before seeding rather than relying
    /// on xUnit execution order.
    /// </summary>
    public async Task ResetQuickLinksBoxAsync(string boxKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
DELETE FROM omp.link_box_items WHERE box_key = @boxKey;
DELETE FROM omp.link_boxes WHERE box_key = @boxKey;",
            conn);
        cmd.Parameters.AddWithValue("@boxKey", boxKey);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<QuickLinkSeedRow>> ReadQuickLinksAsync(string boxKey)
    {
        var rows = new List<QuickLinkSeedRow>();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
SELECT label, url, group_key, sort_order
FROM omp.link_box_items
WHERE box_key = @boxKey
ORDER BY sort_order, label;",
            conn);
        cmd.Parameters.AddWithValue("@boxKey", boxKey);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add(new QuickLinkSeedRow
            {
                Label = rdr.GetString(0),
                Url = rdr.GetString(1),
                GroupKey = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                SortOrder = rdr.GetInt32(3)
            });
        }

        return rows;
    }
}

public sealed class QuickLinkSeedRow
{
    public string Label { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? GroupKey { get; init; }
    public int SortOrder { get; init; }
}
