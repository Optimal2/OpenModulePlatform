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
        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        var accessContext = await GetContentAccessContextAsync(ct);

        await SetContentTitlesAsync("Content", ct);
        Rows = await _repo.ListReadablePagesAsync(
            AppInstanceId,
            accessContext.RoleIds,
            accessContext.CanManageAll,
            ct);
        return Page();
    }
}
