// File: OpenModulePlatform.Auth/Services/LoginThrottleService.cs
using Microsoft.Extensions.Caching.Memory;

namespace OpenModulePlatform.Auth.Services;

/// <summary>
/// Bounds password-guessing against local and alternate-Windows sign-in by
/// counting recent failures per key (user name, optionally combined with the
/// client address) and locking the key out for a cooldown once a threshold is
/// crossed (R3-F1). In-memory and best-effort: it slows brute force on a single
/// node without a hard dependency on shared state.
/// </summary>
public sealed class LoginThrottleService
{
    private const int MaxFailures = 10;
    // Per-source failures across ALL usernames: bounds password spraying (one
    // password tried against many accounts), which the per-username counter never
    // limited because each account accrues only one failure (R4-F3). Higher than
    // the per-username cap so ordinary shared-NAT traffic is not locked out.
    private const int MaxClientFailures = 50;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private readonly IMemoryCache _cache;

    // Serializes the get-or-create of a throttle entry. IMemoryCache.GetOrCreate is
    // not atomic, so on a "cold" key concurrent first-hits could each build their own
    // Attempts instance; the per-instance lock below then guarded different objects
    // and the losing writer's increment was overwritten, so lockout could never be
    // reached under parallel first-hit guessing (R5-F9).
    private readonly object _createLock = new();

    public LoginThrottleService(IMemoryCache cache)
    {
        _cache = cache;
    }

    private sealed class Attempts
    {
        public int Count;
        public DateTimeOffset FirstUtc;
        public DateTimeOffset? LockedUntilUtc;
    }

    public bool IsLockedOut(string key)
    {
        var normalized = Normalize(key);
        if (normalized.Length == 0)
        {
            return false;
        }

        return _cache.TryGetValue(CacheKey(normalized), out Attempts? attempts)
            && attempts?.LockedUntilUtc is { } until
            && until > DateTimeOffset.UtcNow;
    }

    public void RecordFailure(string key)
    {
        var normalized = Normalize(key);
        if (normalized.Length == 0)
        {
            return;
        }

        var cacheKey = CacheKey(normalized);
        var now = DateTimeOffset.UtcNow;
        var attempts = GetOrCreateAttempts(cacheKey, now);

        // Serialize the read-modify-write: concurrent failures for one key used
        // to race on Count++ and lose increments, so the lockout could never be
        // reached under trivially reproducible parallel guessing (R4-F2). Lock on
        // the cached instance so all writers for the same key contend on it.
        lock (attempts)
        {
            // Reset the window if the oldest counted failure aged out.
            if (now - attempts.FirstUtc > FailureWindow)
            {
                attempts.Count = 0;
                attempts.FirstUtc = now;
                attempts.LockedUntilUtc = null;
            }

            attempts.Count++;
            if (attempts.Count >= MaxFailures)
            {
                attempts.LockedUntilUtc = now.Add(Lockout);
            }

            _cache.Set(cacheKey, attempts, attempts.LockedUntilUtc is { } lockUntil
                ? lockUntil - now
                : FailureWindow);
        }
    }

    public void RecordSuccess(string key)
    {
        var normalized = Normalize(key);
        if (normalized.Length > 0)
        {
            _cache.Remove(CacheKey(normalized));
        }
    }

    /// <summary>
    /// True when this client address has accumulated too many failed sign-in
    /// attempts across all usernames (spray defense, R4-F3). No-op for an empty
    /// address (e.g. a proxy that hides the client IP), which falls back to the
    /// per-username throttle alone.
    /// </summary>
    public bool IsClientLockedOut(string? clientAddress)
    {
        var normalized = Normalize(clientAddress ?? string.Empty);
        if (normalized.Length == 0)
        {
            return false;
        }

        return _cache.TryGetValue(ClientCacheKey(normalized), out Attempts? attempts)
            && attempts?.LockedUntilUtc is { } until
            && until > DateTimeOffset.UtcNow;
    }

    public void RecordClientFailure(string? clientAddress)
    {
        var normalized = Normalize(clientAddress ?? string.Empty);
        if (normalized.Length == 0)
        {
            return;
        }

        var cacheKey = ClientCacheKey(normalized);
        var now = DateTimeOffset.UtcNow;
        var attempts = GetOrCreateAttempts(cacheKey, now);

        lock (attempts)
        {
            if (now - attempts.FirstUtc > FailureWindow)
            {
                attempts.Count = 0;
                attempts.FirstUtc = now;
                attempts.LockedUntilUtc = null;
            }

            attempts.Count++;
            if (attempts.Count >= MaxClientFailures)
            {
                attempts.LockedUntilUtc = now.Add(Lockout);
            }

            _cache.Set(cacheKey, attempts, attempts.LockedUntilUtc is { } lockUntil
                ? lockUntil - now
                : FailureWindow);
        }
    }

    private Attempts GetOrCreateAttempts(string cacheKey, DateTimeOffset now)
    {
        // Fast path: an existing entry needs no locking.
        if (_cache.TryGetValue(cacheKey, out Attempts? existing) && existing is not null)
        {
            return existing;
        }

        // Cold key: serialize creation so all concurrent first-hits observe the same
        // single Attempts instance and their per-instance locks contend correctly (R5-F9).
        lock (_createLock)
        {
            return _cache.GetOrCreate(cacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = FailureWindow;
                return new Attempts { FirstUtc = now };
            })!;
        }
    }

    private static string CacheKey(string normalized) => $"omp-login-throttle::{normalized}";

    private static string ClientCacheKey(string normalized) => $"omp-login-throttle-client::{normalized}";

    private static string Normalize(string key) => (key ?? string.Empty).Trim().ToLowerInvariant();
}
