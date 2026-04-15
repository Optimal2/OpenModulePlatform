using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;

namespace OpenModulePlatform.Web.Shared.Services;

public static class OmpErrorDisplayModelFactory
{
    public static OmpErrorDisplayModel CreateForStatusCode(
        int statusCode,
        string? requestedUrl,
        string portalHref,
        string? appHomeHref,
        IStringLocalizer<SharedResource> localizer,
        string? messageOverride = null,
        bool showBackButton = true)
    {
        var title = statusCode switch
        {
            StatusCodes.Status403Forbidden => localizer["StatusPageHeading403"].Value,
            StatusCodes.Status404NotFound => localizer["StatusPageHeading404"].Value,
            _ => localizer["StatusPageHeadingDefault"].Value
        };

        var message = messageOverride ?? (statusCode switch
        {
            StatusCodes.Status403Forbidden => localizer["StatusPageMessage403"].Value,
            StatusCodes.Status404NotFound => localizer["StatusPageMessage404"].Value,
            _ => localizer["StatusPageMessageDefault"].Value
        });

        var normalizedPortalHref = string.IsNullOrWhiteSpace(portalHref) ? "/" : portalHref;
        var normalizedAppHomeHref = string.IsNullOrWhiteSpace(appHomeHref) ? null : appHomeHref;

        return new OmpErrorDisplayModel
        {
            StatusCode = statusCode,
            Title = title,
            Message = message,
            RequestedUrlLabel = string.IsNullOrWhiteSpace(requestedUrl)
                ? null
                : localizer["StatusPageOriginalPath"].Value,
            RequestedUrl = string.IsNullOrWhiteSpace(requestedUrl) ? null : requestedUrl,
            PortalHref = normalizedPortalHref,
            PortalText = localizer["StatusPageBackToPortal"].Value,
            AppHomeHref = normalizedAppHomeHref,
            AppHomeText = normalizedAppHomeHref is null ? null : localizer["StatusPageBackToApp"].Value,
            ShowBackButton = showBackButton,
            BackText = showBackButton ? localizer["StatusPageBack"].Value : null
        };
    }

    public static OmpErrorDisplayModel CreateForbidden(
        string? requestedUrl,
        string portalHref,
        string? appHomeHref,
        IStringLocalizer<SharedResource> localizer,
        string? messageOverride = null,
        bool showBackButton = true)
        => CreateForStatusCode(
            StatusCodes.Status403Forbidden,
            requestedUrl,
            portalHref,
            appHomeHref,
            localizer,
            messageOverride,
            showBackButton);

    public static OmpErrorDisplayModel CreateNotFound(
        string? requestedUrl,
        string portalHref,
        string? appHomeHref,
        IStringLocalizer<SharedResource> localizer,
        string? messageOverride = null,
        bool showBackButton = true)
        => CreateForStatusCode(
            StatusCodes.Status404NotFound,
            requestedUrl,
            portalHref,
            appHomeHref,
            localizer,
            messageOverride,
            showBackButton);
}
