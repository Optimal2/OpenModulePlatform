using Xunit;

namespace OpenModulePlatform.Bootstrapper.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_CheckDeveloperSourceStatus_SetsFlag()
    {
        var options = CliOptions.Parse(["--config", "bootstrap.json", "--check-developer-source-status"]);

        Assert.True(options.CheckDeveloperSourceStatus);
        Assert.False(options.Json);
    }

    [Fact]
    public void Parse_CheckDeveloperSourceStatusWithJson_SetsBothFlags()
    {
        var options = CliOptions.Parse(["--config", "bootstrap.json", "--check-developer-source-status", "--json"]);

        Assert.True(options.CheckDeveloperSourceStatus);
        Assert.True(options.Json);
    }

    [Fact]
    public void Parse_JsonWithoutCheckDeveloperSourceStatus_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CliOptions.Parse(["--config", "bootstrap.json", "--json"]));

        Assert.Contains("--json can only be used with --check-developer-source-status", ex.Message);
    }

    [Fact]
    public void Parse_CheckDeveloperSourceStatusWithRefreshAndStagePackage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CliOptions.Parse(["--config", "bootstrap.json", "--check-developer-source-status", "--refresh-and-stage-package"]));

        Assert.Contains("Choose only one operation mode", ex.Message);
    }

    [Fact]
    public void Parse_CheckDeveloperSourceStatusWithSyncPackageObjects_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => CliOptions.Parse(["--config", "bootstrap.json", "--check-developer-source-status", "--sync-package-objects"]));
    }

    [Fact]
    public void Parse_CheckDeveloperSourceStatusWithUpgradeOrComplete_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => CliOptions.Parse(["--config", "bootstrap.json", "--check-developer-source-status", "--upgrade-or-complete"]));
    }
}
