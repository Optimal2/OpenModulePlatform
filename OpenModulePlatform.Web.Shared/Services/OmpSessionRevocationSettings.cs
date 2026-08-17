using OpenModulePlatform.Web.Shared.Security;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Effective session revocation tuning (R7-F10): whether an unverifiable account
/// state rejects the session, and how long a verified account state may be
/// cached before it is read again.
/// </summary>
/// <remarks>
/// The values live in the omp configuration table under the auth category,
/// alongside the other platform sign-in settings, because the check itself is
/// platform-wide: every OMP web application shares the same auth cookie and
/// runs the same validation hook.
/// </remarks>
public readonly record struct OmpSessionRevocationSettings(bool Strict, int CacheSeconds)
{
    /// <summary>
    /// Default cache window. Sixty seconds bounds how long a revocation (account
    /// disabled, password changed, stamp rotated) can take to reach an active
    /// session, while capping the added database load at one read per user and
    /// application per minute. The window is the documented trade-off between
    /// revocation latency and query load; setting it to 0 checks every request.
    /// </summary>
    public const int DefaultCacheSeconds = 60;

    /// <summary>
    /// Upper clamp for the configured cache window. Anything larger turns the
    /// revocation checkpoint into a formality.
    /// </summary>
    public const int MaxCacheSeconds = 300;

    public static OmpSessionRevocationSettings Default => new(Strict: true, CacheSeconds: DefaultCacheSeconds);

    /// <summary>
    /// Parses the two raw configuration reads. Both reads fail closed (R4-E1):
    /// a value that could not be read at all is not the same as an unset value,
    /// so a failed read falls back to the safe choice -- strict mode and the
    /// default cache window -- never to lenient.
    /// </summary>
    public static OmpSessionRevocationSettings Parse(
        OmpConfigurationRead failureMode,
        OmpConfigurationRead cacheSeconds)
    {
        var strict = failureMode.Failed ||
            !string.Equals(
                failureMode.Value?.Trim(),
                OmpAuthDefaults.SessionRevocationFailureModeLenient,
                StringComparison.OrdinalIgnoreCase);

        var seconds = DefaultCacheSeconds;
        if (!cacheSeconds.Failed &&
            int.TryParse(
                cacheSeconds.Value?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedSeconds))
        {
            seconds = Math.Clamp(parsedSeconds, 0, MaxCacheSeconds);
        }

        return new OmpSessionRevocationSettings(strict, seconds);
    }
}
