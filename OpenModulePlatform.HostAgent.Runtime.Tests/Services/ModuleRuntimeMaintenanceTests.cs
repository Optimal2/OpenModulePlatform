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
            // Nested guards, a one-level IN subquery over another module table, several SET
            // constants and extra AND filters on the same table.
            ModuleRuntimeMaintenance.HostRemoved,
            $@"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'{Schema}.Workers', N'U') IS NOT NULL
        UPDATE {Schema}.Leases
        SET WorkerId = NULL, ReleasedUtc = SYSUTCDATETIME(), State = -1
        WHERE WorkerId IN (SELECT w.WorkerId FROM {Schema}.Workers w WHERE w.HostId = @HostId AND w.IsActive = 1);
    DELETE FROM {Schema}.Leases WHERE HostId = @HostId AND ExpiresUtc < GETUTCDATE();
END;"
        },
        {
            ModuleRuntimeMaintenance.ArtifactRemoved,
            $"IF OBJECT_ID(N'[{Schema}].[Bindings]', N'U') IS NOT NULL UPDATE [{Schema}].[Bindings] SET ArtifactId = NULL WHERE @ArtifactId = ArtifactId;"
        },
        {
            ModuleRuntimeMaintenance.HostRemoved,
            $"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL DELETE {Schema}.Leases WHERE (HostId = @HostId AND State IN (1, 2)) AND ExpiresUtc IS NOT NULL;"
        },
        {
            ModuleRuntimeMaintenance.AppInstanceBlockingCount,
            $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS BlockingCount FROM {Schema}.Bindings WHERE AppInstanceId = @AppInstanceId;"
        },
        {
            ModuleRuntimeMaintenance.AppInstanceBlockingCount,
            $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS BlockingCount, N'binding(s)' AS Description FROM {Schema}.Bindings WHERE AppInstanceId = @AppInstanceId AND IsPinned = 1;"
        },
        {
            // A comment without a parameter name is fine.
            ModuleRuntimeMaintenance.HostRemoved,
            $"-- Drop leases of the removed host.\nIF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL DELETE FROM {Schema}.Leases WHERE HostId = @HostId;"
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
        const string notGuard = "The IF condition must be exactly OBJECT_ID";
        const string unbound = "must restrict the rows by the event parameter";
        return new()
        {
            { "platform table write", notGuard, ModuleRuntimeMaintenance.HostRemoved, "IF OBJECT_ID(N'omp.WorkerInstances', N'U') IS NOT NULL DELETE FROM omp.WorkerInstances WHERE HostId = @HostId;" },
            { "other module write", notGuard, ModuleRuntimeMaintenance.HostRemoved, "IF OBJECT_ID(N'other_module.Leases', N'U') IS NOT NULL DELETE FROM other_module.Leases WHERE HostId = @HostId;" },
            { "other module read", "may only reference the module schema", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId IN (SELECT h.HostId FROM other_module.Hosts h WHERE h.HostId = @HostId);" },
            { "platform read", "may only reference the module schema", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE WorkerId IN (SELECT w.WorkerInstanceId FROM omp.WorkerInstances w WHERE w.HostId = @HostId);" },
            { "no OBJECT_ID guard", "is not allowed at the top level", ModuleRuntimeMaintenance.HostRemoved, $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "IS NULL guard", notGuard, ModuleRuntimeMaintenance.HostRemoved, $"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NULL DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "OR guard", notGuard, ModuleRuntimeMaintenance.HostRemoved, $"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL OR 1 = 1 DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "guard without type", notGuard, ModuleRuntimeMaintenance.HostRemoved, $"IF OBJECT_ID(N'{Schema}.Leases') IS NOT NULL DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "guard for another table", "must be referenced inside IF OBJECT_ID", ModuleRuntimeMaintenance.HostRemoved, $"IF OBJECT_ID(N'{Schema}.Other', N'U') IS NOT NULL DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "write in ELSE", "IF ... ELSE is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId ELSE DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "dynamic SQL", "ExecuteStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"EXEC sp_executesql N'DELETE FROM {Schema}.Leases WHERE HostId = @h', N'@h uniqueidentifier', @h = @HostId;" },
            { "procedure call", "ExecuteStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"EXEC {Schema}.ReleaseLeases @HostId;" },
            { "DDL", "DropTableStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DROP TABLE {Schema}.Leases;" },
            { "DELETE without WHERE", "must have a WHERE clause", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases;" },
            { "UPDATE without WHERE", "must have a WHERE clause", ModuleRuntimeMaintenance.HostRemoved, guard + $"UPDATE {Schema}.Leases SET HostId = NULL;" },
            { "INSERT", "InsertStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"INSERT INTO {Schema}.Leases (HostId) VALUES (@HostId);" },
            { "parameter redeclared", "DeclareVariableStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, "DECLARE @HostId uniqueidentifier = NEWID(); " + guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "two batches", "exactly one batch", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;\nGO\nSELECT 1;" },
            { "alias write target", "A FROM clause or join on the write is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE l FROM {Schema}.Leases l WHERE l.HostId = @HostId;" },
            { "cross-database", "Cross-database", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM OtherDb.{Schema}.Leases WHERE HostId = @HostId;" },
            { "OUTPUT INTO", "OUTPUT is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases OUTPUT deleted.HostId INTO {Schema}.Leases(HostId) WHERE HostId = @HostId;" },
            { "TOP", "TOP is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE TOP (1) FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "table hint", "Table hints", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WITH (TABLOCKX) WHERE HostId = @HostId;" },
            { "OPTION hint", "OPTION hints are not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId OPTION (MAXDOP 1);" },
            { "SELECT INTO", "SELECT is only allowed in the read-only event", ModuleRuntimeMaintenance.HostRemoved, guard + $"SELECT * INTO {Schema}.Copy FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "transaction control", "BeginTransactionStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, "BEGIN TRANSACTION; " + guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId; COMMIT;" },
            { "unqualified table", "must be qualified with the module schema", ModuleRuntimeMaintenance.HostRemoved, guard + "DELETE FROM Leases WHERE HostId = @HostId;" },
            { "OPENROWSET", "must read exactly one table of the module schema", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId IN (SELECT r.HostId FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1') r) AND HostId = @HostId;" },
            { "SET option", "PredicateSetStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, "SET XACT_ABORT OFF; " + guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "SET NOCOUNT", "PredicateSetStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, "SET NOCOUNT ON; " + guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "write in read-only event", "is read-only", ModuleRuntimeMaintenance.AppInstanceBlockingCount, guard + $"DELETE FROM {Schema}.Leases WHERE AppInstanceId = @AppInstanceId;" },
            { "unparseable", "cannot be parsed", ModuleRuntimeMaintenance.ArtifactRemoved, "UPDATE WHERE @ArtifactId" },
            { "platform module schema write", notGuard, ModuleRuntimeMaintenance.HostRemoved, "IF OBJECT_ID(N'omp_portal.Leases', N'U') IS NOT NULL DELETE FROM omp_portal.Leases WHERE HostId = @HostId;" },
            { "platform module schema read", "may only reference the module schema", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId IN (SELECT h.HostId FROM omp_portal.Hosts h WHERE h.HostId = @HostId);" },
            { "parameter only in a separate SELECT", unbound, ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE 1 = 1; SELECT @HostId;" },
            { "parameter only in SET", "A SET value must be a constant", ModuleRuntimeMaintenance.HostRemoved, guard + $"UPDATE {Schema}.Leases SET HostId = @HostId WHERE HostId IS NOT NULL;" },
            { "SET without parameter binding", unbound, ModuleRuntimeMaintenance.HostRemoved, guard + $"UPDATE {Schema}.Leases SET HostId = NULL WHERE HostId IS NOT NULL;" },
            { "parameter diluted by OR", "is not allowed in a runtime maintenance WHERE clause", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId OR 1 = 1;" },
            { "NOT", "BooleanNotExpression is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId AND NOT (State = 1);" },
            { "parameter under NOT IN", "InPredicate is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId NOT IN (SELECT l.HostId FROM {Schema}.Leases l WHERE l.HostId = @HostId);" },
            { "parameter under inequality", "BooleanComparisonExpression is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId <> @HostId;" },
            { "join that excludes the target", "A FROM clause or join on the write is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases FROM omp.WorkerInstances w INNER JOIN omp.Hosts h ON h.HostId = w.HostId AND h.HostId = @HostId WHERE 1 = 1;" },
            { "second DELETE unbound", unbound, ModuleRuntimeMaintenance.HostRemoved, guard + $"BEGIN DELETE FROM {Schema}.Leases WHERE HostId = @HostId; DELETE FROM {Schema}.Leases WHERE 1 = 1; END" },
            { "MERGE deleting unmatched rows", "MergeStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"MERGE {Schema}.Leases AS t USING (SELECT @HostId AS HostId) AS s ON t.HostId = s.HostId AND t.HostId = @HostId WHEN NOT MATCHED BY SOURCE THEN DELETE;" },
            { "two-level subquery", "Only one level of IN subquery", ModuleRuntimeMaintenance.HostRemoved, $"IF OBJECT_ID(N'{Schema}.Owners', N'U') IS NOT NULL " + guard + $"DELETE FROM {Schema}.Leases WHERE LeaseId IN (SELECT o.LeaseId FROM {Schema}.Owners o WHERE o.HostId = @HostId AND o.OwnerId IN (SELECT p.OwnerId FROM {Schema}.Owners p WHERE p.HostId = @HostId));" },
            { "subquery alias equal to the target name", "alias that differs", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE LeaseId IN (SELECT Leases.LeaseId FROM {Schema}.Leases WHERE Leases.HostId = @HostId);" },
            { "subquery without parameter", "The subquery WHERE clause must contain", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId AND LeaseId IN (SELECT l.LeaseId FROM {Schema}.Leases l WHERE l.State = 1);" },
            { "read-only with two SELECTs", "exactly one SELECT", ModuleRuntimeMaintenance.AppInstanceBlockingCount, $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL BEGIN SELECT COUNT(*) AS BlockingCount FROM {Schema}.Bindings WHERE AppInstanceId = @AppInstanceId; SELECT COUNT(*) AS BlockingCount FROM {Schema}.Bindings WHERE AppInstanceId = @AppInstanceId; END" },
            { "read-only SELECT unbound", unbound, ModuleRuntimeMaintenance.AppInstanceBlockingCount, $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS BlockingCount FROM {Schema}.Bindings WHERE AppInstanceId IS NOT NULL;" },
            { "read-only SELECT exposes the parameter", "must select exactly COUNT(*) AS BlockingCount", ModuleRuntimeMaintenance.AppInstanceBlockingCount, $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS BlockingCount, @AppInstanceId AS Id FROM {Schema}.Bindings WHERE AppInstanceId = @AppInstanceId;" },
            { "read-only SELECT with other name", "must select exactly COUNT(*) AS BlockingCount", ModuleRuntimeMaintenance.AppInstanceBlockingCount, $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS Total FROM {Schema}.Bindings WHERE AppInstanceId = @AppInstanceId;" },
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

    // Round 3: each of these got past the round-2 rule "every write references the parameter in its
    // WHERE clause". The allow-list refuses them because they are not one of the allowed forms.
    public static TheoryData<string, string, string, string> Bypasses()
    {
        const string guard = $"IF OBJECT_ID(N'{Schema}.Leases', N'U') IS NOT NULL ";
        return new()
        {
            { "F-A uncorrelated inner join writes every row", "A FROM clause or join on the write is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases FROM {Schema}.Leases INNER JOIN omp.Hosts h ON h.HostId = @HostId WHERE 1 = 1;" },
            { "F-A uncorrelated inner join in UPDATE", "A FROM clause or join on the write is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"UPDATE {Schema}.Leases SET ExpiresUtc = NULL FROM {Schema}.Leases INNER JOIN omp.Hosts h ON h.HostId = @HostId WHERE 1 = 1;" },
            { "F-B parameter rebound with SET", "SetVariableStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"BEGIN SET @HostId = (SELECT TOP (1) HostId FROM {Schema}.Leases); DELETE FROM {Schema}.Leases WHERE HostId = @HostId; END" },
            { "F-B parameter rebound with SELECT", "SELECT is only allowed in the read-only event", ModuleRuntimeMaintenance.HostRemoved, guard + $"BEGIN SELECT @HostId = HostId FROM {Schema}.Leases; DELETE FROM {Schema}.Leases WHERE HostId = @HostId; END" },
            { "F-C INSERT", "InsertStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"BEGIN INSERT INTO {Schema}.Leases (HostId) VALUES (NEWID()); DELETE FROM {Schema}.Leases WHERE HostId = @HostId; END" },
            { "F-D MERGE WHEN NOT MATCHED THEN INSERT", "MergeStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"MERGE {Schema}.Leases AS t USING (SELECT @HostId AS HostId) AS s ON t.HostId = @HostId WHEN MATCHED THEN DELETE WHEN NOT MATCHED THEN INSERT (HostId) VALUES (NEWID());" },
            { "round 2: OR dilutes the parameter", "is not allowed in a runtime maintenance WHERE clause", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE 1 = 1 OR @HostId IS NULL;" },
            { "round 2: parameter only in a comment", "Comments must not contain '@'", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE 1 = 1; -- HostId = @HostId" },
            { "round 2: one of several writes unbound", "must restrict the rows by the event parameter", ModuleRuntimeMaintenance.HostRemoved, guard + $"BEGIN DELETE FROM {Schema}.Leases WHERE HostId = @HostId; DELETE FROM {Schema}.Leases WHERE ExpiresUtc IS NULL; END" },
            { "subquery column resolving to the outer table", "must select exactly one column qualified by", ModuleRuntimeMaintenance.HostRemoved, guard + $"IF OBJECT_ID(N'{Schema}.Owners', N'U') IS NOT NULL DELETE FROM {Schema}.Leases WHERE LeaseId IN (SELECT LeaseId FROM {Schema}.Owners WHERE HostId = @HostId);" },
            { "uncorrelated EXISTS", "ExistsPredicate is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"DELETE FROM {Schema}.Leases WHERE HostId = HostId AND EXISTS (SELECT 1 FROM omp.Hosts WHERE HostId = @HostId);" },
            { "parameter declared", "DeclareVariableStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, $"DECLARE @HostId2 uniqueidentifier; " + guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "CTE", "Common table expressions (WITH) are not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"WITH x AS (SELECT HostId FROM {Schema}.Leases) DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
            { "TRUNCATE", "TruncateTableStatement is not allowed", ModuleRuntimeMaintenance.HostRemoved, guard + $"BEGIN DELETE FROM {Schema}.Leases WHERE HostId = @HostId; TRUNCATE TABLE {Schema}.Leases; END" },
            { "SET value from a subquery", "A SET value must be a constant", ModuleRuntimeMaintenance.HostRemoved, guard + $"UPDATE {Schema}.Leases SET HostId = (SELECT TOP (1) HostId FROM {Schema}.Leases) WHERE HostId = @HostId;" },
            { "SET value from another function", "A SET value must be a constant", ModuleRuntimeMaintenance.HostRemoved, guard + $"UPDATE {Schema}.Leases SET LeaseId = NEWID() WHERE HostId = @HostId;" },
            { "read-only SELECT with extra column", "must select exactly COUNT(*) AS BlockingCount", ModuleRuntimeMaintenance.AppInstanceBlockingCount, $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS BlockingCount, MAX(BindingKey) AS Description FROM {Schema}.Bindings WHERE AppInstanceId = @AppInstanceId;" },
            { "read-only SELECT with join", "must read exactly one table of the module schema", ModuleRuntimeMaintenance.AppInstanceBlockingCount, $"IF OBJECT_ID(N'{Schema}.Bindings', N'U') IS NOT NULL SELECT COUNT(*) AS BlockingCount FROM {Schema}.Bindings b INNER JOIN omp.Hosts h ON h.HostId = b.HostId WHERE b.AppInstanceId = @AppInstanceId;" },
            { "comment containing @", "Comments must not contain '@'", ModuleRuntimeMaintenance.HostRemoved, "/* @HostId */ " + guard + $"DELETE FROM {Schema}.Leases WHERE HostId = @HostId;" },
        };
    }

    [Theory]
    [MemberData(nameof(Bypasses))]
    public void RoundThreeBypass_IsRejected(string description, string expectedReason, string eventName, string sql)
    {
        var error = ModuleRuntimeMaintenance.ValidateStepSql(sql, Schema, ModuleRuntimeMaintenance.Events[eventName]);

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
