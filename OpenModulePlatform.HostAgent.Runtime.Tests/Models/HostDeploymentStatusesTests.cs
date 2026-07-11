using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Models;

public sealed class HostDeploymentStatusesTests
{
    [Fact]
    public void StatusCodes_AreExpectedValues()
    {
        Assert.Equal(0, HostDeploymentStatuses.Pending);
        Assert.Equal(1, HostDeploymentStatuses.Running);
        Assert.Equal(2, HostDeploymentStatuses.Succeeded);
        Assert.Equal(3, HostDeploymentStatuses.Failed);
        Assert.Equal(4, HostDeploymentStatuses.Warning);
    }

    [Fact]
    public void StatusCodes_AreDistinct()
    {
        var values = new[]
        {
            HostDeploymentStatuses.Pending,
            HostDeploymentStatuses.Running,
            HostDeploymentStatuses.Succeeded,
            HostDeploymentStatuses.Failed,
            HostDeploymentStatuses.Warning
        };

        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public void HostAppIdentityCheckStatuses_Compliant_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(HostAppIdentityCheckStatuses.Compliant));
    }
}
