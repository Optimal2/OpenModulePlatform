using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace OpenModulePlatform.Web.Shared.Notifications;

public sealed class SignalRTopBarNotificationStatePublisher : ITopBarNotificationStatePublisher
{
    private readonly IHubContext<TopBarNotificationHub> _hubContext;
    private readonly ILogger<SignalRTopBarNotificationStatePublisher> _logger;

    public SignalRTopBarNotificationStatePublisher(
        IHubContext<TopBarNotificationHub> hubContext,
        ILogger<SignalRTopBarNotificationStatePublisher> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyChangedAsync(int userId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (userId <= 0)
        {
            _logger.LogDebug(
                "Skipped topbar notification state change for invalid OMP user id {UserId}.",
                userId);
            return;
        }

        try
        {
            await _hubContext.Clients
                .Group(TopBarNotificationHub.UserGroupName(userId))
                .SendCoreAsync(TopBarNotificationHub.StateChangedMethod, Array.Empty<object?>(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal request/service shutdown; no notification push is needed.
        }
        catch (HubException ex)
        {
            LogNotificationFanOutFailure(ex, userId);
        }
        catch (ObjectDisposedException ex)
        {
            LogNotificationFanOutFailure(ex, userId);
        }
        catch (InvalidOperationException ex)
        {
            LogNotificationFanOutFailure(ex, userId);
        }
    }

    private void LogNotificationFanOutFailure(Exception ex, int userId)
    {
        // Notification fan-out is opportunistic. Log SignalR failures, but do not fail the
        // originating request because clients can still recover through the normal refresh path.
        _logger.LogWarning(
            ex,
            "Failed to publish topbar notification state change for OMP user {UserId}.",
            userId);
    }
}
