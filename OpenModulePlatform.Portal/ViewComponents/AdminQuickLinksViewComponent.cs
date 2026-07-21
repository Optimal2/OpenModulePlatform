// File: OpenModulePlatform.Portal/ViewComponents/AdminQuickLinksViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.ViewComponents;

public sealed record AdminPaneLink(LinkBoxItemRow Row, string Href);

public sealed class AdminPaneBox
{
    public LinkBoxRow Box { get; init; } = new();
    public IReadOnlyList<AdminPaneLink> Links { get; init; } = Array.Empty<AdminPaneLink>();
}

// Renders the admin pane's link boxes from storage (omp.link_boxes +
// omp.link_box_items), filtered by the viewer's permissions per box AND per
// link, mirroring portal entry visibility. Link targets go through the portal
// entry link resolver, so app entry keys ("app:<id>:home") resolve to app
// links and any link the user cannot access simply does not render. With
// AllowAnonymous (dev mode) everything shows. The gear on each box links to
// the generic editor.
public sealed class AdminQuickLinksViewComponent : ViewComponent
{
    private readonly LinkBoxRepository _linkBoxes;
    private readonly PortalEntryService _entries;
    private readonly RbacService _rbac;
    private readonly IOptions<WebAppOptions> _options;

    public AdminQuickLinksViewComponent(
        LinkBoxRepository linkBoxes,
        PortalEntryService entries,
        RbacService rbac,
        IOptions<WebAppOptions> options)
    {
        _linkBoxes = linkBoxes;
        _entries = entries;
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

        bool HasPermission(string? requiredPermission)
            => allowAll
               || string.IsNullOrWhiteSpace(requiredPermission)
               || permissions.Contains(requiredPermission);

        var resolver = await _entries.CreateLinkResolverAsync(HttpContext.Request, permissions, allowAll, ct);

        var boxes = new List<AdminPaneBox>();
        foreach (var boxKey in PortalLinkBoxes.AdminPaneBoxKeys)
        {
            var box = await _linkBoxes.GetBoxAsync(boxKey, ct);
            if (box is null || !HasPermission(box.RequiredPermission))
            {
                continue;
            }

            var links = new List<AdminPaneLink>();
            foreach (var link in await _linkBoxes.GetItemsAsync(boxKey, ct))
            {
                if (!HasPermission(link.RequiredPermission))
                {
                    continue;
                }

                var (href, isVisible) = resolver.Resolve(link.Url);
                if (!isVisible || string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                links.Add(new AdminPaneLink(link, href));
            }

            boxes.Add(new AdminPaneBox { Box = box, Links = links });
        }

        return View(boxes);
    }
}
