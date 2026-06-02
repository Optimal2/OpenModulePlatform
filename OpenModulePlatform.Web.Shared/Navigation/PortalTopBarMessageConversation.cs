namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Compact conversation data shown in the shared portal top bar.
/// </summary>
public sealed record PortalTopBarMessageConversation(
    long ConversationId,
    string DisplayTitle,
    string? LastMessagePreview,
    DateTime? LastMessageAt,
    int UnreadCount,
    string Href);
