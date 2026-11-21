using System.Reflection;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class HostAgentSelfUpgradeInstallRootTests
{
    [Fact]
    public void ResolveInstallRoot_WithoutLocalRoot_DoesNotTrustDatabasePath()
    {
        var settings = new HostAgentSettings();
        var desired = new HostAgentUpgradeDescriptor
        {
            InstallRoot = Path.Combine(Path.GetTempPath(), "database-selected-root")
        };

        var actual = ResolveInstallRoot(settings, desired);

        Assert.Equal(AppContext.BaseDirectory, actual);
    }

    [Fact]
    public void ResolveInstallRoot_WithLocalRoot_AllowsDatabaseChildPath()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "configured-root");
        var requestedRoot = Path.Combine(localRoot, "child");
        var settings = new HostAgentSettings
        {
            ServicesRoot = localRoot
        };
        var desired = new HostAgentUpgradeDescriptor { InstallRoot = requestedRoot };

        var actual = ResolveInstallRoot(settings, desired);

        Assert.Equal(requestedRoot, actual);
    }

    private static string ResolveInstallRoot(HostAgentSettings settings, HostAgentUpgradeDescriptor desired)
    {
        var method = typeof(HostAgentSelfUpgradeService).GetMethod(
            "ResolveInstallRoot",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, [settings, desired]));
    }
}
