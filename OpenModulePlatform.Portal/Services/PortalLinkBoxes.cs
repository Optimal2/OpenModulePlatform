// File: OpenModulePlatform.Portal/Services/PortalLinkBoxes.cs
namespace OpenModulePlatform.Portal.Services;

// Which link boxes Portal's pages show. Boxes are "shells": a stable key in
// omp.link_boxes paired 1:n with links in omp.link_box_items — titles,
// permissions and links all live in storage (seeded by the portal module's
// initialize SQL, curated at /admin/navigation). Pages declare here, in code,
// which box keys they render and in what order.
public static class PortalLinkBoxes
{
    public const string AdminQuickLinksKey = "portal.admin-quicklinks";

    // The admin pane (rendered by the AdminQuickLinks view component) shows
    // these boxes, in this order.
    public static readonly IReadOnlyList<string> AdminPaneBoxKeys = [AdminQuickLinksKey];
}
