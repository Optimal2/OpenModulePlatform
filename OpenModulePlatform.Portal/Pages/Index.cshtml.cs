// File: OpenModulePlatform.Portal/Pages/Index.cshtml.cs
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.Extensions.Options;
using SharedRbacService = OpenModulePlatform.Web.Shared.Services.RbacService;

namespace OpenModulePlatform.Portal.Pages;

/// <summary>
/// Portal start page showing the app catalog available to the current user.
/// </summary>
public sealed class IndexModel : OmpPageModel<PortalResource>
{
    private readonly AppCatalogService _catalog;
    private readonly SharedRbacService _rbac;
    private readonly OmpAdminRepository _repo;

    public IndexModel(
        IOptions<WebAppOptions> options,
        AppCatalogService catalog,
        SharedRbacService rbac,
        OmpAdminRepository repo)
        : base(options)
    {
        _catalog = catalog;
        _rbac = rbac;
        _repo = repo;
    }

    public IReadOnlyList<PortalAppEntry> Apps { get; private set; } = [];

    public bool IsPortalAdmin { get; private set; }

    public OverviewMetrics Metrics { get; private set; } = new();

    public async Task OnGet(CancellationToken ct)
    {
        SetTitles();

        var permissions = await _rbac.GetUserPermissionsAsync(User, ct);
        IsPortalAdmin = permissions.Contains(OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = IsPortalAdmin;

        if (IsPortalAdmin)
        {
            Metrics = await _repo.GetOverviewAsync(ct);
        }

        var allApps = await _catalog.GetEnabledWebAppsAsync(ct);
        Apps = _catalog.FilterByPermissions(allApps, permissions);
    }
}
