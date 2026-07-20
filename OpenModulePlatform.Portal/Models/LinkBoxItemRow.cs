// File: OpenModulePlatform.Portal/Models/LinkBoxItemRow.cs
namespace OpenModulePlatform.Portal.Models;

public sealed class LinkBoxItemRow
{
    public long LinkBoxItemId { get; init; }
    public string BoxKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? GroupKey { get; init; }
    public int SortOrder { get; init; }
}
