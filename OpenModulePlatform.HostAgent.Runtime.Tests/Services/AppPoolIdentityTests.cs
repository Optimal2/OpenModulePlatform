using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class AppPoolIdentityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RemovedUser_ResetsIdentityAndClearsStoredCredentials(string userName)
    {
        var processModel = new FakeProcessModel();

        WebAppDeploymentService.ApplyAppPoolIdentity(processModel,
            new HostAgentIisAppPoolIdentitySettings { UserName = userName });

        Assert.Equal(FakeIdentityType.ApplicationPoolIdentity, processModel.IdentityType);
        Assert.Equal(string.Empty, processModel.UserName);
        Assert.Equal(string.Empty, processModel.Password);
    }

    [Theory]
    [InlineData("", "old-password")]
    [InlineData("new-password", "new-password")]
    public void SpecificUser_TrimsNameAndPreservesPasswordWhenOmitted(string password, string expectedPassword)
    {
        var processModel = new FakeProcessModel { IdentityType = FakeIdentityType.ApplicationPoolIdentity };

        WebAppDeploymentService.ApplyAppPoolIdentity(processModel,
            new HostAgentIisAppPoolIdentitySettings { UserName = " service-user ", Password = password });

        Assert.Equal(FakeIdentityType.SpecificUser, processModel.IdentityType);
        Assert.Equal("service-user", processModel.UserName);
        Assert.Equal(expectedPassword, processModel.Password);
    }

    public enum FakeIdentityType { SpecificUser, ApplicationPoolIdentity }

    public sealed class FakeProcessModel
    {
        public FakeIdentityType IdentityType { get; set; } = FakeIdentityType.SpecificUser;
        public string UserName { get; set; } = "old-user";
        public string Password { get; set; } = "old-password";
    }
}
