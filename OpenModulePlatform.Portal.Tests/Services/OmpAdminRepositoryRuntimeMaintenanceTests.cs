using Microsoft.Data.SqlClient;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The Portal delete paths run the host-removed, artifact-removed, app-instance-blocking-count and
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
    public async Task DeleteHostAsync_WhenModuleTableReferencesHost_RunsDeclaredHostRemovedStepAndDeletes()
    {
        // F4: a module table with a foreign key to omp.Hosts. The manual delete runs the module's
        // host-removed step, as the HostAgent orphan-host cleanup does, instead of failing on the FK.
        await _fixture.InsertExampleDefinitionAsync(isApplied: true);
        await _fixture.AddLeaseHostForeignKeyAsync();
        var removedHostId = await _fixture.InsertHostAsync();
        var otherHostId = await _fixture.InsertHostAsync();
        await _fixture.InsertHostLeaseAsync(removedHostId);
        await _fixture.InsertHostLeaseAsync(otherHostId);

        Assert.True(await _fixture.CreatePortalRepository().DeleteHostAsync(removedHostId, CancellationToken.None));

        Assert.Equal(0, await _fixture.CountHostsAsync(removedHostId));
        Assert.Equal(0, await _fixture.CountHostLeasesAsync(removedHostId));
        Assert.Equal(1, await _fixture.CountHostLeasesAsync(otherHostId));
    }

    [Fact]
    public async Task DeleteHostAsync_WhenModuleDeclaresNoHostRemovedStep_NamesTheModuleAndDeletesNothing()
    {
        // F3: a module definition applied before it declared runtimeMaintenance. The FK conflict
        // from the module's table becomes guidance naming the module and the event; fail-closed.
        await _fixture.InsertExampleDefinitionAsync(
            isApplied: true,
            rewriteJson: static json =>
            {
                var definition = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                Assert.True(definition.Remove("runtimeMaintenance"));
                return definition.ToJsonString();
            });
        await _fixture.AddLeaseHostForeignKeyAsync();
        var hostId = await _fixture.InsertHostAsync();
        await _fixture.InsertHostLeaseAsync(hostId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fixture.CreatePortalRepository().DeleteHostAsync(hostId, CancellationToken.None));

        Assert.StartsWith("OMP-MODULE-RUNTIME-MAINTENANCE: Module 'example_webapp' (schema omp_example_webapp)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("omp_example_webapp.RuntimeLeases", ex.Message, StringComparison.Ordinal);
        Assert.Contains("event 'host-removed'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(547, Assert.IsType<SqlException>(ex.InnerException).Number);
        Assert.Equal(1, await _fixture.CountHostsAsync(hostId));
        Assert.Equal(1, await _fixture.CountHostLeasesAsync(hostId));
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

    [Fact]
    public async Task DeletePaths_WhenAppliedDefinitionDeclaresNoRuntimeMaintenance_RunNothingAndBlockNothing()
    {
        // A module without the section: no module step runs and none blocks the delete.
        await _fixture.InsertExampleDefinitionAsync(
            isApplied: true,
            rewriteJson: static json =>
            {
                var definition = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                Assert.True(definition.Remove("runtimeMaintenance"));
                return definition.ToJsonString();
            });
        var appInstanceId = Guid.NewGuid();
        await _fixture.InsertBindingAsync(appInstanceId, artifactId: 910009);
        await _fixture.InsertLeaseAsync(appInstanceId);
        var repository = _fixture.CreatePortalRepository();

        await repository.DeleteArtifactAsync(910009, CancellationToken.None);
        await repository.DeleteAppInstanceAsync(appInstanceId, CancellationToken.None);

        Assert.Equal(1, await _fixture.CountBindingsPinnedToAsync(910009));
        Assert.Equal(1, await _fixture.CountLeasesAsync(appInstanceId));
    }

    [Fact]
    public async Task DeleteArtifactAsync_WhenSectionNameIsJsonEscaped_StillRunsTheStep()
    {
        // System.Text.Json reads the escaped name as runtimeMaintenance; a text pre-filter on the
        // stored JSON must not make a validated definition's steps silently disappear.
        await _fixture.InsertExampleDefinitionAsync(
            isApplied: true,
            rewriteJson: static json => json.Replace("\"runtimeMaintenance\"", "\"\\u0072untimeMaintenance\"", StringComparison.Ordinal));
        await _fixture.InsertBindingAsync(Guid.NewGuid(), artifactId: 910004);

        await _fixture.CreatePortalRepository().DeleteArtifactAsync(910004, CancellationToken.None);

        Assert.Equal(0, await _fixture.CountBindingsPinnedToAsync(910004));
    }

    [Fact]
    public async Task DeleteArtifactAsync_WhenRegisteredSchemaDiffers_RefusesAndRunsNothing()
    {
        await _fixture.InsertExampleDefinitionAsync(isApplied: true, registeredSchema: "omp_portal");
        await _fixture.InsertBindingAsync(Guid.NewGuid(), artifactId: 910005);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fixture.CreatePortalRepository().DeleteArtifactAsync(910005, CancellationToken.None));

        Assert.Contains("OMP-MODULE-RUNTIME-MAINTENANCE", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.CountBindingsPinnedToAsync(910005));
    }

    [Fact]
    public async Task DeleteArtifactAsync_WhenAnotherModuleClaimsTheSchema_RefusesAndRunsNothing()
    {
        await _fixture.InsertExampleDefinitionAsync(isApplied: true);
        await _fixture.RegisterIntruderModuleAsync("omp_example_webapp");
        await _fixture.InsertBindingAsync(Guid.NewGuid(), artifactId: 910006);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fixture.CreatePortalRepository().DeleteArtifactAsync(910006, CancellationToken.None));

        Assert.Contains("OMP-MODULE-RUNTIME-MAINTENANCE", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.CountBindingsPinnedToAsync(910006));
    }

    [Fact]
    public async Task DeleteArtifactAsync_WhenOtherDefinitionsAreCorrupt_NamesEachFailedModuleAndRunsNothing()
    {
        // F-F: one module's corrupt document is that module's failure. It is reported next to
        // every other failure instead of escaping as a raw exception, and the delete still stops.
        const string brokenB = ModuleRuntimeMaintenanceTestFixture.BrokenModuleKeyB;
        await _fixture.InsertExampleDefinitionAsync(isApplied: true);
        // A: its step SQL is outside the grammar. B: its SQL payload is not base64, which used to
        // escape as a FormatException instead of a runtime maintenance failure.
        const string brokenA = ModuleRuntimeMaintenanceTestFixture.BrokenModuleKeyA;
        await _fixture.InsertAppliedDocumentAsync(
            brokenA,
            $$$"""
            {"moduleKey":"{{{brokenA}}}","module":{"schemaName":"omp_{{{brokenA}}}"},
             "runtimeMaintenance":{"steps":[{"key":"s","event":"host-removed","execution":"idempotent","inlineSql":"TRUNCATE TABLE omp_{{{brokenA}}}.Leases;"}]}}
            """);
        await _fixture.InsertAppliedDocumentAsync(
            brokenB,
            $$$"""
            {"moduleKey":"{{{brokenB}}}","module":{"schemaName":"omp_{{{brokenB}}}"},
             "runtimeMaintenance":{"steps":[{"key":"s","event":"artifact-removed","execution":"idempotent","contentEncoding":"base64-utf8","content":"***not base64***"}]}}
            """);
        await _fixture.InsertBindingAsync(Guid.NewGuid(), artifactId: 910007);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fixture.CreatePortalRepository().DeleteArtifactAsync(910007, CancellationToken.None));

        Assert.StartsWith("OMP-MODULE-RUNTIME-MAINTENANCE", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"[{brokenA}]", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"[{brokenB}]", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("[example_webapp]", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.CountBindingsPinnedToAsync(910007));
    }

    [Fact]
    public async Task DeleteArtifactAsync_WhenRegisteredKeyDiffersOnlyInCase_RefusesAndRunsNothing()
    {
        // F-E: omp.Modules registers Example_WebApp while the applied document is example_webapp.
        // A case-insensitive collation joins them; the executor must not treat them as one module.
        await _fixture.InsertExampleDefinitionAsync(isApplied: true, registeredModuleKey: "Example_WebApp");
        await _fixture.InsertBindingAsync(Guid.NewGuid(), artifactId: 910008);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fixture.CreatePortalRepository().DeleteArtifactAsync(910008, CancellationToken.None));

        Assert.Contains("OMP-MODULE-RUNTIME-MAINTENANCE", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, await _fixture.CountBindingsPinnedToAsync(910008));
    }

    [Fact]
    public async Task SaveModuleDefinitionDocumentAsync_WhenKeyDiffersOnlyInCase_IsRefused()
    {
        // F-E at import: a second spelling of an imported module key derives the same schema.
        await _fixture.InsertExampleDefinitionAsync(isApplied: true);
        var repository = _fixture.CreatePortalRepository();

        var ex = await Assert.ThrowsAsync<SqlException>(() => repository.SaveModuleDefinitionDocumentAsync(
            new ModuleDefinitionDocumentEditData
            {
                ModuleKey = "Example_WebApp",
                DefinitionVersion = "9.9.9",
                FormatVersion = 1,
                DefinitionJson = "{}",
                DefinitionSha256 = "test",
            },
            replaceExisting: true,
            CancellationToken.None));

        Assert.Contains("OMP-MODULE-KEY-CASE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveModuleAsync_WhenKeyDiffersOnlyInCase_IsRefused()
    {
        // F-E at registration.
        await _fixture.InsertExampleDefinitionAsync(isApplied: true);
        var repository = _fixture.CreatePortalRepository();

        var ex = await Assert.ThrowsAsync<SqlException>(() => repository.SaveModuleAsync(
            new ModuleEditData
            {
                ModuleKey = "EXAMPLE_WEBAPP",
                DisplayName = "Shadow",
                ModuleType = "WebApp",
                SchemaName = "omp_EXAMPLE_WEBAPP",
                IsEnabled = true,
            },
            CancellationToken.None));

        Assert.Contains("OMP-MODULE-KEY-CASE", ex.Message, StringComparison.Ordinal);
    }
}

