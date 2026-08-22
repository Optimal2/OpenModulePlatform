using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Provides a local SQL Server test database provisioned by executing the real
/// core setup script (sql/1-setup-openmoduleplatform.sql) batch by batch, so
/// these tests exercise the shipped omp.MaterializeInstanceTemplate procedure
/// rather than a copy of it. Used to prove that disabling a template row
/// propagates to materialized runtime rows while hand-created rows are left
/// untouched.
/// </summary>
public sealed class TemplateMaterializationTestFixture : IAsyncLifetime
{
    public const string DatabaseName = "OpenModulePlatform_PortalTests_TemplateMaterialization";

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();
        await ApplyCoreSetupScriptAsync();
    }

    public async Task DisposeAsync()
    {
        await DropDatabaseAsync();
    }

    public async Task<int> InsertTemplateAsync(string templateKey, bool isEnabled = true)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.InstanceTemplates (TemplateKey, DisplayName, IsEnabled)
VALUES (@templateKey, @templateKey, @isEnabled);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@templateKey", templateKey);
        cmd.Parameters.AddWithValue("@isEnabled", isEnabled);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> InsertModuleAsync(string moduleKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName)
VALUES (@moduleKey, @moduleKey, N'core', N'omp');
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@moduleKey", moduleKey);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> InsertAppAsync(int moduleId, string appKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.Apps (ModuleId, AppKey, DisplayName, AppType)
VALUES (@moduleId, @appKey, @appKey, N'web');
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@moduleId", moduleId);
        cmd.Parameters.AddWithValue("@appKey", appKey);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> InsertTemplateModuleInstanceAsync(
        int instanceTemplateId,
        int moduleId,
        string moduleInstanceKey,
        bool isEnabled = true)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.InstanceTemplateModuleInstances (InstanceTemplateId, ModuleId, ModuleInstanceKey, DisplayName, IsEnabled)
VALUES (@instanceTemplateId, @moduleId, @moduleInstanceKey, @moduleInstanceKey, @isEnabled);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@instanceTemplateId", instanceTemplateId);
        cmd.Parameters.AddWithValue("@moduleId", moduleId);
        cmd.Parameters.AddWithValue("@moduleInstanceKey", moduleInstanceKey);
        cmd.Parameters.AddWithValue("@isEnabled", isEnabled);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> InsertTemplateAppInstanceAsync(
        int instanceTemplateModuleInstanceId,
        int appId,
        string appInstanceKey,
        bool isEnabled = true)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.InstanceTemplateAppInstances (InstanceTemplateModuleInstanceId, AppId, AppInstanceKey, DisplayName, IsEnabled)
VALUES (@instanceTemplateModuleInstanceId, @appId, @appInstanceKey, @appInstanceKey, @isEnabled);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            conn);
        cmd.Parameters.AddWithValue("@instanceTemplateModuleInstanceId", instanceTemplateModuleInstanceId);
        cmd.Parameters.AddWithValue("@appId", appId);
        cmd.Parameters.AddWithValue("@appInstanceKey", appInstanceKey);
        cmd.Parameters.AddWithValue("@isEnabled", isEnabled);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<Guid> InsertInstanceAsync(string instanceKey, int? instanceTemplateId, bool isEnabled = true)
    {
        var instanceId = Guid.NewGuid();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.Instances (InstanceId, InstanceKey, DisplayName, InstanceTemplateId, IsEnabled)
VALUES (@instanceId, @instanceKey, @instanceKey, @instanceTemplateId, @isEnabled);",
            conn);
        cmd.Parameters.AddWithValue("@instanceId", instanceId);
        cmd.Parameters.AddWithValue("@instanceKey", instanceKey);
        cmd.Parameters.AddWithValue("@instanceTemplateId", (object?)instanceTemplateId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isEnabled", isEnabled);
        await cmd.ExecuteNonQueryAsync();
        return instanceId;
    }

    public async Task InsertHostAsync(Guid instanceId, string hostKey, bool isEnabled = true)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.Hosts (HostId, InstanceId, HostKey, IsEnabled)
