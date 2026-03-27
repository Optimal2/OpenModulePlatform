using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.ViewComponents;

public sealed class PortalTopBarViewComponent : ViewComponent
{
    private readonly IOptions<WebAppOptions> _webAppOptions;
    private readonly PortalTopBarService _portalTopBarService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PortalTopBarViewComponent(
        IOptions<WebAppOptions> webAppOptions,
        PortalTopBarService portalTopBarService,
        IHttpContextAccessor httpContextAccessor)
    {
        _webAppOptions = webAppOptions;
        _portalTopBarService = portalTopBarService;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return View(PortalTopBarModel.Hidden);
        }

        var model = await _portalTopBarService.CreateAsync(
            _webAppOptions.Value,
            httpContext.Request,
            httpContext.User,
            httpContext.RequestAborted);

        return View(model);
    }
}
