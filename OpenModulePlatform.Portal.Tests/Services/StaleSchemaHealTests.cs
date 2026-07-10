using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class StaleSchemaHealTests : IClassFixture<StaleSchemaTestFixture>
{
    private const string PortalSchemaName = "omp_portal_stale_tests";
    private const string HostAgentSchemaName = "omp_hostagent_stale_tests";

    private readonly StaleSchemaTestFixture _fixture;

    public StaleSchemaHealTests(StaleSchemaTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Portal_GetMissingRequiredObjectsByScriptKeyAsync_ReturnsMissingTablesAndSchema()
    {
        await _fixture.DropSchemaObjectsAsync(PortalSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        var repo = _fixture.CreatePortalRepository();

        var missing = await repo.GetMissingRequiredObjectsByScriptKeyAsync(
            BuildPortalDefinitionJson(),
            CancellationToken.None);

        Assert.Equal(2, missing.Count);
        Assert.Contains("setup-stale-schema", missing.Keys);
        Assert.Contains("setup-stale-tables", missing.Keys);
        Assert.Contains($"schema {PortalSchemaName}", missing["setup-stale-schema"]);
        Assert.Contains($"table {PortalSchemaName}.Data", missing["setup-stale-tables"]);
    }

    [Fact]
    public async Task Portal_GetMissingRequiredObjectsByScriptKeyAsync_ReturnsEmptyWhenObjectsExist()
    {
        await _fixture.DropSchemaObjectsAsync(PortalSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        var repo = _fixture.CreatePortalRepository();
        var definitionJson = BuildPortalDefinitionJson();

        var documentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            "stale_test_module",
            "1.0.0",
            definitionJson);
        await repo.ExecuteModuleDefinitionSqlRepairsAsync(documentId, CancellationToken.None);

        var missing = await repo.GetMissingRequiredObjectsByScriptKeyAsync(definitionJson, CancellationToken.None);

        Assert.Empty(missing);
    }

    [Fact]
    public async Task Portal_ExecuteModuleDefinitionSqlRepairsAsync_Filtered_ExecutesOnlyTargetedScripts()
    {
        await _fixture.DropSchemaObjectsAsync(PortalSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        var repo = _fixture.CreatePortalRepository();
        var definitionJson = BuildPortalDefinitionJson();
        var documentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            "stale_test_module",
            "1.0.0",
            definitionJson);

        var result = await repo.ExecuteModuleDefinitionSqlRepairsAsync(
            documentId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "setup-stale-schema", "setup-stale-tables" },
            CancellationToken.None);

        Assert.Equal(2, result.ExecutedCount);
        Assert.Contains("setup-stale-schema", result.HealedScripts);
        Assert.Contains("setup-stale-tables", result.HealedScripts);
        Assert.DoesNotContain("setup-stale-untouched", result.HealedScripts);
        Assert.True(await _fixture.TableExistsAsync(PortalSchemaName, "Data"));
    }

    [Fact]
    public async Task HostAgent_GetMissingRequiredObjectsByScriptKeyAsync_ReturnsMissingTablesAndSchema()
    {
        await _fixture.DropSchemaObjectsAsync(HostAgentSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        var repo = _fixture.CreateHostAgentRepository();

        var missing = await repo.GetMissingRequiredObjectsByScriptKeyAsync(
            BuildHostAgentDefinitionJson(),
            CancellationToken.None);

        Assert.Equal(2, missing.Count);
        Assert.Contains("setup-hostagent-schema", missing.Keys);
        Assert.Contains("setup-hostagent-tables", missing.Keys);
        Assert.Contains($"schema {HostAgentSchemaName}", missing["setup-hostagent-schema"]);
        Assert.Contains($"table {HostAgentSchemaName}.Data", missing["setup-hostagent-tables"]);
    }

    [Fact]
    public async Task HostAgent_GetMissingRequiredObjectsByScriptKeyAsync_ReturnsEmptyWhenObjectsExist()
    {
        await _fixture.DropSchemaObjectsAsync(HostAgentSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        var repo = _fixture.CreateHostAgentRepository();
        var definitionJson = BuildHostAgentDefinitionJson();

        var documentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            "hostagent_stale_test_module",
            "1.0.0",
            definitionJson);
        await repo.ExecuteImportedModuleDefinitionSqlRepairsAsync(documentId, CancellationToken.None);

        var missing = await repo.GetMissingRequiredObjectsByScriptKeyAsync(definitionJson, CancellationToken.None);

        Assert.Empty(missing);
    }

    [Fact]
    public async Task HostAgent_ExecuteImportedModuleDefinitionSqlRepairsAsync_Filtered_ExecutesOnlyTargetedScript()
    {
        await _fixture.DropSchemaObjectsAsync(HostAgentSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        var repo = _fixture.CreateHostAgentRepository();
        var definitionJson = BuildHostAgentDefinitionJson();
        var documentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            "hostagent_stale_test_module",
            "1.0.0",
            definitionJson);

        var executed = await repo.ExecuteImportedModuleDefinitionSqlRepairsAsync(
            documentId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "setup-hostagent-tables" },
            CancellationToken.None);

        Assert.Equal(1, executed);
        Assert.True(await _fixture.TableExistsAsync(HostAgentSchemaName, "Data"));
    }

    [Fact]
    public async Task HostAgent_ImportModuleDefinitionAsync_HealsStaleSchemaWhenInstalledVersionIsNewer()
    {
        await _fixture.DropSchemaObjectsAsync(HostAgentSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        var repo = _fixture.CreateHostAgentRepository();
        var definitionJson = BuildHostAgentDefinitionJson();

        await _fixture.InsertModuleDefinitionDocumentAsync(
            "hostagent_stale_test_module",
            "2.0.0",
            definitionJson,
            isApplied: true);

        var settings = new HostAgentSettings();
        var monitor = new StaticOptionsMonitor<HostAgentSettings>(settings);
        var service = new ArtifactZipImportService(
            monitor,
            repo,
            NullLogger<ArtifactZipImportService>.Instance);

        var importMethod = typeof(ArtifactZipImportService).GetMethod(
            "ImportModuleDefinitionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            [typeof(ModuleDefinitionImportDocument), typeof(CancellationToken)],
            null)!;

        var document = new ModuleDefinitionImportDocument(
            "hostagent_stale_test_module",
            "1.0.0",
            1,
            definitionJson,
            ComputeSha256(definitionJson),
            "test",
            []);

        var task = (Task)importMethod.Invoke(service, [document, CancellationToken.None])!;
        await task;
        var resultProperty = task.GetType().GetProperty("Result")!;
        var result = (ModuleDefinitionImportResult)resultProperty.GetValue(task)!;

        Assert.False(result.Applied);
        Assert.True(result.SqlRepairCount > 0);
        Assert.Contains("setup-hostagent-tables", result.HealedScripts);
        Assert.True(await _fixture.TableExistsAsync(HostAgentSchemaName, "Data"));
    }

    private static string BuildPortalDefinitionJson()
    {
        var setupSchemaSql = $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{PortalSchemaName}') EXEC(N'CREATE SCHEMA [{PortalSchemaName}]');";
        var setupTablesSql = $"IF OBJECT_ID(N'{PortalSchemaName}.Data', N'U') IS NULL CREATE TABLE [{PortalSchemaName}].[Data] (Id int NOT NULL PRIMARY KEY);";
        var untouchedSql = $"IF OBJECT_ID(N'{PortalSchemaName}.ShouldNotExist', N'U') IS NULL CREATE TABLE [{PortalSchemaName}].[ShouldNotExist] (Id int NOT NULL PRIMARY KEY);";

        return $$"""
{
  "moduleKey": "stale_test_module",
  "definitionVersion": "1.0.0",
  "sqlScripts": [
    {
      "key": "setup-stale-schema",
      "phase": "setup",
      "order": 10,
      "execution": "idempotent",
      "inlineSql": "{{setupSchemaSql.Replace("\"", "\\\"")}}"
    },
    {
      "key": "setup-stale-tables",
      "phase": "setup",
      "order": 20,
      "execution": "idempotent",
      "inlineSql": "{{setupTablesSql.Replace("\"", "\\\"")}}"
    },
    {
      "key": "setup-stale-untouched",
      "phase": "setup",
      "order": 30,
      "execution": "idempotent",
      "inlineSql": "{{untouchedSql.Replace("\"", "\\\"")}}"
    }
  ],
  "integrity": {
    "requiredSchemas": ["{{PortalSchemaName}}"],
    "requiredTables": [
      { "schema": "{{PortalSchemaName}}", "name": "Data", "source": "setup-stale-tables" }
    ]
  }
}
""";
    }

    private static string BuildHostAgentDefinitionJson()
    {
        var setupSchemaSql = $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{HostAgentSchemaName}') EXEC(N'CREATE SCHEMA [{HostAgentSchemaName}]');";
        var setupTablesSql = $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{HostAgentSchemaName}') EXEC(N'CREATE SCHEMA [{HostAgentSchemaName}]'); IF OBJECT_ID(N'{HostAgentSchemaName}.Data', N'U') IS NULL CREATE TABLE [{HostAgentSchemaName}].[Data] (Id int NOT NULL PRIMARY KEY);";

        return $$"""
{
  "moduleKey": "hostagent_stale_test_module",
  "definitionVersion": "1.0.0",
  "sqlScripts": [
    {
      "key": "setup-hostagent-schema",
      "phase": "setup",
      "order": 10,
      "execution": "idempotent",
      "inlineSql": "{{setupSchemaSql.Replace("\"", "\\\"")}}"
    },
    {
      "key": "setup-hostagent-tables",
      "phase": "setup",
      "order": 20,
      "execution": "idempotent",
      "inlineSql": "{{setupTablesSql.Replace("\"", "\\\"")}}"
    }
  ],
  "integrity": {
    "requiredSchemas": ["{{HostAgentSchemaName}}"],
    "requiredTables": [
      { "schema": "{{HostAgentSchemaName}}", "name": "Data", "source": "setup-hostagent-tables" }
    ]
  }
}
""";
    }

    private static string ComputeSha256(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : Microsoft.Extensions.Options.IOptionsMonitor<T>
        where T : class
    {
        public T CurrentValue => currentValue;

        public T Get(string? name) => currentValue;

        public IDisposable OnChange(Action<T, string?> listener) => new NullDisposable();

        private sealed class NullDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
