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
                    new PortalAdminMenuItem("Hosts", hrefFactory("/admin/hosts")),
                    new PortalAdminMenuItem("Deployment log", hrefFactory("/admin/hostdeployments")),
                    new PortalAdminMenuItem("Config settings", hrefFactory("/admin/configsettings"), SeparatorBefore: true),
                    new PortalAdminMenuItem("Security", hrefFactory("/admin/security")),
                    new PortalAdminMenuItem("Users", hrefFactory("/admin/users"))
                ]),
            new(
                "Modules",
                [
                    new PortalAdminMenuItem("Definitions", hrefFactory("/admin/modules")),
                    new PortalAdminMenuItem("Instances", hrefFactory("/admin/moduleinstances"))
                ]),
            new(
                "Apps",
                [
                    new PortalAdminMenuItem("Definitions", hrefFactory("/admin/apps")),
                    new PortalAdminMenuItem("Instances", hrefFactory("/admin/appinstances")),
                    new PortalAdminMenuItem("Artifacts", hrefFactory("/admin/artifacts"), SeparatorBefore: true),
                    new PortalAdminMenuItem("Upload artifact", hrefFactory("/admin/artifactupload")),
                    new PortalAdminMenuItem("Workers", hrefFactory("/admin/workers"))
                ])
        ];
}
