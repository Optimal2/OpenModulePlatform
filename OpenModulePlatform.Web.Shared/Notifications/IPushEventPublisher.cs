namespace OpenModulePlatform.Web.Shared.Notifications;

public interface IPushEventPublisher
{
    Task<long> PublishAsync(PushEvent pushEvent, CancellationToken ct);
}
