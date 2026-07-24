using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using ServiceAppServiceCandidate = OpenModulePlatform.HostAgent.Runtime.Services.HostAgentJobProcessor.ServiceAppServiceCandidate;

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

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
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

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
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

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => null,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_SkipsLiveHostAgentVersionDirectoryUnderServicesRoot()
    {
        // Regression: with the default layout ServicesRoot == InstallRoot, the currently
        // running HostAgent-<version> install directory sits directly below ServicesRoot.
        // The orphan sweep must never flag it for deletion.
        using var root = new TempServicesRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "HostAgent-0.3.161"));

        var settings = CreateSettings(root.Path);

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => null,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_StillFlagsRealOrphanNextToHostAgentDirectory()
    {
        // Positive guard: exempting HostAgent install directories must not silence
        // legitimate orphan findings in the same services root.
        using var root = new TempServicesRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "HostAgent-0.3.161"));
        Directory.CreateDirectory(Path.Combine(root.Path, "OrphanApp"));

        var settings = CreateSettings(root.Path);

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => null,
            CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal("OrphanServiceApp", finding.Category);
        Assert.Contains("OrphanApp", finding.TargetIdentifier, StringComparison.OrdinalIgnoreCase);
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
        var candidates = new[]
        {
            new ServiceAppServiceCandidate("OrphanA", "STOPPED", Path.Combine(orphanA, "OrphanA.exe"), "Orphan A")
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            hostId,
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            serviceName => serviceName.Equals("OrphanA", StringComparison.OrdinalIgnoreCase)
                ? ("STOPPED", Path.Combine(orphanA, "OrphanA.exe"))
                : null,
            candidates,
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

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
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

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { deployment },
            _ => null,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_RunningServiceWithoutOwner_IsFlaggedByServiceSweep()
    {
        using var root = new TempServicesRoot();
        var orphanDirectory = Path.Combine(root.Path, "OrphanWithRunningService");
        Directory.CreateDirectory(orphanDirectory);

        var settings = CreateSettings(root.Path);
        var candidates = new[]
        {
            new ServiceAppServiceCandidate(
                "OrphanWithRunningService",
                "RUNNING",
                Path.Combine(orphanDirectory, "OrphanWithRunningService.exe"),
                "Orphan With Running Service")
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => ("RUNNING", Path.Combine(orphanDirectory, "OrphanWithRunningService.exe")),
            candidates,
            CancellationToken.None);

        var serviceFinding = Assert.Single(findings);
        Assert.Equal(MaintenanceTargetKinds.WindowsService, serviceFinding.TargetKind);
        Assert.Contains("OrphanWithRunningService", serviceFinding.TargetIdentifier, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((byte)95, serviceFinding.Confidence);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_SkipsWorkerManagerDirectory()
    {
        using var root = new TempServicesRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "OMP.WorkerManager"));
        Directory.CreateDirectory(Path.Combine(root.Path, "EMP.WorkerManager.v2"));

        var settings = CreateSettings(root.Path);

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => null,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_SkipsHostAgentServiceNameDirectory()
    {
        using var root = new TempServicesRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "OMP.HostAgent"));

        var settings = CreateSettings(root.Path);

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => null,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_AbsoluteInstallPathOutsideServicesRoot_FlagsSameNamedFolderUnderServicesRoot()
    {
        using var servicesRoot = new TempServicesRoot();
        using var externalRoot = new TempServicesRoot();
        var externalActivePath = Path.Combine(externalRoot.Path, "ActiveCustom");
        var orphanSameNamePath = Path.Combine(servicesRoot.Path, "ActiveCustom");
        Directory.CreateDirectory(externalActivePath);
        Directory.CreateDirectory(orphanSameNamePath);

        var deployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "absolute-path-instance",
            InstallationName = "ActiveCustom",
            InstallPath = externalActivePath
        };

        var settings = CreateSettings(servicesRoot.Path);

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { deployment },
            _ => null,
            CancellationToken.None);

        var directoryFinding = Assert.Single(findings);
        Assert.Equal(MaintenanceTargetKinds.Directory, directoryFinding.TargetKind);
        Assert.Equal(orphanSameNamePath, directoryFinding.TargetIdentifier, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_ServiceSweep_FlagsStoppedOwnerlessServiceWithoutDirectory()
    {
        using var root = new TempServicesRoot();
        // The service executable path is reported as under the services root, but the
        // directory itself has already been removed. The sweep must still flag the service.
        var executablePath = Path.Combine(root.Path, "OrphanStopped", "OrphanStopped.exe");

        var settings = CreateSettings(root.Path);
        var candidates = new[]
        {
            new ServiceAppServiceCandidate("OrphanStopped", "STOPPED", executablePath, "Orphan Stopped")
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            Array.Empty<ServiceAppDeploymentDescriptor>(),
            _ => null,
            candidates,
            CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal(MaintenanceTargetKinds.WindowsService, finding.TargetKind);
        Assert.Equal("OrphanStopped", finding.TargetIdentifier, StringComparer.OrdinalIgnoreCase);
        Assert.Equal((byte)90, finding.Confidence);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_ServiceSweep_SkipsClaimedService()
    {
        using var root = new TempServicesRoot();
        var activePath = Path.Combine(root.Path, "MyActiveService");
        Directory.CreateDirectory(activePath);

        var deployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "active-instance",
            InstallationName = "MyActiveService",
            DeployedRuntimeName = "MyActiveService",
            DeployedTargetPath = activePath
        };

        var settings = CreateSettings(root.Path);
        var candidates = new[]
        {
            new ServiceAppServiceCandidate(
                "MyActiveService",
                "RUNNING",
                Path.Combine(activePath, "MyActiveService.exe"),
                "My Active Service")
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { deployment },
            _ => null,
            candidates,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_ServiceSweep_SkipsServiceInActiveTargetDirectory()
    {
        using var root = new TempServicesRoot();
        var activePath = Path.Combine(root.Path, "GenericExeService");
        Directory.CreateDirectory(activePath);

        var deployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "generic-instance",
            InstallationName = "service",
            DeployedTargetPath = activePath
        };

        var settings = CreateSettings(root.Path);
        var candidates = new[]
        {
            new ServiceAppServiceCandidate(
                "GenericExeService",
                "RUNNING",
                Path.Combine(activePath, "GenericExeService.exe"),
                "Generic Exe Service")
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { deployment },
            _ => null,
            candidates,
            CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_ServiceSweep_FlagsDuplicateDisplayNameUnclaimed()
    {
        using var root = new TempServicesRoot();
        var path = Path.Combine(root.Path, "SomeService");
        Directory.CreateDirectory(path);

        var settings = CreateSettings(root.Path);
        var candidates = new[]
        {
            new ServiceAppServiceCandidate("OldService", "STOPPED", Path.Combine(path, "OldService.exe"), "OMP Duplicate Display"),
            new ServiceAppServiceCandidate("NewService", "RUNNING", Path.Combine(path, "NewService.exe"), "OMP Duplicate Display")
        };

        var deployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "new-instance",
            InstallationName = "NewService",
            DeployedRuntimeName = "NewService",
            DeployedTargetPath = path
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { deployment },
            _ => null,
            candidates,
            CancellationToken.None);

        var duplicateFinding = Assert.Single(findings);
        Assert.Equal(MaintenanceTargetKinds.WindowsService, duplicateFinding.TargetKind);
        Assert.Equal("OldService", duplicateFinding.TargetIdentifier, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("duplicate", duplicateFinding.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_ServiceSweep_FlagsLegacyTwinOutsideServicesRoot()
    {
        using var root = new TempServicesRoot();
        using var externalRoot = new TempServicesRoot();
        var canonicalPath = Path.Combine(root.Path, "OMP.iKrock2.Backend");
        var twinExePath = Path.Combine(externalRoot.Path, "iKrock2.Backend", "iKrock2.Backend.exe");

        var deployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "ikrock_backend",
            InstallationName = "OMP.iKrock2.Backend",
            ArtifactId = 42,
            DeployedRuntimeName = "OMP.iKrock2.Backend",
            DeployedTargetPath = canonicalPath
        };

        var settings = CreateSettings(root.Path);
        var candidates = new[]
        {
            new ServiceAppServiceCandidate(
                "OMP.iKrock2.Backend",
                "RUNNING",
                Path.Combine(canonicalPath, "iKrock2.Backend.exe"),
                "OMP iKrock Backend"),
            new ServiceAppServiceCandidate(
                "iKrock2.Backend",
                "RUNNING",
                twinExePath,
                "OMP DESKTOP-TEST iKrock Backend",
                IsUnderServicesRoot: false)
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { deployment },
            _ => null,
            candidates,
            CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal(MaintenanceTargetKinds.WindowsService, finding.TargetKind);
        Assert.Equal("iKrock2.Backend", finding.TargetIdentifier, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("duplicate-twin", finding.FindingKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((byte)95, finding.Confidence);
        Assert.Contains("canonicalServiceName", finding.ActionJson, StringComparison.Ordinal);
        Assert.Contains("OMP.iKrock2.Backend", finding.ActionJson, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_ServiceSweep_FlagsUnclaimedLegacyTwinUnderServicesRootOnlyOnce()
    {
        using var root = new TempServicesRoot();
        var canonicalPath = Path.Combine(root.Path, "OMP.iKrock2.Backend");
        var twinPath = Path.Combine(root.Path, "iKrock2.Backend");

        var deployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "ikrock_backend",
            InstallationName = "OMP.iKrock2.Backend",
            ArtifactId = 42,
            DeployedRuntimeName = "OMP.iKrock2.Backend",
            DeployedTargetPath = canonicalPath
        };

        var settings = CreateSettings(root.Path);
        var candidates = new[]
        {
            new ServiceAppServiceCandidate(
                "OMP.iKrock2.Backend",
                "RUNNING",
                Path.Combine(canonicalPath, "iKrock2.Backend.exe"),
                "OMP iKrock Backend"),
            new ServiceAppServiceCandidate(
                "iKrock2.Backend",
                "STOPPED",
                Path.Combine(twinPath, "iKrock2.Backend.exe"),
                "OMP iKrock Backend old")
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { deployment },
            _ => null,
            candidates,
            CancellationToken.None);

        // The twin must be reported exactly once, as a duplicate-twin finding, and the
        // canonical claimed service must never be flagged.
        var finding = Assert.Single(findings);
        Assert.Equal("iKrock2.Backend", finding.TargetIdentifier, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("duplicate-twin", finding.FindingKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(findings, f => f.TargetIdentifier.Contains("OMP.iKrock2.Backend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_ServiceSweep_FlagsTwinClaimedBySameAppDuplicateInstance()
    {
        using var root = new TempServicesRoot();
        var canonicalPath = Path.Combine(root.Path, "OMP.iKrock2.Backend");
        var twinPath = Path.Combine(root.Path, "iKrock2.Backend");

        var canonicalDeployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "ikrock_backend",
            InstallationName = "OMP.iKrock2.Backend",
            ArtifactId = 42,
            DeployedRuntimeName = "OMP.iKrock2.Backend",
            DeployedTargetPath = canonicalPath
        };
        var duplicateInstanceDeployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "ikrock_backend_desktop_test",
            InstallationName = "iKrock2.Backend",
            ArtifactId = 42,
            DeployedRuntimeName = "iKrock2.Backend",
            DeployedTargetPath = twinPath
        };

        var settings = CreateSettings(root.Path);
        var candidates = new[]
        {
            new ServiceAppServiceCandidate(
                "OMP.iKrock2.Backend",
                "STOPPED",
                Path.Combine(canonicalPath, "iKrock2.Backend.exe"),
                "OMP iKrock Backend"),
            new ServiceAppServiceCandidate(
                "iKrock2.Backend",
                "RUNNING",
                Path.Combine(twinPath, "iKrock2.Backend.exe"),
                "OMP DESKTOP-TEST iKrock Backend")
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { canonicalDeployment, duplicateInstanceDeployment },
            _ => null,
            candidates,
            CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal("iKrock2.Backend", finding.TargetIdentifier, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("duplicate-twin", finding.FindingKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((byte)80, finding.Confidence);
        Assert.Contains("ikrock_backend_desktop_test", finding.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Disable or remove the duplicate AppInstance", finding.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOrphanServiceAppFindings_ServiceSweep_SkipsTwinClaimedByDifferentApp()
    {
        using var root = new TempServicesRoot();
        var canonicalPath = Path.Combine(root.Path, "OMP.iKrock2.Backend");
        var twinPath = Path.Combine(root.Path, "iKrock2.Backend");

        var canonicalDeployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "ikrock_backend",
            InstallationName = "OMP.iKrock2.Backend",
            ArtifactId = 42,
            DeployedRuntimeName = "OMP.iKrock2.Backend",
            DeployedTargetPath = canonicalPath
        };
        var otherAppDeployment = new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "other_app_instance",
            InstallationName = "iKrock2.Backend",
            ArtifactId = 99,
            DeployedRuntimeName = "iKrock2.Backend",
            DeployedTargetPath = twinPath
        };

        var settings = CreateSettings(root.Path);
        var candidates = new[]
        {
            new ServiceAppServiceCandidate(
                "OMP.iKrock2.Backend",
                "RUNNING",
                Path.Combine(canonicalPath, "iKrock2.Backend.exe"),
                "OMP iKrock Backend"),
            new ServiceAppServiceCandidate(
                "iKrock2.Backend",
                "RUNNING",
                Path.Combine(twinPath, "iKrock2.Backend.exe"),
                "Other App Backend")
        };

        var findings = HostAgentJobProcessor.BuildOrphanServiceAppFindingsCore(
            Guid.NewGuid(),
            "TEST",
            settings,
            new[] { canonicalDeployment, otherAppDeployment },
            _ => null,
            candidates,
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
