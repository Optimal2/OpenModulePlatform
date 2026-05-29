namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Compact system banner data shown below the shared portal top bar.
/// </summary>
public sealed record PortalTopBarBanner(
    long BannerId,
    string Title,
    string Content,
    int Level,
    string LevelName,
    string IconUrl,
    DateTime? StartsAtUtc,
    DateTime? ExpiresAtUtc);
