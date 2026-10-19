using System.Data;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Separate database for the concurrency tests, so they never share (and drop)
/// the database of <see cref="OmpTemplateMaterializationTests"/>.
/// </summary>
public sealed class TemplateMaterializationConcurrencyTestFixture : TemplateMaterializationTestFixture
{
    public TemplateMaterializationConcurrencyTestFixture()
        : base("TemplateMaterializationConcurrency")
    {
    }
}

/// <summary>
/// Proves that overlapping omp.MaterializeInstanceTemplate runs -- every
/// HostAgent calls it every cycle -- never both decide that a natural key is
/// missing and both insert it. An unlocked MERGE reads the target, finds no
/// row, and inserts; a second run that read the target in between inserts the
/// same key and fails with a unique-key violation (or a deadlock).
///
/// The runs are released together by a gate: a blocking session holds an
/// uncommitted update of the instance row that every run reads in its MERGE
/// source, and the test waits until every run is blocked before it releases
/// the gate. Many template keys per run make the read-then-insert windows of
/// the released runs overlap.
/// </summary>
public sealed class OmpTemplateMaterializationConcurrencyTests : IClassFixture<TemplateMaterializationConcurrencyTestFixture>
{
    private const int ConcurrentRuns = 8;
    private const int TemplateKeys = 40;
    private const int Rounds = 3;
    private static readonly TimeSpan BlockedWaitTimeout = TimeSpan.FromSeconds(20);

    private readonly TemplateMaterializationConcurrencyTestFixture _fixture;

    public OmpTemplateMaterializationConcurrencyTests(TemplateMaterializationConcurrencyTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OverlappingMaterializations_InsertEachNaturalKeyOnce_WithoutErrors()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var instanceKey = $"tmpl-concurrency-instance-{round}";
            await SeedTemplateInstanceAsync($"tmpl-concurrency-{round}", instanceKey);

            await using var gate = new SqlConnection(_fixture.ConnectionString);
            await gate.OpenAsync();
            await using var gateTransaction = (SqlTransaction)await gate.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await using (var hold = new SqlCommand(
                "UPDATE omp.Instances SET DisplayName = DisplayName + N'*' WHERE InstanceKey = @instanceKey;",
                gate,
                gateTransaction))
            {
                hold.Parameters.AddWithValue("@instanceKey", instanceKey);
                Assert.Equal(1, await hold.ExecuteNonQueryAsync());
            }

            var runs = Enumerable.Range(0, ConcurrentRuns)
                .Select(_ => Task.Run(() => _fixture.MaterializeAsync(instanceKey: instanceKey)))
                .ToArray();

            await WaitUntilAllRunsAreBlockedAsync(runs);
            await gateTransaction.RollbackAsync();

            var results = await Task.WhenAll(runs);

            Assert.Equal(TemplateKeys, results.Sum(r => r.ModuleInstanceChanges));
            Assert.Equal(TemplateKeys, results.Sum(r => r.AppInstanceChanges));
            Assert.Equal(TemplateKeys, await CountAsync(
                "SELECT COUNT(1) FROM omp.ModuleInstances mi INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId WHERE i.InstanceKey = @instanceKey;",
                instanceKey));
            Assert.Equal(TemplateKeys, await CountAsync(
                "SELECT COUNT(1) FROM omp.AppInstances ai INNER JOIN omp.ModuleInstances mi ON mi.ModuleInstanceId = ai.ModuleInstanceId INNER JOIN omp.Instances i ON i.InstanceId = mi.InstanceId WHERE i.InstanceKey = @instanceKey;",
                instanceKey));
        }
    }

    private async Task SeedTemplateInstanceAsync(string templateKey, string instanceKey)
    {
        var templateId = await _fixture.InsertTemplateAsync(templateKey);
        var moduleId = await _fixture.InsertModuleAsync(templateKey + "-module");
        var appId = await _fixture.InsertAppAsync(moduleId, templateKey + "-app");

        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
DECLARE @n int = 0;
DECLARE @tmiId int;
DECLARE @key nvarchar(100);
WHILE @n < @keys
BEGIN
    SET @key = CONCAT(N'mod-', FORMAT(@n, N'000'));
    INSERT INTO omp.InstanceTemplateModuleInstances (InstanceTemplateId, ModuleId, ModuleInstanceKey, DisplayName, IsEnabled)
    VALUES (@templateId, @moduleId, @key, @key, 1);
    SET @tmiId = CAST(SCOPE_IDENTITY() AS int);
    INSERT INTO omp.InstanceTemplateAppInstances (InstanceTemplateModuleInstanceId, AppId, AppInstanceKey, DisplayName, IsEnabled)
    VALUES (@tmiId, @appId, N'app', N'app', 1);
    SET @n += 1;
END;",
            conn);
        cmd.Parameters.AddWithValue("@keys", TemplateKeys);
        cmd.Parameters.AddWithValue("@templateId", templateId);
        cmd.Parameters.AddWithValue("@moduleId", moduleId);
        cmd.Parameters.AddWithValue("@appId", appId);
        await cmd.ExecuteNonQueryAsync();

        await _fixture.InsertInstanceAsync(instanceKey, templateId);
    }

    private async Task WaitUntilAllRunsAreBlockedAsync(Task[] runs)
    {
        await using var monitor = new SqlConnection(_fixture.ConnectionString);
        await monitor.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM sys.dm_exec_requests WHERE database_id = DB_ID() AND blocking_session_id <> 0;",
            monitor);

        var deadline = DateTime.UtcNow + BlockedWaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var finished = runs.FirstOrDefault(r => r.IsCompleted);
            if (finished is not null)
            {
                // Surfaces the run's exception, if any, with its own message.
                await finished;
                Assert.Fail("A materialization run finished while the gate was still closed.");
            }

            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) >= runs.Length)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Not all {runs.Length} materialization runs were blocked behind the gate within {BlockedWaitTimeout}.");
    }

    private async Task<int> CountAsync(string sql, string instanceKey)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@instanceKey", instanceKey);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
