using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installation.Tests;

public sealed class HostProfileDiscoveryTests
{
    [Fact]
    public void ResolveProfileMachineNames_CollectsMachineNamesHostNameAndHostKey()
    {
        var config = new BootstrapConfig
        {
            Profile = new BootstrapProfileOptions
            {
                MachineNames = ["WEB01", " web01 "]
            },
            HostAgent = new HostAgentInstallOptions
            {
                HostName = "web01.example.test",
                HostKey = "web01-key"
            }
        };

        var names = HostProfileDiscovery.ResolveProfileMachineNames(config);

        Assert.Equal(3, names.Count);
        Assert.Contains("WEB01", names);
        Assert.Contains("web01.example.test", names);
        Assert.Contains("web01-key", names);
    }

    [Fact]
    public void ResolveProfileMachineNames_SkipsBlankEntries()
    {
        var config = new BootstrapConfig
        {
            Profile = new BootstrapProfileOptions
            {
                MachineNames = ["", "   "]
            },
            HostAgent = new HostAgentInstallOptions
            {
                HostName = "",
                HostKey = "   "
            }
        };

        Assert.Empty(HostProfileDiscovery.ResolveProfileMachineNames(config));
    }

    [Fact]
    public void GetLocalMachineNames_ContainsMachineNameInBothForms()
    {
        var names = HostProfileDiscovery.GetLocalMachineNames();

        Assert.Contains(Environment.MachineName, names);
    }
}
