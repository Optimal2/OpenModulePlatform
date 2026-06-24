using OpenModulePlatform.Web.Shared.Navigation;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class PortalTopBarNotificationUpdateOptionsTests
{
    [Theory]
    [InlineData(null, PortalTopBarNotificationUpdateOptions.PollMode)]
    [InlineData("", PortalTopBarNotificationUpdateOptions.PollMode)]
    [InlineData("bogus", PortalTopBarNotificationUpdateOptions.PollMode)]
    [InlineData("POLL", PortalTopBarNotificationUpdateOptions.PollMode)]
    [InlineData(" manual ", PortalTopBarNotificationUpdateOptions.ManualMode)]
    [InlineData("push", PortalTopBarNotificationUpdateOptions.PushMode)]
    public void FromConfig_NormalizesMode_WithSafeFallback(string? value, string expected)
    {
        var options = PortalTopBarNotificationUpdateOptions.FromConfig(value, "60");

        Assert.Equal(expected, options.Mode);
    }

    [Theory]
    [InlineData(null, PortalTopBarNotificationUpdateOptions.DefaultPollIntervalSeconds)]
    [InlineData("", PortalTopBarNotificationUpdateOptions.DefaultPollIntervalSeconds)]
    [InlineData("not-a-number", PortalTopBarNotificationUpdateOptions.DefaultPollIntervalSeconds)]
    [InlineData("9", PortalTopBarNotificationUpdateOptions.DefaultPollIntervalSeconds)]
    [InlineData("3601", PortalTopBarNotificationUpdateOptions.DefaultPollIntervalSeconds)]
    [InlineData("10", 10)]
    [InlineData("3600", 3600)]
    [InlineData("120", 120)]
    public void FromConfig_NormalizesPollInterval_WithBounds(string? value, int expected)
    {
        var options = PortalTopBarNotificationUpdateOptions.FromConfig("poll", value);

        Assert.Equal(expected, options.PollIntervalSeconds);
    }

    [Fact]
    public void FromConfig_EnablesPolling_OnlyForPollMode()
    {
        Assert.True(PortalTopBarNotificationUpdateOptions.FromConfig("poll", "60").UsesPolling);
        Assert.False(PortalTopBarNotificationUpdateOptions.FromConfig("manual", "60").UsesPolling);
        Assert.False(PortalTopBarNotificationUpdateOptions.FromConfig("push", "60").UsesPolling);
    }
}
