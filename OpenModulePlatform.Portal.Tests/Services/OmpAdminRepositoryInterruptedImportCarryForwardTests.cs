using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Proves that a Portal artifact import interrupted between registering the new
/// artifact row and writing its configuration rows does not lose the previous
/// version's operator edits on retry.
///
/// Both Portal import paths (ArtifactUpload and PortableModulePackageService)
/// commit omp.Artifacts through SaveArtifactAsync before the replace +
/// carry-forward transaction. A process death in between leaves the new identity
/// registered with no configuration rows, and the retry finds that identity and
/// takes the existing-identity branch, which calls
/// <see cref="OmpAdminRepository.ReplaceArtifactConfigurationFilesAsync"/>.
/// The tests reproduce exactly that database state against the real core schema.
/// </summary>
public sealed class OmpAdminRepositoryInterruptedImportCarryForwardTests
    : IClassFixture<InterruptedImportCarryForwardTestFixture>
{
    private const string ConfigPath = "appsettings.Site.json";
    private const string PackagedContent = "{ \"Theme\": \"default\" }";
    private const string OperatorEditedContent = "{ \"Theme\": \"customer-blue\" }";

    private readonly InterruptedImportCarryForwardTestFixture _fixture;
    private readonly OmpAdminRepository _repository;

    public OmpAdminRepositoryInterruptedImportCarryForwardTests(InterruptedImportCarryForwardTestFixture fixture)
    {
        _fixture = fixture;
        _repository = fixture.CreatePortalRepository();
    }

    [Fact]
    public async Task RetryAfterProcessDeathBeforeConfigurationRows_CarriesOperatorEditForward()
    {
        var appId = await _fixture.InsertAppAsync("interrupted-" + Guid.NewGuid().ToString("N"));
        var previousArtifactId = await _fixture.InsertArtifactAsync(appId, "1.0.0");
        await _repository.ReplaceAndCarryForwardArtifactConfigurationFilesAsync(
            previousArtifactId,
            Package(PackagedContent),
            CancellationToken.None);
        await _fixture.SetFileContentAsync(previousArtifactId, ConfigPath, OperatorEditedContent);

        // The first import committed the new artifact row and then died before
        // its configuration transaction ran.
        var newArtifactId = await _fixture.InsertArtifactAsync(appId, "1.0.1");
        Assert.Empty(await _fixture.GetFilesAsync(newArtifactId));

        // The retry finds the registered identity and takes the existing-identity path.
        await _repository.ReplaceArtifactConfigurationFilesAsync(
            newArtifactId,
            Package(PackagedContent),
            CancellationToken.None);

        var row = Assert.Single(await _fixture.GetFilesAsync(newArtifactId));
        Assert.Equal(OperatorEditedContent, row.FileContent);
        Assert.Equal(PackagedContent, row.PackageFileContent);
        Assert.True(row.IsEnabled);
    }

    [Fact]
    public async Task ReimportOfCompletedRegistration_DoesNotReapplyOverOperatorRevert()
    {
        var appId = await _fixture.InsertAppAsync("completed-" + Guid.NewGuid().ToString("N"));
        var previousArtifactId = await _fixture.InsertArtifactAsync(appId, "1.0.0");
        await _repository.ReplaceAndCarryForwardArtifactConfigurationFilesAsync(
            previousArtifactId,
            Package(PackagedContent),
            CancellationToken.None);
        await _fixture.SetFileContentAsync(previousArtifactId, ConfigPath, OperatorEditedContent);

        var newArtifactId = await _fixture.InsertArtifactAsync(appId, "1.0.1");
        var (_, carryForward) = await _repository.ReplaceAndCarryForwardArtifactConfigurationFilesAsync(
            newArtifactId,
            Package(PackagedContent),
            CancellationToken.None);
        Assert.Equal(
            ArtifactConfigurationCarryForwardOutcome.Preserved,
            Assert.Single(carryForward.Items).Outcome);

        // The operator deliberately reverts the new version to the package default.
        await _fixture.SetFileContentAsync(newArtifactId, ConfigPath, PackagedContent);

        await _repository.ReplaceArtifactConfigurationFilesAsync(
            newArtifactId,
            Package(PackagedContent),
            CancellationToken.None);

        var row = Assert.Single(await _fixture.GetFilesAsync(newArtifactId));
        Assert.Equal(PackagedContent, row.FileContent);
    }

    [Fact]
    public async Task SameIdentityReplacement_KeepsOperatorCreatedRowWithoutPackageBaseline()
    {
        var appId = await _fixture.InsertAppAsync("ownership-" + Guid.NewGuid().ToString("N"));
        var artifactId = await _fixture.InsertArtifactAsync(appId, "1.0.0");
        await _repository.ReplaceArtifactConfigurationFilesAsync(
            artifactId,
            Package(PackagedContent),
            CancellationToken.None);
        // A row created in the Portal editor carries no package baseline.
        await _fixture.InsertFileAsync(artifactId, "operator.extra.json", "{ \"proxy\": true }", packageFileContent: null);

        await _repository.ReplaceArtifactConfigurationFilesAsync(
            artifactId,
            Package(PackagedContent),
            CancellationToken.None);

        var rows = await _fixture.GetFilesAsync(artifactId);
        Assert.Contains(rows, row => row.RelativePath == "operator.extra.json" && row.FileContent == "{ \"proxy\": true }");
        Assert.Contains(rows, row => row.RelativePath == ConfigPath);
    }

    [Fact]
    public async Task SameIdentityReplacement_RemovesPackageRowThePackageNoLongerShips()
    {
        var appId = await _fixture.InsertAppAsync("stale-" + Guid.NewGuid().ToString("N"));
        var artifactId = await _fixture.InsertArtifactAsync(appId, "1.0.0");
        await _repository.ReplaceArtifactConfigurationFilesAsync(
            artifactId,
            [new ArtifactPackageConfigurationFile(ConfigPath, PackagedContent), new ArtifactPackageConfigurationFile("legacy.settings.json", "{ \"old\": true }")],
            CancellationToken.None);

        await _repository.ReplaceArtifactConfigurationFilesAsync(
            artifactId,
            Package(PackagedContent),
            CancellationToken.None);

        var row = Assert.Single(await _fixture.GetFilesAsync(artifactId));
        Assert.Equal(ConfigPath, row.RelativePath);
    }

    [Fact]
    public async Task RetryAfterProcessDeathBeforeContinuityCopy_RestoresRowsFromContinuitySource()
    {
        var appId = await _fixture.InsertAppAsync("zeroconfig-" + Guid.NewGuid().ToString("N"));
        var previousArtifactId = await _fixture.InsertArtifactAsync(appId, "1.0.0");
        await _repository.ReplaceAndCarryForwardArtifactConfigurationFilesAsync(
            previousArtifactId,
            Package(PackagedContent),
            CancellationToken.None);
        await _fixture.SetFileContentAsync(previousArtifactId, ConfigPath, OperatorEditedContent);

        // The first import committed the new artifact row and then died before
        // the continuity copy ran: the package ships no configuration files.
        var newArtifactId = await _fixture.InsertArtifactAsync(appId, "1.0.1");
        Assert.Empty(await _fixture.GetFilesAsync(newArtifactId));

        var resumed = await _repository.CopyConfigurationFilesFromContinuitySourceIfEmptyAsync(
            newArtifactId,
            CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.Equal(1, resumed.CopiedCount);
        var row = Assert.Single(await _fixture.GetFilesAsync(newArtifactId));
        Assert.Equal(OperatorEditedContent, row.FileContent);
        Assert.Equal(PackagedContent, row.PackageFileContent);

        // A second retry finds rows and resumes nothing.
        Assert.Null(await _repository.CopyConfigurationFilesFromContinuitySourceIfEmptyAsync(
            newArtifactId,
            CancellationToken.None));
    }

    private static IReadOnlyList<ArtifactPackageConfigurationFile> Package(string content)
        => [new ArtifactPackageConfigurationFile(ConfigPath, content)];
}

/// <summary>
/// Local SQL Server test database provisioned with the real core setup script
/// (sql/1-setup-openmoduleplatform.sql) for the interrupted-import tests.
/// </summary>
public sealed class InterruptedImportCarryForwardTestFixture : IAsyncLifetime
{
    // Per-process name (pid + start time) so concurrent test hosts never share a
    // database; stale copies from crashed runs are swept by the provisioner.
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("InterruptedImport");

    private int _moduleId;

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

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
        await CoreSetupScript.ApplyAsync(ConnectionString);

        _moduleId = await ScalarAsync(
            @"
INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName)
VALUES (N'interrupted-import', N'interrupted-import', N'core', N'omp');
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
                nameof(InterruptedImportCarryForwardTestFixture),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
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

    public Task<int> InsertAppAsync(string appKey)
        => ScalarAsync(
            @"
INSERT INTO omp.Apps (ModuleId, AppKey, DisplayName, AppType)
VALUES (@moduleId, @appKey, @appKey, N'web');
SELECT CAST(SCOPE_IDENTITY() AS int);",
            ("@moduleId", _moduleId),
            ("@appKey", appKey));

    /// <summary>
    /// Registers an artifact row the way SaveArtifactAsync commits it, with no
    /// configuration rows. CreatedUtc is spread per version so the continuity
    /// source ordering is deterministic.
    /// </summary>
    public Task<int> InsertArtifactAsync(int appId, string version)
        => ScalarAsync(
            @"
INSERT INTO omp.Artifacts (AppId, Version, PackageType, TargetName, RelativePath, Sha256, IsEnabled, CreatedUtc)
VALUES (@appId, @version, N'web-app', N'site', CONCAT(N'web/', @appId, N'/', @version), REPLICATE(N'a', 64), 1,
        DATEADD(SECOND, (SELECT COUNT(1) FROM omp.Artifacts), SYSUTCDATETIME()));
SELECT CAST(SCOPE_IDENTITY() AS int);",
            ("@appId", appId),
            ("@version", version));

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

    public async Task SetFileContentAsync(int artifactId, string relativePath, string fileContent)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
UPDATE omp.ArtifactConfigurationFiles
SET FileContent = @fileContent
WHERE ArtifactId = @artifactId AND RelativePath = @relativePath;",
            conn);
        cmd.Parameters.AddWithValue("@fileContent", fileContent);
        cmd.Parameters.AddWithValue("@artifactId", artifactId);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
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

    private async Task<int> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
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
