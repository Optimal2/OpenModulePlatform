namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// View model for the compact portal shortcut bar shown above module navigation.
/// </summary>
public sealed class PortalTopBarModel
{
    public static PortalTopBarModel Hidden { get; } = new()
    {
        IsVisible = false,
        Links = Array.Empty<PortalTopBarLink>(),
        ModuleLinks = Array.Empty<PortalTopBarLink>(),
        PortalAdminLinks = Array.Empty<PortalTopBarLink>(),
        OverflowToggleTextKey = "More",
        CollapsedToggleTextKey = "Modules",
        PortalAdminToggleTextKey = "Admin"
    };

    public bool IsVisible { get; init; }

    /// <summary>
    /// Combined link collection kept for backwards compatibility.
    /// </summary>
    public IReadOnlyList<PortalTopBarLink> Links { get; init; } = Array.Empty<PortalTopBarLink>();

    public PortalTopBarLink? PortalLink { get; init; }

    public IReadOnlyList<PortalTopBarLink> ModuleLinks { get; init; } = Array.Empty<PortalTopBarLink>();

    public bool IsPortalAdmin { get; init; }

    public IReadOnlyList<PortalTopBarLink> PortalAdminLinks { get; init; } = Array.Empty<PortalTopBarLink>();

    public string OverflowToggleTextKey { get; init; } = "More";

    public string CollapsedToggleTextKey { get; init; } = "Modules";

    public string PortalAdminToggleTextKey { get; init; } = "Admin";
}
