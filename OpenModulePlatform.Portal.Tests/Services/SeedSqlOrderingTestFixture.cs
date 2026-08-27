using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using HostAgentSqlConnectionFactory = OpenModulePlatform.HostAgent.Runtime.Services.SqlConnectionFactory;
using OmpHostArtifactRepository = OpenModulePlatform.HostAgent.Runtime.Services.OmpHostArtifactRepository;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database provisioned by executing the real
/// core setup script (sql/1-setup-openmoduleplatform.sql), so the seed-SQL
/// ordering tests run the full universal-package import pipeline against the
/// shipped schema instead of a hand-maintained minimal DDL.
/// </summary>
public sealed class SeedSqlOrderingTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_SeedSqlOrdering";

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

        // The whole cleanup is best-effort, INCLUDING opening the connection: a failed
        // open (unreachable instance, rejected encryption) must not surface as an xUnit
        // class-cleanup failure after the tests themselves already reported their result.
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
        catch (SqlException)
        {
            // Best-effort cleanup.
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup (connection could not be opened).
        }
    }

    public OmpAdminRepository CreatePortalRepository()
        => new(CreatePortalConnectionFactory());

    public PortalSqlConnectionFactory CreatePortalConnectionFactory()
        => new(BuildConfiguration());

    public OmpHostArtifactRepository CreateHostAgentRepository()
        => new(new HostAgentSqlConnectionFactory(BuildConfiguration()));

    public async Task<int> InsertModuleAsync(string moduleKey, string schemaName)
    {
        return Convert.ToInt32(await ExecuteScalarAsync(
            @"
INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName, IsEnabled)
VALUES (@p0, @p0, N'WorkerModule', @p1, 1);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            moduleKey,
            schemaName));
    }

    public async Task<int> InsertAppAsync(int moduleId, string appKey)
    {
        return Convert.ToInt32(await ExecuteScalarAsync(
            @"
INSERT INTO omp.Apps (ModuleId, AppKey, DisplayName, AppType, IsEnabled)
VALUES (@p0, @p1, @p1, N'Worker', 1);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            moduleId,
            appKey));
    }

    public async Task<int> InsertArtifactAsync(
        int appId,
        string version,
        string packageType,
        string targetName,
        string relativePath,
        string sha256)
    {
        return Convert.ToInt32(await ExecuteScalarAsync(
            @"
INSERT INTO omp.Artifacts (AppId, Version, PackageType, TargetName, RelativePath, Sha256, IsEnabled)
VALUES (@p0, @p1, @p2, @p3, @p4, @p5, 1);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            appId,
            version,
            packageType,
            targetName,
            relativePath,
            sha256));
    }

    public async Task<bool> ArtifactExistsAsync(int appId, string version, string packageType, string targetName)
    {
        var result = await ExecuteScalarAsync(
            @"
SELECT COUNT(1)
FROM omp.Artifacts
WHERE AppId = @p0 AND Version = @p1 AND PackageType = @p2 AND TargetName = @p3;",
            appId,
            version,
            packageType,
            targetName);
        return Convert.ToInt32(result) > 0;
    }

    public async Task<int> CountSucceededDefinitionSqlExecutionsAsync(string moduleKey, string definitionVersion)
    {
        var result = await ExecuteScalarAsync(
            @"
SELECT COUNT(1)
FROM omp.ModuleDefinitionSqlExecutions e
INNER JOIN omp.ModuleDefinitionDocuments d ON d.ModuleDefinitionDocumentId = e.ModuleDefinitionDocumentId
WHERE d.ModuleKey = @p0 AND d.DefinitionVersion = @p1 AND e.ExecutionStatus = N'Succeeded';",
            moduleKey,
            definitionVersion);
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<string>> GetSeededVersionsAsync(string schemaName)
    {
        var versions = new List<string>();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"SELECT [Version] FROM [{schemaName}].[SeededVersions] ORDER BY [Version];",
            conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            versions.Add(rdr.GetString(0));
        }

        return versions;
    }

    public async Task ExecuteAsync(params string[] batches)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        foreach (var batch in batches)
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<object?> ExecuteScalarAsync(string sql, params object[] parameters)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        for (var index = 0; index < parameters.Length; index++)
        {
            cmd.Parameters.AddWithValue($"@p{index}", parameters[index]);
        }

        return await cmd.ExecuteScalarAsync();
    }

    private IConfiguration BuildConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = ConnectionString
            })
            .Build();
}
