using System.Text.Json;
using System.Text.Json.Nodes;
using OpenModulePlatform.ModuleDefinitions;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// The runtimeMaintenance contract (docs/MODULE_DEFINITIONS.md, "Runtime maintenance steps"):
/// the example module's declared steps pass, and every safety gate refuses what it exists to refuse.
/// </summary>
public sealed class ModuleRuntimeMaintenanceTests
{
    // The platform derives a module's schema from its key: omp_<moduleKey>.
    private const string ModuleKey = "mod_key";
    private const string Schema = "omp_mod_key";

    [Fact]
    public void ExampleWebAppDefinition_DeclaresOneValidStepForEveryEvent()
    {
        var json = OmpRepositoryFiles.ReadRepositoryTextFile("examples", "WebAppModule", "example_webapp.module-definition.json");

        var steps = ModuleRuntimeMaintenance.ReadSteps(json);

        Assert.Equal(
            ModuleRuntimeMaintenance.Events.Keys.OrderBy(static key => key, StringComparer.Ordinal),
            steps.Select(static step => step.Event).OrderBy(static key => key, StringComparer.Ordinal));
        Assert.All(steps, step =>
        {
            Assert.Equal("example_webapp", step.ModuleKey);
            Assert.Equal("omp_example_webapp", step.SchemaName);
        });
    }

    [Fact]
    public void DefinitionWithoutSection_HasNoSteps()
    {
        var json = OmpRepositoryFiles.ReadRepositoryTextFile("examples", "WorkerAppModule", "example_workerapp.module-definition.json");

        Assert.Empty(ModuleRuntimeMaintenance.ReadSteps(json));
    }

    public static TheoryData<string, string> SafeSteps() => new()
    {
        {
            ModuleRuntimeMaintenance.HostRemoved,
            $"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL DELETE FROM {Schema}.Leases WHERE HostId = @HostId;"
        },
        {
            // Joining platform rows is a read and allowed; this is the shape of a worker-keyed release.
            ModuleRuntimeMaintenance.HostRemoved,
            $@"SET NOCOUNT ON;
IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL
BEGIN
    UPDATE {Schema}.Leases
    SET WorkerInstanceId = NULL
    WHERE WorkerInstanceId IN (SELECT WorkerInstanceId FROM omp.WorkerInstances WHERE HostId = @HostId);
END;"
        },
        {
            ModuleRuntimeMaintenance.ArtifactRemoved,
            $"IF (OBJECT_ID(N'[{Schema}].[Bindings]') IS NOT NULL AND 1 = 1) UPDATE [{Schema}].[Bindings] SET ArtifactId = NULL WHERE ArtifactId = @ArtifactId;"
        },
        {
            ModuleRuntimeMaintenance.AppInstanceBlockingCount,
            $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS BlockingCount FROM {Schema}.Bindings WHERE AppInstanceId = @AppInstanceId;"
        },
        {
            // The parameter may restrict the write through an inner join on the target table.
            ModuleRuntimeMaintenance.HostRemoved,
            $@"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL
    UPDATE {Schema}.Leases
    SET WorkerInstanceId = NULL
    FROM {Schema}.Leases
    INNER JOIN omp.WorkerInstances w ON w.WorkerInstanceId = {Schema}.Leases.WorkerInstanceId AND w.HostId = @HostId
    WHERE {Schema}.Leases.WorkerInstanceId IS NOT NULL;"
        },
        {
            // ... or through an EXISTS subquery.
            ModuleRuntimeMaintenance.HostRemoved,
            $"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL DELETE FROM {Schema}.Leases WHERE Expired = 1 AND EXISTS (SELECT 1 FROM omp.WorkerInstances w WHERE w.WorkerInstanceId = {Schema}.Leases.WorkerInstanceId AND w.HostId = @HostId);"
        },
    };

    [Theory]
    [MemberData(nameof(SafeSteps))]
    public void SafeStep_IsAccepted(string eventName, string sql)
    {
        Assert.Null(ModuleRuntimeMaintenance.ValidateStepSql(sql, Schema, ModuleRuntimeMaintenance.Events[eventName]));
    }

