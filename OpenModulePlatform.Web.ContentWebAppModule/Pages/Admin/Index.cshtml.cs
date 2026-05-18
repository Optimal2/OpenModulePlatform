// File: OpenModulePlatform.Web.ContentWebAppModule/Pages/Admin/Index.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Pages;
using OpenModulePlatform.Web.ContentWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.ContentWebAppModule.Pages.Admin;

public sealed class IndexModel : ContentWebAppModulePageModel
{
    private readonly ContentPageRepository _repo;
    private readonly HtmlContentFileLoader _htmlFileLoader;
    private readonly ServerReportDefinitionLoader _serverReportLoader;

    public IndexModel(
        IOptions<WebAppOptions> options,
        IOptions<ContentWebAppModuleOptions> contentOptions,
        RbacService rbac,
        ContentPageRepository repo,
        HtmlContentFileLoader htmlFileLoader,
        ServerReportDefinitionLoader serverReportLoader)
        : base(options, contentOptions, rbac)
    {
        _repo = repo;
        _htmlFileLoader = htmlFileLoader;
        _serverReportLoader = serverReportLoader;
    }

    public IReadOnlyList<ContentPageListRow> Rows { get; private set; } = [];
    public bool CanCreatePages { get; private set; }
    public string? StatusMessage { get; private set; }
    public IReadOnlyList<string> AvailableHtmlFileKeys { get; private set; } = [];
    public IReadOnlyList<string> AvailableServerReportKeys { get; private set; } = [];

    public async Task<IActionResult> OnGet(string? saved, int? htmlPages, int? reportPages, CancellationToken ct)
    {
        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        var accessContext = await GetContentAccessContextAsync(ct);

        await SetContentTitlesAsync("Manage pages", ct);
        Rows = await _repo.ListEditablePagesAsync(
            AppInstanceId,
            accessContext.RoleIds,
            accessContext.CanManageAll,
            ct);
        AvailableHtmlFileKeys = _htmlFileLoader.ListHtmlFileKeys();
        AvailableServerReportKeys = _serverReportLoader.ListReportKeys();
        CanCreatePages = accessContext.CanManageAll;
        StatusMessage = saved switch
        {
            "deleted" => T("Page deleted."),
            "loaded" => BuildLoadContentStatus(htmlPages.GetValueOrDefault(), reportPages.GetValueOrDefault()),
            _ => null
        };

        if (!accessContext.CanManageAll && Rows.Count == 0)
        {
            return Forbid();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostLoadContentFiles(CancellationToken ct)
    {
        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        var accessContext = await GetContentAccessContextAsync(ct);
        if (!accessContext.CanManageAll)
        {
            return Forbid();
        }

        var result = await _repo.ImportFileBackedPagesAsync(
            AppInstanceId,
            _htmlFileLoader.ListHtmlFileKeys(),
            _serverReportLoader.ListReportKeys(),
            CurrentUserName(),
            ct);

        return RedirectToPage(
            "/Admin/Index",
            new
            {
                saved = "loaded",
                htmlPages = result.HtmlPagesAdded,
                reportPages = result.ServerReportPagesAdded
            });
    }

    private string BuildLoadContentStatus(int htmlPages, int reportPages)
    {
        if (htmlPages == 0 && reportPages == 0)
        {
            return T("No new content pages were created. Discovered files already had pages or matching slugs.");
        }

        return string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("Loaded content files. Added {0} HTML page(s) and {1} server report page(s)."),
            htmlPages,
            reportPages);
    }
}
