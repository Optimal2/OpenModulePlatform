namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Represents a single admin navigation target inside a shared portal navigation section.
/// <paramref name="Permission"/> names a permission that opens this one item on its own;
/// Portal administrators see every item regardless.
/// </summary>
public sealed record PortalAdminMenuItem(string TextKey, string Href, bool SeparatorBefore = false, string? Permission = null);
