namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Represents a shared portal admin navigation section rendered as a dropdown in the portal navbar
/// and as a nested submenu in the shared top bar admin menu.
/// </summary>
public sealed record PortalAdminMenuSection(string TextKey, IReadOnlyList<PortalAdminMenuItem> Items);
