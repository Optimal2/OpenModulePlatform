using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Provisioning;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Database tests for <see cref="WorkerAppInstanceProvisioningService"/> against the real core
/// schema: both topologies (one host and several hosts), per-host RPC settings, refusals that
/// must leave nothing behind, idempotent retry and concurrent duplicate calls.
/// </summary>
public sealed class WorkerAppInstanceProvisioningServiceTests : IClassFixture<WorkerAppInstanceProvisioningTestFixture>
{
    private readonly WorkerAppInstanceProvisioningTestFixture _fixture;

    public WorkerAppInstanceProvisioningServiceTests(WorkerAppInstanceProvisioningTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OneHost_CreatesHostNeutralInstanceAndOneWorkerCarryingThatHostsRpc()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var latest = await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.10.0");
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "2.0.0", enabled: false);
        await _fixture.AddAppInstanceAsync(scenario, "existing_shared", scenario.WorkerAppId, latest, "worker");

        var result = await _fixture.CreateService(scenario.ModulePermission)
            .ProvisionWorkerAppInstanceAsync(WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("dedicated_a", scenario.Hosts[0]), CancellationToken.None);

        Assert.True(result.AppInstanceCreated);
        var instance = await _fixture.ReadAppInstanceAsync(result.AppInstanceId);
        Assert.Equal(scenario.ModuleInstanceId, instance.ModuleInstanceId);
        Assert.Null(instance.HostId);
        Assert.Null(instance.TargetHostTemplateId);
        Assert.Equal(scenario.WorkerAppId, instance.AppId);
        Assert.Equal("worker", instance.InstallationName);
        Assert.Equal(latest, instance.ArtifactId);
        Assert.True(instance.IsActive);

