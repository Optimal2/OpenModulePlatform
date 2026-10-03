using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed partial class UniversalPackageSeedSqlOrderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_AfterSql_CreatesPortalEntryAndPreservesCustomisations(bool hostAgent)
    {
        await VerifyPortalEntryImportAsync(hostAgent, failSync: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_WhenPortalEntrySyncFails_SucceedsAndFillsArtifactPointer(bool hostAgent)
    {
        await VerifyPortalEntryImportAsync(hostAgent, failSync: true);
    }

    private async Task VerifyPortalEntryImportAsync(bool hostAgent, bool failSync)
    {
        var moduleKey = $"entrysync{(hostAgent ? "ha" : "po")}{(failSync ? "fail" : "ok")}";
        var appInstanceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var moduleInstanceId = Guid.NewGuid();
        var moduleId = await _fixture.InsertModuleAsync(moduleKey, "omp_" + moduleKey);
        var appId = await _fixture.InsertAppAsync(moduleId, "web");
        await _fixture.ExecuteAsync(
            $"UPDATE omp.Apps SET AppType = N'WebApp' WHERE AppId = {appId};",
            $"INSERT omp.Instances (InstanceId, InstanceKey, DisplayName) VALUES ('{instanceId}', N'{moduleKey}', N'Entry sync test');",
            $"INSERT omp.ModuleInstances (ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName) VALUES ('{moduleInstanceId}', '{instanceId}', {moduleId}, N'{moduleKey}', N'Entry sync test');",
            "IF SCHEMA_ID(N'omp_portal') IS NULL EXEC(N'CREATE SCHEMA omp_portal');",
            """
            IF OBJECT_ID(N'omp_portal.portal_entries', N'U') IS NULL
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
        var artifactId = await _fixture.InsertArtifactAsync(
            appId, "1.0.0", "web-app", "web", $"{moduleKey}/web/1.0.0", new string('a', 64));

        var workRoot = CreateWorkRoot();
        try
        {
            if (failSync)
            {
                // A real SqlException from the post-commit reconciliation, not from
                // definition SQL. The module's own SQL and artifact fill must survive.
                await _fixture.ExecuteAsync("""
                    CREATE TRIGGER omp_portal.FailEntrySync ON omp_portal.portal_entries
                    AFTER INSERT AS THROW 51001, N'Injected portal-entry sync failure', 1;
                    """);
            }

            var packagePath = Path.Join(workRoot, $"omp-universal__{moduleKey}__1.0.0.zip");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteTextEntry(archive, UniversalModulePackageReaderManifestName,
                    """{"formatVersion":1,"packageKey":"entry-sync-test","packageVersion":"1.0.0"}""");
                WriteTextEntry(archive, $"module-definitions/{moduleKey}.module-definition.json",
                    JsonSerializer.Serialize(new
                    {
                        moduleKey, definitionVersion = "1.0.0", formatVersion = 1,
                        module = new { displayName = "Entry sync test", moduleType = "WebModule", schemaName = "omp_" + moduleKey },
                        apps = new[] { new { appKey = "web", displayName = "Web", appType = "WebApp" } },
                        sqlScripts = new[] { new { key = "initialize-entry", phase = "initialize", order = 1,
                            execution = "idempotent", inlineSql = $"""
                            IF NOT EXISTS (SELECT 1 FROM omp.AppInstances WHERE AppInstanceId = '{appInstanceId}')
                                INSERT omp.AppInstances (AppInstanceId, ModuleInstanceId, AppId, AppInstanceKey, DisplayName, SortOrder)
                                VALUES ('{appInstanceId}', '{moduleInstanceId}', {appId}, N'web', N'Imported web app', 25);
                            """ } }
                    }));
            }

            var hostLog = new EntrySyncTestLogger<ArtifactZipImportService>();
            var portalLog = new EntrySyncTestLogger<PortableModulePackageService>();
            var storeRoot = Directory.CreateDirectory(Path.Join(workRoot, "store")).FullName;
            var importRoot = Directory.CreateDirectory(Path.Join(workRoot, "import")).FullName;
            var portal = CreatePortalService(storeRoot, portalLog);
            var importCount = 0;

            async Task ImportAsync()
            {
                importCount++;
                if (hostAgent)
                {
                    File.Copy(packagePath, Path.Join(importRoot, Path.GetFileName(packagePath)), overwrite: true);
                    await RunHostAgentImportAsync(importRoot, storeRoot, hostLog);
                    Assert.Empty(Directory.GetFiles(Path.Join(importRoot, "failed")));
                    Assert.NotEmpty(Directory.GetFiles(Path.Join(importRoot, "processed"), "*.zip"));
                }
                else
                {
                    var result = await ImportPortalPackageAsync(portal, packagePath);
                    Assert.DoesNotContain(result.Items, item => item.Status == "Failed");
                    Assert.Equal(importCount == 1 ? "Applied" : "Stored",
                        Assert.Single(result.Items, item => item.Kind == "module-definition").Status);
                }
            }

            await ImportAsync();
            Assert.Equal(1, await _fixture.CountSucceededDefinitionSqlExecutionsAsync(moduleKey, "1.0.0"));
            Assert.Equal(artifactId, Convert.ToInt32(await EntrySyncScalarAsync(
                $"SELECT ArtifactId FROM omp.AppInstances WHERE AppInstanceId = '{appInstanceId}';")));

            var entryQuery = $"SELECT COALESCE((SELECT * FROM omp_portal.portal_entries WHERE source_app_instance_id = '{appInstanceId}' FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]');";
            if (failSync)
            {
                Assert.Equal("[]", await EntrySyncScalarAsync(entryQuery));
                var messages = hostAgent ? hostLog.Messages : portalLog.Messages;
                Assert.Contains(messages, message => message.Level == LogLevel.Warning
                    && message.Exception is SqlException
                    && message.Text.Contains(moduleKey, StringComparison.Ordinal)
                    && message.Text.Contains("AttemptedEntries=1", StringComparison.Ordinal));
                await _fixture.ExecuteAsync("DROP TRIGGER omp_portal.FailEntrySync;");
                await ImportAsync();
            }

            using var entries = JsonDocument.Parse((string)(await EntrySyncScalarAsync(entryQuery))!);
            var entry = Assert.Single(entries.RootElement.EnumerateArray());
            Assert.Equal($"app:{appInstanceId:N}:home", entry.GetProperty("entry_key").GetString());
            Assert.Equal("Imported web app", entry.GetProperty("display_name").GetString());
            Assert.Equal(25, entry.GetProperty("default_sort_order").GetInt32());
            await _fixture.ExecuteAsync($"""
                UPDATE omp_portal.portal_entries SET display_name = N'Custom name', description = N'Custom description',
                    target_entry_key = N'custom:target', is_enabled = 0, default_sort_order = 987, updated_at = '2001-02-03'
                WHERE source_app_instance_id = '{appInstanceId}';
                """);
            var customised = await EntrySyncScalarAsync(entryQuery);
            await ImportAsync();
            Assert.Equal(customised, await EntrySyncScalarAsync(entryQuery));
        }
        finally
        {
            await _fixture.ExecuteAsync(
                "DROP TRIGGER IF EXISTS omp_portal.FailEntrySync;",
                $"DELETE omp_portal.portal_entries WHERE source_app_instance_id = '{appInstanceId}';",
                $"DELETE omp.AppInstances WHERE AppInstanceId = '{appInstanceId}';");
            TryDeleteDirectory(workRoot);
        }
    }

    private async Task<object?> EntrySyncScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private sealed class EntrySyncTestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception), exception));
    }
}
