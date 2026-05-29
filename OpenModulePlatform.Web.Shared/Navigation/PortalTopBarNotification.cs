namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Compact notification data shown in the shared portal top bar.
/// </summary>
public sealed record PortalTopBarNotification(
    long NotificationId,
    string Title,
    string Content,
    string Level,
    string? DestinationUrl,
    string? CallerKey,
    string? CallerDisplayName,
    string? CallerIcon,
    DateTime CreatedAt,
    bool IsUnread);