        var worker = Assert.Single(await _fixture.ReadWorkersAsync(result.AppInstanceId));
        Assert.Equal(scenario.Hosts[0], worker.HostId);
        Assert.Equal(latest, worker.ArtifactId);
        Assert.Equal($"dedicated_a_prov0_{scenario.Suffix}_example_test", worker.WorkerInstanceKey);
        Assert.Equal(
            $"{{\"ProvModule\":{{\"HostAgentRpc\":{{\"Enabled\":true,\"PipeName\":\"OpenModulePlatform.HostAgent.{scenario.HostKey(0)}\",\"TimeoutSeconds\":30}}}}}}",
            worker.ConfigurationJson);
    }

    [Fact]
    public async Task ThreeHosts_EachWorkerGetsItsOwnHostsRpcSettings()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 3);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.AddHostConfigurationAsync(
            scenario.HostKey(1),
            "{\"hostAgentRpc\":{\"pipeName\":\"custom-pipe-one\",\"timeoutSeconds\":45}}");

        var result = await _fixture.CreateService(scenario.ModulePermission)
            .ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(),
                scenario.Request("pooled_lb", scenario.Hosts[0], scenario.Hosts[1], scenario.Hosts[2]),
                CancellationToken.None);

        Assert.Equal(3, result.Workers.Count);
        Assert.All(result.Workers, worker => Assert.True(worker.Created));
        Assert.Equal(scenario.Hosts, result.Workers.Select(worker => worker.HostId));

        var workers = (await _fixture.ReadWorkersAsync(result.AppInstanceId)).ToDictionary(worker => worker.HostId);
        Assert.Contains($"\"PipeName\":\"OpenModulePlatform.HostAgent.{scenario.HostKey(0)}\",\"TimeoutSeconds\":30", workers[scenario.Hosts[0]].ConfigurationJson);
        Assert.Contains("\"PipeName\":\"custom-pipe-one\",\"TimeoutSeconds\":45", workers[scenario.Hosts[1]].ConfigurationJson);
        Assert.Contains($"\"PipeName\":\"OpenModulePlatform.HostAgent.{scenario.HostKey(2)}\",\"TimeoutSeconds\":30", workers[scenario.Hosts[2]].ConfigurationJson);
    }

    [Fact]
    public async Task HostWithoutRpcData_FailsAndCreatesNothing()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 2, hostsWithHostAgent: [0]);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(),
                scenario.Request("no_rpc", scenario.Hosts[0], scenario.Hosts[1]),
                CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.HostAgentRpcUnavailable, error.Error);
        Assert.Contains(scenario.HostKey(1), error.Message);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "no_rpc"));
        Assert.Equal(0, await _fixture.CountWorkersAsync(scenario));
    }

    [Fact]
    public async Task HostConfigurationThatDisablesRpc_FailsAndCreatesNothing()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.AddHostConfigurationAsync(scenario.HostKey(0), "{\"hostAgentRpc\":{\"enabled\":false}}");

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("rpc_off", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.HostAgentRpcUnavailable, error.Error);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "rpc_off"));
    }

    [Fact]
    public async Task TemplateWithDisabledArtifact_IsRefusedAsIncompatible()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        var disabled = await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0", enabled: false);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.1.0");
        var template = await _fixture.AddAppInstanceAsync(scenario, "template", scenario.WorkerAppId, disabled, "worker");

        var request = scenario.Request("from_template", scenario.Hosts[0]);
        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(),
                WithTemplate(request, template),
                CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.IncompatibleArtifact, error.Error);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "from_template"));
    }

    [Fact]
    public async Task ArtifactsOutsideTheAppliedDefinitionRange_AreNotCompatible()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0-x", packageType: "web-app");
        await _fixture.ApplyDefinitionAsync(scenario, min: "2.0.0", max: null);

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("too_old", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.NoCompatibleArtifact, error.Error);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "too_old"));

        var inRange = await _fixture.AddArtifactAsync(scenario.WorkerAppId, "2.1.0");
        var result = await _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("in_range", scenario.Hosts[0]), CancellationToken.None);
        Assert.Equal(inRange, result.ArtifactId);
    }

    [Fact]
    public async Task HostFromAnotherInstallation_IsRefused()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var otherInstallation = await _fixture.AddInstallationAsync(scenario.Suffix);
        var foreignHost = await _fixture.AddHostAsync(otherInstallation, "FOREIGN-" + scenario.Suffix, withHostAgent: true);

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(),
                scenario.Request("cross_install", scenario.Hosts[0], foreignHost),
                CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.HostOutsideInstallation, error.Error);
        Assert.Equal(0, await _fixture.CountWorkersAsync(scenario));
    }

    [Fact]
    public async Task KeyUsedByAHostBoundOrDifferentAppInstance_IsACollision()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        var artifact = await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.AddAppInstanceAsync(scenario, "bound", scenario.WorkerAppId, artifact, "worker", hostId: scenario.Hosts[0]);
        await _fixture.AddAppInstanceAsync(scenario, "web_key", scenario.WebAppId, null, null);
        var service = _fixture.CreateService(scenario.ModulePermission);

        foreach (var key in new[] { "bound", "web_key" })
        {
            var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
                service.ProvisionWorkerAppInstanceAsync(
                    WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request(key, scenario.Hosts[0]), CancellationToken.None));
            Assert.Equal(WorkerAppInstanceProvisioningError.KeyCollision, error.Error);
        }

        Assert.Equal(0, await _fixture.CountWorkersAsync(scenario));
    }

    [Fact]
    public async Task ConcurrentDuplicateCalls_CreateOneInstanceAndOneWorkerPerHost()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 2);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var service = _fixture.CreateService(scenario.ModulePermission);
        var request = scenario.Request("racing", scenario.Hosts[0], scenario.Hosts[1]);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            service.ProvisionWorkerAppInstanceAsync(WorkerAppInstanceProvisioningTestFixture.Caller(), request, CancellationToken.None))));

        Assert.Single(results.Select(result => result.AppInstanceId).Distinct());
        Assert.Single(results, result => result.AppInstanceCreated);
        Assert.Equal(1, await _fixture.CountAppInstancesAsync(scenario, "racing"));
        Assert.Equal(2, await _fixture.CountWorkersAsync(scenario));
    }

    [Fact]
    public async Task RetryAfterAFailedCall_CompletesTheWholeOperation()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 2, hostsWithHostAgent: [0]);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var service = _fixture.CreateService(scenario.ModulePermission);
        var request = scenario.Request("retry", scenario.Hosts[0], scenario.Hosts[1]);

        await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            service.ProvisionWorkerAppInstanceAsync(WorkerAppInstanceProvisioningTestFixture.Caller(), request, CancellationToken.None));
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "retry"));

        await _fixture.RegisterHostAgentAsync(scenario.Hosts[1]);
        var result = await service.ProvisionWorkerAppInstanceAsync(WorkerAppInstanceProvisioningTestFixture.Caller(), request, CancellationToken.None);

        Assert.True(result.AppInstanceCreated);
        Assert.Equal(2, (await _fixture.ReadWorkersAsync(result.AppInstanceId)).Count);
    }

    [Fact]
    public async Task ExistingInstance_IsReusedAndOnlyMissingWorkersAreAdded()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 2);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var service = _fixture.CreateService(scenario.ModulePermission);

        var first = await service.ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("grow", scenario.Hosts[0]), CancellationToken.None);
        var firstWorker = Assert.Single(await _fixture.ReadWorkersAsync(first.AppInstanceId));

        // A newer artifact must not move the existing instance, and the existing worker's
        // configuration must survive the second call byte for byte.
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "9.0.0");
        var second = await service.ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("grow", scenario.Hosts[0], scenario.Hosts[1]), CancellationToken.None);

        Assert.False(second.AppInstanceCreated);
        Assert.Equal(first.AppInstanceId, second.AppInstanceId);
        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.False(second.Workers[0].Created);
        Assert.Equal(firstWorker.WorkerInstanceId, second.Workers[0].WorkerInstanceId);
        Assert.True(second.Workers[1].Created);

        var workers = (await _fixture.ReadWorkersAsync(first.AppInstanceId)).ToDictionary(worker => worker.HostId);
        Assert.Equal(firstWorker.ConfigurationJson, workers[scenario.Hosts[0]].ConfigurationJson);
        Assert.Equal(first.ArtifactId, workers[scenario.Hosts[1]].ArtifactId);
    }

    [Fact]
    public async Task Template_SuppliesBindingButNeverItsWorkerConfiguration()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        var templateArtifact = await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.5.0");
        var template = await _fixture.AddAppInstanceAsync(scenario, "tmpl", scenario.WorkerAppId, templateArtifact, "tmpl-install");
        await _fixture.AddWorkerAsync(template, scenario.Hosts[0], "tmpl_worker", "{\"ProvModule\":{\"HostAgentRpc\":{\"PipeName\":\"someone-elses-pipe\"}}}");

        var result = await _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(),
            WithTemplate(scenario.Request("copy", scenario.Hosts[0]), template),
            CancellationToken.None);

        var instance = await _fixture.ReadAppInstanceAsync(result.AppInstanceId);
        Assert.Equal("tmpl-install", instance.InstallationName);
        Assert.Equal(templateArtifact, instance.ArtifactId);
        var worker = Assert.Single(await _fixture.ReadWorkersAsync(result.AppInstanceId));
        Assert.DoesNotContain("someone-elses-pipe", worker.ConfigurationJson);
        Assert.Contains($"OpenModulePlatform.HostAgent.{scenario.HostKey(0)}", worker.ConfigurationJson);
    }

    [Fact]
    public async Task Authorization_RequiresPlatformAdminOrAPermissionOfTheSameModule()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var other = await _fixture.SeedAsync(hostCount: 0);

        var none = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService().ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("auth_none", scenario.Hosts[0]), CancellationToken.None));
        Assert.Equal(WorkerAppInstanceProvisioningError.Unauthorized, none.Error);

        // A permission that only another module declares does not authorize this module.
        var foreignRequest = WithPermission(scenario.Request("auth_foreign", scenario.Hosts[0]), other.ModulePermission);
        var foreign = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(other.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), foreignRequest, CancellationToken.None));
        Assert.Equal(WorkerAppInstanceProvisioningError.Unauthorized, foreign.Error);
        Assert.Equal(0, await _fixture.CountWorkersAsync(scenario));

        var admin = await _fixture.CreateService(PortalAdminNavigation.AdminPermission).ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(),
            WithPermission(scenario.Request("auth_admin", scenario.Hosts[0]), null),
            CancellationToken.None);
        Assert.True(admin.AppInstanceCreated);
    }

    [Fact]
    public async Task NonWorkerApp_IsRefused()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        var request = scenario.Request("web", scenario.Hosts[0]);
        var webRequest = new WorkerAppInstanceProvisioningRequest
        {
            ModuleKey = request.ModuleKey,
            AppKey = "prov_web",
            AppInstanceKey = request.AppInstanceKey,
            DisplayName = request.DisplayName,
            HostIds = request.HostIds,
            HostAgentRpcConfigurationPath = request.HostAgentRpcConfigurationPath,
            ModuleAdministrationPermission = request.ModuleAdministrationPermission
        };

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), webRequest, CancellationToken.None));
        Assert.Equal(WorkerAppInstanceProvisioningError.NotAWorkerApp, error.Error);
    }

    private static WorkerAppInstanceProvisioningRequest WithTemplate(WorkerAppInstanceProvisioningRequest request, Guid templateId)
        => Copy(request, templateId, request.ModuleAdministrationPermission);

    private static WorkerAppInstanceProvisioningRequest WithPermission(WorkerAppInstanceProvisioningRequest request, string? permission)
        => Copy(request, request.TemplateAppInstanceId, permission);

    private static WorkerAppInstanceProvisioningRequest Copy(WorkerAppInstanceProvisioningRequest request, Guid? templateId, string? permission)
        => new()
        {
            ModuleKey = request.ModuleKey,
            AppKey = request.AppKey,
            AppInstanceKey = request.AppInstanceKey,
            DisplayName = request.DisplayName,
            HostIds = request.HostIds,
            HostAgentRpcConfigurationPath = request.HostAgentRpcConfigurationPath,
            TemplateAppInstanceId = templateId,
            ModuleAdministrationPermission = permission
        };
}
