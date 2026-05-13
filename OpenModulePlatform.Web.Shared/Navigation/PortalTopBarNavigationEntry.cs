namespace OpenModulePlatform.Web.Shared.Navigation;

public sealed class PortalTopBarNavigationEntry
{
    public string EntryKey { get; init; } = string.Empty;

    public Guid? AppInstanceId { get; init; }

    public string GroupKey { get; init; } = string.Empty;

    public string GroupTitle { get; init; } = string.Empty;

    public string TextKey { get; init; } = string.Empty;

    public string Href { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public bool IsFavoritable { get; init; } = true;

    public bool IsFavorite { get; set; }

    public string SearchText { get; init; } = string.Empty;

    public string FavoriteLabel => string.IsNullOrWhiteSpace(GroupTitle)
        ? TextKey
        : $"{GroupTitle} / {TextKey}";
}
