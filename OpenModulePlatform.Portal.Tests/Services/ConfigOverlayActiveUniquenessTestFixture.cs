using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database provisioned by executing the real
/// core setup script (sql/1-setup-openmoduleplatform.sql) batch by batch, so
/// tests that assert schema-level guarantees stay bound to the shipped schema
/// file. Shared through <see cref="ConfigOverlayActiveUniquenessCollection"/> so
/// every test class using it gets the same single instance: two fixture instances
/// would provision the same database name concurrently and corrupt each other.
/// </summary>
public sealed class ConfigOverlayActiveUniquenessTestFixture : IAsyncLifetime
{
    // Per-process name (pid + start time) so concurrent test hosts never share a
    // database; stale copies from crashed runs are swept by the provisioner.
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("ConfigOverlayActiveUniqueness");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();
        await CoreSetupScript.ApplyAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await DropDatabaseAsync();
    }

    /// <summary>
    /// Creates the portal repository against this real-schema database, so the
    /// application save path can be exercised against the same filtered unique
    /// index that production enforces.
    /// </summary>
    public OmpAdminRepository CreatePortalRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = ConnectionString
            })
            .Build();
        return new OmpAdminRepository(new PortalSqlConnectionFactory(configuration));
    }

    /// <summary>
    /// Inserts a config overlay document row with raw SQL, deliberately
    /// bypassing all application-level keep-history logic, so the tests only
    /// pass when the database schema itself rejects a second enabled document
    /// for the same (OverlayKey, HostKey).
    /// </summary>
    public async Task<int> InsertDocumentAsync(
        string overlayKey,
        string hostKey,
        string overlayVersion,
        bool isEnabled)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.ConfigOverlayDocuments
    (OverlayKey, OverlayVersion, HostKey, OverlayJson, OverlaySha256, IsEnabled)
VALUES
    (@overlayKey, @overlayVersion, @hostKey, N'{}', @overlaySha256, @isEnabled);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@overlayKey", overlayKey);
        cmd.Parameters.AddWithValue("@overlayVersion", overlayVersion);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@overlaySha256", new string('a', 64));
        cmd.Parameters.AddWithValue("@isEnabled", isEnabled);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> CountDocumentsAsync(string overlayKey, string hostKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
SELECT COUNT(1)
FROM omp.ConfigOverlayDocuments
WHERE OverlayKey = @overlayKey AND HostKey = @hostKey;",
            conn);
        cmd.Parameters.AddWithValue("@overlayKey", overlayKey);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<IReadOnlyList<(int DocumentId, string OverlayVersion, bool IsEnabled)>> GetDocumentsAsync(
        string overlayKey,
        string hostKey)
    {
        var rows = new List<(int, string, bool)>();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
SELECT ConfigOverlayDocumentId, OverlayVersion, IsEnabled
FROM omp.ConfigOverlayDocuments
WHERE OverlayKey = @overlayKey AND HostKey = @hostKey
ORDER BY ConfigOverlayDocumentId;",
            conn);
        cmd.Parameters.AddWithValue("@overlayKey", overlayKey);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add((rdr.GetInt32(0), rdr.GetString(1), rdr.GetBoolean(2)));
        }

        return rows;
    }

    private async Task EnsureDatabaseExistsAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        // Drop any leftover database from a previous run whose best-effort cleanup
        // failed: the real setup script is not fully idempotent, so applying it on
        // top of a half-provisioned leftover fails on unguarded CREATE statements.
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            builder.ConnectionString,
            $@"
IF DB_ID(N'{DatabaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{DatabaseName}];
END;
CREATE DATABASE [{DatabaseName}];");
    }

    private async Task DropDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            $@"
ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [{DatabaseName}];",
            conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // Best-effort cleanup.
        }
    }
}
