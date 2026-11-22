using OpenModulePlatform.Web.Shared.Telemetry;

namespace OpenModulePlatform.Portal.Tests.Telemetry;

public sealed class OmpPerformanceTelemetryOptionsTests
{
    [Fact]
    public void ValidationError_UsesBoundPortalConfigurationPath()
    {
        var options = new OmpPerformanceTelemetryOptions
        {
            FlushIntervalSeconds = 4
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Equal(
            "Portal:Telemetry:FlushIntervalSeconds must be at least 5.",
            exception.Message);
    }
}
