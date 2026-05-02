using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Models;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.Pages;

[AllowAnonymous]
public abstract class OmpStatusPageModelBase : PageModel
{
    private readonly IOptions<WebAppOptions> _webAppOptions;
    private readonly IStringLocalizer<SharedResource> _localizer;

    protected OmpStatusPageModelBase(
        IOptions<WebAppOptions> webAppOptions,
        IStringLocalizer<SharedResource> localizer)
    {
        _webAppOptions = webAppOptions;
        _localizer = localizer;
    }

    public OmpErrorDisplayModel Error { get; private set; } = new();

    public virtual void OnGet(int statusCode)
    {
        var effectiveStatusCode = statusCode is >= StatusCodes.Status400BadRequest and <= 599
            ? statusCode
            : StatusCodes.Status500InternalServerError;

        Response.StatusCode = effectiveStatusCode;

        var feature = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        var requestedUrl = feature is null
            ? null
            : string.Concat(feature.OriginalPathBase, feature.OriginalPath, feature.OriginalQueryString);
        var portalHref = OmpUrlPathHelper.CombinePortalHref(_webAppOptions.Value.PortalTopBar.PortalBaseUrl, "/");
        var appHomeHref = OmpUrlPathHelper.BuildAppHomeHref(HttpContext.Request.PathBase);

        Error = OmpErrorDisplayModelFactory.CreateForStatusCode(
            effectiveStatusCode,
            requestedUrl,
            portalHref,
            appHomeHref,
            _localizer,
            showBackButton: true);

        ViewData["Title"] = effectiveStatusCode switch
        {
            StatusCodes.Status403Forbidden => _localizer["StatusPageTitle403"].Value,
            StatusCodes.Status404NotFound => _localizer["StatusPageTitle404"].Value,
            _ => _localizer["StatusPageTitleDefault"].Value
        };
    }
}
