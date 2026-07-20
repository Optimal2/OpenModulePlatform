// File: OpenModulePlatform.Portal/ViewComponents/LinkBoxDirectoryViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.ViewComponents;

// A meta link box listing every registered link box, each chip leading to the
// generic editor (/admin/navigation) with that box preselected. Its items are
// generated, not stored; only the collapse state uses the box key. Host pages
// are portal-admin gated, so no permission filtering: admins may edit boxes
// they cannot otherwise see.
public sealed class LinkBoxDirectoryViewComponent : ViewComponent
{
    private readonly LinkBoxRepository _linkBoxes;

    public LinkBoxDirectoryViewComponent(LinkBoxRepository linkBoxes)
    {
        _linkBoxes = linkBoxes;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var boxes = await _linkBoxes.GetBoxesAsync(HttpContext.RequestAborted);
        return View((IReadOnlyList<LinkBoxRow>)boxes);
    }
}