VALUES (NEWID(), @instanceId, @hostKey, @isEnabled);",
            conn);
        cmd.Parameters.AddWithValue("@instanceId", instanceId);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@isEnabled", isEnabled);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts a module instance directly, with no matching template row. This is
    /// what the materializer must never touch.
    /// </summary>
    public async Task<Guid> InsertHandCreatedModuleInstanceAsync(Guid instanceId, int moduleId, string moduleInstanceKey)
    {
        var moduleInstanceId = Guid.NewGuid();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.ModuleInstances (ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName, IsEnabled)
VALUES (@moduleInstanceId, @instanceId, @moduleId, @moduleInstanceKey, @moduleInstanceKey, 1);",
            conn);
        cmd.Parameters.AddWithValue("@moduleInstanceId", moduleInstanceId);
        cmd.Parameters.AddWithValue("@instanceId", instanceId);
        cmd.Parameters.AddWithValue("@moduleId", moduleId);
        cmd.Parameters.AddWithValue("@moduleInstanceKey", moduleInstanceKey);
        await cmd.ExecuteNonQueryAsync();
        return moduleInstanceId;
    }

    /// <summary>
    /// Inserts an app instance directly, with no matching template row. This is
    /// what the materializer must never touch.
    /// </summary>
    public async Task<Guid> InsertHandCreatedAppInstanceAsync(Guid moduleInstanceId, int appId, string appInstanceKey)
    {
        var appInstanceId = Guid.NewGuid();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
INSERT INTO omp.AppInstances (AppInstanceId, ModuleInstanceId, AppId, AppInstanceKey, DisplayName, IsEnabled, IsAllowed, DesiredState)
VALUES (@appInstanceId, @moduleInstanceId, @appId, @appInstanceKey, @appInstanceKey, 1, 1, 1);",
            conn);
        cmd.Parameters.AddWithValue("@appInstanceId", appInstanceId);
        cmd.Parameters.AddWithValue("@moduleInstanceId", moduleInstanceId);
        cmd.Parameters.AddWithValue("@appId", appId);
        cmd.Parameters.AddWithValue("@appInstanceKey", appInstanceKey);
        await cmd.ExecuteNonQueryAsync();
        return appInstanceId;
    }

    public async Task SetTemplateEnabledAsync(int instanceTemplateId, bool isEnabled)
    {
        await ExecuteAsync(
            "UPDATE omp.InstanceTemplates SET IsEnabled = @isEnabled WHERE InstanceTemplateId = @id;",
            new SqlParameter("@id", instanceTemplateId),
            new SqlParameter("@isEnabled", isEnabled));
    }

    public async Task SetTemplateModuleInstanceEnabledAsync(int instanceTemplateModuleInstanceId, bool isEnabled)
    {
        await ExecuteAsync(
            "UPDATE omp.InstanceTemplateModuleInstances SET IsEnabled = @isEnabled WHERE InstanceTemplateModuleInstanceId = @id;",
            new SqlParameter("@id", instanceTemplateModuleInstanceId),
            new SqlParameter("@isEnabled", isEnabled));
    }

    public async Task SetTemplateAppInstanceEnabledAsync(int instanceTemplateAppInstanceId, bool isEnabled)
    {
        await ExecuteAsync(
            "UPDATE omp.InstanceTemplateAppInstances SET IsEnabled = @isEnabled WHERE InstanceTemplateAppInstanceId = @id;",
            new SqlParameter("@id", instanceTemplateAppInstanceId),
            new SqlParameter("@isEnabled", isEnabled));
    }

    public async Task<(int ModuleInstanceChanges, int AppInstanceChanges)> MaterializeAsync(
        string? instanceKey = null,
        string? hostKey = null)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
EXEC omp.MaterializeInstanceTemplate
    @InstanceKey = @instanceKey,
    @HostKey = @hostKey,
    @RequestedBy = N'test';",
            conn);
        cmd.Parameters.AddWithValue("@instanceKey", (object?)instanceKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hostKey", (object?)hostKey ?? DBNull.Value);
        await using var rdr = await cmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync(), "MaterializeInstanceTemplate returned no result row.");
        return (rdr.GetInt32(0), rdr.GetInt32(1));
    }

    public async Task<bool?> GetModuleInstanceEnabledAsync(string instanceKey, string moduleInstanceKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
SELECT mi.IsEnabled
FROM omp.ModuleInstances mi
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
WHERE i.InstanceKey = @instanceKey
  AND mi.ModuleInstanceKey = @moduleInstanceKey;",
            conn);
        cmd.Parameters.AddWithValue("@instanceKey", instanceKey);
        cmd.Parameters.AddWithValue("@moduleInstanceKey", moduleInstanceKey);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : (bool)value;
    }

    public async Task<bool?> GetAppInstanceEnabledAsync(string instanceKey, string moduleInstanceKey, string appInstanceKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            @"
SELECT ai.IsEnabled
FROM omp.AppInstances ai
INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId
INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId
WHERE i.InstanceKey = @instanceKey
  AND mi.ModuleInstanceKey = @moduleInstanceKey
  AND ai.AppInstanceKey = @appInstanceKey;",
            conn);
        cmd.Parameters.AddWithValue("@instanceKey", instanceKey);
        cmd.Parameters.AddWithValue("@moduleInstanceKey", moduleInstanceKey);
        cmd.Parameters.AddWithValue("@appInstanceKey", appInstanceKey);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : (bool)value;
    }

    private async Task ExecuteAsync(string sql, params SqlParameter[] parameters)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureDatabaseExistsAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            builder.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");
    }

    private async Task ApplyCoreSetupScriptAsync()
    {
        var setupSql = ReadRepositoryTextFile("sql", "1-setup-openmoduleplatform.sql");

        // Strip the historical local development database switch, the same way
        // scripts/dev/embed-module-definition-sql.ps1 does, so the script runs
        // against the fixture database instead.
        var portableSql = Regex.Replace(
            setupSql,
            @"^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var batch in SplitBatches(portableSql))
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> SplitBatches(string sql)
    {
        return Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline)
            .Where(batch => !string.IsNullOrWhiteSpace(batch));
    }

    private async Task DropDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
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
        catch (SqlException)
        {
            // Best-effort cleanup.
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }

    private static string ReadRepositoryTextFile(params string[] relativePathSegments)
    {
        var rootedSegment = relativePathSegments.FirstOrDefault(Path.IsPathRooted);
        if (rootedSegment is not null)
        {
            throw new ArgumentException("Repository test paths must be relative.", nameof(relativePathSegments));
        }

        var segments = new string[relativePathSegments.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(relativePathSegments, 0, segments, 1, relativePathSegments.Length);
        return File.ReadAllText(Path.Join(segments));
    }
}
