using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class OmpHostArtifactRepositoryTierCTests : IDisposable
{
    private readonly OmpHostArtifactRepositoryTestDatabase _database;
    private readonly OmpHostArtifactRepository _repository;

    public OmpHostArtifactRepositoryTierCTests()
    {
        _database = new OmpHostArtifactRepositoryTestDatabase();
        _repository = new OmpHostArtifactRepository(_database.CreateFactory());
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task MaterializeTemplatesForHostAsync_WhenProcedureExistsAndHostIsEnabled_ReturnsNonZeroCounts()
    {
        _database.CreateMaterializeProcedure();
        _database.InsertHost("enabled-host");

        var result = await _repository.MaterializeTemplatesForHostAsync(
            "enabled-host",
            hostTemplateId: null,
            CancellationToken.None);

        Assert.True(result.ModuleInstanceChanges > 0, "Expected non-zero module instance changes.");
        Assert.True(result.AppInstanceChanges > 0, "Expected non-zero app instance changes.");
    }

    [Fact]
    public async Task MaterializeTemplatesForHostAsync_WhenProcedureDoesNotExist_ReturnsZeroCounts()
    {
        _database.InsertHost("no-proc-host");

        var result = await _repository.MaterializeTemplatesForHostAsync(
            "no-proc-host",
            hostTemplateId: null,
            CancellationToken.None);

        Assert.Equal(0, result.ModuleInstanceChanges);
        Assert.Equal(0, result.AppInstanceChanges);
    }

    [Fact]
    public async Task MaterializeTemplatesForHostAsync_WhenHostIsMissing_ReturnsZeroCounts()
    {
        _database.CreateMaterializeProcedure();

        var result = await _repository.MaterializeTemplatesForHostAsync(
            "missing-host",
            hostTemplateId: null,
            CancellationToken.None);

        Assert.Equal(0, result.ModuleInstanceChanges);
        Assert.Equal(0, result.AppInstanceChanges);
    }

    [Fact]
    public async Task MaterializeTemplatesForHostAsync_WhenHostIsDisabled_ReturnsZeroCounts()
    {
        _database.CreateMaterializeProcedure();
        _database.InsertHost("disabled-host", isEnabled: false);

        var result = await _repository.MaterializeTemplatesForHostAsync(
            "disabled-host",
            hostTemplateId: null,
            CancellationToken.None);

        Assert.Equal(0, result.ModuleInstanceChanges);
        Assert.Equal(0, result.AppInstanceChanges);
    }

    [Fact]
    public async Task MaterializeTemplatesForHostAsync_WhenSqlThrows_PropagatesException()
    {
        _database.CreateMaterializeProcedureThatThrows();
        _database.InsertHost("throwing-host");

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            _repository.MaterializeTemplatesForHostAsync(
                "throwing-host",
                hostTemplateId: null,
                CancellationToken.None));

        Assert.Contains("Simulated materialization failure", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWebAppDeploymentRecoveryCandidatesAsync_WhenCandidatesExist_ReturnsCandidates()
    {
        var hostId = _database.InsertHost("web-host");
        _database.InsertRecoveryCandidate(
            hostId,
            "web-app-1",
            @"C:\inetpub\web-app-1",
            "OMP.web-app-1",
            packageType: "web-app");

        var candidates = await _repository.GetWebAppDeploymentRecoveryCandidatesAsync(
            "web-host",
            CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal("web-app-1", candidate.AppInstanceKey);
        Assert.Equal(@"C:\inetpub\web-app-1", candidate.TargetPath);
        Assert.Equal("OMP.web-app-1", candidate.RuntimeName);
    }

    [Fact]
    public async Task GetServiceAppDeploymentRecoveryCandidatesAsync_WhenCandidatesExist_ReturnsCandidates()
    {
        var hostId = _database.InsertHost("service-host");
        _database.InsertRecoveryCandidate(
            hostId,
            "service-app-1",
            @"C:\services\service-app-1",
            "OMP.service-app-1",
            packageType: "service-app");

        var candidates = await _repository.GetServiceAppDeploymentRecoveryCandidatesAsync(
            "service-host",
            CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal("service-app-1", candidate.AppInstanceKey);
        Assert.Equal(@"C:\services\service-app-1", candidate.TargetPath);
        Assert.Equal("OMP.service-app-1", candidate.RuntimeName);
    }

    [Fact]
    public async Task GetDeploymentRuntimeRecoveryCandidatesAsync_WhenHostIsMissing_ReturnsEmptyList()
    {
        var candidates = await _repository.GetWebAppDeploymentRecoveryCandidatesAsync(
            "missing-host",
            CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task GetDeploymentRuntimeRecoveryCandidatesAsync_WhenHostIsDisabled_ReturnsEmptyList()
    {
        _database.InsertHost("disabled-host", isEnabled: false);

        var candidates = await _repository.GetWebAppDeploymentRecoveryCandidatesAsync(
            "disabled-host",
            CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task GetDeploymentRuntimeRecoveryCandidatesAsync_WhenNoCandidatesExist_ReturnsEmptyList()
    {
        _database.InsertHost("empty-host");

        var candidates = await _repository.GetWebAppDeploymentRecoveryCandidatesAsync(
            "empty-host",
            CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task UpsertMaintenanceFindingsAsync_WhenCalledTwiceWithSameKey_DoesNotDuplicate()
    {
        var hostId = _database.InsertHost("maintenance-host");
        _database.CreateMaintenanceFindingsTable();

        var findings = new List<MaintenanceFindingUpsert>
        {
            new()
            {
                FindingKey = "test:duplicate-key",
                Scope = MaintenanceScanScopes.Host,
                HostId = hostId,
                Category = "Test",
                TargetKind = MaintenanceTargetKinds.Directory,
                TargetIdentifier = @"C:\test",
                Title = "Test finding"
            }
        };

        await _repository.UpsertMaintenanceFindingsAsync(findings, 1, CancellationToken.None);
        await _repository.UpsertMaintenanceFindingsAsync(findings, 2, CancellationToken.None);

        Assert.Equal(1, _database.CountFindings());
    }

    [Fact]
    public async Task GetDeploymentRuntimeRecoveryCandidatesAsync_WhenSqlThrows_PropagatesException()
    {
        var hostId = _database.InsertHost("bad-host");
        _database.InsertRecoveryCandidate(
            hostId,
            "bad-app",
            @"C:\bad",
            "OMP.bad",
            packageType: "web-app");

        // Corrupt the artifact row so COALESCE resolves to a value that cannot match,
        // then delete the artifact to cause a foreign-key-style logical failure.
        // A simpler deterministic SQL failure is to drop the Artifacts table mid-test,
        // but that breaks cleanup. Instead, use an invalid package-type discriminator
        // is not possible because the repository passes it. We force a failure by
        // dropping the AppInstances table that the query joins.
        using (var conn = new SqlConnection(_database.ConnectionString))
        {
            conn.Open();
            using var cmd = new SqlCommand("DROP TABLE omp.AppInstances;", conn);
            cmd.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<SqlException>(() =>
            _repository.GetWebAppDeploymentRecoveryCandidatesAsync(
                "bad-host",
                CancellationToken.None));
    }
}
