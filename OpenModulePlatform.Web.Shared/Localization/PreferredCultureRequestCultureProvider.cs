using OpenModulePlatform.Web.Shared.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace OpenModulePlatform.Web.Shared.Localization;

/// <summary>
/// Resolves the current request culture from the user's global preferred culture cookie.
/// </summary>
public sealed class PreferredCultureRequestCultureProvider : RequestCultureProvider
{
    private readonly WebAppOptions _options;
    private readonly CultureSelectionService _cultureSelectionService;

    public PreferredCultureRequestCultureProvider(WebAppOptions options, CultureSelectionService cultureSelectionService)
    {
        _options = options;
        _cultureSelectionService = cultureSelectionService;
    }

    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var hasPreferredCookie = httpContext.Request.Cookies.ContainsKey(CultureSelectionService.PreferredCultureCookieName);
        var hasRequestCultureCookie = httpContext.Request.Cookies.ContainsKey(CookieRequestCultureProvider.DefaultCookieName);

        if (!hasPreferredCookie && !hasRequestCultureCookie)
        {
            return Task.FromResult<ProviderCultureResult?>(null);
        }

        var selection = _cultureSelectionService.Resolve(_options, httpContext.Request);
        return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(selection.EffectiveCulture, selection.EffectiveCulture));
    }
}
