namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Input model for the reusable section navigator component.
/// </summary>
public sealed class SectionNavigatorModel
{
    public string Label { get; init; } = "Page sections";

    public IReadOnlyList<SectionNavigatorItem> Items { get; init; } = Array.Empty<SectionNavigatorItem>();
}
