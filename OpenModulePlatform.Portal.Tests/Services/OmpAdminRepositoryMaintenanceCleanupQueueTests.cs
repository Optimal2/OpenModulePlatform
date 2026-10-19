// File: OpenModulePlatform.Portal.Tests/Services/OmpAdminRepositoryMaintenanceCleanupQueueTests.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using PortalSqlConnectionFactory = OpenModulePlatform.Web.Shared.Services.SqlConnectionFactory;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Queueing maintenance cleanup inserts a HostAgent job and then marks its findings as
/// queued. Those two writes used to run on separate connections, so a failure between
/// them left a pending cleanup job whose findings were still selectable, and the next
/// attempt queued a second job for the same findings. Proven against the real core
/// schema: a trigger makes the marking UPDATE fail.
/// </summary>
public sealed class OmpAdminRepositoryMaintenanceCleanupQueueTests
    : IClassFixture<MaintenanceCleanupQueueTestFixture>, IAsyncLifetime
{
    private readonly MaintenanceCleanupQueueTestFixture _fixture;

    public OmpAdminRepositoryMaintenanceCleanupQueueTests(MaintenanceCleanupQueueTestFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_failed_marking_leaves_no_cleanup_job_behind()
    {
        var findingIds = new[]
        {
            await _fixture.InsertGlobalFindingAsync("finding-a"),
            await _fixture.InsertGlobalFindingAsync("finding-b")
        };
        await _fixture.ExecuteAsync(MaintenanceCleanupQueueTestFixture.FailQueuedMarkingTriggerSql);
        var repository = _fixture.CreatePortalRepository();

        await Assert.ThrowsAsync<SqlException>(
            () => repository.QueueMaintenanceCleanupAsync(findingIds, "tester", CancellationToken.None));

        Assert.Equal(0, await _fixture.CountCleanupJobsAsync());
        Assert.Equal(2, await _fixture.CountFindingsWithStatusAsync(0));
    }

    [Fact]
    public async Task Queueing_marks_the_findings_with_the_job_and_a_repeat_queues_nothing()
    {
        var findingIds = new[]
        {
            await _fixture.InsertGlobalFindingAsync("finding-c"),
            await _fixture.InsertGlobalFindingAsync("finding-d")
        };
        var repository = _fixture.CreatePortalRepository();

        var first = await repository.QueueMaintenanceCleanupAsync(findingIds, "tester", CancellationToken.None);
        var second = await repository.QueueMaintenanceCleanupAsync(findingIds, "tester", CancellationToken.None);

        Assert.Equal(1, first.QueuedJobCount);
        Assert.Equal(2, first.QueuedFindingCount);
        Assert.Equal(0, second.QueuedJobCount);
        Assert.Equal(0, second.QueuedFindingCount);
        Assert.Equal(1, await _fixture.CountCleanupJobsAsync());
        Assert.Equal(2, await _fixture.CountFindingsQueuedOnTheCleanupJobAsync());
    }
}

public sealed class MaintenanceCleanupQueueTestFixture : IAsyncLifetime
{
    public const string FailQueuedMarkingTriggerSql = @"
CREATE TRIGGER omp.TR_Test_FailQueuedMarking ON omp.MaintenanceFindings AFTER UPDATE
AS
BEGIN
    IF EXISTS (SELECT 1 FROM inserted WHERE Status = 2)
        THROW 51099, 'Simulated failure while marking findings as queued.', 1;
END;";

    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("MaintenanceCleanupQueue");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            master.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");
        await CoreSetupScript.ApplyAsync(ConnectionString);
    }

    public OmpAdminRepository CreatePortalRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = ConnectionString
            })
            .Build();
        return new OmpAdminRepository(new PortalSqlConnectionFactory(configuration));
    }

    public Task ResetAsync() => ExecuteAsync(@"
IF OBJECT_ID(N'omp.TR_Test_FailQueuedMarking', N'TR') IS NOT NULL
    DROP TRIGGER omp.TR_Test_FailQueuedMarking;
DELETE FROM omp.MaintenanceFindings;
DELETE FROM omp.HostAgentJobs;");

    public async Task ExecuteAsync(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> InsertGlobalFindingAsync(string findingKey)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
INSERT INTO omp.MaintenanceFindings(FindingKey, Scope, Category, TargetKind, TargetIdentifier, Title, ActionJson)
OUTPUT inserted.MaintenanceFindingId
VALUES(@FindingKey, N'Global', N'Test', N'DatabaseRow', @FindingKey, N'Test finding', N'{}');", conn);
        cmd.Parameters.Add("@FindingKey", System.Data.SqlDbType.NVarChar, 450).Value = findingKey;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public Task<int> CountCleanupJobsAsync()
        => ScalarIntAsync("SELECT COUNT(1) FROM omp.HostAgentJobs WHERE JobType = N'MaintenanceCleanup';");

    public Task<int> CountFindingsWithStatusAsync(byte status)
        => ScalarIntAsync($"SELECT COUNT(1) FROM omp.MaintenanceFindings WHERE Status = {status};");

    public Task<int> CountFindingsQueuedOnTheCleanupJobAsync()
        => ScalarIntAsync(@"
SELECT COUNT(1)
FROM omp.MaintenanceFindings finding
INNER JOIN omp.HostAgentJobs job ON job.HostAgentJobId = finding.CleanupHostAgentJobId
WHERE finding.Status = 2
  AND job.JobType = N'MaintenanceCleanup'
  AND finding.RequestedBy = N'tester';");

    private async Task<int> ScalarIntAsync(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];", conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            // A failed drop must not fail the run, but it must not be silent either:
            // the shared log is what CI turns into a warning about the leaked database.
            OmpTestCleanupLog.RecordFailure(
                nameof(OmpAdminRepositoryMaintenanceCleanupQueueTests),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
    }
}
