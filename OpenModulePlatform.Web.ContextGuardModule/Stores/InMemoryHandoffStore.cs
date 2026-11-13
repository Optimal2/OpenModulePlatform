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
        int capacity)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            PruneExpiredCore(now);
            if (_entries.Values.Count(entry => entry.Payload is not null) >= capacity)
            {
                return new HandoffCreateResult(HandoffCreateStatus.CapacityExceeded, null, droppedFieldCount);
            }

            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var payload = new HandoffPayload(profileKey, correlationHash, fields.ToArray());
            _entries.Add(key, new Entry(payload, now.Add(ttl), false));
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

            if (entry.Ended)
            {
                return new HandoffRedemption(HandoffRedemptionStatus.Ended, null);
            }

            if (entry.ExpiresUtc <= timeProvider.GetUtcNow())
            {
                entry.Payload = null;
                return new HandoffRedemption(HandoffRedemptionStatus.Expired, null);
            }

            if (entry.Payload is null)
            {
                return new HandoffRedemption(HandoffRedemptionStatus.AlreadyRedeemed, null);
            }

            var payload = entry.Payload;
            entry.Payload = null;
            return new HandoffRedemption(HandoffRedemptionStatus.Success, payload);
        }
    }

    public int EndByCorrelation(string profileKey, string correlationHash)
    {
        lock (_gate)
        {
            var ended = 0;
            foreach (var entry in _entries.Values.Where(entry =>
                         entry.Payload is not null
                         && string.Equals(entry.Payload.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(entry.Payload.CorrelationHash, correlationHash, StringComparison.Ordinal)))
            {
                entry.Payload = null;
                entry.Ended = true;
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
            else if (entry.ExpiresUtc <= now)
            {
                // Keep a short payload-free tombstone so expiry is distinguishable from reuse.
                entry.Payload = null;
            }
        }
    }

    private sealed class Entry(HandoffPayload? payload, DateTimeOffset expiresUtc, bool ended)
    {
        public HandoffPayload? Payload { get; set; } = payload;
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public bool Ended { get; set; } = ended;
    }
}
