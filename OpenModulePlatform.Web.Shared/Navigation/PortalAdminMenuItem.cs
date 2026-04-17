namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Represents a single admin navigation target inside a shared portal navigation section.
/// </summary>
public sealed record PortalAdminMenuItem(string TextKey, string Href, bool SeparatorBefore = false);
