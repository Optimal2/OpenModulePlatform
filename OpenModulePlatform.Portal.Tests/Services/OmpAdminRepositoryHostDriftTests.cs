using OpenModulePlatform.Portal.Pages.Admin;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Database-backed tests proving that a failed desired-artifact provisioning
/// (omp.HostArtifactStates) surfaces in the host drift view even when the
/// leftover deployment state still reads "Succeeded" with no error.
/// </summary>
public sealed class OmpAdminRepositoryHostDriftTests : IClassFixture<HostDriftTestFixture>
{
    private readonly HostDriftTestFixture _fixture;

    public OmpAdminRepositoryHostDriftTests(HostDriftTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetHostDriftDetailsAsync_SurfacesFailedDesiredArtifactProvisioning()
    {
        const string provisioningError = "Artifact source path does not exist";
        await _fixture.SeedHostWithDesiredArtifactProvisioningStateAsync(3, provisioningError);
        var repo = _fixture.CreatePortalRepository();

        var details = await repo.GetHostDriftDetailsAsync(CancellationToken.None);

        var row = Assert.Single(details);
        Assert.Equal(_fixture.HostId, row.HostId);
        Assert.Equal("Failed", row.DriftReason);
        Assert.Equal((byte)2, row.DeploymentState);
        Assert.Null(row.LastError);
        Assert.Equal((byte)3, row.DesiredProvisioningState);
        Assert.Equal(provisioningError, row.DesiredProvisioningError);
    }

    [Fact]
    public async Task GetHostDriftSummariesAsync_CountsFailedDesiredArtifactProvisioningAsFailedNotInSync()
    {
        await _fixture.SeedHostWithDesiredArtifactProvisioningStateAsync(3, "Artifact source path does not exist");
        var repo = _fixture.CreatePortalRepository();

        var summaries = await repo.GetHostDriftSummariesAsync(CancellationToken.None);

        var summary = Assert.Single(summaries);
        Assert.Equal(_fixture.HostId, summary.HostId);
        Assert.Equal(1, summary.DesiredAppCount);
        Assert.Equal(0, summary.InSyncAppCount);
        Assert.Equal(1, summary.FailedAppCount);
        Assert.Equal(0, summary.WarningAppCount);
        Assert.Equal("Failed", HostDeploymentsModel.FormatHostDriftStatus(summary));
    }

    [Fact]
    public async Task GetHostDriftDetailsAsync_SurfacesDesiredArtifactHashMismatchAsWarning()
    {
        await _fixture.SeedHostWithDesiredArtifactProvisioningStateAsync(4, "Hash mismatch");
        var repo = _fixture.CreatePortalRepository();

        var details = await repo.GetHostDriftDetailsAsync(CancellationToken.None);
        var summaries = await repo.GetHostDriftSummariesAsync(CancellationToken.None);

        var row = Assert.Single(details);
        Assert.Equal("Warning", row.DriftReason);
        Assert.Equal((byte)4, row.DesiredProvisioningState);

        var summary = Assert.Single(summaries);
        Assert.Equal(0, summary.InSyncAppCount);
        Assert.Equal(0, summary.FailedAppCount);
        Assert.Equal(1, summary.WarningAppCount);
    }

    [Fact]
    public async Task GetHostDriftDetailsAsync_KeepsSucceededProvisioningInSync()
    {
        await _fixture.SeedHostWithDesiredArtifactProvisioningStateAsync(2, null);
        var repo = _fixture.CreatePortalRepository();

        var details = await repo.GetHostDriftDetailsAsync(CancellationToken.None);
        var summaries = await repo.GetHostDriftSummariesAsync(CancellationToken.None);

        Assert.Empty(details);
        var summary = Assert.Single(summaries);
        Assert.Equal(1, summary.InSyncAppCount);
        Assert.Equal(0, summary.FailedAppCount);
        Assert.Equal(0, summary.WarningAppCount);
    }
}
