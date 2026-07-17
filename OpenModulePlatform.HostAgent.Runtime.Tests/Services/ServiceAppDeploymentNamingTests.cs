using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ServiceAppDeploymentNamingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    [InlineData("service")]
    [InlineData("Service")]
    [InlineData("serviceapp")]
    [InlineData("ServiceApp")]
    [InlineData("backend")]
    [InlineData("BACKEND")]
    [InlineData("worker")]
    [InlineData("Worker")]
    [InlineData("app")]
    [InlineData("APP")]
    public void IsGenericInstallationName_True_ForGenericNames(string? value)
    {
        Assert.True(ServiceAppDeploymentNaming.IsGenericInstallationName(value));
    }

    [Theory]
    [InlineData("OMP.iKrock2.Backend")]
    [InlineData("MyCustomService")]
    [InlineData("omp-backend")]
    [InlineData("iKrock2.Backend")]
    [InlineData("defaulted")]
    [InlineData("serviceable")]
    [InlineData("application")]
    public void IsGenericInstallationName_False_ForIntentionalNames(string value)
    {
        Assert.False(ServiceAppDeploymentNaming.IsGenericInstallationName(value));
    }

    [Theory]
    [InlineData("backend", "MyApp.exe", "MyApp")]
    [InlineData("service", "OMP.MyApp.exe", "OMP.MyApp")]
    [InlineData("app", "SomeExe.exe", "SomeExe")]
    [InlineData("", "MyApp.exe", "MyApp")]
    [InlineData(null, "MyApp.exe", "MyApp")]
    public void ResolveServiceName_FallsBackToExeName_ForGenericNames(
        string? installationName,
        string executableRelativePath,
        string expected)
    {
        var deployment = CreateDeployment(installationName: installationName);

        var actual = ServiceAppDeploymentNaming.ResolveServiceName(deployment, executableRelativePath);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("OMP.iKrock2.Backend")]
    [InlineData("MyCustomService")]
    public void ResolveServiceName_UsesConfiguredName_ForNonGenericNames(string installationName)
    {
        var deployment = CreateDeployment(installationName: installationName);

        var actual = ServiceAppDeploymentNaming.ResolveServiceName(deployment, "Whatever.exe");

        Assert.Equal(installationName, actual);
    }

    [Fact]
    public void ResolveServiceName_Throws_ForInvalidServiceName()
    {
        var deployment = CreateDeployment(installationName: "Bad/Name");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ServiceAppDeploymentNaming.ResolveServiceName(deployment, "Whatever.exe"));

        Assert.Contains("invalid Windows service name", ex.Message);
    }

    [Fact]
    public void ResolveTargetPath_UsesAbsoluteInstallPath_WhenRooted()
    {
        var settings = CreateSettings(servicesRoot: "E:\\OMP\\Services");
        var deployment = CreateDeployment(installPath: "D:\\Custom\\Path");

        var actual = ServiceAppDeploymentNaming.ResolveTargetPath(settings, deployment, "MyService");

        Assert.Equal("D:\\Custom\\Path", actual);
    }

    [Fact]
    public void ResolveTargetPath_UsesRelativeInstallPath_WhenNotRooted()
    {
        var settings = CreateSettings(servicesRoot: "E:\\OMP\\Services");
        var deployment = CreateDeployment(installPath: "CustomSub");

        var actual = ServiceAppDeploymentNaming.ResolveTargetPath(settings, deployment, "MyService");

        Assert.Equal("E:\\OMP\\Services\\CustomSub", actual);
    }

    [Fact]
    public void ResolveTargetPath_UsesServiceNameFolder_WhenNoInstallPath()
    {
        var settings = CreateSettings(servicesRoot: "E:\\OMP\\Services");
        var deployment = CreateDeployment();

        var actual = ServiceAppDeploymentNaming.ResolveTargetPath(settings, deployment, "MyService");

        Assert.Equal("E:\\OMP\\Services\\MyService", actual);
    }

    [Fact]
    public void EvaluateRenameCleanup_Triggers_WhenDeployedRuntimeNameDiffers()
    {
        var settings = CreateSettings(servicesRoot: "E:\\OMP\\Services");
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: "backend");
        var resolved = new Dictionary<Guid, string>
        {
            [deployment.AppInstanceId] = "OMP.iKrock2.Backend"
        };

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            resolved);

        Assert.True(result.ShouldCleanUp);
        Assert.Equal("backend", result.OldServiceName);
        Assert.Equal("E:\\OMP\\Services\\backend", result.OldTargetPath);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void EvaluateRenameCleanup_Skips_WhenNoDeployedRuntimeNameTracked()
    {
        var settings = CreateSettings();
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installationName: "OMP.iKrock2.Backend");
        var resolved = new Dictionary<Guid, string>
        {
            [deployment.AppInstanceId] = "OMP.iKrock2.Backend"
        };

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            resolved);

        Assert.False(result.ShouldCleanUp);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void EvaluateRenameCleanup_Skips_WhenNamesMatch()
    {
        var settings = CreateSettings();
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: "OMP.iKrock2.Backend");
        var resolved = new Dictionary<Guid, string>
        {
            [deployment.AppInstanceId] = "OMP.iKrock2.Backend"
        };

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            resolved);

        Assert.False(result.ShouldCleanUp);
        Assert.Equal("OMP.iKrock2.Backend", result.OldServiceName);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void EvaluateRenameCleanup_Skips_WhenAnotherActiveInstanceUsesOldName()
    {
        var settings = CreateSettings();
        var thisId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var deployment = CreateDeployment(
            appInstanceId: thisId,
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: "backend");
        var resolved = new Dictionary<Guid, string>
        {
            [thisId] = "OMP.iKrock2.Backend",
            [otherId] = "backend"
        };

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            resolved);

        Assert.False(result.ShouldCleanUp);
        Assert.Equal("backend", result.OldServiceName);
        Assert.NotNull(result.Reason);
        Assert.Contains("Another active app instance", result.Reason);
    }

    [Theory]
    [InlineData("OMP.HostAgent")]
    [InlineData("omp-hostagent")]
    public void EvaluateRenameCleanup_Skips_WhenOldNameMatchesHostAgentService(string hostAgentServiceName)
    {
        var settings = CreateSettings(serviceName: hostAgentServiceName);
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: hostAgentServiceName);
        var resolved = new Dictionary<Guid, string>
        {
            [deployment.AppInstanceId] = "OMP.iKrock2.Backend"
        };

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            resolved);

        Assert.False(result.ShouldCleanUp);
        Assert.Equal(hostAgentServiceName, result.OldServiceName);
        Assert.NotNull(result.Reason);
        Assert.Contains("HostAgent service name", result.Reason);
    }

    [Fact]
    public void EvaluateRenameCleanup_Skips_WhenOldNameMatchesWorkerManagerService()
    {
        var settings = CreateSettings();
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: "OMP.WorkerManager");
        var resolved = new Dictionary<Guid, string>
        {
            [deployment.AppInstanceId] = "OMP.iKrock2.Backend"
        };

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            resolved);

        Assert.False(result.ShouldCleanUp);
        Assert.Equal("OMP.WorkerManager", result.OldServiceName);
        Assert.NotNull(result.Reason);
        Assert.Contains("WorkerManager service name", result.Reason);
    }

    [Theory]
    // Twin matches the canonical executable name (legacy generic-resolution name).
    [InlineData("iKrock2.Backend", "OMP.iKrock2.Backend", "E:\\OMP\\Services\\OMP.iKrock2.Backend\\iKrock2.Backend.exe", true)]
    // Twin is the canonical name without its first prefix segment.
    [InlineData("iKrock2.Backend", "OMP.iKrock2.Backend", null, true)]
    [InlineData("backend", "OMP.backend", "D:\\apps\\backend\\backend.exe", true)]
    // Same name is never a twin.
    [InlineData("OMP.iKrock2.Backend", "OMP.iKrock2.Backend", "E:\\x\\iKrock2.Backend.exe", false)]
    // Unrelated name with no naming relationship.
    [InlineData("SomeOtherService", "OMP.iKrock2.Backend", "E:\\x\\iKrock2.Backend.exe", false)]
    [InlineData("iKrock2.Backend", "OMP.OtherApp.Backend", null, false)]
    public void IsLegacyTwinServiceName_MatchesExpected(
        string candidate,
        string canonical,
        string? canonicalExecutablePath,
        bool expected)
    {
        Assert.Equal(
            expected,
            ServiceAppDeploymentNaming.IsLegacyTwinServiceName(candidate, canonical, canonicalExecutablePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsLegacyTwinServiceName_False_ForBlankCandidate(string? candidate)
    {
        Assert.False(ServiceAppDeploymentNaming.IsLegacyTwinServiceName(
            candidate!,
            "OMP.iKrock2.Backend",
            "E:\\x\\iKrock2.Backend.exe"));
    }

    private static ServiceAppDeploymentDescriptor CreateDeployment(
        Guid? appInstanceId = null,
        string? installationName = null,
        string? installPath = null,
        string? deployedRuntimeName = null)
    {
        return new ServiceAppDeploymentDescriptor
        {
            AppInstanceId = appInstanceId ?? Guid.NewGuid(),
            AppInstanceKey = "test-instance",
            InstallationName = installationName,
            InstallPath = installPath,
            DeployedRuntimeName = deployedRuntimeName
        };
    }

    private static HostAgentSettings CreateSettings(
        string servicesRoot = "E:\\OMP\\Services",
        string serviceName = "OMP.HostAgent")
    {
        return new HostAgentSettings
        {
            ServicesRoot = servicesRoot,
            ServiceName = serviceName
        };
    }
}
