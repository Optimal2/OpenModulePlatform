using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using ServiceAppServiceCandidate = OpenModulePlatform.HostAgent.Runtime.Services.HostAgentJobProcessor.ServiceAppServiceCandidate;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class OrphanServiceAppDirectoryCleanupTests
{
    [Fact]
    public void ValidateCleanup_AllowsUnownedOrphanDirectory()
    {
        using var root = new TempDirectory();
        var orphan = CreateSubDirectory(root.Path, "OMP.iKrock2.Backend");

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            CreateSettings(root.Path),
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            Array.Empty<ServiceAppServiceCandidate>(),
            orphan);

        Assert.Null(refusal);
    }

    [Fact]
    public void ValidateCleanup_RefusesDirectoryOutsideServicesRoot()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var outsideDirectory = CreateSubDirectory(outside.Path, "OMP.iKrock2.Backend");

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            CreateSettings(root.Path),
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            Array.Empty<ServiceAppServiceCandidate>(),
            outsideDirectory);

        Assert.NotNull(refusal);
        Assert.Contains("services root", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCleanup_RefusesServicesRootItself()
    {
        using var root = new TempDirectory();

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            CreateSettings(root.Path),
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            Array.Empty<ServiceAppServiceCandidate>(),
            root.Path);

        Assert.NotNull(refusal);
    }

    [Fact]
    public void ValidateCleanup_RefusesDirectoryOwnedByActiveAppInstance()
    {
        using var root = new TempDirectory();
        var activePath = CreateSubDirectory(root.Path, "MyActiveService");

        var deployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "active-instance",
            InstallationName = "MyActiveService",
            DeployedTargetPath = activePath
        };

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            CreateSettings(root.Path),
            new[] { deployment },
            Array.Empty<ServiceAppServiceCandidate>(),
            activePath);

        Assert.NotNull(refusal);
        Assert.Contains("active-instance", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCleanup_RefusesDirectoryReferencedByRunningService()
    {
        using var root = new TempDirectory();
        var orphan = CreateSubDirectory(root.Path, "OMP.iKrock2.Backend");

        var candidates = new[]
        {
            new ServiceAppServiceCandidate(
                "OMP.iKrock2.Backend",
                "RUNNING",
                Path.Combine(orphan, "iKrock2.Backend.exe"),
                "OMP iKrock Backend")
        };

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            CreateSettings(root.Path),
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            candidates,
            orphan);

        Assert.NotNull(refusal);
        Assert.Contains("OMP.iKrock2.Backend", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCleanup_RefusesDirectoryReferencedByStoppedService()
    {
        using var root = new TempDirectory();
        var orphan = CreateSubDirectory(root.Path, "OrphanApp");

        var candidates = new[]
        {
            new ServiceAppServiceCandidate(
                "OrphanApp",
                "STOPPED",
                Path.Combine(orphan, "OrphanApp.exe"),
                "Orphan App")
        };

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            CreateSettings(root.Path),
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            candidates,
            orphan);

        Assert.NotNull(refusal);
    }

    [Fact]
    public void ValidateCleanup_RefusesHostAgentInstallDirectory()
    {
        using var root = new TempDirectory();
        var hostAgentDirectory = CreateSubDirectory(root.Path, "HostAgent");

        var settings = CreateSettings(root.Path);
        settings.SelfUpgrade.InstallRoot = hostAgentDirectory;

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            Array.Empty<ServiceAppServiceCandidate>(),
            hostAgentDirectory);

        Assert.NotNull(refusal);
        Assert.Contains("HostAgent install", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("OMP.WorkerManager")]
    [InlineData("EMP.WorkerManager.v2")]
    public void ValidateCleanup_RefusesWorkerManagerDirectory(string folderName)
    {
        using var root = new TempDirectory();
        var workerManagerDirectory = CreateSubDirectory(root.Path, folderName);

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            CreateSettings(root.Path),
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            Array.Empty<ServiceAppServiceCandidate>(),
            workerManagerDirectory);

        Assert.NotNull(refusal);
    }

    [Fact]
    public void ValidateCleanup_RefusesActiveHostAgentServiceNameDirectory()
    {
        using var root = new TempDirectory();
        var directory = CreateSubDirectory(root.Path, "OMP.HostAgent");

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            CreateSettings(root.Path),
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            Array.Empty<ServiceAppServiceCandidate>(),
            directory);

        Assert.NotNull(refusal);
    }

    [Fact]
    public void ValidateCleanup_RefusesWhenServicesRootNotConfigured()
    {
        using var root = new TempDirectory();
        var orphan = CreateSubDirectory(root.Path, "OrphanApp");

        var refusal = HostAgentJobProcessor.ValidateOrphanServiceAppDirectoryCleanup(
            CreateSettings(string.Empty),
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            Array.Empty<ServiceAppServiceCandidate>(),
            orphan);

        Assert.NotNull(refusal);
    }

    private static string CreateSubDirectory(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static HostAgentSettings CreateSettings(string servicesRoot)
    {
        return new HostAgentSettings
        {
            ServicesRoot = servicesRoot,
            ServiceName = "OMP.HostAgent"
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"omp-orphan-cleanup-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
