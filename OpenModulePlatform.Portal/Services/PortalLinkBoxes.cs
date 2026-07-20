// File: OpenModulePlatform.Portal/Services/PortalLinkBoxes.cs
namespace OpenModulePlatform.Portal.Services;

// A developer-curated (code) link in a link box. TextKey is a localization
// key; RelativePath is app-relative ("/admin/..."); Group maps to the shared
// LinkBox tint groups.
public sealed record PortalCodeLink(string TextKey, string RelativePath, string? Group = null);

// A link box known to Portal: its stable storage key, a localizable title for
// the editor, and the built-in links that always render and cannot be removed
// through the editor. User-added links live in omp.link_box_items per BoxKey.
public sealed record PortalLinkBoxDefinition(string BoxKey, string TitleKey, IReadOnlyList<PortalCodeLink> CodeLinks);

// Registry of Portal-owned link boxes. The generic editor at /admin/navigation
// lists these (plus any box keys found in storage) and every box renders the
// union of code links and stored links.
public static class PortalLinkBoxes
{
    public const string AdminQuickLinksKey = "portal.admin-quicklinks";

    // Quick links to admin pages that are not in the main menu yet. Edit this
    // ONE list to add/remove a built-in link. Routes are the default Razor
    // Pages routes for Pages/Admin/<Name>.cshtml -> /admin/<name>.
    public static readonly PortalLinkBoxDefinition AdminQuickLinks = new(
        AdminQuickLinksKey,
        "Admin quick links",
        [
            new PortalCodeLink("Overview", "/admin/overview"),
            new PortalCodeLink("Installation", "/admin/instancetemplateedit?id=1"),
            new PortalCodeLink("Navigation", "/admin/navigation"),
            new PortalCodeLink("Instances", "/admin/instances", "topology"),
            new PortalCodeLink("App instances", "/admin/appinstances", "topology"),
            new PortalCodeLink("Module instances", "/admin/moduleinstances", "topology"),
            new PortalCodeLink("Hosts", "/admin/hosts", "topology"),
            new PortalCodeLink("Host resources", "/admin/hostresources", "topology"),
            new PortalCodeLink("Host deployments", "/admin/hostdeployments", "topology"),
            new PortalCodeLink("Modules", "/admin/modules", "modules"),
            new PortalCodeLink("Module definitions", "/admin/moduledefinitions", "modules"),
            new PortalCodeLink("Apps", "/admin/apps", "modules"),
            new PortalCodeLink("Workers", "/admin/workers", "operations"),
            new PortalCodeLink("Maintenance", "/admin/maintenance", "operations")
        ]);

    public static readonly IReadOnlyList<PortalLinkBoxDefinition> All = [AdminQuickLinks];

    public static PortalLinkBoxDefinition? Find(string boxKey)
        => All.FirstOrDefault(box => string.Equals(box.BoxKey, boxKey, StringComparison.OrdinalIgnoreCase));
}
