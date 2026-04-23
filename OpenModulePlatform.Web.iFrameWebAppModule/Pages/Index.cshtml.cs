using OpenModulePlatform.Web.iFrameWebAppModule.Services;
using OpenModulePlatform.Web.iFrameWebAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.iFrameWebAppModule.Pages;

public sealed class IndexModel : iFrameWebAppModulePageModel
{
    private const string DefaultSetKey = "default";

    private readonly IFrameWebAppModuleRepository _repo;
    private readonly RbacService _rbac;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        IFrameWebAppModuleRepository repo,
        ILogger<IndexModel> logger)
        : base(options, rbac)
    {
        _repo = repo;
        _rbac = rbac;
        _logger = logger;
    }

    public string CurrentSetKey { get; private set; } = DefaultSetKey;
    public int SelectedUrlId { get; private set; }
    public string? SelectedUrl { get; private set; }
    public string SelectedDisplayName { get; private set; } = string.Empty;
    public string? SelectedError { get; private set; }
    public IReadOnlyList<IFrameUrlButton> UrlButtons { get; private set; } = [];

    public async Task<IActionResult> OnGet(string? setKey, int? urlId, CancellationToken ct)
    {
        _logger.LogInformation("Loading iFrame Web App Module page.");

        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            _logger.LogWarning("Access denied for iFrame Web App Module page.");
            return guard;
        }

        CurrentSetKey = string.IsNullOrWhiteSpace(setKey) ? DefaultSetKey : setKey.Trim();
        SetTitles("Overview");

        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        var urlSet = await _repo.GetUrlSetAsync(CurrentSetKey, ct);
        if (urlSet is null || !urlSet.Enabled)
        {
            SelectedError = T("The selected URL set is not configured.");
            return Page();
        }

        var configuredRows = await _repo.GetConfiguredUrlsForSetAsync(urlSet.Id, ct);
        if (configuredRows.Count == 0)
        {
            SelectedError = T("The selected URL set has no configured URLs.");
            return Page();
        }

        var firstAvailableRow = configuredRows.FirstOrDefault(row => row.Enabled && IsAllowedForRole(row.AllowedRoles, roleContext.ActiveRoleName));
        SelectedUrlId = urlId.HasValue && configuredRows.Any(row => row.Id == urlId.Value)
            ? urlId.Value
            : (firstAvailableRow?.Id ?? configuredRows[0].Id);

        UrlButtons = configuredRows
            .Select(row => new IFrameUrlButton
            {
                Id = row.Id,
                Label = row.DisplayName,
                IsSelected = row.Id == SelectedUrlId,
                IsAvailable = row.Enabled && IsAllowedForRole(row.AllowedRoles, roleContext.ActiveRoleName)
            })
            .ToArray();

        var selectedRow = configuredRows.FirstOrDefault(row => row.Id == SelectedUrlId);
        if (selectedRow is null)
        {
            SelectedError = T("The selected URL is not configured.");
            return Page();
        }

        if (!selectedRow.Enabled)
        {
            SelectedError = T("The selected URL is disabled.");
            return Page();
        }

        if (!IsAllowedForRole(selectedRow.AllowedRoles, roleContext.ActiveRoleName))
        {
            SelectedError = T("The selected URL is not allowed for the active role.");
            return Page();
        }

        SelectedUrl = selectedRow.Url;
        SelectedDisplayName = selectedRow.DisplayName;
        return Page();
    }

    private static bool IsAllowedForRole(string? allowedRoles, string? activeRoleName)
    {
        if (string.IsNullOrWhiteSpace(allowedRoles))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(activeRoleName))
        {
            return false;
        }

        return allowedRoles
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(role => string.Equals(role, activeRoleName, StringComparison.OrdinalIgnoreCase));
    }
}
