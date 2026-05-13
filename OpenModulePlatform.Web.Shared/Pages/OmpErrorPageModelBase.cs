using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Models;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.Shared.Pages;

[AllowAnonymous]
public abstract class OmpErrorPageModelBase : PageModel
{
    private readonly IOptions<WebAppOptions> _webAppOptions;
    private readonly IStringLocalizer<SharedResource> _localizer;

    protected OmpErrorPageModelBase(
        IOptions<WebAppOptions> webAppOptions,
        IStringLocalizer<SharedResource> localizer)
    {
        _webAppOptions = webAppOptions;
        _localizer = localizer;
    }

    public OmpErrorDisplayModel Error { get; private set; } = new();

    public virtual void OnGet()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;

        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        var portalHref = OmpUrlPathHelper.CombinePortalHref(_webAppOptions.Value.PortalTopBar.PortalBaseUrl, "/");
        var appHomeHref = OmpUrlPathHelper.BuildAppHomeHref(HttpContext.Request.PathBase);

        Error = OmpErrorDisplayModelFactory.CreateForStatusCode(
            StatusCodes.Status500InternalServerError,
            feature?.Path,
            portalHref,
            appHomeHref,
            _localizer,
            showBackButton: true);

        ViewData["Title"] = _localizer["StatusPageTitleDefault"].Value;
    }
}
