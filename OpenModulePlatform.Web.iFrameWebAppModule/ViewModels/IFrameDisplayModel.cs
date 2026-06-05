namespace OpenModulePlatform.Web.iFrameWebAppModule.ViewModels;

public sealed record IFrameDisplayModel(
    string? SelectedUrl,
    string SelectedDisplayName,
    string? SelectedError);
