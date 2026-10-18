using System.Text.Json.Nodes;
using OpenModulePlatform.TestSupport;
using static OpenModulePlatform.Installation.InstallationEngine;

namespace OpenModulePlatform.Bootstrapper.Tests;

public sealed class PlatformCoreMaintenanceTests : IDisposable
{
    private readonly string root;
    private readonly string configPath;
    private readonly string definitionPath;

    public PlatformCoreMaintenanceTests()
    {
        root = Path.Combine(Environment.GetEnvironmentVariable("AO_JOB_SCRATCH") ?? Path.GetTempPath(), "core-maintenance-tests", Guid.NewGuid().ToString("N"));
        var definitions = Path.Combine(root, "data", "global", "module-definitions");
        Directory.CreateDirectory(definitions);
        configPath = Path.Combine(root, "bootstrap.json");
        File.WriteAllText(configPath, """
            {"sql":{"enabled":true,"server":"offline-contract.invalid","database":"ExistingCoreContract",
                "bootstrapPortalAdminPrincipal":"TEST\\Operator",
                "createDatabase":true,"grantRuntimeDatabaseAccess":true,"scripts":[{"path":"must-not-run.sql"}]},
             "artifactStoreRoot":"must-not-prepare", "artifacts":[{"source":"must-not-register"}],
             "hostAgent":{"enabled":true,"installPath":"must-not-install","serviceName":"must-not-touch"}}
            """);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "OpenModulePlatform.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        definitionPath = Path.Combine(definitions, "omp_core.module-definition.json");
        File.Copy(Path.Combine(repository.FullName, "omp_core.module-definition.json"), definitionPath);
    }

    private CliOptions Options(params string[] extra)
        => CliOptions.Parse(["--maintain-platform-core", "--config", configPath, "--payload-root", root,
            "--expected-core-version", "0.3.270", .. extra]);

