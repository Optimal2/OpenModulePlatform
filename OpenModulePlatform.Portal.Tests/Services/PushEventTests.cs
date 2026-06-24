using OpenModulePlatform.Web.Shared.Notifications;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class PushEventTests
{
    [Fact]
    public void Normalize_AllowsUserTarget()
    {
        var pushEvent = PushEvent.ForUser(
            42,
            $"  {PushEventCategories.TopBarNotificationStateChanged}  ",
            """{"refresh":true}""");

        var normalized = pushEvent.Normalize();

        Assert.Equal(PushEventCategories.TopBarNotificationStateChanged, normalized.EventCategory);
        Assert.Equal(PushEventTargetTypes.User, normalized.TargetType);
        Assert.Equal(42, normalized.TargetUserId);
        Assert.Equal("""{"refresh":true}""", normalized.PayloadJson);
    }

    [Fact]
    public void Normalize_ClearsUserIdForBroadcastTargets()
    {
        var pushEvent = new PushEvent(
            PushEventCategories.TopBarNotificationStateChanged,
            PushEventTargetTypes.Broadcast,
            TargetUserId: 42);

        var normalized = pushEvent.Normalize();

        Assert.Equal(PushEventTargetTypes.Broadcast, normalized.TargetType);
        Assert.Null(normalized.TargetUserId);
    }

    [Fact]
    public void Normalize_RejectsInvalidPayloadJson()
    {
        var pushEvent = PushEvent.ForAuthenticatedUsers(
            PushEventCategories.TopBarNotificationStateChanged,
            "{");

        Assert.Throws<ArgumentException>(() => pushEvent.Normalize());
    }

    [Fact]
    public void Normalize_RejectsMissingUserTarget()
    {
        var pushEvent = new PushEvent(
            PushEventCategories.TopBarNotificationStateChanged,
            PushEventTargetTypes.User);

        Assert.Throws<ArgumentOutOfRangeException>(() => pushEvent.Normalize());
    }
}
