// File: OpenModulePlatform.Web.ContentWebAppModule/Pages/Page.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.ContentWebAppModule.Pages;

public sealed class PageModel : ContentWebAppModulePageModel
{
    private readonly ContentPageRepository _repo;
    private readonly ContentRenderer _renderer;

    public PageModel(
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
        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        var normalizedSlug = ContentSlugNormalizer.Normalize(slug);
        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            return RedirectToPage("/Index");
        }

        var accessContext = await GetContentAccessContextAsync(ct);

        PageContent = await _repo.GetReadablePageBySlugAsync(
            AppInstanceId,
            normalizedSlug,
            accessContext.RoleIds,
            accessContext.CanManageAll,
            ct);

        if (PageContent is null)
        {
            await SetContentTitlesAsync("Page not found", ct);
            return NotFound();
        }

        await SetContentTitlesAsync(PageContent.Title, ct);
        RenderedHtml = _renderer.RenderToHtml(PageContent.Body, PageContent.ContentType);
        return Page();
    }
}
