// File: OpenModulePlatform.Portal/ViewComponents/AdminQuickLinksViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.ViewComponents;

public sealed class AdminPaneBox
{
    public LinkBoxRow Box { get; init; } = new();
    public IReadOnlyList<LinkBoxItemRow> Links { get; init; } = Array.Empty<LinkBoxItemRow>();
}

// Renders the admin pane's link boxes from storage (omp.link_boxes +
// omp.link_box_items), filtered by the viewer's permissions per box AND per
// link, mirroring portal entry visibility. With AllowAnonymous (dev mode)
// everything shows. The gear on each box links to the generic editor.
public sealed class AdminQuickLinksViewComponent : ViewComponent
{
    private readonly LinkBoxRepository _linkBoxes;
    private readonly RbacService _rbac;
    private readonly IOptions<WebAppOptions> _options;

    public AdminQuickLinksViewComponent(
        LinkBoxRepository linkBoxes,
        RbacService rbac,
        IOptions<WebAppOptions> options)
    {
        _linkBoxes = linkBoxes;
        _rbac = rbac;
        _options = options;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var allowAll = _options.Value.AllowAnonymous;
        var permissions = allowAll
            ? new HashSet<string>()
            : await _rbac.GetUserPermissionsAsync(HttpContext.User, ct);

        bool IsVisible(string? requiredPermission)
            => allowAll
               || string.IsNullOrWhiteSpace(requiredPermission)
               || permissions.Contains(requiredPermission);

        var boxes = new List<AdminPaneBox>();
        foreach (var boxKey in PortalLinkBoxes.AdminPaneBoxKeys)
        {
            var box = await _linkBoxes.GetBoxAsync(boxKey, ct);
            if (box is null || !IsVisible(box.RequiredPermission))
            {
                continue;
            }

            var links = (await _linkBoxes.GetItemsAsync(boxKey, ct))
                .Where(link => IsVisible(link.RequiredPermission))
                .ToArray();

            boxes.Add(new AdminPaneBox { Box = box, Links = links });
        }

        return View(boxes);
    }
}
