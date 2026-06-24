using OpenModulePlatform.EventPublisher;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class PushEventTests
{
    [Fact]
    public void Normalize_AllowsUserTarget()
    {
        var pushEvent = PushEvent.ForUser(
            42,
            new PushEventCategory($"  {PushEventCategory.TopBarNotificationStateChanged.Value}  "),
            """{"refresh":true}""",
            deduplicationKey: " notification:42:99 ",
            correlationKey: " request-123 ");

        var normalized = pushEvent.Normalize();

        Assert.Equal(PushEventCategory.TopBarNotificationStateChanged.Value, normalized.EventCategory);
        Assert.Equal("user", normalized.TargetType);
        Assert.Equal(42, normalized.TargetUserId);
        Assert.Equal("""{"kind":"user","ids":["42"]}""", normalized.TargetJson);
        Assert.Equal("""{"refresh":true}""", normalized.PayloadJson);
        Assert.Equal("notification:42:99", normalized.DeduplicationKey);
        Assert.Equal("request-123", normalized.CorrelationKey);
        Assert.Equal(PushEvent.DefaultMaxRetries, normalized.MaxRetries);
    }

    [Fact]
    public void Normalize_ClearsIdsForBroadcastTargets()
    {
        var pushEvent = new PushEvent(
            PushEventCategory.TopBarNotificationStateChanged,
            new PushTarget(PushTargetKind.Broadcast, ["42"]));

        var normalized = pushEvent.Normalize();

        Assert.Equal("broadcast", normalized.TargetType);
        Assert.Null(normalized.TargetUserId);
        Assert.Equal("""{"kind":"broadcast","ids":[]}""", normalized.TargetJson);
    }

    [Fact]
    public void Normalize_RejectsInvalidPayloadJson()
    {
        var pushEvent = PushEvent.ForAuthenticatedUsers(
            PushEventCategory.TopBarNotificationStateChanged,
            "{");

        Assert.Throws<ArgumentException>(() => pushEvent.Normalize());
    }

    [Fact]
    public void Normalize_RejectsMissingUserTarget()
    {
        var pushEvent = new PushEvent(
            PushEventCategory.TopBarNotificationStateChanged,
            new PushTarget(PushTargetKind.User, []));

        Assert.Throws<ArgumentException>(() => pushEvent.Normalize());
    }

    [Fact]
    public void Normalize_RejectsInvalidRetryCount()
    {
        var pushEvent = PushEvent.ForBroadcast(
            PushEventCategory.TopBarBannerStateChanged) with
        {
            MaxRetries = 21
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => pushEvent.Normalize());
    }

    [Fact]
    public void Categories_ReserveModuleMessageAndBannerRefreshHints()
    {
        Assert.Equal("module.state-changed", PushEventCategory.ModuleStateChanged.Value);
        Assert.Equal("topbar.message-state-changed", PushEventCategory.TopBarMessageStateChanged.Value);
        Assert.Equal("topbar.banner-state-changed", PushEventCategory.TopBarBannerStateChanged.Value);
    }
}
