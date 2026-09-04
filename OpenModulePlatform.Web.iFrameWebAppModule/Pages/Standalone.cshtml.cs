using OpenModulePlatform.Web.iFrameWebAppModule.Services;
using OpenModulePlatform.Web.iFrameWebAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.iFrameWebAppModule.Pages;

public sealed class StandaloneModel : iFrameWebAppModulePageModel
{
    private readonly IFrameWebAppModuleRepository _repo;
    private readonly RbacService _rbac;
    private readonly ILogger<StandaloneModel> _logger;

    public StandaloneModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        IFrameWebAppModuleRepository repo,
        ILogger<StandaloneModel> logger)
        : base(options, rbac)
    {
        _repo = repo;
        _rbac = rbac;
        _logger = logger;
    }

    public string? SelectedUrl { get; private set; }

    public string SelectedDisplayName { get; private set; } = string.Empty;

    public string? SelectedError { get; private set; }

    public IFrameDisplayModel Display => new(SelectedUrl, SelectedDisplayName, SelectedError);

    public async Task<IActionResult> OnGet(int urlId, CancellationToken ct)
    {
        _logger.LogInformation("Loading standalone iFrame URL {UrlId}.", urlId);

        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            _logger.LogWarning("Access denied for standalone iFrame URL {UrlId}.", urlId);
            return guard;
        }

        ViewData["HideModuleTopbar"] = true;
        SetTitles("Standalone view");

        var selectedRow = await _repo.GetStandaloneUrlAsync(urlId, ct);
        if (selectedRow is null)
        {
            SelectedError = T("The selected URL is not configured.");
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Page();
        }

        if (!selectedRow.Enabled)
        {
            SelectedError = T("The selected URL is disabled.");
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Page();
        }

        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        if (!IndexModel.IsAllowedForRole(selectedRow.AllowedRoles, roleContext.ActiveRoleName))
        {
            SelectedError = T("The selected URL is not allowed for the active role.");
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Page();
        }

        // Same read-path guard as Index (R8-P1-4): rows predating the write-side
        // scheme check, or written by any other route into the table, must never
        // reach <iframe src> unchecked.
        var safeUrl = OmpUrlSafety.SanitizeHref(selectedRow.Url);
        if (safeUrl is null)
        {
            SelectedError = T("The selected URL is not a valid web address and was not opened.");
            return Page();
        }

        SelectedUrl = safeUrl;
        SelectedDisplayName = selectedRow.DisplayName;
        return Page();
    }
}
