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
    private static readonly int[] SupportedUrlIds = [1, 2, 3];

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

    public int SelectedUrlId { get; private set; } = 1;
    public string? SelectedUrl { get; private set; }
    public string SelectedDisplayName { get; private set; } = string.Empty;
    public string? SelectedError { get; private set; }
    public IReadOnlyList<IFrameUrlButton> UrlButtons { get; private set; } = [];

    public async Task<IActionResult> OnGet(int? urlId, CancellationToken ct)
    {
        _logger.LogInformation("Loading iFrame Web App Module page.");

        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            _logger.LogWarning("Access denied for iFrame Web App Module page.");
            return guard;
        }

        SelectedUrlId = SupportedUrlIds.Contains(urlId ?? 1) ? (urlId ?? 1) : 1;
        SetTitles("Overview");

        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        var configuredRows = await _repo.GetConfiguredUrlsAsync(ct);
        var availableRows = configuredRows.ToDictionary(x => x.Id);

        UrlButtons = SupportedUrlIds
            .Select(id =>
            {
                availableRows.TryGetValue(id, out var row);
                var isAvailable = row is not null && row.Enabled && IsAllowedForRole(row.AllowedRoles, roleContext.ActiveRoleName);

                return new IFrameUrlButton
                {
                    Id = id,
                    Label = row?.DisplayName ?? $"URL {id}",
                    IsSelected = id == SelectedUrlId,
                    IsAvailable = isAvailable
                };
            })
            .ToArray();

        if (!availableRows.TryGetValue(SelectedUrlId, out var selectedRow))
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
