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
                "Modules",
                [
                    new PortalAdminMenuItem("Instances", hrefFactory("/admin/moduleinstances")),
                    new PortalAdminMenuItem("Definitions", hrefFactory("/admin/modules"))
                ]),
            new(
                "Apps",
                [
                    new PortalAdminMenuItem("Definitions", hrefFactory("/admin/apps")),
                    new PortalAdminMenuItem("Instances", hrefFactory("/admin/appinstances")),
                    new PortalAdminMenuItem("Artifacts", hrefFactory("/admin/artifacts"), SeparatorBefore: true),
                    new PortalAdminMenuItem("Workers", hrefFactory("/admin/workers"))
                ]),
            new(
                "Hosts",
                [
                    new PortalAdminMenuItem("Definitions", hrefFactory("/admin/hosttemplates")),
                    new PortalAdminMenuItem("Instances", hrefFactory("/admin/hosts"))
                ]),
            new(
                "System",
                [
                    new PortalAdminMenuItem("Definitions", hrefFactory("/admin/instancetemplates")),
                    new PortalAdminMenuItem("Instances", hrefFactory("/admin/instances")),
                    new PortalAdminMenuItem("Security", hrefFactory("/admin/security"), SeparatorBefore: true),
                    new PortalAdminMenuItem("Automation", hrefFactory("/admin/automation"))
                ])
        ];
}
