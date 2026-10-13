using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ModuleDefinitionScriptCompletionTests : IAsyncLifetime
{
    private readonly string _databaseName = "OmpScriptCompletion_" + Guid.NewGuid().ToString("N");
    private string _connectionString = null!;
    private OmpHostArtifactRepository _repository = null!;
    private readonly TestLogger _logger = new();

    public async Task InitializeAsync()
    {
        var builder = new SqlConnectionStringBuilder(OmpHostArtifactRepositoryTestDatabase.GetBaseConnectionString());
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(builder.ConnectionString, $"CREATE DATABASE [{_databaseName}];");
        builder.InitialCatalog = _databaseName;
        _connectionString = builder.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OmpDb"] = _connectionString
        }).Build();
        _repository = new OmpHostArtifactRepository(new SqlConnectionFactory(configuration));
        var sql = OmpRepositoryFiles.ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql");
        sql = Regex.Replace(sql, @"^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline).Where(static text => !string.IsNullOrWhiteSpace(text)))
            await ExecuteAsync(batch);
        await ExecuteAsync("CREATE SCHEMA completion;");
        await ExecuteAsync("CREATE TABLE completion.Items (Id int NOT NULL PRIMARY KEY);");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(OmpHostArtifactRepositoryTestDatabase.GetBaseConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand($"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Import_MissingColumnAndChangedInitialize_ExecutesBothCurrentHashesOnce()
    {
        var old = Definition("1.0.0", "SELECT 1;", "SELECT 1;", declareColumn: false);
        await Service().ImportModuleDefinitionAsync(old, CancellationToken.None);
        var current = Definition();

        var result = await Service().ImportModuleDefinitionAsync(current, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(["setup"], result.HealedScripts);
        Assert.Equal(2, result.SqlRepairCount);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM completion.Items WHERE Id = 1 AND Note = N'initialized';"));
        Assert.Equal(2, await CurrentExecutionCountAsync());
        var reimport = await Service().ImportModuleDefinitionAsync(current, CancellationToken.None);
        Assert.Equal(0, reimport.SqlRepairCount);
        Assert.Equal(2, await CurrentExecutionCountAsync());
    }

    [Fact]
    public async Task Import_EqualAppliedVersionWithOnlySetupSucceeded_HealsInitialize()
    {
        var definition = Definition();
        var saved = await _repository.SaveImportedModuleDefinitionAsync(definition, false, CancellationToken.None);
        await _repository.ApplyImportedModuleDefinitionAsync(saved.ModuleDefinitionDocumentId, CancellationToken.None);
        await _repository.ExecuteImportedModuleDefinitionSqlRepairsAsync(saved.ModuleDefinitionDocumentId, new HashSet<string> { "setup" }, CancellationToken.None);
        Assert.Equal(1, await CurrentExecutionCountAsync());

        var result = await Service().ImportModuleDefinitionAsync(definition, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Empty(result.HealedScripts);
        Assert.Equal(1, result.SqlRepairCount);
        Assert.Equal(2, await CurrentExecutionCountAsync());
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM completion.Items WHERE Id = 1;"));
    }

    [Fact]
    public async Task Import_StrictlyNewerInstalled_DoesNotRunPendingInitialize()
    {
        var newer = Definition("2.0.0");
        await Service().ImportModuleDefinitionAsync(newer, CancellationToken.None);
        await ExecuteAsync("DELETE FROM completion.Items;");

        var result = await Service().ImportModuleDefinitionAsync(Definition(), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(0, result.SqlRepairCount);
        Assert.Equal(0, await CurrentExecutionCountAsync());
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM completion.Items;"));
    }

    [Fact]
    public async Task Import_AppliedDocumentWithPendingScriptAndNewerInstalled_FailsAndLogsError()
    {
        var definition = Definition();
        var saved = await _repository.SaveImportedModuleDefinitionAsync(definition, false, CancellationToken.None);
        await _repository.ApplyImportedModuleDefinitionAsync(saved.ModuleDefinitionDocumentId, CancellationToken.None);
        await _repository.ExecuteImportedModuleDefinitionSqlRepairsAsync(saved.ModuleDefinitionDocumentId, new HashSet<string> { "setup" }, CancellationToken.None);
        await Service().ImportModuleDefinitionAsync(Definition("2.0.0"), CancellationToken.None);
        // Reconstruct an inconsistent applied flag. The newer-installed gate
        // prevents running old SQL, but cannot make the incomplete applied
        // document a successful import. This drives the actual import outcome.
        await ExecuteAsync("UPDATE omp.ModuleDefinitionDocuments SET IsApplied = 1, AppliedUtc = '20000101', UpdatedUtc = '20000101' WHERE DefinitionVersion = N'1.0.1';");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().ImportModuleDefinitionAsync(definition, CancellationToken.None));

        Assert.Contains("initialize", error.Message);
        Assert.Contains("is marked applied", error.Message);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("initialize", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Witness_OldHashAndFailedCurrentHash_DoNotCountAsSucceeded()
    {
        var definition = Definition();
        var saved = await _repository.SaveImportedModuleDefinitionAsync(definition, false, CancellationToken.None);
        await _repository.ApplyImportedModuleDefinitionAsync(saved.ModuleDefinitionDocumentId, CancellationToken.None);
        await _repository.ExecuteImportedModuleDefinitionSqlRepairsAsync(saved.ModuleDefinitionDocumentId, CancellationToken.None);
        await ExecuteAsync("UPDATE omp.ModuleDefinitionSqlExecutions SET ScriptSha256 = REPLICATE('0', 64) WHERE ScriptKey = N'initialize';");
        Assert.Equal(["initialize"], await _repository.GetModuleDefinitionScriptsWithoutSucceededExecutionAsync(saved.ModuleDefinitionDocumentId, CancellationToken.None));
        await _repository.ExecuteImportedModuleDefinitionSqlRepairsAsync(saved.ModuleDefinitionDocumentId, new HashSet<string> { "initialize" }, CancellationToken.None);
        await ExecuteAsync("UPDATE omp.ModuleDefinitionSqlExecutions SET ExecutionStatus = N'Failed' WHERE ScriptKey = N'initialize' AND ScriptSha256 <> REPLICATE('0', 64);");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().VerifyAppliedDefinitionScriptsExecutedAsync(saved.ModuleDefinitionDocumentId, definition, CancellationToken.None));
    }

    private ArtifactZipImportService Service() => new(new SettingsMonitor(), _repository, _logger);

    private static ModuleDefinitionImportDocument Definition(string version = "1.0.1", string? setup = null, string? initialize = null, bool declareColumn = true)
    {
        var root = new JsonObject
        {
            ["moduleKey"] = "script_completion",
            ["definitionVersion"] = version,
            ["sqlScripts"] = new JsonArray(
                Script("setup", "setup", 10, setup ?? "IF COL_LENGTH(N'completion.Items', N'Note') IS NULL ALTER TABLE completion.Items ADD Note nvarchar(50) NULL;"),
                Script("initialize", "initialize", 20, initialize ?? "IF NOT EXISTS (SELECT 1 FROM completion.Items WHERE Id = 1) INSERT completion.Items (Id, Note) VALUES (1, N'initialized');"),
                Script("validate", "validate", 0, "SELECT N'OK' AS Status;")),
            ["integrity"] = new JsonObject
            {
                ["requiredColumns"] = declareColumn ? new JsonArray(new JsonObject
                {
                    ["schema"] = "completion", ["table"] = "Items", ["name"] = "Note", ["source"] = "setup"
                }) : new JsonArray()
            }
        };
        var json = root.ToJsonString();
        return new("script_completion", version, 1, json, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), "test", [], []);
    }

    private static JsonObject Script(string key, string phase, int order, string sql) => new()
    {
        ["key"] = key, ["phase"] = phase, ["order"] = order, ["execution"] = "idempotent", ["inlineSql"] = sql
    };

    private Task<int> CurrentExecutionCountAsync() => ScalarAsync("SELECT COUNT(*) FROM omp.ModuleDefinitionSqlExecutions e JOIN omp.ModuleDefinitionDocuments d ON d.ModuleDefinitionDocumentId = e.ModuleDefinitionDocumentId WHERE d.DefinitionVersion = N'1.0.1' AND e.ExecutionStatus = N'Succeeded';");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class SettingsMonitor : IOptionsMonitor<HostAgentSettings>
    {
        public HostAgentSettings CurrentValue { get; } = new();
        public HostAgentSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<HostAgentSettings, string?> listener) => null;
    }

    private sealed class TestLogger : ILogger<ArtifactZipImportService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
