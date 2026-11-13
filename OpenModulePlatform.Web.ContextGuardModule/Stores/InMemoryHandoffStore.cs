using System.Security.Cryptography;
using OpenModulePlatform.Web.ContextGuardModule.Models;

namespace OpenModulePlatform.Web.ContextGuardModule.Stores;

public sealed class InMemoryHandoffStore(TimeProvider timeProvider) : IHandoffStore
{
    private const int TombstoneCapacityFactor = 2;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _nextTombstoneSequence;

    public HandoffCreateResult Create(
        string profileKey,
        string correlationHash,
        IReadOnlyList<ContextGuardField> fields,
        int droppedFieldCount,
        TimeSpan ttl,
        int capacity)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            PruneExpiredCore(now);
            var pendingCount = _entries.Values.Count(entry => entry.State == HandoffEntryState.Pending);
            if (pendingCount >= capacity)
            {
                return new HandoffCreateResult(HandoffCreateStatus.CapacityExceeded, null, droppedFieldCount);
            }

            // Leave room for the new pending entry while retaining at most 2x capacity tombstones.
            var tombstoneCount = _entries.Count - pendingCount;
            var tombstoneLimit = (long)capacity * TombstoneCapacityFactor;
            while (tombstoneCount >= tombstoneLimit && EvictOldestTombstoneCore())
            {
                tombstoneCount--;
            }

            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var payload = new HandoffPayload(profileKey, correlationHash, fields.ToArray());
            _entries.Add(key, new Entry(payload, now.Add(ttl)));
            return new HandoffCreateResult(HandoffCreateStatus.Created, key, droppedFieldCount);
        }
    }

    public HandoffRedemption Redeem(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return new HandoffRedemption(HandoffRedemptionStatus.Unknown, null);
            }

            if (entry.State == HandoffEntryState.Ended)
            {
                return new HandoffRedemption(HandoffRedemptionStatus.Ended, null);
            }

            if (entry.State == HandoffEntryState.Redeemed)
            {
                return new HandoffRedemption(HandoffRedemptionStatus.AlreadyRedeemed, null);
            }

            if (entry.State == HandoffEntryState.Expired)
            {
                return new HandoffRedemption(HandoffRedemptionStatus.Expired, null);
            }

            if (entry.ExpiresUtc <= timeProvider.GetUtcNow())
            {
                MarkTombstone(entry, HandoffEntryState.Expired);
                return new HandoffRedemption(HandoffRedemptionStatus.Expired, null);
            }

            var payload = entry.Payload!;
            MarkTombstone(entry, HandoffEntryState.Redeemed);
            return new HandoffRedemption(HandoffRedemptionStatus.Success, payload);
        }
    }

    public int EndByCorrelation(string profileKey, string correlationHash)
    {
        lock (_gate)
        {
            var ended = 0;
            foreach (var entry in _entries.Values.Where(entry =>
                         entry.State == HandoffEntryState.Pending
                         && string.Equals(entry.Payload!.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(entry.Payload.CorrelationHash, correlationHash, StringComparison.Ordinal)))
            {
                MarkTombstone(entry, HandoffEntryState.Ended);
                ended++;
            }

            return ended;
        }
    }

    private void PruneExpiredCore(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries.ToArray())
        {
            if (entry.ExpiresUtc.AddMinutes(5) <= now)
            {
                _entries.Remove(key);
            }
            else if (entry.State == HandoffEntryState.Pending && entry.ExpiresUtc <= now)
            {
                // Keep a short payload-free tombstone so expiry is distinguishable from reuse.
                MarkTombstone(entry, HandoffEntryState.Expired);
            }
        }
    }

    private void MarkTombstone(Entry entry, HandoffEntryState state)
    {
        entry.Payload = null;
        entry.State = state;
        entry.TombstoneSequence = ++_nextTombstoneSequence;
    }

    private bool EvictOldestTombstoneCore()
    {
        var oldest = _entries
            .Where(pair => pair.Value.State != HandoffEntryState.Pending)
            .MinBy(pair => pair.Value.TombstoneSequence);
        return oldest.Value is not null && _entries.Remove(oldest.Key);
    }

    private enum HandoffEntryState
    {
        Pending,
        Redeemed,
        Expired,
        Ended
    }

    private sealed class Entry(HandoffPayload payload, DateTimeOffset expiresUtc)
    {
        public HandoffPayload? Payload { get; set; } = payload;
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public HandoffEntryState State { get; set; } = HandoffEntryState.Pending;
        public long TombstoneSequence { get; set; }
    }
}
