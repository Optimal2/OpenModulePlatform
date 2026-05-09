// File: OpenModulePlatform.Web.ContentWebAppModule/Pages/Index.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.ContentWebAppModule.Pages;

public sealed class IndexModel : ContentWebAppModulePageModel
{
    private readonly ContentPageRepository _repo;
    private readonly ContentRenderer _renderer;

    public IndexModel(
        IOptions<WebAppOptions> options,
        IOptions<ContentWebAppModuleOptions> contentOptions,
        RbacService rbac,
        ContentPageRepository repo,
        ContentRenderer renderer)
        : base(options, contentOptions, rbac)
    {
        _repo = repo;
        _renderer = renderer;
    }

    public ContentPageRenderRow? PageContent { get; private set; }
    public string RenderedHtml { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGet(string? slug, CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        var normalizedSlug = ContentSlugNormalizer.Normalize(slug);
        PageContent = await _repo.GetPublishedPageBySlugAsync(AppInstanceId, normalizedSlug, ct);
        if (PageContent is null && string.IsNullOrEmpty(normalizedSlug))
        {
            var fallbackHomeSlug = ContentSlugNormalizer.Normalize(ContentOptions.HomeSlug);
            if (!string.IsNullOrEmpty(fallbackHomeSlug))
            {
                PageContent = await _repo.GetPublishedPageBySlugAsync(AppInstanceId, fallbackHomeSlug, ct);
            }
        }

        if (PageContent is null)
        {
            await SetContentTitlesAsync("Page not found", ct);
            return NotFound();
        }

        await SetContentTitlesAsync(PageContent.MetaTitle ?? PageContent.Title, ct);
        RenderedHtml = _renderer.RenderToSafeHtml(PageContent.Content, PageContent.ContentFormat);
        return Page();
    }
}
