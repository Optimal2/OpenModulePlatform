using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace OpenModulePlatform.Web.Shared.Notifications;

public sealed class SignalRTopBarNotificationStatePublisher : ITopBarNotificationStatePublisher
{
    private readonly IHubContext<TopBarNotificationHub> _hubContext;
    private readonly IPushEventPublisher _pushEvents;
    private readonly ILogger<SignalRTopBarNotificationStatePublisher> _logger;

    public SignalRTopBarNotificationStatePublisher(
        IHubContext<TopBarNotificationHub> hubContext,
        IPushEventPublisher pushEvents,
        ILogger<SignalRTopBarNotificationStatePublisher> logger)
    {
        _hubContext = hubContext;
        _pushEvents = pushEvents;
        _logger = logger;
    }

    public async Task NotifyChangedAsync(int userId, CancellationToken ct)
    {
        if (userId <= 0 || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _pushEvents.PublishAsync(
                PushEvent.ForUser(userId, PushEventCategories.TopBarNotificationStateChanged),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist topbar notification state change for OMP user {UserId}.",
                userId);
        }

        try
        {
            await _hubContext.Clients
                .Group(TopBarNotificationHub.UserGroupName(userId))
                .SendCoreAsync(TopBarNotificationHub.StateChangedMethod, Array.Empty<object?>(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish topbar notification state change for OMP user {UserId}.",
                userId);
        }
    }
}
