// File: OpenModulePlatform.Portal/Pages/Index.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.Extensions.Options;
using SharedRbacService = OpenModulePlatform.Web.Shared.Services.RbacService;

namespace OpenModulePlatform.Portal.Pages;

public sealed class IndexModel : OmpPageModel
{
    private readonly AppCatalogService _catalog;
    private readonly SharedRbacService _rbac;

    public IndexModel(
        IOptions<WebAppOptions> options,
        AppCatalogService catalog,
        SharedRbacService rbac)
        : base(options)
    {
        _catalog = catalog;
        _rbac = rbac;
    }

    public IReadOnlyList<PortalAppEntry> Apps { get; private set; } = [];
    public bool IsPortalAdmin { get; private set; }

    public async Task OnGet(CancellationToken ct)
    {
        SetTitles();

        var permissions = await _rbac.GetUserPermissionsAsync(User, ct);
        ViewData["IsPortalAdmin"] = permissions.Contains(OmpPortalPermissions.Admin);
        IsPortalAdmin = permissions.Contains(OmpPortalPermissions.Admin);

        var allApps = await _catalog.GetEnabledWebAppsAsync(ct);
        Apps = _catalog.FilterByPermissions(allApps, permissions);
    }
}
