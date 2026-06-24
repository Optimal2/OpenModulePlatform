namespace OpenModulePlatform.EventPublisher;

public interface IPushEventPublisher
{
    Task<long> PublishAsync(PushEvent pushEvent, CancellationToken ct);
}
