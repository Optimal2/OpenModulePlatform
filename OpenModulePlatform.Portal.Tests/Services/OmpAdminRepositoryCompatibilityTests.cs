using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class OmpAdminRepositoryCompatibilityTests
{
    [Theory]
    [InlineData("channel-type", "Portal")]
    [InlineData("channel-type", "WebApp")]
    [InlineData("channel-type", "Worker")]
    [InlineData("channel-type", "ServiceApp")]
    [InlineData("channel-type", "HostAgent")]
    [InlineData("channel-type", "WorkerHost")]
    [InlineData("CHANNEL-TYPE", "worker")]
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

    [Fact]
    public void CreateIncompatibleAutoApplySkipResult_LeavesRuntimeBindingsUntouched_ForWorkerChannelTypeIncidentShape()
    {
        var result = OmpAdminRepository.CreateIncompatibleAutoApplySkipResult(
            "channel-type",
            "Worker",
            "ibs_packager_worker");

        Assert.Equal(0, result.TemplateAppRowsUpdated);
        Assert.Equal(0, result.AppInstanceRowsUpdated);
        Assert.Equal(0, result.WorkerInstanceRowsUpdated);
        Assert.Equal(0, result.HostAgentDesiredRowsUpdated);
        Assert.Equal(0, result.TotalRowsUpdated);
        Assert.NotNull(result.AutoApplyInfoMessage);
        Assert.Contains("ibs_packager_worker", result.AutoApplyInfoMessage, StringComparison.Ordinal);
        Assert.Contains("channel-type", result.AutoApplyInfoMessage, StringComparison.Ordinal);
        Assert.Contains("compatibility/channel metadata", result.AutoApplyInfoMessage, StringComparison.Ordinal);
        Assert.Contains("Worker", result.AutoApplyInfoMessage, StringComparison.Ordinal);
        Assert.Contains("AppInstances", result.AutoApplyInfoMessage, StringComparison.Ordinal);
        Assert.Contains("WorkerInstances", result.AutoApplyInfoMessage, StringComparison.Ordinal);
        Assert.Contains("InstanceTemplateAppInstances", result.AutoApplyInfoMessage, StringComparison.Ordinal);
    }
}
