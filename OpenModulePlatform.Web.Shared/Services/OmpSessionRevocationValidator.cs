using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Web.Shared.Security;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Session revocation checkpoint for the shared OMP auth cookie (R7-F10).
/// </summary>
/// <remarks>
/// Before this existed, account status was read only at sign-in and the cookie
/// then lived until it expired -- with sliding renewal, effectively forever.
/// Every request now re-validates the session against omp.users: the account
/// must still exist and be active, and the security stamp in the cookie must
/// still match the account's current stamp. Disabling an account or changing
/// its password rotates the stamp, which ends the session.
///
/// The verified account state is cached briefly per user (see
/// <see cref="OmpSessionRevocationSettings"/>); the cache window is the
/// deliberate trade-off between revocation latency and database load. Failures
/// are never cached, so the next request retries instead of repeating a verdict.
/// </remarks>
public sealed class OmpSessionRevocationValidator
{
    private const int ActiveAccountStatus = 1;

    private readonly IOmpSessionRevocationStore _store;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OmpSessionRevocationValidator> _log;

    public OmpSessionRevocationValidator(
        IOmpSessionRevocationStore store,
        IMemoryCache cache,
        ILogger<OmpSessionRevocationValidator> log)
    {
        _store = store;
        _cache = cache;
        _log = log;
    }

    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var userIdValue = context.Principal?.FindFirst(OmpAuthDefaults.UserIdClaimType)?.Value;
        if (!int.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId) ||
            userId <= 0)
        {
            // Identities without an OMP database user id (external identities
            // that never linked to an OMP user, application-local schemes) have
            // no account row to revoke against.
            return;
        }

        var ct = context.HttpContext.RequestAborted;
        var settings = await ReadSettingsAsync(ct);
        var read = await ReadAccountStateAsync(userId, settings.CacheSeconds, ct);

        if (read.Failed)
        {
            if (settings.Strict)
            {
                // Fail closed: a session whose validity cannot be confirmed is
                // not trusted. The user signs in again once the read succeeds.
                _log.LogWarning(
                    "Session for OMP user {UserId} was rejected because the account state could not be verified (strict failure mode).",
                    userId);
                context.RejectPrincipal();
            }
            else
            {
                _log.LogWarning(
                    "The account state for OMP user {UserId} could not be verified; the session was kept because the lenient failure mode is configured.",
                    userId);
            }

            return;
        }

        var stampClaim = context.Principal?.FindFirst(OmpAuthDefaults.SecurityStampClaimType)?.Value;
        if (ShouldReject(read.State, stampClaim, out var reason))
        {
            _log.LogInformation(
                "Session for OMP user {UserId} was rejected: {Reason}",
                userId,
                reason);
            context.RejectPrincipal();
        }
    }

    /// <summary>
    /// The revocation decision itself, kept pure so it can be pinned without a
    /// database: a session is rejected when the account is gone, when it is no
    /// longer active, or when the cookie's security stamp is missing or stale.
    /// </summary>
    public static bool ShouldReject(
        OmpSessionAccountState? state,
        string? securityStampClaim,
        out string reason)
    {
        if (state is null)
        {
            reason = "the account no longer exists";
            return true;
        }

        if (state.Value.AccountStatus != ActiveAccountStatus)
        {
            reason = "the account is no longer active";
            return true;
        }

        if (!Guid.TryParse(securityStampClaim, out var claimStamp))
        {
            reason = "the session carries no valid security stamp";
            return true;
        }

        if (claimStamp != state.Value.SecurityStamp)
        {
            reason = "the account security stamp has changed";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private async Task<OmpSessionRevocationSettings> ReadSettingsAsync(CancellationToken ct)
    {
        try
        {
            return await _store.GetSettingsAsync(ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Fail closed, same as an unreadable value: the built-in defaults
            // are strict mode with the default cache window.
            _log.LogWarning(ex, "The session revocation settings could not be read; using the strict built-in defaults.");
            return OmpSessionRevocationSettings.Default;
        }
    }

    private async Task<OmpSessionAccountStateRead> ReadAccountStateAsync(
        int userId,
        int cacheSeconds,
        CancellationToken ct)
    {
        var cacheKey = CreateCacheKey(userId);
        if (cacheSeconds > 0 &&
            _cache.TryGetValue(cacheKey, out OmpSessionAccountStateRead cached))
        {
            return cached;
        }

        try
        {
            var state = await _store.GetAccountStateAsync(userId, ct);
            var read = new OmpSessionAccountStateRead(state, Failed: false);
            if (cacheSeconds > 0)
            {
                _cache.Set(cacheKey, read, TimeSpan.FromSeconds(cacheSeconds));
            }

            return read;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Never cached: the next request retries rather than repeating the
            // failure for the whole cache window (R4-E1).
            _log.LogWarning(ex, "Reading the account state for OMP user {UserId} failed.", userId);
            return new OmpSessionAccountStateRead(null, Failed: true);
        }
    }

    private static string CreateCacheKey(int userId)
        => "omp:session-revocation:" + userId.ToString(CultureInfo.InvariantCulture);

    private readonly record struct OmpSessionAccountStateRead(OmpSessionAccountState? State, bool Failed);
}
