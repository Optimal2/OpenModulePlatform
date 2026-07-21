using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database provisioned by executing the real
/// core setup script (sql/1-setup-openmoduleplatform.sql) batch by batch, so
/// tests that assert schema-level guarantees stay bound to the shipped schema
/// file. Used to prove that omp.Artifacts rejects duplicate artifact identities
/// at the database level.
/// </summary>
public sealed class ArtifactIdentityUniquenessTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_ArtifactIdentity";

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

    public async Task<int> InsertModuleAsync(string moduleKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName)
VALUES (@moduleKey, @moduleKey, N'core', N'omp');
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@moduleKey", moduleKey);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> InsertAppAsync(int moduleId, string appKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.Apps (ModuleId, AppKey, DisplayName, AppType)
VALUES (@moduleId, @appKey, @appKey, N'web');
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@moduleId", moduleId);
        cmd.Parameters.AddWithValue("@appKey", appKey);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Inserts an artifact row with raw SQL, deliberately bypassing all
    /// application-level duplicate checks, so the test only passes when the
    /// database schema itself rejects duplicate identities.
    /// </summary>
    public async Task<int> InsertArtifactAsync(
        int appId,
        string version,
        string packageType,
        string? targetName,
        string relativePath,
        string sha256)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.Artifacts (AppId, Version, PackageType, TargetName, RelativePath, Sha256, IsEnabled)
VALUES (@appId, @version, @packageType, @targetName, @relativePath, @sha256, 1);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@appId", appId);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@packageType", packageType);
        cmd.Parameters.AddWithValue("@targetName", (object?)targetName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);
        cmd.Parameters.AddWithValue("@sha256", sha256);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
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
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> SplitBatches(string sql)
    {
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline))
        {
            if (!string.IsNullOrWhiteSpace(batch))
            {
                yield return batch;
            }
        }
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
