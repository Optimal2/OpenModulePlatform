using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class WebAppDeploymentServiceTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_WhenDeploySetWarningExists_AttachesWarningToDiagnosticWarningMessage()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out var moduleInstanceKey);
        repository.DesiredWebAppDeployments.Add(descriptor);

        var deploySetWarnings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [moduleInstanceKey] = "Deploy-set inconsistency in set 'default': versions differ."
        };

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, deploySetWarnings, CancellationToken.None);

        var published = repository.PublishedWebAppResults;
        Assert.NotEmpty(published);
        var last = published.Last();
        Assert.Contains("Deploy-set inconsistency", last.Result.DiagnosticWarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeployDesiredWebAppsAsync_WhenNoDeploySetWarning_LeavesDiagnosticWarningMessageClean()
    {
        var (service, repository, _) = CreateServiceWithFakeRepository();
        var descriptor = CreateWebAppDeploymentDescriptor(out _);
        repository.DesiredWebAppDeployments.Add(descriptor);

        await service.DeployDesiredWebAppsAsync(descriptor.HostKey, CancellationToken.None);

        var published = repository.PublishedWebAppResults;
        Assert.NotEmpty(published);
        var last = published.Last();
        Assert.Null(last.Result.DiagnosticWarningMessage);
    }

    private (WebAppDeploymentService Service, FakeOmpHostArtifactRepository Repository, HostAgentSettings Settings) CreateServiceWithFakeRepository()
    {
        var repository = new FakeOmpHostArtifactRepository();
        var settings = CreateTestSettings();
        var optionsMonitor = new FakeOptionsMonitor<HostAgentSettings> { CurrentValue = settings };

        var service = new WebAppDeploymentService(
            optionsMonitor,
            repository,
            credentialStore: null!,
            NullLogger<WebAppDeploymentService>.Instance);

        return (service, repository, settings);
    }

    private HostAgentSettings CreateTestSettings()
    {
        var tempRoot = CreateTempDirectory();
        var webAppsRoot = Path.Combine(tempRoot, "webapps");
        var portalPath = Path.Combine(tempRoot, "portal");
        Directory.CreateDirectory(webAppsRoot);
        Directory.CreateDirectory(portalPath);

        return new HostAgentSettings
        {
            DeployWebApps = true,
            CentralArtifactRoot = Path.Combine(tempRoot, "central"),
            LocalArtifactCacheRoot = Path.Combine(tempRoot, "cache"),
            IisSiteName = "TestSite",
            EnsureIisSite = false,
            WebAppsRoot = webAppsRoot,
            PortalPhysicalPath = portalPath,
            UseAppOfflineForWebAppDeployment = true,
            StopIisAppPoolForWebAppDeployment = false,
            StartIisAppPoolAfterWebAppDeployment = false,
            AppOfflineShutdownDelayMilliseconds = 0,
            WebAppDataProtectionKeyPath = @"\\localhost\omp-dataprotection",
            MaxArtifactsPerCycle = 10,
            PortalHealthCheck = new HostAgentPortalHealthCheckSettings { Enabled = false }
        };
    }

    private WebAppDeploymentDescriptor CreateWebAppDeploymentDescriptor(out string moduleInstanceKey)
    {
        moduleInstanceKey = "test-module-instance";
        var tempRoot = CreateTempDirectory();
        var sourcePath = Path.Combine(tempRoot, "source");
        var targetPath = Path.Combine(tempRoot, "target");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(targetPath);
        File.WriteAllText(Path.Combine(sourcePath, "app.txt"), "content");

        return new WebAppDeploymentDescriptor
        {
            HostId = Guid.NewGuid(),
            HostKey = "test-host",
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "test-app",
            ModuleInstanceKey = moduleInstanceKey,
            DisplayName = "Test App",
            ArtifactId = 1,
            Version = "1.0.0",
            SourceLocalPath = sourcePath,
            InstallPath = targetPath,
            DeploymentState = HostDeploymentStatuses.Succeeded,
            DeployedArtifactId = 1,
            DeployedSourceLocalPath = sourcePath,
            DeployedTargetPath = targetPath,
            DeployedRuntimeName = "TestSite/"
        };
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"OmpWebAppDeployTests-{Guid.NewGuid():N}");
        _tempPaths.Add(path);
        return path;
    }
}
