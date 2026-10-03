using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class HostAgentPortalEntrySyncTests : IAsyncLifetime
{
    private readonly StaleSchemaTestFixture _database = new(
        OmpTestDatabaseNames.ForPortalTests("PortalEntrySync"));

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        await ExecuteAsync("""
            CREATE TABLE omp.Modules (
                ModuleId int IDENTITY PRIMARY KEY, ModuleKey nvarchar(100) UNIQUE,
                DisplayName nvarchar(200), ModuleType nvarchar(50), SchemaName nvarchar(128),
                Description nvarchar(500), SortOrder int, IsEnabled bit, UpdatedUtc datetime2);
            CREATE TABLE omp.Apps (
                AppId int IDENTITY PRIMARY KEY, ModuleId int, AppKey nvarchar(100),
                DisplayName nvarchar(200), AppType nvarchar(50), AllowMultipleActiveInstances bit,
                Description nvarchar(500), SortOrder int, IsEnabled bit, UpdatedUtc datetime2);
            CREATE TABLE omp.AppInstances (
                AppInstanceId uniqueidentifier PRIMARY KEY, AppId int, DisplayName nvarchar(200),
                Description nvarchar(500), SortOrder int, IsEnabled bit, IsAllowed bit);
            """);
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task ModuleImportedAfterPortalSync_GetsHomeEntry_AndRepeatedImportIsIdempotent()
    {
        await CreatePortalTableAsync();
        // The previous Portal sync completed before this module's initialization SQL.
        Assert.Equal("[]", await EntriesAsync());
        var appId = Guid.NewGuid();
        await ImportAsync(appId);
        using var entries = JsonDocument.Parse(await EntriesAsync());
        var entry = Assert.Single(entries.RootElement.EnumerateArray());
        Assert.Equal($"app:{appId:N}:home", entry.GetProperty("entry_key").GetString());
        Assert.Equal($"app:{appId:N}:home", entry.GetProperty("target_entry_key").GetString());
        Assert.Equal(appId, entry.GetProperty("source_app_instance_id").GetGuid());
        Assert.Equal("New app", entry.GetProperty("display_name").GetString());
        Assert.True(entry.GetProperty("is_enabled").GetBoolean());
        Assert.Equal(25, entry.GetProperty("default_sort_order").GetInt32());
        var before = await EntriesAsync();
        await ImportAsync(appId);
        Assert.Equal(before, await EntriesAsync());
    }

    [Fact]
    public async Task Import_PreservesEveryFieldOfCustomisedEntry()
    {
        await CreatePortalTableAsync();
        var appId = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT omp_portal.portal_entries
                (entry_key, display_name, description, target_entry_key, source_app_instance_id,
                 is_enabled, default_sort_order, updated_at)
            VALUES (N'app:{appId:N}:home', N'Administrator name', N'Custom description',
                    N'custom:target', '{appId}', 0, 987, '2001-02-03');
            """);
        var before = await EntriesAsync();
        await ImportAsync(appId);
        Assert.Equal(before, await EntriesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_WithoutPortalTable_IsNoOp(bool schemaExists)
    {
        if (schemaExists)
            await ExecuteAsync("CREATE SCHEMA omp_portal;");
        await ImportAsync(Guid.NewGuid());
        Assert.False(await _database.TableExistsAsync("omp_portal", "portal_entries"));
    }

    [Fact]
    public async Task PortalArrivesLater_SkippedOlderModuleImportStillAddsMissingEntry()
    {
        var appId = Guid.NewGuid();
        await ImportAsync(appId);
        await CreatePortalTableAsync();
        // A newer installed definition skips application and SQL repairs altogether.
        await ExecuteAsync("UPDATE omp.ModuleDefinitionDocuments SET IsApplied = 0;");
        await _database.InsertModuleDefinitionDocumentAsync("entry_sync_test", "2.0.0", "{}", true);
        var result = await ImportAsync(appId);
        Assert.False(result.Applied);
        Assert.Equal(0, result.SqlRepairCount);
        using var entries = JsonDocument.Parse(await EntriesAsync());
        Assert.Single(entries.RootElement.EnumerateArray());
    }

    [Theory]
    [InlineData("Portal", true, true, 1)]
    [InlineData("WebApp", false, true, 0)]
    [InlineData("WebApp", true, false, 0)]
    [InlineData("ServiceApp", true, true, 0)]
    public async Task Import_UsesExistingEligibilityAndPortalTargetRules(
        string appType, bool enabled, bool allowed, int expectedCount)
    {
        await CreatePortalTableAsync();
        await ImportAsync(Guid.NewGuid(), appType, enabled, allowed);
        using var entries = JsonDocument.Parse(await EntriesAsync());
        Assert.Equal(expectedCount, entries.RootElement.GetArrayLength());
        if (expectedCount > 0)
            Assert.Equal(JsonValueKind.Null, entries.RootElement[0].GetProperty("target_entry_key").ValueKind);
    }

    [Fact]
    public async Task DeletedEntry_IsRecreatedOnNextImport()
    {
        await CreatePortalTableAsync();
        var appId = Guid.NewGuid();
        await ImportAsync(appId);
        await ExecuteAsync("DELETE FROM omp_portal.portal_entries;");
        await ImportAsync(appId);
        using var entries = JsonDocument.Parse(await EntriesAsync());
        Assert.Single(entries.RootElement.EnumerateArray());
    }

    private Task<ModuleDefinitionImportResult> ImportAsync(
        Guid appId, string appType = "WebApp", bool enabled = true, bool allowed = true)
    {
        var seed = $"""
            IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = '{appId}')
                INSERT omp.AppInstances (AppInstanceId, AppId, DisplayName, Description, SortOrder, IsEnabled, IsAllowed)
                SELECT '{appId}', AppId, N'New app', N'App description', 25, {(enabled ? 1 : 0)}, {(allowed ? 1 : 0)}
                FROM omp.Apps WHERE AppKey = N'entry_sync_test';
            """;
        var json = JsonSerializer.Serialize(new
        {
            moduleKey = "entry_sync_test", definitionVersion = "1.0.0",
            module = new { displayName = "Entry sync test" },
            apps = new[] { new { appKey = "entry_sync_test", appType } },
            sqlScripts = new[] { new { key = "initialize-entry-sync", phase = "initialize",
                order = 1, execution = "idempotent", inlineSql = seed } }
        });
        var service = new ArtifactZipImportService(new StaticOptionsMonitor(),
            _database.CreateHostAgentRepository(), NullLogger<ArtifactZipImportService>.Instance);
        return service.ImportModuleDefinitionAsync(new ModuleDefinitionImportDocument(
            "entry_sync_test", "1.0.0", 1, json,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), "test", [], []),
            CancellationToken.None);
    }

    private async Task CreatePortalTableAsync()
    {
        await ExecuteAsync("CREATE SCHEMA omp_portal;");
        await ExecuteAsync("""
            CREATE TABLE omp_portal.portal_entries (
                portal_entry_id int IDENTITY PRIMARY KEY,
                entry_key nvarchar(200) NOT NULL UNIQUE,
                display_name nvarchar(200) NOT NULL,
                description nvarchar(1000), target_entry_key nvarchar(200),
                source_app_instance_id uniqueidentifier, is_enabled bit NOT NULL,
                default_sort_order int NOT NULL,
                created_at datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
                updated_at datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
            """);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new SqlConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> EntriesAsync()
    {
        await using var conn = new SqlConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT (SELECT * FROM omp_portal.portal_entries ORDER BY entry_key FOR JSON PATH, INCLUDE_NULL_VALUES);", conn);
        return await cmd.ExecuteScalarAsync() as string ?? "[]";
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<HostAgentSettings>
    {
        public HostAgentSettings CurrentValue { get; } = new();
        public HostAgentSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<HostAgentSettings, string?> listener) => null;
    }
}
