using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Models;

public sealed class HostDeploymentStatusClassifierTests
{
    [Theory]
    [InlineData(HostDeploymentStatuses.Pending, HostDeploymentStatuses.Running)]
    [InlineData(HostDeploymentStatuses.Running, HostDeploymentStatuses.Succeeded)]
    [InlineData(HostDeploymentStatuses.Running, HostDeploymentStatuses.Failed)]
    public void IsValidTransition_AllowsExpectedTransitions(byte fromStatus, byte toStatus)
    {
        Assert.True(HostDeploymentStatusClassifier.IsValidTransition(fromStatus, toStatus));
    }

    [Theory]
    [InlineData(HostDeploymentStatuses.Pending, HostDeploymentStatuses.Succeeded)]
    [InlineData(HostDeploymentStatuses.Pending, HostDeploymentStatuses.Failed)]
    [InlineData(HostDeploymentStatuses.Succeeded, HostDeploymentStatuses.Running)]
    [InlineData(HostDeploymentStatuses.Succeeded, HostDeploymentStatuses.Failed)]
    [InlineData(HostDeploymentStatuses.Failed, HostDeploymentStatuses.Running)]
    [InlineData(HostDeploymentStatuses.Failed, HostDeploymentStatuses.Succeeded)]
    [InlineData(HostDeploymentStatuses.Warning, HostDeploymentStatuses.Running)]
    public void IsValidTransition_RejectsInvalidTransitions(byte fromStatus, byte toStatus)
    {
        Assert.False(HostDeploymentStatusClassifier.IsValidTransition(fromStatus, toStatus));
    }

    [Theory]
    [InlineData(HostDeploymentStatuses.Pending, "Pending")]
    [InlineData(HostDeploymentStatuses.Running, "Running")]
    [InlineData(HostDeploymentStatuses.Succeeded, "Succeeded")]
    [InlineData(HostDeploymentStatuses.Failed, "Failed")]
    [InlineData(HostDeploymentStatuses.Warning, "Warning")]
    [InlineData(255, "Unknown")]
    public void GetDisplayName_ReturnsExpectedName(byte status, string expected)
    {
        Assert.Equal(expected, HostDeploymentStatusClassifier.GetDisplayName(status));
    }
}
