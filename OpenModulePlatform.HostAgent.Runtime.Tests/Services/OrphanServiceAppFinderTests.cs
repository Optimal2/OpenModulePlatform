using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class OrphanServiceAppFinderTests
{
    [Fact]
    public void BuildOrphanServiceAppFindings_FlagsOrphanDirectory()
    {
        using var root = new TempServicesRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "OrphanApp"));

        var settings = CreateSettings(root.Path);
        var hostId = Guid.NewGuid();

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindings(
            hostId,
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => null,
            CancellationToken.None);

        Assert.Single(findings);
        var finding = findings[0];
        Assert.Equal("OrphanServiceApp", finding.Category);
        Assert.Equal(MaintenanceTargetKinds.Directory, finding.TargetKind);
        Assert.Equal(2, finding.Severity);
        Assert.Equal((byte)80, finding.Confidence);
        Assert.Contains("OrphanApp", finding.TargetIdentifier, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_SkipsActiveAppInstanceDirectory()
    {
        using var root = new TempServicesRoot();
        var activePath = Path.Combine(root.Path, "MyActiveService");
        Directory.CreateDirectory(activePath);

        var deployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "active-instance",
            InstallationName = "MyActiveService",
            DeployedTargetPath = activePath
        };

        var settings = CreateSettings(root.Path);

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindings(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { deployment },
            _ => null,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_SkipsHostAgentInstallDirectory()
    {
        using var root = new TempServicesRoot();
        var hostAgentDirectory = Path.Combine(root.Path, "HostAgent");
        Directory.CreateDirectory(hostAgentDirectory);

        var settings = CreateSettings(root.Path);
        settings.SelfUpgrade.InstallRoot = hostAgentDirectory;

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindings(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => null,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_MultipleOrphansWithAndWithoutService()
    {
        using var root = new TempServicesRoot();
        var orphanA = Path.Combine(root.Path, "OrphanA");
        var orphanB = Path.Combine(root.Path, "OrphanB");
        Directory.CreateDirectory(orphanA);
        Directory.CreateDirectory(orphanB);

        var settings = CreateSettings(root.Path);
        var hostId = Guid.NewGuid();

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindings(
            hostId,
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            serviceName => serviceName.Equals("OrphanA", StringComparison.OrdinalIgnoreCase)
                ? ("STOPPED", Path.Combine(orphanA, "OrphanA.exe"))
                : null,
            CancellationToken.None);

        Assert.Equal(3, findings.Count);

        var directoryA = Assert.Single(findings, f =>
            f.TargetKind == MaintenanceTargetKinds.Directory
            && f.TargetIdentifier.Contains("OrphanA", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((byte)90, directoryA.Confidence);

        var serviceA = Assert.Single(findings, f =>
            f.TargetKind == MaintenanceTargetKinds.WindowsService
            && f.TargetIdentifier.Contains("OrphanA", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((byte)90, serviceA.Confidence);

        var directoryB = Assert.Single(findings, f =>
            f.TargetKind == MaintenanceTargetKinds.Directory
            && f.TargetIdentifier.Contains("OrphanB", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((byte)80, directoryB.Confidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildOrphanServiceAppFindings_EmptyServicesRoot_ReturnsNoFindings(string? servicesRoot)
    {
        var settings = CreateSettings(servicesRoot ?? string.Empty);

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindings(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => null,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_RespectsCustomRelativeInstallPath()
    {
        using var root = new TempServicesRoot();
        var customPath = Path.Combine(root.Path, "CustomSub");
        Directory.CreateDirectory(customPath);

        var deployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "custom-path-instance",
            InstallationName = "MyCustomService",
            InstallPath = "CustomSub"
        };

        var settings = CreateSettings(root.Path);

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindings(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { deployment },
            _ => null,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_SkipsRunningServiceUnderOrphanDirectory()
    {
        using var root = new TempServicesRoot();
        var orphanDirectory = Path.Combine(root.Path, "OrphanWithRunningService");
        Directory.CreateDirectory(orphanDirectory);

        var settings = CreateSettings(root.Path);

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindings(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => ("RUNNING", Path.Combine(orphanDirectory, "OrphanWithRunningService.exe")),
            CancellationToken.None);

        Assert.Empty(findings);
    }

    private static HostAgentSettings CreateSettings(string servicesRoot)
    {
        return new HostAgentSettings
        {
            ServicesRoot = servicesRoot,
            ServiceName = "OMP.HostAgent"
        };
    }

    private sealed class TempServicesRoot : IDisposable
    {
        public string Path { get; }

        public TempServicesRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"omp-orphan-test-{Guid.NewGuid():N}");
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
