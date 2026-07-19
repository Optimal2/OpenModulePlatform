namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Input model for the reusable link box component: a bordered panel of
/// chip-style links that flow in a wrapping row. Intended for the left pane
/// next to the section navigator (inside a .section-navigator-pane wrapper),
/// where it shares the navigator's resizable width, but it also works on its
/// own with the same default width.
/// </summary>
public sealed class LinkBoxModel
{
    public string Label { get; init; } = "Quick links";

    public string? Heading { get; init; }

    public IReadOnlyList<LinkBoxItem> Items { get; init; } = Array.Empty<LinkBoxItem>();
}
