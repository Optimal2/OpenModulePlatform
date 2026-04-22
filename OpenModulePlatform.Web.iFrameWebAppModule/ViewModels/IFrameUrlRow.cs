namespace OpenModulePlatform.Web.iFrameWebAppModule.ViewModels;

public sealed class IFrameUrlRow
{
    public int Id { get; init; }
    public string Url { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? AllowedRoles { get; init; }
    public bool Enabled { get; init; }
}
