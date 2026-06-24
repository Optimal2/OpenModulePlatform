namespace OpenModulePlatform.Web.Shared.Notifications;

public interface ITopBarNotificationStatePublisher
{
    Task NotifyChangedAsync(int userId, CancellationToken ct);
}
