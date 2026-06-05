namespace OpenModulePlatform.Portal.Models;

public sealed record PortalEntryIFrameStandaloneHelperOptions(
    IReadOnlyList<PortalEntryIFrameStandaloneAppOption> Apps,
    IReadOnlyList<PortalEntryIFrameStandaloneUrlOption> Urls)
{
    public bool HasAppOptions => Apps.Count > 0;

    public bool HasUrlOptions => Urls.Count > 0;

    public bool HasOptions => HasAppOptions && HasUrlOptions;
}

public sealed record PortalEntryIFrameStandaloneAppOption(
    Guid AppInstanceId,
    string AppInstanceKey,
    string DisplayName,
    string BasePath);

public sealed record PortalEntryIFrameStandaloneUrlOption(
    int UrlId,
    string DisplayName);

public sealed record PortalEntryIFrameStandaloneSelection(
    Guid AppInstanceId,
    int UrlId);
