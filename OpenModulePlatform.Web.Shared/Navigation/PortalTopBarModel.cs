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
        OverflowToggleTextKey = "More"
    };

    public bool IsVisible { get; init; }

    public IReadOnlyList<PortalTopBarLink> Links { get; init; } = Array.Empty<PortalTopBarLink>();

    public string OverflowToggleTextKey { get; init; } = "More";
}
