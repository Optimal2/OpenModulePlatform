using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class OmpAdminRepositoryCompatibilityTests
{
    [Theory]
    [InlineData("channel-type", "Worker")]
    [InlineData("worker-plugin", "Worker")]
    [InlineData("worker", "WebApp")]
    [InlineData("host-agent", "Portal")]
    [InlineData("service-app", "Worker")]
    public void IsArtifactPackageCompatibleWithAppType_ReturnsFalse_ForIncompatibleAutoApplyTargets(
        string packageType,
        string appType)
    {
        var compatible = OmpAdminRepository.IsArtifactPackageCompatibleWithAppType(packageType, appType);

        Assert.False(compatible);
    }

    [Theory]
    [InlineData("web-app", "Portal")]
    [InlineData("web-app", "WebApp")]
    [InlineData("service-app", "ServiceApp")]
    [InlineData("worker", "Worker")]
    [InlineData("host-agent", "HostAgent")]
    [InlineData("worker-host", "WorkerHost")]
    [InlineData("WORKER", "worker")]
    public void IsArtifactPackageCompatibleWithAppType_ReturnsTrue_ForSupportedAutoApplyTargets(
        string packageType,
        string appType)
    {
        var compatible = OmpAdminRepository.IsArtifactPackageCompatibleWithAppType(packageType, appType);

        Assert.True(compatible);
    }
}
