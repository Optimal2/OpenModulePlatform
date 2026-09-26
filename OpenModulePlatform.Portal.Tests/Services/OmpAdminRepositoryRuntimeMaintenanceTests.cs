namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The Portal delete paths run the artifact-removed, app-instance-blocking-count and
/// app-instance-removed runtime maintenance steps that the applied example web app definition
/// declares, and nothing module-specific when no applied definition declares a step.
/// </summary>
/// <remarks>
/// The ids are deliberately absent from omp.Artifacts and omp.AppInstances: the steps run before
/// the platform rows are deleted and are keyed only by the event parameter, so a missing platform
/// row isolates exactly what the module step did.
/// </remarks>
public sealed class OmpAdminRepositoryRuntimeMaintenanceTests
    : IClassFixture<ModuleRuntimeMaintenanceTestFixture>, IAsyncLifetime
{
    private readonly ModuleRuntimeMaintenanceTestFixture _fixture;

    public OmpAdminRepositoryRuntimeMaintenanceTests(ModuleRuntimeMaintenanceTestFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DeleteArtifactAsync_RunsDeclaredArtifactRemovedStep()
    {
        await _fixture.InsertExampleDefinitionAsync(isApplied: true);
        await _fixture.InsertBindingAsync(Guid.NewGuid(), artifactId: 910001);
        await _fixture.InsertBindingAsync(Guid.NewGuid(), artifactId: 910002);

        await _fixture.CreatePortalRepository().DeleteArtifactAsync(910001, CancellationToken.None);

        Assert.Equal(0, await _fixture.CountBindingsPinnedToAsync(910001));
        Assert.Equal(1, await _fixture.CountBindingsPinnedToAsync(910002));
    }

    [Fact]
    public async Task DeleteAppInstanceAsync_WhenModuleReportsBlockingRows_RefusesAndNamesThem()
    {
        await _fixture.InsertExampleDefinitionAsync(isApplied: true);
        var appInstanceId = Guid.NewGuid();
        await _fixture.InsertBindingAsync(appInstanceId, artifactId: null);
        await _fixture.InsertBindingAsync(appInstanceId, artifactId: null);
        await _fixture.InsertLeaseAsync(appInstanceId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fixture.CreatePortalRepository().DeleteAppInstanceAsync(appInstanceId, CancellationToken.None));

        Assert.Contains("2 example runtime binding(s)", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.CountLeasesAsync(appInstanceId));
    }

    [Fact]
    public async Task DeleteAppInstanceAsync_WhenNothingBlocks_RunsDeclaredAppInstanceRemovedStep()
    {
        await _fixture.InsertExampleDefinitionAsync(isApplied: true);
        var removedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await _fixture.InsertLeaseAsync(removedId);
        await _fixture.InsertLeaseAsync(otherId);

        await _fixture.CreatePortalRepository().DeleteAppInstanceAsync(removedId, CancellationToken.None);

        Assert.Equal(0, await _fixture.CountLeasesAsync(removedId));
        Assert.Equal(1, await _fixture.CountLeasesAsync(otherId));
    }

    [Fact]
    public async Task DeletePaths_WhenNoAppliedDefinitionDeclaresSteps_LeaveModuleRowsUntouched()
    {
        // Imported but not applied: nothing module-specific runs, and nothing blocks.
        await _fixture.InsertExampleDefinitionAsync(isApplied: false);
        var appInstanceId = Guid.NewGuid();
        await _fixture.InsertBindingAsync(appInstanceId, artifactId: 910003);
        await _fixture.InsertLeaseAsync(appInstanceId);
        var repository = _fixture.CreatePortalRepository();

        await repository.DeleteArtifactAsync(910003, CancellationToken.None);
        await repository.DeleteAppInstanceAsync(appInstanceId, CancellationToken.None);

        Assert.Equal(1, await _fixture.CountBindingsPinnedToAsync(910003));
        Assert.Equal(1, await _fixture.CountLeasesAsync(appInstanceId));
    }
}
