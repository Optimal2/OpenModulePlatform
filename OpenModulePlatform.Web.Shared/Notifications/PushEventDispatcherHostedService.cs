using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Notifications;

internal sealed class PushEventDispatcherHostedService : BackgroundService
{
    private readonly SqlPushEventOutboxStore _outbox;
    private readonly IHubContext<TopBarNotificationHub> _hubContext;
    private readonly IOptionsMonitor<PushEventDispatcherOptions> _options;
    private readonly ILogger<PushEventDispatcherHostedService> _logger;
    private readonly string _leaseOwner;
    private DateTime _nextCleanupUtc = DateTime.MinValue;

    public PushEventDispatcherHostedService(
        SqlPushEventOutboxStore outbox,
        IHubContext<TopBarNotificationHub> hubContext,
        IOptionsMonitor<PushEventDispatcherOptions> options,
        ILogger<PushEventDispatcherHostedService> logger,
        IHostEnvironment environment)
    {
        _outbox = outbox;
        _hubContext = hubContext;
        _options = options;
        _logger = logger;
        _leaseOwner = string.Create(
            CultureInfo.InvariantCulture,
            $"{environment.ApplicationName}:{Environment.MachineName}:{Environment.ProcessId}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            if (!options.Enabled)
            {
                await DelayAsync(options, stoppingToken);
                continue;
            }

            try
            {
                await CleanupIfDueAsync(options, stoppingToken);

                var leased = await _outbox.AcquireLeaseAsync(
                    options,
                    _leaseOwner,
                    Guid.NewGuid(),
                    stoppingToken);

                if (leased.Count == 0)
                {
                    await DelayAsync(options, stoppingToken);
                    continue;
                }

                foreach (var pushEvent in leased)
                {
                    await DispatchAsync(pushEvent, options, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal service shutdown; let the background service exit quietly.
            }
            // Keep the dispatcher alive after transient SQL/SignalR failures; each failure is
            // logged here and the polling loop retries according to PushEventDispatcherOptions.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to lease or dispatch OMP push events.");
                await DelayAsync(options, stoppingToken);
            }
        }
    }

    private async Task CleanupIfDueAsync(PushEventDispatcherOptions options, CancellationToken ct)
    {
        if (!options.CleanupEnabled)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc < _nextCleanupUtc)
        {
            return;
        }

        _nextCleanupUtc = nowUtc.AddMinutes(options.EffectiveCleanupIntervalMinutes);

        try
        {
            var deleted = await _outbox.CleanupExpiredAsync(options, ct);
            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Deleted {DeletedCount} expired OMP push event outbox rows.",
                    deleted);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal service shutdown; preserve the caller's cancellation flow.
            throw;
        }
        // Cleanup is best-effort maintenance. A failed cleanup must not stop dispatching fresh
        // push events, and the next cleanup interval will try again.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up expired OMP push event outbox rows.");
        }
    }

    private async Task DispatchAsync(
        LeasedPushEvent pushEvent,
        PushEventDispatcherOptions options,
        CancellationToken ct)
    {
        try
        {
            var envelope = TopBarPushEventEnvelope.FromLeasedEvent(pushEvent);
            var groups = ResolveTargetGroups(pushEvent).ToArray();
            if (groups.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Push event {pushEvent.PushEventId} has no SignalR target groups.");
            }

            await _hubContext.Clients
                .Groups(groups)
                .SendCoreAsync(
                    TopBarNotificationHub.PushEventMethod,
                    [envelope],
                    ct);

            if (IsTopBarSummaryRefreshCategory(pushEvent.EventCategory))
            {
                await _hubContext.Clients
                    .Groups(groups)
                    .SendCoreAsync(
                        TopBarNotificationHub.StateChangedMethod,
                        [envelope],
                        ct);
            }

            await _outbox.MarkDispatchedAsync(pushEvent, ct);

            _logger.LogDebug(
                "Dispatched OMP push event {PushEventId} category {EventCategory} to {GroupCount} SignalR group(s).",
                pushEvent.PushEventId,
                pushEvent.EventCategory,
                groups.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal service shutdown; preserve the caller's cancellation flow.
            throw;
        }
        // Dispatch errors must be recorded on the outbox row so retry/dead-letter handling can
        // make progress without terminating the whole background dispatcher.
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to dispatch OMP push event {PushEventId}.",
                pushEvent.PushEventId);
            await _outbox.MarkFailedAsync(pushEvent, options, ex, ct);
        }
    }

    internal static IReadOnlyList<string> ResolveTargetGroups(LeasedPushEvent pushEvent)
    {
        var target = PushEventTarget.FromJson(pushEvent.TargetType, pushEvent.TargetUserId, pushEvent.TargetJson);
        return target.Kind switch
        {
            "user" => target.Values.Select(ParsePositiveInt).Where(id => id.HasValue)
                .Select(id => TopBarNotificationHub.UserGroupName(id!.Value))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            "role" => target.Values.Select(ParsePositiveInt).Where(id => id.HasValue)
                .Select(id => TopBarNotificationHub.RoleGroupName(id!.Value))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            "broadcast" => [TopBarNotificationHub.BroadcastGroupName],
            "authenticated" => [TopBarNotificationHub.AuthenticatedGroupName],
            "app" => target.Values.Select(TopBarNotificationHub.AppGroupName)
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            "module" => target.Values.Select(TopBarNotificationHub.ModuleGroupName)
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            _ => []
        };
    }

    private static int? ParsePositiveInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;

    private static bool IsTopBarSummaryRefreshCategory(string category)
        => string.Equals(
               category,
               "topbar.notification-state-changed",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               category,
               "topbar.message-state-changed",
               StringComparison.OrdinalIgnoreCase);

    private static Task DelayAsync(PushEventDispatcherOptions options, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(options.EffectivePollingIntervalSeconds), ct);
}
