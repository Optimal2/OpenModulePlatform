using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class OmpHostArtifactRepositoryHostDeploymentLeaseTests : IDisposable
{
    private const string TestServiceName = "OMP.HostAgent.Test";
    private const int LeaseSeconds = 30;
    private const int MaxAttempts = 3;

    private readonly string _connectionString;
    private readonly OmpHostArtifactRepository _repository;
    private readonly List<long> _createdDeploymentIds = [];

    public OmpHostArtifactRepositoryHostDeploymentLeaseTests()
    {
        _connectionString = GetConnectionString();
        _repository = new OmpHostArtifactRepository(CreateFactory(_connectionString));
    }

    public void Dispose()
    {
        CleanupCreatedDeployments();
    }

    [Fact]
    public async Task TryClaimNextHostDeploymentAsync_ClaimsPendingRowAndSetsLease()
    {
        var hostKey = await GetDefaultHostKeyAsync();
        var hostTemplateId = await GetDefaultHostTemplateIdAsync();
        var deploymentId = await InsertPendingDeploymentAsync(hostKey, hostTemplateId);

        var deployment = await _repository.TryClaimNextHostDeploymentAsync(
            hostKey,
            TestServiceName,
            LeaseSeconds,
            MaxAttempts,
            CancellationToken.None);

        Assert.NotNull(deployment);
        Assert.Equal(deploymentId, deployment.HostDeploymentId);
        Assert.Equal(hostTemplateId, deployment.HostTemplateId);
        Assert.NotEqual(Guid.Empty, deployment.LeaseToken);

        var row = await GetDeploymentRowAsync(deploymentId);
        Assert.Equal(HostDeploymentStatuses.Running, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(TestServiceName, row.ClaimedByServiceName);
        Assert.NotNull(row.LeaseUntilUtc);
        Assert.True(row.LeaseUntilUtc > DateTime.UtcNow.AddSeconds(LeaseSeconds - 5));
        Assert.Equal(deployment.LeaseToken, row.LeaseToken);
    }

    [Fact]
    public async Task TryClaimNextHostDeploymentAsync_ReclaimsExpiredRunningRow()
    {
        var hostKey = await GetDefaultHostKeyAsync();
        var hostTemplateId = await GetDefaultHostTemplateIdAsync();
        var deploymentId = await InsertRunningDeploymentAsync(
            hostKey,
            hostTemplateId,
            attemptCount: 1,
            leaseUntilUtc: DateTime.UtcNow.AddMinutes(-1));

        var deployment = await _repository.TryClaimNextHostDeploymentAsync(
            hostKey,
            TestServiceName,
            LeaseSeconds,
            MaxAttempts,
            CancellationToken.None);

        Assert.NotNull(deployment);
        Assert.Equal(deploymentId, deployment.HostDeploymentId);

        var row = await GetDeploymentRowAsync(deploymentId);
        Assert.Equal(HostDeploymentStatuses.Running, row.Status);
        Assert.Equal(2, row.AttemptCount);
        Assert.Equal(TestServiceName, row.ClaimedByServiceName);
        Assert.NotNull(row.LeaseUntilUtc);
        Assert.True(row.LeaseUntilUtc > DateTime.UtcNow);
        Assert.Equal(deployment.LeaseToken, row.LeaseToken);
    }

    [Fact]
    public async Task TryClaimNextHostDeploymentAsync_FailsExpiredRunningRow_WhenMaxAttemptsReached()
    {
        var hostKey = await GetDefaultHostKeyAsync();
        var hostTemplateId = await GetDefaultHostTemplateIdAsync();
        var deploymentId = await InsertRunningDeploymentAsync(
            hostKey,
            hostTemplateId,
            attemptCount: 3,
            leaseUntilUtc: DateTime.UtcNow.AddMinutes(-1));

        var firstDeployment = await _repository.TryClaimNextHostDeploymentAsync(
            hostKey,
            TestServiceName,
            LeaseSeconds,
            MaxAttempts,
            CancellationToken.None);

        Assert.Null(firstDeployment);

        var row = await GetDeploymentRowAsync(deploymentId);
        Assert.Equal(HostDeploymentStatuses.Failed, row.Status);
        Assert.NotNull(row.CompletedUtc);
        Assert.Null(row.LeaseToken);
        Assert.Null(row.LeaseUntilUtc);
        Assert.Contains("maximum number of attempts", row.OutcomeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryClaimNextHostDeploymentAsync_UsesConfiguredMaxAttempts_WhenReclaimingExpiredRunningRow()
    {
        var hostKey = await GetDefaultHostKeyAsync();
        var hostTemplateId = await GetDefaultHostTemplateIdAsync();
        var configuredMaxAttempts = 5;
        var deploymentId = await InsertRunningDeploymentAsync(
            hostKey,
            hostTemplateId,
            attemptCount: MaxAttempts,
            leaseUntilUtc: DateTime.UtcNow.AddMinutes(-1));

        var deployment = await _repository.TryClaimNextHostDeploymentAsync(
            hostKey,
            TestServiceName,
            LeaseSeconds,
            configuredMaxAttempts,
            CancellationToken.None);

        Assert.NotNull(deployment);
        Assert.Equal(deploymentId, deployment.HostDeploymentId);

        var row = await GetDeploymentRowAsync(deploymentId);
        Assert.Equal(HostDeploymentStatuses.Running, row.Status);
        Assert.Equal(MaxAttempts + 1, row.AttemptCount);
        Assert.Equal(configuredMaxAttempts, row.MaxAttempts);
        Assert.Equal(deployment.LeaseToken, row.LeaseToken);
    }

    [Fact]
    public async Task RenewHostDeploymentLeaseAsync_ExtendsLease()
    {
        var hostKey = await GetDefaultHostKeyAsync();
        var hostTemplateId = await GetDefaultHostTemplateIdAsync();
        var deploymentId = await InsertPendingDeploymentAsync(hostKey, hostTemplateId);

        var deployment = await _repository.TryClaimNextHostDeploymentAsync(
            hostKey,
            TestServiceName,
            LeaseSeconds,
            MaxAttempts,
            CancellationToken.None);

        Assert.NotNull(deployment);
        var originalLeaseUntilUtc = (await GetDeploymentRowAsync(deploymentId)).LeaseUntilUtc;
        Assert.NotNull(originalLeaseUntilUtc);

        var renewed = await _repository.RenewHostDeploymentLeaseAsync(
            deployment.HostDeploymentId,
            deployment.LeaseToken,
            LeaseSeconds * 2,
            CancellationToken.None);

        Assert.True(renewed);

        var row = await GetDeploymentRowAsync(deploymentId);
        Assert.NotNull(row.LeaseUntilUtc);
        Assert.True(row.LeaseUntilUtc > originalLeaseUntilUtc.Value.AddSeconds(LeaseSeconds - 5));
    }

    [Fact]
    public async Task CompleteHostDeploymentAsync_WithLeaseToken_SetsSucceededAndClearsLease()
    {
        var hostKey = await GetDefaultHostKeyAsync();
        var hostTemplateId = await GetDefaultHostTemplateIdAsync();
        var deploymentId = await InsertPendingDeploymentAsync(hostKey, hostTemplateId);

        var deployment = await _repository.TryClaimNextHostDeploymentAsync(
            hostKey,
            TestServiceName,
            LeaseSeconds,
            MaxAttempts,
            CancellationToken.None);

        Assert.NotNull(deployment);

        await _repository.CompleteHostDeploymentAsync(
            deployment.HostDeploymentId,
            deployment.LeaseToken,
            succeeded: true,
            outcomeMessage: "Test completed.",
            CancellationToken.None);

        var row = await GetDeploymentRowAsync(deploymentId);
        Assert.Equal(HostDeploymentStatuses.Succeeded, row.Status);
        Assert.NotNull(row.CompletedUtc);
        Assert.Null(row.LeaseToken);
        Assert.Null(row.LeaseUntilUtc);
        Assert.Equal("Test completed.", row.OutcomeMessage);
    }

    private async Task<string> GetDefaultHostKeyAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP (1) HostKey FROM omp.Hosts WHERE IsEnabled = 1 ORDER BY HostKey;",
            conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (string)result;
    }

    private async Task<int> GetDefaultHostTemplateIdAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP (1) HostTemplateId FROM omp.HostTemplates WHERE IsEnabled = 1 ORDER BY HostTemplateId;",
            conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (int)result;
    }

    private async Task<long> InsertPendingDeploymentAsync(string hostKey, int? hostTemplateId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
DECLARE @hostId uniqueidentifier;
SELECT @hostId = HostId FROM omp.Hosts WHERE HostKey = @hostKey;

INSERT INTO omp.HostDeployments(HostId, HostTemplateId, RequestedBy, RequestedUtc, Status)
VALUES(@hostId, @hostTemplateId, N'Test', SYSUTCDATETIME(), @pendingStatus);

SELECT SCOPE_IDENTITY();",
            conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@hostTemplateId", hostTemplateId.HasValue ? hostTemplateId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@pendingStatus", HostDeploymentStatuses.Pending);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        _createdDeploymentIds.Add(id);
        return id;
    }

    private async Task<long> InsertRunningDeploymentAsync(
        string hostKey,
        int? hostTemplateId,
        int attemptCount,
        DateTime leaseUntilUtc)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
DECLARE @hostId uniqueidentifier;
SELECT @hostId = HostId FROM omp.Hosts WHERE HostKey = @hostKey;

INSERT INTO omp.HostDeployments(
    HostId, HostTemplateId, RequestedBy, RequestedUtc,
    Status, StartedUtc, AttemptCount, MaxAttempts, LeaseUntilUtc, LeaseToken)
VALUES(
    @hostId, @hostTemplateId, N'Test', SYSUTCDATETIME(),
    @runningStatus, SYSUTCDATETIME(), @attemptCount, 3, @leaseUntilUtc, NEWID());

SELECT SCOPE_IDENTITY();",
            conn);
        cmd.Parameters.AddWithValue("@hostKey", hostKey);
        cmd.Parameters.AddWithValue("@hostTemplateId", hostTemplateId.HasValue ? hostTemplateId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@runningStatus", HostDeploymentStatuses.Running);
        cmd.Parameters.AddWithValue("@attemptCount", attemptCount);
        cmd.Parameters.AddWithValue("@leaseUntilUtc", leaseUntilUtc);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        _createdDeploymentIds.Add(id);
        return id;
    }

    private async Task<DeploymentRow> GetDeploymentRowAsync(long hostDeploymentId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            @"
SELECT Status, AttemptCount, MaxAttempts, ClaimedByServiceName, ClaimedUtc,
       LeaseUntilUtc, LeaseToken, CompletedUtc, OutcomeMessage
FROM omp.HostDeployments
WHERE HostDeploymentId = @hostDeploymentId;",
            conn);
        cmd.Parameters.AddWithValue("@hostDeploymentId", hostDeploymentId);
        await using var rdr = await cmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync());
        return new DeploymentRow(
            rdr.GetByte(0),
            rdr.GetInt32(1),
            rdr.GetInt32(2),
            rdr.IsDBNull(3) ? null : rdr.GetString(3),
            rdr.IsDBNull(4) ? null : rdr.GetDateTime(4),
            rdr.IsDBNull(5) ? null : rdr.GetDateTime(5),
            rdr.IsDBNull(6) ? null : rdr.GetGuid(6),
            rdr.IsDBNull(7) ? null : rdr.GetDateTime(7),
            rdr.IsDBNull(8) ? null : rdr.GetString(8));
    }

    private void CleanupCreatedDeployments()
    {
        if (_createdDeploymentIds.Count == 0)
        {
            return;
        }

        try
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();
            using var cmd = new SqlCommand(
                "DELETE FROM omp.HostDeployments WHERE HostDeploymentId IN (SELECT value FROM STRING_SPLIT(@ids, ','));",
                conn);
            cmd.Parameters.AddWithValue("@ids", string.Join(",", _createdDeploymentIds));
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort cleanup; do not fail the test because cleanup failed.
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("OMP_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        return "Server=(local);Database=OpenModulePlatform;Integrated Security=true;TrustServerCertificate=true";
    }

    private static SqlConnectionFactory CreateFactory(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = connectionString
            })
            .Build();
        return new SqlConnectionFactory(configuration);
    }

    private sealed record DeploymentRow(
        byte Status,
        int AttemptCount,
        int MaxAttempts,
        string? ClaimedByServiceName,
        DateTime? ClaimedUtc,
        DateTime? LeaseUntilUtc,
        Guid? LeaseToken,
        DateTime? CompletedUtc,
        string? OutcomeMessage);
}
