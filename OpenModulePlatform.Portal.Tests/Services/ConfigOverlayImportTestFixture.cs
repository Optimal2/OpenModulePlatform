using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Services;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database with the minimal overlay tables required
/// to exercise <see cref="OmpAdminRepository.SaveImportedConfigOverlayAsync"/>.
/// </summary>
public sealed class ConfigOverlayImportTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_ConfigOverlayImport";

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();
        await EnsureSchemaAsync();
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

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureSchemaAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await ExecuteAsync(conn, "IF SCHEMA_ID(N'omp') IS NULL EXEC(N'CREATE SCHEMA omp');");

        await ExecuteAsync(conn, @"
IF OBJECT_ID(N'omp.ConfigOverlayDocuments', N'U') IS NULL
CREATE TABLE omp.ConfigOverlayDocuments
(
    ConfigOverlayDocumentId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    OverlayKey nvarchar(200) NOT NULL,
    OverlayVersion nvarchar(50) NOT NULL,
    HostKey nvarchar(128) NOT NULL,
    ModuleKey nvarchar(100) NULL,
    ModuleDefinitionVersion nvarchar(50) NULL,
    AppKey nvarchar(100) NULL,
    PackageType nvarchar(50) NULL,
    TargetName nvarchar(200) NULL,
    ArtifactVersion nvarchar(50) NULL,
    FormatVersion int NOT NULL CONSTRAINT DF_Import_OverlayDocuments_FormatVersion DEFAULT(1),
    OverlayJson nvarchar(max) NOT NULL,
    OverlaySha256 nvarchar(128) NOT NULL,
    SourceName nvarchar(400) NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Import_OverlayDocuments_IsEnabled DEFAULT(1),
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Import_OverlayDocuments_CreatedUtc DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Import_OverlayDocuments_UpdatedUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Import_OverlayDocuments_Key_Host_Version UNIQUE(OverlayKey, HostKey, OverlayVersion)
);

IF OBJECT_ID(N'omp.ConfigOverlayConfigurationFiles', N'U') IS NULL
CREATE TABLE omp.ConfigOverlayConfigurationFiles
(
    ConfigOverlayConfigurationFileId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ConfigOverlayDocumentId int NOT NULL,
    RelativePath nvarchar(500) NOT NULL,
    FileContent nvarchar(max) NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_Import_OverlayFiles_IsEnabled DEFAULT(1)
);");
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
