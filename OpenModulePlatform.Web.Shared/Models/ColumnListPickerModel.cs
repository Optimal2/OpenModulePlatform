namespace OpenModulePlatform.Web.Shared.Models;

public sealed class ColumnListPickerModel
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? ListLabel { get; init; }

    public string? DetailsLabel { get; init; }

    public string? EmptyState { get; init; }

    public string? ListPartialName { get; init; }

    public object? ListModel { get; init; }

    public string? DetailsPartialName { get; init; }

    public object? DetailsModel { get; init; }

    public string? FooterPartialName { get; init; }

    public object? FooterModel { get; init; }

    public string? CssClass { get; init; }
}
