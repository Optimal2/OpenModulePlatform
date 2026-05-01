// File: OpenModulePlatform.Web.ExampleWebAppModule/Pages/Index.cshtml.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Services;
using OpenModulePlatform.Web.ExampleWebAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleWebAppModule.Pages;

public sealed class IndexModel : ExampleWebAppModulePageModel
{
    private readonly ExampleWebAppModuleAdminRepository _repo;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        ExampleWebAppModuleAdminRepository repo,
        ILogger<IndexModel> logger)
        : base(options, rbac)
    {
        _repo = repo;
        _logger = logger;
    }

    public OverviewRow Overview { get; private set; } = new();

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        _logger.LogInformation("Loading ExampleWebAppModule overview page.");

        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            _logger.LogWarning("Access denied for ExampleWebAppModule overview page.");
            return guard;
        }

        SetTitles("Overview");
        Overview = await _repo.GetOverviewAsync(ct);

        _logger.LogInformation("ExampleWebAppModule overview loaded successfully.");
        return Page();
    }
}
