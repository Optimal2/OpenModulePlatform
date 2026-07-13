using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.Notifications;

internal sealed class MigratingTopBarNotificationStatePublisher : ITopBarNotificationStatePublisher
{
    private readonly SignalRTopBarNotificationStatePublisher _signalRPublisher;
    private readonly OutboxTopBarNotificationStatePublisher _outboxPublisher;
    private readonly IOptionsMonitor<PushEventProducerOptions> _options;

    public MigratingTopBarNotificationStatePublisher(
        SignalRTopBarNotificationStatePublisher signalRPublisher,
        OutboxTopBarNotificationStatePublisher outboxPublisher,
        IOptionsMonitor<PushEventProducerOptions> options)
    {
        _signalRPublisher = signalRPublisher;
        _outboxPublisher = outboxPublisher;
        _options = options;
    }

    public Task NotifyChangedAsync(int userId, CancellationToken ct)
        => NotifyChangedAsync(userId, null, ct);

    public Task NotifyChangedAsync(int userId, int? unreadCount, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        return options.UseOutboxForNotificationStateChanges
            ? _outboxPublisher.NotifyChangedAsync(userId, unreadCount, ct)
            : _signalRPublisher.NotifyChangedAsync(userId, unreadCount, ct);
    }
}
