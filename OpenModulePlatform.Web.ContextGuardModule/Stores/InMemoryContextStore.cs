using OpenModulePlatform.Web.ContextGuardModule.Models;
using OpenModulePlatform.Web.ContextGuardModule.Options;

namespace OpenModulePlatform.Web.ContextGuardModule.Stores;

public sealed class InMemoryContextStore(TimeProvider timeProvider) : IContextStore
{
    private const int RetentionTimeoutPeriods = 3;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, GuardContext> _contexts = [];
    private readonly Dictionary<Guid, DateTimeOffset> _retiredAt = [];
    private readonly Dictionary<string, Guid> _currentByOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _ownerByContext = [];
    private DateTimeOffset _nextMaintenanceUtc = DateTimeOffset.MinValue;

    public GuardContext Create(
        int userId,
        string clientId,
        string profileKey,
        string correlationHash,
        string scope,
        TimeSpan heartbeatTimeout)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            RunMaintenanceCore(now, heartbeatTimeout);
            var ownerKey = OwnerKey(userId, clientId, scope);
            if (_currentByOwner.TryGetValue(ownerKey, out var previousId)
                && _contexts.TryGetValue(previousId, out var previous))
            {
                var lifecycle = now - previous.LastHeartbeatUtc > heartbeatTimeout
                    ? ContextLifecycle.Ended
                    : ContextLifecycle.Superseded;
                RetireCore(previous, lifecycle, now);
            }

            var context = new GuardContext(
                Guid.NewGuid(), userId, clientId, profileKey, correlationHash,
                now, now, ContextLifecycle.Current);
            _contexts[context.ContextId] = context;
            _currentByOwner[ownerKey] = context.ContextId;
            _ownerByContext[context.ContextId] = ownerKey;
            return context;
        }
    }

    public ContextStateResult Heartbeat(Guid contextId, int userId, TimeSpan heartbeatTimeout)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var result = GetStateCore(contextId, userId, now, heartbeatTimeout);
            if (result.State == ContextLifecycle.Current && result.Context is not null)
            {
                var updated = result.Context with { LastHeartbeatUtc = now };
                _contexts[contextId] = updated;
                return new ContextStateResult(ContextLifecycle.Current, updated);
            }

            return result;
        }
    }

    public ContextStateResult GetState(Guid contextId, int userId, TimeSpan heartbeatTimeout)
    {
        lock (_gate)
        {
            return GetStateCore(contextId, userId, timeProvider.GetUtcNow(), heartbeatTimeout);
        }
    }

    public int EndByCorrelation(string profileKey, string correlationHash)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var ended = 0;
            foreach (var (_, context) in _contexts.ToArray())
            {
                if (context.Lifecycle != ContextLifecycle.Current
                    || !string.Equals(context.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(context.CorrelationHash, correlationHash, StringComparison.Ordinal))
                {
                    continue;
                }

                RetireCore(context, ContextLifecycle.Ended, now);
                ended++;
            }

            return ended;
        }
    }

    private ContextStateResult GetStateCore(
        Guid contextId,
        int userId,
        DateTimeOffset now,
        TimeSpan heartbeatTimeout)
    {
        RunMaintenanceCore(now, heartbeatTimeout);
        if (!_contexts.TryGetValue(contextId, out var context) || context.UserId != userId)
        {
            return new ContextStateResult(ContextLifecycle.Ended, null);
        }

        if (context.Lifecycle == ContextLifecycle.Current
            && now - context.LastHeartbeatUtc > heartbeatTimeout)
        {
            context = RetireCore(context, ContextLifecycle.Ended, now);
        }

        return new ContextStateResult(context.Lifecycle, context);
    }

    private void RunMaintenanceCore(DateTimeOffset now, TimeSpan heartbeatTimeout)
    {
        if (now < _nextMaintenanceUtc)
        {
            return;
        }

        _nextMaintenanceUtc = now.Add(heartbeatTimeout);
        var retention = TimeSpan.FromTicks(heartbeatTimeout.Ticks * RetentionTimeoutPeriods);
        foreach (var (contextId, context) in _contexts.ToArray())
        {
            if (context.Lifecycle == ContextLifecycle.Current)
            {
                if (now - context.LastHeartbeatUtc > heartbeatTimeout)
                {
                    RetireCore(context, ContextLifecycle.Ended, now);
                }

                continue;
            }

            if (_retiredAt.TryGetValue(contextId, out var retiredAt)
                && now - retiredAt >= retention)
            {
                _contexts.Remove(contextId);
                _retiredAt.Remove(contextId);
                _ownerByContext.Remove(contextId);
            }
        }
    }

    private GuardContext RetireCore(GuardContext context, ContextLifecycle lifecycle, DateTimeOffset now)
    {
        var retired = context with { Lifecycle = lifecycle };
        _contexts[context.ContextId] = retired;
        _retiredAt[context.ContextId] = now;
        RemoveCurrentMappingCore(context);
        return retired;
    }

    private void RemoveCurrentMappingCore(GuardContext context)
    {
        if (_ownerByContext.Remove(context.ContextId, out var ownerKey)
            && _currentByOwner.TryGetValue(ownerKey, out var currentId)
            && currentId == context.ContextId)
        {
            _currentByOwner.Remove(ownerKey);
        }
    }

    private static string OwnerKey(int userId, string clientId, string scope)
        => string.Equals(scope, ContextGuardScopes.UserAndClient, StringComparison.OrdinalIgnoreCase)
            ? $"{userId}\n{clientId}"
            : userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
