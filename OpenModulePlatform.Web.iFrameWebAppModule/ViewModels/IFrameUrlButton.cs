namespace OpenModulePlatform.Web.iFrameWebAppModule.ViewModels;

public sealed class IFrameUrlButton
{
    public int Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
    public bool IsAvailable { get; init; }
}
