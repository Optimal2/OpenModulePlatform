using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task RenameCleanup_DeleteFailure_RetriesNextCycle_And_PersistsOldRuntimeNameUntilCleaned()
    {
        var (service, repository, control, deployment, newTargetPath, oldTargetPath) = CreateRenameScenario();
        var oldServiceName = "backend";
        var newServiceName = "OMP.iKrock2.Backend";

        // Simulate a transient sc.exe delete failure on the first attempt.
        var deleteAttempts = 0;
        control.DeleteServiceSimulator = name =>
        {
            deleteAttempts++;
            return deleteAttempts == 1
                ? new InvalidOperationException($"Simulated failure deleting '{name}'.")
                : null;
        };

        // First cycle: cleanup fails -> abort, RuntimeName stays OLD.
        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);

        Assert.Equal(2, repository.PublishedServiceAppResults.Count);
        var runningResult = repository.PublishedServiceAppResults[0].Result;
        var warningResult = repository.PublishedServiceAppResults[1].Result;

        Assert.Equal(HostDeploymentStatuses.Running, runningResult.State);
        Assert.Equal(oldServiceName, runningResult.RuntimeName);
        Assert.Equal(HostDeploymentStatuses.Warning, warningResult.State);
        Assert.Equal(oldServiceName, warningResult.RuntimeName);
        Assert.Contains("retry on the next deployment cycle", warningResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, deleteAttempts);
        Assert.Empty(control.DeletedServices);
        Assert.True(Directory.Exists(oldTargetPath));

        repository.PublishedServiceAppResults.Clear();

        // Second cycle: cleanup succeeds -> deployment proceeds, RuntimeName advances to NEW.
        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);

        Assert.Equal(2, repository.PublishedServiceAppResults.Count);
        var secondRunningResult = repository.PublishedServiceAppResults[0].Result;
        var succeededResult = repository.PublishedServiceAppResults[1].Result;

        Assert.Equal(HostDeploymentStatuses.Running, secondRunningResult.State);
        Assert.Equal(oldServiceName, secondRunningResult.RuntimeName);
        Assert.Equal(HostDeploymentStatuses.Succeeded, succeededResult.State);
        Assert.Equal(newServiceName, succeededResult.RuntimeName);
        Assert.Equal(2, deleteAttempts);
        Assert.Single(control.DeletedServices, oldServiceName);
        Assert.False(Directory.Exists(oldTargetPath));
        Assert.True(Directory.Exists(newTargetPath));
    }

    [Fact]
    public async Task NonRename_NoOldRuntimeNameChange_NoCleanupAttempted()
    {
        var (service, repository, control, deployment, targetPath) = CreateScenario();
        control.SetState(deployment.DeployedRuntimeName!, "RUNNING");

        await service.DeployDesiredServiceAppsAsync("test-host", CancellationToken.None);

        Assert.Single(repository.PublishedServiceAppResults);
        var result = repository.PublishedServiceAppResults[0].Result;
        Assert.Equal(HostDeploymentStatuses.Succeeded, result.State);
        Assert.Equal(deployment.DeployedRuntimeName, result.RuntimeName);
        Assert.Empty(control.DeletedServices);
    }

    private (ServiceAppDeploymentService Service, FakeOmpHostArtifactRepository Repository, FakeWindowsServiceControl Control, ServiceAppDeploymentDescriptor Deployment, string TargetPath, string OldTargetPath) CreateRenameScenario()
    {
        var settings = new HostAgentSettings
        {
            DeployServiceApps = true,
            CentralArtifactRoot = _tempRoot,
            LocalArtifactCacheRoot = _tempRoot,
            ServicesRoot = _tempRoot,
            StartServiceAfterServiceAppDeployment = true,
            StopServiceForServiceAppDeployment = true,
            ServiceAppStopTimeoutSeconds = 1,
            ServiceAppStartTimeoutSeconds = 1
        };
        var optionsMonitor = new FakeOptionsMonitor<HostAgentSettings> { CurrentValue = settings };
        var repository = new FakeOmpHostArtifactRepository();
        var control = new FakeWindowsServiceControl();
        var credentialStore = new HostAgentCredentialStoreService(optionsMonitor);
        var service = new ServiceAppDeploymentService(
            optionsMonitor,
            repository,
            credentialStore,
            NullLogger<ServiceAppDeploymentService>.Instance,
            control);

        var (deployment, newTargetPath, oldTargetPath) = CreateRenameDeployment();
        repository.DesiredServiceAppDeployments.Add(deployment);

        // The old service exists in the SCM and must be deleted during rename cleanup.
        control.SetState("backend", "STOPPED");

        return (service, repository, control, deployment, newTargetPath, oldTargetPath);
    }

    private (ServiceAppDeploymentDescriptor Deployment, string NewTargetPath, string OldTargetPath) CreateRenameDeployment()
    {
        var appInstanceId = Guid.NewGuid();
        var appInstanceKey = $"test-app-{appInstanceId:N}";
        var oldServiceName = "backend";
        var newServiceName = "OMP.iKrock2.Backend";
        var executableRelativePath = "iKrock2.Backend.exe";
        var sourcePath = Path.Combine(_tempRoot, "source", appInstanceKey);
        var newTargetPath = Path.Combine(_tempRoot, newServiceName);
        var oldTargetPath = Path.Combine(_tempRoot, oldServiceName);
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(oldTargetPath);
        File.WriteAllText(Path.Combine(sourcePath, executableRelativePath), string.Empty);
        File.WriteAllText(Path.Combine(oldTargetPath, executableRelativePath), string.Empty);

        var deployment = new ServiceAppDeploymentDescriptor
        {
            HostId = Guid.NewGuid(),
            HostKey = "test-host",
            AppInstanceId = appInstanceId,
            AppInstanceKey = appInstanceKey,
            ModuleInstanceKey = $"module-{appInstanceId:N}",
            DisplayName = "OMP iKrock Backend",
            ArtifactId = 42,
            Version = "1.0.0",
            SourceLocalPath = sourcePath,
            InstallationName = newServiceName,
            DeployedArtifactId = 42,
            DeploymentState = HostDeploymentStatuses.Succeeded,
            DeployedSourceLocalPath = sourcePath,
            DeployedTargetPath = oldTargetPath,
            DeployedRuntimeName = oldServiceName
        };

        return (deployment, newTargetPath, oldTargetPath);
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
        var credentialStore = new HostAgentCredentialStoreService(optionsMonitor);
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

        public List<string> DeletedServices { get; } = [];

        public bool StartChangesStateToRunning { get; set; } = true;

        public Func<string, Exception?>? DeleteServiceSimulator { get; set; }

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

        public void DeleteService(string serviceName)
        {
            if (DeleteServiceSimulator is not null)
            {
                var simulated = DeleteServiceSimulator(serviceName);
                if (simulated is not null)
                {
                    throw simulated;
                }
            }

            if (_states.Remove(serviceName))
            {
                DeletedServices.Add(serviceName);
            }
        }
    }
}
