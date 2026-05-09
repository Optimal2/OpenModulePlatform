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

    public IndexModel(
        IOptions<WebAppOptions> options,
        IOptions<ContentWebAppModuleOptions> contentOptions,
        RbacService rbac,
        ContentPageRepository repo)
        : base(options, contentOptions, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<ContentPageListRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequireManageAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        await SetContentTitlesAsync("Manage pages", ct);
        Rows = await _repo.ListPagesAsync(AppInstanceId, ct);
        return Page();
    }
}
