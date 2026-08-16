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
            @"E:\OMP\Services\OMP.iKrock2.Backend",
            resolved,
            []);

        Assert.True(result.ShouldRemoveOldService);
        Assert.True(result.ShouldDeleteOldDirectory);
        Assert.Equal("backend", result.OldServiceName);
        Assert.Equal("E:\\OMP\\Services\\backend", result.OldTargetPath);
        Assert.Null(result.ServiceSkipReason);
        Assert.Null(result.DirectorySkipReason);
    }

    /// <summary>
    /// The name-collision guard sees app instances the deployment cycle never loaded.
    /// </summary>
    /// <remarks>
    /// The old guard only consulted the names resolved during this cycle, a set capped by
    /// MaxArtifactsPerCycle and further thinned by the pre-pass catch. An app instance
    /// that fell outside it was invisible, and its service and folder were deletable
    /// (R7-D4). The dictionary here is deliberately empty, exactly as it would be for an
    /// instance that did not fit in the cycle.
    /// </remarks>
    [Fact]
    public void EvaluateRenameCleanup_Skips_WhenAnotherHostFootprintUsesTheOldRuntimeName()
    {
        var settings = CreateSettings(servicesRoot: "E:\\OMP\\Services");
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: "backend");

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            @"E:\OMP\Services\OMP.iKrock2.Backend",
            new Dictionary<Guid, string>(),
            [new HostRuntimeFootprint(Guid.NewGuid(), "backend", @"E:\OMP\Services\backend")]);

        // A NAME collision withholds both permissions: that service belongs to somebody
        // else, so removing it would take a live installation down with it (R12-A1).
        Assert.False(result.ShouldRemoveOldService);
        Assert.False(result.ShouldDeleteOldDirectory);
        Assert.Contains("backend", result.ServiceSkipReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A folder another app instance lives in is never deleted, whatever it is called --
    /// but the stale service registration still goes.
    /// </summary>
    /// <remarks>
    /// The footprint below carries a different runtime name, so every name-based check
    /// passes. Only comparing the resolved paths stops the delete -- which is the whole
    /// point of R7-D4: cleanup compared names and deleted directories. It is not a reason
    /// to keep the old Windows service, and treating it as one was R12-A1.
    /// </remarks>
    [Fact]
    public void EvaluateRenameCleanup_KeepsDirectoryButRemovesService_WhenTheOldPathIsAnotherInstancesLiveDirectory()
    {
        var settings = CreateSettings(servicesRoot: "E:\\OMP\\Services");
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: "backend");

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            @"E:\OMP\Services\OMP.iKrock2.Backend",
            new Dictionary<Guid, string>(),
            [new HostRuntimeFootprint(Guid.NewGuid(), "SomethingElse", @"E:\OMP\Services\backend")]);

        Assert.True(result.ShouldRemoveOldService);
        Assert.False(result.ShouldDeleteOldDirectory);
        Assert.Null(result.ServiceSkipReason);
        Assert.Contains(@"E:\OMP\Services\backend", result.DirectorySkipReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A rename that keeps the same folder deletes no files -- and must still remove the
    /// old service.
    /// </summary>
    /// <remarks>
    /// This is the case R12-A1 was reported against. Renaming only InstallationName leaves
    /// old and new resolving to one directory; R7-D read that as "nothing to clean up" and
    /// skipped DeleteService along with the directory delete, so the previous Windows
    /// service stayed registered, auto-starting, and pointed at the very binaries the new
    /// service was about to run -- two services against one inbox. The assertion that
    /// matters here is ShouldRemoveOldService; the earlier version of this test asserted
    /// the combined flag and so codified the regression.
    /// </remarks>
    [Fact]
    public void EvaluateRenameCleanup_RemovesServiceButKeepsDirectory_WhenOnlyTheServiceNameChanged()
    {
        var settings = CreateSettings(servicesRoot: "E:\\OMP\\Services");
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installPath: @"E:\OMP\Services\shared-folder",
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: "backend");

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            @"E:\OMP\Services\shared-folder",
            new Dictionary<Guid, string>(),
            []);

        Assert.True(result.ShouldRemoveOldService);
        Assert.False(result.ShouldDeleteOldDirectory);
        Assert.Equal("backend", result.OldServiceName);
        Assert.Null(result.ServiceSkipReason);
        Assert.Contains("target path", result.DirectorySkipReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The executable name is carried out of evaluation for the caller to verify.</summary>
    /// <remarks>
    /// <c>executableRelativePath</c> was accepted and never read. It is the only evidence
    /// that distinguishes our own old directory from a stranger's, so evaluation must hand
    /// it on rather than drop it.
    /// </remarks>
    [Fact]
    public void EvaluateRenameCleanup_CarriesTheExpectedExecutableFileName()
    {
        var settings = CreateSettings(servicesRoot: "E:\\OMP\\Services");
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: "backend");

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            Path.Join("nested", "iKrock2.Backend.exe"),
            "OMP.iKrock2.Backend",
            @"E:\OMP\Services\OMP.iKrock2.Backend",
            new Dictionary<Guid, string>(),
            []);

        Assert.True(result.ShouldRemoveOldService);
        Assert.True(result.ShouldDeleteOldDirectory);
        Assert.Equal("iKrock2.Backend.exe", result.ExpectedExecutableFileName);
    }

    /// <summary>
    /// The executable name is carried out even when only the service may be removed, so the
    /// caller's directory-ownership objection still has something to compare against.
    /// </summary>
    [Fact]
    public void EvaluateRenameCleanup_CarriesTheExpectedExecutableFileName_WhenOnlyTheServiceMayBeRemoved()
    {
        var settings = CreateSettings(servicesRoot: "E:\\OMP\\Services");
        var deployment = CreateDeployment(
            appInstanceId: Guid.NewGuid(),
            installPath: @"E:\OMP\Services\shared-folder",
            installationName: "OMP.iKrock2.Backend",
            deployedRuntimeName: "backend");

        var result = ServiceAppDeploymentNaming.EvaluateRenameCleanup(
            settings,
            deployment,
            "iKrock2.Backend.exe",
            "OMP.iKrock2.Backend",
            @"E:\OMP\Services\shared-folder",
            new Dictionary<Guid, string>(),
            []);

        Assert.True(result.ShouldRemoveOldService);
        Assert.Equal("iKrock2.Backend.exe", result.ExpectedExecutableFileName);
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
            @"E:\OMP\Services\OMP.iKrock2.Backend",
            resolved,
            []);

        Assert.False(result.ShouldRemoveOldService);
        Assert.False(result.ShouldDeleteOldDirectory);
        Assert.NotNull(result.ServiceSkipReason);
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
            @"E:\OMP\Services\OMP.iKrock2.Backend",
            resolved,
            []);

        Assert.False(result.ShouldRemoveOldService);
        Assert.False(result.ShouldDeleteOldDirectory);
        Assert.Equal("OMP.iKrock2.Backend", result.OldServiceName);
        Assert.NotNull(result.ServiceSkipReason);
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
            @"E:\OMP\Services\OMP.iKrock2.Backend",
            resolved,
            []);

        Assert.False(result.ShouldRemoveOldService);
        Assert.False(result.ShouldDeleteOldDirectory);
        Assert.Equal("backend", result.OldServiceName);
        Assert.NotNull(result.ServiceSkipReason);
        Assert.Contains("Another active app instance", result.ServiceSkipReason);
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
            @"E:\OMP\Services\OMP.iKrock2.Backend",
            resolved,
            []);

        Assert.False(result.ShouldRemoveOldService);
        Assert.False(result.ShouldDeleteOldDirectory);
        Assert.Equal(hostAgentServiceName, result.OldServiceName);
        Assert.NotNull(result.ServiceSkipReason);
        Assert.Contains("HostAgent service name", result.ServiceSkipReason);
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
            @"E:\OMP\Services\OMP.iKrock2.Backend",
            resolved,
            []);

        Assert.False(result.ShouldRemoveOldService);
        Assert.False(result.ShouldDeleteOldDirectory);
        Assert.Equal("OMP.WorkerManager", result.OldServiceName);
        Assert.NotNull(result.ServiceSkipReason);
        Assert.Contains("WorkerManager service name", result.ServiceSkipReason);
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
