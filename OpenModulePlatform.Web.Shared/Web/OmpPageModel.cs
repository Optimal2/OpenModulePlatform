// File: OpenModulePlatform.Web.Shared/Web/OmpPageModel.cs
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.Web;

/// <summary>
/// Base Razor Page model for OMP web applications.
/// </summary>
public abstract class OmpPageModel<TResource> : PageModel where TResource : class
{
    private readonly IOptions<WebAppOptions> _options;

    protected OmpPageModel(IOptions<WebAppOptions> options)
    {
        _options = options;
    }

    protected WebAppOptions WebAppOptions => _options.Value;

    protected IStringLocalizer<TResource> Localizer =>
        HttpContext.RequestServices.GetRequiredService<IStringLocalizer<TResource>>();

    protected string T(string key) => Localizer[key];

    protected async Task<string> TWithBrandingAsync(string key, CancellationToken ct)
    {
        var branding = await HttpContext.RequestServices
            .GetRequiredService<OmpBrandingService>()
            .GetBrandingAsync(ct);

        return branding.ApplyPlatformName(T(key));
    }

    protected async Task<string> ApplyBrandingAsync(string text, CancellationToken ct)
    {
        var branding = await HttpContext.RequestServices
            .GetRequiredService<OmpBrandingService>()
            .GetBrandingAsync(ct);

        return branding.ApplyPlatformName(text);
    }

    public string WebAppTitle => string.IsNullOrWhiteSpace(WebAppOptions.Title)
        ? "OpenModulePlatform"
        : WebAppOptions.Title;

    /// <summary>
    /// Sets the common layout titles used by all OMP web applications.
    /// </summary>
    protected void SetTitles(string? pageTitle = null)
    {
        var localizedPageTitle = string.IsNullOrWhiteSpace(pageTitle)
            ? null
            : T(pageTitle);

        ViewData["PortalTitle"] = WebAppTitle;
        ViewData["Title"] = string.IsNullOrWhiteSpace(localizedPageTitle)
            ? WebAppTitle
            : $"{WebAppTitle} - {localizedPageTitle}";
    }
}

public abstract class OmpPageModel : OmpPageModel<SharedResource>
{
    protected OmpPageModel(IOptions<WebAppOptions> options)
        : base(options)
    {
    }
}
