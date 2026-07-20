// File: OpenModulePlatform.Portal/ViewComponents/AdminQuickLinksViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.ViewComponents;

public sealed class AdminQuickLinksViewModel
{
    public PortalLinkBoxDefinition Definition { get; init; } = PortalLinkBoxes.AdminQuickLinks;
    public IReadOnlyList<LinkBoxItemRow> UserLinks { get; init; } = Array.Empty<LinkBoxItemRow>();
}

// Renders the admin quick links box: the registry's code links merged with
// user-curated links from omp.link_box_items. Rendering never depends on the
// editor; the gear just links to /admin/navigation. The component does no
// permission check of its own — its host pages are portal-admin gated.
public sealed class AdminQuickLinksViewComponent : ViewComponent
{
    private readonly LinkBoxRepository _linkBoxes;

    public AdminQuickLinksViewComponent(LinkBoxRepository linkBoxes)
    {
        _linkBoxes = linkBoxes;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userLinks = await _linkBoxes.GetItemsAsync(
            PortalLinkBoxes.AdminQuickLinksKey,
            HttpContext.RequestAborted);

        return View(new AdminQuickLinksViewModel { UserLinks = userLinks });
    }
}
