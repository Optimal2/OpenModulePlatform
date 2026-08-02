namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Input model for the reusable section navigator component.
/// </summary>
public sealed class SectionNavigatorModel
{
    public string Label { get; init; } = "Page sections";

    public string? RootText { get; init; }

    public string? RootHref { get; init; }

    public bool RootInitiallyExpanded { get; init; } = true;

    public IReadOnlyList<SectionNavigatorItem> Items { get; init; } = Array.Empty<SectionNavigatorItem>();

    /// <summary>
    /// Wraps the navigator in a section-navigator-pane so every page gets the
    /// shared pane behavior (narrow-screen collapse, full-height grab edge).
    /// Pages that compose their own pane around the navigator opt out.
    /// </summary>
    public bool WrapInPane { get; init; } = true;
}
