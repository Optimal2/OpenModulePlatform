using OpenModulePlatform.Web.ContextGuardModule.Models;
using OpenModulePlatform.Web.ContextGuardModule.Options;

namespace OpenModulePlatform.Web.ContextGuardModule.Stores;

public sealed class InMemoryContextStore(TimeProvider timeProvider) : IContextStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, GuardContext> _contexts = [];
    private readonly Dictionary<string, Guid> _currentByOwner = new(StringComparer.Ordinal);

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
            ExpireTimedOutCore(now, heartbeatTimeout);
            var ownerKey = OwnerKey(userId, clientId, scope);
            if (_currentByOwner.TryGetValue(ownerKey, out var previousId)
                && _contexts.TryGetValue(previousId, out var previous))
            {
                _contexts[previousId] = previous with { Lifecycle = ContextLifecycle.Superseded };
            }

            var context = new GuardContext(
                Guid.NewGuid(), userId, clientId, profileKey, correlationHash,
                now, now, ContextLifecycle.Current);
            _contexts[context.ContextId] = context;
            _currentByOwner[ownerKey] = context.ContextId;
            return context;
        }
    }

    public ContextStateResult Heartbeat(Guid contextId, int userId, TimeSpan heartbeatTimeout)
    {
        lock (_gate)
        {
            var result = GetStateCore(contextId, userId, timeProvider.GetUtcNow(), heartbeatTimeout);
            if (result.State == ContextLifecycle.Current && result.Context is not null)
            {
                var updated = result.Context with { LastHeartbeatUtc = timeProvider.GetUtcNow() };
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
            var ended = 0;
            foreach (var (contextId, context) in _contexts.ToArray())
            {
                if (context.Lifecycle != ContextLifecycle.Current
                    || !string.Equals(context.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(context.CorrelationHash, correlationHash, StringComparison.Ordinal))
                {
                    continue;
                }

                _contexts[contextId] = context with { Lifecycle = ContextLifecycle.Ended };
                RemoveCurrentMappingCore(context);
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
        ExpireTimedOutCore(now, heartbeatTimeout);
        if (!_contexts.TryGetValue(contextId, out var context) || context.UserId != userId)
        {
            return new ContextStateResult(ContextLifecycle.Ended, null);
        }

        return new ContextStateResult(context.Lifecycle, context);
    }

    private void ExpireTimedOutCore(DateTimeOffset now, TimeSpan heartbeatTimeout)
    {
        foreach (var (contextId, context) in _contexts.ToArray())
        {
            if (context.Lifecycle != ContextLifecycle.Current
                || now - context.LastHeartbeatUtc <= heartbeatTimeout)
            {
                continue;
            }

            _contexts[contextId] = context with { Lifecycle = ContextLifecycle.Ended };
            RemoveCurrentMappingCore(context);
        }
    }

    private void RemoveCurrentMappingCore(GuardContext context)
    {
        foreach (var (ownerKey, contextId) in _currentByOwner.ToArray())
        {
            if (contextId == context.ContextId)
            {
                _currentByOwner.Remove(ownerKey);
            }
        }
    }

    private static string OwnerKey(int userId, string clientId, string scope)
        => string.Equals(scope, ContextGuardScopes.UserAndClient, StringComparison.OrdinalIgnoreCase)
            ? $"{userId}\n{clientId}"
            : userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
