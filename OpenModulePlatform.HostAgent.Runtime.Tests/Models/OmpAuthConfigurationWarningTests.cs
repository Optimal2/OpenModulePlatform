using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Models;

public sealed class OmpAuthConfigurationWarningTests
{
    [Fact]
    public void ToStoredString_EmitsCodeAndParamsAsSingleLineJson()
    {
        var warning = new OmpAuthConfigurationWarning(
            OmpAuthConfigurationWarning.DataProtectionKeyPathNotUncPathCode,
            [@"C:\OMP\Keys"],
            "OmpAuth:DataProtectionKeyPath 'C:\\OMP\\Keys' is not a UNC path.");

        // This exact JSON shape is the storage contract parsed by the Portal render layer
        // (HostDeploymentWarningLocalizer); keep both sides in sync.
        Assert.Equal(
            @"{""code"":""OmpAuth.DataProtectionKeyPath.NotUncPath"",""params"":[""C:\\OMP\\Keys""]}",
            warning.ToStoredString());
    }
}