    [Fact]
    public async Task OfflinePlanAndRefusedConfirmation_DoNotInvokeAnyDatabaseOperationOrWriteFiles()
    {
        var before = Snapshot();
        Task Forbidden(SqlBootstrapOptions sql, string payload, IReadOnlyList<(string DefinitionPath, ModuleDefinitionDocument Definition)> selected)
            => throw new Exception("No SQL call is allowed during offline plan or cancellation.");
        Assert.Equal(0, await Program.RunPlatformCoreMaintenanceAsync(Options("--plan-only"), _ => throw new Exception("No confirmation in plan"), Forbidden));
        Assert.Equal(2, await Program.RunPlatformCoreMaintenanceAsync(Options(), _ => false, Forbidden));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task ConfirmedAction_ImportsOnlyCoreSnapshot_WithoutRuntimeServiceConfigOrCredentialWrites()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(definitionPath)!, "other.json"), """
            {"moduleKey":"other_module","definitionVersion":"1.0.0"}
            """);
        var before = Snapshot();
        var calls = 0;
        Assert.Equal(0, await Program.RunPlatformCoreMaintenanceAsync(Options(), _ => true, (sql, payload, selected) =>
        {
            calls++;
            Assert.Equal("ExistingCoreContract", sql.Database);
            Assert.Equal(root, payload);
            var core = Assert.Single(selected);
            Assert.Equal(definitionPath, core.DefinitionPath);
            Assert.Equal("omp_core", core.Definition.ModuleKey);
            Assert.Equal("0.3.270", core.Definition.DefinitionVersion);
            return Task.CompletedTask;
        }));
        Assert.Equal(1, calls);
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("renamed")]
    [InlineData("duplicate")]
    [InlineData("malformed-other")]
    [InlineData("wrong-key")]
    [InlineData("case-key")]
    [InlineData("padded-key")]
    [InlineData("padded-version")]
    [InlineData("wrong-type")]
    [InlineData("wrong-format")]
    [InlineData("missing-format")]
    [InlineData("string-format")]
    [InlineData("wrong-version")]
    [InlineData("duplicate-json")]
    [InlineData("hash")]
    [InlineData("encoding")]
    [InlineData("ownership")]
    [InlineData("unsafe")]
    [InlineData("no-setup")]
    [InlineData("no-validation")]
    [InlineData("duplicate-script")]
    [InlineData("non-idempotent")]
    public async Task InvalidPayload_FailsBeforeConfirmationOrDatabase(string problem)
    {
        var json = JsonNode.Parse(await File.ReadAllTextAsync(definitionPath))!;
        var scripts = json["sqlScripts"]!.AsArray();
        var setup = scripts.Single(s => s!["key"]!.GetValue<string>() == "setup-core-schema")!;
        switch (problem)
        {
            case "missing": File.Delete(definitionPath); break;
            case "renamed": File.Move(definitionPath, definitionPath + ".renamed.json"); break;
            case "duplicate": File.Copy(definitionPath, Path.Combine(Path.GetDirectoryName(definitionPath)!, "duplicate.json")); break;
            case "malformed-other": File.WriteAllText(Path.Combine(Path.GetDirectoryName(definitionPath)!, "broken.json"), "{"); break;
            case "wrong-key": json["moduleKey"] = "other_module"; break;
            case "case-key": json["moduleKey"] = "OMP_CORE"; break;
            case "padded-key": json["moduleKey"] = "omp_core "; break;
            case "padded-version": json["definitionVersion"] = "0.3.270 "; break;
            case "wrong-type": json["definitionType"] = "module"; break;
            case "wrong-format": json["formatVersion"] = 2; break;
            case "missing-format": json.AsObject().Remove("formatVersion"); break;
            case "string-format": json["formatVersion"] = "1"; break;
            case "wrong-version": json["definitionVersion"] = "0.3.265"; break;
            case "duplicate-json": File.WriteAllText(definitionPath, "{\"moduleKey\":\"omp_core\"," + json.ToJsonString()[1..]); break;
            case "hash": setup["sha256"] = new string('0', 64); break;
            case "encoding": setup["contentEncoding"] = "unknown"; break;
            case "ownership": ReplaceSql(setup, "UPDATE omp.ConfigOverlayDocuments SET HostScope = N'AllHosts';"); break;
            case "unsafe": ReplaceSql(setup, "DROP TABLE omp.Probe;"); break;
            case "no-setup": scripts.Remove(setup); break;
            case "no-validation": scripts.Remove(scripts.Single(s => s!["key"]!.GetValue<string>() == "validate-core-schema")); break;
            case "duplicate-script": scripts.Add(setup.DeepClone()); break;
            case "non-idempotent": setup["execution"] = "once"; break;
        }
        if (problem is not ("missing" or "renamed" or "duplicate" or "malformed-other" or "duplicate-json"))
            File.WriteAllText(definitionPath, json.ToJsonString());
        var confirmations = 0;
        var imports = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => Program.RunPlatformCoreMaintenanceAsync(Options(), _ => { confirmations++; return true; },
            (_, _, _) => { imports++; return Task.CompletedTask; }));
        Assert.Equal(0, confirmations);
        Assert.Equal(0, imports);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sql\":{\"server\":\"offline.invalid\"}}")]
    [InlineData("{\"sql\":{\"server\":\"\",\"database\":\"db\"}}")]
    [InlineData("{\"sql\":{\"server\":\"offline.invalid\",\"database\":\"db\",\"enabled\":false}}")]
    [InlineData("{\"sql\":{\"server\":\"offline.invalid\",\"database\":\"db\",\"database\":\"other\"}}")]
    public async Task MissingOrAmbiguousSqlIdentity_RefusedOffline(string text)
    {
        File.WriteAllText(configPath, text);
        await Assert.ThrowsAnyAsync<Exception>(() => Program.RunPlatformCoreMaintenanceAsync(Options("--plan-only")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("newer")]
    [InlineData("hash-drift")]
    public async Task ExistingInstallationGuard_RefusesWithoutStoreOrRepair(string problem)
    {
        var core = await ReadModuleDefinitionAsync(definitionPath);
        AppliedModuleDefinition? current = problem switch
        {
            "missing" => null,
            "newer" => new(1, "0.3.271", core.DefinitionSha256),
            _ => new(1, core.DefinitionVersion, "wrong-hash")
        };
        Assert.Throws<InvalidOperationException>(() => ValidateExistingCoreTransition(core, current));
    }

    [Fact]
    public async Task OldExecutorFails_NewExecutorMigratesBeforePortableSetup_ThenRepeatedInvocationDoesNoSetup()
    {
        PlatformConfigurationImportContract.CompiledMigrationOwnsHostScope(typeof(InstallationEngine));
        var core = await ReadModuleDefinitionAsync(definitionPath);
        var database = new OfflineCoreDatabase { Current = new(1, "0.3.265", "old-hash") };
        async Task Import(SqlBootstrapOptions options, string payload, IReadOnlyList<(string DefinitionPath, ModuleDefinitionDocument Definition)> selected)
        {
            var definition = Assert.Single(selected).Definition;
            ValidateExistingCoreTransition(definition, database.Current);
            await ImportDefinitionWithOperationsAsync(definition, database.Current, true, false,
                () => { database.Stores++; database.Current = new(2, core.DefinitionVersion, core.DefinitionSha256); return Task.FromResult(2); },
                _ => Task.CompletedTask,
                _ => ExecuteModuleDefinitionSqlRepairsAsync(options, payload, definition, database.Operations, ensureCoreMigration: true));
            ValidateCoreMaintenanceSchema(database.HasHostScope);
        }

        // Existing-table CREATE guards let an old executor record portable SQL success
        // without adding HostScope. Applied version and successful history are insufficient.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Program.RunPlatformCoreMaintenanceAsync(Options("--yes"), import: Import));
        Assert.Contains("HostScope", failure.ToString());
        Assert.False(database.HasHostScope);
        Assert.All(database.Statuses, status => Assert.Equal("Succeeded", status));
        Assert.True(database.Executions > 0);

        database.UpdatedExecutor = true;
        var oldExecutions = database.Executions;
        Assert.Equal(0, await Program.RunPlatformCoreMaintenanceAsync(Options("--yes"), import: Import));
        Assert.True(database.HasHostScope);
        Assert.Equal(oldExecutions, database.Executions); // Compiled migration still runs when ordinary repair is a no-op.
        Assert.Equal(1, database.Stores);
        var executed = database.Executions;
        var starts = database.Starts;
        Assert.Equal(0, await Program.RunPlatformCoreMaintenanceAsync(Options("--yes"), import: Import));
        Assert.Equal(executed, database.Executions);
        Assert.Equal(starts, database.Starts);
        Assert.Equal(1, database.Stores);

        // Starting directly from old core with the updated executor migrates BEFORE portable SQL.
        database = new OfflineCoreDatabase { Current = new(1, "0.3.265", "old-hash"), UpdatedExecutor = true };
        Assert.Equal(0, await Program.RunPlatformCoreMaintenanceAsync(Options("--yes"), import: Import));
        Assert.True(database.Events.IndexOf("migration:setup-core-schema") < database.Events.IndexOf("execute:setup-core-schema"));
    }

    [Fact]
    public async Task CompiledMigrationFailure_StopsBeforePortableSqlAndNeverReportsSuccess()
    {
        var core = await ReadModuleDefinitionAsync(definitionPath);
        var database = new OfflineCoreDatabase { FailMigration = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteModuleDefinitionSqlRepairsAsync(
            new() { BootstrapPortalAdminPrincipal = @"TEST\Operator" }, root, core, database.Operations, ensureCoreMigration: true));
        Assert.Equal(0, database.Starts);
        Assert.Equal(0, database.Executions);
        Assert.Empty(database.Statuses);
    }

    [Fact]
    public async Task FailedPortableSql_IsRecordedAndRetriedByTheSameGuardedExecutor()
    {
        var core = await ReadModuleDefinitionAsync(definitionPath);
        var database = new OfflineCoreDatabase { UpdatedExecutor = true, FailSetup = true };
        var sql = new SqlBootstrapOptions { BootstrapPortalAdminPrincipal = @"TEST\Operator" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteModuleDefinitionSqlRepairsAsync(sql, root, core, database.Operations, true));
        Assert.Contains("Failed", database.Statuses);
        database.FailSetup = false;
        Assert.Equal(2, await ExecuteModuleDefinitionSqlRepairsAsync(sql, root, core, database.Operations, true));
        Assert.Equal("Succeeded", database.Statuses.Last());
    }

    [Fact]
    public async Task GuardedRepair_RejectsSqlOwnershipBeforeAnyDatabaseBoundaryCall()
    {
        var json = JsonNode.Parse(File.ReadAllText(definitionPath))!;
        ReplaceSql(json["sqlScripts"]!.AsArray()[1]!, "ALTER TABLE omp.ConfigOverlayDocuments ADD Probe int;");
        var core = (await ReadModuleDefinitionAsync(definitionPath)) with { DefinitionJson = json.ToJsonString() };
        var database = new OfflineCoreDatabase();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteModuleDefinitionSqlRepairsAsync(new(), root, core, database.Operations));
        Assert.Empty(database.Events);
    }

    [Theory]
    [InlineData("--gui")]
    [InlineData("--upgrade-or-complete")]
    [InlineData("--uninstall")]
    [InlineData("--refresh-installer-package")]
    [InlineData("--sync-package-objects")]
    [InlineData("--refresh-and-stage-package")]
    [InlineData("--check-developer-source-status")]
    [InlineData("--sync-package-objects-before-action")]
    [InlineData("--full-content-check")]
    [InlineData("--restart-gui")]
    public void MaintenanceRejectsOtherModesAndBootstrapModifiers(string flag)
        => Assert.Throws<InvalidOperationException>(() => Options(flag));

    [Fact]
    public void MaintenanceRequiresExplicitInputsAndScopedPlanFlag()
    {
        Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(["--maintain-platform-core"]));
        Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(["--plan-only"]));
        Assert.Throws<InvalidOperationException>(() => Options("--payload-zip", "payload.zip"));
        Assert.Throws<InvalidOperationException>(() => Options("--plan-only", "--yes"));
    }

    [Theory]
    [InlineData("0.3.269", "different", false, "")]
    [InlineData("0.3.270", "different", true, "")]
    [InlineData("0.3.270", "same", false, "metadata,repair")]
    [InlineData("0.3.270", "different", false, "store,repair")]
    [InlineData("0.3.271", "different", false, "store,repair")]
    public async Task SharedImporter_PreservesOrdinaryImportDecisions(string targetVersion, string hash, bool trustSameVersion, string expected)
    {
        var definition = new ModuleDefinitionDocument("module_probe", targetVersion, 1, "{}", hash, []);
        var current = new AppliedModuleDefinition(1, "0.3.270", "same");
        var calls = new List<string>();
        await ImportDefinitionWithOperationsAsync(definition, current, true, trustSameVersion,
            () => { calls.Add("store"); return Task.FromResult(2); },
            id => { Assert.Equal(1, id); calls.Add("metadata"); return Task.CompletedTask; },
            _ => { calls.Add("repair"); return Task.FromResult(0); });
        Assert.Equal(expected, string.Join(",", calls));
    }

    private static void ReplaceSql(JsonNode script, string text)
    {
        script["inlineSql"] = text;
        script.AsObject().Remove("content");
        script.AsObject().Remove("contentEncoding");
        script["sha256"] = ComputeTextSha256(text);
    }

    private string[] Snapshot() => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(p => p).Select(p => p + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray();

    public void Dispose() => Directory.Delete(root, recursive: true);

    // Only SQL I/O is modeled. Document selection, ownership/hash gates, version decisions,
    // validation/repair decisions, execution ordering and failure propagation are production code.
    private sealed class OfflineCoreDatabase
    {
        internal AppliedModuleDefinition? Current;
        internal bool UpdatedExecutor;
        internal bool FailMigration;
        internal bool FailSetup;
        internal bool HasHostScope;
        internal int Stores, Starts, Executions;
        internal List<string> Events = [];
        internal List<string> Statuses = [];
        private readonly HashSet<string> succeeded = [];
        private PortableModuleDefinitionSqlScript? active;

        internal ModuleDefinitionSqlRepairOperations Operations => new(
            () => { Events.Add("lock"); return Task.CompletedTask; },
            scripts => Task.FromResult(scripts.Any(s => !succeeded.Contains(s.Key))),
            _ => Task.FromResult(new ModuleDefinitionValidationResult(true, null)),
            (script, _) => { Starts++; active = script; return Task.FromResult((long)Starts); },
            key =>
            {
                if (FailMigration) throw new InvalidOperationException("Compiled migration failed.");
                if (UpdatedExecutor && key == "setup-core-schema") { Events.Add("migration:" + key); HasHostScope = true; }
                return Task.CompletedTask;
            },
            text =>
            {
                Events.Add("execute:" + active!.Key);
                if (active.Key == "setup-core-schema")
                {
                    Assert.Contains("HostScope", text); // Fresh-table DDL does not upgrade an existing table.
                    if (FailSetup) throw new InvalidOperationException("Injected portable setup failure.");
                }
                Executions++;
                return Task.CompletedTask;
            },
            (_, status, _) => { Statuses.Add(status); if (status == "Succeeded") succeeded.Add(active!.Key); return Task.CompletedTask; });
    }
}
