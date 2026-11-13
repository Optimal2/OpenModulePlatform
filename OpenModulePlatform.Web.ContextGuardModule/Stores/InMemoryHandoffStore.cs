using System.Security.Cryptography;
using OpenModulePlatform.Web.ContextGuardModule.Models;

namespace OpenModulePlatform.Web.ContextGuardModule.Stores;

public sealed class InMemoryHandoffStore(TimeProvider timeProvider) : IHandoffStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public HandoffCreateResult Create(
        string profileKey,
        string correlationHash,
        IReadOnlyList<ContextGuardField> fields,
        int droppedFieldCount,
        TimeSpan ttl,
        int pendingCapacity,
        int tombstoneCapacity)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            PruneExpiredCore(now, tombstoneCapacity);
            var pendingCount = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.Payload is not null)
                {
                    pendingCount++;
                }
            }

            if (pendingCount >= pendingCapacity)
            {
                return new HandoffCreateResult(HandoffCreateStatus.CapacityExceeded, null, droppedFieldCount);
            }

            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var payload = new HandoffPayload(profileKey, correlationHash, fields.ToArray());
            _entries.Add(key, new Entry(payload, now.Add(ttl), TerminalState.Pending, null));
            return new HandoffCreateResult(HandoffCreateStatus.Created, key, droppedFieldCount);
        }
    }

    public HandoffRedemption Redeem(string key, int tombstoneCapacity)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return new HandoffRedemption(HandoffRedemptionStatus.Unknown, null);
            }

            switch (entry.State)
            {
                case TerminalState.Ended:
                    return new HandoffRedemption(HandoffRedemptionStatus.Ended, null);
                case TerminalState.Redeemed:
                    return new HandoffRedemption(HandoffRedemptionStatus.AlreadyRedeemed, null);
                case TerminalState.Expired:
                    return new HandoffRedemption(HandoffRedemptionStatus.Expired, null);
            }

            var now = timeProvider.GetUtcNow();
            if (entry.ExpiresUtc <= now)
            {
                RetireCore(entry, TerminalState.Expired, now);
                EnforceTombstoneCapCore(tombstoneCapacity);
                return new HandoffRedemption(HandoffRedemptionStatus.Expired, null);
            }

            var payload = entry.Payload;
            RetireCore(entry, TerminalState.Redeemed, now);
            EnforceTombstoneCapCore(tombstoneCapacity);
            return new HandoffRedemption(HandoffRedemptionStatus.Success, payload);
        }
    }

    public int EndByCorrelation(string profileKey, string correlationHash, int tombstoneCapacity)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var ended = 0;
            foreach (var entry in _entries.Values.Where(entry =>
                         entry.Payload is not null
                         && string.Equals(entry.Payload.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(entry.Payload.CorrelationHash, correlationHash, StringComparison.Ordinal)))
            {
                RetireCore(entry, TerminalState.Ended, now);
                ended++;
            }

            if (ended > 0)
            {
                EnforceTombstoneCapCore(tombstoneCapacity);
            }

            return ended;
        }
    }

    private void PruneExpiredCore(DateTimeOffset now, int tombstoneCapacity)
    {
        foreach (var (key, entry) in _entries.ToArray())
        {
            if (entry.ExpiresUtc.AddMinutes(5) <= now)
            {
                _entries.Remove(key);
            }
            else if (entry.ExpiresUtc <= now && entry.State == TerminalState.Pending)
            {
                // Keep a short payload-free tombstone so expiry is distinguishable from reuse.
                RetireCore(entry, TerminalState.Expired, now);
            }
        }

        EnforceTombstoneCapCore(tombstoneCapacity);
    }

    private void EnforceTombstoneCapCore(int tombstoneCapacity)
    {
        var tombstoneKeys = _entries
            .Where(kvp => kvp.Value.Payload is null)
            .OrderBy(kvp => kvp.Value.RetiredUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => kvp.Key)
            .ToArray();
        foreach (var key in tombstoneKeys.Take(Math.Max(0, tombstoneKeys.Length - tombstoneCapacity)))
        {
            _entries.Remove(key);
        }
    }

    private static void RetireCore(Entry entry, TerminalState state, DateTimeOffset now)
    {
        entry.Payload = null;
        entry.State = state;
        entry.RetiredUtc = now;
    }

    private sealed class Entry(HandoffPayload? payload, DateTimeOffset expiresUtc, TerminalState state, DateTimeOffset? retiredUtc)
    {
        public HandoffPayload? Payload { get; set; } = payload;
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public TerminalState State { get; set; } = state;
        public DateTimeOffset? RetiredUtc { get; set; } = retiredUtc;
    }

    private enum TerminalState
    {
        Pending,
        Redeemed,
        Expired,
        Ended
    }
}
