using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// A local SQL Server test database provisioned from the real core setup script plus the example
/// web app module's setup script, so the Portal delete paths run their runtime maintenance steps
/// against the shipped schema and the example module's shipped tables.
/// </summary>
public sealed class ModuleRuntimeMaintenanceTestFixture : IAsyncLifetime
{
    // Per-process name (pid + start time) so concurrent test hosts never share a
    // database; stale copies from crashed runs are swept by the provisioner.
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("RuntimeMaintenance");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            master.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");

        await ExecuteScriptAsync(OmpRepositoryFiles.ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql"));
        await ExecuteScriptAsync(OmpRepositoryFiles.ReadRepositoryTextFile("examples", "WebAppModule", "Sql", "1-setup-example-webapp.sql"));
    }

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
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
            OmpTestCleanupLog.RecordFailure(
                nameof(ModuleRuntimeMaintenanceTestFixture),
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

    /// <summary>A module key the tests register to claim another module's schema.</summary>
    public const string IntruderModuleKey = "runtime_maintenance_intruder";

    /// <summary>Removes the example definition, its module registrations and every example runtime row.</summary>
    public Task ResetAsync() => ExecuteAsync($@"
DELETE FROM omp.ModuleDefinitionDocuments WHERE ModuleKey = N'example_webapp';
DELETE FROM omp.Modules WHERE ModuleKey IN (N'example_webapp', N'{IntruderModuleKey}');
DELETE FROM omp_example_webapp.RuntimeBindings WHERE RuntimeBindingId > 0;
DELETE FROM omp_example_webapp.RuntimeLeases WHERE RuntimeLeaseId > 0;");

    /// <summary>
    /// Stores the example definition (optionally rewritten by <paramref name="rewriteJson"/>) and
    /// registers the module in omp.Modules under <paramref name="registeredSchema"/>, as a
    /// definition import does.
    /// </summary>
    public Task InsertExampleDefinitionAsync(
        bool isApplied,
        Func<string, string>? rewriteJson = null,
        string registeredSchema = "omp_example_webapp")
        => ExecuteAsync(
            @"
INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName)
VALUES (N'example_webapp', N'Example web app', N'WebApp', @schema);
INSERT INTO omp.ModuleDefinitionDocuments
    (ModuleKey, DefinitionVersion, FormatVersion, DefinitionJson, DefinitionSha256, SourceName, IsApplied, AppliedUtc)
VALUES
    (N'example_webapp', N'runtime-maintenance-test', 1, @json, N'test', N'test', @isApplied,
     CASE WHEN @isApplied = 1 THEN SYSUTCDATETIME() END);",
            cmd =>
            {
                var json = OmpRepositoryFiles.ReadRepositoryTextFile("examples", "WebAppModule", "example_webapp.module-definition.json");
                cmd.Parameters.AddWithValue("@json", rewriteJson is null ? json : rewriteJson(json));
                cmd.Parameters.AddWithValue("@isApplied", isApplied);
                cmd.Parameters.AddWithValue("@schema", registeredSchema);
            });

    /// <summary>Registers another module that claims <paramref name="schema"/>.</summary>
    public Task RegisterIntruderModuleAsync(string schema)
        => ExecuteAsync(
            "INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName) VALUES (@key, N'Intruder', N'WebApp', @schema);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@key", IntruderModuleKey);
                cmd.Parameters.AddWithValue("@schema", schema);
            });

    public Task InsertBindingAsync(Guid appInstanceId, int? artifactId)
        => ExecuteAsync(
            "INSERT INTO omp_example_webapp.RuntimeBindings (BindingKey, AppInstanceId, ArtifactId) VALUES (N'binding', @appInstanceId, @artifactId);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
                cmd.Parameters.AddWithValue("@artifactId", (object?)artifactId ?? DBNull.Value);
            });

    public Task InsertLeaseAsync(Guid appInstanceId)
        => ExecuteAsync(
            "INSERT INTO omp_example_webapp.RuntimeLeases (AppInstanceId, HostId, ExpiresUtc) VALUES (@appInstanceId, NEWID(), SYSUTCDATETIME());",
            cmd => cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId));

    public Task<int> CountBindingsPinnedToAsync(int artifactId)
        => ScalarAsync(
            "SELECT COUNT(*) FROM omp_example_webapp.RuntimeBindings WHERE ArtifactId = @artifactId;",
            cmd => cmd.Parameters.AddWithValue("@artifactId", artifactId));

    public Task<int> CountLeasesAsync(Guid appInstanceId)
        => ScalarAsync(
            "SELECT COUNT(*) FROM omp_example_webapp.RuntimeLeases WHERE AppInstanceId = @appInstanceId;",
            cmd => cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId));

    private async Task ExecuteScriptAsync(string script)
    {
        // Strip the local development database switch, as the embed tool does.
        var portable = Regex.Replace(
            script,
            @"^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        foreach (var batch in Regex.Split(portable, @"^\s*GO\s*$", RegexOptions.Multiline).Where(static b => !string.IsNullOrWhiteSpace(b)))
        {
            await ExecuteAsync(batch);
        }
    }

    private async Task ExecuteAsync(string sql, Action<SqlCommand>? bind = null)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        bind?.Invoke(cmd);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string sql, Action<SqlCommand> bind)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        bind(cmd);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
