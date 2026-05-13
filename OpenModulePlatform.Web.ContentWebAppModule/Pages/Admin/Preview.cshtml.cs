// File: OpenModulePlatform.Web.ContentWebAppModule/Pages/Admin/Preview.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Pages;
using OpenModulePlatform.Web.ContentWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.ContentWebAppModule.Pages.Admin;

public sealed class PreviewModel : ContentWebAppModulePageModel
{
    private readonly ContentPageRepository _repo;
    private readonly ContentRenderer _renderer;

    public PreviewModel(
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

    public ContentPageEditRow? PageContent { get; private set; }
    public string RenderedHtml { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGet(Guid contentId, CancellationToken ct)
    {
        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        var accessContext = await GetContentAccessContextAsync(ct);

        PageContent = await _repo.GetPageForEditAsync(
            AppInstanceId,
            contentId,
            accessContext.RoleIds,
            accessContext.CanManageAll,
            ct);

        if (PageContent is null)
        {
            return await _repo.ContentExistsAsync(AppInstanceId, contentId, ct)
                ? Forbid()
                : NotFound();
        }

        await SetContentTitlesAsync("Preview", ct);
        RenderedHtml = await _renderer.RenderToHtmlAsync(
            PageContent.Body,
            PageContent.ContentType,
            PageContent.ServerReportKey,
            ct);
        return Page();
    }
}
