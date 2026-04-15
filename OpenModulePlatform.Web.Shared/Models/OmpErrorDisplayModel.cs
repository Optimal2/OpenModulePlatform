namespace OpenModulePlatform.Web.Shared.Models;

public sealed class OmpErrorDisplayModel
{
    public int StatusCode { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? RequestedUrlLabel { get; init; }

    public string? RequestedUrl { get; init; }

    public string? PortalHref { get; init; }

    public string? PortalText { get; init; }

    public string? AppHomeHref { get; init; }

    public string? AppHomeText { get; init; }

    public bool ShowBackButton { get; init; }

    public string? BackText { get; init; }
}
