using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Services;
using WorkerSqlFactory = OpenModulePlatform.WorkerManager.WindowsService.Services.SqlConnectionFactory;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class HostWorkerLifecycleTests : IDisposable
{
    private readonly OmpHostArtifactRepositoryTestDatabase _database = new();
    private readonly Guid _hostA;
    private readonly Guid _hostB;
    private readonly Guid _app;
    private readonly CaptureLogger _logger = new();

    public HostWorkerLifecycleTests()
    {
        try
        {
            var instanceId = Guid.NewGuid();
            _hostA = _database.InsertHost("host-a", instanceId: instanceId);
            _hostB = _database.InsertHost("host-b", instanceId: instanceId);
            _app = _database.InsertAppInstance(Guid.NewGuid(), "worker-app");
            Execute("""
                ALTER TABLE omp.AppInstances ADD TargetHostTemplateId int NULL, InstallPath nvarchar(500) NULL,
                    InstallationName nvarchar(150) NULL, SortOrder int NOT NULL DEFAULT(0);
                ALTER TABLE omp.Artifacts ADD Version nvarchar(50) NOT NULL DEFAULT('1.0.0');
                ALTER TABLE omp.WorkerInstances ADD WorkerInstanceKey nvarchar(150) NOT NULL DEFAULT('worker'),
                    IsAllowed bit NOT NULL DEFAULT(1), DesiredState tinyint NOT NULL DEFAULT(1),
                    ConfigurationJson nvarchar(max) NULL, SortOrder int NOT NULL DEFAULT(0);
                CREATE TABLE omp.HostDeploymentAssignments(HostId uniqueidentifier, HostTemplateId int, IsActive bit);
                CREATE TABLE omp.Apps(AppId int PRIMARY KEY, IsEnabled bit);
                CREATE TABLE omp.AppWorkerDefinitions(AppId int, WorkerTypeKey nvarchar(200),
                    PluginRelativePath nvarchar(400), IsEnabled bit, RuntimeKind nvarchar(100));
                """);
            Execute("""
                INSERT omp.Apps VALUES(1, 1);
                INSERT omp.AppWorkerDefinitions VALUES(1, 'test.worker', 'Worker.dll', 1, 'windows-worker-plugin');
                UPDATE omp.Artifacts SET PackageType = 'worker';
                UPDATE omp.AppInstances SET AppId = 1, TargetHostTemplateId = 1, InstallPath = 'C:\workers\test';
                INSERT omp.HostDeploymentAssignments SELECT HostId, 1, 1 FROM omp.Hosts;
                """);
        }
        catch
        {
            _database.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task DesiredStateTwo_IsStillReconciledByHostAgent()
    {
        Execute("UPDATE omp.Artifacts SET PackageType = 'service-app'; UPDATE omp.AppInstances SET DesiredState = 2;");
        Execute("INSERT omp.HostAppDeploymentStates(HostId, AppInstanceId, RuntimeName, TargetPath) VALUES(@host, @app, 'test-service', 'C:\\workers\\test');",
            new("@host", _hostA), new("@app", _app));

        var candidates = await new OmpHostArtifactRepository(_database.CreateFactory())
            .GetDisabledServiceAppServicesAsync("host-a", 100, CancellationToken.None);

        Assert.Equal(_app, Assert.Single(candidates).AppInstanceId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipleRoleHosts_RejectUnpinnedWorkersWithDiagnostic(bool explicitWorker)
    {
        if (explicitWorker) InsertWorker(null);

        Assert.Empty(await Catalog("host-a").GetDesiredWorkersAsync(CancellationToken.None));
        Assert.Empty(await Catalog("host-b").GetDesiredWorkersAsync(CancellationToken.None));
        Assert.Contains(_logger.Messages, message => message.Contains("HostId", StringComparison.Ordinal)
            && message.Contains("multiple", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExplicitHost_RunsOnlyOnAssignedHost()
    {
        var worker = InsertWorker(_hostA);
        Assert.Equal(worker, Assert.Single(await Catalog("host-a").GetDesiredWorkersAsync(CancellationToken.None)).WorkerInstanceId);
        Assert.Empty(await Catalog("host-b").GetDesiredWorkersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SingleActiveRoleHost_CanRunUnpinnedWorker()
    {
        var worker = InsertWorker(null);
        Execute("UPDATE omp.HostDeploymentAssignments SET IsActive = 0 WHERE HostId = @host;", new SqlParameter("@host", _hostB));
        Assert.Equal(worker, Assert.Single(await Catalog("host-a").GetDesiredWorkersAsync(CancellationToken.None)).WorkerInstanceId);
        Assert.Empty(await Catalog("host-b").GetDesiredWorkersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StoppedApp_DoesNotRunExplicitWorker()
    {
        InsertWorker(_hostA);
        Execute("UPDATE omp.AppInstances SET DesiredState = 2;");
        Assert.Empty(await Catalog("host-a").GetDesiredWorkersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ImplicitWorker_HasExplicitWarningIdentifyingItsRuntimeSummary()
    {
        Execute("UPDATE omp.AppInstances SET HostId = @host;", new SqlParameter("@host", _hostA));
        Assert.Equal(_app, Assert.Single(await Catalog("host-a").GetDesiredWorkersAsync(CancellationToken.None)).WorkerInstanceId);
        Assert.Contains(_logger.Messages, message => message.Contains("implicit", StringComparison.OrdinalIgnoreCase)
            && message.Contains(_app.ToString(), StringComparison.OrdinalIgnoreCase)
            && message.Contains("AppInstanceRuntimeStates", StringComparison.Ordinal));
    }

    private Guid InsertWorker(Guid? host)
    {
        var id = Guid.NewGuid();
        Execute("INSERT omp.WorkerInstances(WorkerInstanceId, AppInstanceId, HostId) VALUES(@id, @app, @host);",
            new("@id", id), new("@app", _app), new("@host", (object?)host ?? DBNull.Value));
        return id;
    }

    private OmpDatabaseWorkerInstanceCatalog Catalog(string hostKey)
    {
        var settings = new WorkerManagerSettings { HostKey = hostKey };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OmpDb"] = _database.ConnectionString
        }).Build();
        return new(new WorkerSqlFactory(configuration),
            new FakeOptionsMonitor<WorkerManagerSettings> { CurrentValue = settings }, _logger);
    }

    private void Execute(string sql, params SqlParameter[] parameters)
    {
        using var connection = new SqlConnection(_database.ConnectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _database.Dispose();

    private sealed class CaptureLogger : ILogger<OmpDatabaseWorkerInstanceCatalog>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Messages.Add(formatter(state, exception));
        }
    }
}
