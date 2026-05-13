namespace OpenModulePlatform.Web.Shared.Navigation;

public sealed class PortalTopBarNavigationGroup
{
    public string GroupKey { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<PortalTopBarNavigationEntry> Entries { get; init; } = Array.Empty<PortalTopBarNavigationEntry>();
}
