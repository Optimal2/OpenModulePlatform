// File: OpenModulePlatform.Portal/Options/ModuleFragmentWidgetOptions.cs
namespace OpenModulePlatform.Portal.Options;

/// <summary>
/// Configuration for the generic <c>module-fragment</c> dashboard widget type.
/// </summary>
/// <remarks>
/// The Portal fetches each fragment server-side from the module's web app with the
/// user's shared OMP cookie. Every value is clamped to a safe range when read, so a
/// missing or odd section never disables the timeout or the size limit.
/// </remarks>
public sealed class ModuleFragmentWidgetOptions
{
    public const string SectionName = "ModuleFragmentWidgets";

    /// <summary>
    /// Seconds one fragment result is reused per user, active role and widget.
    /// Zero disables caching. Clamped to 0-3600.
    /// </summary>
    public int CacheSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum time for one fragment request. Clamped to 250-10000 ms.
    /// </summary>
    public int TimeoutMilliseconds { get; set; } = 3000;

    /// <summary>
    /// Largest fragment response accepted. Clamped to 1 KiB - 1 MiB.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Optional scheme and authority the Portal uses for its own server-side request
    /// instead of the public address, for example when the public host name is not
    /// resolvable from the server. The path always comes from the module's registration.
    /// </summary>
    public string? InternalBaseUrl { get; set; }

    public TimeSpan GetCacheDuration()
        => TimeSpan.FromSeconds(Math.Clamp(CacheSeconds, 0, 3600));

    public TimeSpan GetTimeout()
        => TimeSpan.FromMilliseconds(Math.Clamp(TimeoutMilliseconds, 250, 10000));

    public int GetMaxResponseBytes()
        => Math.Clamp(MaxResponseBytes, 1024, 1024 * 1024);
}
