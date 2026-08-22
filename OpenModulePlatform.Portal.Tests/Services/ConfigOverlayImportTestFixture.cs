using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Services;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database provisioned with the real core setup
/// script (sql/1-setup-openmoduleplatform.sql), so the save-path tests run against
/// the same schema production enforces -- including the filtered unique index
/// UX_omp_ConfigOverlayDocuments_Enabled_Key_Host. A hand-maintained minimal DDL
/// without that index let a statement-ordering bug in the save path pass unnoticed.
/// </summary>
public sealed class ConfigOverlayImportTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_ConfigOverlayImport";

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();
        await CoreSetupScript.ApplyAsync(ConnectionString);
        await ResetAsync();
    }

    public async Task DisposeAsync()
    {
        await DropDatabaseAsync();
    }

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

    public async Task<IReadOnlyList<(int DocumentId, string OverlayVersion, bool IsEnabled, DateTime UpdatedUtc)>> GetDocumentsAsync(
        string overlayKey,
        string hostKey)
    {
        var rows = new List<(int, string, bool, DateTime)>();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
SELECT ConfigOverlayDocumentId, OverlayVersion, IsEnabled, UpdatedUtc
FROM omp.ConfigOverlayDocuments
WHERE OverlayKey = @overlayKey AND HostKey = @hostKey
ORDER BY ConfigOverlayDocumentId;",
            conn);
        cmd.Parameters.AddWithValue("@overlayKey", overlayKey);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add((rdr.GetInt32(0), rdr.GetString(1), rdr.GetBoolean(2), rdr.GetDateTime(3)));
        }

        return rows;
    }

    public async Task<int> CountConfigurationFilesAsync(int documentId)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM omp.ConfigOverlayConfigurationFiles WHERE ConfigOverlayDocumentId = @documentId;",
            conn);
        cmd.Parameters.AddWithValue("@documentId", documentId);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task SetEnabledAsync(int documentId, bool isEnabled)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE omp.ConfigOverlayDocuments SET IsEnabled = @isEnabled WHERE ConfigOverlayDocumentId = @documentId;",
            conn);
        cmd.Parameters.AddWithValue("@isEnabled", isEnabled);
        cmd.Parameters.AddWithValue("@documentId", documentId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ResetAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, @"
DELETE FROM omp.ConfigOverlayConfigurationFiles;
DELETE FROM omp.ConfigOverlayDocuments;");
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

    private static async Task ExecuteAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
