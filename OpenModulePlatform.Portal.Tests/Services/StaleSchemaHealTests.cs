using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
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
    private const string WitnessKindSchemaName = "omp_witness_kind_tests";
    private const string WitnessProbeSchemaName = "omp_witness_probe_tests";
    private const string WitnessHostAgentSchemaName = "omp_witness_hostagent_tests";

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
    public async Task HostAgent_SeedSql_WithSharedWebAndChannelVersion_IsBlockedBeforeGhostArtifactMutation()
    {
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        var repo = _fixture.CreateHostAgentRepository();
        var definitionJson = BuildArtifactPointerGuardDefinitionJson();
        var documentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            "artifact_pointer_guard_test",
            "1.0.0",
            definitionJson);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.ExecuteImportedModuleDefinitionSqlRepairsAsync(documentId, CancellationToken.None));

        Assert.Contains(
            "Module definition SQL must not register or mutate omp.Artifacts",
            exception.Message,
            StringComparison.Ordinal);
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
            [],
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

    // R12-G3: the witness used to answer only "is this schema/table there?". Every other
    // object kind fell into a default branch that answered "present" without looking, so a
    // migration that added a column, an index, a constraint or a trigger was skipped with a
    // green "package version is not newer than installed version".
    [Fact]
    public async Task Portal_Witness_ReportsEveryDeclaredObjectKindThatIsMissing()
    {
        await ResetWitnessSchemaAsync(WitnessKindSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        await CreateWitnessKindBaselineAsync();
        var repo = _fixture.CreatePortalRepository();

        var missing = await repo.GetMissingRequiredObjectsByScriptKeyAsync(
            BuildWitnessKindDefinitionJson(),
            CancellationToken.None);

        Assert.True(missing.TryGetValue("setup-witness", out var reported));
        Assert.Contains($"column {WitnessKindSchemaName}.Items.MissingColumn", reported);
        Assert.Contains($"index IX_Items_Missing on {WitnessKindSchemaName}.Items", reported);
        Assert.Contains($"constraint CK_Items_Missing on {WitnessKindSchemaName}.Items", reported);
        Assert.Contains($"trigger {WitnessKindSchemaName}.TR_Items_Missing", reported);

        // The objects that do exist must not be reported, or the witness would force a
        // repair on every single import and stop meaning anything.
        Assert.DoesNotContain(reported, item => item.Contains("Name", StringComparison.Ordinal));
        Assert.DoesNotContain(reported, item => item.Contains("IX_Items_Present", StringComparison.Ordinal));
        Assert.DoesNotContain(reported, item => item.Contains("CK_Items_Present", StringComparison.Ordinal));
        Assert.DoesNotContain(reported, item => item.Contains("TR_Items_Present", StringComparison.Ordinal));
        Assert.Equal(4, reported.Count);
    }

    // R12-G3: an object kind the witness cannot probe must answer "do not know" and force an
    // apply. Answering "present" is what let a declaration nobody could verify count as proof.
    [Fact]
    public async Task Portal_Witness_FailsClosedOnDeclarationsItCannotProbe()
    {
        await ResetWitnessSchemaAsync(WitnessKindSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        await CreateWitnessKindBaselineAsync();
        var repo = _fixture.CreatePortalRepository();

        var definition = BuildWitnessDefinitionRoot("witness_unverifiable_module", "setup-witness", "SELECT 1;");
        var integrity = (JsonObject)definition["integrity"]!;
        integrity["requiredSchemas"] = new JsonArray(WitnessKindSchemaName);
        integrity["requiredColumns"] = new JsonArray(
            new JsonObject
            {
                ["schema"] = WitnessKindSchemaName,
                ["name"] = "Name",
                ["source"] = "setup-witness"
            });
        integrity["requiredSequences"] = new JsonArray(
            new JsonObject
            {
                ["schema"] = WitnessKindSchemaName,
                ["name"] = "SomeSequence"
            });

        var missing = await repo.GetMissingRequiredObjectsByScriptKeyAsync(
            definition.ToJsonString(),
            CancellationToken.None);

        Assert.True(missing.TryGetValue("setup-witness", out var reported));
        Assert.Contains(reported, item => item.Contains("integrity.requiredColumns[0] is missing 'table'", StringComparison.Ordinal));
        Assert.Contains(reported, item => item.Contains("integrity.requiredSequences declares an object kind", StringComparison.Ordinal));
    }

    // R12-G3, the R4-B1 shape: every declared object exists, but the module's own validation
    // probe knows about an index the integrity block never mentioned. Before the fix the
    // witness returned empty and the import was skipped on version alone.
    [Fact]
    public async Task Portal_Witness_UsesValidationProbeWhenDeclaredObjectsAllExist()
    {
        await ResetWitnessSchemaAsync(WitnessProbeSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        await ExecuteAsync(
            $"EXEC(N'CREATE SCHEMA [{WitnessProbeSchemaName}]');",
            $"CREATE TABLE [{WitnessProbeSchemaName}].[Items](Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);");
        var repo = _fixture.CreatePortalRepository();
        var definitionJson = BuildWitnessProbeDefinitionJson(WitnessProbeSchemaName, "witness_probe_module");

        var missing = await repo.GetMissingRequiredObjectsByScriptKeyAsync(definitionJson, CancellationToken.None);

        Assert.Contains("setup-witness-probe", missing.Keys);
        Assert.Contains(missing["setup-witness-probe"], item => item.Contains("validation script", StringComparison.Ordinal));

        // The signal must actually heal the database, not just render a warning (§4.2).
        var documentId = await _fixture.InsertModuleDefinitionDocumentAsync(
            "witness_probe_module",
            "1.0.0",
            definitionJson);
        var repair = await repo.ExecuteModuleDefinitionSqlRepairsAsync(
            documentId,
            missing.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        Assert.True(repair.ExecutedCount > 0);
        Assert.True(await IndexExistsAsync(WitnessProbeSchemaName, "Items", "UX_Items_Name"));

        // And it must stop signalling once healed, or every import would repair forever.
        var afterHeal = await repo.GetMissingRequiredObjectsByScriptKeyAsync(definitionJson, CancellationToken.None);
        Assert.Empty(afterHeal);
    }

    // §4.1: the same witness lives in both repositories, and HostAgent folder import is the
    // path that runs unattended. This is the full skip scenario: installed version is newer,
    // so the definition is not applied -- but the missing index must still be healed.
    [Fact]
    public async Task HostAgent_Witness_HealsMissingIndexWhenInstalledVersionIsNewer()
    {
        await ResetWitnessSchemaAsync(WitnessHostAgentSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        await ExecuteAsync(
            $"EXEC(N'CREATE SCHEMA [{WitnessHostAgentSchemaName}]');",
            $"CREATE TABLE [{WitnessHostAgentSchemaName}].[Items](Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);");
        var repo = _fixture.CreateHostAgentRepository();
        var definitionJson = BuildWitnessProbeDefinitionJson(
            WitnessHostAgentSchemaName,
            "witness_hostagent_probe_module");

        var missing = await repo.GetMissingRequiredObjectsByScriptKeyAsync(definitionJson, CancellationToken.None);
        Assert.Contains("setup-witness-probe", missing.Keys);

        await _fixture.InsertModuleDefinitionDocumentAsync(
            "witness_hostagent_probe_module",
            "2.0.0",
            definitionJson,
            isApplied: true);

        var service = new ArtifactZipImportService(
            new StaticOptionsMonitor<HostAgentSettings>(new HostAgentSettings()),
            repo,
            NullLogger<ArtifactZipImportService>.Instance);
        var importMethod = typeof(ArtifactZipImportService).GetMethod(
            "ImportModuleDefinitionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            [typeof(ModuleDefinitionImportDocument), typeof(CancellationToken)],
            null)!;
        var document = new ModuleDefinitionImportDocument(
            "witness_hostagent_probe_module",
            "1.0.0",
            1,
            definitionJson,
            ComputeSha256(definitionJson),
            "test",
            [],
            []);

        var task = (Task)importMethod.Invoke(service, [document, CancellationToken.None])!;
        await task;
        var result = (ModuleDefinitionImportResult)task.GetType().GetProperty("Result")!.GetValue(task)!;

        Assert.False(result.Applied);
        Assert.True(result.SqlRepairCount > 0);
        Assert.True(await IndexExistsAsync(WitnessHostAgentSchemaName, "Items", "UX_Items_Name"));
    }

    private async Task CreateWitnessKindBaselineAsync()
    {
        await ExecuteAsync(
            $"EXEC(N'CREATE SCHEMA [{WitnessKindSchemaName}]');",
            $@"CREATE TABLE [{WitnessKindSchemaName}].[Items]
(
    Id int NOT NULL CONSTRAINT PK_witness_Items PRIMARY KEY,
    Name nvarchar(50) NOT NULL,
    CONSTRAINT CK_Items_Present CHECK(Id > 0)
);",
            $"CREATE INDEX IX_Items_Present ON [{WitnessKindSchemaName}].[Items](Name);",
            $"CREATE TRIGGER [{WitnessKindSchemaName}].[TR_Items_Present] ON [{WitnessKindSchemaName}].[Items] AFTER INSERT AS SET NOCOUNT ON;");
    }

    private static string BuildWitnessKindDefinitionJson()
    {
        var definition = BuildWitnessDefinitionRoot("witness_kind_module", "setup-witness", "SELECT 1;");
        var integrity = (JsonObject)definition["integrity"]!;
        integrity["requiredSchemas"] = new JsonArray(WitnessKindSchemaName);
        integrity["requiredTables"] = new JsonArray(RequiredObject(name: "Items"));
        integrity["requiredColumns"] = new JsonArray(
            RequiredObject("Items", "Name"),
            RequiredObject("Items", "MissingColumn"));
        integrity["requiredIndexes"] = new JsonArray(
            RequiredObject("Items", "IX_Items_Present"),
            RequiredObject("Items", "IX_Items_Missing"));
        integrity["requiredConstraints"] = new JsonArray(
            RequiredObject("Items", "CK_Items_Present"),
            RequiredObject("Items", "CK_Items_Missing"));
        integrity["requiredTriggers"] = new JsonArray(
            RequiredObject("Items", "TR_Items_Present"),
            RequiredObject("Items", "TR_Items_Missing"));
        return definition.ToJsonString();

        static JsonObject RequiredObject(string? table = null, string name = "Items")
        {
            var item = new JsonObject
            {
                ["schema"] = WitnessKindSchemaName,
                ["name"] = name,
                ["source"] = "setup-witness"
            };
            if (table is not null)
            {
                item["table"] = table;
            }

            return item;
        }
    }

    /// <summary>
    /// A repair that does not actually repair must not be reported as a heal.
    /// </summary>
    /// <remarks>
    /// Both import paths ran the repair scripts and then logged "Schema drift healed"
    /// with the list of objects that had been missing BEFORE the repair, without ever
    /// looking again. A repair that silently failed produced a message identical to one
    /// that worked. The comment sitting directly above that code in
    /// ArtifactZipImportService describes the exact failure it was allowing: R4-B1's
    /// unique index sat booked as fixed for four days while no database had it.
    ///
    /// Observed in a customer test environment 2026-08-23: an omp_portal import reported
    /// "Schema drift healed" and "0 failed" while its own validation probe said storage
    /// was missing a required object. Nothing in the output could tell an operator
    /// whether the repair worked.
    ///
    /// Here the setup script deliberately does NOT create the index the validation probe
    /// requires, so the repair cannot succeed. The import must say so.
    /// </remarks>
    [Fact]
    public async Task HostAgent_Witness_ReportsIncompleteWhenRepairDoesNotHeal()
    {
        await ResetWitnessSchemaAsync(WitnessHostAgentSchemaName);
        await _fixture.CleanModuleDefinitionDocumentsAsync();
        await ExecuteAsync(
            $"EXEC(N'CREATE SCHEMA [{WitnessHostAgentSchemaName}]');",
            $"CREATE TABLE [{WitnessHostAgentSchemaName}].[Items](Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);");
        var repo = _fixture.CreateHostAgentRepository();

        // Same shape as the healing witness, except the setup script creates only the
        // table. The validation probe still demands the index, so the repair runs and
        // changes nothing.
        var definitionJson = BuildUnhealableWitnessDefinitionJson(
            WitnessHostAgentSchemaName,
            "witness_unhealable_module");

        var missingBefore = await repo.GetMissingRequiredObjectsByScriptKeyAsync(definitionJson, CancellationToken.None);
        Assert.NotEmpty(missingBefore);

        await _fixture.InsertModuleDefinitionDocumentAsync(
            "witness_unhealable_module",
            "2.0.0",
            definitionJson,
            isApplied: true);

        var logger = new CapturingLogger<ArtifactZipImportService>();
        var service = new ArtifactZipImportService(
            new StaticOptionsMonitor<HostAgentSettings>(new HostAgentSettings()),
            repo,
            logger);
        var importMethod = typeof(ArtifactZipImportService).GetMethod(
            "ImportModuleDefinitionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            [typeof(ModuleDefinitionImportDocument), typeof(CancellationToken)],
            null)!;
        var document = new ModuleDefinitionImportDocument(
            "witness_unhealable_module",
            "1.0.0",
            1,
            definitionJson,
            ComputeSha256(definitionJson),
            "test",
            [],
            []);

        var task = (Task)importMethod.Invoke(service, [document, CancellationToken.None])!;
        await task;

        // The object is still missing, so nothing may claim it healed.
        var missingAfter = await repo.GetMissingRequiredObjectsByScriptKeyAsync(definitionJson, CancellationToken.None);
        Assert.NotEmpty(missingAfter);

        var messages = logger.Messages;
        Assert.DoesNotContain(messages, m => m.Contains("Schema drift healed", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Schema repair INCOMPLETE", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("STILL missing", StringComparison.Ordinal));
    }

    /// <summary>Like the probe witness, but the setup script cannot satisfy the probe.</summary>
    private static string BuildUnhealableWitnessDefinitionJson(string schemaName, string moduleKey)
    {
        var setupSql = $"IF SCHEMA_ID(N'{schemaName}') IS NULL EXEC(N'CREATE SCHEMA [{schemaName}]'); "
            + $"IF OBJECT_ID(N'{schemaName}.Items', N'U') IS NULL CREATE TABLE [{schemaName}].[Items](Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);";

        var validateSql = "SET NOCOUNT ON; SELECT CAST(CASE WHEN EXISTS("
            + "SELECT 1 FROM sys.indexes i INNER JOIN sys.tables t ON t.object_id = i.object_id "
            + "INNER JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + $"WHERE s.name = N'{schemaName}' AND t.name = N'Items' AND i.name = N'UX_Items_Never_Created'"
            + ") THEN 1 ELSE 0 END AS bit) AS IsHealthy, N'witness probe (unsatisfiable)' AS Message;";

        var definition = BuildWitnessDefinitionRoot(moduleKey, "setup-witness-unhealable", setupSql);
        ((JsonArray)definition["sqlScripts"]!).Add(new JsonObject
        {
            ["key"] = "validate-witness-unhealable",
            ["phase"] = "validate",
            ["order"] = 0,
            ["execution"] = "validate",
            ["inlineSql"] = validateSql
        });

        var integrity = (JsonObject)definition["integrity"]!;
        integrity["requiredSchemas"] = new JsonArray(schemaName);
        integrity["requiredTables"] = new JsonArray(new JsonObject
        {
            ["schema"] = schemaName,
            ["name"] = "Items",
            ["source"] = "setup-witness-unhealable"
        });
        return definition.ToJsonString();
    }

    /// <summary>Captures formatted log messages so a test can assert on what was reported.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _messages.Add(formatter(state, exception));
    }

    private static string BuildWitnessProbeDefinitionJson(string schemaName, string moduleKey)
    {
        var setupSql = $"IF SCHEMA_ID(N'{schemaName}') IS NULL EXEC(N'CREATE SCHEMA [{schemaName}]'); "
            + $"IF OBJECT_ID(N'{schemaName}.Items', N'U') IS NULL CREATE TABLE [{schemaName}].[Items](Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL); "
            + $"IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'{schemaName}.Items') AND name = N'UX_Items_Name') "
            + $"CREATE UNIQUE INDEX UX_Items_Name ON [{schemaName}].[Items](Name);";

        // Shaped like IbsPackager's 0-validate script: it probes an index, which is precisely
        // what the declared-object list could not express before R12-G3.
        var validateSql = "SET NOCOUNT ON; SELECT CAST(CASE WHEN EXISTS("
            + "SELECT 1 FROM sys.indexes i INNER JOIN sys.tables t ON t.object_id = i.object_id "
            + "INNER JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + $"WHERE s.name = N'{schemaName}' AND t.name = N'Items' AND i.name = N'UX_Items_Name'"
            + ") THEN 1 ELSE 0 END AS bit) AS IsHealthy, N'witness probe' AS Message;";

        var definition = BuildWitnessDefinitionRoot(moduleKey, "setup-witness-probe", setupSql);
        ((JsonArray)definition["sqlScripts"]!).Add(new JsonObject
        {
            ["key"] = "validate-witness-probe",
            ["phase"] = "validate",
            ["order"] = 0,
            ["execution"] = "validation",
            ["inlineSql"] = validateSql
        });

        // Everything the integrity block declares already exists in the database, so only the
        // validation probe can tell that the schema is stale.
        var integrity = (JsonObject)definition["integrity"]!;
        integrity["requiredSchemas"] = new JsonArray(schemaName);
        integrity["requiredTables"] = new JsonArray(new JsonObject
        {
            ["schema"] = schemaName,
            ["name"] = "Items",
            ["source"] = "setup-witness-probe"
        });
        return definition.ToJsonString();
    }

    private static JsonObject BuildWitnessDefinitionRoot(string moduleKey, string scriptKey, string setupSql)
        => new()
        {
            ["moduleKey"] = moduleKey,
            ["definitionVersion"] = "1.0.0",
            ["sqlScripts"] = new JsonArray(new JsonObject
            {
                ["key"] = scriptKey,
                ["phase"] = "setup",
                ["order"] = 10,
                ["execution"] = "idempotent",
                ["inlineSql"] = setupSql
            }),
            ["integrity"] = new JsonObject()
        };

    private async Task ResetWitnessSchemaAsync(string schemaName)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $@"
DECLARE @drop nvarchar(max) = N'';
SELECT @drop = @drop + N'DROP TABLE [' + s.name + N'].[' + t.name + N'];'
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = N'{schemaName}';
IF @drop <> N'' EXEC sp_executesql @drop;
IF SCHEMA_ID(N'{schemaName}') IS NOT NULL EXEC(N'DROP SCHEMA [{schemaName}]');",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(params string[] batches)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var batch in batches)
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<bool> IndexExistsAsync(string schemaName, string tableName, string indexName)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
SELECT 1
FROM sys.indexes i
INNER JOIN sys.tables t ON t.object_id = i.object_id
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schemaName
  AND t.name = @tableName
  AND i.name = @indexName;",
            conn);
        cmd.Parameters.AddWithValue("@schemaName", schemaName);
        cmd.Parameters.AddWithValue("@tableName", tableName);
        cmd.Parameters.AddWithValue("@indexName", indexName);
        return await cmd.ExecuteScalarAsync() is not null;
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

    private static string BuildArtifactPointerGuardDefinitionJson()
    {
        const string seedSql = """
IF OBJECT_ID(N'omp.Apps', N'U') IS NULL
    CREATE TABLE omp.Apps(AppId int NOT NULL PRIMARY KEY, AppKey nvarchar(100) NOT NULL);

IF OBJECT_ID(N'omp.Artifacts', N'U') IS NULL
    CREATE TABLE omp.Artifacts
    (
        ArtifactId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        AppId int NOT NULL,
        Version nvarchar(50) NOT NULL,
        PackageType nvarchar(50) NOT NULL,
        TargetName nvarchar(100) NOT NULL,
        RelativePath nvarchar(400) NULL,
        Sha256 nvarchar(128) NULL,
        IsEnabled bit NOT NULL
    );

IF OBJECT_ID(N'omp.InstanceTemplateAppInstances', N'U') IS NULL
    CREATE TABLE omp.InstanceTemplateAppInstances
    (
        InstanceTemplateAppInstanceId int NOT NULL PRIMARY KEY,
        AppId int NOT NULL,
        DesiredArtifactId int NULL
    );

IF NOT EXISTS (SELECT 1 FROM omp.Apps WHERE AppId = 3101)
    INSERT INTO omp.Apps(AppId, AppKey) VALUES(3101, N'web');
IF NOT EXISTS (SELECT 1 FROM omp.Apps WHERE AppId = 3102)
    INSERT INTO omp.Apps(AppId, AppKey) VALUES(3102, N'channel-type');
IF NOT EXISTS (SELECT 1 FROM omp.InstanceTemplateAppInstances WHERE InstanceTemplateAppInstanceId = 3101)
    INSERT INTO omp.InstanceTemplateAppInstances(InstanceTemplateAppInstanceId, AppId, DesiredArtifactId)
    VALUES(3101, 3101, NULL);

DECLARE @WebVersion nvarchar(50) = N'0.3.321';
DECLARE @ChannelTypeVersion nvarchar(50) = N'0.3.147';
DECLARE @Version nvarchar(50) = @ChannelTypeVersion;
DECLARE @WebArtifactId int;

MERGE omp.Artifacts AS target
USING (VALUES(3101, @Version, N'web-app', N'web')) AS source(AppId, Version, PackageType, TargetName)
ON target.AppId = source.AppId AND target.Version = source.Version AND target.PackageType = source.PackageType AND target.TargetName = source.TargetName
WHEN NOT MATCHED THEN
    INSERT(AppId, Version, PackageType, TargetName, RelativePath, Sha256, IsEnabled)
    VALUES(source.AppId, source.Version, source.PackageType, source.TargetName, N'web/' + source.Version, NULL, 1);

MERGE omp.Artifacts AS target
USING (VALUES(3102, @Version, N'channel-type', N'channel')) AS source(AppId, Version, PackageType, TargetName)
ON target.AppId = source.AppId AND target.Version = source.Version AND target.PackageType = source.PackageType AND target.TargetName = source.TargetName
WHEN NOT MATCHED THEN
    INSERT(AppId, Version, PackageType, TargetName, RelativePath, Sha256, IsEnabled)
    VALUES(source.AppId, source.Version, source.PackageType, source.TargetName, N'channel/' + source.Version, NULL, 1);

SELECT @WebArtifactId = ArtifactId
FROM omp.Artifacts
WHERE AppId = 3101 AND Version = @Version;

UPDATE omp.InstanceTemplateAppInstances
SET DesiredArtifactId = @WebArtifactId
WHERE InstanceTemplateAppInstanceId = 3101;
""";

        var root = new JsonObject
        {
            ["moduleKey"] = "artifact_pointer_guard_test",
            ["definitionVersion"] = "1.0.0",
            ["sqlScripts"] = new JsonArray(
                new JsonObject
                {
                    ["key"] = "seed-artifact-pointer-confusion",
                    ["phase"] = "setup",
                    ["order"] = 10,
                    ["execution"] = "idempotent",
                    ["inlineSql"] = seedSql
                })
        };
        return root.ToJsonString();
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