    public static TheoryData<string, string, string, string> UnsafeSteps()
    {
        const string guard = $"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL ";
        return new()
        {
            { "platform table write", "may only write the module schema", ModuleRuntimeMaintenance.HostRemoved, "IF OBJECT_ID(N'omp.WorkerInstances', N'U') IS NOT NULL DELETE FROM omp.WorkerInstances WHERE HostId = @HostId;" },
            { "other module write", "may only write the module schema", ModuleRuntimeMaintenance.HostRemoved, "IF OBJECT_ID(N'other_module.Leases', N'U') IS NOT NULL DELETE FROM other_module.Leases WHERE HostId = @HostId;" },
            { "other module read", "may only reference the module schema", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId IN (SELECT HostId FROM other_module.Hosts WHERE HostId = @HostId);" },
            { "no OBJECT_ID guard", "must be referenced inside IF OBJECT_ID", ModuleRuntimeMaintenance.HostRemoved, $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "IS NULL guard", "must be referenced inside IF OBJECT_ID", ModuleRuntimeMaintenance.HostRemoved, $"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NULL DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "OR guard", "must be referenced inside IF OBJECT_ID", ModuleRuntimeMaintenance.HostRemoved, $"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL OR 1 = 1 DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "guard for another table", "must be referenced inside IF OBJECT_ID", ModuleRuntimeMaintenance.HostRemoved, $"IF OBJECT_ID(N'{Schema}.Other', N'U') IS NOT NULL DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "write in ELSE", "must be referenced inside IF OBJECT_ID", ModuleRuntimeMaintenance.HostRemoved, guard + $"BEGIN SELECT @HostId; END ELSE DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "dynamic SQL", "ExecuteStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"EXEC sp_executesql N'DELETE FROM {Schema}.Leases WHERE HostId = @h', N'@h uniqueidentifier', @h = @HostId;" },
            { "procedure call", "ExecuteStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"EXEC {Schema}.ReleaseLeases @HostId;" },
            { "DDL", "DropTableStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DROP TABLE {Schema}.Leases;" },
            { "DELETE without WHERE", "must have a WHERE clause", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases; SELECT @HostId;" },
            { "UPDATE without WHERE", "must have a WHERE clause", ModuleRuntimeMaintenance.HostRemoved, guard + $"UPDATE {Schema}.Leases SET HostId = @HostId;" },
            { "parameter unused", "must use the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"INSERT INTO {Schema}.Leases (HostId) VALUES (NULL);" },
            { "parameter redeclared", "must not be declared", ModuleRuntimeMaintenance.HostRemoved, $"DECLARE @HostId uniqueidentifier = NEWID(); " + guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "two batches", "exactly one batch", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;\nGO\nSELECT 1;" },
            { "alias write target", "use the qualified table name, not an alias", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE l FROM {Schema}.Leases l WHERE l.HostId = @HostId;" },
            { "cross-database", "Cross-database", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM OtherDb.{Schema}.Leases WHERE HostId = @HostId;" },
            { "OUTPUT INTO", "OUTPUT INTO is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases OUTPUT deleted.HostId INTO {Schema}.Leases(HostId) WHERE HostId = @HostId;" },
            { "SELECT INTO", "SELECT INTO is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"SELECT * INTO {Schema}.Copy FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "transaction control", "BeginTransactionStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, "BEGIN TRANSACTION; " + guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId; COMMIT;" },
            { "unqualified table", "use the qualified table name, not an alias", ModuleRuntimeMaintenance.HostRemoved, guard + "DELETE FROM Leases WHERE HostId = @HostId;" },
            { "OPENROWSET", "Table source", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId IN (SELECT HostId FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1') r) AND HostId = @HostId;" },
            { "SET option", "PredicateSetStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, "SET XACT_ABORT OFF; " + guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "write in read-only event", "is read-only", ModuleRuntimeMaintenance.AppInstanceBlockingCount, guard + $"DELETE FROM {Schema}.Leases WHERE AppInstanceId = @AppInstanceId;" },
            { "unparseable", "cannot be parsed", ModuleRuntimeMaintenance.ArtifactRemoved, "UPDATE WHERE @ArtifactId" },
            { "platform module schema write", "may only write the module schema", ModuleRuntimeMaintenance.HostRemoved, "IF OBJECT_ID(N'omp_portal.Leases', N'U') IS NOT NULL DELETE FROM omp_portal.Leases WHERE HostId = @HostId;" },
            { "platform module schema read", "may only reference the module schema", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId IN (SELECT HostId FROM omp_portal.Hosts WHERE HostId = @HostId);" },
            // The WHERE clause of each write, not merely the step, must carry the event parameter.
            { "parameter only in a separate SELECT", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE 1 = 1; SELECT @HostId;" },
            { "parameter only in SET", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"UPDATE {Schema}.Leases SET HostId = @HostId WHERE HostId IS NOT NULL;" },
            { "parameter diluted by OR", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId OR 1 = 1;" },
            { "parameter under NOT IN", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId NOT IN (SELECT HostId FROM omp.Hosts WHERE HostId = @HostId);" },
            { "parameter under inequality", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId <> @HostId;" },
            { "join that excludes the target", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases FROM omp.WorkerInstances w INNER JOIN omp.Hosts h ON h.HostId = w.HostId AND h.HostId = @HostId WHERE 1 = 1;" },
            { "second DELETE unbound", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"BEGIN DELETE FROM {Schema}.Leases WHERE HostId = @HostId; DELETE FROM {Schema}.Leases WHERE 1 = 1; END" },
            { "MERGE deleting unmatched rows", "WHEN NOT MATCHED BY SOURCE", ModuleRuntimeMaintenance.HostRemoved, guard + $"MERGE {Schema}.Leases AS t USING (SELECT @HostId AS HostId) AS s ON t.HostId = s.HostId AND t.HostId = @HostId WHEN NOT MATCHED BY SOURCE THEN DELETE;" },
            { "MERGE ON unbound", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"MERGE {Schema}.Leases AS t USING (SELECT @HostId AS HostId) AS s ON t.HostId = s.HostId WHEN MATCHED THEN DELETE;" },
            { "read-only with two SELECTs", "exactly one SELECT", ModuleRuntimeMaintenance.AppInstanceBlockingCount, $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS BlockingCount FROM {Schema}.Bindings WHERE 1 = 1; SELECT @AppInstanceId;" },
            { "read-only SELECT unbound", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.AppInstanceBlockingCount, $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS BlockingCount, @AppInstanceId AS Id FROM {Schema}.Bindings WHERE AppInstanceId IS NOT NULL;" },
        };
    }

    [Theory]
    [MemberData(nameof(UnsafeSteps))]
    public void UnsafeStep_IsRejected(string description, string expectedReason, string eventName, string sql)
    {
        var error = ModuleRuntimeMaintenance.ValidateStepSql(sql, Schema, ModuleRuntimeMaintenance.Events[eventName]);

        // Each case must be refused by the gate it targets, not by a typo in the probe.
        Assert.True(error is not null, $"Expected '{description}' to be rejected.");
        Assert.Contains(expectedReason, error, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> InvalidDocuments() => new()
    {
        { "unknown event", Document(step => step["event"] = "host-maintenance") },
        { "wrong execution", Document(step => step["execution"] = "read-only") },
        { "unknown step property", Document(step => step["phase"] = "setup") },
        { "missing key", Document(step => step.Remove("key")) },
        { "non-integer order", Document(step => step["order"] = "first") },
        { "missing SQL", Document(step => step.Remove("inlineSql")) },
        { "duplicate key", Document(_ => { }, duplicate: true) },
        { "missing module schema", Document(_ => { }, schemaName: null) },
        { "platform schema", Document(_ => { }, schemaName: "omp") },
        { "foreign platform module schema", Document(_ => { }, schemaName: "omp_portal") },
        { "another module's schema", Document(_ => { }, schemaName: "omp_other_key") },
        { "schema not derived from the key", Document(_ => { }, schemaName: ModuleKey) },
        { "key that derives a platform schema", Document(step => step["inlineSql"] = PortalStep, moduleKey: "portal", schemaName: "omp_portal") },
        { "step writes a platform module schema", Document(step => step["inlineSql"] = PortalStep) },
        { "step writes an unqualified (dbo) table", Document(step => step["inlineSql"] = "IF OBJECT_ID(N'Leases', N'U') IS NOT NULL DELETE FROM Leases WHERE HostId = @HostId;") },
        { "duplicate schemaName property", Document(_ => { }).Replace("\"schemaName\":", "\"schemaName\":\"omp_portal\",\"schemaName\":", StringComparison.Ordinal) },
        { "unsafe SQL", Document(step => step["inlineSql"] = $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;") },
    };

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void InvalidDocument_IsRejectedByEveryImportGate(string description, string json)
    {
        var direct = Assert.Throws<InvalidOperationException>(() => ModuleRuntimeMaintenance.ReadSteps(json));
        Assert.StartsWith(ModuleRuntimeMaintenance.RuleId, direct.Message, StringComparison.Ordinal);

        // The shared sqlScripts gate used by HostAgent, Portal and Bootstrapper import runs it too.
        var gate = Assert.Throws<InvalidOperationException>(() => ModuleDefinitionSqlOwnership.ValidateDocument(json));
        Assert.True(gate.Message.Contains(ModuleRuntimeMaintenance.RuleId, StringComparison.Ordinal), description);
    }

    [Fact]
    public void ValidDocument_PassesTheImportGate()
    {
        var json = Document(_ => { });

        ModuleDefinitionSqlOwnership.ValidateDocument(json);
        var step = Assert.Single(ModuleRuntimeMaintenance.ReadSteps(json));
        Assert.Equal(ModuleRuntimeMaintenance.HostRemoved, step.Event);
        Assert.Equal(Schema, step.SchemaName);
    }

    [Fact]
    public void AllowedSchema_IsDerivedFromTheModuleKey()
    {
        Assert.Equal("omp_example_webapp", ModuleRuntimeMaintenance.AllowedSchemaFor("example_webapp"));
    }

    [Fact]
    public void EscapedSectionName_IsParsedLikeThePlainName()
    {
        // System.Text.Json reads \u0072untimeMaintenance as runtimeMaintenance, so the executor
        // must not pre-filter stored documents on the literal property text.
        var escaped = Document(_ => { }).Replace("\"runtimeMaintenance\"", "\"\\u0072untimeMaintenance\"", StringComparison.Ordinal);

        Assert.DoesNotContain("\"runtimeMaintenance\"", escaped, StringComparison.Ordinal);
        Assert.Single(ModuleRuntimeMaintenance.ReadSteps(escaped));
    }

    private const string PortalStep = "IF OBJECT_ID(N'omp_portal.Leases', N'U') IS NOT NULL DELETE FROM omp_portal.Leases WHERE HostId = @HostId;";

    private static string Document(Action<JsonObject> editStep, bool duplicate = false, string? schemaName = Schema, string moduleKey = ModuleKey)
    {
        // The default step writes the declared schema, so a refused schema is refused by the
        // schema binding and not by the step SQL.
        var stepSchema = schemaName ?? Schema;
        JsonObject NewStep() => new()
        {
            ["key"] = "release-leases",
            ["event"] = ModuleRuntimeMaintenance.HostRemoved,
            ["order"] = 10,
            ["execution"] = "idempotent",
            ["inlineSql"] = $"IF OBJECT_ID(N'{stepSchema}.Leases', N'U') IS NOT NULL DELETE FROM {stepSchema}.Leases WHERE HostId = @HostId;",
        };

        var step = NewStep();
        editStep(step);
        var steps = new JsonArray(step);
        if (duplicate) steps.Add(NewStep());

        var module = new JsonObject { ["displayName"] = "Module" };
        if (schemaName is not null) module["schemaName"] = schemaName;

        var root = new JsonObject
        {
            ["formatVersion"] = 1,
            ["moduleKey"] = moduleKey,
            ["definitionVersion"] = "1.0.0",
            ["module"] = module,
            ["runtimeMaintenance"] = new JsonObject { ["steps"] = steps },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
