namespace OpenModulePlatform.Web.Shared.Navigation;

public sealed class PortalTopBarNavigationGroup
{
    public string GroupKey { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string EntryKey { get; init; } = string.Empty;

    public Guid? AppInstanceId { get; init; }

    public string Href { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public bool IsFavoritable { get; init; }

    public bool IsFavorite { get; set; }

    public string SearchText { get; init; } = string.Empty;

    public IReadOnlyList<PortalTopBarNavigationEntry> Entries { get; init; } = Array.Empty<PortalTopBarNavigationEntry>();

    public bool HasLink => !string.IsNullOrWhiteSpace(Href);
}
