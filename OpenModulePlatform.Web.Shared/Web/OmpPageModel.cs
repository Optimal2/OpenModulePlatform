// File: OpenModulePlatform.Web.Shared/Web/OmpPageModel.cs
using OpenModulePlatform.Web.Shared.Options;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.Web;

public abstract class OmpPageModel : PageModel
{
    private readonly IOptions<WebAppOptions> _options;

    protected OmpPageModel(IOptions<WebAppOptions> options)
    {
        _options = options;
    }

    protected WebAppOptions WebAppOptions => _options.Value;

    public string WebAppTitle => string.IsNullOrWhiteSpace(WebAppOptions.Title)
        ? "OpenModulePlatform"
        : WebAppOptions.Title;

    protected void SetTitles(string? pageTitle = null)
    {
        ViewData["PortalTitle"] = WebAppTitle;
        ViewData["Title"] = string.IsNullOrWhiteSpace(pageTitle)
            ? WebAppTitle
            : $"{WebAppTitle} - {pageTitle}";
    }
}
