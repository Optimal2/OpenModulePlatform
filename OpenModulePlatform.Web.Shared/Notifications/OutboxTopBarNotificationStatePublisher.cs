using OpenModulePlatform.EventPublisher;

namespace OpenModulePlatform.Web.Shared.Notifications;

internal sealed class OutboxTopBarNotificationStatePublisher : ITopBarNotificationStatePublisher
{
    private readonly IPushEventPublisher _pushEventPublisher;

    public OutboxTopBarNotificationStatePublisher(IPushEventPublisher pushEventPublisher)
    {
        _pushEventPublisher = pushEventPublisher;
    }

    public Task NotifyChangedAsync(int userId, CancellationToken ct)
        => _pushEventPublisher.PublishAsync(
            PushEvent.ForUser(userId, PushEventCategory.TopBarNotificationStateChanged),
            ct);
}
