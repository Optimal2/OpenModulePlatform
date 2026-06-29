namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Describes one explicit page section link for the reusable section navigator.
/// Pages decide which rendered sections to expose.
/// </summary>
public sealed class SectionNavigatorItem
{
    public SectionNavigatorItem(string text, string? anchorId = null)
    {
        Text = text;
        AnchorId = anchorId;
    }

    public string Text { get; }

    public string? AnchorId { get; }

    public string? Href { get; init; }

    public IReadOnlyList<SectionNavigatorItem> Children { get; init; } = Array.Empty<SectionNavigatorItem>();

    public bool InitiallyExpanded { get; init; } = true;

    public bool HasLink => !string.IsNullOrWhiteSpace(Href) || !string.IsNullOrWhiteSpace(AnchorId);

    public string? LinkHref
        => string.IsNullOrWhiteSpace(Href)
            ? string.IsNullOrWhiteSpace(AnchorId) ? null : $"#{AnchorId}"
            : Href;
}
