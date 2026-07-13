using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ServiceAppDeploymentServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private bool _disposed;

    public ServiceAppDeploymentServiceTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("omp-serviceapp-tests-").FullName;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }

    [Fact]
    public async Task AlreadyApplied_ServiceStopped_DesiredRunning_StartsServiceAndEmitsWarning()
    {
        var (service, repository, control, deployment, targetPath) = CreateScenario();
        control.SetState(deployment.DeployedRuntimeName!, "STOPPED");

        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);

        Assert.Single(repository.PublishedServiceAppResults);
        var result = repository.PublishedServiceAppResults[0].Result;
        Assert.Equal(HostDeploymentStatuses.Warning, result.State);
        Assert.Contains($"Service '{deployment.DeployedRuntimeName}' was stopped during reconcile", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attempted restart", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(control.StartAttempts, deployment.DeployedRuntimeName!);
        Assert.True(control.IsServiceRunning(deployment.DeployedRuntimeName!));
        Assert.False(File.Exists(DeploymentRuntimeStopMarker.GetPath(targetPath)));
    }

    [Fact]
    public async Task AlreadyApplied_ServiceStopped_StartAfterDeploymentDisabled_LeavesServiceAlone()
    {
        var (service, repository, control, deployment, targetPath) = CreateScenario(startAfterDeployment: false);
        control.SetState(deployment.DeployedRuntimeName!, "STOPPED");

        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);

        Assert.Single(repository.PublishedServiceAppResults);
        var result = repository.PublishedServiceAppResults[0].Result;
        Assert.Equal(HostDeploymentStatuses.Succeeded, result.State);
        Assert.Null(result.ErrorMessage);
        Assert.Empty(control.StartAttempts);
        Assert.False(control.IsServiceRunning(deployment.DeployedRuntimeName!));
        Assert.False(File.Exists(DeploymentRuntimeStopMarker.GetPath(targetPath)));
    }

    [Fact]
    public async Task AlreadyApplied_ServiceStopped_StopMarkerPresent_LeavesServiceAlone()
    {
        var (service, repository, control, deployment, targetPath) = CreateScenario();
        control.SetState(deployment.DeployedRuntimeName!, "STOPPED");
        DeploymentRuntimeStopMarker.Write(
            targetPath,
            "service-app",
            deployment.DeployedRuntimeName!,
            deployment.AppInstanceId,
            deployment.AppInstanceKey,
            deployment.HostKey);

        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);

        Assert.Single(repository.PublishedServiceAppResults);
        var result = repository.PublishedServiceAppResults[0].Result;
        Assert.Equal(HostDeploymentStatuses.Succeeded, result.State);
        Assert.Null(result.ErrorMessage);
        Assert.Empty(control.StartAttempts);
        Assert.False(control.IsServiceRunning(deployment.DeployedRuntimeName!));
    }

    [Fact]
    public async Task AlreadyApplied_ServiceRunning_NoChange()
    {
        var (service, repository, control, deployment, targetPath) = CreateScenario();
        control.SetState(deployment.DeployedRuntimeName!, "RUNNING");

        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);

        Assert.Single(repository.PublishedServiceAppResults);
        var result = repository.PublishedServiceAppResults[0].Result;
        Assert.Equal(HostDeploymentStatuses.Succeeded, result.State);
        Assert.Null(result.ErrorMessage);
        Assert.Empty(control.StartAttempts);
        Assert.True(control.IsServiceRunning(deployment.DeployedRuntimeName!));
        Assert.False(File.Exists(DeploymentRuntimeStopMarker.GetPath(targetPath)));
    }

    [Fact]
    public async Task AlreadyApplied_ServiceStopsRepeatedly_AfterThresholdEmitsPersistentWarningAndStopsRestarting()
    {
        const int Threshold = 3;
        var (service, repository, control, deployment, targetPath) = CreateScenario();
        control.SetState(deployment.DeployedRuntimeName!, "STOPPED");
        // Simulate a start that succeeds transiently but the service dies again
        // before the next reconcile tick.
        control.StartChangesStateToRunning = false;

        for (var i = 0; i < Threshold + 2; i++)
        {
            await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);
            Assert.Single(repository.PublishedServiceAppResults);

            var result = repository.PublishedServiceAppResults[^1].Result;
            if (i < Threshold)
            {
                Assert.Equal(HostDeploymentStatuses.Warning, result.State);
                Assert.Contains($"Service '{deployment.DeployedRuntimeName}' was stopped during reconcile", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("exceeded the maximum number of restart attempts", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(i + 1, control.StartAttempts.Count);
            }
            else
            {
                Assert.Equal(HostDeploymentStatuses.Warning, result.State);
                Assert.Contains("exceeded the maximum number of restart attempts", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Threshold, control.StartAttempts.Count);
            }

            repository.PublishedServiceAppResults.Clear();
        }
    }

    [Fact]
    public async Task AlreadyApplied_ServiceRestartsThenRuns_CounterResets()
    {
        var (service, repository, control, deployment, targetPath) = CreateScenario();
        control.SetState(deployment.DeployedRuntimeName!, "STOPPED");

        // First reconcile: stopped -> start -> warning.
        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);
        Assert.Single(repository.PublishedServiceAppResults);
        Assert.Equal(HostDeploymentStatuses.Warning, repository.PublishedServiceAppResults[0].Result.State);
        Assert.Single(control.StartAttempts);
        repository.PublishedServiceAppResults.Clear();

        // Second reconcile: running -> succeeded, counter resets.
        control.SetState(deployment.DeployedRuntimeName!, "RUNNING");
        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);
        Assert.Single(repository.PublishedServiceAppResults);
        Assert.Equal(HostDeploymentStatuses.Succeeded, repository.PublishedServiceAppResults[0].Result.State);
        repository.PublishedServiceAppResults.Clear();

        // Third reconcile: stopped again -> should restart (counter was reset).
        control.SetState(deployment.DeployedRuntimeName!, "STOPPED");
        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);
        Assert.Single(repository.PublishedServiceAppResults);
        Assert.Equal(HostDeploymentStatuses.Warning, repository.PublishedServiceAppResults[0].Result.State);
        Assert.Equal(2, control.StartAttempts.Count);
    }

    private (ServiceAppDeploymentService Service, FakeOmpHostArtifactRepository Repository, FakeWindowsServiceControl Control, ServiceAppDeploymentDescriptor Deployment, string TargetPath) CreateScenario(
        bool startAfterDeployment = true)
    {
        var settings = new HostAgentSettings
        {
            DeployServiceApps = true,
            CentralArtifactRoot = _tempRoot,
            LocalArtifactCacheRoot = _tempRoot,
            ServicesRoot = _tempRoot,
            StartServiceAfterServiceAppDeployment = startAfterDeployment,
            StopServiceForServiceAppDeployment = true,
            ServiceAppStopTimeoutSeconds = 1,
            ServiceAppStartTimeoutSeconds = 1
        };
        var optionsMonitor = new FakeOptionsMonitor<HostAgentSettings> { CurrentValue = settings };
        var repository = new FakeOmpHostArtifactRepository();
        var control = new FakeWindowsServiceControl();
        var credentialStore = new HostAgentCredentialStoreService(Options.Create(settings));
        var service = new ServiceAppDeploymentService(
            optionsMonitor,
            repository,
            credentialStore,
            NullLogger<ServiceAppDeploymentService>.Instance,
            control);

        var (deployment, targetPath) = CreateAlreadyAppliedDeployment();
        repository.DesiredServiceAppDeployments.Add(deployment);
        ApplyConfigurationForAlreadyAppliedDeployment(repository, settings, deployment, targetPath);

        return (service, repository, control, deployment, targetPath);
    }

    private static void ApplyConfigurationForAlreadyAppliedDeployment(
        FakeOmpHostArtifactRepository repository,
        HostAgentSettings settings,
        ServiceAppDeploymentDescriptor deployment,
        string targetPath)
    {
        var configuredConnectionString = repository.GetConfiguredConnectionString();
        var configurationFiles = repository.GetArtifactConfigurationFilesAsync(
            deployment.ArtifactId,
            deployment.HostKey,
            CancellationToken.None).GetAwaiter().GetResult();
        configurationFiles = ArtifactConfigurationFileWriter.WithBuiltInServiceAppConfiguration(
            configurationFiles,
            deployment,
            configuredConnectionString,
            settings);
        var configurationVariables = ArtifactConfigurationFileWriter.CreateVariables(
            deployment,
            configuredConnectionString,
            settings);
        ArtifactConfigurationFileWriter.ApplyAsync(
            targetPath,
            configurationFiles,
            configurationVariables,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private (ServiceAppDeploymentDescriptor Deployment, string TargetPath) CreateAlreadyAppliedDeployment()
    {
        var appInstanceId = Guid.NewGuid();
        var appInstanceKey = $"test-app-{appInstanceId:N}";
        var serviceName = "TestService";
        var sourcePath = Path.Combine(_tempRoot, "source", appInstanceKey);
        var targetPath = Path.Combine(_tempRoot, "target", appInstanceKey);
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(targetPath);
        var executableRelativePath = $"{serviceName}.exe";
        var sourceExecutablePath = Path.Combine(sourcePath, executableRelativePath);
        var targetExecutablePath = Path.Combine(targetPath, executableRelativePath);
        File.WriteAllText(sourceExecutablePath, string.Empty);
        File.WriteAllText(targetExecutablePath, string.Empty);

        var deployment = new ServiceAppDeploymentDescriptor
        {
            HostId = Guid.NewGuid(),
            HostKey = "test-host",
            AppInstanceId = appInstanceId,
            AppInstanceKey = appInstanceKey,
            ModuleInstanceKey = $"module-{appInstanceId:N}",
            DisplayName = serviceName,
            ArtifactId = 42,
            Version = "1.0.0",
            SourceLocalPath = sourcePath,
            InstallPath = targetPath,
            DeployedArtifactId = 42,
            DeploymentState = HostDeploymentStatuses.Succeeded,
            DeployedSourceLocalPath = sourcePath,
            DeployedTargetPath = targetPath,
            DeployedRuntimeName = serviceName
        };

        return (deployment, targetPath);
    }

    private sealed class FakeWindowsServiceControl : IWindowsServiceControl
    {
        private readonly Dictionary<string, string> _states = new(StringComparer.OrdinalIgnoreCase);

        public List<string> StartAttempts { get; } = [];

        public bool StartChangesStateToRunning { get; set; } = true;

        public void SetState(string serviceName, string state)
            => _states[serviceName] = state;

        public string? GetServiceState(string serviceName)
            => _states.TryGetValue(serviceName, out var state) ? state : null;

        public bool IsServiceRunning(string serviceName)
            => string.Equals(GetServiceState(serviceName), "RUNNING", StringComparison.OrdinalIgnoreCase);

        public void StartServiceIfStopped(string serviceName, int timeoutSeconds)
        {
            StartAttempts.Add(serviceName);
            if (GetServiceState(serviceName) is null)
            {
                throw new InvalidOperationException($"Windows service '{serviceName}' was not found.");
            }

            if (StartChangesStateToRunning)
            {
                _states[serviceName] = "RUNNING";
            }
        }

        public bool StopServiceIfRunning(string serviceName, int timeoutSeconds)
        {
            if (!IsServiceRunning(serviceName))
            {
                return false;
            }

            _states[serviceName] = "STOPPED";
            return true;
        }

        public void EnsureServiceConfigured(
            string serviceName,
            string executablePath,
            string displayName,
            string description)
        {
            if (GetServiceState(serviceName) is null)
            {
                _states[serviceName] = "STOPPED";
            }
        }
    }
}
