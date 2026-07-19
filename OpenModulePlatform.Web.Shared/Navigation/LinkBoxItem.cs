namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// One chip-style link in a <see cref="LinkBoxModel"/>. Items sharing the same
/// group key get the same muted tint; the active item renders emphasized.
/// </summary>
public sealed class LinkBoxItem
{
    public LinkBoxItem(string text, string url, string? group = null, bool isActive = false)
    {
        Text = text;
        Url = url;
        Group = group;
        IsActive = isActive;
    }

    public string Text { get; }

    public string Url { get; }

    public string? Group { get; }

    public bool IsActive { get; }
}
