namespace OpenModulePlatform.Web.iFrameWebAppModule.ViewModels;

public sealed class IFrameUrlSetRow
{
    public int Id { get; init; }
    public string SetKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Enabled { get; init; }
}
