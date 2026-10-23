using Microsoft.Data.SqlClient;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
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

        await _fixture.RegisterHostAgentAsync(scenario.Hosts[1], WorkerAppInstanceProvisioningTestFixture.DefaultPipe(scenario.HostKey(1)));
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

    [Fact]
    public async Task UnauthenticatedCaller_IsRefusedEvenWhenPermissionsWouldAllowIt()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(PortalAdminNavigation.AdminPermission, scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.AnonymousCaller(), scenario.Request("anon", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.Unauthorized, error.Error);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "anon"));
    }

    [Fact]
    public async Task PermissionDeclaredOnlyByADisabledApp_DoesNotAuthorize()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.DisableAppAsync(scenario.WebAppId);

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("disabled_decl", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.Unauthorized, error.Error);
        Assert.Equal(0, await _fixture.CountWorkersAsync(scenario));
    }

    [Fact]
    public async Task TemplateOfAnotherApp_IsATemplateMismatch()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var webTemplate = await _fixture.AddAppInstanceAsync(scenario, "web_tmpl", scenario.WebAppId, null, "web");

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(),
                WithTemplate(scenario.Request("from_web", scenario.Hosts[0]), webTemplate),
                CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.TemplateMismatch, error.Error);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "from_web"));
    }

    [Fact]
    public async Task ModuleInstanceOfAnotherModule_IsUnresolved()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var other = await _fixture.SeedAsync(hostCount: 0);

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(),
                WithModuleInstance(scenario.Request("foreign_mi", scenario.Hosts[0]), other.ModuleInstanceId),
                CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.ModuleInstanceUnresolved, error.Error);
        Assert.Equal(0, await _fixture.CountWorkersAsync(scenario));
        Assert.Equal(0, await _fixture.CountWorkersAsync(other));
    }

    [Fact]
    public async Task HostKeysThatNormalizeToTheSameWorkerKey_AreACollisionAndCreateNothing()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 0);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var dash = await _fixture.AddHostAsync(scenario.InstanceId, $"NORM-{scenario.Suffix}.test", withHostAgent: true);
        var underscore = await _fixture.AddHostAsync(scenario.InstanceId, $"norm_{scenario.Suffix}.test", withHostAgent: true);

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("norm", dash, underscore), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.KeyCollision, error.Error);
        Assert.Contains($"norm_norm_{scenario.Suffix}_test", error.Message);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "norm"));
        Assert.Equal(0, await _fixture.CountWorkersAsync(scenario));
    }

    [Fact]
    public async Task OverlongWorkerKeys_AreTruncatedToDistinctHostIdSuffixedKeys()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 0);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        var longHost = new string('h', 100);
        var first = await _fixture.AddHostAsync(scenario.InstanceId, $"{longHost}-a-{scenario.Suffix}", withHostAgent: true);
        var second = await _fixture.AddHostAsync(scenario.InstanceId, $"{longHost}-b-{scenario.Suffix}", withHostAgent: true);
        var appInstanceKey = "k" + new string('x', 99);

        var result = await _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request(appInstanceKey, first, second), CancellationToken.None);

        Assert.All(result.Workers, worker => Assert.True(worker.WorkerInstanceKey.Length <= 150));
        Assert.Equal($"{appInstanceKey}_{first.ToString("N")[..12]}", result.Workers[0].WorkerInstanceKey);
        Assert.Equal($"{appInstanceKey}_{second.ToString("N")[..12]}", result.Workers[1].WorkerInstanceKey);
    }

    [Fact]
    public async Task ReusedInstanceWithoutArtifact_IsRefusedAndAddsNoWorker()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.AddAppInstanceAsync(scenario, "no_artifact", scenario.WorkerAppId, null, "worker");

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("no_artifact", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.NoCompatibleArtifact, error.Error);
        Assert.Equal(0, await _fixture.CountWorkersAsync(scenario));
    }

    [Theory]
    [InlineData(false, 0, "pipe")]
    [InlineData(true, 3600, "pipe")]
    [InlineData(true, 0, null)]
    public async Task InactiveStaleOrPipelessRegistration_IsNotTrusted(bool isActive, int lastSeenSecondsAgo, string? pipeName)
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1, hostsWithHostAgent: []);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.RegisterHostAgentAsync(
            scenario.Hosts[0],
            pipeName is null ? null : WorkerAppInstanceProvisioningTestFixture.DefaultPipe(scenario.HostKey(0)),
            isActive,
            lastSeenSecondsAgo);

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("untrusted", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.HostAgentRpcUnavailable, error.Error);
        Assert.Contains(scenario.HostKey(0), error.Message);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "untrusted"));
    }

    [Fact]
    public async Task CustomPipePublishedByHostAgent_IsWrittenToTheWorker()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1, hostsWithHostAgent: []);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");

        // The real HostAgent publish path, as a HostAgent with RpcPipeName = "custom.pipe.<suffix>" runs it.
        var process = new HostAgentProcessContext("OMP.HostAgent.v2", "2.0.0", HostAgentRuntimeMode.Normal, null);
        process.SetRpcPipeName("custom.pipe." + scenario.Suffix);
        var repository = new OmpHostArtifactRepository(new TestHostAgentConnectionFactory(_fixture.ConnectionString));
        await repository.PublishHostAgentRuntimeStateAsync(
            scenario.Hosts[0], process, HostAgentRuntimeMode.Normal, artifactId: null, installPath: null, isActive: true, statusMessage: null, CancellationToken.None);

        var result = await _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("custom", scenario.Hosts[0]), CancellationToken.None);

        Assert.Equal("custom.pipe." + scenario.Suffix, Assert.Single(result.Workers).HostAgentRpcPipeName);
        var worker = Assert.Single(await _fixture.ReadWorkersAsync(result.AppInstanceId));
        Assert.Contains($"\"PipeName\":\"custom.pipe.{scenario.Suffix}\",\"TimeoutSeconds\":30", worker.ConfigurationJson);
        Assert.DoesNotContain("OpenModulePlatform.HostAgent.", worker.ConfigurationJson);
    }

    [Fact]
    public async Task OnlyTheActiveRegistrationCounts_WhenAnOlderServiceRowRemains()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1, hostsWithHostAgent: []);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.RegisterHostAgentAsync(scenario.Hosts[0], "retired-pipe", isActive: false, serviceName: "OMP.HostAgent.v1");
        await _fixture.RegisterHostAgentAsync(scenario.Hosts[0], "current-pipe", isActive: true, lastSeenSecondsAgo: 30, serviceName: "OMP.HostAgent.v2");

        var result = await _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("takeover", scenario.Hosts[0]), CancellationToken.None);

        Assert.Equal("current-pipe", Assert.Single(result.Workers).HostAgentRpcPipeName);
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData(30, 600)]
    [InlineData(200, 600)]
    [InlineData(900, 2700)]
    public void RegistrationWindow_IsTheDefaultOrThreeOfTheAgentsOwnIntervals(int? refreshSeconds, long expected)
        => Assert.Equal(expected, WorkerAppInstanceProvisioningService.GetRegistrationMaxAgeSeconds(refreshSeconds));

    [Fact]
    public async Task SlowHeartbeatAgent_IsTrustedWithinItsOwnWindow()
    {
        // RefreshSeconds 900: last seen 25 minutes ago is past the 10-minute default but
        // within three of the agent's own intervals, so the healthy agent is not stale.
        var scenario = await _fixture.SeedAsync(hostCount: 1, hostsWithHostAgent: []);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.RegisterHostAgentAsync(scenario.Hosts[0], "slow-pipe", lastSeenSecondsAgo: 1500, refreshSeconds: 900);

        var result = await _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("slow", scenario.Hosts[0]), CancellationToken.None);

        Assert.Equal("slow-pipe", Assert.Single(result.Workers).HostAgentRpcPipeName);
    }

    [Fact]
    public async Task SlowHeartbeatAgent_PastItsOwnWindow_IsStaleAndTheRefusalNamesItsVersion()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1, hostsWithHostAgent: []);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.RegisterHostAgentAsync(
            scenario.Hosts[0], "slow-pipe", lastSeenSecondsAgo: 3000, refreshSeconds: 900, version: "0.3.334");

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("stale", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.HostAgentRpcUnavailable, error.Error);
        Assert.Contains(scenario.HostKey(0), error.Message);
        Assert.Contains("version 0.3.334", error.Message);
        Assert.Contains("2700 s freshness window", error.Message);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "stale"));
    }

    [Fact]
    public async Task DefaultIntervalAgent_PastTheDefaultWindow_IsStale()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1, hostsWithHostAgent: []);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.RegisterHostAgentAsync(scenario.Hosts[0], "pipe", lastSeenSecondsAgo: 700, refreshSeconds: 30);

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("default_stale", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.HostAgentRpcUnavailable, error.Error);
        Assert.Contains("600 s freshness window", error.Message);
    }

    [Fact]
    public async Task PipelessRegistration_RefusalNamesTheRegisteringAgentVersion()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1, hostsWithHostAgent: []);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.RegisterHostAgentAsync(scenario.Hosts[0], null, version: "0.3.300");

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("old_agent", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.HostAgentRpcUnavailable, error.Error);
        Assert.Contains("version 0.3.300", error.Message);
        Assert.Contains("registered no RPC pipe", error.Message);
    }

    [Fact]
    public async Task HostAgentPublish_RegistersItsRefreshSeconds()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1, hostsWithHostAgent: []);
        var process = new HostAgentProcessContext("OMP.HostAgent.slow", "2.0.0", HostAgentRuntimeMode.Normal, null);
        process.SetRpcPipeName("slow.pipe." + scenario.Suffix);
        process.SetRefreshSeconds(900);
        var repository = new OmpHostArtifactRepository(new TestHostAgentConnectionFactory(_fixture.ConnectionString));
        await repository.PublishHostAgentRuntimeStateAsync(
            scenario.Hosts[0], process, HostAgentRuntimeMode.Normal, artifactId: null, installPath: null, isActive: true, statusMessage: null, CancellationToken.None);

        Assert.Equal(900, await _fixture.ReadRegisteredRefreshSecondsAsync(scenario.Hosts[0], "OMP.HostAgent.slow"));
    }

    [Theory]
    [InlineData("{\"values\":{\"hostAgentRpc\":", true)]
    [InlineData("[{\"values\":{}}]", false)]
    [InlineData("{\"values\":{\"hostAgentRpc\":{\"pipeName\":\"a\"}},\"values\":{}}", false)]
    public async Task UnreadableHostConfiguration_IsRefusedInsteadOfFallingBackToTheRegistration(string rawJson, bool bypassJsonCheck)
    {
        // The host has a fresh registration, so a silent fallback would succeed.
        var scenario = await _fixture.SeedAsync(hostCount: 1);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.AddRawHostConfigurationAsync(scenario.HostKey(0), rawJson, bypassJsonCheck);

        var error = await Assert.ThrowsAsync<WorkerAppInstanceProvisioningException>(() =>
            _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
                WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("unreadable", scenario.Hosts[0]), CancellationToken.None));

        Assert.Equal(WorkerAppInstanceProvisioningError.HostConfigurationInvalid, error.Error);
        Assert.Contains(scenario.HostKey(0), error.Message);
        Assert.Equal(0, await _fixture.CountAppInstancesAsync(scenario, "unreadable"));
        Assert.Equal(0, await _fixture.CountWorkersAsync(scenario));
    }

    [Fact]
    public async Task HostConfiguration_TakesPrecedenceOverTheRegisteredPipe()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 2, hostsWithHostAgent: []);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.RegisterHostAgentAsync(scenario.Hosts[0], "registered-zero");
        await _fixture.RegisterHostAgentAsync(scenario.Hosts[1], "registered-one");
        await _fixture.AddHostConfigurationAsync(scenario.HostKey(0), "{\"hostAgentRpc\":{\"pipeName\":\"configured-zero\"}}");
        await _fixture.AddHostConfigurationAsync(scenario.HostKey(1), "{\"hostAgentRpc\":{\"timeoutSeconds\":75}}");

        var result = await _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("precedence", scenario.Hosts[0], scenario.Hosts[1]), CancellationToken.None);

        var workers = (await _fixture.ReadWorkersAsync(result.AppInstanceId)).ToDictionary(worker => worker.HostId);
        Assert.Contains("\"PipeName\":\"configured-zero\",\"TimeoutSeconds\":30", workers[scenario.Hosts[0]].ConfigurationJson);
        // A configuration without pipeName still contributes its timeout to the registered pipe.
        Assert.Contains("\"PipeName\":\"registered-one\",\"TimeoutSeconds\":75", workers[scenario.Hosts[1]].ConfigurationJson);
    }

    [Fact]
    public async Task ActiveHostConfiguration_IsTheHighestConfigurationVersionNotTheLatestUpdate()
    {
        var scenario = await _fixture.SeedAsync(hostCount: 1, hostsWithHostAgent: []);
        await _fixture.AddArtifactAsync(scenario.WorkerAppId, "1.0.0");
        await _fixture.AddHostConfigurationAsync(
            scenario.HostKey(0), "{\"hostAgentRpc\":{\"pipeName\":\"pinned-1-10\"}}", version: "1.10.0", updatedSecondsAgo: 3600);
        await _fixture.AddHostConfigurationAsync(scenario.HostKey(0), "{}", version: "1.9.0");

        var result = await _fixture.CreateService(scenario.ModulePermission).ProvisionWorkerAppInstanceAsync(
            WorkerAppInstanceProvisioningTestFixture.Caller(), scenario.Request("versioned", scenario.Hosts[0]), CancellationToken.None);

        Assert.Equal("pinned-1-10", Assert.Single(result.Workers).HostAgentRpcPipeName);
    }

    private static WorkerAppInstanceProvisioningRequest WithTemplate(WorkerAppInstanceProvisioningRequest request, Guid templateId)
        => Copy(request, templateId, request.ModuleAdministrationPermission, request.ModuleInstanceId);

    private static WorkerAppInstanceProvisioningRequest WithPermission(WorkerAppInstanceProvisioningRequest request, string? permission)
        => Copy(request, request.TemplateAppInstanceId, permission, request.ModuleInstanceId);

    private static WorkerAppInstanceProvisioningRequest WithModuleInstance(WorkerAppInstanceProvisioningRequest request, Guid moduleInstanceId)
        => Copy(request, request.TemplateAppInstanceId, request.ModuleAdministrationPermission, moduleInstanceId);

    private static WorkerAppInstanceProvisioningRequest Copy(
        WorkerAppInstanceProvisioningRequest request,
        Guid? templateId,
        string? permission,
        Guid? moduleInstanceId)
        => new()
        {
            ModuleKey = request.ModuleKey,
            AppKey = request.AppKey,
            AppInstanceKey = request.AppInstanceKey,
            DisplayName = request.DisplayName,
            HostIds = request.HostIds,
            HostAgentRpcConfigurationPath = request.HostAgentRpcConfigurationPath,
            TemplateAppInstanceId = templateId,
            ModuleInstanceId = moduleInstanceId,
            ModuleAdministrationPermission = permission
        };

    private sealed class TestHostAgentConnectionFactory(string connectionString) : ISqlConnectionFactory
    {
        public string GetConnectionString() => connectionString;

        public SqlConnection Create() => new(connectionString);
    }
}
