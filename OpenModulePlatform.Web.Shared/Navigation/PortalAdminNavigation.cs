namespace OpenModulePlatform.Web.Shared.Navigation;

/// <summary>
/// Shared definition for the portal admin navigation structure used by both the portal navbar and
/// the shared module top bar admin menu.
/// </summary>
public static class PortalAdminNavigation
{
    public static IReadOnlyList<PortalAdminMenuSection> CreateSections(Func<string, string> hrefFactory)
        =>
        [
            new(
                "System",
                [
                    new PortalAdminMenuItem("Installation", hrefFactory("/admin/instancetemplateedit?id=1")),
                    new PortalAdminMenuItem("Import/export", hrefFactory("/admin/modulepackageimport")),
                    new PortalAdminMenuItem("Artifacts", hrefFactory("/admin/artifacts")),
                    new PortalAdminMenuItem("Maintenance", hrefFactory("/admin/maintenance")),
                    new PortalAdminMenuItem("Operations", hrefFactory("/admin/hostdeployments"))
                ]),
            new(
                "Administration",
                [
                    new PortalAdminMenuItem("Navigation", hrefFactory("/admin/portalentries")),
                    new PortalAdminMenuItem("Dashboard widgets", hrefFactory("/admin/dashboardwidgets")),
                    new PortalAdminMenuItem("Banners", hrefFactory("/admin/banners")),
                    new PortalAdminMenuItem("Config settings", hrefFactory("/admin/configsettings")),
                    new PortalAdminMenuItem("Security", hrefFactory("/admin/security")),
                    new PortalAdminMenuItem("Users", hrefFactory("/admin/users"))
                ])
        ];
}
