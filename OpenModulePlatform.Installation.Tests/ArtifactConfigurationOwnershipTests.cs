using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Installation.Tests;

/// <summary>
/// Proves the Bootstrapper's same-identity configuration replacement keeps operator-owned
/// rows: only package-delivered rows (those with a PackageFileContent baseline) are
/// stale-deletion candidates -- the same rule the HostAgent and Portal import paths use.
/// </summary>
public sealed class ArtifactConfigurationOwnershipTests : IClassFixture<ArtifactConfigurationOwnershipFixture>
{
    private const string ConfigPath = "appsettings.Site.json";
    private const string PackagedContent = "{ \"Theme\": \"default\" }";

    private readonly ArtifactConfigurationOwnershipFixture _fixture;

    public ArtifactConfigurationOwnershipTests(ArtifactConfigurationOwnershipFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Same_identity_replacement_keeps_operator_created_row_without_package_baseline()
    {
        var artifactId = await _fixture.InsertArtifactAsync("ownership-" + Guid.NewGuid().ToString("N"), "1.0.0");
        await ReplaceAsync(artifactId, [new ArtifactPackageConfigurationFile(ConfigPath, PackagedContent)]);
        // A row created by an operator carries no package baseline.
        await _fixture.InsertFileAsync(artifactId, "operator.extra.json", "{ \"proxy\": true }", packageFileContent: null);

        await ReplaceAsync(artifactId, [new ArtifactPackageConfigurationFile(ConfigPath, PackagedContent)]);

        var rows = await _fixture.GetFilesAsync(artifactId);
        Assert.Contains(rows, row => row.RelativePath == "operator.extra.json" && row.FileContent == "{ \"proxy\": true }");
        Assert.Contains(rows, row => row.RelativePath == ConfigPath);
    }

    [Fact]
    public async Task Same_identity_replacement_removes_package_row_the_package_no_longer_ships()
    {
        var artifactId = await _fixture.InsertArtifactAsync("stale-" + Guid.NewGuid().ToString("N"), "1.0.0");
        await ReplaceAsync(
            artifactId,
            [new ArtifactPackageConfigurationFile(ConfigPath, PackagedContent), new ArtifactPackageConfigurationFile("legacy.settings.json", "{ \"old\": true }")]);

        await ReplaceAsync(artifactId, [new ArtifactPackageConfigurationFile(ConfigPath, PackagedContent)]);

        var row = Assert.Single(await _fixture.GetFilesAsync(artifactId));
        Assert.Equal(ConfigPath, row.RelativePath);
    }

    private static async Task ReplaceAsync(int artifactId, IReadOnlyList<ArtifactPackageConfigurationFile> files)
    {
        await using var conn = new SqlConnection(ArtifactConfigurationOwnershipFixture.ConnectionString);
        await conn.OpenAsync();
        await InstallationEngine.ReplaceArtifactConfigurationFilesAsync(conn, artifactId, files);
    }
}

/// <summary>
/// Local SQL Server test database provisioned with the real core setup script
/// (sql/1-setup-openmoduleplatform.sql) for the Bootstrapper ownership tests.
/// </summary>
public sealed class ArtifactConfigurationOwnershipFixture : IAsyncLifetime
{
    // Per-process name (pid + start time) so concurrent test hosts never share a
    // database; stale copies from crashed runs are swept by the provisioner.
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("InstallOwnership");

    public static string ConnectionString { get; } = ForDatabase(DatabaseName);

    private int _moduleId;

    public async Task InitializeAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            builder.ConnectionString,
            $@"
IF DB_ID(N'{DatabaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{DatabaseName}];
END;
CREATE DATABASE [{DatabaseName}];");
        await ApplyCoreSetupScriptAsync();

        _moduleId = await ScalarAsync(
            @"
INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName)
VALUES (N'installation-ownership', N'installation-ownership', N'core', N'omp');
SELECT CAST(SCOPE_IDENTITY() AS int);");
    }

    public async Task DisposeAsync()
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
        catch (SqlException ex)
        {
            // A failed drop must not fail the run, but it must not be silent either:
            // the shared log is what CI turns into a warning about the leaked database.
            OmpTestCleanupLog.RecordFailure(
                nameof(ArtifactConfigurationOwnershipFixture),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
    }

    public Task<int> InsertArtifactAsync(string appKey, string version)
        => ScalarAsync(
            @"
DECLARE @appId int;
INSERT INTO omp.Apps (ModuleId, AppKey, DisplayName, AppType)
VALUES (@moduleId, @appKey, @appKey, N'web');
SET @appId = CAST(SCOPE_IDENTITY() AS int);
INSERT INTO omp.Artifacts (AppId, Version, PackageType, TargetName, RelativePath, Sha256, IsEnabled, CreatedUtc)
VALUES (@appId, @version, N'web-app', N'site', CONCAT(N'web/', @appId, N'/', @version), REPLICATE(N'a', 64), 1,
        DATEADD(SECOND, (SELECT COUNT(1) FROM omp.Artifacts), SYSUTCDATETIME()));
SELECT CAST(SCOPE_IDENTITY() AS int);",
            ("@moduleId", (object)_moduleId),
            ("@appKey", (object)appKey),
            ("@version", (object)version));

    public async Task InsertFileAsync(int artifactId, string relativePath, string fileContent, string? packageFileContent)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.ArtifactConfigurationFiles (ArtifactId, RelativePath, FileContent, PackageFileContent, IsEnabled)
VALUES (@artifactId, @relativePath, @fileContent, @packageFileContent, 1);",
            conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);
        cmd.Parameters.AddWithValue("@fileContent", fileContent);
        cmd.Parameters.AddWithValue("@packageFileContent", (object?)packageFileContent ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<(string RelativePath, string FileContent, string? PackageFileContent, bool IsEnabled)>> GetFilesAsync(
        int artifactId)
    {
        var rows = new List<(string, string, string?, bool)>();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
SELECT RelativePath, FileContent, PackageFileContent, IsEnabled
FROM omp.ArtifactConfigurationFiles
WHERE ArtifactId = @artifactId
ORDER BY RelativePath;",
            conn);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add((
                rdr.GetString(0),
                rdr.GetString(1),
                rdr.IsDBNull(2) ? null : rdr.GetString(2),
                rdr.GetBoolean(3)));
        }

        return rows;
    }

    private static string ForDatabase(string databaseName)
    {
        var overrideString = Environment.GetEnvironmentVariable("OMP_TEST_CONNECTION_STRING");
        var builder = string.IsNullOrWhiteSpace(overrideString)
            ? new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }
            : new SqlConnectionStringBuilder(overrideString);

        builder.InitialCatalog = databaseName;
        return builder.ConnectionString;
    }

    private static async Task ApplyCoreSetupScriptAsync()
    {
        var setupSql = OmpRepositoryFiles.ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql");

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

        foreach (var batch in Regex.Split(portableSql, @"^\s*GO\s*$", RegexOptions.Multiline)
            .Where(batch => !string.IsNullOrWhiteSpace(batch)))
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
