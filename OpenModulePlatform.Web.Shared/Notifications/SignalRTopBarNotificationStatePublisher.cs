using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.EventPublisher;

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

    public Task NotifyChangedAsync(int userId, CancellationToken ct)
        => NotifyChangedAsync(userId, null, ct);

    public async Task NotifyChangedAsync(int userId, int? unreadCount, CancellationToken ct)
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
            var arguments = BuildArguments(unreadCount);
            await _hubContext.Clients
                .Group(TopBarNotificationHub.UserGroupName(userId))
                .SendCoreAsync(TopBarNotificationHub.StateChangedMethod, arguments, ct);
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

    private static object?[] BuildArguments(int? unreadCount)
        => unreadCount.HasValue
            ? [new { category = PushEventCategory.TopBarNotificationStateChanged.Value, payload = new { unreadNotificationCount = unreadCount.Value } }]
            : Array.Empty<object?>();

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
