namespace OpenModulePlatform.Web.Shared.Notifications;

public interface ITopBarNotificationStatePublisher
{
    Task NotifyChangedAsync(int userId, CancellationToken ct);

    Task NotifyChangedAsync(int userId, int? unreadCount, CancellationToken ct);
}
