using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database provisioned by executing the real
/// core setup script (sql/1-setup-openmoduleplatform.sql) batch by batch, so
/// tests that assert schema-level guarantees stay bound to the shipped schema
/// file. Used to prove that omp.ConfigOverlayDocuments rejects a second
/// enabled document for the same overlay key and host at the database level.
/// </summary>
public sealed class ConfigOverlayActiveUniquenessTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_ConfigOverlayActiveUniqueness";

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();
        await ApplyCoreSetupScriptAsync();
    }

    public async Task DisposeAsync()
    {
        await DropDatabaseAsync();
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

    private async Task EnsureDatabaseExistsAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            builder.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");
    }

    private async Task ApplyCoreSetupScriptAsync()
    {
        var setupSql = ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql");

        // Strip the historical local development database switch, the same way
        // scripts/dev/embed-module-definition-sql.ps1 does, so the script runs
        // against the fixture database instead.
        var portableSql = Regex.Replace(
            setupSql,
            @"^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var batch in SplitBatches(portableSql))
        {
            await ExecuteNonQueryAsync(conn, batch);
        }
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection conn, string batch)
    {
        await using var cmd = new SqlCommand(batch, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static IEnumerable<string> SplitBatches(string sql)
    {
        return Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline)
            .Where(batch => !string.IsNullOrWhiteSpace(batch));
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }

    private static string ReadRepositoryTextFile(params string[] relativePathSegments)
    {
        var rootedSegment = relativePathSegments.FirstOrDefault(Path.IsPathRooted);
        if (rootedSegment is not null)
        {
            throw new ArgumentException("Repository test paths must be relative.", nameof(relativePathSegments));
        }

        var segments = new string[relativePathSegments.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(relativePathSegments, 0, segments, 1, relativePathSegments.Length);
        return File.ReadAllText(Path.Join(segments));
    }
}
