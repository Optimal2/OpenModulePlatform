using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// DeleteOrphanHostAsync runs the host-removed runtime maintenance steps that the applied module
/// definitions declare -- here the example web app module -- and nothing module-specific otherwise.
/// </summary>
public sealed class OmpHostArtifactRepositoryRuntimeMaintenanceTests : IDisposable
{
    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;

    public OmpHostArtifactRepositoryRuntimeMaintenanceTests()
    {
        _database = new OmpHostArtifactRepositoryTestDatabase();
        try
        {
            _repository = new OmpHostArtifactRepository(_database.CreateFactory());
            CreateDefinitionTableAndExampleModule();
        }
        catch
        {
            _database.Dispose();
            throw;
        }
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task DeleteOrphanHostAsync_WhenModuleDeclaresHostRemovedStep_ReleasesOnlyThatHostsModuleRows()
    {
        InsertExampleDefinition(isApplied: true);
        var removedHostId = _database.InsertHost("runtime-maintenance-removed", environment: null);
        var otherHostId = Guid.NewGuid();
        InsertLease(removedHostId);
        InsertLease(otherHostId);

        var deleted = await _repository.DeleteOrphanHostAsync(removedHostId, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(0, CountLeases(removedHostId));
        Assert.Equal(1, CountLeases(otherHostId));
    }

    [Fact]
    public async Task DeleteOrphanHostAsync_WhenNoAppliedDefinitionDeclaresSteps_LeavesModuleRowsUntouched()
    {
        // Imported but not applied: the contract in force declares nothing, so nothing runs.
        InsertExampleDefinition(isApplied: false);
        var hostId = _database.InsertHost("runtime-maintenance-unapplied", environment: null);
        InsertLease(hostId);

        var deleted = await _repository.DeleteOrphanHostAsync(hostId, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(1, CountLeases(hostId));
    }

    [Fact]
    public async Task DeleteOrphanHostAsync_WhenStoredStepIsUnsafe_RefusesAndKeepsHost()
    {
        // A definition row edited in the database after import is re-validated, not executed.
        var definition = JsonNode.Parse(ReadExampleDefinition())!;
        var step = definition["runtimeMaintenance"]!["steps"]!.AsArray()
            .Single(node => (string?)node!["event"] == "host-removed")!;
        step["inlineSql"] = "DELETE FROM omp.Hosts WHERE HostId = @HostId;";
        step["content"] = null;
        step["contentEncoding"] = null;
        var tampered = definition.ToJsonString();
        InsertDefinition(tampered, isApplied: true);
        var hostId = _database.InsertHost("runtime-maintenance-tampered", environment: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.DeleteOrphanHostAsync(hostId, CancellationToken.None));

        Assert.Contains("OMP-MODULE-RUNTIME-MAINTENANCE", ex.Message, StringComparison.Ordinal);
        Assert.True(_database.HostExists(hostId));
    }

    private static string ReadExampleDefinition()
        => OmpRepositoryFiles.ReadRepositoryTextFile("examples", "WebAppModule", "example_webapp.module-definition.json");

    private void CreateDefinitionTableAndExampleModule()
    {
        Execute(@"
CREATE TABLE omp.ModuleDefinitionDocuments
(
    ModuleDefinitionDocumentId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ModuleKey nvarchar(100) NOT NULL,
    DefinitionVersion nvarchar(50) NOT NULL,
    DefinitionJson nvarchar(max) NOT NULL,
    IsApplied bit NOT NULL,
    AppliedUtc datetime2(3) NULL,
    UpdatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);
IF OBJECT_ID(N'omp.Modules', N'U') IS NULL
    CREATE TABLE omp.Modules
    (
        ModuleId int NOT NULL PRIMARY KEY,
        ModuleKey nvarchar(100) NOT NULL,
        SchemaName nvarchar(128) NULL
    );");

        // The example module's own setup script creates the tables its steps release.
        var setup = OmpRepositoryFiles.ReadRepositoryTextFile("examples", "WebAppModule", "Sql", "1-setup-example-webapp.sql");
        foreach (var batch in Regex.Split(setup, @"^\s*GO\s*$", RegexOptions.Multiline).Where(static b => !string.IsNullOrWhiteSpace(b)))
        {
            Execute(batch);
        }
    }

    private void InsertExampleDefinition(bool isApplied) => InsertDefinition(ReadExampleDefinition(), isApplied);

    private void InsertDefinition(string json, bool isApplied)
    {
        using var conn = new SqlConnection(_database.ConnectionString);
        conn.Open();
        // The import registers the module and its platform-derived schema in omp.Modules; the
        // executor refuses steps of a module whose registration does not match.
        using var cmd = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM omp.Modules WHERE ModuleKey = N'example_webapp')
    INSERT INTO omp.Modules (ModuleId, ModuleKey, SchemaName) VALUES (900, N'example_webapp', N'omp_example_webapp');
INSERT INTO omp.ModuleDefinitionDocuments (ModuleKey, DefinitionVersion, DefinitionJson, IsApplied, AppliedUtc)
VALUES (N'example_webapp', N'test', @json, @isApplied, CASE WHEN @isApplied = 1 THEN SYSUTCDATETIME() END);", conn);
        cmd.Parameters.AddWithValue("@json", json);
        cmd.Parameters.AddWithValue("@isApplied", isApplied);
        cmd.ExecuteNonQuery();
    }

    private void InsertLease(Guid hostId)
    {
        using var conn = new SqlConnection(_database.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO omp_example_webapp.RuntimeLeases (AppInstanceId, HostId, ExpiresUtc)
VALUES (NEWID(), @hostId, DATEADD(minute, 5, SYSUTCDATETIME()));", conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        cmd.ExecuteNonQuery();
    }

    private int CountLeases(Guid hostId)
    {
        using var conn = new SqlConnection(_database.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM omp_example_webapp.RuntimeLeases WHERE HostId = @hostId;", conn);
        cmd.Parameters.AddWithValue("@hostId", hostId);
        return (int)cmd.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var conn = new SqlConnection(_database.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
}
