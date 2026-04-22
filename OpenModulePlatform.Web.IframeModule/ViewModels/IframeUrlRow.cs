namespace OpenModulePlatform.Web.IframeModule.ViewModels;

public sealed class IframeUrlRow
{
    public int Id { get; init; }
    public string Url { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? AllowedRoles { get; init; }
    public int SortOrder { get; init; }
}
