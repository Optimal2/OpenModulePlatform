using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Provisioning;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Disposable database provisioned with the real core setup script, for
/// <see cref="WorkerAppInstanceProvisioningService"/>. Every test seeds its own module,
/// installation and hosts under unique keys, so tests never see each other's rows.
/// </summary>
public sealed class WorkerAppInstanceProvisioningTestFixture : IAsyncLifetime
{
    // Per-process name (pid + start time) so concurrent test hosts never share a
    // database; stale copies from crashed runs are swept by the provisioner.
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("WorkerAppProvisioning");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = "master"
        };

        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            builder.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");
        await CoreSetupScript.ApplyAsync(ConnectionString);
    }

    public async Task DisposeAsync()
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
        catch (SqlException ex)
        {
            OmpTestCleanupLog.RecordFailure(
                nameof(WorkerAppInstanceProvisioningTestFixture),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
    }

    public WorkerAppInstanceProvisioningService CreateService(params string[] permissions)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = ConnectionString
            })
            .Build();
        IReadOnlySet<string> granted = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
        return new WorkerAppInstanceProvisioningService(
            new PortalSqlConnectionFactory(configuration),
            (_, _) => Task.FromResult(granted));
    }

    public static ClaimsPrincipal Caller()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "provisioning-test")], "Test"));

    /// <summary>A principal without an authentication type, so <c>IsAuthenticated</c> is false.</summary>
    public static ClaimsPrincipal AnonymousCaller()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "anonymous")]));

    /// <summary>
    /// Seeds an installation with one module (a web app declaring the module permission and a
    /// worker app), one enabled module instance, and the given hosts. Hosts listed in
    /// <paramref name="hostsWithHostAgent"/> get a HostAgent runtime row.
    /// </summary>
    public async Task<Scenario> SeedAsync(int hostCount, int[]? hostsWithHostAgent = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var scenario = new Scenario
        {
            Suffix = suffix,
            InstanceId = Guid.NewGuid(),
            ModuleKey = "prov_mod_" + suffix,
            ModuleInstanceId = Guid.NewGuid(),
            ModulePermission = "ProvTest.Admin." + suffix
        };

        await using var conn = await OpenAsync();
        await ExecuteAsync(conn, @"
INSERT INTO omp.Instances (InstanceId, InstanceKey, DisplayName) VALUES (@instanceId, @instanceKey, @instanceKey);
INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName) VALUES (@moduleKey, @moduleKey, N'module', N'prov');
DECLARE @moduleId int = CAST(SCOPE_IDENTITY() AS int);
INSERT INTO omp.ModuleInstances (ModuleInstanceId, InstanceId, ModuleId, ModuleInstanceKey, DisplayName)
VALUES (@moduleInstanceId, @instanceId, @moduleId, N'default', N'Default');
INSERT INTO omp.Apps (ModuleId, AppKey, DisplayName, AppType) VALUES (@moduleId, N'prov_web', N'Web', N'WebApp');
DECLARE @webAppId int = CAST(SCOPE_IDENTITY() AS int);
INSERT INTO omp.Apps (ModuleId, AppKey, DisplayName, AppType) VALUES (@moduleId, N'prov_worker', N'Worker', N'Worker');
INSERT INTO omp.Permissions (Name) VALUES (@permission);
INSERT INTO omp.AppPermissions (AppId, PermissionId) VALUES (@webAppId, CAST(SCOPE_IDENTITY() AS int));",
            ("@instanceId", scenario.InstanceId),
            ("@instanceKey", "prov-inst-" + suffix),
            ("@moduleKey", scenario.ModuleKey),
            ("@moduleInstanceId", scenario.ModuleInstanceId),
            ("@permission", scenario.ModulePermission));

        scenario.ModuleId = await ScalarAsync<int>(conn, "SELECT ModuleId FROM omp.Modules WHERE ModuleKey = @moduleKey", ("@moduleKey", scenario.ModuleKey));
        scenario.WorkerAppId = await ScalarAsync<int>(conn, "SELECT AppId FROM omp.Apps WHERE ModuleId = @moduleId AND AppKey = N'prov_worker'", ("@moduleId", scenario.ModuleId));
        scenario.WebAppId = await ScalarAsync<int>(conn, "SELECT AppId FROM omp.Apps WHERE ModuleId = @moduleId AND AppKey = N'prov_web'", ("@moduleId", scenario.ModuleId));

        for (var index = 0; index < hostCount; index++)
        {
            scenario.Hosts.Add(await AddHostAsync(conn, scenario.InstanceId, $"PROV{index}-{suffix}.example.test", hostsWithHostAgent?.Contains(index) ?? true));
        }

        return scenario;
    }

    public async Task<Guid> AddHostAsync(Guid instanceId, string hostKey, bool withHostAgent)
    {
        await using var conn = await OpenAsync();
        return await AddHostAsync(conn, instanceId, hostKey, withHostAgent);
    }

    private static async Task<Guid> AddHostAsync(SqlConnection conn, Guid instanceId, string hostKey, bool withHostAgent)
    {
        var hostId = Guid.NewGuid();
        await ExecuteAsync(conn, @"
INSERT INTO omp.Hosts (HostId, InstanceId, HostKey, DisplayName) VALUES (@hostId, @instanceId, @hostKey, @hostKey);",
            ("@hostId", hostId), ("@instanceId", instanceId), ("@hostKey", hostKey));
        if (withHostAgent)
        {
            await RegisterHostAgentAsync(conn, hostId, DefaultPipe(hostKey), isActive: true, lastSeenSecondsAgo: 0, "OMP.HostAgent");
        }

        return hostId;
    }

    /// <summary>The pipe a HostAgent with an empty RpcPipeName setting serves and registers.</summary>
    public static string DefaultPipe(string hostKey) => "OpenModulePlatform.HostAgent." + hostKey;

    /// <summary>
    /// Writes a HostAgent runtime-state row the way HostAgent publishes it. A null
    /// <paramref name="pipeName"/> is what an agent older than the RpcPipeName column leaves.
    /// </summary>
    public async Task RegisterHostAgentAsync(
        Guid hostId,
        string? pipeName,
        bool isActive = true,
        int lastSeenSecondsAgo = 0,
        string serviceName = "OMP.HostAgent")
    {
        await using var conn = await OpenAsync();
        await RegisterHostAgentAsync(conn, hostId, pipeName, isActive, lastSeenSecondsAgo, serviceName);
    }

    private static Task RegisterHostAgentAsync(
        SqlConnection conn,
        Guid hostId,
        string? pipeName,
        bool isActive,
        int lastSeenSecondsAgo,
        string serviceName)
        => ExecuteAsync(conn, @"
INSERT INTO omp.HostAgentRuntimeStates (HostId, ServiceName, IsActive, LastSeenUtc, RpcPipeName)
VALUES (@hostId, @serviceName, @isActive, DATEADD(SECOND, -@age, SYSUTCDATETIME()), @pipeName);",
            ("@hostId", hostId),
            ("@serviceName", serviceName),
            ("@isActive", isActive),
            ("@age", lastSeenSecondsAgo),
            ("@pipeName", (object?)pipeName ?? DBNull.Value));

    public async Task DisableAppAsync(int appId)
    {
        await using var conn = await OpenAsync();
        await ExecuteAsync(conn, "UPDATE omp.Apps SET IsEnabled = 0 WHERE AppId = @appId", ("@appId", appId));
    }

    public async Task<Guid> AddInstallationAsync(string suffix)
    {
        var instanceId = Guid.NewGuid();
        await using var conn = await OpenAsync();
        await ExecuteAsync(conn, "INSERT INTO omp.Instances (InstanceId, InstanceKey, DisplayName) VALUES (@id, @key, @key);",
            ("@id", instanceId), ("@key", "prov-other-" + suffix));
        return instanceId;
    }

    public async Task<int> AddArtifactAsync(int appId, string version, string packageType = "worker", bool enabled = true)
    {
        await using var conn = await OpenAsync();
        return await ScalarAsync<int>(conn, @"
INSERT INTO omp.Artifacts (AppId, Version, PackageType, TargetName, IsEnabled) VALUES (@appId, @version, @packageType, N'worker', @enabled);
SELECT CAST(SCOPE_IDENTITY() AS int);",
            ("@appId", appId), ("@version", version), ("@packageType", packageType), ("@enabled", enabled));
    }

    /// <summary>Applies a module definition whose only worker slot allows <paramref name="min"/>..<paramref name="max"/>.</summary>
    public async Task ApplyDefinitionAsync(Scenario scenario, string? min, string? max)
    {
        await using var conn = await OpenAsync();
        await ExecuteAsync(conn, @"
INSERT INTO omp.ModuleDefinitionDocuments (ModuleKey, DefinitionVersion, DefinitionJson, DefinitionSha256, IsApplied, AppliedUtc)
VALUES (@moduleKey, N'1.0.0', N'{}', N'sha', 1, SYSUTCDATETIME());
INSERT INTO omp.ModuleDefinitionArtifactCompatibility (ModuleDefinitionDocumentId, AppKey, PackageType, TargetName, MinArtifactVersion, MaxArtifactVersion)
VALUES (CAST(SCOPE_IDENTITY() AS int), N'prov_worker', N'worker', N'worker', @min, @max);",
            ("@moduleKey", scenario.ModuleKey), ("@min", (object?)min ?? DBNull.Value), ("@max", (object?)max ?? DBNull.Value));
    }

    public async Task<Guid> AddAppInstanceAsync(
        Scenario scenario,
        string key,
        int appId,
        int? artifactId,
        string? installationName,
        Guid? hostId = null)
    {
        var id = Guid.NewGuid();
        await using var conn = await OpenAsync();
        await ExecuteAsync(conn, @"
INSERT INTO omp.AppInstances (AppInstanceId, ModuleInstanceId, HostId, AppId, AppInstanceKey, DisplayName, InstallationName, ArtifactId)
VALUES (@id, @moduleInstanceId, @hostId, @appId, @key, @key, @installationName, @artifactId);",
            ("@id", id),
            ("@moduleInstanceId", scenario.ModuleInstanceId),
            ("@hostId", (object?)hostId ?? DBNull.Value),
            ("@appId", appId),
            ("@key", key),
            ("@installationName", (object?)installationName ?? DBNull.Value),
            ("@artifactId", (object?)artifactId ?? DBNull.Value));
        return id;
    }

    public async Task<Guid> AddWorkerAsync(Guid appInstanceId, Guid hostId, string key, string? configurationJson)
    {
        var id = Guid.NewGuid();
        await using var conn = await OpenAsync();
        await ExecuteAsync(conn, @"
INSERT INTO omp.WorkerInstances (WorkerInstanceId, AppInstanceId, HostId, WorkerInstanceKey, DisplayName, ConfigurationJson)
VALUES (@id, @appInstanceId, @hostId, @key, @key, @configurationJson);",
            ("@id", id), ("@appInstanceId", appInstanceId), ("@hostId", hostId), ("@key", key),
            ("@configurationJson", (object?)configurationJson ?? DBNull.Value));
        return id;
    }

    public async Task AddHostConfigurationAsync(
        string hostKey,
        string valuesJson,
        string version = "1.0.0",
        int updatedSecondsAgo = 0)
    {
        var json = $"{{\"formatVersion\":1,\"objectType\":\"host-configuration\",\"hostKey\":\"{hostKey}\",\"configurationVersion\":\"{version}\",\"values\":{valuesJson}}}";
        await using var conn = await OpenAsync();
        await ExecuteAsync(conn, @"
INSERT INTO omp.HostConfigurationDocuments (HostKey, ConfigurationVersion, ConfigurationJson, ConfigurationSha256, UpdatedUtc)
VALUES (@hostKey, @version, @json, N'sha', DATEADD(SECOND, -@age, SYSUTCDATETIME()));",
            ("@hostKey", hostKey), ("@version", version), ("@json", json), ("@age", updatedSecondsAgo));
    }

    public async Task<int> CountAppInstancesAsync(Scenario scenario, string key)
    {
        await using var conn = await OpenAsync();
        return await ScalarAsync<int>(conn,
            "SELECT COUNT(*) FROM omp.AppInstances WHERE ModuleInstanceId = @moduleInstanceId AND AppInstanceKey = @key",
            ("@moduleInstanceId", scenario.ModuleInstanceId), ("@key", key));
    }

    public async Task<int> CountWorkersAsync(Scenario scenario)
    {
        await using var conn = await OpenAsync();
        return await ScalarAsync<int>(conn, @"
SELECT COUNT(*) FROM omp.WorkerInstances wi
INNER JOIN omp.AppInstances ai ON ai.AppInstanceId = wi.AppInstanceId
WHERE ai.ModuleInstanceId = @moduleInstanceId",
            ("@moduleInstanceId", scenario.ModuleInstanceId));
    }

    public async Task<AppInstanceSnapshot> ReadAppInstanceAsync(Guid appInstanceId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(@"
SELECT ModuleInstanceId, HostId, TargetHostTemplateId, AppId, InstallationName, ArtifactId, IsEnabled, IsAllowed, DesiredState
FROM omp.AppInstances WHERE AppInstanceId = @id", conn);
        cmd.Parameters.AddWithValue("@id", appInstanceId);
        await using var rdr = await cmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync());
        return new AppInstanceSnapshot(
            rdr.GetGuid(0),
            rdr.IsDBNull(1) ? null : rdr.GetGuid(1),
            rdr.IsDBNull(2) ? null : rdr.GetInt32(2),
            rdr.GetInt32(3),
            rdr.IsDBNull(4) ? null : rdr.GetString(4),
            rdr.IsDBNull(5) ? null : rdr.GetInt32(5),
            rdr.GetBoolean(6) && rdr.GetBoolean(7) && rdr.GetByte(8) == 1);
    }

    public async Task<IReadOnlyList<WorkerSnapshot>> ReadWorkersAsync(Guid appInstanceId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(@"
SELECT WorkerInstanceId, HostId, WorkerInstanceKey, ArtifactId, ConfigurationJson
FROM omp.WorkerInstances WHERE AppInstanceId = @id ORDER BY WorkerInstanceKey", conn);
        cmd.Parameters.AddWithValue("@id", appInstanceId);
        var rows = new List<WorkerSnapshot>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add(new WorkerSnapshot(
                rdr.GetGuid(0),
                rdr.GetGuid(1),
                rdr.GetString(2),
                rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
                rdr.IsDBNull(4) ? null : rdr.GetString(4)));
        }

        return rows;
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecuteAsync(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed class Scenario
    {
        public required string Suffix { get; init; }
        public required Guid InstanceId { get; init; }
        public required string ModuleKey { get; init; }
        public required Guid ModuleInstanceId { get; init; }
        public required string ModulePermission { get; init; }
        public int ModuleId { get; set; }
        public int WorkerAppId { get; set; }
        public int WebAppId { get; set; }
        public List<Guid> Hosts { get; } = [];

        public string HostKey(int index) => $"PROV{index}-{Suffix}.example.test";

        public WorkerAppInstanceProvisioningRequest Request(string key, params Guid[] hostIds) => new()
        {
            ModuleKey = ModuleKey,
            AppKey = "prov_worker",
            AppInstanceKey = key,
            DisplayName = "Prov Worker " + key,
            HostIds = hostIds,
            HostAgentRpcConfigurationPath = "ProvModule:HostAgentRpc",
            ModuleAdministrationPermission = ModulePermission
        };
    }

    public sealed record AppInstanceSnapshot(
        Guid ModuleInstanceId,
        Guid? HostId,
        int? TargetHostTemplateId,
        int AppId,
        string? InstallationName,
        int? ArtifactId,
        bool IsActive);

    public sealed record WorkerSnapshot(
        Guid WorkerInstanceId,
        Guid HostId,
        string WorkerInstanceKey,
        int? ArtifactId,
        string? ConfigurationJson);
}
