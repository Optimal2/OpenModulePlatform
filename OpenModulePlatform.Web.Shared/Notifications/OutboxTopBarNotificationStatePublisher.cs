using OpenModulePlatform.EventPublisher;
using System.Text.Json;

namespace OpenModulePlatform.Web.Shared.Notifications;

internal sealed class OutboxTopBarNotificationStatePublisher : ITopBarNotificationStatePublisher
{
    private readonly IPushEventPublisher _pushEventPublisher;

    public OutboxTopBarNotificationStatePublisher(IPushEventPublisher pushEventPublisher)
    {
        _pushEventPublisher = pushEventPublisher;
    }

    public Task NotifyChangedAsync(int userId, CancellationToken ct)
        => NotifyChangedAsync(userId, null, ct);

    public Task NotifyChangedAsync(int userId, int? unreadCount, CancellationToken ct)
        => _pushEventPublisher.PublishAsync(
            PushEvent.ForUser(
                userId,
                PushEventCategory.TopBarNotificationStateChanged,
                BuildPayloadJson(unreadCount)),
            ct);

    private static string? BuildPayloadJson(int? unreadCount)
        => unreadCount.HasValue
            ? JsonSerializer.Serialize(new { unreadNotificationCount = unreadCount.Value })
            : null;
}
